namespace CreatFlow.Platforms.WechatVideo;

using CreatFlow.Platforms.Core;
using Serilog;

/// <summary>
/// 视频号 @好友 候选（2026-11-11 B2 抓包 <c>wire-2026-11-11.md</c> §5.2）。
/// </summary>
/// <param name="Nickname">昵称 —— **提交体唯一字段**（<c>mentionedUser:[{nickname}]</c>），
/// 平台按昵称解析；正文里也要出现 <c>@昵称 </c></param>
/// <param name="Username">视频号 username（仅展示 / 去重；**不进提交体** —— 抓包实证只有昵称）</param>
/// <param name="AvatarUrl">头像 URL（仅展示）</param>
/// <param name="AuthCompanyName">认证公司名（仅展示，用于同名账号辨认）</param>
public sealed record WechatVideoMentionUser(
    string Nickname,
    string? Username = null,
    string? AvatarUrl = null,
    string? AuthCompanyName = null);

/// <summary>
/// 视频号 @好友 搜索 —— <c>POST /helper/helper_search_finder_account</c>（B2 §5.2）。
///
/// <code>
///   请求体：{"pageSize":10,"currentPage":1,"offset":0,"query":"知新"} + 公共上报参数
///   响应：{"data":{"list":[{"username":"v2_…@finder","headImgUrl":"https://…",
///          "nickName":"知鸭","authImgUrl":"…","authCompanyName":""}]}}
/// </code>
///
/// 发布体落点：<c>objectDesc.mentionedUser = [{"nickname":"温史知新"}]</c> +
/// 正文（<c>description</c> / <c>topic.finderTopicInfo</c> 的 value0）里的 <c>@昵称 </c>（结尾空格）。
/// **只有昵称**：平台侧按昵称解析，username 不进 wire（B2 §5.3）。
///
/// 证据边界（如实记录）：提交体形状来自草稿接口；@ 是否真按昵称挂上待 P5 真账号回读。
/// </summary>
public sealed class WechatVideoMentionService
{
    /// <summary>@好友候选搜索路径（/micro/content/cgi-bin/mmfinderassistant-bin 之下）</summary>
    private const string SearchPath = "/helper/helper_search_finder_account";

    private readonly PlatformOptions _options;

    public WechatVideoMentionService(PlatformOptions options) => _options = options;

    /// <summary>
    /// 按关键词搜索可 @ 的视频号。空关键词原样发给平台（抓包是「知新」；空词行为未抓，不本地编造）。
    /// 失败向上抛（由页面写状态行）—— 不把错误静默吞成空列表。
    /// </summary>
    public async Task<List<WechatVideoMentionUser>> SearchAsync(
        string cookie, string deviceId, string keyword, int pageSize = 10,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new InvalidOperationException(
                "视频号凭证缺 deviceId —— 到「平台账号」页打开该账号窗口、点「完成登录」刷新一次凭证");
        }

        using var client = new WechatVideoApiClient(_options, cookie, deviceId);
        var resp = await client.PostAsync(SearchPath, new Dictionary<string, object?>
        {
            ["pageSize"] = Math.Clamp(pageSize, 1, 20),
            ["currentPage"] = 1,
            ["offset"] = 0,
            ["query"] = keyword ?? "",
        }, ct: ct);

        if (resp.ErrCode != 0)
        {
            throw new InvalidOperationException(
                $"视频号 @好友搜索失败（errCode={resp.ErrCode}）：{resp.ErrMsg ?? "平台未给出原因"}");
        }

        var users = ParseUsers(resp.Data);
        Log.Debug("[WechatVideoMention] helper_search_finder_account 「{Keyword}」→ {Count} 人",
            keyword, users.Count);
        return users;
    }

    /// <summary>
    /// <c>data.list[]</c> → 候选（纯映射，公开 static 供自检钉字段）。
    /// 缺昵称的条目跳过（提交体只认昵称）；同一 username 只留一条。
    /// 字段名兼容 <c>nickName</c>（抓包原样）与 <c>nickname</c>（防御）。
    /// </summary>
    public static List<WechatVideoMentionUser> ParseUsers(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("list", out var list)
            || list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var users = new List<WechatVideoMentionUser>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var el in list.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;

            var nickname = Str(el, "nickName") ?? Str(el, "nickname");
            if (string.IsNullOrWhiteSpace(nickname)) continue;

            var username = Str(el, "username");
            var key = string.IsNullOrWhiteSpace(username) ? nickname : username;
            if (!seen.Add(key)) continue;

            users.Add(new WechatVideoMentionUser(
                nickname,
                Nz(username),
                Nz(Str(el, "headImgUrl")),
                Nz(Str(el, "authCompanyName"))));
        }
        return users;
    }

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()?.Trim()
            : null;

    /// <summary>空串 / 空白统一成 null（展示字段不留空字符串，界面判断只看 null）。</summary>
    private static string? Nz(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
