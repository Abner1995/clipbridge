using CreatFlow.Platforms.Kuaishou.Requests;

namespace CreatFlow.Platforms.Kuaishou;

/// <summary>
/// 快手视频管理服务 — 列表、删除、编辑。
/// 对标 Python examples/kuaishou/media/ 下的各 API 示例。
/// </summary>
public sealed class KuaishouVideoService
{
    private readonly PlatformOptions _options;
    private readonly ILogger<KuaishouVideoService>? _logger;

    /// <summary>submit 后视频进列表的等待参数。</summary>
    private const int ListRetryDelayMs = 3000;
    private const int ListRetryMaxAttempts = 5;

    public KuaishouVideoService(PlatformOptions options, ILogger<KuaishouVideoService>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    // ═══ 列表 ═══

    /// <summary>获取视频作品列表。POST /rest/cp/works/v2/video/pc/photo/list (sig3)。</summary>
    public async Task<KuaishouVideoListResult> ListAsync(
        string cookie, long? cursor = null, int limit = 30,
        long? startTime = null, long? endTime = null, string? keyword = null,
        string? apiPh = null, string queryType = "0", CancellationToken ct = default)
    {
        using var client = new KuaishouApiClient(cookie, _options, apiPh,
            logger: _logger as ILogger<PlatformHttpClient>);

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var oneDayMs = 24L * 3600 * 1000;
        var body = new
        {
            queryType,
            cursor = cursor ?? nowMs + 180 * oneDayMs,
            startTime = startTime ?? nowMs - 180 * oneDayMs,
            endTime = endTime ?? nowMs + 180 * oneDayMs,
            limit,
            timeRangeType = 5,
            keyword = keyword ?? "",
        };
        var data = await client.PostAsync<KuaishouVideoListResponse>(
            "/rest/cp/works/v2/video/pc/photo/list", body, ct);

        return new KuaishouVideoListResult(
            Items: data?.List ?? [],
            Total: data?.Total ?? 0,
            NextCursor: data?.NextCursor);
    }

    // ═══ 按封面 Key 查找（提交后回查 ID） ═══

    /// <summary>
    /// 通过封面 Key / 标题在作品列表中查找视频条目。
    ///
    /// submit API 返回空 data，不返回任何 ID，只能回查列表。
    /// **匹配锚点有两路**（2026-11-09 抓包核对后改）：
    ///   ① 封面 Key（<c>unPublishCoverKey</c>）—— 只对待发布作品有值；
    ///   ② 标题 —— **已发布作品的 <c>unPublishCoverKey</c> 回读恒为 null**
    ///      （真实列表三条全是 null），标题是唯一稳定锚点。
    /// 另外 keyword 检索对「仅自己」作品命中不了（全量列表里有、拿标题当 keyword 却回 0 条），
    /// 所以找不到时会再拉一次**全量列表**按标题兜底。
    ///
    /// 提交后视频可能还没进列表索引，带重试（最多 5 次，间隔递增）。
    /// API 错误（109 / 500002 = 登录凭证过期）也会重试。
    /// </summary>
    /// <param name="cookie">Cookie</param>
    /// <param name="coverKey">封面 key（来自上传 finish）</param>
    /// <param name="title">视频标题（keyword + 精确匹配；可为空）</param>
    /// <param name="apiPh">API PH token</param>
    /// <param name="ct">取消令牌</param>
    /// <param name="maxAttempts">最多查几次（删除兜底只要一次：作品早就发出去了，不必等索引）</param>
    /// <returns>匹配的条目，找不到返回 null</returns>
    public async Task<KuaishouVideoItem?> FindByCoverKeyAsync(
        string cookie, string coverKey, string? title = null,
        string? apiPh = null, CancellationToken ct = default,
        int maxAttempts = ListRetryMaxAttempts)
    {
        maxAttempts = Math.Max(1, maxAttempts);
        var keyword = TitleKeyword(title);
        var triedUnfiltered = false;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (attempt > 0)
            {
                var delay = ListRetryDelayMs + (attempt - 1) * 2000;
                _logger?.LogDebug("Kuaishou FindByCoverKey: retry {Attempt}/{Max} after {Delay}ms",
                    attempt + 1, maxAttempts, delay);
                await Task.Delay(delay, ct);
            }

            try
            {
                // 前 N-1 次用 keyword 缩小范围，最后一次用空 keyword 拉全部。
                // maxAttempts=1（删除兜底）时直接用调用方给的 keyword —— 不能因为
                // 「最后一次」就丢掉标题，账号作品超过一页时那会搜不到
                var searchKeyword = attempt == maxAttempts - 1 && maxAttempts > 1 ? "" : keyword;
                if (searchKeyword.Length == 0) triedUnfiltered = true;

                var list = await ListAsync(cookie,
                    keyword: searchKeyword,
                    limit: 30, apiPh: apiPh, ct: ct);

                var match = MatchListItem(list.Items, coverKey, title);

                if (match is not null)
                {
                    _logger?.LogDebug("Kuaishou FindByCoverKey: found after {Attempt} attempts, workId={WorkId}, publishId={PublishId}",
                        attempt + 1, match.WorkId, match.PublishId);
                    return match;
                }

                _logger?.LogDebug("Kuaishou FindByCoverKey: attempt {Attempt}/{Max} — not in list (total={Total}, keyword={Kw})",
                    attempt + 1, maxAttempts, list.Total,
                    string.IsNullOrEmpty(searchKeyword) ? "(empty)" : searchKeyword);
            }
            catch (KuaishouApiException ex)
            {
                // 109 / 500002 = 登录凭证过期（2026-11-09 实测：同参数同签名，换新同步的 Cookie 即成功）
                _logger?.LogWarning("Kuaishou FindByCoverKey: attempt {Attempt}/{Max} API error (retrying): {Code} {Msg} {Hint}",
                    attempt + 1, maxAttempts, ex.ResultCode, ex.Message, CredentialHint(ex.ResultCode) ?? "");
                Serilog.Log.Warning(
                    "[KsDelete] 列表请求失败: result={Code} msg={Msg} title={Title}",
                    ex.ResultCode, ex.Message, title);
                if (attempt == maxAttempts - 1)
                    break; // 最后一次不抛 —— 交给下面的全量列表兜底（keyword 对「仅自己」无效）
            }
        }

        // 关键词检索对「仅自己」作品无效（2026-11-09 实测）→ 全量列表按标题再兜一次。
        if (keyword.Length > 0 && !triedUnfiltered)
        {
            try
            {
                var list = await ListAsync(cookie, keyword: "", limit: 30, apiPh: apiPh, ct: ct);
                var match = MatchListItem(list.Items, coverKey, title);
                if (match is not null)
                {
                    _logger?.LogInformation(
                        "Kuaishou FindByCoverKey: found via unfiltered list, workId={WorkId}, publishId={PublishId}",
                        match.WorkId, match.PublishId);
                    return match;
                }
            }
            catch (KuaishouApiException ex)
            {
                _logger?.LogWarning("Kuaishou FindByCoverKey: unfiltered lookup failed: {Code} {Msg} {Hint}",
                    ex.ResultCode, ex.Message, CredentialHint(ex.ResultCode) ?? "");
                Serilog.Log.Warning(
                    "[KsDelete] 全量列表请求失败: result={Code} msg={Msg}", ex.ResultCode, ex.Message);
            }
        }

        return null;
    }

