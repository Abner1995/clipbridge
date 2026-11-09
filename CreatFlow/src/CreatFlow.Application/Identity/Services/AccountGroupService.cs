using System.Text.Json;

namespace CreatFlow.Application.Identity.Services;

/// <summary>
/// 账号分组 — 会员级云端存储（换设备/重装可恢复；本地缓存见桌面端 account-groups.json）。
///
/// 语义 = **按会员整表替换**：桌面端每次改动推送全量列表，服务端按 ID diff
/// （命中更新 / 缺的新增 / 多出的删除），sort_order = 数组序。
/// 所有改动合成**一次 SaveChangesAsync**（EF 单次提交自带事务 → 整表替换原子）。
///
/// ⚠️ 本文件是本仓服务端**第一个 JSON 序列化点**：x_platform_sessions 的
/// local_storage/extra_data 是客户端序列化后的纯透传；这里的 session_ids 数组
/// 由本服务自己 Serialize/Deserialize（读坏行 catch → 空组，不让脏数据 500 整个列表）。
/// </summary>
public class AccountGroupService
{
    // 防御上限（正常用户十几组、单组几十个账号；超限拒绝而非截断）
    private const int MaxGroups = 200;
    private const int MaxSessionsPerGroup = 500;
    private const int MaxNameLength = 100;

    private readonly IAccountGroupRepository _repo;

    public AccountGroupService(IAccountGroupRepository repo) => _repo = repo;

    // ═══ List ═══

    /// <summary>该会员的全部分组（按 sort_order）。</summary>
    public async Task<List<AccountGroupDto>> GetListAsync(
        Guid memberId, CancellationToken ct = default)
    {
        var rows = await _repo.GetByMemberAsync(memberId, ct);
        return rows.Select(ToDto).ToList();
    }

    // ═══ Save（整表替换）⚡ ═══

    /// <summary>
    /// 整表替换该会员的分组。校验失败抛 ValidationException（400）——
    /// 注意 Groups == null **不**当空表清库：客户端删到 0 组时发的是 "groups": []。
    /// </summary>
    public async Task SaveAsync(
        Guid memberId, SaveAccountGroupsRequest request, CancellationToken ct = default)
    {
        if (request.Groups is null)
            throw new ValidationException("缺少 groups 字段");
        if (request.Groups.Count > MaxGroups)
            throw new ValidationException($"分组数量超出上限（{MaxGroups}）");

        // 校验 + 归一化（下标 i 即 sort_order）
        var normalized = new List<(Guid Id, string Name, string SessionIdsJson)>(request.Groups.Count);
        var seen = new HashSet<Guid>();
        foreach (var g in request.Groups)
        {
            if (g is null)
                throw new ValidationException("分组项不能为空");
            if (g.Id == Guid.Empty)
                throw new ValidationException("分组 ID 不能为空");
            if (!seen.Add(g.Id))
                throw new ValidationException($"分组 ID 重复：{g.Id}");

            var name = (g.Name ?? "").Trim();
            if (name.Length == 0)
                throw new ValidationException("分组名称不能为空");
            if (name.Length > MaxNameLength)
                throw new ValidationException($"分组名称过长（>{MaxNameLength} 字）");

            var sessions = (g.SessionIds ?? []).Distinct().ToList();
            if (sessions.Count > MaxSessionsPerGroup)
                throw new ValidationException($"单个分组的账号数超出上限（{MaxSessionsPerGroup}）");

            // 空组写 "[]" 不写 null —— 与读侧 ParseSessions 成对
            normalized.Add((g.Id, name, JsonSerializer.Serialize(sessions)));
        }

        // diff：命中更新 / 缺的新增 / 多出的删除
        var existing = await _repo.GetByMemberAsync(memberId, ct);
        var byId = existing.ToDictionary(r => r.Id.Value);

        var newIds = normalized.Where(x => !byId.ContainsKey(x.Id)).Select(x => x.Id).ToList();
        if (newIds.Count > 0)
        {
            // 同 ID 属他人（随机 Guid 冲突趋零）：不越权占用，按冲突报错
            //（先例 PlatformSessionService「同 ID 属他人…不越权占用」；一次查回不循环）
            var clash = await _repo.GetByIdsAsync(newIds, ct);
            if (clash.Count > 0)
                throw new ValidationException($"分组 ID 冲突：{clash[0].Id.Value}");
        }

        for (var i = 0; i < normalized.Count; i++)
        {
            var (id, name, sessionIdsJson) = normalized[i];
            if (byId.Remove(id, out var row))
                row.Update(name, sessionIdsJson, i);
            else
                _repo.Add(AccountGroup.Create(AccountGroupId.Of(id), memberId, name, sessionIdsJson, i));
        }

        if (byId.Count > 0)
            _repo.RemoveRange(byId.Values);

        await _repo.SaveChangesAsync(ct);
    }

    // ═══ Mappers ═══

    private static AccountGroupDto ToDto(AccountGroup row) => new(
        row.Id.Value,
        row.Name,
        ParseSessions(row.SessionIdsJson));

    /// <summary>会话数组反序列化；坏行（旧数据/手工改坏）按空组。</summary>
    private static List<Guid> ParseSessions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<Guid>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }
}
