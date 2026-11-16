using System.Text.Json;
using CreatFlow.Platforms.Kuaishou.Requests;
using Serilog;

namespace CreatFlow.Platforms.Kuaishou;

/// <summary>
/// 快手 @好友 候选（B3 抓包 2026-11-11，<c>docs/kuaishou/wire-2026-11-11.md</c> §2）。
///
/// 端点（cp 域 POST，Cookie + api_ph + <c>__NS_sig3</c>，与其它 cp 接口同一条 Handler 链）：
/// <code>
///   POST /rest/cp/works/v2/video/pc/at/search
///   body: {"keyword":"房","count":30,"page":0}
///   → {"result":1, "data":{"hasMore":true,"sessionId":"…",
///        "list":[{"userName":"房","userId":2210679639,"fansCount":4272,"headUrl":"https://…"}]}}
///
///   POST /rest/cp/works/v2/video/pc/at/choose
///   body: {"contactId":2210679639}          // contactId = 上一步的 userId
///   → {"result":1,"data":null}
/// </code>
///
/// ⚠️ 落点**不是独立发布字段**：真实 <c>video/pc/submit</c> 的 caption 是纯文本，
/// 每个 @ 写成 <c>@昵称(O&lt;userId&gt;)</c>（2026-11-15 页面 submit 抓包；编辑器 autosave 的
/// <c>at-user-item</c> HTML 只是页面序列化，**不能直接提交** —— 会被当普通文字，被 @ 方收不到通知）。
/// 发布前要对每个选中用户先调一次 <see cref="ChooseAsync"/>（页面选人时即调）。
///
/// 证据边界（如实记录）：提交抓包来自页面真发布（2026-11-15）；翻页参数（<c>page</c> / <c>hasMore</c>）
/// 语义未验证；<c>@</c> 与 <c>#话题</c> 混排的 submit 文本未单独抓（wire §6）。
/// 这些都不在本地编造，按页面默认原样下发。
/// </summary>
public sealed class KuaishouMentionService
{
    private const string SearchPath = "/rest/cp/works/v2/video/pc/at/search";
    private const string ChoosePath = "/rest/cp/works/v2/video/pc/at/choose";

    /// <summary>页面固定候选条数（抓包逐字 <c>count:30</c>）。</summary>
    public const int PageCount = 30;

    private readonly PlatformOptions _options;
    private readonly ILogger<KuaishouMentionService>? _logger;

    public KuaishouMentionService(
        PlatformOptions options, ILogger<KuaishouMentionService>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// 按关键词搜索可 @ 的快手用户。空关键词原样发给平台（抓包空词返回空列表，页面显示「未匹配到用户」；
    /// 空词行为未单独验证，不本地编造）。
    /// 失败向上抛（由页面写状态行）—— 不把错误静默吞成空列表。
    /// </summary>
    public async Task<KuaishouMentionPage> SearchAsync(
        string cookie, string? keyword = null, int count = PageCount, int page = 0,
        CancellationToken ct = default)
    {
        using var client = new KuaishouApiClient(cookie, _options,
            logger: _logger as ILogger<PlatformHttpClient>);

        var body = new
        {
            keyword = keyword ?? "",
            count = Math.Clamp(count, 1, PageCount),
            page = Math.Max(0, page),
        };
        var resp = await client.PostAsync<KuaishouAtSearchResponse>(
            SearchPath, body, PublishReferer, ct);
        var result = Normalize(resp);

        Log.Debug("[KuaishouMention] at/search 「{Keyword}」→ {Count} 人（hasMore={HasMore}）",
            keyword, result.Items.Count, result.HasMore);
        return result;
    }

    /// <summary>
    /// 选中确认 —— 页面在用户点选时调用（<c>contactId</c> = 候选的 <c>userId</c>）。
    /// 发布路径要在提交前对每个选中用户再调一次：<c>at/choose</c> 是会话级登记，
    /// 发布时的 Cookie/签名宿主与选人时刻不一定同一条连接（证据边界见类注释）。
    /// 失败向上抛（由调用方决定是否降级成「这次不挂 @」）。
    /// </summary>
    public async Task ChooseAsync(string cookie, long userId, CancellationToken ct = default)
    {
        if (userId <= 0)
            throw new ArgumentOutOfRangeException(nameof(userId), userId, "@好友 userId 必须为正数");

        using var client = new KuaishouApiClient(cookie, _options,
            logger: _logger as ILogger<PlatformHttpClient>);

        var resp = await client.PostRawAsync<KuaishouAtChooseResponse>(
            ChoosePath, new { contactId = userId }, PublishReferer, ct);
        if (resp.Result != 1)
        {
            throw new KuaishouApiException(
                $"Kuaishou at/choose: result={resp.Result}, msg={resp.Message ?? "?"}",
                resp.Result);
        }
    }

    // ═══ 解析（公开 static 供自检 / 排障钉字段映射）═══

    /// <summary>
    /// 解析 <c>at/search</c> 原始响应（含 envelope）。坏形状按空页处理，不抛。
    /// 缺昵称或 <c>userId ≤ 0</c> 的条目丢弃（caption HTML 两个值都要用）；同一 userId 只留一条。
    /// </summary>
    public static KuaishouMentionPage ParseUsers(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (Long(root, "result") != 1) return KuaishouMentionPage.Empty;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return KuaishouMentionPage.Empty;

        var dto = new KuaishouAtSearchResponse
        {
            HasMore = data.TryGetProperty("hasMore", out var hm) && hm.ValueKind == JsonValueKind.True,
            SessionId = Str(data, "sessionId"),
        };
        if (data.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in list.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                (dto.List ??= []).Add(new KuaishouAtUserDto
                {
                    UserName = Str(el, "userName"),
                    UserId = Long(el, "userId"),
                    FansCount = Long(el, "fansCount"),
                    HeadUrl = Str(el, "headUrl"),
                });
            }
        }
        return Normalize(dto);
    }

