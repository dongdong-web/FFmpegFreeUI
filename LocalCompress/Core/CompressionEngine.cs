using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using LocalCompress.Upstream;

namespace LocalCompress.Core;

public enum CompressionQuality { Quality, Balanced, Small }
public sealed record ProcessingOptions(int MaxShortEdge = 0, bool ReduceNoise = false, bool NormalizeAudio = false, double? TargetMegabytes = null);
public sealed record MediaInfo(double Duration, int Width, int Height, bool HasAudio, bool IsHdr);
public sealed record CompressionProgress(double Fraction, string Speed, string Stage = "压缩中");
public sealed record CompressionResult(string OutputPath, long OriginalBytes, long OutputBytes, bool AlreadyWithinTarget = false);
public sealed record ToolPaths(string Ffmpeg, string Ffprobe)
{
    public static ToolPaths? Find(string appDirectory)
    {
        // The portable package always uses its own engines before considering PATH.
        var folders = new[] { Path.Combine(appDirectory, "tools"), appDirectory }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            try
            {
                var encoder = Path.GetFullPath(Path.Combine(folder.Trim('"'), "ffmpeg.exe"));
                var probe = Path.GetFullPath(Path.Combine(folder.Trim('"'), "ffprobe.exe"));
                if (File.Exists(encoder) && File.Exists(probe)) return new(encoder, probe);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { }
        }
        return null;
    }
}

