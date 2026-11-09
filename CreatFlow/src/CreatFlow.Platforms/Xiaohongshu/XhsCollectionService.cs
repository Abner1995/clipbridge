using CreatFlow.Platforms.Xiaohongshu.Requests;

namespace CreatFlow.Platforms.Xiaohongshu;

/// <summary>
/// 小红书合集服务 — CRUD + add/remove notes。
///
/// 实现 ICollectionList / ICollectionCreate / ICollectionUpdate / ICollectionDelete。
///
/// 对标 Python examples/xiaohongshu/collection/ 下的各 API 示例。
/// 所有合集 API 在 edith.xiaohongshu.com 上。
///
/// API 端点:
///   List:       POST /api/sns/v1/note/collection/pc/list_v2
///   Create:     POST /api/sns/v1/note/collection/pc/create
///   Update:     POST /api/sns/v1/note/collection/pc/update
///   Delete:     POST /api/sns/v1/note/collection/pc/delete
///   Detail:     GET  /api/sns/v1/note/collection/pc/info?collectionId=...
///   AddNote:    POST /api/sns/v1/note/collection/pc/add_note
///   RemoveNote: POST /api/sns/v1/note/collection/pc/remove_note
/// </summary>
public sealed class XhsCollectionService :
    ICollectionList, ICollectionCreate, ICollectionUpdate, ICollectionDelete
{
    private readonly PlatformOptions _options;
    private readonly ILogger<XhsCollectionService>? _logger;

    public XhsCollectionService(PlatformOptions options, ILogger<XhsCollectionService>? logger = null)
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
        var cursorStr = cursor == 0 ? "" : cursor.ToString();

        // 小红书 list_v2 是 POST（不是 GET！）
        // need_type_list 必须带类型（0 = 普通合集）—— 空数组平台按「一个类型都不要」返回空列表，
        // 会让「先查后建」永远查不到已有合集（2026-09-12 实测：空数组 → 0 条，同一账号平台上确有合集）
        var body = new Dictionary<string, object?>
        {
            ["cursor"] = cursorStr,
            ["need_type_list"] = new List<int> { 0 },
            ["target_uid"] = "",
        };

        var resp = await client.PostEdithAsync<XhsCollectionListResponse>(
            "/api/sns/v1/note/collection/pc/list_v2", body, ct);

        var items = resp?.Data?.Collections?.Select(ToCollectionInfo).ToList() ?? [];
        var hasMore = resp?.Data?.HasMore ?? false;
        var nextCursor = hasMore ? cursor + count : 0;

        return new CollectionListResult(items, hasMore, nextCursor);
    }

    // ═══ ICollectionCreate ═══

    /// <inheritdoc />
    public async Task<CreateCollectionResult> CreateAsync(
        string cookie, CreateCollectionInfo request, CancellationToken ct = default)
    {
        using var client = CreateClient(cookie);

        var body = new Dictionary<string, object?>
        {
            ["name"] = request.Title,
            ["desc"] = request.Description ?? "",
            ["type"] = 0, // 0 = 普通合集, 3 = 播客合集
        };

        try
        {
            var resp = await client.PostEdithRawAsync(
                "/api/sns/v1/note/collection/pc/create", body, ct);

            // Response format: { data: { collection_id: "..." }, success: true }
            var collectionId = "";
            if (resp.TryGetProperty("data", out var data) &&
                (data.TryGetProperty("collection_id", out var cid) ||
                 data.TryGetProperty("id", out cid)))
                collectionId = cid.GetString() ?? "";

            // 拿到 collection_id 即算成功 —— success 字段缺失/为 false 但给回了 ID 时
            // （如平台把「重名已存在」按既有 ID 返回）仍应把 ID 收回，否则平台侧有合集、
            // 本端却记成「未同步」，用户看到的就是「明明建了却说没建」。
            var success = !string.IsNullOrEmpty(collectionId)
                || (resp.TryGetProperty("success", out var s) && s.GetBoolean());

            _logger?.LogInformation("XHS collection create: id={Id}, success={Ok}", collectionId, success);
            return new CreateCollectionResult(collectionId, success, success ? null : "Create failed");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "XHS collection create failed");
            return new CreateCollectionResult("", false, DescribeError(ex));
        }
    }

    /// <summary>错误文案：406 = 小红书反爬签名校验未通过（x-s 缺失/失效），单独说明便于用户处置。</summary>
    private static string DescribeError(Exception ex) =>
        ex.Message.Contains("HTTP 406", StringComparison.Ordinal)
            ? "小红书拒绝了请求（406 签名校验未通过）——请先打开该账号标签页、确认能正常进创作平台后重试"
            : ex.Message;

    // ═══ ICollectionUpdate ═══

    /// <inheritdoc />
    public async Task<UpdateCollectionResult> UpdateAsync(
        string cookie, UpdateCollectionInfo request, CancellationToken ct = default)
    {
        using var client = CreateClient(cookie);

        // 2026-11-05 抓包：普通合集编辑只传这三个字段（普通合集没有独立封面上传入口；
        // 旧实现的 image/collectionBackgroundColor 是多余的，去掉以免干扰平台参数校验）。
        var body = new Dictionary<string, object?>
        {
            ["collection_id"] = request.PlatformCollectionId,
            ["name"] = request.Title,
            ["desc"] = request.Description ?? "",
        };

        try
        {
            var resp = await client.PostEdithAsync<XhsCollectionMutationResponse>(
                "/api/sns/v1/note/collection/pc/update", body, ct);
            if (resp is null || !resp.IsOk)
            {
                var error = resp?.Msg is { Length: > 0 } m
                    ? m
                    : $"更新合集失败（code={resp?.Code}, result={resp?.Result}）";
                _logger?.LogWarning("XHS collection update rejected: id={Id}, error={Error}",
                    request.PlatformCollectionId, error);
                return new UpdateCollectionResult(false, error);
            }
            _logger?.LogInformation("XHS collection update: id={Id}", request.PlatformCollectionId);
            return new UpdateCollectionResult(true, null);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "XHS collection update failed: id={Id}", request.PlatformCollectionId);
            return new UpdateCollectionResult(false, DescribeError(ex));
        }
    }

    // ═══ ICollectionDelete ═══

    /// <inheritdoc />
    public async Task<DeleteCollectionResult> DeleteAsync(
        string cookie, DeleteCollectionInfo request, CancellationToken ct = default)
    {
        using var client = CreateClient(cookie);

        try
        {
            // 2026-11-05 抓包：delete 必须带**当前合集名**（名称不符 / 合集非空 → code:-1）。
            // DeleteCollectionInfo.Title 由调用方能给就给；给不了先查详情（比让平台回
            // 「required param check: name」再猜原因好）。
            var name = request.Title?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                var detail = await FetchDetailAsync(client, request.PlatformCollectionId, ct);
                name = detail?.Name?.Trim();
            }
            if (string.IsNullOrEmpty(name))
            {
                return new DeleteCollectionResult(false,
                    "未获取到合集名称，无法删除（小红书删除接口要求带当前合集名）");
            }

            var body = new Dictionary<string, object?>
            {
                ["collection_id"] = request.PlatformCollectionId,
                ["name"] = name,
            };

            var resp = await client.PostEdithAsync<XhsCollectionMutationResponse>(
                "/api/sns/v1/note/collection/pc/delete", body, ct);
            if (resp is null || !resp.IsOk)
            {
                var error = resp?.Msg is { Length: > 0 } m
                    ? m
                    : $"删除合集失败（code={resp?.Code}, result={resp?.Result}）";
                _logger?.LogWarning("XHS collection delete rejected: id={Id}, error={Error}",
                    request.PlatformCollectionId, error);
                return new DeleteCollectionResult(false, error);
            }

            _logger?.LogInformation("XHS collection delete: id={Id}, name={Name}",
                request.PlatformCollectionId, name);
            return new DeleteCollectionResult(true, null);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "XHS collection delete failed: id={Id}", request.PlatformCollectionId);
            return new DeleteCollectionResult(false, DescribeError(ex));
        }
    }

    // ═══ 额外方法 ═══

    /// <summary>获取合集详情。</summary>
    public async Task<XhsCollectionDetail?> DetailAsync(
        string cookie, string collectionId, CancellationToken ct = default)
    {
        using var client = CreateClient(cookie);

        try
        {
            return await FetchDetailAsync(client, collectionId, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "XHS collection detail failed: id={Id}", collectionId);
            return null;
        }
    }

    /// <summary>
    /// 详情请求（内部共用一个 client，删除前取 name 也走这里）。
    /// 查询参数是 **snake_case `collection_id`**（2026-11-05 抓包；camelCase 会走平台参数校验失败）。
    /// </summary>
    private static async Task<XhsCollectionDetail?> FetchDetailAsync(
        XhsApiClient client, string collectionId, CancellationToken ct)
    {
        var resp = await client.GetEdithAsync<XhsCollectionInfoResponse>(
            "/api/sns/v1/note/collection/pc/info",
            new Dictionary<string, string> { ["collection_id"] = collectionId }, ct);
        return resp?.Data;
    }

    /// <summary>添加笔记到合集。</summary>
    public async Task<bool> AddNoteAsync(
        string cookie, string collectionId, List<string> noteIds, CancellationToken ct = default)
    {
        using var client = CreateClient(cookie);

        var body = new Dictionary<string, object?>
        {
            ["collection_id"] = collectionId,
            ["note_ids"] = noteIds,
        };

        try
        {
            await client.PostEdithAsync<XhsNoteManagementResponse>(
                "/api/sns/v1/note/collection/pc/add_note", body, ct);
            _logger?.LogInformation("XHS add notes: collection={Cid}, notes={Notes}",
                collectionId, string.Join(",", noteIds));
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "XHS add note failed: collection={Cid}", collectionId);
            return false;
        }
    }

    /// <summary>从合集中移除笔记。</summary>
    public async Task<bool> RemoveNoteAsync(
        string cookie, string collectionId, List<string> noteIds, CancellationToken ct = default)
    {
        using var client = CreateClient(cookie);

        var body = new Dictionary<string, object?>
        {
            ["collection_id"] = collectionId,
            ["note_ids"] = noteIds,
        };

        try
        {
            await client.PostEdithAsync<XhsNoteManagementResponse>(
                "/api/sns/v1/note/collection/pc/remove_note", body, ct);
            _logger?.LogInformation("XHS remove notes: collection={Cid}, notes={Notes}",
                collectionId, string.Join(",", noteIds));
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "XHS remove note failed: collection={Cid}", collectionId);
            return false;
        }
    }

    // ═══ Private ═══

    private XhsApiClient CreateClient(string cookie) =>
        new(cookie, _options, _logger as ILogger<PlatformHttpClient>);

    private static CollectionInfo ToCollectionInfo(XhsCollectionItem m) => new(
        PlatformCollectionId: m.Id,
        Title: m.Name ?? "",
        Description: m.Desc,
        CoverUrl: m.Icon,
        Status: 0,
        StatusText: "正常",
        VideoCount: m.NoteNum,
        CreatedAt: FromMsTimestamp(m.CreateTime),
        UpdatedAt: null);

    private static DateTime? FromMsTimestamp(long ms)
    {
        if (ms <= 0) return null;
        try { return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime; }
        catch { return null; }
    }
}