    /// <summary>
    /// 列表行匹配（纯函数，自检直接断言）：先按封面 Key，再按标题 —— 同名多条取最新上传。
    ///
    /// 标题匹配是必需的：**已发布作品的 <c>unPublishCoverKey</c> 回读恒为 null**
    /// （2026-11-09 真实列表抓包，三条作品全是 null），只按 Key 匹配永远找不到。
    /// </summary>
    public static KuaishouVideoItem? MatchListItem(
        IEnumerable<KuaishouVideoItem> items, string? coverKey, string? title)
    {
        var byCover = items.FirstOrDefault(v =>
            !string.IsNullOrWhiteSpace(coverKey)
            && string.Equals(v.UnPublishCoverKey, coverKey, StringComparison.Ordinal));
        if (byCover is not null) return byCover;

        var want = NormalizeTitleForMatch(title);
        if (want.Length == 0) return null;

        // ① 归一化后全等（列表 title 可能带正文/话题）
        var exact = items
            .Where(v => NormalizeTitleForMatch(v.Title).Equals(want, StringComparison.Ordinal))
            .OrderByDescending(v => v.UploadTime)
            .FirstOrDefault();
        if (exact is not null) return exact;

        // ② 前缀匹配：归档里通常只有标题，列表 title 是「标题\n正文 #话题」；
        //    也可能归档标题带话题、列表 title 被截断 —— 两边都试一次。
        return items
            .Where(v =>
            {
                var candidate = NormalizeTitleForMatch(v.Title);
                return candidate.Length > 0
                    && (candidate.StartsWith(want, StringComparison.Ordinal)
                        || want.StartsWith(candidate, StringComparison.Ordinal));
            })
            .OrderByDescending(v => v.UploadTime)
            .FirstOrDefault();
    }

