namespace CreatFlow.NewApp.Services;

/// <summary>
/// 会话 → 账号显示名的**唯一口径**：备注 → 平台昵称 → 未命名账号。
///
/// 为什么必须收口（2026-09-20 实测）：库里 `account_label` 大量为 NULL，
/// 昵称都躺在 `platform_name` 里 —— 少写「昵称」这一层回退，界面上就是满屏「未命名账号」。
/// 合集页/上传队列页/视频管理页三处都要这个口径，各写一份迟早漂移。
/// </summary>
public static class AccountLabels
{
    /// <summary>取不到名字时的兜底（会话存在但两处都空）</summary>
    public const string Unnamed = "未命名账号";

    public static string DisplayName(PlatformSessionItem s) =>
        !string.IsNullOrWhiteSpace(s.AccountLabel) ? s.AccountLabel!
        : !string.IsNullOrWhiteSpace(s.PlatformName) ? s.PlatformName!
        : Unnamed;

    /// <summary>会话列表 → `sessionId → 显示名`（空 Id 的占位会话不入表）</summary>
    public static Dictionary<Guid, string> Map(IEnumerable<PlatformSessionItem> sessions) =>
        sessions.Where(s => s.Id != Guid.Empty).ToDictionary(s => s.Id, DisplayName);

    /// <summary>
    /// 查显示名。**返回 null = 这个会话已经不在服务端了**（重登换了新 id、管理端删过会话、
    /// 存量历史数据）。调用方据此显示「账号已解绑」—— 记录上没有账号名快照，事后无从追溯。
    /// （用户端解绑现在直接硬删该账号的全部记录、不再产生这类无主行，上述来源仍需兜底。）
    /// </summary>
    public static string? LabelOf(IReadOnlyDictionary<Guid, string> map, Guid? sessionId) =>
        sessionId is { } id && id != Guid.Empty && map.TryGetValue(id, out var label)
            ? label
            : null;
}
