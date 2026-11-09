using System.Text.Json;

namespace CreatFlow.Application.Identity.Services;

/// <summary>
/// 合成角色库 — 会员级云端存储（换设备/重装可恢复；本地缓存见桌面端 compose_roles.json）。
///
/// 语义 = **每会员一行、整表替换**（不是按角色 ID diff —— 角色 Id 是任意字符串）。
/// 与账号分组的关键差异：**Exists 标记**。客户端有「本地档缺失 → 回退内置示例；显式空
/// 不复活内置」的语义，云端必须区分「从未保存」（无行）与「保存过空表」（行在且 roles="[]"），
/// 否则首拉会洗掉内置示例、或「删光角色」同步不到别的设备。
///
/// ⚠️ 读侧坏 JSON **按 exists=false 处理**（不是 roles=[]）—— roles=[] 等于静默抹掉
/// 用户全部角色，而 exists=false 让客户端保留本地、任一次编辑推送即自愈。
/// </summary>
public class ComposeRoleSetService
{
    // 防御上限（正常用户几条到几十条角色；超限拒绝而非截断）
    private const int MaxRoles = 200;
    private const int MaxIdLength = 64;
    private const int MaxNameLength = 100;
    private const int MaxTemplateLength = 20_000;
    private const int MaxPersonaLength = 50_000;
    private const int MaxKnowledgeLength = 200_000;

    /// <summary>单行 JSON 兜底：防逐字段上限叠加出超大 blob 撞 max_allowed_packet（500 → 推送永久失败）。</summary>
    private const int MaxTotalChars = 400_000;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IComposeRoleSetRepository _repo;

    public ComposeRoleSetService(IComposeRoleSetRepository repo) => _repo = repo;

    // ═══ Get ═══

    /// <summary>该会员的角色库；无行 / 坏形状 → Exists=false（客户端保留本地内置/待推）。</summary>
    public async Task<ComposeRoleSetDto> GetAsync(Guid memberId, CancellationToken ct = default)
    {
        var row = await _repo.GetByMemberAsync(memberId, ct);
        if (row is null) return new(false, []);

        var roles = ParseRoles(row.RolesJson);
        return roles is null
            ? new(false, [])          // 坏形状 = 云端未初始化（绝不静默清空本地）
            : new(true, roles);       // "[]" → (true, []) = 显式空库
    }

    // ═══ Save（整表替换）⚡ ═══

    /// <summary>
    /// 整表替换该会员的角色库。校验失败抛 ValidationException（400）——
    /// 注意 Roles == null **不**当空库清表：客户端删到 0 条时发的是 "roles": []。
    /// </summary>
    public async Task SaveAsync(
        Guid memberId, SaveComposeRolesRequest request, CancellationToken ct = default)
    {
        if (request.Roles is null)
            throw new ValidationException("缺少 roles 字段");
        if (request.Roles.Count > MaxRoles)
            throw new ValidationException($"角色数量超出上限（{MaxRoles}）");

        var normalized = new List<ComposeRoleItemDto>(request.Roles.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var total = 0;
        foreach (var r in request.Roles)
        {
            if (r is null)
                throw new ValidationException("角色项不能为空");

            var id = (r.Id ?? "").Trim();
            if (id.Length == 0)
                throw new ValidationException("角色 ID 不能为空");
            if (id.Length > MaxIdLength)
                throw new ValidationException($"角色 ID 过长（>{MaxIdLength} 字）");
            if (!seen.Add(id))
                throw new ValidationException($"角色 ID 重复：{id}");

            var name = (r.Name ?? "").Trim();
            if (name.Length == 0)
                throw new ValidationException("角色名称不能为空");
            if (name.Length > MaxNameLength)
                throw new ValidationException($"角色名称过长（>{MaxNameLength} 字）");

            var template = (r.Template ?? "").Trim();
            var persona = (r.Persona ?? "").Trim();
            var knowledge = (r.Knowledge ?? "").Trim();
            if (template.Length > MaxTemplateLength)
                throw new ValidationException($"台词模板过长（>{MaxTemplateLength} 字）");
            if (persona.Length > MaxPersonaLength)
                throw new ValidationException($"人设提示词过长（>{MaxPersonaLength} 字）");
            if (knowledge.Length > MaxKnowledgeLength)
                throw new ValidationException($"知识库过长（>{MaxKnowledgeLength} 字）");

            total += id.Length + name.Length + template.Length + persona.Length + knowledge.Length;
            normalized.Add(new ComposeRoleItemDto(id, name, template, persona, knowledge));
        }

        if (total > MaxTotalChars)
            throw new ValidationException($"角色库总长度超出上限（>{MaxTotalChars} 字符）");

        var json = JsonSerializer.Serialize(normalized, JsonOpts);

        var row = await _repo.GetByMemberAsync(memberId, ct);
        if (row is null)
            await _repo.AddAsync(ComposeRoleSet.Create(ComposeRoleSetId.New(), memberId, json), ct);
        else
        {
            row.Update(json);
            await _repo.UpdateAsync(row, ct);
        }
    }

    // ═══ Mappers ═══

    /// <summary>角色数组反序列化；坏形状返回 null（= exists false，见类注释；Application 层无日志，静默按未初始化处理）。</summary>
    private static List<ComposeRoleItemDto>? ParseRoles(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<List<ComposeRoleItemDto>>(json, JsonOpts);
        }
        catch
        {
            return null;
        }
    }
}
