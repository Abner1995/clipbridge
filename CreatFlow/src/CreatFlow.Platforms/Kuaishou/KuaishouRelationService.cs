using System.Text.Json;
using CreatFlow.Platforms.Kuaishou.Requests;
using Serilog;

namespace CreatFlow.Platforms.Kuaishou;

/// <summary>
/// 快手「作者服务 · 关联小程序」（**2026-11-15 真页面抓包**）。
///
/// 端点（cp 域 POST，Cookie + api_ph + <c>__NS_sig3</c>，与其它 cp 接口同一条 Handler 链）：
/// <code>
///   POST /rest/cp/works/v2/video/pc/relation/list
///   body（输入链接后）: {"type":9,"title":"https://fcvs…/f/X3uTsgHfDnXDtNu","cursor":""}
///   → {"result":1, "data":{
///        "list":[{"title":"优酷","url":"http://…png",
///                 "associateTaskId":{"taskId":0,"bindId":"plc669066023879745919",
///                                    "entranceType":9,"bindType":"25","taskType":"PLC"},
///                 "bannerTask":null,"desc":"…","validMessage":null}],
///        "cursor":"0"}}
/// </code>
///
/// · <c>type = 9</c> 是发布页「选择服务类型」里「关联小程序」那一格的 value
///   （同一路径也服务其它变现入口，如房源 <c>HOUSE_TOOL</c>）。
/// · 页面选中即 <c>onChange("associateTasks", [item.associateTaskId])</c>；真实 submit 体里
///   <c>associateTasks</c> 是**整个对象**的数组（2026-11-15 受控发布抓包逐字）。
/// · 未关联时页面 submit **不带 <c>associateTasks</c> 键**（2026-11-05 抓包无此键）。
///
/// 证据边界：只验证过「关联小程序」这一种 type；链接解析结果条数（本次 1 条）、
/// 翻页 cursor 语义、<c>validMessage</c> 的含义均未单独验证 —— 原样透传，不本地编造。
/// </summary>
public sealed class KuaishouRelationService
{
    private const string Path = "/rest/cp/works/v2/video/pc/relation/list";

    /// <summary>「关联小程序」服务类型（页面下拉 value，2026-11-15 抓包）。</summary>
    public const int AppletType = 9;

    /// <summary>首屏游标（页面输入链接时的值，抓包逐字空串）。</summary>
    public const string FirstCursor = "";

    private readonly PlatformOptions _options;
    private readonly ILogger<KuaishouRelationService>? _logger;

    public KuaishouRelationService(
        PlatformOptions options, ILogger<KuaishouRelationService>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// 用小程序分享链接换关联任务。<paramref name="link"/> 必须是快手 APP 小程序页面
    /// 「复制页面链接」得到的完整 URL；解析不到时平台回 <c>list: []</c>（不是错误）。
    /// 失败向上抛（由页面写状态行）。
    /// </summary>
    /// <param name="cookie">Cookie</param>
    /// <param name="link">小程序页面链接</param>
    /// <param name="type">服务类型（默认 <see cref="AppletType"/> = 关联小程序）</param>
    public async Task<KuaishouRelationPage> SearchAsync(
        string cookie, string link, int type = AppletType, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(link))
            throw new ArgumentException("小程序链接不能为空", nameof(link));

        using var client = new KuaishouApiClient(cookie, _options,
            logger: _logger as ILogger<PlatformHttpClient>);

        var body = new
        {
            type,
            title = link.Trim(),
            cursor = FirstCursor,
        };
        var resp = await client.PostAsync<KuaishouRelationListResponse>(Path, body, ct);
        var page = Normalize(resp);

        Log.Debug("[KuaishouRelation] relation/list type={Type} 「{Link}」→ {Count} 个关联项",
            type, link, page.Items.Count);
        return page;
    }

    // ═══ 解析（公开 static 供自检 / 排障钉字段映射）═══

