namespace CreatFlow.Platforms.Douyin;

using CreatFlow.Platforms.Core;
using Serilog;
using System.Text.RegularExpressions;

/// <summary>
/// 抖音视频管理服务 — 发布、列表、删除、修改。
///
/// 对标 Python fangai/douyin/api.py 的 DouyinAPI 类。
///
/// API 端点：
///   Create: POST /web/api/media/aweme/create_v2/   (Cookie + msToken/a_bogus)
///   List:   GET  /janus/douyin/creator/pc/work_list (Cookie)
///   Delete: POST /web/api/media/aweme/delete/       (Cookie + form)
///   Update: POST /web/api/media/aweme/update_v2/    (Cookie + msToken/a_bogus)
/// </summary>
public sealed partial class DouyinVideoService
{
    private readonly PlatformOptions _options;
    private readonly ILogger<DouyinVideoService>? _logger;

    public DouyinVideoService(
        PlatformOptions options,
        ILogger<DouyinVideoService>? logger = null)
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

    private void LogWarn(Exception ex, string message, params object[] args)
    {
        if (_logger is not null) _logger.LogWarning(ex, message, args);
        else Log.Warning(ex, message, args);
    }

    // ═══ CreateWork — 发布视频 ═══

    /// <summary>发布视频作品（CookieJar 模式 — Set-Cookie 自动回写）。</summary>
    public async Task<(string? ItemId, string? Error)> CreateWorkAsync(
        CookieJar? cookieJar,
        CreateDouyinWorkRequest request,
        string? msToken = null,
        string? aBogus = null,
        string? csrfToken = null,
        BdTicketSigner? signer = null,
        CancellationToken ct = default)
    {
        using var client = BuildClient(cookieJar, csrfToken, signer);

        var path = "/web/api/media/aweme/create_v2/";
        var qs = PlatformHttpClient.BuildQueryString(
            new Dictionary<string, string> { ["read_aid"] = "2906" },
            msToken: msToken, aBogus: aBogus);
        var url = $"{_options.DouyinCreatorHost}{path}?{qs}";

        var item = BuildCreateItem(request);
        var wrap = new { item };

        LogDebug("CreateWork → {Url}, videoId={VideoId}", url, request.VideoId);

        try
        {
            var json = await client.PostJsonAsync(url, wrap, ct);

            // 空响应体 = 抖音 WAF 静默拒绝（BdTicketGuard 票据失效 / 风控限流），
            // 直接抛 JsonException 会显示"执行异常"，这里给友好错误。
            if (string.IsNullOrWhiteSpace(json))
            {
                LogWarn("CreateWork returned HTTP 200 with EMPTY body — 疑似票据失效或风控拦截");
                return (null, "抖音返回空响应（疑似票据失效或风控拦截），请重新登录抖音后重试");
            }

            var resp = JsonSerializer.Deserialize<DouyinCreateWorkResponse>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (resp?.StatusCode != 0)
            {
                // 带上平台的人话说明（status_msg）—— 只有状态码时用户在队列页看到的
                // 是一串「status_code=35」，完全没法判断下一步该做什么
                var err = $"status_code={resp?.StatusCode}"
                          + (string.IsNullOrWhiteSpace(resp?.StatusMsg) ? "" : $"，{resp!.StatusMsg}")
                          + $", item_id={resp?.ItemId}";
                LogWarn("CreateWork failed: {Err}", err);
                return (null, err);
            }

            LogInfo("CreateWork OK: item_id={Id}", resp!.ItemId);
            return (resp.ItemId, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // 用户暂停/删除选中：取消不是「发布失败」
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // 取消时底层可能抛 HttpRequestException（连接被掐断）而不是 OCE —— 统一改写成 OCE
            throw new OperationCanceledException("抖音发布已取消", ct);
        }
        catch (JsonException ex)
        {
            LogWarn(ex, "CreateWork response is not JSON");
            return (null, "抖音返回了非 JSON 响应（可能被风控拦截）");
        }
        catch (HttpRequestException ex)
        {
            // Bd-Ticket-Guard-Key-Sign-Result=2 = 服务器永久拒绝当前 ECDSA 密钥，
            // 重试无用，必须重新登录刷新 BdTicketGuard key。
            if (ex.Message.Contains("Key-Sign-Result=2", StringComparison.OrdinalIgnoreCase))
            {
                LogWarn("CreateWork 403: BdTicketGuard key REJECTED by server (Key-Sign-Result=2) — 需要重新登录刷新密钥");
                return (null, "抖音票据已被拒绝，请在平台管理里重新登录抖音后重试");
            }

            LogWarn(ex, "CreateWork HTTP error");
            return (null, ex.Message);
        }
    }

    /// <summary>发布视频作品（字符串 cookie — 向后兼容）。</summary>
    public async Task<(string? ItemId, string? Error)> CreateWorkAsync(
        string cookie,
        CreateDouyinWorkRequest request,
        string? msToken = null,
        string? aBogus = null,
        string? csrfToken = null,
        CancellationToken ct = default)
    {
        return await CreateWorkAsync(new CookieJar(cookie), request, msToken, aBogus, csrfToken, null, ct);
    }

    // ═══ UpdateWork — 修改视频 ═══

    /// <summary>
    /// 修改已发布的视频。
    ///
    /// 端点: POST /web/api/media/aweme/update_v2/
    /// </summary>
    /// <param name="cookie">平台 Cookie</param>
    /// <param name="itemId">要修改的视频 item_id</param>
    /// <param name="request">修改后的发布参数（不需要传 videoId）</param>
    /// <param name="msToken">安全 SDK msToken</param>
    /// <param name="aBogus">安全 SDK a_bogus</param>
    /// <param name="csrfToken">CSRF Token</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>成功返回 true</returns>
    public async Task<bool> UpdateWorkAsync(
        string cookie,
        string itemId,
        CreateDouyinWorkRequest request,
        string? msToken = null,
        string? aBogus = null,
        string? csrfToken = null,
        CancellationToken ct = default)
    {
        using var client = BuildClient(cookie, csrfToken);

        var path = "/web/api/media/aweme/update_v2/";
        var qs = PlatformHttpClient.BuildQueryString(
            new Dictionary<string, string> { ["read_aid"] = "2906" },
            msToken: msToken,
            aBogus: aBogus);
        var url = $"{_options.DouyinCreatorHost}{path}?{qs}";

        var item = BuildCreateItem(request);
        // update 需要指定 item_id
        item.Common.CreationId = itemId;

        var wrap = new { item };

        LogDebug("UpdateWork → {Url}, itemId={Id}", url, itemId);

        var json = await client.PostJsonAsync(url, wrap, ct);
        var resp = JsonSerializer.Deserialize<DouyinUpdateWorkResponse>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var ok = resp?.StatusCode == 0;
        if (!ok)
            LogWarn("UpdateWork failed: status_code={Code}", resp?.StatusCode);
        return ok;
    }

    // ═══ DeleteWork — 删除视频 ═══

    /// <summary>
    /// 删除视频作品。
    ///
    /// 端点: POST /web/api/media/aweme/delete/
    /// </summary>
    /// <param name="cookie">平台 Cookie</param>
    /// <param name="itemId">视频 item_id</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>删除结果（区分成功/未授权/不存在/失败）</returns>
    public async Task<DeleteWorkResult> DeleteWorkAsync(
        string cookie,
        string itemId,
        CancellationToken ct = default)
    {
        using var client = BuildClient(cookie, null);

        var path = "/web/api/media/aweme/delete/";
        var qs = PlatformHttpClient.BuildQueryString();
        var url = $"{_options.DouyinCreatorHost}{path}?{qs}";

        var form = new Dictionary<string, string> { ["item_id"] = itemId };

        LogDebug("DeleteWork → itemId={Id}", itemId);

        try
        {
            var json = await client.PostFormAsync(url, form, ct);
            var resp = JsonSerializer.Deserialize<DouyinDeleteWorkResponse>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (resp?.StatusCode == 0)
                return DeleteWorkResult.Success;

            LogWarn("DeleteWork failed: status_code={Code}", resp?.StatusCode);
            return DeleteWorkResult.Failed;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            LogInfo("DeleteWork: video not found on platform (404) for {Id}", itemId);
            return DeleteWorkResult.NotFound;
        }
        catch (HttpRequestException ex) when (
            ex.StatusCode == System.Net.HttpStatusCode.Unauthorized
            || ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            LogWarn("DeleteWork: unauthorized (401/403) for {Id}", itemId);
            return DeleteWorkResult.Unauthorized;
        }
        catch (HttpRequestException ex)
        {
            LogWarn(ex, "DeleteWork HTTP error for {Id}", itemId);
            return DeleteWorkResult.Failed;
        }
    }

    // ═══ ListWorks — 视频列表 ═══

    /// <summary>
    /// 获取视频作品列表。
    ///
    /// 端点: GET /janus/douyin/creator/pc/work_list
    /// </summary>
    /// <param name="cookie">平台 Cookie</param>
    /// <param name="status">筛选状态: 0=全部, 1=已发布, 2=草稿, 3=待审核</param>
    /// <param name="count">每页数量</param>
    /// <param name="maxCursor">分页游标（首次传 0）</param>
    /// <param name="ct">取消令牌</param>
    public async Task<DouyinListWorksResult?> ListWorksAsync(
        string cookie,
        int status = 0,
        int count = 12,
        long maxCursor = 0,
        CancellationToken ct = default)
    {
        using var client = BuildClient(cookie, null);

        var path = "/janus/douyin/creator/pc/work_list";
        var extra = new Dictionary<string, string>
        {
            ["status"] = status.ToString(),
            ["count"] = count.ToString(),
            ["max_cursor"] = maxCursor.ToString(),
            ["scene"] = "star_atlas",
            ["device_platform"] = "android",
        };
        var qs = PlatformHttpClient.BuildQueryString(extra);
        var url = $"{_options.DouyinCreatorHost}{path}?{qs}";

        LogDebug("ListWorks → {Url}", url);

        var resp = await client.GetJsonAsync<DouyinListWorksResponse>(url, ct);
        if (resp is null) return null;

        var works = new List<DouyinWorkInfo>();
        if (resp.AwemeList is not null)
        {
            foreach (var raw in resp.AwemeList)
            {
                works.Add(ParseWorkItem(raw));
            }
        }

        return new DouyinListWorksResult(
            works,
            resp.HasMore,
            resp.MaxCursor,
            resp.MinCursor,
            resp.Total);
    }

    // ═══ Private Helpers ═══

    /// <summary>构建 DouyinCreateItem — 对标 Python create_work item dict。</summary>
    public static DouyinCreateItem BuildCreateItem(CreateDouyinWorkRequest r)
    {
        var creationId = !string.IsNullOrWhiteSpace(r.CreationId)
            ? r.CreationId
            : Guid.NewGuid().ToString("N")[..24];

        var common = new DouyinCommon
        {
            // Browser pattern: item_title is pure title (no #hashtags), text/caption have them
            Caption = r.Description ?? r.Title ?? "",
            ItemTitle = StripHashtags(r.Title ?? ""),
            Text = (r.Title ?? "") + " " + (r.Description ?? r.Title ?? ""),
            VideoId = r.VideoId,
            VisibilityType = (int)(r.Visibility ?? DouyinVisibility.Public),
            Download = r.AllowDownload ? 1 : 0,
            Timing = r.ScheduledAt.HasValue
                ? (int)new DateTimeOffset(r.ScheduledAt.Value).ToUnixTimeSeconds() : 0,
            CreationId = creationId,
            // 2026-11-05 抓包：未选热点时 hashtag_source = "recommend"（旧默认三段式已过时）；
            // 选了热点词时页面用 "search/search/search"（旧版实现知识，实账号复核）
            HashtagSource = r.HashtagSource
                ?? (string.IsNullOrWhiteSpace(r.HotSentence) ? "recommend" : "search/search/search"),
            HotSentence = r.HotSentence,
            Activity = BuildActivityJson(r.ActivityIds),
            // music_source 的取值来自「选中音乐对象的 source」（页面 JS 抓取），本次未选音乐没抓到；
            // 先固定 0（与抓包一致），选了音乐是否被采纳待实账号探针（阶段 2.1 验收）
            MusicSource = 0,
            MusicId = string.IsNullOrWhiteSpace(r.MusicId) ? null : r.MusicId,
        };

        // @ 用户 / 互动贴纸 —— 页面直接 JSON.stringify 数组；空值保持默认 "[]"
        if (!string.IsNullOrWhiteSpace(r.Mentions)) common.Mentions = r.Mentions;
        if (!string.IsNullOrWhiteSpace(r.InteractionStickers))
            common.InteractionStickers = r.InteractionStickers;

        var item = new DouyinCreateItem { Common = common };

        // 话题标记 — 发布时由 BuildHashtagExtras 预计算好 text_extra/challenges 字符串，
        // 这里透传（对标 createMedia.py item.common.text_extra / challenges）。
        // WebView2 和 HTTP 兜底两条路径都走这里，避免兜底路径丢话题。
        if (!string.IsNullOrWhiteSpace(r.TextExtra))
            common.TextExtra = r.TextExtra;
        if (!string.IsNullOrWhiteSpace(r.Challenges))
            common.Challenges = r.Challenges;

        // POI 位置 — 在 anchor 里（对标浏览器 anchor: {poi_id, poi_name, anchor_content}）
        if (r.PoiInfo is { Count: > 0 })
        {
            item.Anchor["poi_id"] = r.PoiInfo.GetValueOrDefault("poi_id", "");
            item.Anchor["poi_name"] = r.PoiInfo.GetValueOrDefault("poi_name", "");
            item.Anchor["anchor_content"] = "{\"loc_from\":\"normal\",\"is_commerce_intention\":false}";
        }

        // 合集挂载
        // 2026-11-05 抓包：向空合集发布时 mix_order=0；合集内排序由用户指定
        // （老样例 mix_order=83 证明该字段有效；页面默认 0 = 按发布时间顺序）。
        if (!string.IsNullOrWhiteSpace(r.CollectionMixId))
        {
            item.Mix = new DouyinMixAttachment
            {
                MixId = r.CollectionMixId,
                MixOrder = r.MixOrder ?? 0,
            };
        }

        // 自主声明 — item.declare.user_declare_info (JSON 字符串)
        if (!string.IsNullOrWhiteSpace(r.DeclareInfo))
        {
            item.Declare = new DouyinDeclareInfo
            {
                UserDeclareInfo = r.DeclareInfo,
            };
        }

        // 封面图（已上传到 TOS 后获得的 URI）—— 只发 poster：2026-11-05 的 create_v2、
        // 2026-06 的 create_media_v2 抓包里 cover 都只有 poster，没有 uri
        if (!string.IsNullOrWhiteSpace(r.CoverUri))
        {
            item.Cover = new DouyinCoverInfo { Poster = r.CoverUri };
        }

        // 封面帧 / 自定义裁剪（2026-11-05 抓包）—— poster_delay 是**秒**，模型里是毫秒
        if (r.CoverFrameMs is { } frameMs)
            item.Cover.PosterDelay = (int)Math.Max(0, frameMs / 1000);
        if (r.CoverCropWidth is > 0) item.Cover.CustomCoverImageWidth = r.CoverCropWidth;
        if (r.CoverCropHeight is > 0) item.Cover.CustomCoverImageHeight = r.CoverCropHeight;
        if (!string.IsNullOrWhiteSpace(r.CoverToolsInfo))
            item.Cover.CoverToolsInfo = r.CoverToolsInfo;
        if (!string.IsNullOrWhiteSpace(r.CoverToolsExtendInfo))
            item.Cover.CoverToolsExtendInfo = r.CoverToolsExtendInfo;

        // 章节 —— 结构来自 2026-11-05 页面 JS：
        //   chapter_details: [{desc, timestamp(毫秒)}]，chapter_type: 有章节 ? 1 : 0
        // 没有章节时保留默认空章节模板（type=0），与旧行为一致
        if (r.Chapters is { Count: > 0 })
            item.Chapter = new DouyinChapterInfo { Chapter = BuildChapterJson(r.Chapters) };

        // 同步到头条/西瓜 —— 抓包只有「未开」的形态（should_sync=false / sync_to_toutiao=0）；
        // 开启时两个字段一起置真（页面 JS 的 should_sync=1 / sync_to_toutiao=1）
        if (r.SyncTargets?.Any(t => string.Equals(t, "toutiao", StringComparison.OrdinalIgnoreCase)) == true)
        {
            item.Sync = new DouyinSyncInfo { ShouldSync = true, SyncToToutiao = 1 };
        }

        return item;
    }

    /// <summary>
    /// 官方活动 —— <c>common.activity</c> 是 **JSON 字符串**（内容为 ID 数组）。
    /// ID 是纯数字时按数字下发（页面样例 <c>"[7702034615116174390]"</c>），否则按字符串保留。
    /// </summary>
    internal static string BuildActivityJson(IEnumerable<string>? ids)
    {
        var values = new List<object>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in ids ?? [])
        {
            var id = raw?.Trim();
            if (string.IsNullOrEmpty(id) || !seen.Add(id)) continue;
            values.Add(long.TryParse(id, out var num) ? num : id);
        }
        return JsonSerializer.Serialize(values);
    }

