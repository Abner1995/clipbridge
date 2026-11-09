using System.Text.Json;
using CreatFlow.Platforms.Kuaishou.Requests;

namespace CreatFlow.Platforms.Kuaishou;

/// <summary>
/// 快手视频发布服务 — submit API。
///
/// 对标 Python fangai/kuaishou/api.py 的 submit() 方法。
/// POST /rest/cp/works/v2/video/pc/submit — sig4 (__NS_hxfalcon+caver=2)
///
/// 重要：submit API 返回 data:{} — 不返回任何 workId/publishId。
/// SubmitAsync 提交后自动调列表接口，按 coverKey 匹配回传视频 ID。
/// </summary>
public sealed class KuaishouPublishService
{
    private readonly PlatformOptions _options;
    private readonly ILogger<KuaishouPublishService>? _logger;

    /// <summary>submit 后视频进列表的等待参数。</summary>
    private const int ListRetryDelayMs = 3000;
    private const int ListRetryMaxAttempts = 5;

    public KuaishouPublishService(PlatformOptions options, ILogger<KuaishouPublishService>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// 发布视频作品。
    ///
    /// 先调 submit API，成功后自动查列表按 coverKey 匹配拿 workId/publishId。
    /// </summary>
    /// <param name="cookie">Cookie</param>
    /// <param name="request">发布参数</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>发布结果（含 workId / publishId）</returns>
    public async Task<KuaishouPublishResult> SubmitAsync(
        string cookie,
        KuaishouPublishRequest request,
        CancellationToken ct = default)
    {
        using var client = new KuaishouApiClient(cookie, _options,
            logger: _logger as ILogger<PlatformHttpClient>);

        var body = BuildSubmitRequest(request);

        // Use PostRawAsync — submit response has {result, message} without data wrapper
        var resp = await client.PostRawAsync<KuaishouSubmitResponse>(
            "/rest/cp/works/v2/video/pc/submit", body, ct);
        if (resp.Result != 1)
            throw new KuaishouApiException(
                $"Kuaishou submit: result={resp.Result}, msg={resp.Message ?? "?"}",
                resp.Result);

        // submit 返回 data:{} — 没有 ID。通过列表接口按 coverKey 回查。
        _logger?.LogInformation("Kuaishou submit OK, looking up video IDs by coverKey={CoverKey}", request.CoverKey);

        // 等一小会让视频进列表索引，然后回查 ID。
        // 注意：列表查询可能偶发 500002（反爬），不能因此杀死已成功的发布。
        try
        {
            // submit 后等视频进列表索引（快手需要 3-5 秒）
            await Task.Delay(3000, ct);

            var videoSvc = new KuaishouVideoService(_options);
            var item = await videoSvc.FindByCoverKeyAsync(
                cookie, request.CoverKey, request.Caption, request.ApiPh, ct);

            if (item is not null)
            {
                _logger?.LogInformation(
                    "Kuaishou publish: found workId={WorkId}, publishId={PublishId}",
                    item.WorkId, item.PublishId);
                return new KuaishouPublishResult(
                    Success: true,
                    WorkId: item.WorkId,
                    PublishId: item.PublishId,
                    CoverKey: request.CoverKey,
                    ErrorMessage: null);
            }

            _logger?.LogWarning("Kuaishou publish: video submitted but not found in list yet, coverKey={CoverKey}", request.CoverKey);
        }
        catch (Exception ex)
        {
            // 列表回查失败不影响发布结果——视频已经成功提交到快手
            _logger?.LogWarning(ex, "Kuaishou post-submit list lookup failed (non-fatal): coverKey={CoverKey}", request.CoverKey);
        }

        return new KuaishouPublishResult(
            Success: true,
            WorkId: null,
            PublishId: 0,
            CoverKey: request.CoverKey,
            ErrorMessage: null);
    }

    /// <summary>
    /// 发布参数 → submit 请求体（纯映射，自检可直接断言，不发网络请求）。
    ///
    /// 取值口径全部来自 2026-11-05 抓包（<c>docs/kuaishou/20261105/requests/video_submit_*.json</c>）：
    /// · <c>downloadType</c>：1 = 允许下载 / **2 = 禁止**（旧实现传 0 —— 0 不是「禁止」）；
    /// · <c>notifyResult</c>：1 = 通知粉丝 / 0 = 不通知（页面默认 1）；
    /// · <c>coverTimeStamp</c> / <c>pkCoverTimeStamp</c>：毫秒；后者 -1 = 未设置；
    /// · <c>chapters</c> / <c>coPublishUser</c>：元素结构未抓到（抓包恒空数组），原始 JSON 透传。
    /// </summary>
    public static KuaishouSubmitRequest BuildSubmitRequest(KuaishouPublishRequest request) => new()
    {
        FileId = request.FileId,
        CoverKey = request.CoverKey,
        CoverTimeStamp = (int)Math.Clamp(request.CoverFrameMs ?? 0, 0, int.MaxValue),
        Caption = request.Caption ?? "",
        PhotoStatus = request.Visibility switch
        {
            KuaishouVisibility.Friends => 4,
            KuaishouVisibility.Self => 2,
            _ => 1, // Public
        },
        CollectionId = request.CollectionId is > 0 ? request.CollectionId : "",
        PublishTime = request.ScheduledTime?.ToUnixTimeMilliseconds() ?? 0,
        Longitude = request.Longitude ?? "",
        Latitude = request.Latitude ?? "",
        // 抓包是 number（无位置时 0），不是字符串
        PoiId = long.TryParse(request.PoiId, out var poi) && poi > 0 ? poi : 0,
        NotifyResult = request.NotifyFollowers ? 1 : 0,
        CoverTitle = request.CoverTitle ?? "",
        DownloadType = request.AllowDownload ? 1 : 2,
        DisableNearbyShow = !request.ShowInNearby,
        AllowSameFrame = request.AllowSameFrame,
        DeclareInfo = request.Declaration is not null
            ? KuaishouDeclarationOptions.BuildDeclareInfo(
                request.Declaration.Value, request.DeclarationPlatform,
                request.DeclarationLocation, request.DeclarationTime)
            : new Dictionary<string, object>(),
        ActivityIds = BuildActivityIds(request.ActivityIds),
        Chapters = ParseJsonArray(request.ChaptersJson, "章节"),
        CoPublishUser = ParseJsonArray(request.CoPublishUsersJson, "共创用户"),
        CoPublishRole = request.CoPublishRole ?? 0,
        RecTagIdList = request.RecTagIds ?? [],
        // 长尾（1.3 快手表）
        Domain = request.Domain ?? "",
        SecondDomain = request.SecondDomain ?? "",
        MovieId = request.MovieId ?? "",
        ProjectId = request.ProjectId ?? "",
        PkCoverKey = request.PkCoverKey ?? "",
        PkCoverTimeStamp = (int)Math.Clamp(request.PkCoverTimeStamp ?? -1, int.MinValue, int.MaxValue),
        PkCoverType = request.PkCoverType ?? 1,
        PkCoverSize = request.PkCoverSize ?? "",
        ProfileCoverKey = request.ProfileCoverKey ?? "",
        RiseQuality = request.RiseQuality ?? false,
        UseAiCaption = request.UseAiCaption ?? false,
        UseAiCaptionCover = request.UseAiCaptionCover ?? false,
        UseAiCover = request.UseAiCover ?? false,
        IsUseIdealTime = request.UseIdealTime ?? false,
        DisallowRecreation = request.DisallowRecreation ?? false,
        MediaId = request.MediaId,
        PhotoIdStr = request.PhotoIdStr,
        VideoDuration = request.VideoDuration,
        ApiPh = request.ApiPh ?? "393eb14dff89997d06918e8c11c9e4946041",
    };

    /// <summary>
    /// 官方活动 ID 数组：数字字符串 → JSON 数字，其余保留字符串（与抖音 <c>activity</c> 同款口径）。
    /// 元素结构未抓到 —— 活动接口里的 ID 是数字（如 318524），这里做一次保守归一。
    /// </summary>
    public static List<object> BuildActivityIds(IEnumerable<string>? ids)
    {
        var values = new List<object>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in ids ?? [])
        {
            var id = raw?.Trim();
            if (string.IsNullOrEmpty(id) || !seen.Add(id)) continue;
            values.Add(long.TryParse(id, out var num) ? num : id);
        }
        return values;
    }

