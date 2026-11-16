namespace CreatFlow.Platforms.Xiaohongshu;

/// <summary>
/// 小红书 @用户搜索 —— <c>POST /web_api/sns/v1/search/user_info</c>（edith 域）。
///
/// 请求 / 响应取自 2026-11-10 真账号抓包（发布页正文里打 @ 触发的搜索，
/// <c>docs/官方发布页截图/小红书/capture-2026-11-10T08-33-18.jsonl</c>）：
/// <code>
/// 请求体：{"keyword":"","search_id":"1794300152281.8921","page":{"page_size":20,"page":1}}
/// 响应：{"data":{"user_info_dtos":[{"user_base_dto":{
///          "user_id":"6581a220000000002002f3b0",
///          "user_nickname":"办公技巧学习平台",
///          "red_id":"9124880334","image":"https://…","desc":"…"}}]}}
/// </code>
///
/// 发布体 <c>common.ats</c> 的元素形状（**发布路径** = 页面 <c>constructData</c>）：
/// <c>{userId, nickname}</c> —— 编辑器选中候选时
/// <c>e.command({data:{userId: n.uid, nickname: n.nickname}})</c>（MentionList 逐字），
/// <c>&lt;a data-user&gt;</c> 节点带的就是它；发布时 <c>constructData</c> 直接
/// <c>{nickname: e.name, ...e}</c> 展开 → 落成两键。
/// 正文里页面把 @ 作为内联节点，序列化出来就是「@昵称」——
/// 我们的正文是纯文本，见 <c>PublishPayloadBuilder.XhsCaption</c> 的补写口径。
///
/// ⚠️ 2026-11-15 真账号回读：旧实现照 <c>{nickname, userId, userid}</c>（草稿回填路径
/// <c>noteContent.userIds</c> 的形状）下发，<c>note/detail</c> 的 <c>ats=[]</c>；
/// 改用发布路径形状后再受控发布复核（详见进度表 P5 记录，**不判定平台忽略**）。
/// </summary>
public sealed class XhsMentionService
{
    private const string SearchPath = "/web_api/sns/v1/search/user_info";

    private readonly PlatformOptions _options;

    public XhsMentionService(PlatformOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// 按昵称搜索用户。空关键词 = 页面同款（平台返回推荐用户，抓包样本就是空关键词）。
    /// 失败向上抛（由页面写状态行）—— 不把错误静默吞成空列表。
    /// </summary>
    public async Task<List<XhsMentionUser>> SearchAsync(
        string cookie, string keyword, CancellationToken ct = default)
    {
        using var client = new XhsApiClient(cookie, _options);

        var searchId = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"
            + $".{Random.Shared.Next(1000, 10000)}";

        var body = new Dictionary<string, object?>
        {
            ["keyword"] = keyword ?? "",
            ["search_id"] = searchId,
            ["page"] = new Dictionary<string, object?>
            {
                ["page_size"] = 20,
                ["page"] = 1,
            },
        };

        var resp = await client.PostEdithRawAsync(SearchPath, body, ct);
        return ParseUsers(resp);
    }

    /// <summary>
    /// 解析搜索响应。<c>data.user_info_dtos[].user_base_dto</c> 是抓包形状；
    /// 兼容「数组直接挂在根 / data 下」「条目本身就是用户对象」两种变体。
    /// 缺 user_id 或缺昵称的条目跳过（选不出可展示的行）。
    /// </summary>
    public static List<XhsMentionUser> ParseUsers(JsonElement root)
    {
        var users = new List<XhsMentionUser>();
        if (FindUserArray(root) is not { } arr) return users;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;

            var dto = el.TryGetProperty("user_base_dto", out var nested)
                      && nested.ValueKind == JsonValueKind.Object
                ? nested
                : el;

            var id = Str(dto, "user_id", "userId", "userid", "id");
            var name = Str(dto, "user_nickname", "nickname", "userName", "name");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) continue;
            if (!seen.Add(id)) continue;

            users.Add(new XhsMentionUser(
                id,
                name,
                Str(dto, "red_id", "redId"),
                Str(dto, "desc", "description"),
                Str(dto, "image", "image_size_large", "avatar")));
        }

        return users;
    }

    /// <summary>
    /// 把存档里的 <c>common.ats</c> JSON 解析回用户列表。
    /// **永不抛** —— 这段 JSON 的格式错误由发布执行端 <c>ParseJsonValue</c> 报给用户；
    /// 预检 / 正文组装拿到坏 JSON 时安静跳过即可，不能因此炸掉整个预检。
    /// </summary>
    public static List<XhsMentionUser> ParseAts(string? json)
    {
        var users = new List<XhsMentionUser>();
        if (string.IsNullOrWhiteSpace(json)) return users;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return users;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var id = Str(el, "userId", "userid", "user_id");
                var name = Str(el, "nickname", "name");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) continue;
                if (!seen.Add(id)) continue;
                users.Add(new XhsMentionUser(id, name));
            }
        }
        catch (JsonException)
        {
            // 坏 JSON 留给发布执行端报错（见 summary）
        }

        return users;
    }

    /// <summary>
    /// 组装发布体 <c>common.ats</c> 的 JSON 串 —— **与页面发布路径逐字同形**：
    /// 编辑器选中候选时 <c>e.command({data:{userId: n.uid, nickname: n.nickname}})</c>
    /// （2026-11-15 线上 bundle <c>project-publish-vue.d001adc9.js</c> 的 MentionList 逐字），
    /// 发布时 <c>constructData</c> 直接展开该对象 → 条目就是 <c>{userId, nickname}</c>。
    /// 旧形状 <c>{nickname, userId, userid}</c> 是草稿回填路径的。空列表返回 null（不下发）。
    /// </summary>
    public static string? BuildAtsJson(IEnumerable<XhsMentionUser> users)
    {
        var list = new List<Dictionary<string, object?>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var u in users)
        {
            if (string.IsNullOrWhiteSpace(u.UserId) || string.IsNullOrWhiteSpace(u.Nickname)) continue;
            if (!seen.Add(u.UserId)) continue;
            list.Add(new Dictionary<string, object?>
            {
                ["userId"] = u.UserId,
                ["nickname"] = u.Nickname,
            });
        }

        return list.Count == 0 ? null : JsonSerializer.Serialize(list);
    }

    private static JsonElement? FindUserArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return root;
        if (root.ValueKind != JsonValueKind.Object) return null;

        if (root.TryGetProperty("user_info_dtos", out var top)
            && top.ValueKind == JsonValueKind.Array)
            return top;

        if (root.TryGetProperty("data", out var data))
        {
            if (data.ValueKind == JsonValueKind.Array) return data;
            if (data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("user_info_dtos", out var arr)
                && arr.ValueKind == JsonValueKind.Array)
                return arr;
        }

        return null;
    }

    private static string? Str(JsonElement el, params string[] props)
    {
        foreach (var prop in props)
        {
            if (!el.TryGetProperty(prop, out var v)) continue;
            if (v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
        }
        return null;
    }
}

/// <summary>
/// 小红书 @用户（搜索候选 / <c>common.ats</c> 存档共用）。
/// <paramref name="RedId"/> 是展示用的「小红书号」，<paramref name="Desc"/> 是简介片段，
/// 只用于候选列表副标题，不进 wire。
/// </summary>
public sealed record XhsMentionUser(
    string UserId,
    string Nickname,
    string? RedId = null,
    string? Desc = null,
    string? AvatarUrl = null);
