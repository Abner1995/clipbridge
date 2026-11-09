namespace CreatFlow.Platforms.WechatVideo;

using CreatFlow.Platforms.Core;
using CreatFlow.Platforms.WechatVideo.Requests;
using Serilog;

/// <summary>
/// 微信视频号合集服务 — 封装 channels.weixin.qq.com 合集 CRUD + 置顶 API。
///
/// 实现 ICollectionList / ICollectionCreate / ICollectionUpdate / ICollectionDelete / ICollectionPin。
///
/// 对标 Python examples/wechatvideo/collection/ 下的各接口。
///
/// API 端点（全部 POST，Cookie + device_id 认证）：
///   List:   /collection/get_collection_list
///   Create: /collection/create_collection
///   Update: /collection/update_collection_info  （含置顶 stickyOp=1/2）
///   Delete: /collection/delete_collection
/// </summary>
public sealed class WechatVideoCollectionService :
    ICollectionList, ICollectionCreate, ICollectionUpdate, ICollectionDelete, ICollectionPin
{
    private readonly PlatformOptions _options;
    private readonly ILogger<WechatVideoCollectionService>? _logger;

    public WechatVideoCollectionService(
        PlatformOptions options,
        ILogger<WechatVideoCollectionService>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    private void LogInfo(string message, params object[] args)
    {
        if (_logger is not null) _logger.LogInformation(message, args);
        else Log.Information(message, args);
    }

    private void LogDebug(string message, params object[] args)
    {
        if (_logger is not null) _logger.LogDebug(message, args);
        else Log.Debug(message, args);
    }

    private void LogWarn(string message, params object[] args)
    {
        if (_logger is not null) _logger.LogWarning(message, args);
        else Log.Warning(message, args);
    }

    // ═══ ICollectionList — 获取合集列表 ═══

    /// <inheritdoc />
    public async Task<CollectionListResult> ListAsync(
        string cookie,
        long cursor = 0,
        int count = 12,
        CancellationToken ct = default)
    {
        var deviceId = ResolveDeviceId(null, cookie);
        using var client = new WechatVideoApiClient(_options, cookie, deviceId);

        var body = new Dictionary<string, object?>
        {
            ["pageNum"] = cursor > 0 ? (int)(cursor / count) + 1 : 1,
            ["pageSize"] = count,
            ["collectionScene"] = 0,
            ["collectionBusinessType"] = 0,
        };

        LogDebug("[WechatVideo] Collection list → page={Page}, size={Size}", (int)(cursor / count) + 1, count);

        var resp = await client.PostAsync("/collection/get_collection_list", body,
            "https://channels.weixin.qq.com/micro/content/post/list?tab=collection", ct);

        if (resp.ErrCode != 0)
        {
            LogWarn("[WechatVideo] Collection list failed: errCode={Code}", resp.ErrCode);
            return new CollectionListResult([], false, 0);
        }

        var listData = JsonSerializer.Deserialize<CollectionListData>(
            resp.Data.GetRawText(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (listData?.Collections is null)
            return new CollectionListResult([], false, 0);

        var items = listData.Collections.Select(c => new CollectionInfo(
            PlatformCollectionId: c.CollectionId ?? "",
            Title: c.Name ?? "",
            Description: c.Description,
            CoverUrl: c.CoverUrl,
            Status: c.Status,
            StatusText: c.Status == 0 ? "正常" : "异常",
            VideoCount: c.VideoCount,
            CreatedAt: DateTimeOffset.FromUnixTimeSeconds(c.CreateTime).UtcDateTime,
            UpdatedAt: DateTimeOffset.FromUnixTimeSeconds(c.UpdateTime).UtcDateTime
        )).ToList();

        var nextCursor = items.Count >= count ? cursor + count : 0;
        return new CollectionListResult(items, nextCursor > 0, nextCursor);
    }

    // ═══ ICollectionCreate — 创建合集 ═══

    /// <inheritdoc />
    public async Task<CreateCollectionResult> CreateAsync(
        string cookie,
        CreateCollectionInfo request,
        CancellationToken ct = default)
    {
        var deviceId = ResolveDeviceId(request.CsrfToken, cookie);

        using var client = new WechatVideoApiClient(_options, cookie, deviceId);

        var body = new Dictionary<string, object?>
        {
            ["name"] = request.Title,
            ["collectionBusinessType"] = 0,
        };

        var resp = await client.PostAsync("/collection/create_collection", body,
            "https://channels.weixin.qq.com/micro/content/post/list?tab=collection", ct);

        if (resp.ErrCode != 0)
            return new CreateCollectionResult("", false, resp.ErrMsg ?? $"errCode={resp.ErrCode}");

        var createData = JsonSerializer.Deserialize<CreateCollectionData>(
            resp.Data.GetRawText(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var collectionId = createData?.CollectionId ?? "";
        LogInfo("[WechatVideo] Collection created: id={Id}", collectionId);
        return new CreateCollectionResult(collectionId, true, null);
    }

    // ═══ ICollectionUpdate — 更新合集（含置顶 stickyOp） ═══

    /// <inheritdoc />
    public async Task<UpdateCollectionResult> UpdateAsync(
        string cookie,
        UpdateCollectionInfo request,
        CancellationToken ct = default)
    {
        var deviceId = ResolveDeviceId(request.CsrfToken, cookie);
        using var client = new WechatVideoApiClient(_options, cookie, deviceId);

        var body = new Dictionary<string, object?>
        {
            ["name"] = request.Title,
            ["updateCollectionScene"] = 0,
            ["collectionId"] = request.PlatformCollectionId,
            ["collectionBusinessType"] = 0,
        };

        LogDebug("[WechatVideo] Collection update → id={Id}, name={Name}",
            request.PlatformCollectionId, request.Title);

        var resp = await client.PostAsync("/collection/update_collection_info", body,
            "https://channels.weixin.qq.com/micro/content/collection/item", ct);

        var ok = resp.ErrCode == 0;
        if (!ok) LogWarn("[WechatVideo] Collection update failed: errCode={Code}, errMsg={Msg}",
            resp.ErrCode, resp.ErrMsg);
        return new UpdateCollectionResult(ok, ok ? null : resp.ErrMsg);
    }

    // ═══ ICollectionDelete — 删除合集 ═══

    /// <inheritdoc />
    public async Task<DeleteCollectionResult> DeleteAsync(
        string cookie,
        DeleteCollectionInfo request,
        CancellationToken ct = default)
    {
        var deviceId = ResolveDeviceId(request.CsrfToken, cookie);
        using var client = new WechatVideoApiClient(_options, cookie, deviceId);

        var body = new Dictionary<string, object?>
        {
            ["collectionId"] = request.PlatformCollectionId,
            ["collectionBusinessType"] = 0,
        };

        LogDebug("[WechatVideo] Collection delete → id={Id}", request.PlatformCollectionId);

        var resp = await client.PostAsync("/collection/delete_collection", body,
            "https://channels.weixin.qq.com/micro/content/post/list?tab=collection", ct);

        var ok = resp.ErrCode == 0;
        if (!ok) LogWarn("[WechatVideo] Collection delete failed: errCode={Code}, errMsg={Msg}",
            resp.ErrCode, resp.ErrMsg);
        return new DeleteCollectionResult(ok, ok ? null : resp.ErrMsg);
    }

    // ═══ ICollectionPin — 置顶/取消置顶合集 ═══

    /// <inheritdoc />
    public async Task<PinCollectionResult> PinAsync(
        string cookie,
        string platformCollectionId,
        bool pinned,
        CancellationToken ct = default)
    {
        var deviceId = ResolveDeviceId(null, cookie);
        using var client = new WechatVideoApiClient(_options, cookie, deviceId);

        // stickyOp: 1=置顶, 2=取消置顶
        var body = new Dictionary<string, object?>
        {
            ["collectionId"] = platformCollectionId,
            ["collectionBusinessType"] = 0,
            ["updateCollectionScene"] = 0,
            ["stickyOp"] = pinned ? 1 : 2,
        };

        LogDebug("[WechatVideo] Collection pin → id={Id}, pinned={Pinned}", platformCollectionId, pinned);

        var resp = await client.PostAsync("/collection/update_collection_info", body,
            "https://channels.weixin.qq.com/micro/content/collection/item", ct);

        var ok = resp.ErrCode == 0;
        if (!ok) LogWarn("[WechatVideo] Collection pin failed: errCode={Code}, errMsg={Msg}",
            resp.ErrCode, resp.ErrMsg);
        return new PinCollectionResult(ok, ok ? null : resp.ErrMsg);
    }

    // ═══ Helper: 搜索位置 ═══

    /// <summary>
    /// 搜索位置 POI（用于发布页添加位置）。
    ///
    /// 端点: POST /helper/helper_search_location
    /// </summary>
    public async Task<List<LocationItem>> SearchLocationAsync(
        string cookie,
        string deviceId,
        string query,
        double longitude = 0,
        double latitude = 0,
        CancellationToken ct = default)
    {
        using var client = new WechatVideoApiClient(_options, cookie, deviceId);

        var body = new Dictionary<string, object?>
        {
            ["query"] = query,
            ["cookies"] = "",
            ["longitude"] = longitude,
            ["latitude"] = latitude,
        };

        var resp = await client.PostAsync("/helper/helper_search_location", body,
            "https://channels.weixin.qq.com/micro/content/post/create", ct);

        if (resp.ErrCode != 0) return [];

        var locData = JsonSerializer.Deserialize<LocationSearchData>(
            resp.Data.GetRawText(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        return locData?.List ?? [];
    }

    // ═══ Private Helpers ═══

    /// <summary>
    /// 解析 device_id：优先用显式传入的值（来自 ExtraDataJson），
    /// 为空时 fallback 到 Cookie 中的 _qimei_fingerprint。
    ///
    /// 注意：不能用 ?? 因为传进来的可能是空字符串 ""（不是 null），
    /// 空字符串同样需要 fallback。
    /// </summary>
    private static string ResolveDeviceId(string? explicitDeviceId, string cookie)
    {
        if (!string.IsNullOrWhiteSpace(explicitDeviceId))
            return explicitDeviceId;

        // Fallback: 从 Cookie 提取 _qimei_fingerprint
        foreach (var part in cookie.Split("; "))
        {
            if (part.StartsWith("_qimei_fingerprint=", StringComparison.OrdinalIgnoreCase))
                return part["_qimei_fingerprint=".Length..];
        }
        return "";
    }

    /// <summary>从 Cookie 字符串提取指定 key 的值</summary>
    private static string? ExtractFromCookie(string cookie, string key)
    {
        foreach (var part in cookie.Split("; "))
        {
            if (part.StartsWith($"{key}=", StringComparison.OrdinalIgnoreCase))
                return part[(key.Length + 1)..];
        }
        return null;
    }
}
