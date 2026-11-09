namespace CreatFlow.Application.DTOs;

/// <summary>
/// DTO for DingTalk notification configuration.
/// </summary>
public sealed record DingTalkConfigDto(
    Guid Id,
    Guid MemberId,
    string MemberUsername,
    string WebhookKey,
    string Secret,           // 原始密钥（管理端明文显示用）
    string SecretMasked,     // 脱敏密钥（桌面端安全展示用）
    DateTime CreatedAt,
    DateTime UpdatedAt
);
