using System.Globalization;
using System.Text.RegularExpressions;
using LocalCompress.Upstream;

namespace LocalCompress.Core;

public sealed record SceneSample(double Position, long BaselineBytes, long CandidateBytes, double BaselineSsim, double CandidateSsim, double MaximumRegionDrop);
public sealed record SceneDecision(int QualityOffset, IReadOnlyList<SceneSample> Samples, string? FallbackReason = null, bool PreserveOriginal = false);

public sealed partial class CompressionEngine
{
    // These are bounded screening heuristics, not a perceptual-losslessness test.
    // All three locations must qualify; a risky baseline preserves the original.
    private async Task<SceneDecision> AnalyzeSceneAsync(string input, MediaInfo info, VideoScene scene,
        string prefix, IProgress<CompressionProgress>? progress, CancellationToken token)
    {
        const double sampleDuration = 2;
        var offset = scene == VideoScene.Daily ? 1 : 2;
        var minimum = scene == VideoScene.Daily ? .985 : .995;
        var positions = new[] { 0d, (info.Duration - sampleDuration) * .5, info.Duration - sampleDuration };
        var files = new List<string>();
        var samples = new List<SceneSample>();
        long baselineBytes = 0, candidateBytes = 0;
        var qualifies = true;
        var baselineQualifies = true;
        try
        {
            for (var index = 0; index < positions.Length; index++)
            {
                var scores = new double[2][];
                var sizes = new long[2];
                for (var candidate = 0; candidate < 2; candidate++)
                {
                    token.ThrowIfCancellationRequested();
                    progress?.Report(new((index * 2 + candidate) / 6d * .15, "", "试压画面"));
                    var output = prefix + $".sample-{index}-{candidate}.mp4";
                    files.Add(output);
                    var arguments = PresetCompiler.BuildPlan(input, output, 0, hasAudio: false,
                        scene: (int)scene, sceneQualityOffset: candidate == 0 ? 0 : offset)[0].ToList();
                    var inputIndex = arguments.IndexOf("-i");
                    arguments.InsertRange(inputIndex, ["-ss", positions[index].ToString("R", CultureInfo.InvariantCulture)]);
                    arguments.InsertRange(arguments.Count - 1, ["-t", "2"]);
                    var result = await RunAsync(tools.Ffmpeg, arguments, null, token);
                    if (result.ExitCode != 0) return new(0, samples.AsReadOnly(), "Trial encoding unavailable", PreserveOriginal: !baselineQualifies);
                    sizes[candidate] = new FileInfo(output).Length;
                    scores[candidate] = await SampleScoresAsync(input, output, positions[index], scene, info, token);
                    if (candidate == 0 && scores[0].Length > 0) baselineQualifies &= scores[0].Min() >= minimum;
                }
                baselineBytes += sizes[0];
                candidateBytes += sizes[1];
                if (scores[0].Length == 0 || scores[0].Length != scores[1].Length)
                    return new(0, samples.AsReadOnly(), "Trial measurement unavailable", PreserveOriginal: !baselineQualifies);
                var baselineSsim = scores[0].Min();
                var candidateSsim = scores[1].Min();
                var regionDrop = scores[0].Zip(scores[1], (baseline, candidate) => baseline - candidate).Max();
                samples.Add(new(positions[index], sizes[0], sizes[1], baselineSsim, candidateSsim, regionDrop));
                var maxDrop = scene == VideoScene.Daily ? .0015 : .001;
                qualifies &= candidateSsim >= minimum && regionDrop <= maxDrop && sizes[1] <= sizes[0] * .98;
            }
            // Require material savings in video-only samples. Final output still has
            // its own integrity/size checks; sample ratios are never promised to users.
            return new(baselineQualifies && qualifies && candidateBytes <= baselineBytes * .9 ? offset : 0, samples.AsReadOnly(), PreserveOriginal: !baselineQualifies);
        }
        finally
        {
            foreach (var file in files) if (File.Exists(file)) File.Delete(file);
        }
    }

    private async Task<double[]> SampleScoresAsync(string input, string encoded, double position,
        VideoScene scene, MediaInfo info, CancellationToken token)
    {
        var graph = "[0:v]scale=trunc(iw/2)*2:trunc(ih/2)*2,format=yuv420p,settb=AVTB,setpts=PTS-STARTPTS[a];" +
                    "[1:v]format=yuv420p,settb=AVTB,setpts=PTS-STARTPTS[b];";
        var count = scene == VideoScene.Screen && info.Width >= 8 && info.Height >= 8 ? 5 : 1;
        if (count == 1) graph += "[a][b]ssim@region0=shortest=1[s0]";
        else
        {
            graph += "[a]split=5[a0][a1][a2][a3][a4];[b]split=5[b0][b1][b2][b3][b4];[a0][b0]ssim@region0=shortest=1[s0];";
            var width = info.Width / 4 * 2;
            var height = info.Height / 4 * 2;
            for (var region = 1; region <= 4; region++)
            {
                var x = (region - 1) % 2 == 0 ? 0 : (info.Width / 2 * 2 - width);
                var y = region <= 2 ? 0 : (info.Height / 2 * 2 - height);
                graph += $"[a{region}]crop={width}:{height}:{x}:{y}[ar{region}];" +
                         $"[b{region}]crop={width}:{height}:{x}:{y}[br{region}];" +
                         $"[ar{region}][br{region}]ssim@region{region}=shortest=1[s{region}]" + (region == 4 ? "" : ";");
            }
        }
        var arguments = new List<string> { "-hide_banner", "-nostdin", "-protocol_whitelist", "file,pipe",
            "-ss", position.ToString("R", CultureInfo.InvariantCulture), "-t", "2", "-i", input,
            "-protocol_whitelist", "file,pipe", "-i", encoded, "-filter_complex", graph };
        for (var region = 0; region < count; region++) arguments.AddRange(["-map", $"[s{region}]"]);
        arguments.AddRange(["-an", "-f", "null", "NUL"]);
        var result = await RunAsync(tools.Ffmpeg, arguments, null, token);
        if (result.ExitCode != 0) return [];
        var matches = Regex.Matches(result.Error, @"ssim@region([0-9]+).*SSIM.*All:([0-9.]+)");
        if (matches.Count != count) return [];
        var regions = matches.Select(x => (Index: int.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture),
            Score: double.Parse(x.Groups[2].Value, CultureInfo.InvariantCulture))).OrderBy(x => x.Index).ToArray();
        // Pair the named regions, independent of FFmpeg's log emission order.
        return regions.Select(x => x.Index).SequenceEqual(Enumerable.Range(0, count)) && regions.All(x => double.IsFinite(x.Score))
            ? regions.Select(x => x.Score).ToArray() : [];
    }
}