    /// <summary>
    /// 解析 <c>relation/list</c> 原始响应（含 envelope）。坏形状按空页处理，不抛。
    /// 缺 <c>bindId</c> 的条目丢弃（没有它 submit 无法关联）；其余字段原样保留。
    /// </summary>
    public static KuaishouRelationPage ParseRelations(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (Long(root, "result") != 1) return KuaishouRelationPage.Empty;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return KuaishouRelationPage.Empty;

        var dto = new KuaishouRelationListResponse
        {
            Data = new KuaishouRelationListData { Cursor = Str(data, "cursor") },
        };
        if (data.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in list.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                KuaishouAssociateTaskDto? task = null;
                if (el.TryGetProperty("associateTaskId", out var at)
                    && at.ValueKind == JsonValueKind.Object)
                {
                    task = new KuaishouAssociateTaskDto
                    {
                        TaskId = Long(at, "taskId"),
                        BindId = Str(at, "bindId") ?? "",
                        EntranceType = (int)Long(at, "entranceType"),
                        BindType = Str(at, "bindType") ?? "",
                        TaskType = Str(at, "taskType") ?? "",
                    };
                }

                (dto.Data.List ??= []).Add(new KuaishouRelationItemDto
                {
                    Title = Str(el, "title"),
                    Url = Str(el, "url"),
                    Desc = Str(el, "desc"),
                    AssociateTaskId = task,
                    ValidMessage = Str(el, "validMessage"),
                });
            }
        }
        return Normalize(dto);
    }

    /// <summary>过滤缺 <c>bindId</c> 的条目（没有它提交不了）；title 缺省回落 bindId。</summary>
    private static KuaishouRelationPage Normalize(KuaishouRelationListResponse? resp)
    {
        var items = new List<KuaishouRelationItem>();
        foreach (var item in resp?.Data?.List ?? [])
        {
            var task = item.AssociateTaskId;
            var bindId = task?.BindId?.Trim();
            if (task is null || string.IsNullOrEmpty(bindId)) continue;

            items.Add(new KuaishouRelationItem(
                Title: string.IsNullOrWhiteSpace(item.Title) ? bindId : item.Title.Trim(),
                Url: NullIfEmpty(item.Url),
                Desc: NullIfEmpty(item.Desc),
                ValidMessage: NullIfEmpty(item.ValidMessage),
                Task: new KuaishouAssociateTask(
                    TaskId: task.TaskId,
                    BindId: bindId,
                    EntranceType: task.EntranceType,
                    BindType: task.BindType?.Trim() ?? "",
                    TaskType: task.TaskType?.Trim() ?? "")));
        }

        return new KuaishouRelationPage(
            items, NullIfEmpty(resp?.Data?.Cursor));
    }

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static long Long(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt64(out var n) ? n : 0;

    private static string? Str(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}

/// <summary>
/// 一页关联项 + 平台游标（<c>cursor</c> 是字符串；**翻页语义未验证** ——
/// 观测值 <c>"0"</c> 不能直接当「有下一页」，本服务不解释它，只原样带回）。
/// </summary>
public sealed record KuaishouRelationPage(
    IReadOnlyList<KuaishouRelationItem> Items, string? Cursor)
{
    public static KuaishouRelationPage Empty { get; } = new([], null);
}

/// <summary>
/// 一条关联项。来源 <c>relation/list</c>；<see cref="Task"/> 原样进 submit 的
/// <c>associateTasks[0]</c>（页面行为：<c>onChange("associateTasks",[item.associateTaskId])</c>）。
/// </summary>
/// <param name="Title">小程序名称（展示）</param>
/// <param name="Url">小程序图标 URL（仅展示）</param>
/// <param name="Desc">小程序描述（仅展示）</param>
/// <param name="ValidMessage">平台校验提示（未观测到非空值，原样保留）</param>
/// <param name="Task">wire 元素（taskId / bindId / entranceType / bindType / taskType）</param>
public sealed record KuaishouRelationItem(
    string Title,
    string? Url,
    string? Desc,
    string? ValidMessage,
    KuaishouAssociateTask Task);

/// <summary>
/// submit <c>associateTasks</c> 的元素（2026-11-15 抓包逐字）：
/// <c>{taskId, bindId, entranceType, bindType, taskType}</c>。
/// </summary>
public sealed record KuaishouAssociateTask(
    long TaskId,
    string BindId,
    int EntranceType,
    string BindType,
    string TaskType);
