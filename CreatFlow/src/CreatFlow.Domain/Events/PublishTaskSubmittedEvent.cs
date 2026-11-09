namespace CreatFlow.Domain.Events;

/// <summary>
/// 发布任务已提交事件。
/// </summary>
public sealed record PublishTaskSubmittedEvent(
    PublishTaskId TaskId,
    Guid MemberId,
    int PlatformCount
) : IDomainEvent;
