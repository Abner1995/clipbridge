using CreatFlow.NewApp.Models;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 角色库服务端调用集中点（每会员一行、整表替换语义；路径字符串只出现在本文件）：
///
///   list = 拉云端角色库（含 exists 标记；失败/未登录/离线 → null，调用方保留本地缓存）
///   save = 整表推送（覆盖该会员那一行；空库发 "roles":[]）
///
/// 与 AccountGroupApi 同款：ApiClient 网络错误返回 default 不抛，
/// 但普通 401/404/5xx 抛 ApiException —— 容错由调用方（ComposeRoleCloudSync）负责。
/// </summary>
public static class ComposeRoleApi
{
    /// <summary>拉云端角色库（exists 区分「从未保存」与「保存过含空库」）；失败/未登录/离线返回 null。</summary>
    public static async Task<ComposeRoleListResponse?> ListAsync()
        => await ApiClient.PostAsync<ComposeRoleListResponse>("/api/compose-roles/list");

    /// <summary>整表推送；服务端已收下返回 true（dirty 才可清）。</summary>
    public static async Task<bool> SaveAsync(IReadOnlyList<ComposeRole> roles)
    {
        var resp = await ApiClient.PostAsync<SaveComposeRolesResponse>(
            "/api/compose-roles/save", new SaveComposeRolesRequest(MapForPush(roles)));
        return resp?.Ok == true;
    }

    // ═══ 纯映射（自检直接钉）═══

    /// <summary>推送映射：字段原样（Id 是任意字符串，无需解析）。</summary>
    internal static List<ComposeRoleItem> MapForPush(IReadOnlyList<ComposeRole> roles) =>
        [.. roles.Select(r => new ComposeRoleItem(r.Id, r.Name, r.Template, r.Persona, r.Knowledge))];

    /// <summary>拉取映射：空字段兜底、**空 Id 行丢弃**（哨兵/坏行不进本地库；服务端本不该给）。</summary>
    internal static List<ComposeRole> MapFromPull(ComposeRoleListResponse resp) =>
        [.. (resp.Roles ?? [])
            .Where(r => !string.IsNullOrEmpty(r.Id))
            .Select(r => new ComposeRole
            {
                Id = r.Id!,
                Name = r.Name ?? "",
                Template = r.Template ?? "",
                Persona = r.Persona ?? "",
                Knowledge = r.Knowledge ?? "",
            })];
}
