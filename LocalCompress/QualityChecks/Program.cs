using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using LocalCompress.Core;

var tools = ToolPaths.Find(args.Length > 0 ? Path.GetFullPath(args[0]) : AppContext.BaseDirectory)
    ?? throw new Exception("Local FFmpeg is required.");
var reportPath = Path.GetFullPath(args.Length > 1 ? args[1] : "quality-report.md");
var root = Path.Combine(Path.GetTempPath(), "LocalCompress-quality-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var report = new StringBuilder("# 场景压缩回归检查\n\n素材为程序生成的 SDR 测试视频，不是真实手机实拍或用户录屏。SSIM 是回归指标，不是肉眼无损证明。\n\n");
report.AppendLine("| 素材 | 方案 | 压缩后 / 原始字节 | SSIM | 编码秒数 |").AppendLine("| --- | --- | --- | --- | --- |");
try
{
    var engine = new CompressionEngine(tools);
    var font = "C:/Windows/Fonts/arial.ttf";
    if (!File.Exists(font)) throw new Exception("The screen fixture requires the Windows Arial font.");
    var fixtures = new[]
    {
        (Name: "移动图形", Filter: "testsrc2=size=1280x720:rate=30", Scene: VideoScene.Daily, Crop: "", Minimum: .98),
        (Name: "渐变与细线", Filter: "testsrc=size=960x540:rate=30,format=yuv420p", Scene: VideoScene.Daily, Crop: "", Minimum: .98),
        (Name: "录屏小字", Filter: "color=c=white:s=960x540:r=30,drawgrid=w=32:h=32:t=1:c=gray@0.2,drawtext=fontfile='C\\:/Windows/Fonts/arial.ttf':text='Invoice 0123456789 - Total 123.45 - Menu Settings':fontsize=18:fontcolor=black:x=40:y=60,drawtext=fontfile='C\\:/Windows/Fonts/arial.ttf':text='Small text and thin lines must stay readable.':fontsize=16:fontcolor=black:x=40:y=100", Scene: VideoScene.Screen, Crop: ",crop=700:120:40:40", Minimum: .99)
    };
    foreach (var fixture in fixtures.Where(_ => args.Length <= 2))
    {
        var input = Path.Combine(root, fixture.Name + ".mp4");
        await Ffmpeg("-v", "error", "-f", "lavfi", "-i", fixture.Filter, "-t", "3", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "0", input);
        var watch = Stopwatch.StartNew();
        var result = await engine.CompressSceneAsync(input, root, fixture.Scene, null, null, default);
        watch.Stop();
        if (result.NotSmaller) throw new Exception("Fixture did not compress: " + fixture.Name);
        var sceneSsim = await Ssim(input, result.OutputPath, fixture.Crop);
        var baseline = await engine.CompressAsync(input, root, CompressionQuality.Balanced, null, default);
        var oldSsim = await Ssim(input, baseline.OutputPath, fixture.Crop);
        if (sceneSsim < fixture.Minimum || sceneSsim < oldSsim)
            throw new Exception($"Quality regression: {fixture.Name}, scene {sceneSsim:F6}, old {oldSsim:F6}.");
        report.AppendLine(FormattableString.Invariant($"| {fixture.Name} | {(fixture.Scene == VideoScene.Daily ? "日常" : "录屏／课程")} | {result.OutputBytes} / {result.OriginalBytes} | {sceneSsim:F6} | {watch.Elapsed.TotalSeconds:F2} |"));
        report.AppendLine(FormattableString.Invariant($"| {fixture.Name} | 旧版均衡（对照） | {baseline.OutputBytes} / {baseline.OriginalBytes} | {oldSsim:F6} | — |"));
        Console.WriteLine(FormattableString.Invariant($"PASS {fixture.Name}: SSIM {sceneSsim:F6}, old {oldSsim:F6}; {result.OutputBytes}/{result.OriginalBytes} bytes"));
    }
    if (args.Length > 2)
    {
        if ((args.Length - 2) % 2 != 0) throw new ArgumentException("Use --balanced file, --daily file or --screen file pairs after the report path.");
        report.Clear().AppendLine("# 本地真实素材压缩检查\n\n使用用户授权的本地视频，保留原文件。测试结果只适用于所列素材；SSIM 不是肉眼无损证明。输出留在原件旁，便于本机播放对比。\n\n| 视频 | 方案 | 输出 / 输入字节 | SSIM | 秒数 |\n| --- | --- | --- | --- | --- |" );
        var decisionDetails = new StringBuilder();
        for (var index = 2; index < args.Length; index += 2)
        {
            VideoScene? scene = args[index] switch { "--daily" => VideoScene.Daily, "--screen" => VideoScene.Screen, "--balanced" => null, _ => throw new ArgumentException("Unknown profile switch.") };
            var input = CompressionEngine.LocalFile(Path.GetFullPath(args[index + 1]));
            var before = SHA256.HashData(await File.ReadAllBytesAsync(input));
            var info = await engine.ProbeAsync(input, default);
            var watch = Stopwatch.StartNew();
            var result = scene.HasValue ? await engine.CompressSceneAsync(input, Path.GetDirectoryName(input)!, scene.Value, null, null, default)
                : await engine.CompressBalancedAsync(input, Path.GetDirectoryName(input)!, null, null, default);
            watch.Stop();
            var metric = result.QualityProtected ? "未另存：试压提示画质风险" : result.NotSmaller ? "未另存：没有变小" : (await Ssim(input, result.OutputPath, "")).ToString("F6", CultureInfo.InvariantCulture);
            var outputInfo = await engine.ProbeAsync(result.OutputPath, default);
            if (outputInfo.Width != info.Width || outputInfo.Height != info.Height || Math.Abs(info.Duration - outputInfo.Duration) > .1)
                throw new Exception("Real sample dimensions or duration changed.");
            var after = SHA256.HashData(await File.ReadAllBytesAsync(input));
            if (!before.SequenceEqual(after)) throw new Exception("Real sample was modified.");
            report.AppendLine(FormattableString.Invariant($"| {Path.GetFileName(input)} | {scene?.ToString() ?? "Balanced"} | {result.OutputBytes} / {result.OriginalBytes} | {metric} | {watch.Elapsed.TotalSeconds:F2} |"));
            decisionDetails.AppendLine($"\n## {Path.GetFileName(input)}\n\n结果路径：{result.OutputPath}");
            if (result.Decision is { } decision)
            {
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(decision));
                decisionDetails.AppendLine($"\n试压决策：质量偏移 {decision.QualityOffset}；保留原件 {decision.PreserveOriginal}；回退原因 {decision.FallbackReason ?? "无"}。");
                foreach (var sample in decision.Samples)
                    decisionDetails.AppendLine(FormattableString.Invariant($"\n位置 {sample.Position:F2}s：候选/基线视频字节 {sample.CandidateBytes}/{sample.BaselineBytes}，最差 SSIM {sample.CandidateSsim:F6}/{sample.BaselineSsim:F6}，对应区域最大下降 {sample.MaximumRegionDrop:F6}。"));
            }
            Console.WriteLine($"PASS Real {Path.GetFileName(input)}: {result.OutputBytes}/{result.OriginalBytes} bytes, SSIM {metric}, scene offset {result.SceneQualityOffset}, original hash unchanged");
        }
        report.Append(decisionDetails);
        report.AppendLine("\n尺寸和时长已检查，输入 SHA256 不变。尚需用户在原观看设备上播放对比，尤其关注运动、暗部与小字。大小限制模式不受这些恒定质量结果保证。");
    }
    else report.AppendLine("\n录屏指标只统计文字附近的 700×120 区域，避免大片空白掩盖文字损失。阈值用于这组固定素材的回归，不代表其他视频的质量门槛。\n\n下一步仍需真实实拍、暗部、运动和用户录屏的动态观感验收。大小限制模式不受这些恒定质量结果保证。");
    Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
    await File.WriteAllTextAsync(reportPath, report.ToString());
    Console.WriteLine("Report: " + reportPath);
}
finally
{
    var absolute = Path.GetFullPath(root);
    var tempPrefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!absolute.StartsWith(tempPrefix, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(absolute).StartsWith("LocalCompress-quality-", StringComparison.Ordinal))
        throw new Exception("Unsafe fixture cleanup path.");
    Directory.Delete(absolute, true);
}

async Task<string> Ffmpeg(params string[] arguments)
{
    var start = new ProcessStartInfo(tools.Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    var output = process.StandardOutput.ReadToEndAsync();
    var errors = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    var stderr = await errors;
    await output;
    if (process.ExitCode != 0) throw new Exception(stderr);
    return stderr;
}

async Task<double> Ssim(string original, string compressed, string crop)
{
    var filter = $"[0:v]settb=AVTB,setpts=PTS-STARTPTS{crop}[a];[1:v]settb=AVTB,setpts=PTS-STARTPTS{crop}[b];[a][b]ssim";
    var log = await Ffmpeg("-hide_banner", "-i", original, "-i", compressed, "-filter_complex", filter, "-an", "-f", "null", "NUL");
    var match = Regex.Match(log, @"SSIM.*All:([0-9.]+)");
    if (!match.Success) throw new Exception("Missing SSIM measurement: " + log);
    return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
}
