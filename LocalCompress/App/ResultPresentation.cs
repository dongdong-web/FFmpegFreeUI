using LocalCompress.Core;

namespace LocalCompress.App;

public sealed record ResultPresentation(bool CreatedFile, string Status, string Detail)
{
    public static ResultPresentation From(CompressionResult result)
    {
        var original = FormatBytes(result.OriginalBytes);
        if (result.QualityProtected || result.NotSmaller || result.AlreadyWithinTarget)
        {
            var reason = result.QualityProtected ? "试压未通过保守画质检查" :
                result.NotSmaller ? "压缩结果没有比原文件更小" : "原文件已符合大小上限";
            var explanation = result.QualityProtected ? "这不代表视频无法压缩；当前策略为保留画质而跳过。" :
                result.NotSmaller ? "本次方案未压小，临时结果已丢弃；不代表其他方案也无法压缩。" : "无需生成重复文件。";
            return new(false, "未生成文件 · " + reason,
                $"未生成压缩文件：{reason}。\r\n原文件仍为 {original}，本次没有压缩后文件或压缩率。{explanation}\r\n原文件位置：{result.OutputPath}");
        }
        var reduction = 1 - (double)result.OutputBytes / result.OriginalBytes;
        var summary = $"{original} → {FormatBytes(result.OutputBytes)}，减少 {reduction:P1}";
        var mode = result.Automatic is { Profile: 2 } ? "自动加强压缩（有损）。" :
            result.Automatic is { Profile: 1 } ? "自动调教均衡方案（有损）。" : "";
        return new(true, "已生成 · " + summary,
            $"已生成压缩文件：{summary}\r\n保存位置：{result.OutputPath}\r\n{mode}原文件保留。可定位文件或复制保存路径；压缩比例不代表画质评价。");
    }

    private static string FormatBytes(long size) => size >= 1_000_000_000 ? $"{size / 1_000_000_000d:F2} GB" : $"{size / 1_000_000d:F2} MB";
}
