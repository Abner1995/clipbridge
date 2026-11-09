namespace CreatFlow.Platforms.Xiaohongshu;

/// <summary>
/// 小红书话题推荐服务。
///
/// 对标 Python examples/xiaohongshu/tag/topics.py。
/// POST /api/galaxy/v2/creator/recommend/suggest/topics (on creator host)
///
/// 用户在输入标题+描述后调用此 API 获取推荐话题标签，
/// 返回的每个话题包含 id、name、type，用于构建 hash_tag 列表。
/// </summary>
public sealed class XhsTopicService
{
    private readonly PlatformOptions _options;
    private readonly ILogger<XhsTopicService>? _logger;

    public XhsTopicService(PlatformOptions options, ILogger<XhsTopicService>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// 按关键词**搜索**话题（网页版话题下拉用的就是这个接口，不是 recommend/suggest）。
    ///
    /// 对标 creator 网页版的 hashtag 下拉（project-publish-vue 里 <c>_.eD({keyword,
    /// suggestTopicRequest, page})</c>）：
    ///   POST /web_api/sns/v1/search/topic (edith host)
    ///   body = { keyword, suggestTopicRequest: { title, desc }, page: { pageSize, page } }
    /// 返回的 <c>topicInfoDtos</c> 里 id/name 就是发布时 hashtag 需要的 topic id/name。
    ///
    /// 为什么要它：<see cref="SuggestAsync"/> 是**按标题+正文推荐**，跟关键词无关
    /// （2026-10-22 用户实测：「狗狗冷知识」被推成「#亲近大自然」）。精确匹配只能走搜索。
    /// </summary>
    /// <param name="cookie">Cookie</param>
    /// <param name="keyword">关键词（不带 #）</param>
    /// <param name="title">当前标题（搜索接口的上下文，可空）</param>
    /// <param name="desc">当前正文（同上，可空）</param>
    /// <param name="pageSize">一页取多少条</param>
    /// <param name="ct">取消令牌</param>
    public async Task<List<XhsTopicItem>> SearchAsync(
        string cookie,
        string keyword,
        string title = "",
        string desc = "",
        int pageSize = 20,
        CancellationToken ct = default)
    {
        using var client = new XhsApiClient(cookie, _options,
            _logger as ILogger<PlatformHttpClient>);

        try
        {
            var body = new Dictionary<string, object?>
            {
                ["keyword"] = keyword,
                ["suggestTopicRequest"] = new Dictionary<string, object?>
                {
                    ["title"] = title,
                    ["desc"] = desc,
                },
                ["page"] = new Dictionary<string, object?>
                {
                    ["pageSize"] = pageSize,
                },
            };

            var resp = await client.PostEdithRawAsync("/web_api/sns/v1/search/topic", body, ct);

            var respText = resp.GetRawText();
            Serilog.Log.Debug("[XhsTopic] search raw len={Len} json={Json}",
                respText.Length, respText.Length > 2000 ? respText[..2000] : respText);

            var topics = new List<XhsTopicItem>();

            // 字段名两种写法都认：网页版 transform 之后读的是 topicInfoDtos，
            // 裸接口常见 topic_info_dtos；data 包一层也兼容（老接口形状差异）
            if (TryGetTopicArray(resp, out var arr))
            {
                foreach (var t in arr.EnumerateArray())
                {
                    var id = TryGetString(t, "id") ?? "";
                    var name = TryGetString(t, "name") ?? "";
                    if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                        topics.Add(new XhsTopicItem(id, name));
                }
            }

            Serilog.Log.Information("XHS topic search: kw={Kw}, found={Count}", keyword, topics.Count);
            return topics;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "XHS topic search failed: kw={Kw}", keyword);
            return [];
        }
    }

    /// <summary>话题数组可能挂在根上，也可能在 data 下；键名可能是 camelCase 或 snake_case</summary>
    private static bool TryGetTopicArray(JsonElement resp, out JsonElement arr)
    {
        arr = default;
        foreach (var name in new[] { "topicInfoDtos", "topic_info_dtos" })
        {
            if (resp.TryGetProperty(name, out arr) && arr.ValueKind == JsonValueKind.Array) return true;
        }
        if (resp.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "topicInfoDtos", "topic_info_dtos" })
            {
                if (data.TryGetProperty(name, out arr) && arr.ValueKind == JsonValueKind.Array)
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 根据标题和描述推荐话题标签。
    /// </summary>
    /// <param name="cookie">Cookie</param>
    /// <param name="title">视频标题</param>
    /// <param name="desc">视频描述</param>
    /// <param name="fileIds">封面图 fileIds（可选，用于内容识别）</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>推荐话题列表</returns>
    public async Task<List<XhsTopicItem>> SuggestAsync(
        string cookie,
        string title,
        string desc,
        List<string>? fileIds = null,
        CancellationToken ct = default)
    {
        using var client = new XhsApiClient(cookie, _options,
            _logger as ILogger<PlatformHttpClient>);

        try
        {
            var body = new Dictionary<string, object?>
            {
                ["title"] = title,
                ["desc"] = desc,
                ["file_ids"] = fileIds ?? new List<string>(),
                ["topic_round_start_time"] = 0,
            };

            var resp = await client.PostCreatorRawAsync(
                "/api/galaxy/v2/creator/recommend/suggest/topics", body, ct);

            var topics = new List<XhsTopicItem>();

            // Response: { "data": { "topic_dto_list": [{ "id", "name", "link", ... }] }, "success": true }
            // 注意字段名是 topic_dto_list，不是 topics（对比 topics.json）。
            // 条目里没有 type 字段（真实字段是 topic_type_v2，int），发布时固定用 "topic"。
            if (resp.TryGetProperty("data", out var data) &&
                data.TryGetProperty("topic_dto_list", out var topicsArr) &&
                topicsArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in topicsArr.EnumerateArray())
                {
                    var id = TryGetString(t, "id") ?? "";
                    var name = TryGetString(t, "name") ?? "";

                    if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                        topics.Add(new XhsTopicItem(id, name));
                }
            }

            // App 层以 new XhsTopicService(options) 创建（logger 为 null），
            // 用 Serilog 静态日志保证结果和失败都能被看到。
            Serilog.Log.Information("XHS topics: title={Title}, found={Count}",
                title, topics.Count);
            return topics;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "XHS topic suggest failed: title={Title}", title);
            return [];
        }
    }

    private static string? TryGetString(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString();
        return null;
    }
}
