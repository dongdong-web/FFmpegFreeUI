using System.Globalization;
using LocalCompress.Upstream;

namespace LocalCompress.Core;

public sealed record AutomaticSample(double Position, int Profile, long Bytes, double Ssim, double MinimumSsim, double RegionDrop);
public sealed record AutomaticDecision(int Profile, IReadOnlyList<AutomaticSample> Samples, string? FallbackReason = null);

public sealed partial class CompressionEngine
{
    // Short trials select among bounded H.264 settings, never certify losslessness
    // or block the entire job. Missing metrics and timeouts retain the baseline.
    private async Task<AutomaticDecision> SelectAutomaticAsync(string input, MediaInfo info, long originalBytes,
        string prefix, IProgress<CompressionProgress>? progress, CancellationToken token)
    {
        var files = new List<string>();
        var samples = new List<AutomaticSample>();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            var positions = new[] { .1, .5, .9 }.Select(x => (info.Duration - 2) * x).ToArray();
            var totals = new long[3];
            var qualifies = new[] { true, true, true };
            for (var index = 0; index < positions.Length; index++)
            {
                double[] baseline = [];
                for (var profile = 0; profile < 3; profile++)
                {
                    budget.Token.ThrowIfCancellationRequested();
                    progress?.Report(new((index * 3 + profile) / 9d * .15, "", "自动选择压缩方案"));
                    var output = prefix + $".auto-{index}-{profile}.mp4";
                    files.Add(output);
                    var args = PresetCompiler.BuildPlan(input, output, 1, hasAudio: false,
                        preserveTiming: true, automaticProfile: profile)[0].ToList();
                    args.InsertRange(args.IndexOf("-i"), ["-ss", positions[index].ToString("R", CultureInfo.InvariantCulture)]);
                    args.InsertRange(args.Count - 1, ["-t", "2"]);
                    var encoded = await RunAsync(tools.Ffmpeg, args, null, budget.Token);
                    if (encoded.ExitCode != 0) return new(0, samples.AsReadOnly(), "Trial encoding unavailable");
                    // Full frame plus four quadrants catches local detail loss that
                    // can disappear in an average over large empty backgrounds.
                    var scores = await SampleScoresAsync(input, output, positions[index], VideoScene.Screen, info, budget.Token);
                    if (scores.Length == 0 || profile > 0 && scores.Length != baseline.Length)
                        return new(0, samples.AsReadOnly(), "Trial measurement unavailable");
                    if (profile == 0) baseline = scores;
                    var drop = baseline.Zip(scores, (a, b) => a - b).Max();
                    var bytes = new FileInfo(output).Length;
                    totals[profile] += bytes;
                    samples.Add(new(positions[index], profile, bytes, scores[0], scores.Min(), drop));
                    qualifies[profile] &= scores[0] >= .98 && scores.Min() >= .97 && drop <= (profile == 2 ? .008 : .004);
                }
            }
            var tuned = qualifies[1] && totals[1] <= totals[0] * .92 ? 1 : 0;
            var audioRate = info.HasAudio ? (info.AudioCodec == "aac" && info.AudioBitrate is > 0 and <= 128000 ? info.AudioBitrate : 128000) : 0;
            // A rough screening estimate, never exposed as a promised output size.
            // Use stronger settings only when the milder candidate looks unlikely
            // to materially reduce this already compressed input.
            var predicted = totals[tuned] / 6d * info.Duration + audioRate / 8d * info.Duration;
            if (predicted >= originalBytes * .9 && qualifies[2] && totals[2] <= totals[tuned] * .88)
                tuned = 2;
            return new(tuned, samples.AsReadOnly());
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return new(0, samples.AsReadOnly(), "Trial time budget exceeded"); }
        finally
        {
            foreach (var file in files) if (File.Exists(file)) File.Delete(file);
        }
    }
}
