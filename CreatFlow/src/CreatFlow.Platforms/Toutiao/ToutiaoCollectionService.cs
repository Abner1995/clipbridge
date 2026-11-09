using CreatFlow.Platforms.Toutiao.Requests;

namespace CreatFlow.Platforms.Toutiao;

/// <summary>
/// 今日头条视频合集（专辑 / pSeries）服务 — 列表 / 创建 / 编辑 / 删除 / 详情。
///
/// 头条另有「文章合集」一套完全不同的接口（/mp/fe_api/collection/*）；本服务只做
/// **视频合集**（App 只发视频）。2026-11-05 抓包（docs/toutiao/20261105/合集接口.md）：
///   - 列表：GET  /xigua/api/pSeries/GetAlbumInfoByMediaId/
///   - 详情：GET  /xigua/api/pSeries/GetPseriesInfo/
///   - 创建/编辑：POST /xigua/api/pSeries/PublishPseries/（PublishType 1/2）
///   - 删除：POST /xigua/api/pSeries/DeletePseries/
/// 写接口必须带 x-secsdk-csrf-token（页面 secsdk 现签）。
/// </summary>
public sealed class ToutiaoCollectionService :
    ICollectionList, ICollectionCreate, ICollectionUpdate, ICollectionDelete
{
    private const string ManageReferer =
        "https://mp.toutiao.com/profile_v4/xigua/content-manage-collections?action=pSeriesManage";
    private const string PublishReferer =
        "https://mp.toutiao.com/profile_v4/xigua/upload-video?index=2";

    private readonly PlatformOptions _options;
    private readonly ILogger<ToutiaoCollectionService>? _logger;

    public ToutiaoCollectionService(
        PlatformOptions options, ILogger<ToutiaoCollectionService>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    // ═══ ICollectionList ═══

    /// <inheritdoc />
    public async Task<CollectionListResult> ListAsync(
        string cookie, long cursor = 0, int count = 12, CancellationToken ct = default)
    {
        using var client = new ToutiaoApiClient(cookie, _options, null, ManageReferer);
        var offset = (int)Math.Max(0, cursor);
        var paramsJson = JsonSerializer.Serialize(new { Offset = offset, SortType = 1 });
        var url = _options.ToutiaoHost + "/xigua/api/pSeries/GetAlbumInfoByMediaId/?params=" +
                  Uri.EscapeDataString(paramsJson);

        var resp = await client.GetJsonAsync<ToutiaoPseriesListResponse>(url, ct);
        if (resp is not { Status: 0 } || resp.Data?.Items is null)
        {
            _logger?.LogWarning("Toutiao pSeries list failed: status={Status}, msg={Msg}",
                resp?.Status, resp?.Message);
            return new CollectionListResult([], false, cursor);
        }

        var items = resp.Data.Items.Select(ToCollectionInfo).ToList();
        var total = resp.Data.Total;
        var next = offset + items.Count;
        return new CollectionListResult(items, next < total, next);
    }

    // ═══ ICollectionCreate ═══

    /// <inheritdoc />
    public async Task<CreateCollectionResult> CreateAsync(
        string cookie, CreateCollectionInfo request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.CoverTosKey))
            return new CreateCollectionResult("", false, "头条合集需要先上传封面图");
        if (string.IsNullOrWhiteSpace(request.CsrfToken))
            return new CreateCollectionResult("", false,
                "缺少头条 CSRF 凭证：请打开头条账号窗口（加载完页面后点「完成登录」）刷新凭证后重试");
        if (TitleLengthError(request.Title) is { } titleError)
            return new CreateCollectionResult("", false, titleError);

        using var client = new ToutiaoApiClient(cookie, _options, request.CsrfToken, PublishReferer);
        var body = new
        {
            AlbumInfo = new
            {
                AlbumId = "",
                SeqsCount = 0,
                Title = request.Title,
                CoverUri = request.CoverTosKey,
                CoverHeight = request.CoverHeight,
                CoverWidth = request.CoverWidth,
            },
            PublishType = 1,
            UpdateEpisodes = Array.Empty<object>(),
            DeleteEpisodeIds = Array.Empty<string>(),
            Attrs = new { from_recommend = "0" },
        };

        try
        {
            var resp = await client.PostJsonAsync<ToutiaoPublishPseriesResponse>(
                _options.ToutiaoHost + "/xigua/api/pSeries/PublishPseries/", body, ct);
            if (resp is not { Status: 0 } || string.IsNullOrWhiteSpace(resp.Data?.AlbumId))
            {
                var error = resp?.Message is { Length: > 0 } m
                    ? m
                    : $"创建合集失败（status={resp?.Status}）";
                _logger?.LogWarning("Toutiao create collection rejected: {Error}", error);
                return new CreateCollectionResult("", false, error);
            }

            _logger?.LogInformation("Toutiao create collection OK: AlbumId={AlbumId}",
                resp.Data.AlbumId);
            return new CreateCollectionResult(resp.Data.AlbumId, true, null);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Toutiao create collection failed");
            return new CreateCollectionResult("", false, ex.Message);
        }
    }

    // ═══ ICollectionUpdate ═══

    /// <inheritdoc />
    public async Task<UpdateCollectionResult> UpdateAsync(
        string cookie, UpdateCollectionInfo request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.CsrfToken))
            return new UpdateCollectionResult(false,
                "缺少头条 CSRF 凭证：请打开头条账号窗口（加载完页面后点「完成登录」）刷新凭证后重试");
        if (TitleLengthError(request.Title) is { } titleError)
            return new UpdateCollectionResult(false, titleError);

        // 编辑页是 upload-video?from=edit（抓包用这个 referer）；与创建共用发布页 referer。
        using var client = new ToutiaoApiClient(cookie, _options, request.CsrfToken, PublishReferer);

        try
        {
            // 没换封面时平台仍要求 CoverUri —— 查详情回填当前封面/尺寸/集数。
            var detail = await GetDetailAsync(client, request.PlatformCollectionId, ct);
            var coverUri = string.IsNullOrWhiteSpace(request.CoverTosKey)
                ? detail?.CoverUri
                : request.CoverTosKey;
            if (string.IsNullOrWhiteSpace(coverUri))
                return new UpdateCollectionResult(false,
                    "未获取到头条合集的当前封面，无法编辑（请重新选择封面图）");

            var width = request.CoverWidth > 0 ? request.CoverWidth : detail?.CoverWidth ?? 0;
            var height = request.CoverHeight > 0 ? request.CoverHeight : detail?.CoverHeight ?? 0;

            var body = new
            {
                AlbumInfo = new
                {
                    AlbumId = request.PlatformCollectionId,
                    SeqsCount = detail?.SeqsCount ?? 0,
                    Title = request.Title,
                    CoverUri = coverUri,
                    CoverHeight = height,
                    CoverWidth = width,
                },
                PublishType = 2,
                UpdateEpisodes = Array.Empty<object>(),
                DeleteEpisodeIds = Array.Empty<string>(),
                Attrs = new Dictionary<string, string>(),
            };

            var resp = await client.PostJsonAsync<ToutiaoPublishPseriesResponse>(
                _options.ToutiaoHost + "/xigua/api/pSeries/PublishPseries/", body, ct);
            var ok = resp is { Status: 0 }
                && (!string.IsNullOrWhiteSpace(resp.Data?.AlbumId)
                    || resp.Data?.BaseResp is { StatusCode: 0 });
            if (!ok)
            {
                var error = resp?.Message is { Length: > 0 } m
                    ? m
                    : $"编辑合集失败（status={resp?.Status}）";
                _logger?.LogWarning("Toutiao update collection rejected: {Error}", error);
                return new UpdateCollectionResult(false, error);
            }

            _logger?.LogInformation("Toutiao update collection OK: AlbumId={AlbumId}",
                request.PlatformCollectionId);
            return new UpdateCollectionResult(true, null);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Toutiao update collection failed: id={Id}",
                request.PlatformCollectionId);
            return new UpdateCollectionResult(false, ex.Message);
        }
    }

    // ═══ ICollectionDelete ═══

    /// <inheritdoc />
    public async Task<DeleteCollectionResult> DeleteAsync(
        string cookie, DeleteCollectionInfo request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.CsrfToken))
            return new DeleteCollectionResult(false,
                "缺少头条 CSRF 凭证：请打开头条账号窗口（加载完页面后点「完成登录」）刷新凭证后重试");

        using var client = new ToutiaoApiClient(cookie, _options, request.CsrfToken, ManageReferer);
        var body = new { AlbumIdList = new[] { request.PlatformCollectionId } };

        try
        {
            var resp = await client.PostJsonAsync<ToutiaoDeletePseriesResponse>(
                _options.ToutiaoHost + "/xigua/api/pSeries/DeletePseries/", body, ct);
            var ok = resp is { Status: 0 } && resp.Data?.BaseResp is { StatusCode: 0 };
            if (!ok)
            {
                var error = resp?.Message is { Length: > 0 } m
                    ? m
                    : resp?.Data?.BaseResp?.StatusMessage ?? $"删除合集失败（status={resp?.Status}）";
                _logger?.LogWarning("Toutiao delete collection rejected: {Error}", error);
                return new DeleteCollectionResult(false, error);
            }

            _logger?.LogInformation("Toutiao delete collection OK: AlbumId={AlbumId}",
                request.PlatformCollectionId);
            return new DeleteCollectionResult(true, null);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Toutiao delete collection failed: id={Id}",
                request.PlatformCollectionId);
            return new DeleteCollectionResult(false, ex.Message);
        }
    }

    // ═══ 额外方法 ═══

    /// <summary>获取视频合集详情（编辑时回填封面/集数）。</summary>
    public async Task<ToutiaoPseriesAlbum?> DetailAsync(
        string cookie, string csrfToken, string albumId, CancellationToken ct = default)
    {
        using var client = new ToutiaoApiClient(cookie, _options, csrfToken, ManageReferer);
        return await GetDetailAsync(client, albumId, ct);
    }

    private async Task<ToutiaoPseriesAlbum?> GetDetailAsync(
        ToutiaoApiClient client, string albumId, CancellationToken ct)
    {
        var paramsJson = JsonSerializer.Serialize(new
        {
            AlbumId = albumId,
            Limit = 30,
            Offset = 0,
            ReadOption = 2,
        });
        var url = _options.ToutiaoHost + "/xigua/api/pSeries/GetPseriesInfo/?params=" +
                  Uri.EscapeDataString(paramsJson);
        var resp = await client.GetJsonAsync<ToutiaoPseriesDetailResponse>(url, ct);
        return resp is { Status: 0 } ? resp.Data?.Album : null;
    }

    /// <summary>头条视频合集标题 UI 约束 5~30 字（平台侧也会校验，这里先拦一次给出可读文案）。</summary>
    private static string? TitleLengthError(string? title)
    {
        var length = title?.Trim().Length ?? 0;
        return length is >= 5 and <= 30
            ? null
            : $"头条合集标题需 5~30 字（当前 {length} 字）";
    }

    private static CollectionInfo ToCollectionInfo(ToutiaoPseriesItem item) => new(
        PlatformCollectionId: item.Id,
        Title: item.Title,
        Description: null,
        CoverUrl: item.ImageUrl ?? item.ImageUri,
        Status: item.ArticleStatusInstance?.ItemStatusCode ?? 0,
        StatusText: (item.ArticleStatusInstance?.ItemStatusCode ?? 0) switch
        {
            20 => "已发布",
            40 => "审核中",
            _ => "未知",
        },
        VideoCount: item.AlbumItemNum,
        CreatedAt: item.CreateTime > 0
            ? DateTimeOffset.FromUnixTimeSeconds(item.CreateTime).UtcDateTime
            : null,
        UpdatedAt: null);
}
