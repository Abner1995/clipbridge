using Serilog;

using CreatFlow.Platforms.Bilibili;

namespace CreatFlow.Platforms.Topics;

/// <summary>
/// B站话题 / 标签 / 官方活动（2026-11-04 抓包：<c>docs/bilibili/20261104/capture-2026-11-04T09-50-44.jsonl</c>）。
///
/// 端点（都是 member 域 GET，Cookie 认证，无 WBI）：
///   · 参与话题 <c>/x/vupre/web/topic/type/v2?pn=0&amp;ps=6&amp;title=&amp;track_id=&amp;upload_id=&amp;zip_url=
///     &amp;platform=pc&amp;type_pid={human_type2}&amp;t={ms}&amp;x-bili-locale-json=…&amp;x-bili-device-req-json=…</c>
///     → <c>data.topics[{topic_id,topic_name,topic_description,mission_id,activity_text,
///     activity_description,arc_play_vv,show_activity_icon}]</c>
///   · 标签推荐 <c>/x/vupre/web/tag/recommend?upload_id=&amp;subtype_id={tid}&amp;title=&amp;filename=
///     &amp;description=&amp;cover_url=&amp;t={ms}&amp;…</c> → <c>data[].tag</c>
///
/// **话题必须实时拉**（用户要求「B站话题必须从 API 获取、不能默认」）：
///   · <c>mission_id &gt; 0</c> 的话题 = 同时参加官方活动 → 本服务把它们按 mission 聚成
///     <see cref="PlatformActivity"/>（B站没有独立的活动列表接口）；
///   · <c>upload_id</c>：2026-11-06 真账号探针实测 **传 0 也能按标题推荐**（返回 20 条带
///     mission_id 的家居话题）—— 所以编辑阶段就能拉，不用等上传；真实 upload_id 只是页面语境。
///     **标签推荐不同**：<c>tag/recommend</c> 传 0 回 HTTP 404，必须等真实 upload_id（上传后）。
///   · 不拿默认话题充数：接口失败就报失败，UI 退回手动输入。
///
/// **B站标签 ≠ 话题**：标签走 <c>add_v3.tag</c>（逗号分隔，1~10 个），单独由
/// <see cref="ITagSuggestService"/> 提供；两者在 UI 上是两个块。
/// </summary>
public sealed class BilibiliTopicApi : ITopicSuggestService, IActivityListService, ITagSuggestService
{
    public static readonly TopicCapability StaticCapability = new(
        SupportsSearch: false,
        SupportsRecommend: true,
        SupportsActivities: true,
        // 话题推荐传 upload_id=0 实测可用（2026-11-06 真账号探针）—— 编辑阶段即可拉
        RecommendNeedsUpload: false);

    private const string TopicPath = "/x/vupre/web/topic/type/v2";
    private const string TagRecommendPath = "/x/vupre/web/tag/recommend";

    /// <summary>抓包 Referer（视频投稿页的封面/话题面板）</summary>
    private const string Referer = "https://member.bilibili.com/platform/upload/video/frame";

    private readonly PlatformOptions _options;

    public BilibiliTopicApi(PlatformOptions options) => _options = options;

    public string PlatformKey => PlatformNames.Bilibili;

    public TopicCapability Capability => StaticCapability;

    public bool Supported => true;

    // ═══ 话题 ═══

    /// <summary>B站没有关键词搜索（页面就是按标题/分区推荐）—— 明确返回原因，不假装能搜。</summary>
    public Task<TopicFetchResult> SearchAsync(
        PlatformCredentials credentials, TopicQuery query, CancellationToken ct = default)
        => Task.FromResult(TopicFetchResult.Fail(
            "B站话题按标题和分区推荐，不支持关键词搜索 —— 请点「获取平台推荐」"));

