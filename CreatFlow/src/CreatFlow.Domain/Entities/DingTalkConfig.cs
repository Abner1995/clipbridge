namespace CreatFlow.Domain.Entities;

using CreatFlow.Domain.Events;

/// <summary>
/// 钉钉通知配置聚合根。
/// 每个会员最多一条配置，记录钉钉群机器人的 Webhook Key 和加签 Secret。
/// </summary>
public class DingTalkConfig : Aggregate<DingTalkConfigId>
{
    /// <summary>所属会员ID（无FK，应用层校验）</summary>
    public MemberId MemberId { get; private set; } = default!;

    /// <summary>钉钉机器人 access_token（从 Webhook URL 提取）</summary>
    public string WebhookKey { get; private set; } = default!;

    /// <summary>加签 Secret（HMAC-SHA256 签名用）</summary>
    public string Secret { get; private set; } = default!;

    private DingTalkConfig() { }

    /// <summary>
    /// 创建新的钉钉通知配置。
    /// </summary>
    public static DingTalkConfig Create(
        DingTalkConfigId id,
        MemberId memberId,
        string webhookKey,
        string secret)
    {
        ArgumentNullException.ThrowIfNull(memberId);
        ArgumentException.ThrowIfNullOrWhiteSpace(webhookKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        var now = Clock.Now;
        var config = new DingTalkConfig
        {
            Id = id,
            MemberId = memberId,
            WebhookKey = webhookKey.Trim(),
            Secret = secret.Trim(),
            CreatedAt = now,
            LastModified = now
        };

        config.AddDomainEvent(new DingTalkConfigCreatedEvent(id, memberId));

        return config;
    }

    /// <summary>
    /// 更新 Webhook Key 和 Secret。
    /// </summary>
    public void Update(string webhookKey, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(webhookKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        WebhookKey = webhookKey.Trim();
        Secret = secret.Trim();
        LastModified = Clock.Now;

        AddDomainEvent(new DingTalkConfigUpdatedEvent(Id, MemberId));
    }
}
