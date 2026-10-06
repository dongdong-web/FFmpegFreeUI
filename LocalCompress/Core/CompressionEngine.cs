using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using LocalCompress.Upstream;

namespace LocalCompress.Core;

public enum CompressionQuality { Quality, Balanced, Small }
public sealed record ProcessingOptions(int MaxShortEdge = 0, bool ReduceNoise = false, bool NormalizeAudio = false);
public sealed record MediaInfo(double Duration, int Width, int Height, bool HasAudio, bool IsHdr);
public sealed record CompressionProgress(double Fraction, string Speed);
public sealed record CompressionResult(string OutputPath, long OriginalBytes, long OutputBytes);
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
        var info = await ProbeAsync(input, cancellationToken);
        if (info.IsHdr) throw new InvalidOperationException("这个视频是 HDR。首版暂不支持 HDR 压缩，以免出现颜色失真；原文件未改动。");
        if (!Path.IsPathFullyQualified(outputDirectory) || outputDirectory.StartsWith(@"\\", StringComparison.Ordinal) || outputDirectory.StartsWith("//", StringComparison.Ordinal))
            throw new InvalidOperationException("请选择本机磁盘上的保存文件夹。");
        outputDirectory = LocalStoragePath(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var temp = Path.Combine(outputDirectory, ".localcompress-" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            double fraction = 0;
            string speed = "";
            var lastReport = Stopwatch.StartNew();
            var result = await RunAsync(tools.Ffmpeg, BuildArguments(input, temp, quality, options, info.HasAudio), line =>
            {
                var split = line.IndexOf('=');
                if (split < 0) return;
                var value = line[(split + 1)..];
                switch (line[..split])
                {
                    case "out_time_us":
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var microseconds))
                            fraction = Math.Clamp(microseconds / 1_000_000 / info.Duration, 0, .99);
                        break;
                    case "speed": speed = value; break;
                    case "progress" when lastReport.ElapsedMilliseconds >= 250:
                        progress?.Report(new(fraction, speed));
                        lastReport.Restart();
                        break;
                }
            }, cancellationToken);
            if (result.ExitCode != 0) throw new InvalidOperationException("压缩没有完成，原文件未改动。\n" + result.Error);
            cancellationToken.ThrowIfCancellationRequested();
            var output = await ProbeAsync(temp, cancellationToken);
            if (Math.Abs(output.Duration - info.Duration) > Math.Max(1, info.Duration * .02) || (info.HasAudio && !output.HasAudio))
                throw new InvalidOperationException("压缩结果未通过完整性检查，原文件未改动。");
            var originalBytes = new FileInfo(input).Length;
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
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
    private static async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> args,
        Action<string>? outputLine, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
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
