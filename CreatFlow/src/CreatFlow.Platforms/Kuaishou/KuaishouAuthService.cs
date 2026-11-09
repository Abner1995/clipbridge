using CreatFlow.Platforms.Kuaishou.Requests;

namespace CreatFlow.Platforms.Kuaishou;

/// <summary>
/// 快手认证服务 — 会话探活 + 用户信息获取 + 账号统计。
///
/// 对标 Python fangai/kuaishou/api.py 的 get_user_info()。
/// </summary>
public sealed class KuaishouAuthService
{
    private readonly PlatformOptions _options;
    private readonly ILogger<KuaishouAuthService>? _logger;

    public KuaishouAuthService(PlatformOptions options, ILogger<KuaishouAuthService>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// 获取当前登录用户基本信息（轻量探活）。
    /// POST /rest/cp/creator/pc/home/userInfo (sig3)
    /// </summary>
    public async Task<KuaishouUserResult?> ProbeAsync(string cookie, string? apiPh = null, CancellationToken ct = default)
    {
        try
        {
            using var client = new KuaishouApiClient(cookie, _options, apiPh, _logger as ILogger<PlatformHttpClient>);
            var data = await client.PostAsync<KuaishouUserResponse>(
                "/rest/cp/creator/pc/home/userInfo", new { }, ct);
            var core = data?.CoreUserInfo;
            if (core is null) return null;
            return new KuaishouUserResult(
                UserId: core.UserId,
                UserName: core.UserName ?? "",
                HeadUrl: core.HeadUrl ?? "",
                FansNum: core.FansNum);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Kuaishou userInfo probe failed");
            return null; // 探活失败 = 会话过期
        }
    }

    /// <summary>
    /// 完整探活 — 获取用户信息 + 账号统计（视频数、合集数）。
    /// 比 ProbeAsync 慢（额外 1-2 个 API 调用），但返回更丰富的信息。
    ///
    /// 适用场景：平台管理页首次加载、用户点击"刷新状态"
    /// </summary>
    public async Task<KuaishouAccountInfo?> ProbeFullAsync(
        string cookie, string? apiPh = null, CancellationToken ct = default)
    {
        try
        {
            using var client = new KuaishouApiClient(cookie, _options, apiPh, _logger as ILogger<PlatformHttpClient>);
            var data = await client.PostAsync<KuaishouUserResponse>(
                "/rest/cp/creator/pc/home/userInfo", new { }, ct);
            var core = data?.CoreUserInfo;
            if (core is null) return null;

            // 并行获取视频数和合集数（轻量调用，limit=1 只拿 total）
            int? videoCount = null, collectionCount = null;

            try
            {
                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var videoResp = await client.PostAsync<KuaishouVideoListResponse>(
                    "/rest/cp/works/v2/video/pc/photo/list",
                    new { queryType = "0", cursor = nowMs + 365L * 24 * 3600 * 1000,
                        startTime = nowMs - 365L * 24 * 3600 * 1000,
                        endTime = nowMs + 365L * 24 * 3600 * 1000,
                        limit = 1, timeRangeType = 5, keyword = "" }, ct);
                videoCount = videoResp?.Total;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Kuaishou video list count failed during probe");
            }

            try
            {
                var collResp = await client.PostAsync<KuaishouCollectionListResponse>(
                    "/rest/cp/works/v2/collection/list",
                    new { count = 1, page = 1, showOnProfileFilter = 0,
                        publishStatus = 0, type = "0" }, ct);
                collectionCount = collResp?.Total;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Kuaishou collection list count failed during probe");
            }

            return new KuaishouAccountInfo(
                UserId: core.UserId,
                UserName: core.UserName ?? "",
                HeadUrl: core.HeadUrl ?? "",
                FansNum: core.FansNum,
                VideoCount: videoCount,
                CollectionCount: collectionCount);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Kuaishou full probe failed");
            return null;
        }
    }
}

/// <summary>快手用户基本信息结果（轻量探活）。</summary>
public sealed record KuaishouUserResult(
    long UserId,
    string UserName,
    string HeadUrl,
    int FansNum);

/// <summary>快手账号完整信息（含统计数据）。</summary>
public sealed record KuaishouAccountInfo(
    long UserId,
    string UserName,
    string HeadUrl,
    int FansNum,
    int? VideoCount,
    int? CollectionCount);