public sealed class CompressionEngine(ToolPaths tools)
{
    public static string LocalFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.StartsWith("//", StringComparison.Ordinal))
            throw new InvalidOperationException("请选择本机磁盘上的视频，不支持网址或网络共享。");
        var full = LocalStoragePath(path);
        if (!File.Exists(full)) throw new FileNotFoundException("找不到这个视频，请重新选择。", full);
        return full;
    }

    private static string LocalStoragePath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        if (new DriveInfo(root).DriveType == DriveType.Network)
            throw new InvalidOperationException("请选择本机磁盘，不支持网络磁盘。");
        var current = root;
        // Reject junctions, symlinks and cloud placeholders before following them.
        foreach (var segment in full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("请选择普通本机文件夹，不支持符号链接、目录联接或云占位文件。");
            }
            catch (FileNotFoundException) { break; }
            catch (DirectoryNotFoundException) { break; }
        }
        return full;
    }

    public async Task<MediaInfo> ProbeAsync(string input, CancellationToken cancellationToken)
    {
        input = LocalFile(input);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var result = await RunAsync(tools.Ffprobe,
            ["-v", "error", "-protocol_whitelist", "file,pipe", "-show_entries",
             "format=duration:stream=codec_type,width,height,color_transfer", "-of", "json", input],
            null, timeout.Token);
        if (result.ExitCode != 0) throw new InvalidOperationException("无法读取视频。文件可能已损坏，或格式不受支持。\n" + result.Error);
        using var json = JsonDocument.Parse(result.Output);
        if (!json.RootElement.TryGetProperty("streams", out var streams))
            throw new InvalidOperationException("没有找到可压缩的视频画面。");
        var videos = streams.EnumerateArray().Where(x => x.TryGetProperty("codec_type", out var type) && type.GetString() == "video").ToArray();
        if (videos.Length == 0) throw new InvalidOperationException("这个文件没有视频画面。");
        var video = videos[0];
        double duration = 0;
        if (json.RootElement.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var durationJson))
            double.TryParse(durationJson.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
        if (!double.IsFinite(duration) || duration <= 0)
            throw new InvalidOperationException("无法确定视频时长，暂时不能压缩这个文件。");
        var transfer = video.TryGetProperty("color_transfer", out var color) ? color.GetString() : "";
        return new(duration, video.GetProperty("width").GetInt32(), video.GetProperty("height").GetInt32(),
            streams.EnumerateArray().Any(x => x.TryGetProperty("codec_type", out var type) && type.GetString() == "audio"),
            transfer is "smpte2084" or "arib-std-b67");
    }

    public static IReadOnlyList<string> BuildArguments(string input, string temporaryOutput, CompressionQuality quality,
        ProcessingOptions? options = null, bool hasAudio = true)
    {
        options ??= new();
        return PresetCompiler.BuildArguments(input, temporaryOutput, (int)quality,
            options.MaxShortEdge, options.ReduceNoise, options.NormalizeAudio, hasAudio);
    }

    public async Task<CompressionResult> CompressAsync(string input, string outputDirectory,
        CompressionQuality quality, IProgress<CompressionProgress>? progress, CancellationToken cancellationToken,
        ProcessingOptions? options = null)
    {
        input = LocalFile(input);
        options ??= new();
        long? targetBytes = null;
        if (options.TargetMegabytes is double target)
        {
            if (!double.IsFinite(target) || target < 0.1 || target > 100000)
                throw new ArgumentOutOfRangeException(nameof(options), "目标大小须在 0.1 到 100000 MB 之间。");
            // Decimal MB matches upload limits; binary MiB would exceed them.
            targetBytes = checked((long)Math.Floor(target * 1_000_000));
        }
        var info = await ProbeAsync(input, cancellationToken);
        if (info.IsHdr) throw new InvalidOperationException("这个视频是 HDR。首版暂不支持 HDR 压缩，以免出现颜色失真；原文件未改动。");
        if (!Path.IsPathFullyQualified(outputDirectory) || outputDirectory.StartsWith(@"\\", StringComparison.Ordinal) || outputDirectory.StartsWith("//", StringComparison.Ordinal))
            throw new InvalidOperationException("请选择本机磁盘上的保存文件夹。");
        outputDirectory = LocalStoragePath(outputDirectory);
        var originalBytes = new FileInfo(input).Length;
        if (targetBytes is long limit && originalBytes <= limit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new(1, "", "已符合目标"));
            return new(input, originalBytes, originalBytes, true);
        }
        Directory.CreateDirectory(outputDirectory);
        var temp = Path.Combine(outputDirectory, ".localcompress-" + Guid.NewGuid().ToString("N") + ".mp4");
        var passLog = temp + ".pass";
        try
        {
            double fraction = 0;
            string speed = "";
            var lastReport = Stopwatch.StartNew();
            var videoBitrate = targetBytes is long budget ? CalculateTargetBitrate(budget, info.Duration, info.HasAudio) : 0;
            var attempts = targetBytes.HasValue ? 2 : 1;
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                var plan = PresetCompiler.BuildPlan(input, temp, (int)quality, options.MaxShortEdge,
                    options.ReduceNoise, options.NormalizeAudio, info.HasAudio, videoBitrate, passLog);
                for (var stageIndex = 0; stageIndex < plan.Count; stageIndex++)
                {
                    var stageName = targetBytes.HasValue ? (attempt == 0 ? "" : "大小校正 · ") + (stageIndex == 0 ? "分析画面" : "生成视频") : "压缩中";
                    progress?.Report(new(fraction, speed, stageName));
                    var result = await RunAsync(tools.Ffmpeg, plan[stageIndex], line =>
                    {
                        var split = line.IndexOf('=');
                        if (split < 0) return;
                        var value = line[(split + 1)..];
                        switch (line[..split])
                        {
                            case "out_time_us":
                                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var microseconds))
                                {
                                    var stageFraction = Math.Clamp(microseconds / 1_000_000 / info.Duration, 0, 1);
                                    // Reserve space for a possible correction, without moving backwards.
                                    var candidate = targetBytes.HasValue ? (attempt == 0 ? (stageIndex + stageFraction) / plan.Count * .9 : .9 + (stageIndex + stageFraction) / plan.Count * .09) : stageFraction * .99;
                                    fraction = Math.Max(fraction, Math.Min(candidate, .99));
                                }
                                break;
                            case "speed": speed = value; break;
                            case "progress" when lastReport.ElapsedMilliseconds >= 250:
                                progress?.Report(new(fraction, speed, stageName));
                                lastReport.Restart();
                                break;
                        }
                    }, cancellationToken);
                    if (result.ExitCode != 0) throw new InvalidOperationException("压缩没有完成，原文件未改动。\n" + result.Error);
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (targetBytes is not long maxBytes || new FileInfo(temp).Length <= maxBytes) break;
                if (attempt == attempts - 1)
                    throw new InvalidOperationException("结果仍超过目标大小，已清理临时结果。请增加目标大小，或选择更低分辨率；原文件未改动。");
                var actual = new FileInfo(temp).Length;
                videoBitrate = Math.Min(videoBitrate - 1, checked((int)Math.Floor(videoBitrate * (double)maxBytes / actual * .92)));
                if (videoBitrate < 16000) throw new InvalidOperationException("目标太小，无法保留可用的视频画面。请增加目标大小；原文件未改动。");
                File.Delete(temp);
                CleanPassLogs(passLog);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var output = await ProbeAsync(temp, cancellationToken);
            if (Math.Abs(output.Duration - info.Duration) > Math.Max(1, info.Duration * .02) || (info.HasAudio && !output.HasAudio))
                throw new InvalidOperationException("压缩结果未通过完整性检查，原文件未改动。");
            var outputBytes = new FileInfo(temp).Length;
            if (outputBytes >= originalBytes)
                throw new InvalidOperationException("这个视频已经很紧凑，本次结果没有变小。已丢弃压缩结果，原文件未改动；可以尝试“体积优先”。");
            cancellationToken.ThrowIfCancellationRequested();
            var stem = Path.GetFileNameWithoutExtension(input) + "_压缩";
            for (var index = 0; ; index++)
            {
                var final = Path.Combine(outputDirectory, stem + (index == 0 ? "" : $" ({index})") + ".mp4");
                try { File.Move(temp, final, false); progress?.Report(new(1, speed)); return new(final, originalBytes, outputBytes); }
                catch (IOException) when (File.Exists(final)) { }
            }
        }
        finally { if (File.Exists(temp)) File.Delete(temp); CleanPassLogs(passLog); }
    }

    public static int CalculateTargetBitrate(long targetBytes, double duration, bool hasAudio)
    {
        if (targetBytes <= 0 || !double.IsFinite(duration) || duration <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetBytes));
        // Reserve container overhead and encoder variance; keep the existing 128 kb/s audio.
        var video = (targetBytes * .96 - 16000) * 8 / duration - (hasAudio ? 128000 : 0);
        if (video < 16000)
            throw new InvalidOperationException("目标太小，无法保留音轨和可用的视频画面。请增加目标大小，或缩短视频；原文件未改动。");
        return (int)Math.Min(Math.Floor(video), 1_000_000_000);
    }

    private static void CleanPassLogs(string prefix)
    {
        // Only remove files belonging to this job's unique prefix.
        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(prefix)!, Path.GetFileName(prefix) + "*"))
            File.Delete(file);
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
    private static async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> args,
        Action<string>? outputLine, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException("无法启动本地视频处理引擎。");
        using var registration = token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        var stdout = ReadBoundedAsync(process.StandardOutput, outputLine);
        var stderr = ReadBoundedAsync(process.StandardError, null);
        try
        {
            await process.WaitForExitAsync(token);
            await Task.WhenAll(stdout, stderr);
            token.ThrowIfCancellationRequested();
            return new(process.ExitCode, await stdout, await stderr);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, Action<string>? onLine)
    {
        var buffer = new StringBuilder();
        while (await reader.ReadLineAsync() is { } line)
        {
            onLine?.Invoke(line);
            buffer.AppendLine(line);
            if (buffer.Length > 16384) buffer.Remove(0, buffer.Length - 16384);
        }
        return buffer.ToString().Trim();
    }
}
