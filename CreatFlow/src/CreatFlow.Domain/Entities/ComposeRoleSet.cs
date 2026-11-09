namespace CreatFlow.Domain.Entities;

/// <summary>
/// 合成角色库 — 会员的**整份**角色数组（名称 / 台词模板 / 人设提示词 / 知识库），
/// 每会员**一行**、整表替换（与桌面端本地 compose_roles.json 一一对应）。
///
/// 为什么每会员一行而不是一角色一行：客户端有「文件缺失 → 回退内置 2 条示例；显式空库
/// 不复活内置」的语义 —— 云端必须能区分「从未保存过」与「保存过空表」。
/// 按行存两者都是"零行"，区分不了；一行存则「无行 = 从未保存」vs「行在且 roles="[]" = 显式空」。
/// 另：角色 Id 是任意字符串（builtin-*/Guid），不是 Guid，分组那套 ID diff + clash 检查不适用。
///
/// RolesJson 与 session_ids / local_storage 同口径：JSON 字符串（序列化在 Application 层做），
/// 空库写 "[]" 绝不写 null。
/// </summary>
public class ComposeRoleSet : Aggregate<ComposeRoleSetId>
{
    /// <summary>所属会员</summary>
    public Guid MemberId { get; private set; }

    /// <summary>角色数组（JSON；空库为 "[]"，绝不写 null）</summary>
    public string RolesJson { get; private set; } = "[]";

    // ── ctor ──

    private ComposeRoleSet() { }

    // ═══ Factory ═══

    public static ComposeRoleSet Create(ComposeRoleSetId id, Guid memberId, string rolesJson)
    {
        var now = Clock.Now;
        return new ComposeRoleSet
        {
            Id = id,
            MemberId = memberId,
            RolesJson = rolesJson,
            CreatedAt = now,
            LastModified = now,
        };
    }

    // ═══ Behaviors ═══

    /// <summary>整表替换（桌面端每次改动推送全量角色数组）</summary>
    public void Update(string rolesJson)
    {
        RolesJson = rolesJson;
        LastModified = Clock.Now;
    }
}