    public async Task<TopicFetchResult> RecommendAsync(
        PlatformCredentials credentials, TopicQuery query, CancellationToken ct = default)
    {
        // upload_id 缺省用 "0"：2026-11-06 真账号探针实测可按标题正常推荐
        //（页面语境里的 upload_id 只用于草稿关联，服务端不据此拒绝）
        try
        {
            var url = BuildTopicUrl(query);
            using var client = new BilibiliApiClient(credentials.Cookie, _options);
            var json = await client.GetStringAsync(url, Referer, ct);

            var (topics, code, message) = ParseTopics(json);
            if (code != 0)
            {
                var hint = code == -101 ? "（B站登录已过期，请打开 B站账号窗口重新登录）" : "";
                return TopicFetchResult.Fail($"B站推荐话题失败：code={code}，{message}{hint}");
            }
            return new TopicFetchResult(topics);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "[BilibiliTopic] 推荐失败 upload_id={UploadId}", query.UploadId);
            return TopicFetchResult.Fail(Describe(ex, "B站推荐话题"));
        }
    }

    /// <summary>标签推荐（<c>add_v3.tag</c> 的候选）。</summary>
    public async Task<TopicFetchResult> SuggestTagsAsync(
        PlatformCredentials credentials, TopicQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query.UploadId))
        {
            return TopicFetchResult.Fail(
                "视频上传完成后才能获取 B站 推荐标签（标签接口依赖 upload_id）");
        }

        try
        {
            var url = BuildTagUrl(query);
            using var client = new BilibiliApiClient(credentials.Cookie, _options);
            var json = await client.GetStringAsync(url, Referer, ct);

            var (tags, code, message) = ParseTags(json);
            if (code != 0)
            {
                var hint = code == -101 ? "（B站登录已过期，请打开 B站账号窗口重新登录）" : "";
                return TopicFetchResult.Fail($"B站推荐标签失败：code={code}，{message}{hint}");
            }
            return new TopicFetchResult(tags);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "[BilibiliTopic] 标签推荐失败 upload_id={UploadId}", query.UploadId);
            return TopicFetchResult.Fail(Describe(ex, "B站推荐标签"));
        }
    }

    // ═══ 官方活动（从话题的 mission_id 聚合） ═══

    public async Task<ActivityFetchResult> ListAsync(
        PlatformCredentials credentials, ActivityQuery query, CancellationToken ct = default)
    {
        // B站活动随话题接口返回；按调用方给的标题/upload_id 拉一次话题再按 mission 聚合。
        var topics = await RecommendAsync(credentials, new TopicQuery
        {
            Title = query.Title,
            UploadId = query.UploadId,
            Count = Math.Clamp(query.Count, 1, 50),
        }, ct);
        if (!topics.Ok) return ActivityFetchResult.Fail(topics.Error ?? "B站活动列表不可用");
        return new ActivityFetchResult(GroupActivities(topics.Items));
    }

    /// <summary>B站活动没有分类接口（话题推荐本身就是筛选后的）→ 空表。</summary>
    public Task<IReadOnlyList<ActivityCategory>> GetCategoriesAsync(
        PlatformCredentials credentials, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ActivityCategory>>([]);

    /// <summary>
    /// 把带 <c>mission_id</c> 的话题聚成活动（同一 mission 的多个话题 = 一个活动的多个赛道）。
    /// 活动名优先取 <c>activity_text</c>；本次样本它为空 → 退回该组第一个话题名。
    /// </summary>
    public static IReadOnlyList<PlatformActivity> GroupActivities(
        IReadOnlyList<TopicSuggestion> topics)
    {
        return [.. topics
            .Where(t => t.MissionId > 0)
            .GroupBy(t => t.MissionId)
            .Select(g => new PlatformActivity
            {
                Id = g.Key.ToString(),
                Name = g.Select(t => t.ActivityText)
                            .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))
                        ?? g.First().Name,
                HotScore = g.Max(t => t.ViewCount),
                Topics = [.. g],
                ExtraJson = JsonSerializer.Serialize(new
                {
                    mission_id = g.Key,
                    topic_ids = g.Select(t => t.PlatformId).ToArray(),
                }),
            })];
    }

    // ═══ URL 组装 ═══

    private string BuildTopicUrl(TopicQuery query)
    {
        var ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // upload_id 缺省 "0"（探针实测可按标题推荐；空串也试过，服务端按缺失处理）
        var uploadId = string.IsNullOrWhiteSpace(query.UploadId) ? "0" : query.UploadId;
        return $"{_options.BilibiliHost}{TopicPath}"
               + "?pn=0"
               + $"&ps={Math.Clamp(query.Count, 1, 50)}"
               + $"&title={Uri.EscapeDataString(query.Title)}"
               + $"&track_id={Uri.EscapeDataString(query.TrackId ?? "")}"
               + $"&upload_id={Uri.EscapeDataString(uploadId)}"
               + "&zip_url="
               + "&platform=pc"
               + $"&type_pid={query.BilibiliTypePid}"
               + $"&t={ms}"
               + $"&x-bili-locale-json={BilibiliUploadService.AxiosEscape(BilibiliUploadService.LocaleJson)}"
               + $"&x-bili-device-req-json={BilibiliUploadService.AxiosEscape(BilibiliUploadService.DeviceJson)}";
    }

    private string BuildTagUrl(TopicQuery query)
    {
        var ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return $"{_options.BilibiliHost}{TagRecommendPath}"
               + $"upload_id={Uri.EscapeDataString(query.UploadId ?? "")}"
               + $"&subtype_id={query.BilibiliTid}"
               + $"&title={Uri.EscapeDataString(query.Title)}"
               + $"&filename={Uri.EscapeDataString(query.FileName ?? "")}"
               + $"&description={Uri.EscapeDataString(query.Description)}"
               + $"&cover_url={Uri.EscapeDataString(query.CoverUrl ?? "")}"
               + $"&t={ms}"
               + $"&x-bili-locale-json={BilibiliUploadService.AxiosEscape(BilibiliUploadService.LocaleJson)}"
               + $"&x-bili-device-req-json={BilibiliUploadService.AxiosEscape(BilibiliUploadService.DeviceJson)}";
    }

    // ═══ 解析（公开 static 供自检钉字段映射） ═══

    public static (IReadOnlyList<TopicSuggestion> Items, int Code, string Message) ParseTopics(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var code = Int(root, "code");
        var message = Str(root, "message") ?? "";
        if (code != 0) return ([], code, message);

        if (!root.TryGetProperty("data", out var data)
            || !data.TryGetProperty("topics", out var arr)
            || arr.ValueKind != JsonValueKind.Array) return ([], 0, "OK");

        var list = new List<TopicSuggestion>();
        foreach (var t in arr.EnumerateArray())
        {
            var id = Long(t, "topic_id");
            var name = Str(t, "topic_name");
            if (id <= 0 || string.IsNullOrWhiteSpace(name)) continue;

            list.Add(new TopicSuggestion
            {
                Name = name,
                PlatformId = id.ToString(),
                ViewCount = Long(t, "arc_play_vv"),
                MissionId = Long(t, "mission_id"),
                ActivityText = Str(t, "activity_text"),
                Description = Str(t, "topic_description"),
                ExtraJson = JsonSerializer.Serialize(new
                {
                    activity_description = Str(t, "activity_description"),
                    show_activity_icon = t.TryGetProperty("show_activity_icon", out var icon)
                        && icon.ValueKind == JsonValueKind.True,
                }),
            });
        }
        return (list, 0, message);
    }

    public static (IReadOnlyList<TopicSuggestion> Items, int Code, string Message) ParseTags(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var code = Int(root, "code");
        var message = Str(root, "message") ?? "";
        if (code != 0) return ([], code, message);

        if (!root.TryGetProperty("data", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return ([], 0, "OK");

        var list = new List<TopicSuggestion>();
        foreach (var t in arr.EnumerateArray())
        {
            var name = Str(t, "tag");
            if (!string.IsNullOrWhiteSpace(name))
                list.Add(new TopicSuggestion { Name = name });
        }
        return (list, 0, message);
    }

    // ═══ 小工具 ═══

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static int Int(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt32(out var i) ? i : 0;

    private static long Long(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt64(out var l) ? l : 0;

    private static string Describe(Exception ex, string what) =>
        ex is TaskCanceledException or OperationCanceledException
            ? $"{what}超时（网络或签名服务无响应）"
            : $"{what}失败：{ex.Message}";
}
