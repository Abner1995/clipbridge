namespace CreatFlow.Platforms.Bilibili;

/// <summary>
/// B站个性化卡片 —— 三类：互动引导（type=5）/ 关联视频（type=2）/ 投票弹幕（type=9）。
/// 2026-11-15 B5 抓包，wire 见 <c>docs/bilibili/wire-2026-11-15.md</c>。
///
/// <code>
/// POST https://api.bilibili.com/x/v2/dm/command/post      （form-urlencoded）
/// 互动引导 type=5：type=5&amp;cid=&lt;cid&gt;&amp;progress=&lt;毫秒&gt;&amp;plat=10
///   &amp;data={"duration":5000,"posX_2":50,"posY_2":65,"type":2,"auto_create":false,"count_down":10}
///   &amp;auto_create=false&amp;count_down=10&amp;csrf=&lt;bili_jct&gt;
/// 关联视频 type=2：type=2&amp;cid&amp;progress&amp;plat=10
///   &amp;data={"oid":"&lt;BVID&gt;","msg":"…","duration":10000,"posX_2":50,"posY_2":65}&amp;csrf
/// 投票弹幕 type=9：type=9&amp;cid&amp;progress&amp;plat=10
///   &amp;data={"question":…,"duration":&lt;选区毫秒&gt;,"posX_2":97,"posY_2":85,
///           "pub_dynamic":false,"has_self_def":"0","optionA":…,"optionB":…}&amp;csrf
/// </code>
///
/// 卡片**按 cid 提前建好**（视频上传完成即可，aid 可为 0），发布体只落
/// <c>videos[0].pre_command = true</c>（<see cref="BilibiliPublishService"/>）。
///
/// 前置条件（2026-11-15 实测）：视频必须是 <b>h264 / mp4</b>，否则投稿页的
/// 「+ 个性化卡片」按钮本身就是灰的。我们照发，平台拒绝时按非致命处理
/// （不发 pre_command，稿件照常投）。
///
/// 边界（wire §4.3）：关联视频的目标稿件是「仅自己」时平台回
/// <c>79025 发送失败，暂不支持跳转他人稿件</c>；公开本人稿件未验 ——
/// 失败走非致命路径，不影响稿件本身。
/// </summary>
public sealed class BilibiliCardService
{
    /// <summary>卡片创建/修改（抓包 <c>POST /x/v2/dm/command/post</c>）</summary>
    public const string PostPath = "/x/v2/dm/command/post";

    /// <summary>卡片删除（<c>/x/v2/dm/command/del</c>，v1 未用）</summary>
    public const string DeletePath = "/x/v2/dm/command/del";

    /// <summary>
    /// 已建卡片列表（抓包 <c>GET /x/v2/dm/command/creative/list?oid=&lt;cid&gt;&amp;plat=10</c>）——
    /// 2026-11-15 抓包里创建前后各拉过一次：创建前 <c>data:null</c>，创建后是卡片数组。
    /// 这是「卡片是否真的挂到 cid 上」的回读通道（探针用，不进发布链路）。
    /// </summary>
    public const string ListPath = "/x/v2/dm/command/creative/list";

    /// <summary>外层 <c>type</c>：5 = 互动引导（关注弹幕）</summary>
    public const int GuideCardType = 5;

    /// <summary>外层 <c>type</c>：2 = 关联视频（wire §4.1）</summary>
    public const int RelatedVideoCardType = 2;

    /// <summary>外层 <c>type</c>：9 = 投票弹幕（wire §4.2）</summary>
    public const int VoteCardType = 9;

    /// <summary>投稿页（播放器 editor 场景）用的 <c>plat</c>；抓包逐字 10</summary>
    public const int Platform = 10;

    /// <summary>互动引导页面默认持续 5 秒</summary>
    public const int DefaultDurationMs = 5000;

    /// <summary>页面默认倒计时 10 秒（「稿件时长不足时，一律在倒数 10 秒展示」）</summary>
    public const int DefaultCountdownSeconds = 10;

    /// <summary>关联视频持续时间固定 10 秒（编辑器 <c>dm2</c>：<c>i.duration=1e4</c>）</summary>
    public const int RelatedVideoDurationMs = 10000;

    /// <summary>关联视频卡片位置（抓包默认）</summary>
    public const int RelatedVideoPosX = 50;
    public const int RelatedVideoPosY = 65;

    /// <summary>投票弹幕卡片位置（抓包默认，右下角）</summary>
    public const int VotePosX = 97;
    public const int VotePosY = 85;

