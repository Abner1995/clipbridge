namespace CreatFlow.NewApp.Services;

/// <summary>角色库存档形状（2026-10-15 上云）：角色数组 + 未推送云端的本地改动旗标。</summary>
public sealed record ComposeRolesData
{
    public List<ComposeRole> Roles { get; init; } = [];

    /// <summary>有未推送云端的本地改动（推送成功后清位；旧档采纳时置 true 随首推上云）。</summary>
    public bool Dirty { get; init; }
}

/// <summary>
/// 角色库存档 —— **会员级**（2026-10-15 批次 8）：accounts/{memberId:N}/compose_roles.json
/// —— 与 sessions.json 同「微信式会员隔离」，且是**云端同步的本地缓存**
/// （推拉编排见 <see cref="ComposeRoleCloudSync"/>；Dirty = 未推送旗标）。
///
/// 三条规矩：
///  · **独立文件、绝不进 llm.json** —— 老版 CreatFlow.App 保存 llm.json 时整文件覆写，任何扩展字段都会丢；
///  · 缺失 / 读坏 → 内置默认（<see cref="ComposeRole.Defaults"/>，**只在内存、不落盘**）；
///    合法空列表 `[]` → 空（用户手动删光就删光，不复活内置）；
///  · **旧机器级档（裸数组形状）由升级后第一个登录的会员一次性采纳**；`[]` 也采纳
///    （= 显式空库，保住「删光不复活」语义）并标 Dirty 随首推上云
///    —— 与 <see cref="LocalJsonFile.ReadMemberOrAdoptAsync"/> 的「空旧档只删不采纳」**相反**，
///    因为角色的空有语义（六档没有），勿并入六档矩阵。
/// 冒烟模式在 App 层换 TEMP 根（不碰用户文件）。
/// </summary>
public sealed class ComposeRoleStore
{
    private const string FileName = "compose_roles.json";

    private readonly LocalJsonFile _io;
    private readonly Func<Guid> _memberId;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="root">存储根（默认 %LocalAppData%\CreatFlow；冒烟传 TEMP 根）</param>
    /// <param name="memberId">当前会员提供者（DI 传 SessionContext；默认 Guid.Empty = no-member）</param>
    public ComposeRoleStore(string? root = null, Func<Guid>? memberId = null)
    {
        _io = new LocalJsonFile(root ?? LocalJsonFile.DefaultRoot, "ComposeRoles");
        _memberId = memberId ?? (() => Guid.Empty);
    }

    /// <summary>当前会员 ID（服务层换号检测用）</summary>
    public Guid CurrentMemberId => _memberId();

    /// <summary>当前会员的存档路径（AI 设置页显示 / 冒烟 TEMP 根守卫）</summary>
    public string GetConfigPath() => MemberPath(CurrentMemberId);

    /// <summary>旧版机器级存档路径（一次性采纳的来源；冒烟清档要一并清）</summary>
    public string GetLegacyPath() => _io.PathOf(FileName);

    private string MemberPath(Guid memberId) => _io.PathOf(LocalJsonFile.MemberRelative(memberId, FileName));

    /// <summary>读存档（会员级）。读不到/读坏 → 内置兜底（内存，不落盘）。</summary>
    public async Task<ComposeRolesData> LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var memberId = _memberId();   // 入口锁定：await 期间换号不影响本次读的归属
            var path = MemberPath(memberId);

            var data = await _io.ReadAsync<ComposeRolesData>(path, ct);   // 形状①：包装档（现行）
            if (data is not null) return data;

            // 形状②：裸数组（旧形状被手工拷进会员目录）—— 识别并升级成包装档
            //（必须先于"坏档 → 内置"判定：裸数组读包装档必然失败，不能误判成坏档）
            var bare = await _io.ReadAsync<List<ComposeRole>>(path, ct);
            if (bare is not null) return await AdoptIntoMemberAsync(path, bare, null, "会员档（旧形状裸数组）", ct);

            if (File.Exists(path)) return BuiltinFallback();              // 真坏档 → 内置；**绝不读旧档**

            return await AdoptLegacyAsync(memberId, path, ct) ?? BuiltinFallback();
        }
        finally { _gate.Release(); }
    }

    /// <summary>整份覆盖写。memberId 显式锁定归属会员 —— 服务层入队时锁定，排队落盘期间换会员也不写错目录。</summary>
    public async Task SaveAsync(ComposeRolesData data, Guid? memberId = null, CancellationToken ct = default)
    {
        var path = MemberPath(memberId ?? _memberId());
        await _gate.WaitAsync(ct);
        try { await _io.WriteAsync(path, data, ct); }
        finally { _gate.Release(); }
    }

    // ═══ 采纳 / 兜底 ═══

    /// <summary>旧机器级档采纳（一次性）：非空裸数组或 `[]` 都采纳（空 = 显式空库），标记待推。</summary>
    private async Task<ComposeRolesData?> AdoptLegacyAsync(Guid memberId, string memberPath, CancellationToken ct)
    {
        if (memberId == Guid.Empty) return null;   // 未登录不采纳（懒到下次登录）

        var legacy = await _io.ReadAsync<List<ComposeRole>>(GetLegacyPath(), ct);
        if (legacy is null) return null;           // 旧档不存在/读坏 → 不采纳，旧档原样留
        return await AdoptIntoMemberAsync(memberPath, legacy, GetLegacyPath(), "旧机器级存档", ct);
    }

    private async Task<ComposeRolesData> AdoptIntoMemberAsync(
        string memberPath, List<ComposeRole> roles, string? legacyPathToDelete, string what, CancellationToken ct)
    {
        var adopted = new ComposeRolesData { Roles = roles, Dirty = true };
        await _io.WriteAsync(memberPath, adopted, ct);
        if (!File.Exists(memberPath)) return adopted;   // 没写成功：旧档不动（下次重试）

        if (legacyPathToDelete is not null)
        {
            try { File.Delete(legacyPathToDelete); }
            catch { /* 删不掉留着：会员档已存在，不再走采纳路径 */ }
        }
        Log.Information("[ComposeRoles] {What}已采纳到会员（{Count} 条，待推云端）", what, roles.Count);
        return adopted;
    }

    /// <summary>内置兜底：**只在内存、不落盘**（「缺失 = 显示内置但永不落盘」，用户一保存才以存档为准）。</summary>
    private static ComposeRolesData BuiltinFallback() => new() { Roles = [.. ComposeRole.Defaults], Dirty = false };
}