    /// <summary>
    /// 标题归一化（列表匹配用）：取第一行 + 去掉 #话题 + 去首尾空白。
    /// 快手列表返回的 <c>title</c> 是整段 caption（标题 + 正文 + 话题），
    /// 归档里只有标题，直接全等永远匹配不上（2026-11-09 实测）。
    /// 纯函数，自检直接断言。
    /// </summary>
    public static string NormalizeTitleForMatch(string? title)
    {
        var t = (title ?? "").Trim();
        if (t.Length == 0) return "";

        var newline = t.IndexOfAny(['\r', '\n']);
        if (newline >= 0) t = t[..newline].Trim();

        var hash = t.IndexOf('#');
        if (hash >= 0) t = t[..hash].TrimEnd();

        return t.Trim();
    }

    /// <summary>
    /// 列表 keyword：去掉「#话题」部分（平台检索不认标签，2026-11-09 实测）。
    /// 纯函数，自检直接断言。
    /// </summary>
    public static string TitleKeyword(string? title)
    {
        var keyword = (title ?? "").Trim();
        var hashIdx = keyword.IndexOf('#');
        if (hashIdx > 0) keyword = keyword[..hashIdx].TrimEnd();
        return keyword;
    }

    /// <summary>
    /// 平台 result 码 → 用户可执行的提示（不认识返回 null）。
    ///
    /// 109 / 500002 在 2026-11-09 实测都是**登录凭证问题**，与参数/签名无关：
    /// 去掉 Cookie 里的 <c>kuaishou.web.cp.api_st</c> → 109；
    /// <c>api_st</c> 过期（上次同步的凭证太旧）→ 500002「请稍后重试」。
    /// 同一份请求换成「重新同步的 Cookie」立刻 result=1。
    /// </summary>
    public static string? CredentialHint(int resultCode) => resultCode switch
    {
        109 or 500002 =>
            "快手登录凭证可能已过期：打开「平台账号」页刷新快手账号（重新登录一次）后再试",
        _ => null,
    };

    // ═══ 删除（已发布 — workId） ═══

    /// <summary>
    /// 删除已发布的视频作品（老式 workId 端点）。
    /// POST /rest/cp/works/v2/video/pc/delete (sig3)，参数 photoId=workId。
    ///
    /// ⚠️ 只认 workId（如 "3xfu3f4smpejnfy"）。归档库里更常见的是 **publishId**
    /// （提交后回查列表时 workId 还是 null）—— 那个走 <see cref="DeletePublishedAsync"/>，
    /// 别拿 publishId/mediaId 来调这里（平台回 10001「网络不畅」）。
    /// </summary>
    /// <param name="cookie">Cookie</param>
    /// <param name="workId">作品 workId（字符串，如 "3xfu3f4smpejnfy"）</param>
    /// <param name="apiPh">API PH token</param>
    /// <param name="ct">取消令牌</param>
    public async Task<bool> DeleteAsync(string cookie, string workId,
        string? apiPh = null, CancellationToken ct = default)
    {
        using var client = new KuaishouApiClient(cookie, _options, apiPh,
            logger: _logger as ILogger<PlatformHttpClient>);
        // delete 返回 data:null，不能用 PostAsync（会抛 null data in response）
        var resp = await client.PostRawAsync<KuaishouSubmitResponse>(
            "/rest/cp/works/v2/video/pc/delete", new { photoId = workId }, ct);
        if (resp.Result != 1)
            throw new KuaishouApiException(
                $"Kuaishou delete: result={resp.Result}, msg={resp.Message ?? "?"}", resp.Result);
        return true;
    }