    /// <summary>
    /// 原始 JSON 数组 → 可序列化节点（空/未填 = 空数组）。
    /// **格式坏就报错**（不静默丢成 []）—— 用户填了内容却发不出去，比直接失败更难排查。
    /// </summary>
    public static object ParseJsonArray(string? json, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<object>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw new KuaishouApiException($"快手{fieldName}必须是 JSON 数组", -1);
            return doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new KuaishouApiException($"快手{fieldName} JSON 格式不正确：{ex.Message}", -1);
        }
    }
}

/// <summary>快手发布请求参数。</summary>
public sealed class KuaishouPublishRequest
{
    /// <summary>文件 ID（从 upload finish 获取）</summary>
    public required long FileId { get; init; }

    /// <summary>封面 key（从 upload finish 获取）</summary>
    public required string CoverKey { get; init; }

    /// <summary>作品描述/标题</summary>
    public string? Caption { get; init; }

    /// <summary>谁可以看</summary>
    public KuaishouVisibility Visibility { get; init; } = KuaishouVisibility.Public;

    /// <summary>合集 ID（null = 不加入合集）</summary>
    public long? CollectionId { get; init; }

    /// <summary>定时发布时间（null = 立即发布）</summary>
    public DateTimeOffset? ScheduledTime { get; init; }

    /// <summary>经度</summary>
    public string? Longitude { get; init; }

    /// <summary>纬度</summary>
    public string? Latitude { get; init; }

