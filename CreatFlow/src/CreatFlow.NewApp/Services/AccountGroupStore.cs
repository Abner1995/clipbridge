namespace CreatFlow.NewApp.Services;

/// <summary>一个账号分组（组名 + 成员会话 ID）。同一账号可属多组；组序 = 列表序。</summary>
public sealed record AccountGroup
{
    /// <summary>组 ID（Guid "N" 格式字符串；与 <see cref="AccountGroups.AllGroupsId"/> 同域比较）</summary>
    public string Id { get; init; } = "";

    public string Name { get; init; } = "";

    /// <summary>成员会话 ID。可能含已解绑的悬空 Id —— 消费侧一律与在册集合取交
    /// （<see cref="AccountGroups.Members"/>），平台页加载时才顺手剪除。</summary>
    public List<Guid> Sessions { get; init; } = [];
}

/// <summary><see cref="AccountGroupStore"/> 的存档形状（一份文件一张表）。</summary>
public sealed record AccountGroupsData
{
    public List<AccountGroup> Groups { get; init; } = [];

    /// <summary>有未推送云端的本地改动（拉取时先推；推送成功才清位）。旧档缺该字段默认 false。</summary>
    public bool Dirty { get; init; }
}

/// <summary>
/// 账号分组的本地持久化（**会员级**：accounts/{memberId:N}/account-groups.json，2026-10-15 上云）。
///
/// 与 sessions.json 同一「微信式会员隔离」模型（PlatformPaths）：换会员登录自动换目录
/// 「换号不串库」—— 此前机器级共享一份时，平台页剪除会误删另一会员的分组成员。
///
/// 云端为权威（<see cref="AccountGroupCloudSync"/>），本文件是**缓存 + dirty 旗标**
/// （有未推送改动）。旧版机器级 account-groups.json（2026-10-14 那版）由 <see cref="LoadAsync"/>
/// 一次性采纳：升级后**第一个登录的会员**整份认领（无法事前判定归属，接受），
/// 采纳后标 Dirty，随首次推送上云；未登录（Guid.Empty）不适配，懒到下次登录。
/// </summary>
public sealed class AccountGroupStore
{
    private const string FileName = "account-groups.json";

    private readonly LocalJsonFile _io;
    private readonly Func<Guid> _memberId;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="root">存储根（默认 %LocalAppData%\CreatFlow；冒烟传 TEMP 根）</param>
    /// <param name="memberId">当前会员提供者（DI 传 SessionContext；默认 Guid.Empty = no-member ——
    /// 自检/无 DI 场景确定性落该目录，且不触碰用户 token）</param>
    public AccountGroupStore(string? root = null, Func<Guid>? memberId = null)
    {
        _io = new LocalJsonFile(root ?? LocalJsonFile.DefaultRoot, "AccountGroups");
        _memberId = memberId ?? (() => Guid.Empty);
    }

    /// <summary>当前会员 ID（服务层换号检测用）</summary>
    public Guid CurrentMemberId => _memberId();

    /// <summary>当前会员的存档路径（冒烟 TEMP 根守卫 / 测试用）</summary>
    public string GetConfigPath() => MemberPath(CurrentMemberId);

    /// <summary>旧版机器级存档路径（一次性采纳的来源；冒烟清档要一并清）</summary>
    public string GetLegacyPath() => _io.PathOf(FileName);

    private string MemberPath(Guid memberId) => _io.PathOf(Path.Combine(
        "accounts", memberId == Guid.Empty ? "no-member" : memberId.ToString("N"), FileName));

    /// <summary>读存档。文件缺失/损坏 → 空表（没分组 = 组条整条不出现）。</summary>
    public async Task<AccountGroupsData> LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var memberId = _memberId();
            var path = MemberPath(memberId);
            var data = await _io.ReadAsync<AccountGroupsData>(path, ct);
            if (data is not null) return Normalize(data);

            return await AdoptLegacyAsync(memberId, path, ct) ?? new();
        }
        finally { _gate.Release(); }
    }

    /// <summary>整份覆盖写（增删改组各一次，规模十几条）。memberId 显式锁定归属会员 ——
    /// 服务层在入队时锁定，排队落盘期间换会员也不写错目录。</summary>
    public async Task SaveAsync(AccountGroupsData data, Guid? memberId = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { await _io.WriteAsync(MemberPath(memberId ?? _memberId()), data, ct); }
        finally { _gate.Release(); }
    }

    /// <summary>组 Id 统一成 Guid "N"（云端往返可能带 "D" 形式；不归一同一组会长出两条）。</summary>
    private static AccountGroupsData Normalize(AccountGroupsData data)
    {
        var src = data.Groups ?? [];
        var groups = new List<AccountGroup>(src.Count);
        foreach (var g in src)
        {
            if (Guid.TryParse(g.Id, out var id) && g.Id != id.ToString("N"))
                groups.Add(g with { Id = id.ToString("N") });
            else
                groups.Add(g);
        }
        return data with { Groups = groups };
    }

    /// <summary>旧档采纳（一次性）：会员档缺失 && member != Empty && 旧机器级档非空 →
    /// 写入会员档（标 Dirty）→ 确认落盘后才删旧档。member 为空不适配（未登录看不到分组页）。</summary>
    private async Task<AccountGroupsData?> AdoptLegacyAsync(Guid memberId, string memberPath, CancellationToken ct)
    {
        if (memberId == Guid.Empty) return null;

        var legacyPath = _io.PathOf(FileName);
        var legacy = await _io.ReadAsync<AccountGroupsData>(legacyPath, ct);
        if (legacy is null || (legacy.Groups?.Count ?? 0) == 0) return null;

        var adopted = Normalize(legacy) with { Dirty = true };
        await _io.WriteAsync(memberPath, adopted, ct);
        if (File.Exists(memberPath))
        {
            try { File.Delete(legacyPath); } catch { /* 删不掉留着：下次会员档已存在，不再走这里 */ }
            Log.Information("[AccountGroups] 旧机器级存档已采纳到会员 {Member}（{Count} 组，待推云端）",
                memberId, adopted.Groups.Count);
        }
        return adopted;
    }
}