    /// <summary>投票问题上限（页面校验，wire §4.2）</summary>
    public const int VoteQuestionMaxChars = 12;

    /// <summary>投票选项上限（页面校验）</summary>
    public const int VoteOptionMaxChars = 6;

    /// <summary>投票选项数量：A/B 必填，C/D 可选</summary>
    public const int VoteMinOptions = 2;
    public const int VoteMaxOptions = 4;

    /// <summary>播放器弹窗的 Referer（卡片请求从 iframe 里发出）</summary>
    public const string Referer = "https://www.bilibili.com/";

    private readonly PlatformOptions _options;

    public BilibiliCardService(PlatformOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// 在 <paramref name="cid"/> 上创建互动引导卡片。
    /// <paramref name="followType"/>：<b>2 = 三连+关注 / 1 = 仅三连 / 0 = 仅关注</b>
    /// （3/4 是充电相关，仅充电权限账号可见，v1 不支持）。
    /// <paramref name="progressMs"/> = 起始时间（毫秒）。
    /// </summary>
    public async Task<BilibiliCreatedCard> CreateGuideCardAsync(
        string cookie, long cid, int followType, long progressMs,
        CancellationToken ct = default)
    {
        if (cid <= 0)
            throw new BilibiliApiException($"B站卡片：cid 不合法（{cid}）", -1);
        if (followType is < 0 or > 2)
        {
            throw new BilibiliApiException(
                $"B站互动引导样式只支持 0/1/2（仅关注 / 仅三连 / 三连+关注），当前 {followType}", -1);
        }
        if (progressMs < 0) progressMs = 0;

        var csrf = BilibiliUploadService.RequireCsrf(cookie);
        using var client = new BilibiliApiClient(cookie, _options);

        var url = BuildPostUrl(_options.BilibiliApiHost);
        using var form = BuildGuideCardForm(cid, followType, progressMs, csrf);
        var body = await client.PostFormAsync(url, form, Referer, ct);
        return ParseCardResponse(body);
    }

    /// <summary>
    /// 创建关联视频卡片（wire §4.1）。<paramref name="oid"/> 是目标视频 BVID
    /// （面板 form 里叫 <c>bvid</c>，create 体字段名是 <c>oid</c>）。
    /// <paramref name="msg"/> = 描述（可空）。目标稿件「仅自己」时回 79025，由调用方按非致命处理。
    /// </summary>
    public async Task<BilibiliCreatedCard> CreateRelatedVideoCardAsync(
        string cookie, long cid, string oid, string? msg, long progressMs,
        CancellationToken ct = default)
    {
        if (cid <= 0)
            throw new BilibiliApiException($"B站关联视频卡片：cid 不合法（{cid}）", -1);
        var target = (oid ?? "").Trim();
        if (target.Length == 0)
            throw new BilibiliApiException("B站关联视频卡片：目标视频 BVID 不能为空", -1);
        if (progressMs < 0) progressMs = 0;

        var csrf = BilibiliUploadService.RequireCsrf(cookie);
        using var client = new BilibiliApiClient(cookie, _options);

        var url = BuildPostUrl(_options.BilibiliApiHost);
        using var form = BuildRelatedVideoCardForm(cid, target, msg, progressMs, csrf);
        var body = await client.PostFormAsync(url, form, Referer, ct);
        return ParseCardResponse(body);
    }

    /// <summary>
    /// 创建投票弹幕卡片（wire §4.2）。<paramref name="options"/> = A/B/C/D（2~4 个，按顺序落
    /// <c>optionA…optionD</c>），<paramref name="durationMs"/> = 时间轴选区毫秒。
    /// 规则（问题 ≤12 字、选项 ≤6 字、不许含分号 / 重复）先在本地校验，不合规直接报错。
    /// </summary>
    public async Task<BilibiliCreatedCard> CreateVoteCardAsync(
        string cookie, long cid, string question, IReadOnlyList<string> options,
        long durationMs, long progressMs, CancellationToken ct = default)
    {
        if (cid <= 0)
            throw new BilibiliApiException($"B站投票弹幕：cid 不合法（{cid}）", -1);

        var problems = ValidateVote(question, options);
        if (problems.Count > 0)
            throw new BilibiliApiException("B站投票弹幕参数不合法：" + string.Join("；", problems), -1);
        if (durationMs <= 0) durationMs = DefaultDurationMs;
        if (progressMs < 0) progressMs = 0;

        var csrf = BilibiliUploadService.RequireCsrf(cookie);
        using var client = new BilibiliApiClient(cookie, _options);

        var url = BuildPostUrl(_options.BilibiliApiHost);
        using var form = BuildVoteCardForm(
            cid, question, options, durationMs, progressMs, csrf);
        var body = await client.PostFormAsync(url, form, Referer, ct);
        return ParseCardResponse(body);
    }

    /// <summary>
    /// 列出 <paramref name="cid"/> 上已建的卡片（回读通道）。
    /// 发布/草稿卡片只看 cid，不需要 aid；<c>data:null</c>（还没建过）= 空列表。
    /// </summary>
    public async Task<IReadOnlyList<BilibiliCommandCard>> ListAsync(
        string cookie, long cid, CancellationToken ct = default)
    {
        if (cid <= 0)
            throw new BilibiliApiException($"B站卡片列表：cid 不合法（{cid}）", -1);

        using var client = new BilibiliApiClient(cookie, _options);
        var url = BuildListUrl(_options.BilibiliApiHost, cid);
        var body = await client.GetStringAsync(url, Referer, ct);
        return ParseCardList(body);
    }

    // ═══ 纯函数（自检直接验 wire 形体） ═══

    /// <summary>卡片请求 URL（无 query；csrf 在表单里 —— 抓包逐字）。</summary>
    public static string BuildPostUrl(string? apiHost = null)
    {
        var host = string.IsNullOrWhiteSpace(apiHost)
            ? "https://api.bilibili.com"
            : apiHost.TrimEnd('/');
        return host + PostPath;
    }

    /// <summary>卡片列表 URL（抓包逐字：<c>?oid=&lt;cid&gt;&amp;plat=10</c>，无 csrf）。</summary>
    public static string BuildListUrl(string? apiHost, long cid)
    {
        var host = string.IsNullOrWhiteSpace(apiHost)
            ? "https://api.bilibili.com"
            : apiHost.TrimEnd('/');
        return $"{host}{ListPath}?oid={cid}&plat={Platform}";
    }

    /// <summary>
    /// <c>data</c> 字段的 JSON 串（抓包逐字，顺序也保持一致）：
    /// <c>{"duration":5000,"posX_2":50,"posY_2":65,"type":2,"auto_create":false,"count_down":10}</c>
    /// </summary>
    public static string BuildGuideCardDataJson(int followType)
        => $"{{\"duration\":{DefaultDurationMs},\"posX_2\":50,\"posY_2\":65,"
           + $"\"type\":{followType},\"auto_create\":false,"
           + $"\"count_down\":{DefaultCountdownSeconds}}}";

    /// <summary>
    /// 表单（抓包逐字）：外层 <c>type=5</c> / <c>cid</c> / <c>progress</c>（毫秒）/
    /// <c>plat=10</c> / <c>data</c> / <c>auto_create=false</c> / <c>count_down=10</c> / <c>csrf</c>。
    /// </summary>
    public static FormUrlEncodedContent BuildGuideCardForm(
        long cid, int followType, long progressMs, string csrf)
    {
        // 用 KeyValuePair 数组而不是 Dictionary：编码顺序与抓包逐字一致
        //（Dictionary 的枚举顺序是实现细节，别指望它）
        return new FormUrlEncodedContent(
        [
            new("type", GuideCardType.ToString()),
            new("cid", cid.ToString()),
            new("progress", progressMs.ToString()),
            new("plat", Platform.ToString()),
            new("data", BuildGuideCardDataJson(followType)),
            new("auto_create", "false"),
            new("count_down", DefaultCountdownSeconds.ToString()),
            new("csrf", csrf),
        ]);
    }

    /// <summary>
    /// 关联视频 <c>data</c>（wire §4.1 逐字）：
    /// <c>{"oid":"BV1RFWs6XExT","msg":"…","duration":10000,"posX_2":50,"posY_2":65}</c>。
    /// 目标视频字段名是 <c>oid</c>（BVID 字符串），不是面板 form 里的 <c>bvid</c>。
    /// </summary>
    public static string BuildRelatedVideoDataJson(string oid, string? msg)
        => $"{{\"oid\":{JsonString(oid)},\"msg\":{JsonString(msg ?? "")},"
           + $"\"duration\":{RelatedVideoDurationMs},"
           + $"\"posX_2\":{RelatedVideoPosX},\"posY_2\":{RelatedVideoPosY}}}";

    /// <summary>
    /// 关联视频表单（wire §4.1）：<c>type=2 / cid / progress / plat=10 / data / csrf</c> ——
    /// **没有** <c>auto_create</c> / <c>count_down</c>（那是互动引导专属，抓包逐字）。
    /// </summary>
    public static FormUrlEncodedContent BuildRelatedVideoCardForm(
        long cid, string oid, string? msg, long progressMs, string csrf)
    {
        return new FormUrlEncodedContent(
        [
            new("type", RelatedVideoCardType.ToString()),
            new("cid", cid.ToString()),
            new("progress", progressMs.ToString()),
            new("plat", Platform.ToString()),
            new("data", BuildRelatedVideoDataJson(oid, msg)),
            new("csrf", csrf),
        ]);
    }

    /// <summary>
    /// 投票弹幕 <c>data</c>（wire §4.2 逐字，顺序也一致）：
    /// <c>{"question":…,"duration":7000,"posX_2":97,"posY_2":85,"pub_dynamic":false,
    /// "has_self_def":"0","optionA":…,"optionB":…}</c>。
    /// <c>duration</c> = 时间轴选区毫秒；选项按顺序落 <c>optionA…optionD</c>。
    /// </summary>
    public static string BuildVoteDataJson(
        string question, IReadOnlyList<string> options, long durationMs)
    {
        var sb = new StringBuilder();
        sb.Append("{\"question\":").Append(JsonString(question));
        sb.Append(",\"duration\":").Append(Math.Max(0, durationMs));
        sb.Append($",\"posX_2\":{VotePosX},\"posY_2\":{VotePosY}");
        sb.Append(",\"pub_dynamic\":false,\"has_self_def\":\"0\"");
        for (var i = 0; i < options.Count && i < VoteMaxOptions; i++)
            sb.Append(",\"option").Append((char)('A' + i)).Append("\":")
              .Append(JsonString(options[i]));
        sb.Append('}');
        return sb.ToString();
    }

    /// <summary>
    /// 投票弹幕表单（wire §4.2）：<c>type=9 / cid / progress / plat=10 / data / csrf</c>。
    /// </summary>
    public static FormUrlEncodedContent BuildVoteCardForm(
        long cid, string question, IReadOnlyList<string> options,
        long durationMs, long progressMs, string csrf)
    {
        return new FormUrlEncodedContent(
        [
            new("type", VoteCardType.ToString()),
            new("cid", cid.ToString()),
            new("progress", progressMs.ToString()),
            new("plat", Platform.ToString()),
            new("data", BuildVoteDataJson(question, options, durationMs)),
            new("csrf", csrf),
        ]);
    }

    /// <summary>
    /// 投票规则校验（页面同款，wire §4.2）：问题 ≤12 字；选项 2~4 个、每个 ≤6 字、
    /// 不许含分号、不许重复。返回空列表 = 合法。
    /// </summary>
    public static IReadOnlyList<string> ValidateVote(
        string? question, IReadOnlyList<string>? options)
    {
        var problems = new List<string>();

        var q = (question ?? "").Trim();
        if (q.Length == 0)
            problems.Add("投票问题不能为空");
        else if (q.Length > VoteQuestionMaxChars)
            problems.Add($"投票问题最多 {VoteQuestionMaxChars} 个字（当前 {q.Length} 个字）");

        var opts = (options ?? []).Select(o => (o ?? "").Trim()).ToList();
        if (opts.Count < VoteMinOptions)
            problems.Add($"投票选项至少要 {VoteMinOptions} 个（当前 {opts.Count} 个）");
        else if (opts.Count > VoteMaxOptions)
            problems.Add($"投票选项最多 {VoteMaxOptions} 个（当前 {opts.Count} 个）");

        for (var i = 0; i < opts.Count; i++)
        {
            var label = $"选项{(char)('A' + i)}";
            if (opts[i].Length == 0)
            {
                problems.Add($"{label}不能为空");
                continue;
            }
            if (opts[i].Length > VoteOptionMaxChars)
                problems.Add($"{label}最多 {VoteOptionMaxChars} 个字（当前 {opts[i].Length} 个字）");
            if (opts[i].Contains(';') || opts[i].Contains('；'))
                problems.Add($"{label}不能包含分号");
        }

        var duplicates = opts
            .Where(o => o.Length > 0)
            .GroupBy(o => o, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicates.Count > 0)
            problems.Add("投票选项不能重复：" + string.Join("、", duplicates));

        return problems;
    }

    /// <summary>
    /// A/B/C/D 四格 → 选项列表（A/B 恒在，C/D 有值才追加）。
    /// 注意：C 空而 D 有值时这个列表会把 D 落到 optionC —— 预检用
    /// <see cref="HasOptionGap"/> 提前拦掉这种不连续的配置。
    /// </summary>
    public static List<string> VoteOptionsFrom(string? optionA, string? optionB,
        string? optionC = null, string? optionD = null)
    {
        var list = new List<string>
        {
            (optionA ?? "").Trim(),
            (optionB ?? "").Trim(),
        };
        var c = (optionC ?? "").Trim();
        var d = (optionD ?? "").Trim();
        if (c.Length > 0) list.Add(c);
        if (d.Length > 0) list.Add(d);
        return list;
    }

    /// <summary>选项空洞判定：C 空但 D 有值（平台选项必须从 A 开始连续）。</summary>
    public static bool HasOptionGap(string? optionA, string? optionB,
        string? optionC, string? optionD)
        => string.IsNullOrWhiteSpace(optionC) && !string.IsNullOrWhiteSpace(optionD);

    /// <summary>
    /// 解析响应：<c>code=0</c>；<c>data.command</c> / <c>data.content</c> / id 有就带出
    /// （缺 id 不算失败 —— 稿件落点靠 pre_command，不靠这个 id）。
    /// 三类卡片响应同形（互动引导 <c>#ATTENTION#</c> / 投票 <c>#VOTE#</c>…）。
    /// </summary>
    public static BilibiliCreatedCard ParseCardResponse(string bodyText)
    {
        BilibiliUploadService.RequireCodeZero(bodyText, "卡片");

        using var doc = JsonDocument.Parse(bodyText);
        var root = doc.RootElement;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return new BilibiliCreatedCard(0, null, null);

        long id = 0;
        if (data.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
            idEl.TryGetInt64(out id);

        return new BilibiliCreatedCard(
            id,
            data.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString()
                : null,
            data.TryGetProperty("content", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null);
    }

    /// <summary>
    /// 解析卡片列表响应（抓包 <c>data</c> 是数组；<c>null</c> = 一条都没有）。
    /// 只带出回读关心的字段：<c>type</c>（5 互动引导 / 2 关联视频 / 9 投票弹幕…）、
    /// <c>command</c> / <c>content</c> / <c>progress</c>（毫秒）。
    /// </summary>
    public static IReadOnlyList<BilibiliCommandCard> ParseCardList(string bodyText)
    {
        BilibiliUploadService.RequireCodeZero(bodyText, "卡片列表");

        using var doc = JsonDocument.Parse(bodyText);
        var root = doc.RootElement;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<BilibiliCommandCard>();
        foreach (var el in data.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;

            long id = 0;
            if (el.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
                idEl.TryGetInt64(out id);

            long progress = 0;
            if (el.TryGetProperty("progress", out var pEl) && pEl.ValueKind == JsonValueKind.Number)
                pEl.TryGetInt64(out progress);

            var type = 0;
            if (el.TryGetProperty("type", out var tEl) && tEl.ValueKind == JsonValueKind.Number)
                tEl.TryGetInt32(out type);

            list.Add(new BilibiliCommandCard(
                id,
                type,
                el.TryGetProperty("command", out var cEl) && cEl.ValueKind == JsonValueKind.String
                    ? cEl.GetString()
                    : null,
                el.TryGetProperty("content", out var txtEl) && txtEl.ValueKind == JsonValueKind.String
                    ? txtEl.GetString()
                    : null,
                progress));
        }
        return list;
    }

    /// <summary>互动引导样式的界面标签（0/1/2；v1 支持的三种）</summary>
    public static string FollowTypeLabel(int followType) => followType switch
    {
        0 => "仅关注",
        1 => "仅三连",
        2 => "三连+关注",
        _ => $"未知样式 {followType}",
    };

    // 卡片 data 里含中文（msg / question / 选项）—— 与 add/v3 同口径保留原文，
    // 不用默认编码器把中文转成 \uXXXX（抓包样本是 UTF-8 原文）。
    private static readonly JsonSerializerOptions DataJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string JsonString(string? value)
        => JsonSerializer.Serialize(value ?? "", DataJsonOptions);
}

/// <summary>创建成功的卡片（id / 服务端生成的指令与文案）。三类卡片响应同形。</summary>
public sealed record BilibiliCreatedCard(long Id, string? Command, string? Content);

/// <summary>
/// 已建卡片（<c>dm/command/creative/list</c> 的一条）。
/// <paramref name="Type"/> 是外层卡片类型：5 = 互动引导 / 2 = 关联视频 / 9 = 投票弹幕；
/// <paramref name="Progress"/> 是起始毫秒。
/// </summary>
public sealed record BilibiliCommandCard(
    long Id, int Type, string? Command, string? Content, long Progress);