    /// <summary>
    /// 删除已发布（含定时待发布）的视频作品 —— 创作者中心「作品管理」页的删除接口。
    /// POST /rest/cp/works/v2/video/pc/publish/delete (sig3)，参数 id=publishId。
    ///
    /// 2026-06-16 抓包验证 result=1（docs/kuaishou/接口/20260616/delete.json）。
    /// ⚠️ 2026-11-09 实测该端点已返回 **404**（平台下线）；当前网页版作品管理页对
    /// 数字 publishId 改用 `unpublished/delete`（见 <see cref="DeleteByPublishIdAsync"/>）。
    /// 本方法保留作旧平台兜底，不再作为首选。
    /// </summary>
    /// <param name="cookie">Cookie</param>
    /// <param name="publishId">作品 publishId（列表返回的整数）</param>
    /// <param name="apiPh">API PH token</param>
    /// <param name="ct">取消令牌</param>
    public async Task<bool> DeletePublishedAsync(string cookie, long publishId,
        string? apiPh = null, CancellationToken ct = default)
    {
        using var client = new KuaishouApiClient(cookie, _options, apiPh,
            logger: _logger as ILogger<PlatformHttpClient>);
        var resp = await client.PostRawAsync<KuaishouSubmitResponse>(
            "/rest/cp/works/v2/video/pc/publish/delete", new { id = publishId }, ct);
        if (resp.Result != 1)
            throw new KuaishouApiException(
                $"Kuaishou delete published: result={resp.Result}, msg={resp.Message ?? "?"}", resp.Result);
        return true;
    }

    /// <summary>
    /// 按归档库里保存的作品 ID 删除一条视频 —— **端点分流 + 兜底**，视频管理页与
    /// 异步删除任务共用这一份（不要再在调用方按「纯数字」自己分流）。
    ///
    /// 分流（移植创作者中心作品管理页的删除分支，2026-11-09 按当前网页版校正）：
    ///   ① 非纯数字 = workId → `/video/pc/delete { photoId }`
    ///   ② 纯数字 = publishId → `/video/pc/unpublished/delete { id }`（当前网页版正路）
    ///   ③ ② 被拒 → `/video/pc/publish/delete { id }`（旧端点，2026-11-09 起 404）
    ///   ④ 都失败且给了封面 Key → 查作品列表按 unPublishCoverKey 精确匹配，
    ///      拿匹配项的 workId/publishId 重来一遍
    ///
    /// 为什么需要 ④：早期发布时列表回查失败，归档里存的是 `mediaId`
    /// （26 位数字，long 都装不下），两条删除端点都不认它；封面 Key 才是稳定锚点。
    /// </summary>
    /// <param name="cookie">Cookie</param>
    /// <param name="videoId">归档里保存的平台作品 ID（publishId / workId / mediaId 三种都可能）</param>
    /// <param name="coverKey">封面 Key（extra_data_json 的 ks_cover_key，可为空）</param>
    /// <param name="title">作品标题（仅用于缩小列表 keyword，可为空）</param>
    /// <param name="apiPh">API PH token</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>false 只在「拿封面 Key 列表里也找不到这条」时 —— 平台侧确实没有</returns>
    public async Task<bool> DeleteSmartAsync(string cookie, string videoId, string? coverKey = null,
        string? title = null, string? apiPh = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(videoId))
            return false;

