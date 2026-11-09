using CreatFlow.Platforms.Toutiao.Requests;

namespace CreatFlow.Platforms.Toutiao;

/// <summary>
/// 今日头条**文章合集**服务 — 列表 / 详情 / 创建 / 编辑 / 删除。
///
/// 头条有两套完全独立的合集系统（2026-11-05 抓包，见 docs/toutiao/20261105/合集接口.md）：
///   - 视频合集（专辑 pSeries）：/xigua/api/pSeries/*，见 <see cref="ToutiaoCollectionService"/>；
///   - 文章合集：/mp/fe_api/collection/*，本服务。
/// 两者的 ID、响应壳、封面桶、发布挂载字段都不同，**不能混用**。
///
/// 关键坑（实现必须照做）：
///   1. 新建可能 HTTP 500 但实际已创建 → 必须回查列表（标题 + 封面 uri）判定；
///   2. 编辑必须带 item_ids = 详情里的 all_sub_item_id_list，否则会清空合集内文章；
///   3. 删除遇到「修改审核中」会多出审核版本 → 回查列表，删到目标 ID 消失；
///   4. 封面是 /spice/image?upload_source=20020010 的 multipart 回执（见 ToutiaoImageUploadService）。
/// </summary>
public sealed class ToutiaoArticleCollectionService :
    ICollectionList, ICollectionCreate, ICollectionUpdate, ICollectionDelete
{
    private const string ManageReferer =
        "https://mp.toutiao.com/profile_v4/graphic/collections";
    private const string PublishReferer =
        "https://mp.toutiao.com/profile_v4/graphic/collection-publish?from=collection_publish";

    private const string StatusList = "40,20,25,15,10,7,9";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly PlatformOptions _options;
    private readonly ILogger<ToutiaoArticleCollectionService>? _logger;

    public ToutiaoArticleCollectionService(
        PlatformOptions options, ILogger<ToutiaoArticleCollectionService>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    // ═══ ICollectionList ═══

    /// <inheritdoc />
    public async Task<CollectionListResult> ListAsync(
        string cookie, long cursor = 0, int count = 12, CancellationToken ct = default)
    {
        var page = (int)Math.Max(0, cursor);
        using var client = new ToutiaoApiClient(cookie, _options, null, ManageReferer);

        var userId = await client.GetUserIdAsync(ct);
        if (string.IsNullOrWhiteSpace(userId))
        {
            _logger?.LogWarning("Toutiao article collection list: missing user_id");
            return new CollectionListResult([], false, page);
        }

        ToutiaoArticleCollectionListResponse? resp;
        try
        {
            resp = await FetchListAsync(client, userId, page, count, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Toutiao article collection list failed");
            return new CollectionListResult([], false, page);
        }

        if (resp is not { Code: 0 })
        {
            _logger?.LogWarning("Toutiao article collection list rejected: code={Code}, msg={Msg}",
                resp?.Code, resp?.Message);
            return new CollectionListResult([], false, page);
        }

        var items = (resp.Data ?? []).Select(ToCollectionInfo).ToList();
        var total = int.TryParse(resp.TotalCount, out var t) ? t : items.Count;
        var next = page + 1;
        return new CollectionListResult(items, next * count < total, next);
    }

    // ═══ ICollectionCreate ═══

    /// <inheritdoc />
    public async Task<CreateCollectionResult> CreateAsync(
        string cookie, CreateCollectionInfo request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.CsrfToken))
            return new CreateCollectionResult("", false,
                "缺少头条 CSRF 凭证：请打开头条账号窗口（加载完页面后点「完成登录」）刷新凭证后重试");
        if (TitleLengthError(request.Title) is { } titleError)
            return new CreateCollectionResult("", false, titleError);
        if (string.IsNullOrWhiteSpace(request.CoverTosKey))
            return new CreateCollectionResult("", false, "文章合集需要先上传封面图");

        using var client = new ToutiaoApiClient(cookie, _options, request.CsrfToken, PublishReferer);
        var userId = await client.GetUserIdAsync(ct);
        if (string.IsNullOrWhiteSpace(userId))
            return new CreateCollectionResult("", false, "缺少头条用户 ID，请重新登录头条账号");

        var width = request.CoverWidth > 0 ? request.CoverWidth : 1080;
        var height = request.CoverHeight > 0 ? request.CoverHeight : 1080;
        var body = new
        {
            title = request.Title,
            cover_image = new
            {
                url = request.CoverUrl ?? "",
                uri = request.CoverTosKey,
                width,
                height,
                thumbHeight = height,
                thumbWidth = width,
                originUri = request.CoverOriginUri ?? request.CoverTosKey,
                percent = 100,
                localUrl = "",
            },
            collection_id = "0",
            user_id = userId,
            item_ids = Array.Empty<string>(),
            is_recommended = false,
        };

        var (status, text) = await client.SendJsonAllowErrorAsync(
            _options.ToutiaoHost + "/mp/fe_api/collection/publish",
            body, useXiguaCsrf: false, ct);

        var parsed = TryParse<ToutiaoArticlePublishResponse>(text);
        if (parsed?.CollectionId is { Length: > 0 } && IsOk(parsed.Code, parsed.BaseResp))
        {
            _logger?.LogInformation("Toutiao article collection create OK: id={Id}",
                parsed.CollectionId);
            return new CreateCollectionResult(parsed.CollectionId, true, null);
        }

        // ⚠️ 抓包实测：新建会返回 HTTP 500 + {"message":""}，但合集实际已创建。
        // 不能只看状态码 —— 回查列表，按 (标题, 封面 uri) 精确匹配。
        var found = await FindCreatedAsync(client, userId, request.Title, request.CoverTosKey, ct);
        if (found?.Id is { Length: > 0 })
        {
            _logger?.LogInformation(
                "Toutiao article collection create verified after HTTP {Status}: id={Id}",
                status, found.Id);
            return new CreateCollectionResult(found.Id, true, null);
        }

        var error = parsed?.Message is { Length: > 0 } m
            ? m
            : $"创建文章合集失败（HTTP {status}）";
        _logger?.LogWarning(
            "Toutiao article collection create rejected: HTTP {Status}, body={Body}, error={Error}",
            status, Snippet(text), error);

        // 已经返回过「可能已创建」的账号，重试前先让用户核对，避免重复建。
        return new CreateCollectionResult("", false,
            error + "；若平台侧已出现同名合集，请勿重复创建，先刷新核对");
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

        using var client = new ToutiaoApiClient(cookie, _options, request.CsrfToken, PublishReferer);
        var userId = await client.GetUserIdAsync(ct);
        if (string.IsNullOrWhiteSpace(userId))
            return new UpdateCollectionResult(false, "缺少头条用户 ID，请重新登录头条账号");

        // 详情回填：item_ids 必须原样带上，否则平台会把合集内文章清空。
        var detail = await FetchDetailAsync(client, userId, request.PlatformCollectionId, ct);
        if (detail?.CollectionAttr is null)
            return new UpdateCollectionResult(false,
                "无法获取文章合集详情（编辑需要回填 item_ids，避免清空合集内文章），请稍后重试");

        // 详情必须带回 all_sub_item_id_list。字段缺失时宁可取消编辑，也不能提交 item_ids=[] ——
        // 那会把合集里的文章全部清空（抓包样本里空合集是 []，不是缺字段）。
        if (detail.AllSubItemIdList is null)
            return new UpdateCollectionResult(false,
                "无法读取合集内文章列表（all_sub_item_id_list 缺失），为避免清空文章已取消编辑，请稍后重试");

        object cover;
        if (!string.IsNullOrWhiteSpace(request.CoverTosKey))
        {
            var width = request.CoverWidth > 0 ? request.CoverWidth : 1080;
            var height = request.CoverHeight > 0 ? request.CoverHeight : 1080;
            cover = new
            {
                url = request.CoverUrl ?? "",
                uri = request.CoverTosKey,
                width,
                height,
                thumbHeight = height,
                thumbWidth = width,
                originUri = request.CoverOriginUri ?? request.CoverTosKey,
                percent = 100,
                localUrl = "",
            };
        }
        else
        {
            // 没换封面：详情里的封面原样回填（uri 才是关键，url 只是签名预览）
            var current = detail.CollectionAttr.CoverImage;
            if (current is null || string.IsNullOrWhiteSpace(current.Uri))
                return new UpdateCollectionResult(false,
                    "未获取到文章合集的当前封面，无法编辑（请重新选择封面图）");
            cover = new
            {
                url = current.Url ?? "",
                uri = current.Uri,
                width = current.Width,
                height = current.Height,
                mime_type = current.MimeType ?? "",
                percent = 100,
            };
        }

        var body = new
        {
            title = request.Title,
            cover_image = cover,
            collection_id = request.PlatformCollectionId,
            user_id = userId,
            item_ids = detail.AllSubItemIdList ?? [],
        };

        var (status, text) = await client.SendJsonAllowErrorAsync(
            _options.ToutiaoHost + "/mp/fe_api/collection/publish",
            body, useXiguaCsrf: false, ct);

        var parsed = TryParse<ToutiaoArticlePublishResponse>(text);
        if (parsed is not null && IsOk(parsed.Code, parsed.BaseResp)
            && (string.IsNullOrWhiteSpace(parsed.CollectionId)
                || parsed.CollectionId == request.PlatformCollectionId))
        {
            _logger?.LogInformation("Toutiao article collection update OK: id={Id}",
                request.PlatformCollectionId);
            return new UpdateCollectionResult(true, null);
        }

        var error = parsed?.Message is { Length: > 0 } m
            ? m
            : $"编辑文章合集失败（HTTP {status}）";
        _logger?.LogWarning(
            "Toutiao article collection update rejected: id={Id}, HTTP {Status}, body={Body}, error={Error}",
            request.PlatformCollectionId, status, Snippet(text), error);
        return new UpdateCollectionResult(false, error);
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
        var userId = await client.GetUserIdAsync(ct);
        if (string.IsNullOrWhiteSpace(userId))
            return new DeleteCollectionResult(false, "缺少头条用户 ID，请重新登录头条账号");

        // 删除可能先删掉「修改审核中」的版本，原合集仍在 —— 回查列表，删到目标 ID 消失。
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var (status, text) = await client.PostFormAllowErrorAsync(
                _options.ToutiaoHost + "/mp/fe_api/collection/delete",
                new Dictionary<string, string>
                {
                    ["user_id"] = userId,
                    ["item_id"] = request.PlatformCollectionId,
                }, ct);

            var parsed = TryParse<ToutiaoArticleDeleteResponse>(text);
            if (parsed is null || !IsOk(parsed.Code, parsed.BaseResp))
            {
                // 上一轮删成功后，本轮可能只是重复删到「已不存在」——回查一次再定性。
                if (await ContainsCollectionAsync(client, userId, request.PlatformCollectionId, ct) == false)
                    return new DeleteCollectionResult(true, null);

                var error = parsed?.Message is { Length: > 0 } m
                    ? m
                    : $"删除文章合集失败（HTTP {status}）";
                _logger?.LogWarning(
                    "Toutiao article collection delete rejected: id={Id}, HTTP {Status}, body={Body}, error={Error}",
                    request.PlatformCollectionId, status, Snippet(text), error);
                return new DeleteCollectionResult(false, error);
            }

            var stillThere = await ContainsCollectionAsync(
                client, userId, request.PlatformCollectionId, ct);
            if (stillThere == false)
            {
                _logger?.LogInformation(
                    "Toutiao article collection delete OK: id={Id} (attempt {Attempt})",
                    request.PlatformCollectionId, attempt);
                return new DeleteCollectionResult(true, null);
            }

            // true = 列表里还在（审核版本）；null = 列表查不到，无法确认但删除接口已成功。
            if (stillThere is null)
                return new DeleteCollectionResult(true, null);

            if (attempt < 3) await Task.Delay(800, ct);
        }

        return new DeleteCollectionResult(false,
            "平台返回删除成功但合集仍在（可能有修改审核版本），请稍后重试删除");
    }

    // ═══ HTTP helpers ═══

    private async Task<ToutiaoArticleCollectionListResponse?> FetchListAsync(
        ToutiaoApiClient client, string userId, int page, int count, CancellationToken ct)
    {
        var url = _options.ToutiaoHost + "/mp/fe_api/collection/list"
            + $"?user_id={Uri.EscapeDataString(userId)}"
            + $"&page_num={page}&page_size={Math.Clamp(count, 1, 50)}"
            + "&need_sub_item_count=true"
            + $"&item_status_list={StatusList}";
        return await client.GetJsonAsync<ToutiaoArticleCollectionListResponse>(url, ct);
    }

    private async Task<ToutiaoArticleCollectionDetailResponse?> FetchDetailAsync(
        ToutiaoApiClient client, string userId, string collectionId, CancellationToken ct)
    {
        var url = _options.ToutiaoHost + "/mp/fe_api/collection/edit"
            + $"?user_id={Uri.EscapeDataString(userId)}"
            + $"&collection_id={Uri.EscapeDataString(collectionId)}"
            + "&page_num=0&page_size=10";
        var resp = await client.GetJsonAsync<ToutiaoArticleCollectionDetailResponse>(url, ct);
        return resp is { Code: 0 } ? resp : null;
    }

    /// <summary>
    /// 新建回查：按 (标题, 封面 uri) 在列表里找刚创建的合集。
    /// 封面 uri 是本次上传的唯一 key，能区分同名合集；列表可能延迟，重试一次。
    /// </summary>
    private async Task<ToutiaoArticleCollectionItem?> FindCreatedAsync(
        ToutiaoApiClient client, string userId, string title, string coverUri,
        CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                var resp = await FetchListAsync(client, userId, 0, 20, ct);
                var hit = resp?.Data?.FirstOrDefault(i =>
                    string.Equals(i.CollectionAttr?.Title, title, StringComparison.Ordinal)
                    && string.Equals(i.CollectionAttr?.CoverImage?.Uri, coverUri,
                        StringComparison.Ordinal));
                if (hit != null) return hit;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Toutiao article collection create verify list failed");
            }

            if (attempt < 2) await Task.Delay(1200, ct);
        }
        return null;
    }

    /// <summary>
    /// 删除回查：true = 列表里还有；false = 已消失；null = 列表接口失败，无法确认。
    /// </summary>
    private async Task<bool?> ContainsCollectionAsync(
        ToutiaoApiClient client, string userId, string collectionId, CancellationToken ct)
    {
        try
        {
            var resp = await FetchListAsync(client, userId, 0, 50, ct);
            if (resp is not { Code: 0 }) return null;
            return resp.Data?.Any(i => i.Id == collectionId) == true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Toutiao article collection delete verify list failed");
            return null;
        }
    }

    private static bool IsOk(int code, ToutiaoBaseResp? baseResp) =>
        code == 0 && (baseResp is null || baseResp.StatusCode == 0);

    private static T? TryParse<T>(string text) where T : class
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonSerializer.Deserialize<T>(text, JsonOpts); }
        catch { return null; }
    }

    private static CollectionInfo ToCollectionInfo(ToutiaoArticleCollectionItem item)
    {
        var attr = item.CollectionAttr;
        var created = long.TryParse(attr?.CreateTime, out var unix) && unix > 0
            ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime
            : (DateTime?)null;

        return new CollectionInfo(
            PlatformCollectionId: item.Id ?? "",
            Title: attr?.Title ?? "",
            Description: null,
            CoverUrl: attr?.CoverImage?.Url ?? attr?.CoverImage?.Uri,
            Status: attr?.ItemStatus ?? 0,
            StatusText: item.ItemStatusDesc ?? StatusText(attr?.ItemStatus ?? 0),
            VideoCount: item.SubItemCount,
            CreatedAt: created,
            UpdatedAt: null,
            ContentType: 1);   // 文章合集：不能挂到头条视频发布
    }

    private static string StatusText(int status) => status switch
    {
        10 => "审核中",
        20 => "已发布",
        25 => "修改审核中",
        15 => "审核未通过",
        _ => "未知",
    };

    /// <summary>文章合集标题 5~30 字（与视频合集同款；平台侧也校验）。</summary>
    private static string? TitleLengthError(string? title)
    {
        var length = title?.Trim().Length ?? 0;
        return length is >= 5 and <= 30
            ? null
            : $"头条合集标题需 5~30 字（当前 {length} 字）";
    }

    private static string Snippet(string s) => s.Length <= 400 ? s : s[..400];
}