    /// <summary>解析 <c>at/choose</c> 原始响应，返回 <c>result</c>（1 = 成功）。坏形状返回 0。</summary>
    public static int ParseChooseResult(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return (int)Long(doc.RootElement, "result");
    }

    /// <summary>过滤无效条目（缺昵称 / 非正 userId）并按 userId 去重。</summary>
    private static KuaishouMentionPage Normalize(KuaishouAtSearchResponse? resp)
    {
        var items = new List<KuaishouMentionUser>();
        var seen = new HashSet<long>();
        foreach (var item in resp?.List ?? [])
        {
            var name = item.UserName?.Trim();
            if (item.UserId <= 0 || string.IsNullOrEmpty(name)) continue;
            if (!seen.Add(item.UserId)) continue;
            items.Add(new KuaishouMentionUser(
                UserId: item.UserId,
                UserName: name,
                FansCount: item.FansCount,
                HeadUrl: string.IsNullOrWhiteSpace(item.HeadUrl) ? null : item.HeadUrl.Trim()));
        }

        return new KuaishouMentionPage(
            items,
            HasMore: resp?.HasMore ?? false,
            SessionId: string.IsNullOrWhiteSpace(resp?.SessionId) ? null : resp.SessionId.Trim());
    }

    /// <summary>发布页 Referer（抓包原样；<see cref="KuaishouApiClient"/> 的路径推断只认 upload/submit）。</summary>
    private string PublishReferer => $"{_options.KuaishouCpHost}/article/publish/video?tabType=1";

    private static long Long(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt64(out var n) ? n : 0;

    private static string? Str(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}

/// <summary>
/// 一页 @好友 候选 + 是否还有更多 + 会话号。
/// <see cref="SessionId"/> 仅诊断用（抓包里 <c>at/choose</c> 不带它）。
/// </summary>
public sealed record KuaishouMentionPage(
    IReadOnlyList<KuaishouMentionUser> Items, bool HasMore, string? SessionId)
{
    public static KuaishouMentionPage Empty { get; } = new([], false, null);
}

/// <summary>
/// 一条快手用户候选。来源 <c>at/search</c>；选中后 <c>userId</c> 进 caption 的
/// <c>data-user-id</c> 并作为 <c>at/choose.contactId</c>。
/// </summary>
/// <param name="UserId">平台用户 ID（数字，wire 值）</param>
/// <param name="UserName">昵称（展示 + caption 的 <c>data-user-name</c> / <c>@昵称</c>）</param>
/// <param name="FansCount">粉丝数（仅展示）</param>
/// <param name="HeadUrl">头像 URL（仅展示）</param>
public sealed record KuaishouMentionUser(
    long UserId,
    string UserName,
    long FansCount,
    string? HeadUrl);