        if (!KuaishouDeleteRoutes.IsPublishId(videoId))
        {
            // ① workId（非纯数字）→ /video/pc/delete { photoId }
            //    失败也可能是「存的是 mediaId」—— 当封面 Key 存在时继续走 ④ 回查
            //    （mediaId 是 26 位，long 装不下，会被分到这一支，平台回 10001「网络不畅」）
            try
            {
                return await DeleteAsync(cookie, videoId, apiPh, ct);
            }
            catch (Exception ex) when ((ex is KuaishouApiException or HttpRequestException)
                                       && (!string.IsNullOrWhiteSpace(coverKey)
                                           || !string.IsNullOrWhiteSpace(title)))
            {
                _logger?.LogInformation(
                    "Kuaishou delete: /pc/delete rejected ({Msg}), resolving by coverKey/title", ex.Message);
            }
        }
        else
        {
            var publishId = long.Parse(videoId);

            // ② 数字 publishId：当前网页版的正路是 /unpublished/delete { id }
            //    （2026-11-09 实测：旧 /publish/delete 已 404；网页版作品管理页
            //      onClickDelete 对已发布作品也是调 unpublished/delete）
            try
            {
                return await DeleteByPublishIdAsync(cookie, publishId, apiPh, ct);
            }
            catch (Exception ex) when ((ex is KuaishouApiException or HttpRequestException)
                                       && (!string.IsNullOrWhiteSpace(coverKey)
                                           || !string.IsNullOrWhiteSpace(title)))
            {
                _logger?.LogInformation(
                    "Kuaishou delete: publishId endpoints rejected ({Msg}), resolving by coverKey/title", ex.Message);
            }
        }

        // ④ 两条端点都不认这个 ID（早期 mediaId 记录 / 作品 ID 变了）→ 用封面 Key / 标题回查真实 ID。
        // maxAttempts: 1 —— 删除是事后操作，作品早已进了列表索引，不需要发布后的重试等待
        Serilog.Log.Information(
            "[KsDelete] 端点都拒绝，改查列表: id={Id} coverKey={Cover} title={Title}",
            videoId, coverKey, title);
        var item = await FindByCoverKeyAsync(cookie, coverKey!, title, apiPh, ct, maxAttempts: 1);
        if (item is null)
        {
            _logger?.LogWarning("Kuaishou delete: coverKey={CoverKey} not found in video list", coverKey);
            Serilog.Log.Warning("[KsDelete] 列表里也没找到: id={Id} title={Title}", videoId, title);
            return false;
        }

