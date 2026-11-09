using CreatFlow.Platforms.Kuaishou.Requests;

namespace CreatFlow.Platforms.Kuaishou;

/// <summary>
/// 快手合集服务 — CRUD + 详情查询。
///
/// 实现 ICollectionList / ICollectionCreate / ICollectionUpdate / ICollectionDelete。
/// 对标 Python examples/kuaishou/collection/ 下的各 API 示例。
/// </summary>
public sealed class KuaishouCollectionService :
    ICollectionList, ICollectionCreate, ICollectionUpdate, ICollectionDelete
{
    private readonly PlatformOptions _options;
    private readonly ILogger<KuaishouCollectionService>? _logger;

    public KuaishouCollectionService(PlatformOptions options, ILogger<KuaishouCollectionService>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    // ═══ ICollectionList ═══

    /// <inheritdoc />
    public async Task<CollectionListResult> ListAsync(
        string cookie, long cursor = 0, int count = 12, CancellationToken ct = default)
    {
        using var client = CreateClient(cookie);
        var body = new { count, page = cursor == 0 ? 1 : (int)(cursor / count) + 1, showOnProfileFilter = 0, publishStatus = 0, type = "0" };
        var data = await client.PostAsync<KuaishouCollectionListResponse>(
            "/rest/cp/works/v2/collection/list", body, ct);

        var items = data?.List?.Select(ToCollectionInfo).ToList() ?? [];
        var hasMore = (data?.Total ?? 0) > (cursor > 0 ? cursor + count : count);
        return new CollectionListResult(items, hasMore, cursor + count);
    }

    // ═══ ICollectionCreate ═══

    /// <inheritdoc />
    public async Task<CreateCollectionResult> CreateAsync(
        string cookie, CreateCollectionInfo request, CancellationToken ct = default)
    {
        using var client = CreateClient(cookie, apiPh: request.CsrfToken);
        var body = new
        {
            title = request.Title,
            type = "",
            showType = 1,
            photoIdList = request.ItemIds ?? [],
            showStatusType = 1,
        };
        var data = await client.PostAsync<KuaishouCollectionCreateResponse>(
            "/rest/cp/works/v2/collection/add", body, ct);

        if (data is null)
            return new CreateCollectionResult("", false, "Empty response");

        return new CreateCollectionResult(
            data.CollectionId.ToString(),
            true,
            null);
    }

    // ═══ ICollectionUpdate ═══

    /// <inheritdoc />
    public async Task<UpdateCollectionResult> UpdateAsync(
        string cookie, UpdateCollectionInfo request, CancellationToken ct = default)
    {
        using var client = CreateClient(cookie, apiPh: request.CsrfToken);
        var body = new
        {
            collectionId = long.Parse(request.PlatformCollectionId),
            tubeId = request.PlatformCollectionId,
            title = request.Title,
            type = "",
            showType = 1,
            photoIdList = Array.Empty<long>(),
            showStatusType = 1,
        };
        var data = await client.PostAsync<KuaishouCollectionCreateResponse>(
            "/rest/cp/works/v2/collection/update", body, ct);

        return new UpdateCollectionResult(data is not null, null);
    }

    // ═══ ICollectionDelete ═══

    /// <inheritdoc />
    public async Task<DeleteCollectionResult> DeleteAsync(
        string cookie, DeleteCollectionInfo request, CancellationToken ct = default)
    {
        using var client = CreateClient(cookie, apiPh: request.CsrfToken);
        var body = new
        {
            collectionId = long.Parse(request.PlatformCollectionId),
            tubeId = request.PlatformCollectionId,
        };
        // ⚠️ collection/delete **成功时 data 也是 null**（envelope 只有 result=1 + message=成功，
        // 见 examples/kuaishou/collection/delete.json 抓包；add/update 才带 data）。
        // 用 PostAsync 会抛 "null data in response"，于是每次删除都被报成「平台删除失败」——
        // 2026-10-14 用户实测：异步任务页快手子项永远失败、合集记录永远清不掉，
        // 重试多少次都一个样（而平台侧其实每次都删成功了）。
        // 与视频删除同款修法（KuaishouVideoService.cs:145）：PostRawAsync 自己看 result。
        var resp = await client.PostRawAsync<KuaishouSubmitResponse>(
            "/rest/cp/works/v2/collection/delete", body, ct);
        if (resp.Result != 1)
            throw new KuaishouApiException(
                $"Kuaishou collection/delete: result={resp.Result}, msg={resp.Message ?? "?"}",
                resp.Result);
        return new DeleteCollectionResult(true, null);
    }

    // ═══ 额外方法 ═══

    /// <summary>获取合集详情。</summary>
    public async Task<KuaishouCollectionDetail?> DetailAsync(
        string cookie, long collectionId, CancellationToken ct = default)
    {
        using var client = CreateClient(cookie);
        var body = new { collectionId, tubeId = collectionId.ToString() };
        var data = await client.PostAsync<KuaishouCollectionInfoResponse>(
            "/rest/cp/works/v2/collection/info", body, ct);
        if (data is null) return null;
        return new KuaishouCollectionDetail(
            CollectionId: data.CollectionId,
            Title: data.Title ?? "",
            Description: data.Description ?? "",
            Type: data.Type,
            ShowType: data.ShowType,
            PhotoCount: data.PhotoIdList?.Count ?? 0,
            CollectCount: data.CollectCount,
            ViewCount: data.ViewCount,
            CreateTime: data.CreateTime);
    }

    /// <summary>获取未收录作品列表（用于合集添加作品时选择）。</summary>
    public async Task<KuaishouUnrecordedResult> ListUnrecordedAsync(
        string cookie, string? keyword = null, string? cursor = null, int limit = 20, CancellationToken ct = default)
    {
        using var client = CreateClient(cookie);
        var body = new { keyword = keyword ?? "", cursor = cursor ?? "", containsFiltered = true, limit };
        var data = await client.PostAsync<KuaishouUnrecordedListResponse>(
            "/rest/cp/works/v2/collection/unrecorded/list/v2", body, ct);
        return new KuaishouUnrecordedResult(
            Items: data?.List ?? [],
            NextCursor: data?.NextCursor);
    }

    // ═══ Private ═══

    private KuaishouApiClient CreateClient(string cookie, string? apiPh = null) =>
        new(cookie, _options, apiPh, _logger as ILogger<PlatformHttpClient>);

    private static CollectionInfo ToCollectionInfo(KuaishouCollectionItem m) => new(
        PlatformCollectionId: m.CollectionId.ToString(),
        Title: m.Title ?? "",
        Description: m.Description ?? "",
        CoverUrl: m.CoverUrl,
        Status: m.Status,
        StatusText: m.Status switch { 0 => "正常", 1 => "审核中", _ => "未知" },
        VideoCount: m.Size,
        CreatedAt: FromMsTimestamp(m.CreateTime),
        UpdatedAt: null);

    private static DateTime? FromMsTimestamp(long ms)
    {
        if (ms <= 0) return null;
        try { return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime; }
        catch { return null; }
    }
}

public sealed record KuaishouCollectionDetail(
    long CollectionId, string Title, string Description,
    string? Type, int ShowType, int PhotoCount, int CollectCount, long ViewCount, long CreateTime);

public sealed record KuaishouUnrecordedResult(
    List<KuaishouUnrecordedItem> Items, string? NextCursor);
