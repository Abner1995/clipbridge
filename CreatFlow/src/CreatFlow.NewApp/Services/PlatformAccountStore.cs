using System.Text.Json;

namespace CreatFlow.NewApp.Services;

/// <summary>本地镜像登记项（sessions.json 一行 = 一个已绑定平台账号）</summary>
public sealed class LocalAccountRecord
{
    public Guid Id { get; set; }
    public string PlatformKey { get; set; } = "";
    public string? PlatformUid { get; set; }
    public string? PlatformName { get; set; }
    public string? AccountLabel { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// 平台账号本地注册表 —— 微信式本地数据模型 v1(会员级隔离)。
///
///   %LocalAppData%/CreatFlow/
///     token.dat                        （联网版：登录会话；单机版不读写）
///     accounts/{会员Id}/               ← 每个 CreatFlow 会员一套（换号不串库）
///       creatflow.db                   **单机版：会话/发布/合集权威（SQLite）**
///       sessions.json                  本地注册表：已绑定账号**镜像**
///       wv2/{sessionId}/               B 阶段：每账号独立 WebView2 档案（cookie/指纹隔离）
///
/// 主存语义：
///   · 联网版 —— 服务端 `x_platform_sessions` 是主存，json 只是本机镜像；
///   · 单机版 —— **本地 SQLite 是主存**，json 仍是镜像（标签坞 / 档案清理 / 备注展示用）。
///     所以 json 读坏/缺失不得当作「账号全没了」去清 wv2 档案（档案是本机登录态，
///     丢了要用户重新扫码）—— 孤儿清扫在单机版按保守口径处理，见 AccountProfileCleanup。
///
/// 「账号 = 一条浏览器档案」：本地先生成会话 Guid（与服务端 session 一一对应）→
/// 档案目录用该 Guid 命名；换会员登录时目录自动切换（微信多账号同思路）。
/// 路径常量统一走 PlatformPaths（与 AccountProfileCleanup/标签坞共用）。
/// </summary>
public sealed class PlatformAccountStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly Guid _memberId;
    private readonly string _memberDir;

    public PlatformAccountStore(SessionContext session)
    {
        // 未登录(空 MemberId)极少发生：给占位目录，避免写坏他人数据
        _memberId = session.MemberId;
        _memberDir = PlatformPaths.MemberDir(_memberId);
    }

    /// <summary>当前会员 ID（清理扫描用）</summary>
    public Guid MemberId => _memberId;

    /// <summary>当前会员本地数据目录（平台页展示/测试用）</summary>
    public string MemberDir => _memberDir;

    /// <summary>某账号的浏览器档案目录（登录窗 EnvironmentRequested 用它隔离每账号 cookie/指纹）</summary>
    public string ProfileDir(Guid sessionId) =>
        Path.Combine(_memberDir, "wv2", sessionId.ToString("N"));

    private string JsonPath => Path.Combine(_memberDir, "sessions.json");

    /// <summary>读取本地注册表（无文件 → 空表）</summary>
    public List<LocalAccountRecord> Load()
    {
        try
        {
            if (!File.Exists(JsonPath)) return [];
            var json = File.ReadAllText(JsonPath);
            return JsonSerializer.Deserialize<List<LocalAccountRecord>>(json, JsonOptions) ?? [];
        }
        catch
        {
            // 损坏不致命：列表主存不在这份 json 里（联网版=服务端，单机版=SQLite），
            // 下次登录同步会用最新记录重建镜像。**不要**因读坏去清 wv2 档案。
            return [];
        }
    }

    private void Save(List<LocalAccountRecord> list)
    {
        Directory.CreateDirectory(_memberDir);
        File.WriteAllText(JsonPath, JsonSerializer.Serialize(list, JsonOptions));
    }

    /// <summary>登记/更新镜像（登录同步成功后由 VM 调用；B 阶段同步建档案目录）</summary>
    public void Register(Guid id, string platformKey, string? platformUid,
        string? platformName, string? accountLabel)
    {
        var list = Load();
        var existing = list.FirstOrDefault(r => r.Id == id);
        if (existing != null)
        {
            existing.PlatformKey = platformKey;
            existing.PlatformUid = platformUid;
            existing.PlatformName = platformName;
            existing.AccountLabel = accountLabel;
            existing.UpdatedAt = DateTime.Now;
        }
        else
        {
            list.Add(new LocalAccountRecord
            {
                Id = id, PlatformKey = platformKey, PlatformUid = platformUid,
                PlatformName = platformName, AccountLabel = accountLabel,
                UpdatedAt = DateTime.Now
            });
        }
        Save(list);
    }

    /// <summary>
    /// 解绑：移除镜像 + 清掉该账号浏览器档案目录（如存在）。
    ///
    /// 清理升级（标签坞常驻后目录删除必发生在 WebView 可能存活/刚关的场景）：
    /// 调用方应先关闭该账号标签，本方法后台线程等渲染进程退出（锁探测 ≤8s）→
    /// 重试删除（5×1s）→ 仍失败写会员域 marker（wv2/{id:N}.pending-delete），
    /// 由下次启动/开标签前的补删兜底 —— 避免半删损坏 profile 导致登录白屏。
    /// </summary>
    public async Task RemoveAsync(Guid id)
    {
        var list = Load();
        list.RemoveAll(r => r.Id == id);
        Save(list);

        var profileDir = Path.Combine(_memberDir, "wv2", id.ToString("N"));
        if (!Directory.Exists(profileDir)) return;

        try
        {
            await Task.Run(() =>
            {
                if (!AccountProfileCleanup.TryDeleteProfileDir(profileDir))
                    AccountProfileCleanup.WritePendingMarker(profileDir, _memberId);
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Profile dir cleanup failed for {Session}", id);
            AccountProfileCleanup.WritePendingMarker(profileDir, _memberId);
        }
    }
}
