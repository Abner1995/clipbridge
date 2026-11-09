using CreatFlow.NewApp.Models;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 账号分组服务端调用集中点（整表替换语义；路径字符串只出现在本文件）：
///
///   list = 拉云端全量（云端权威；失败/未登录/离线 → null，调用方保留本地缓存）
///   save = 整表推送（服务端按 ID diff：更新/新增/删除，一次事务）
///
/// 与 PlatformSessionApi 同款：ApiClient 网络错误返回 default 不抛，
/// 但普通 401/404/5xx 抛 ApiException —— 容错由调用方（AccountGroupCloudSync）负责。
/// </summary>
public static class AccountGroupApi
{
    /// <summary>拉云端分组（Id 已归一 "N" 格式）；失败/未登录/离线返回 null。</summary>
    public static async Task<List<AccountGroup>?> ListAsync()
    {
        var resp = await ApiClient.PostAsync<AccountGroupListResponse>("/api/account-groups/list");
        return resp is null ? null : MapFromPull(resp);
    }

    /// <summary>整表推送；服务端已收下返回 true（dirty 才可清）。</summary>
    public static async Task<bool> SaveAsync(IReadOnlyList<AccountGroup> groups)
    {
        var items = MapForPush(groups, out _);
        var resp = await ApiClient.PostAsync<SaveAccountGroupsResponse>(
            "/api/account-groups/save", new SaveAccountGroupsRequest(items));
        return resp?.Ok == true;
    }

    // ═══ 纯映射（自检直接钉）═══

    /// <summary>推送映射：本地 "N" → Guid；解析失败的行跳过并计数（本地数据不会出现，防御）。</summary>
    internal static List<AccountGroupItem> MapForPush(IReadOnlyList<AccountGroup> groups, out int skipped)
    {
        var items = new List<AccountGroupItem>(groups.Count);
        skipped = 0;
        foreach (var g in groups)
        {
            if (!Guid.TryParse(g.Id, out var id)) { skipped++; continue; }
            items.Add(new AccountGroupItem(id, g.Name, [.. g.Sessions]));
        }
        return items;
    }

    /// <summary>拉取映射：Guid → "N"；数组序 = 组序（服务端已按 sort_order 返回）。</summary>
    internal static List<AccountGroup> MapFromPull(AccountGroupListResponse resp) =>
        (resp.Groups ?? []).Select(g => new AccountGroup
        {
            Id = g.Id.ToString("N"),
            Name = g.Name ?? "",
            Sessions = g.SessionIds ?? [],
        }).ToList();
}
