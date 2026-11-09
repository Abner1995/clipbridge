using CreatFlow.Media;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// B站封面双比例准备 —— 把用户选的一张封面裁成 B站投稿需要的两张：
/// **16:9**（<c>add/v3</c> 的 <c>cover</c>，个人空间封面）与
/// **4:3**（<c>add/v3</c> 的 <c>cover43</c>，首页推荐封面）。
///
/// 依据 2026-11-04 抓包「封面制作」弹窗：两个比例同步改动，实际发出的两张图是
/// 同一源图裁出的 1080×607（16:9）与 1080×810（4:3），<c>cover/up</c> 每次上传一张。
///
/// 裁剪用 ffmpeg 表达式做**居中裁切**（不需要先读尺寸，ffmpeg 能解码的图片都行），
/// 再统一缩到宽 1080（与抓包输出一致，小图会放大 —— 页面导出也是 1080 宽）：
/// <code>
/// 16:9 → crop='min(iw,ih*16/9)':'min(ih,iw*9/16)',scale=1080:-2
/// 4:3  → crop='min(iw,ih*4/3)':'min(ih,iw*3/4)',scale=1080:-2
/// </code>
///
/// 已知边界：页面允许用户拖动裁剪位置，这里固定居中（v1 简化，观感差异小；
/// 抓包里的双比例同步改动也没留下偏移参数）。
/// </summary>
public static class BilibiliCoverPreparer
{
    /// <summary>16:9 滤镜（居中裁切 + 缩放到宽 1080，高度按 -2 取偶数）</summary>
    public const string Aspect16x9Filter =
        "crop='min(iw,ih*16/9)':'min(ih,iw*9/16)',scale=1080:-2:flags=lanczos";

    /// <summary>4:3 滤镜（同上）</summary>
    public const string Aspect43Filter =
        "crop='min(iw,ih*4/3)':'min(ih,iw*3/4)',scale=1080:-2:flags=lanczos";

    /// <summary>
    /// 从一张源图生成两张封面 JPEG（16:9 与 4:3）。
    /// 源图不存在 / ffmpeg 失败 → <see cref="IOException"/>。
    /// 产物落在 <paramref name="workDir"/>（调用方负责清理）。
    /// </summary>
    public static async Task<BilibiliCoverPair> PrepareAsync(
        string sourceImagePath, string workDir, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceImagePath) || !File.Exists(sourceImagePath))
            throw new IOException($"封面图不存在：{sourceImagePath}");

        Directory.CreateDirectory(workDir);
        var tag = Guid.NewGuid().ToString("N")[..8];
        var path169 = Path.Combine(workDir, $"bilibili_cover_{tag}_169.jpg");
        var path43 = Path.Combine(workDir, $"bilibili_cover_{tag}_43.jpg");

        await CropAsync(sourceImagePath, path169, Aspect16x9Filter, ct);
        await CropAsync(sourceImagePath, path43, Aspect43Filter, ct);
        return new BilibiliCoverPair(path169, path43);
    }

    private static async Task CropAsync(
        string source, string output, string filter, CancellationToken ct)
    {
        var (_, stderr, code) = await FfmpegCli.RunAsync(new[]
        {
            "-hide_banner", "-y",
            "-i", source,
            "-vf", filter,
            "-frames:v", "1", "-update", "1",
            "-q:v", "3", "-an",
            output,
        }, ct);

        if (code != 0 || !File.Exists(output))
        {
            throw new IOException(
                "B站封面裁剪失败（需要 ffmpeg）: "
                + stderr[..Math.Min(stderr.Length, 300)]);
        }
    }
}

/// <summary>B站封面双比例产物路径。</summary>
public sealed record BilibiliCoverPair(string Path16x9, string Path43);
