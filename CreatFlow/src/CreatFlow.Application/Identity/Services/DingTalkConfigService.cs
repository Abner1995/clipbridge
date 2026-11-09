namespace CreatFlow.Application.Identity.Services;

/// <summary>
/// Application service for DingTalk notification configuration.
/// </summary>
public class DingTalkConfigService
{
    private readonly IDingTalkConfigRepository _repository;
    private readonly IMemberRepository _memberRepository;

    public DingTalkConfigService(
        IDingTalkConfigRepository repository,
        IMemberRepository memberRepository)
    {
        _repository = repository;
        _memberRepository = memberRepository;
    }

    /// <summary>
    /// Get the current member's DingTalk config.
    /// Returns null if not configured.
    /// </summary>
    public async Task<DingTalkConfigDto?> GetByMemberAsync(MemberId memberId, CancellationToken ct = default)
    {
        var config = await _repository.GetByMemberIdAsync(memberId, ct);
        return config == null ? null : ToDto(config, memberId);
    }

    /// <summary>
    /// Create or update DingTalk config for a member.
    /// </summary>
    public async Task<DingTalkConfigDto> SaveAsync(
        MemberId memberId, string webhookKey, string secret, CancellationToken ct = default)
    {
        // 校验成员存在（应用层引用完整性）
        var member = await _memberRepository.GetByIdAsync(memberId, ct);
        if (member == null)
            throw new NotFoundException("会员不存在");

        var existing = await _repository.GetByMemberIdAsync(memberId, ct);

        if (existing != null)
        {
            existing.Update(webhookKey, secret);
            await _repository.UpdateAsync(existing, ct);
            return ToDto(existing, memberId);
        }

        var config = DingTalkConfig.Create(
            DingTalkConfigId.New(),
            memberId,
            webhookKey,
            secret);

        await _repository.AddAsync(config, ct);
        return ToDto(config, memberId);
    }

    /// <summary>
    /// Delete the member's own config.
    /// </summary>
    public async Task DeleteAsync(DingTalkConfigId id, MemberId memberId, CancellationToken ct = default)
    {
        var config = await _repository.GetByIdAsync(id, ct);
        if (config == null)
            throw new NotFoundException("配置不存在");

        if (config.MemberId.Value != memberId.Value)
            throw new ForbiddenException("无权删除此配置");

        config.AddDomainEvent(new DingTalkConfigDeletedEvent(id, config.MemberId));
        await _repository.DeleteAsync(config, ct);
    }

    /// <summary>
    /// Send a test message to the member's DingTalk group.
    /// </summary>
    public async Task<(bool Success, string? Error)> TestAsync(MemberId memberId, CancellationToken ct = default)
    {
        var config = await _repository.GetByMemberIdAsync(memberId, ct);
        if (config == null)
            return (false, "尚未配置钉钉通知");

        return await DingTalk.DingTalkClient.SendTextAsync(
            config.WebhookKey,
            config.Secret,
            "🎉 这是一条来自 CreatFlow 的测试消息！钉钉群机器人配置成功，您将在这里收到发布通知。");
    }

    /// <summary>
    /// Admin: get all configs (paginated, with member usernames).
    /// </summary>
    public async Task<(List<DingTalkConfigDto> Items, int TotalCount)> GetPagedForAdminAsync(
        int page, int pageSize, string? search, Guid? memberId = null, CancellationToken ct = default)
    {
        var (items, total) = await _repository.GetPagedAsync(page, pageSize, search, memberId, ct);

        var dtos = new List<DingTalkConfigDto>();
        foreach (var item in items)
        {
            var member = await _memberRepository.GetByIdAsync(item.MemberId, ct);
            var username = member?.Username.Value ?? "(已删除)";
            dtos.Add(new DingTalkConfigDto(
                item.Id.Value,
                item.MemberId.Value,
                username,
                item.WebhookKey,
                item.Secret,
                MaskSecret(item.Secret),
                item.CreatedAt ?? DateTime.MinValue,
                item.LastModified ?? DateTime.MinValue));
        }

        return (dtos, total);
    }

    /// <summary>
    /// Admin: delete any config (super admin only).
    /// </summary>
    public async Task AdminDeleteAsync(DingTalkConfigId id, CancellationToken ct = default)
    {
        var config = await _repository.GetByIdAsync(id, ct);
        if (config == null)
            throw new NotFoundException("配置不存在");

        config.AddDomainEvent(new DingTalkConfigDeletedEvent(id, config.MemberId));
        await _repository.DeleteAsync(config, ct);
    }

    /// <summary>
    /// Admin: batch delete configs (super admin only).
    /// </summary>
    public async Task<int> AdminBatchDeleteAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default)
    {
        var deleted = 0;
        foreach (var id in ids)
        {
            var config = await _repository.GetByIdAsync(DingTalkConfigId.Of(id), ct);
            if (config == null) continue;
            config.AddDomainEvent(new DingTalkConfigDeletedEvent(config.Id, config.MemberId));
            await _repository.DeleteAsync(config, ct);
            deleted++;
        }
        return deleted;
    }

    private static DingTalkConfigDto ToDto(DingTalkConfig config, MemberId memberId)
    {
        return new DingTalkConfigDto(
            config.Id.Value,
            memberId.Value,
            "",
            config.WebhookKey,
            config.Secret,
            MaskSecret(config.Secret),
            config.CreatedAt ?? DateTime.MinValue,
            config.LastModified ?? DateTime.MinValue);
    }

    private static string MaskSecret(string secret)
    {
        if (secret.Length <= 8) return new string('*', secret.Length);
        return secret[..4] + new string('*', secret.Length - 8) + secret[^4..];
    }
}
