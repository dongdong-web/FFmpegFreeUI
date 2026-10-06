using System.Diagnostics;
using System.Security.Cryptography;
using LocalCompress.Core;
using LocalCompress.Upstream;

static void Check(bool condition, string message)
{ if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }
static async Task<string> Tool(string executable, params string[] args)
{
    var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
    foreach (var argument in args) start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    var stderr = process.StandardError.ReadToEndAsync();
    var stdout = process.StandardOutput.ReadToEndAsync();
    await process.WaitForExitAsync();
    if (process.ExitCode != 0) throw new Exception(await stderr);
    return await stdout;
}
static async Task Reject(Func<Task> action, string name)
{
    try { await action(); }
    catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or OperationCanceledException or ArgumentException)
    { Console.WriteLine("PASS " + name); return; }
    throw new Exception("Expected rejection: " + name);
}

var tools = ToolPaths.Find(args.Length > 0 ? Path.GetFullPath(args[0]) : AppContext.BaseDirectory) ?? throw new Exception("Integration tests require local ffmpeg.exe and ffprobe.exe in PATH.");
var engine = new CompressionEngine(tools);
var root = Path.Combine(Path.GetTempPath(), "LocalCompress-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    await Reject(() => engine.ProbeAsync("https://example.com/video.mp4", default), "URLs rejected before launching a process");
    await Reject(() => engine.ProbeAsync(@"\\server\share\video.mp4", default), "UNC network paths rejected");
    var input = Path.Combine(root, "中文 空格 & 视频.mp4");
    await Tool(tools.Ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=24", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-t", "3", "-c:v", "libx264", "-crf", "0", "-c:a", "aac", "-metadata", "title=private-title-test", "-metadata:s:a:0", "title=private-audio-test", input);
    var before = SHA256.HashData(await File.ReadAllBytesAsync(input));
    var info = await engine.ProbeAsync(input, default);
    Check(info.HasAudio && info.Width == 320 && Math.Abs(info.Duration - 3) < .1, "media probe reads video and audio");
    var first = await engine.CompressAsync(input, root, CompressionQuality.Balanced, null, default);
    Check(first.OutputBytes < first.OriginalBytes && File.Exists(first.OutputPath), "real compression produces a smaller valid MP4");
    var tags = await Tool(tools.Ffprobe, "-v", "error", "-show_entries", "format_tags:stream_tags", "-of", "json", first.OutputPath);
    Check(!tags.Contains("private-title-test") && !tags.Contains("private-audio-test"), "optional global and stream metadata is removed");
    var second = await engine.CompressAsync(input, root, CompressionQuality.Small, null, default);
    Check(second.OutputPath != first.OutputPath && File.Exists(first.OutputPath), "existing outputs are preserved with unique names");
    var after = SHA256.HashData(await File.ReadAllBytesAsync(input));
    Check(before.SequenceEqual(after), "original content remains byte-for-byte unchanged");
    var noAudio = Path.Combine(root, "无音轨.mp4");
    await Tool(tools.Ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=24", "-t", "2", "-c:v", "libx264", "-crf", "0", noAudio);
    var silent = await engine.CompressAsync(noAudio, root, CompressionQuality.Quality, null, default);
    Check(!(await engine.ProbeAsync(silent.OutputPath, default)).HasAudio, "videos without audio succeed");
    var processed = await engine.CompressAsync(input, root, CompressionQuality.Balanced, null, default,
        new ProcessingOptions(720, ReduceNoise: true, NormalizeAudio: true));
    var processedInfo = await engine.ProbeAsync(processed.OutputPath, default);
    Check(processedInfo.Width == 320 && processedInfo.Height == 240 && processedInfo.HasAudio,
        "upstream scaling never enlarges small videos and combines noise reduction with loudness adjustment");
    var silentProcessed = await engine.CompressAsync(noAudio, root, CompressionQuality.Balanced, null, default,
        new ProcessingOptions(NormalizeAudio: true));
    Check(!(await engine.ProbeAsync(silentProcessed.OutputPath, default)).HasAudio,
        "loudness adjustment is safely skipped for videos without audio");
    var landscape = Path.Combine(root, "横屏 缩小.mp4");
    await Tool(tools.Ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=1280x800:rate=24", "-t", "1", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "0", landscape);
    var landscapeOutput = await engine.CompressAsync(landscape, root, CompressionQuality.Balanced, null, default, new ProcessingOptions(720));
    var landscapeInfo = await engine.ProbeAsync(landscapeOutput.OutputPath, default);
    Check(landscapeInfo.Width == 1152 && landscapeInfo.Height == 720, "upstream preset scales landscape video to 720p with aspect ratio retained");
    var portrait = Path.Combine(root, "竖屏 缩小.mp4");
    await Tool(tools.Ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=800x1280:rate=24", "-t", "1", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "0", portrait);
    var portraitOutput = await engine.CompressAsync(portrait, root, CompressionQuality.Balanced, null, default, new ProcessingOptions(720));
    var portraitInfo = await engine.ProbeAsync(portraitOutput.OutputPath, default);
    Check(portraitInfo.Width == 720 && portraitInfo.Height == 1152, "upstream preset scales portrait video correctly");
    await Reject(() => engine.CompressAsync(input, root, CompressionQuality.Balanced, null, default,
        new ProcessingOptions(999)), "unsupported options cannot inject arbitrary preset arguments");
    Check(!typeof(PresetCompiler).Assembly.GetReferencedAssemblies().Any(x => x.Name is "LakeUI" or "System.Windows.Forms" or "System.Net.Http"),
        "source-linked upstream compiler has no desktop framework or HTTP dependency");
    var hdr = Path.Combine(root, "HDR.mp4");
    await Tool(tools.Ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=24", "-t", "1", "-c:v", "libx264", "-x264-params", "colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc", hdr);
    Check((await engine.ProbeAsync(hdr, default)).IsHdr, "HDR test fixture contains PQ transfer metadata");
    await Reject(() => engine.CompressAsync(hdr, root, CompressionQuality.Balanced, null, default), "HDR rejected without altering the source");
    var broken = Path.Combine(root, "broken.mp4");
    await File.WriteAllTextAsync(broken, "not a video");
    await Reject(() => engine.CompressAsync(broken, root, CompressionQuality.Balanced, null, default), "invalid input reports failure");
    var compact = Path.Combine(root, "已高度压缩.mp4");
    await Tool(tools.Ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=24", "-t", "2", "-c:v", "libx264", "-crf", "51", compact);
    await Reject(() => engine.CompressAsync(compact, root, CompressionQuality.Quality, null, default), "larger recompression is discarded");
    Check(!File.Exists(Path.Combine(root, "已高度压缩_压缩.mp4")), "non-shrinking job leaves no misleading output");
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    await Reject(() => engine.CompressAsync(input, root, CompressionQuality.Balanced, null, cancellation.Token), "cancelled job does not create output");
    var longVideo = Path.Combine(root, "取消测试.mp4");
    await Tool(tools.Ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30", "-t", "15", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "0", longVideo);
    using var activeCancellation = new CancellationTokenSource();
    var cancelProgress = new InlineProgress(_ => activeCancellation.Cancel());
    await Reject(() => engine.CompressAsync(longVideo, root, CompressionQuality.Balanced, cancelProgress, activeCancellation.Token), "running encoder can be cancelled");
    Check(activeCancellation.IsCancellationRequested && File.Exists(longVideo), "in-flight cancellation leaves the original intact");
    using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    listener.Start();
    var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    var playlist = Path.Combine(root, "remote.m3u8");
    await File.WriteAllTextAsync(playlist, $"#EXTM3U\n#EXT-X-TARGETDURATION:5\n#EXTINF:5,\nhttp://127.0.0.1:{port}/video.ts\n#EXT-X-ENDLIST\n");
    await Reject(() => engine.ProbeAsync(playlist, default), "local playlist cannot load remote video segments");
    Check(!listener.Pending(), "FFmpeg input probing makes no HTTP connection for remote playlist segments");
    Check(!Directory.EnumerateFiles(root, ".localcompress-*").Any(), "temporary results are cleaned up");
    var argsList = CompressionEngine.BuildArguments(input, Path.Combine(root, "output.mp4"), CompressionQuality.Balanced);
    Check(argsList.Contains("file,pipe") && argsList.Contains("-map_metadata"), "local protocol restriction and metadata removal are configured");
    Check(argsList.Contains("-n") && !argsList.Contains("-y"), "upstream overwrite behavior is replaced by the no-overwrite policy");
    var filterPlan = CompressionEngine.BuildArguments(input, Path.Combine(root, "options.mp4"), CompressionQuality.Balanced,
        new ProcessingOptions(1080, ReduceNoise: true, NormalizeAudio: true));
    Check(filterPlan.Any(x => x.Contains("hqdn3d")) && filterPlan.Any(x => x.Contains("loudnorm")) && filterPlan.Any(x => x.Contains("1080")),
        "optional filters are generated by the shared upstream preset engine");
    Console.WriteLine("All integration checks passed.");
}

finally
{
    var absolute = Path.GetFullPath(root);
    var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!absolute.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(absolute).StartsWith("LocalCompress-tests-", StringComparison.Ordinal))
        throw new InvalidOperationException("Refusing to clean up outside the test directory.");
    Directory.Delete(absolute, true);
}

sealed class InlineProgress(Action<CompressionProgress> report) : IProgress<CompressionProgress>
{
    public void Report(CompressionProgress value) => report(value);
}