        Serilog.Log.Information("[KsDelete] 列表命中: workId={WorkId} publishId={PublishId}",
            item.WorkId, item.PublishId);
        if (!string.IsNullOrWhiteSpace(item.WorkId))
            return await DeleteAsync(cookie, item.WorkId, apiPh, ct);
        if (item.PublishId > 0)
            return await DeleteByPublishIdAsync(cookie, item.PublishId, apiPh, ct);
        return false;
    }

    /// <summary>
    /// 删除待发布（定时）的视频作品。
    /// POST /rest/cp/works/v2/video/pc/unpublished/delete (sig3)，参数 id=publishId。
    /// </summary>
    /// <param name="cookie">Cookie</param>
    /// <param name="publishId">待发布作品 publishId（整数）</param>
    /// <param name="apiPh">API PH token</param>
    /// <param name="ct">取消令牌</param>
    public async Task<bool> DeleteUnpublishedAsync(string cookie, long publishId,
        string? apiPh = null, CancellationToken ct = default)
    {
        using var client = new KuaishouApiClient(cookie, _options, apiPh,
            logger: _logger as ILogger<PlatformHttpClient>);
        var resp = await client.PostRawAsync<KuaishouSubmitResponse>(
            "/rest/cp/works/v2/video/pc/unpublished/delete", new { id = publishId }, ct);
        if (resp.Result != 1)
            throw new KuaishouApiException(
                $"Kuaishou delete unpublished: result={resp.Result}, msg={resp.Message ?? "?"}", resp.Result);
        return true;
    }

    /// <summary>
    /// 数字 publishId 删除 —— 按当前网页版的分支顺序：
    ///   ① <c>/rest/cp/works/v2/video/pc/unpublished/delete { id }</c>
    ///      （2026-11-09 实测：网页版作品管理页 onClickDelete 对已发布作品也走这条）
    ///   ② <c>/rest/cp/works/v2/video/pc/publish/delete { id }</c>
    ///      （旧端点，2026-11-09 起返回 404，保留作旧平台兜底）
    /// </summary>
    private async Task<bool> DeleteByPublishIdAsync(string cookie, long publishId,
        string? apiPh, CancellationToken ct)
    {
        try
        {
            return await DeleteUnpublishedAsync(cookie, publishId, apiPh, ct);
        }
        catch (Exception first) when (first is KuaishouApiException or HttpRequestException)
        {
            _logger?.LogInformation(
                "Kuaishou delete: unpublished/delete rejected ({Msg}), trying legacy publish/delete",
                first.Message);
            Serilog.Log.Information("[KsDelete] unpublished/delete 被拒: {Msg}", first.Message);
        }

        return await DeletePublishedAsync(cookie, publishId, apiPh, ct);
    }

    /// <summary>
    /// 通过标题+封面Key查找并删除视频。
    ///
    /// 查到条目后交给 <see cref="DeleteSmartAsync"/>（端点分流 + 待发布兜底都在那里），
    /// 有 workId 用 workId，否则用 publishId。
    /// </summary>
    /// <param name="cookie">Cookie</param>
    /// <param name="title">视频标题（用作 keyword）</param>
    /// <param name="coverKey">封面 key（精确匹配）</param>
    /// <param name="apiPh">API PH token</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>true 删除成功，false 未找到匹配视频</returns>
    public async Task<bool> DeleteByMatchAsync(
        string cookie, string title, string coverKey,
        string? apiPh = null, CancellationToken ct = default)
    {
        var item = await FindByCoverKeyAsync(cookie, coverKey, title, apiPh, ct);
        if (item is null)
        {
            _logger?.LogWarning("Kuaishou DeleteByMatch: no video found for coverKey={CoverKey}", coverKey);
            return false;
        }

        var videoId = !string.IsNullOrWhiteSpace(item.WorkId)
            ? item.WorkId!
            : item.PublishId > 0 ? item.PublishId.ToString() : "";
        if (videoId.Length == 0)
        {
            _logger?.LogWarning("Kuaishou DeleteByMatch: video found but has no workId or publishId");
            return false;
        }

        _logger?.LogInformation("Kuaishou DeleteByMatch: deleting video {VideoId}", videoId);
        return await DeleteSmartAsync(cookie, videoId, coverKey, title, apiPh, ct);
    }

    // ═══ 置顶 ═══

    /// <summary>置顶视频。POST .../edit/top (sig3, type=4)。</summary>
    public async Task<bool> TopAsync(string cookie, string workId, CancellationToken ct = default)
    {
        using var client = new KuaishouApiClient(cookie, _options,
            logger: _logger as ILogger<PlatformHttpClient>);
        await client.PostAsync<object>(
            "/rest/cp/works/v2/video/pc/edit/top", new { workId, type = 4 }, ct);
        return true;
    }

    /// <summary>取消置顶。POST .../edit/top (sig3, type=5)。</summary>
    public async Task<bool> UntopAsync(string cookie, string workId, CancellationToken ct = default)
    {
        using var client = new KuaishouApiClient(cookie, _options,
            logger: _logger as ILogger<PlatformHttpClient>);
        await client.PostAsync<object>(
            "/rest/cp/works/v2/video/pc/edit/top", new { workId, type = 5 }, ct);
        return true;
    }

    /// <summary>立即发布已定时的作品。POST .../publish (sig3)。</summary>
    public async Task<bool> PublishNowAsync(string cookie, long publishId, CancellationToken ct = default)
    {
        using var client = new KuaishouApiClient(cookie, _options,
            logger: _logger as ILogger<PlatformHttpClient>);
        await client.PostAsync<object>(
            "/rest/cp/works/v2/video/pc/publish", new { id = publishId }, ct);
        return true;
    }
}

// ═══ 公共结果类型 ═══

public sealed record KuaishouVideoListResult(
    List<KuaishouVideoItem> Items, int Total, string? NextCursor);

/// <summary>
/// 归档里的快手作品 ID 分流（纯函数，自检直接断言）。
///
/// 判据只有「能不能被 long 装下」：10 位 publishId 可以，workId（"3xfu3f…"）不行。
/// ⚠️ 26 位 mediaId 也**装不下** → 会先走 workId 端点、被拒后再靠封面 Key 回查
/// （见 <see cref="KuaishouVideoService.DeleteSmartAsync"/> 第 ④ 步）。
/// </summary>
public static class KuaishouDeleteRoutes
{
    public static bool IsPublishId(string? videoId) => long.TryParse(videoId, out _);
}