    /// <summary>
    /// 章节 JSON（<c>item.chapter.chapter</c> 是一个字符串）—— 结构取自 2026-11-05 页面 JS：
    /// <c>{chapter_abstract, chapter_details:[{desc,timestamp(ms)}], chapter_type:1, chapter_tools_info}</c>。
    /// </summary>
    internal static string BuildChapterJson(IReadOnlyList<DouyinChapter> chapters)
    {
        // 同一时间点只能有一个章节（页面编辑器本来就不允许重复；载荷侧再兜一次）
        var seen = new HashSet<long>();
        var details = chapters
            .OrderBy(c => c.StartMs)
            .Where(c => seen.Add(Math.Max(0, c.StartMs)))
            .Select(c => new Dictionary<string, object?>
            {
                ["desc"] = c.Title ?? "",
                ["timestamp"] = Math.Max(0, c.StartMs),
            })
            .ToList();

        var payload = new Dictionary<string, object?>
        {
            ["chapter_abstract"] = "",
            ["chapter_details"] = details,
            ["chapter_type"] = details.Count > 0 ? 1 : 0,
            ["chapter_tools_info"] = new Dictionary<string, object?>
            {
                ["chapter_recommend_detail"] = Array.Empty<object>(),
                ["chapter_recommend_abstract"] = "",
                ["chapter_source"] = 2,
                ["chapter_recommend_type"] = -2,
                ["create_date"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["is_pc"] = "1",
                ["is_pre_generated"] = "0",
                ["is_syn"] = "1",
            },
        };
        return JsonSerializer.Serialize(payload);
    }

    /// <summary>
    /// 从文本中提取 #话题 并构建 text_extra + challenges JSON。
    /// 对标 Python createMedia.py 的 text_extra 和 challenges 字段。
    ///
    /// text_extra: [{"start":12,"end":16,"type":1,"hashtag_name":"水族馆","hashtag_id":"...",...}]
    /// challenges: [1580970656910349,...]
    /// </summary>
    /// <param name="text">完整文本（item.common.text）</param>
    /// <param name="caption">简介文本（item.common.caption）</param>
    /// <param name="searchResults">challengesug API 返回的话题列表（需按名称匹配）</param>
    /// <returns>(textExtraJson, challengesJson)</returns>
    public static (string TextExtra, string Challenges) BuildHashtagExtras(
        string text, string caption, List<DouyinHashtagResult> searchResults)
    {
        var extras = new List<HashtagExtraEntry>();
        // 对标 createMedia.py: challenges 是数字数组（cid 数字），不是字符串数组
        var challenges = new List<long>();
        var matchedIds = new HashSet<long>();

        // Find all #xxx in text and caption using regex
        var hashtagRegex = HashtagRegex();
        var textMatches = hashtagRegex.Matches(text);
        var captionMatches = hashtagRegex.Matches(caption);

        foreach (Match match in textMatches)
        {
            var name = match.Groups[1].Value;             // "水族馆" (without #)
            var start = match.Index;                       // position of #
            var end = match.Index + match.Length;          // exclusive end

            // Find matching search result by name (case-insensitive)
            var result = searchResults.FirstOrDefault(
                r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
            if (result == null) continue;
            if (!long.TryParse(result.Cid, out var cid)) continue;  // cid 必须能转数字
            if (!matchedIds.Add(cid)) continue;           // dedup

            // Find this hashtag's position in caption
            var captionPos = FindHashtagInText(caption, name, matchedIds.Count == 1
                ? -1 : extras.LastOrDefault()?.CaptionEnd ?? -1);

            extras.Add(new HashtagExtraEntry
            {
                Start = start,
                End = end,
                Type = 1,
                HashtagName = name,
                HashtagId = cid,
                CaptionStart = captionPos.Start,
                CaptionEnd = captionPos.End,
            });

            challenges.Add(cid);
        }

        foreach (Match match in captionMatches)
        {
            var name = match.Groups[1].Value;
            var start = match.Index;
            var end = match.Index + match.Length;

            var result = searchResults.FirstOrDefault(
                r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
            if (result == null) continue;
            if (!long.TryParse(result.Cid, out var cid)) continue;
            if (!matchedIds.Add(cid)) continue;

            extras.Add(new HashtagExtraEntry
            {
                Start = start,
                End = end,
                Type = 1,
                HashtagName = name,
                HashtagId = cid,
                UserId = "",
                CaptionStart = start,
                CaptionEnd = end,
            });

            challenges.Add(cid);
        }

        var textExtraJson = JsonSerializer.Serialize(extras,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var challengesJson = JsonSerializer.Serialize(challenges);

        return (textExtraJson, challengesJson);
    }

    /// <summary>从文本中移除所有 #话题，返回纯文本标题。</summary>
    private static string StripHashtags(string s)
    {
        return HashtagRegex().Replace(s, "").Trim();
    }

    /// <summary>Find a #hashtag position in text, optionally after a minimum position.</summary>
    private static (int Start, int End) FindHashtagInText(string text, string name, int afterPos)
    {
        var searchStart = afterPos >= 0 ? afterPos : 0;
        if (searchStart >= text.Length) return (0, 0);

        var idx = text.IndexOf($"#{name}", searchStart, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) idx = text.IndexOf($"#{name}", 0, StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? (idx, idx + name.Length + 1) : (0, 0);
    }

    /// <summary>Match #xxx hashtag — Chinese/English/digits only after #.</summary>
    /// <summary>
    /// `#话题` 的切法 —— **唯一的模式**。发出去之前往正文里追加话题的那一端
    /// （NewApp 的 PublishPayloadBuilder.ComposeCaption）也用这一个；
    /// 两边不一致 = 追加的名字与这里的偏移量对不上 = 抖音拒收（status_code=35）。
    /// </summary>
    public const string HashtagPattern = @"#([\w一-鿿㐀-䶿]+)";

    [System.Text.RegularExpressions.GeneratedRegex(HashtagPattern,
        System.Text.RegularExpressions.RegexOptions.Compiled)]
    private static partial System.Text.RegularExpressions.Regex HashtagRegex();

    /// <summary>从列表 API 的 JsonElement 解析 WorkItem。</summary>
    private static DouyinWorkInfo ParseWorkItem(JsonElement raw)
    {
        var awemeId = raw.TryGetProperty("aweme_id", out var aid) ? aid.GetString() ?? "" : "";
        var desc = raw.TryGetProperty("desc", out var d) ? d.GetString() ?? "" : "";
        var caption = raw.TryGetProperty("caption", out var c) ? c.GetString() ?? "" : "";

        string? coverUrl = null;
        if (raw.TryGetProperty("Cover", out var cover) && cover.ValueKind == JsonValueKind.Object)
        {
            if (cover.TryGetProperty("url_list", out var urls) && urls.ValueKind == JsonValueKind.Array)
            {
                var first = urls.EnumerateArray().FirstOrDefault();
                coverUrl = first.ValueKind != JsonValueKind.Null ? first.GetString() : null;
            }
        }

        long createTime = 0;
        if (raw.TryGetProperty("create_time", out var ct) && ct.ValueKind == JsonValueKind.Number)
            createTime = ct.GetInt64();

        int status = 0;
        if (raw.TryGetProperty("status_value", out var sv) && sv.ValueKind == JsonValueKind.Number)
            status = sv.GetInt32();

        int duration = 0;
        if (raw.TryGetProperty("video", out var video) && video.ValueKind == JsonValueKind.Object)
        {
            if (video.TryGetProperty("duration", out var dur) && dur.ValueKind == JsonValueKind.Number)
                duration = (int)(dur.GetDouble() * 1000);  // 秒转毫秒
        }

        string ratio = "";
        if (raw.TryGetProperty("video", out var v2) && v2.ValueKind == JsonValueKind.Object)
        {
            if (v2.TryGetProperty("ratio", out var r) && r.ValueKind == JsonValueKind.String)
                ratio = r.GetString() ?? "";
        }

        var stats = new Dictionary<string, int>();
        if (raw.TryGetProperty("statistics", out var s) && s.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in s.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Number)
                    stats[prop.Name] = prop.Value.GetInt32();
            }
        }

        return new DouyinWorkInfo(awemeId, desc, caption, coverUrl, createTime, status, duration, ratio, stats);
    }

    /// <summary>CookieJar 模式 — Set-Cookie 自动回写，BdTicketGuard 读最新 cookie。</summary>
    private PlatformHttpClient BuildClient(CookieJar? jar, string? csrfToken = null,
        BdTicketSigner? signer = null)
    {
        var loggingHandler = new Core.Handlers.LoggingHandler(null);
        var cookieHandler = jar != null
            ? new Core.Handlers.CookieAuthHandler(jar)
            : new Core.Handlers.CookieAuthHandler("");
        var bdTicketGuardHandler = new Handlers.BdTicketGuardHandler(
            () => jar?.GetCookieString() ?? "", signer, jar);
        var secSdkHandler = new Handlers.SecSdkHandler();

        DelegatingHandler head;
        if (!string.IsNullOrWhiteSpace(csrfToken))
        {
            var csrfHandler = new Core.Handlers.CsrfTokenHandler(csrfToken);
            csrfHandler.InnerHandler = secSdkHandler;
            secSdkHandler.InnerHandler = bdTicketGuardHandler;
            bdTicketGuardHandler.InnerHandler = cookieHandler;
            cookieHandler.InnerHandler = loggingHandler;
            head = csrfHandler;
        }
        else
        {
            secSdkHandler.InnerHandler = bdTicketGuardHandler;
            bdTicketGuardHandler.InnerHandler = cookieHandler;
            cookieHandler.InnerHandler = loggingHandler;
            head = secSdkHandler;
        }

        return new PlatformHttpClient(head, _options, null);
    }

    /// <summary>向后兼容：从 cookie 字符串构建。</summary>
    private PlatformHttpClient BuildClient(string cookie, string? csrfToken = null)
    {
        return BuildClient(new CookieJar(cookie), csrfToken);
    }
}

// ═══ Public Request/Response Types ═══

/// <summary>
/// 发布视频的请求参数（平台无关子集，DouyinVideoService 映射到 DouyinCreateItem）。
///
/// 对标 Python upload_publish_douyin.py 中 create_work 的 item 结构。
/// </summary>
public sealed record CreateDouyinWorkRequest(
    /// <summary>视频 ID（必填）— 来自 DouyinUploadService.UploadAsync() 的返回</summary>
    string VideoId,

    /// <summary>视频标题</summary>
    string? Title = null,

    /// <summary>视频简介</summary>
    string? Description = null,

    /// <summary>谁可以看（默认公开）</summary>
    DouyinVisibility? Visibility = null,

    /// <summary>是否允许下载</summary>
    bool AllowDownload = true,

    /// <summary>定时发布时间（null = 立即发布）</summary>
    DateTime? ScheduledAt = null,

    /// <summary>合集 mix_id（挂载到已有合集）</summary>
    string? CollectionMixId = null,

    /// <summary>合集内排序序号（默认 0 = 按发布时间顺序；2026-11-05 抓包）</summary>
    int? MixOrder = null,

    /// <summary>自主声明 — user_declare_info JSON 字符串</summary>
    string? DeclareInfo = null,

    /// <summary>位置 POI 信息 — key: poi_id, poi_name, address, lat, lng</summary>
    Dictionary<string, string>? PoiInfo = null,

    /// <summary>热点来源 — "recommend/recommend/recommend" 或 "search/search/search"</summary>
    string? HashtagSource = null,

    /// <summary>选中热点词（hashtag_source=search/search/search 时）</summary>
    string? HotSentence = null,

    /// <summary>封面图 TOS URI（上传后获得，null = 抖音自动选帧）</summary>
    string? CoverUri = null,

    /// <summary>客户端生成的唯一 ID（用于去重/幂等，null 时自动生成）</summary>
    string? CreationId = null,

    /// <summary>话题 text_extra JSON 字符串（含 hashtag_id 数字定位信息，由 BuildHashtagExtras 预计算）</summary>
    string? TextExtra = null,

    /// <summary>话题 challenges JSON 字符串（cid 数字数组）</summary>
    string? Challenges = null,

    // ── 2026-11-06 补齐（阶段 2.1）：1.3 抖音表里的用户可编辑长尾 ──

    /// <summary>背景音乐 ID — <c>common.music_id</c>（null = 不选音乐）</summary>
    string? MusicId = null,

    /// <summary>章节 — <c>item.chapter.chapter</c>（毫秒 + 标题；empty = 默认空章节）</summary>
    List<DouyinChapter>? Chapters = null,

    /// <summary>官方活动 ID — <c>common.activity</c>（下发为 JSON 字符串数组）</summary>
    List<string>? ActivityIds = null,

    /// <summary>@ 用户（原始 JSON 数组串）— <c>common.mentions</c></summary>
    string? Mentions = null,

    /// <summary>互动贴纸（原始 JSON 数组串）— <c>common.interaction_stickers</c></summary>
    string? InteractionStickers = null,

    /// <summary>同步目标（平台键）— 目前只认 "toutiao"（同步头条/西瓜）</summary>
    List<string>? SyncTargets = null,

    /// <summary>封面帧（毫秒）— 下发时换算成 <c>cover.poster_delay</c> 的**秒**</summary>
    long? CoverFrameMs = null,

    /// <summary>自定义封面裁剪宽（px）— <c>cover.custom_cover_image_width</c></summary>
    int? CoverCropWidth = null,

    /// <summary>自定义封面裁剪高（px）— <c>cover.custom_cover_image_height</c></summary>
    int? CoverCropHeight = null,

    /// <summary>封面工具信息（原始 JSON 串）— <c>cover.cover_tools_info</c></summary>
    string? CoverToolsInfo = null,

    /// <summary>封面工具扩展信息（原始 JSON 串）— <c>cover.cover_tools_extend_info</c></summary>
    string? CoverToolsExtendInfo = null
);

/// <summary>
/// 抖音章节（平台无关的小记录）—— <see cref="CreateDouyinWorkRequest.Chapters"/> 的输入。
/// 不直接引用 NewApp 的 <c>PlatformChapter</c>：Platforms 层不依赖桌面端模型。
/// </summary>
public sealed record DouyinChapter(long StartMs, string Title);

/// <summary>
/// 抖音可见范围枚举（映射到 visibility_type）。
///
/// 值来源: createMedia.py item.common.visibility_type
///   0=公开, 1=仅自己可见, 2=好友可见
///   注意: 1 和 2 的值和直觉相反，这是抖音 API 的真值。
/// </summary>
public enum DouyinVisibility
{
    /// <summary>公开 — visibility_type=0</summary>
    Public = 0,

    /// <summary>仅自己可见 — visibility_type=1</summary>
    Private = 1,

    /// <summary>好友可见 — visibility_type=2</summary>
    Friends = 2,
}

/// <summary>
/// 删除视频的返回结果。
///
/// 区分不同失败原因，让调用方决定后续行为：
/// - Success: 平台已删除，可以安全删除 DB 记录
/// - NotFound: 视频在平台上已不存在（用户手动删了），DB 记录是脏数据
/// - Unauthorized: Cookie 过期，需要重新登录，DB 记录保留
/// - Failed: 其他错误（网络、平台限流等），DB 记录保留
/// </summary>
public enum DeleteWorkResult
{
    /// <summary>平台侧删除成功</summary>
    Success,

    /// <summary>视频在平台上已不存在（404）</summary>
    NotFound,

    /// <summary>未授权 — Cookie 过期（401/403）</summary>
    Unauthorized,

    /// <summary>其他错误</summary>
    Failed,
}
