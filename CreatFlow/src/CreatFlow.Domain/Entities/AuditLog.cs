namespace CreatFlow.Domain.Entities;

using CreatFlow.Domain.ValueObjects;

/// <summary>
/// 操作日志聚合根。记录管理员、会员和系统的关键操作，只读。
/// actor_id 存储管理员 ID，member_id 存储会员 ID，互斥（其一为 null）。
/// </summary>
public class AuditLog : Aggregate<AuditLogId>
{
    /// <summary>执行操作的管理员 ID（会员/系统操作为 null）</summary>
    public Guid? ActorId { get; private set; }

    /// <summary>执行操作的会员 ID（管理员/系统操作为 null）</summary>
    public Guid? MemberId { get; private set; }

    /// <summary>操作者类型：admin / member / system</summary>
    public string ActorType { get; private set; } = "system";

    /// <summary>操作类型：login / create_member / generate_codes / disable_member 等</summary>
    public string Action { get; private set; } = default!;

    /// <summary>人类可读的操作描述</summary>
    public string Detail { get; private set; } = default!;

    /// <summary>操作时的 IP 地址</summary>
    public string? IpAddress { get; private set; }

    private AuditLog() { }

    public static AuditLog Create(
        AuditLogId id,
        Guid? actorId,
        Guid? memberId,
        string action,
        string detail,
        string? ipAddress = null,
        string actorType = "system")
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(detail);

        return new AuditLog
        {
            Id = id,
            ActorId = actorId,
            MemberId = memberId,
            ActorType = actorType,
            Action = action.ToLowerInvariant(),
            Detail = detail,
            IpAddress = ipAddress,
            CreatedAt = Clock.Now
        };
    }
}
