namespace CreatFlow.Domain.Entities;

/// <summary>
/// 通知发送日志 — 每条记录 = 一次通知尝试。
///
/// 用途：按 (member_id + type + 日期) 去重，防止同一天重复通知。
/// 钉钉发送失败时不标记 is_sent，下次 Worker 轮询时重试。
/// </summary>
public class NotificationLog : Aggregate<NotificationLogId>
{
    /// <summary>接收通知的会员 ID（无 FK）</summary>
    public MemberId MemberId { get; private set; } = default!;

    /// <summary>通知渠道：dingtalk / wecom / email</summary>
    public string Channel { get; private set; } = "dingtalk";

    /// <summary>通知类型：expire_soon / expired</summary>
    public string Type { get; private set; } = default!;

    /// <summary>通知内容摘要（如 "会员 xxx 将于 3 天后到期"）</summary>
    public string? Detail { get; private set; }

    /// <summary>是否发送成功</summary>
    public bool IsSent { get; private set; }

    /// <summary>发送失败时的错误信息</summary>
    public string? ErrorMsg { get; private set; }

    /// <summary>发送成功时间</summary>
    public DateTime? SentAt { get; private set; }

    private NotificationLog() { }

    /// <summary>
    /// 创建一条通知日志（尚未发送）。
    /// </summary>
    public static NotificationLog Create(
        MemberId memberId,
        string type,
        string channel = "dingtalk",
        string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);

        var now = Clock.Now;
        return new NotificationLog
        {
            Id = NotificationLogId.New(),
            MemberId = memberId,
            Channel = channel,
            Type = type,
            Detail = detail,
            IsSent = false,
            CreatedAt = now,
            LastModified = now
        };
    }

    /// <summary>标记发送成功。</summary>
    public void MarkSent()
    {
        IsSent = true;
        SentAt = Clock.Now;
        LastModified = Clock.Now;
    }

    /// <summary>标记发送失败。</summary>
    public void MarkFailed(string errorMsg)
    {
        IsSent = false;
        ErrorMsg = errorMsg;
        LastModified = Clock.Now;
    }
}
