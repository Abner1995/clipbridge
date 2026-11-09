namespace CreatFlow.Application.Common.Interfaces;

/// <summary>
/// Repository for DingTalk notification configuration.
/// </summary>
public interface IDingTalkConfigRepository
{
    Task<DingTalkConfig?> GetByIdAsync(DingTalkConfigId id, CancellationToken ct = default);
    Task<DingTalkConfig?> GetByMemberIdAsync(MemberId memberId, CancellationToken ct = default);
    Task AddAsync(DingTalkConfig config, CancellationToken ct = default);
    Task UpdateAsync(DingTalkConfig config, CancellationToken ct = default);
    Task DeleteAsync(DingTalkConfig config, CancellationToken ct = default);
    Task<(List<DingTalkConfig> Items, int TotalCount)> GetPagedAsync(
        int page,
        int pageSize,
        string? search = null,
        Guid? memberId = null,
        CancellationToken ct = default);
}