    /// <summary>POI ID</summary>
    public string? PoiId { get; init; }

    /// <summary>是否允许下载</summary>
    public bool AllowDownload { get; init; } = true;

    /// <summary>是否在同城页展示</summary>
    public bool ShowInNearby { get; init; } = true;

    /// <summary>是否允许同框</summary>
    public bool AllowSameFrame { get; init; } = true;

    /// <summary>发布后通知粉丝（1 = 通知 / 0 = 不通知）</summary>
    public bool NotifyFollowers { get; init; } = true;

    /// <summary>封面帧（毫秒）—— <c>coverTimeStamp</c>；null = 0（首帧）</summary>
    public long? CoverFrameMs { get; init; }

    /// <summary>封面文字 —— <c>coverTitle</c></summary>
    public string? CoverTitle { get; init; }

    /// <summary>PK 封面 key —— <c>pkCoverKey</c></summary>
    public string? PkCoverKey { get; init; }

    /// <summary>PK 封面帧（毫秒；-1 = 未设置）—— <c>pkCoverTimeStamp</c></summary>
    public long? PkCoverTimeStamp { get; init; }

    /// <summary>PK 封面类型 —— <c>pkCoverType</c>（抓包默认 1）</summary>
    public int? PkCoverType { get; init; }

    /// <summary>PK 封面尺寸 —— <c>pkCoverSize</c></summary>
    public string? PkCoverSize { get; init; }

    /// <summary>个人主页封面 key —— <c>profileCoverKey</c></summary>
    public string? ProfileCoverKey { get; init; }

    /// <summary>画质增强 —— <c>riseQuality</c></summary>
    public bool? RiseQuality { get; init; }

    /// <summary>AI 字幕 —— <c>useAiCaption</c></summary>
    public bool? UseAiCaption { get; init; }

    /// <summary>AI 字幕封面 —— <c>useAiCaptionCover</c></summary>
    public bool? UseAiCaptionCover { get; init; }

    /// <summary>AI 封面 —— <c>useAiCover</c></summary>
    public bool? UseAiCover { get; init; }

    /// <summary>智能发布时间 —— <c>isUseIdealTime</c></summary>
    public bool? UseIdealTime { get; init; }

    /// <summary>禁止二创 —— <c>disallowRecreation</c></summary>
    public bool? DisallowRecreation { get; init; }

    /// <summary>专属领域 —— <c>domain</c></summary>
    public string? Domain { get; init; }

    /// <summary>二级领域 —— <c>secondDomain</c></summary>
    public string? SecondDomain { get; init; }

    /// <summary>关联短剧 —— <c>movieId</c></summary>
    public string? MovieId { get; init; }

    /// <summary>项目 ID —— <c>projectId</c></summary>
    public string? ProjectId { get; init; }

    /// <summary>章节原始 JSON 数组（元素结构未抓到；空 = <c>[]</c>）</summary>
    public string? ChaptersJson { get; init; }

    /// <summary>共创用户原始 JSON 数组（元素结构未抓到；空 = <c>[]</c>）</summary>
    public string? CoPublishUsersJson { get; init; }

    /// <summary>共创角色 —— <c>coPublishRole</c></summary>
    public int? CoPublishRole { get; init; }

    /// <summary>官方活动 ID</summary>
    public List<string>? ActivityIds { get; init; }

    /// <summary>作者声明类型（1-4）</summary>
    public int? Declaration { get; init; }

    /// <summary>来源平台（声明类型=4 时: 1=快手, 2=其他）</summary>
    public int? DeclarationPlatform { get; init; }

    /// <summary>来源地区（声明类型=4 时）</summary>
    public string? DeclarationLocation { get; init; }

    /// <summary>发生时间戳 ms（声明类型=4 时）</summary>
    public long? DeclarationTime { get; init; }

    /// <summary>推荐标签 ID 列表</summary>
    public List<long>? RecTagIds { get; init; }

    /// <summary>媒体 ID（从 upload finish 获取）</summary>
    public required string MediaId { get; init; }

    /// <summary>作品 ID 字符串（从 upload finish 获取）</summary>
    public required string PhotoIdStr { get; init; }

    /// <summary>视频时长（毫秒）</summary>
    public required int VideoDuration { get; init; }

    /// <summary>API PH token</summary>
    public string? ApiPh { get; init; }
}

/// <summary>快手可见性。</summary>
public enum KuaishouVisibility
{
    /// <summary>所有人可见 (photoStatus=1)</summary>
    Public = 1,
    /// <summary>仅好友可见 (photoStatus=4)</summary>
    Friends = 4,
    /// <summary>仅自己可见 (photoStatus=2)</summary>
    Self = 2,
}

/// <summary>
/// 快手发布结果。
///
/// submit API 返回空 data，workId/publishId 通过列表接口回查获得。
/// </summary>
public sealed record KuaishouPublishResult(
    bool Success,
    string? WorkId,
    long PublishId,
    string? CoverKey,
    string? ErrorMessage);
