using Avalonia.Media.Imaging;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 官方活动封面（平台 CDN 的远程 URL）→ 位图（2.6.4）。
///
/// 为什么不走 <c>PathToBitmapConverter</c>：那是本地路径转换器；活动封面是远程 URL，
/// 要在后台下载、限制并发、按 URL 缓存。
///
/// 三条纪律：
///   1. **永不抛** —— 封面是锦上添花，下载失败就显示首字占位，绝不拦活动列表/发布；
///   2. **并发上限 4** —— 一次拉 20 个活动不打出 20 个并发请求；
///   3. **只解码到 96px 宽** —— 卡片只有 56px，没必要把 1080p 封面整张解进内存。
///
/// 失败也进缓存（同一 URL 不反复重试打平台 CDN）；重启软件后缓存自然失效。
/// </summary>
internal static class ActivityCoverLoader
{
    /// <summary>同时最多几个下载（活动列表一次可能 20~50 条）</summary>
    public const int MaxConcurrent = 4;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly SemaphoreSlim Gate = new(MaxConcurrent, MaxConcurrent);
    private static readonly Dictionary<string, Bitmap?> Cache = new(StringComparer.Ordinal);
    private static readonly object Sync = new();

    /// <summary>
    /// 平台 CDN 的防盗链 Referer（没有对应值 → null，裸请求）。
    /// 封面加载失败不影响功能，这里只做「尽量成功」。
    /// </summary>
    public static string? RefererFor(string platformKey) => platformKey switch
    {
        PlatformNames.Douyin => "https://www.douyin.com/",
        PlatformNames.Kuaishou => "https://cp.kuaishou.com/",
        PlatformNames.Xiaohongshu => "https://creator.xiaohongshu.com/",
        PlatformNames.Bilibili => "https://member.bilibili.com/",
        _ => null,
    };

    /// <summary>按 URL 取封面（命中缓存直接返回；空 URL → null）。</summary>
    public static async Task<Bitmap?> LoadAsync(
        string? url, string? referer = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        lock (Sync)
        {
            if (Cache.TryGetValue(url, out var cached)) return cached;
        }

        await Gate.WaitAsync(ct);
        try
        {
            // 等锁期间可能已被别的卡片下载完 —— 再查一次，避免同 URL 重复下载
            lock (Sync)
            {
                if (Cache.TryGetValue(url, out var cached)) return cached;
            }

            Bitmap? bitmap = null;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrWhiteSpace(referer)) request.Headers.Referrer = new Uri(referer);
                using var response = await Http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.IsSuccessStatusCode)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(ct);
                    var buffer = new MemoryStream();
                    await stream.CopyToAsync(buffer, ct);
                    buffer.Position = 0;
                    // DecodeToWidth：只解到展示需要的宽度，大封面不进全尺寸内存
                    bitmap = Bitmap.DecodeToWidth(buffer, 96);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[Publish] 活动封面加载失败 {Url}", url);
            }

            lock (Sync) Cache[url] = bitmap;
            return bitmap;
        }
        finally
        {
            Gate.Release();
        }
    }
}
