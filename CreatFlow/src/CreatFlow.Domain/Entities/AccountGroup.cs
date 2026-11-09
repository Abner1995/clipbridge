namespace CreatFlow.Domain.Entities;

/// <summary>
/// 账号分组 — 会员自定义的一组平台会话（组名 + 会话 ID 列表），跨平台、同一账号可属多组。
///
/// 语义（与桌面端本地 account-groups.json 一一对应）：
///   - 组内成员只存会话 ID（不存平台 key；平台由会话 join 得出），可能含已解绑的悬空 ID
///     —— 消费侧一律与在册集合取交；平台页剪除后再整表回传收敛。
///   - 组序 = sort_order（客户端数组序）；会话序 = session_ids JSON 数组序。
///   - 云端按会员整表替换（save 接口按 ID diff）。
///
/// SessionIdsJson 与 PlatformSession.LocalStorageJson 同口径：值为 JSON 字符串
/// （序列化/反序列化在 Application 层做），不做 List&lt;Guid&gt; 值转换器。
/// </summary>
public class AccountGroup : Aggregate<AccountGroupId>
{
    /// <summary>所属会员</summary>
    public Guid MemberId { get; private set; }

    /// <summary>分组名称（≤100，客户端已拦重名）</summary>
    public string Name { get; private set; } = default!;

    /// <summary>成员会话 ID 数组（JSON；空组为 "[]"，不写 null）</summary>
    public string? SessionIdsJson { get; private set; }

    /// <summary>组序（客户端数组序，组条按它展示）</summary>
    public int SortOrder { get; private set; }

    // ── ctor ──

    private AccountGroup() { }

    // ═══ Factory ═══

    public static AccountGroup Create(
        AccountGroupId id,
        Guid memberId,
        string name,
        string? sessionIdsJson,
        int sortOrder)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(name);

        var now = Clock.Now;
        return new AccountGroup
        {
            Id = id,
            MemberId = memberId,
            Name = name,
            SessionIdsJson = sessionIdsJson,
            SortOrder = sortOrder,
            CreatedAt = now,
            LastModified = now,
        };
    }

    // ═══ Behaviors ═══

    /// <summary>整表替换时的行更新（组名 / 成员 / 组序）</summary>
    public void Update(string name, string? sessionIdsJson, int sortOrder)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        SessionIdsJson = sessionIdsJson;
        SortOrder = sortOrder;
        LastModified = Clock.Now;
    }
}
