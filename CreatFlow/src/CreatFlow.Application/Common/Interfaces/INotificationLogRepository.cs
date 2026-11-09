namespace CreatFlow.Application.Common.Interfaces;

/// <summary>
/// Repository for notification send logs.
/// Used by MembershipNotificationWorker for dedup: same member + type + date → skip.
/// </summary>
public interface INotificationLogRepository
{
    /// <summary>
    /// 检查指定会员今天是否已发送过某类通知。
    /// </summary>
    Task<bool> ExistsAsync(
        MemberId memberId, string type, string channel, DateTime date, CancellationToken ct = default);

    /// <summary>
    /// 添加一条通知日志。
    /// </summary>
    Task AddAsync(NotificationLog log, CancellationToken ct = default);

    /// <summary>
    /// 保存变更。
    /// </summary>
    Task SaveChangesAsync(CancellationToken ct = default);
}
