using Serilog;

namespace CreatFlow.Platforms.Xiaohongshu;

/// <summary>
/// 小红书隐私设置相关服务 — 好友/粉丝列表 + 用户搜索。
///
/// 用于"不给谁看"(op_type=2) 和"只给谁看"(op_type=3) 的用户选择器。
///
/// 对标 Python:
///   examples/xiaohongshu/helper/friend_fans.py
///   examples/xiaohongshu/helper/note_privacy.py
/// </summary>
public sealed class XhsPrivacyService
{
    private readonly PlatformOptions _options;
    private readonly ILogger<XhsPrivacyService>? _logger;

    public XhsPrivacyService(PlatformOptions options, ILogger<XhsPrivacyService>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// 获取好友/粉丝列表（用于隐私设置的用户选择器）。
    /// GET /api/sns/capa/servicegw/note_privacy/user/friend_fans
    /// </summary>
    /// <param name="cookie">Cookie</param>
    /// <param name="cursor">翻页游标（首次为空）</param>
    /// <param name="size">每页条数</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>用户列表</returns>
    public async Task<List<XhsPrivacyUser>> GetFriendFansAsync(
        string cookie,
        string cursor = "",
        int size = 20,
        CancellationToken ct = default)
    {
        using var client = new XhsApiClient(cookie, _options,
            _logger as ILogger<PlatformHttpClient>);

        try
        {
            var query = new Dictionary<string, string>
            {
                ["cursor"] = cursor,
                ["size"] = size.ToString(),
            };

            var resp = await client.GetEdithRawAsync(
                "/api/sns/capa/servicegw/note_privacy/user/friend_fans", query, ct);

            return ParseUserList(resp);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "XHS friend_fans failed");
            return [];
        }
    }

    /// <summary>
    /// 按关键词搜索用户（用于隐私屏蔽/白名单）。
    /// GET /api/sns/capa/servicegw/note_privacy/search/user
    /// </summary>
    /// <param name="cookie">Cookie</param>
    /// <param name="keyword">搜索关键词</param>
    /// <param name="page">页码（1-based）</param>
    /// <param name="pageSize">每页条数</param>
    /// <param name="searchId">搜索 ID（可选，用于去重追踪）</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>用户列表</returns>
    public async Task<List<XhsPrivacyUser>> SearchUsersAsync(
        string cookie,
        string keyword,
        int page = 1,
        int pageSize = 20,
        string? searchId = null,
        CancellationToken ct = default)
    {
        using var client = new XhsApiClient(cookie, _options,
            _logger as ILogger<PlatformHttpClient>);

        try
        {
            var query = new Dictionary<string, string>
            {
                ["keyword"] = keyword,
                ["page"] = page.ToString(),
                ["page_size"] = pageSize.ToString(),
                ["search_id"] = searchId ?? Guid.NewGuid().ToString("N")[..20],
            };

            var resp = await client.GetEdithRawAsync(
                "/api/sns/capa/servicegw/note_privacy/search/user", query, ct);

            var respText = resp.GetRawText();
            Log.Debug("[XhsPrivacy] raw response len={Len} json={Json}",
                respText.Length, respText.Length > 2000 ? respText[..2000] : respText);

            return ParseUserList(resp);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "XHS user search failed: kw={Kw}", keyword);
            return [];
        }
    }

    // ═══ Helpers ═══

    private static List<XhsPrivacyUser> ParseUserList(JsonElement resp)
    {
        var users = new List<XhsPrivacyUser>();

        // Response: { "data": { "user_list": [...] } }
        if (resp.TryGetProperty("data", out var data) &&
            data.TryGetProperty("user_list", out var usersArr) &&
            usersArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var u in usersArr.EnumerateArray())
            {
                var userId = TryGetString(u, "user_id") ?? TryGetString(u, "id") ?? "";
                var name = TryGetString(u, "nick_name") ?? TryGetString(u, "nickname") ?? TryGetString(u, "name") ?? "";
                var avatar = TryGetString(u, "avatar") ?? TryGetString(u, "image") ?? "";

                if (!string.IsNullOrWhiteSpace(userId))
                    users.Add(new XhsPrivacyUser(userId, name, avatar));
            }
        }

        return users;
    }

    private static string? TryGetString(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString();
        return null;
    }
}

/// <summary>隐私设置中的用户条目。</summary>
public sealed record XhsPrivacyUser(
    string UserId,
    string Nickname,
    string Avatar);
