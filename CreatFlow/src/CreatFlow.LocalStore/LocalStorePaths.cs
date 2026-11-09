namespace CreatFlow.LocalStore;

/// <summary>
/// 单机版数据路径单一来源（与 NewApp 的 <c>PlatformPaths</c> 同一套会员目录）：
///
///   %LocalAppData%\CreatFlow\accounts\{memberId:N}\
///     creatflow.db            SQLite 业务库（权威）
///     creatflow.db-wal / -shm
///     backups\                自动备份（VACUUM INTO 快照）
///     sessions.json / wv2\    NewApp 既有本地档（镜像 / 浏览器档案）
///
/// <see cref="AccountsRootOverride"/> 只给 --smoke 用（TEMP 根，绝不碰用户数据）。
/// </summary>
public static class LocalStorePaths
{
    /// <summary>冒烟/自检覆盖根目录；null = 正常 %LocalAppData%</summary>
    public static string? AccountsRootOverride { get; set; }

    /// <summary>默认数据根（%LocalAppData%\CreatFlow\accounts）</summary>
    public static string DefaultAccountsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CreatFlow", "accounts");

    public static string AccountsRoot => AccountsRootOverride ?? DefaultAccountsRoot;

    /// <summary>
    /// 机器级数据根（模型 / 日志 / 崩溃报告，与「会员数据」相对）：
    /// 正常 = <c>%LocalAppData%\CreatFlow</c>；冒烟/自检覆盖时 = 覆盖根本身
    /// （夹具里机器级目录与会员目录同根，<see cref="LocalDataWipe"/> 只删白名单子目录）。
    /// </summary>
    public static string MachineRoot => AccountsRootOverride
        ?? Path.GetDirectoryName(DefaultAccountsRoot.TrimEnd(Path.DirectorySeparatorChar))!;

    /// <summary>本机会员数据目录（固定 ID）</summary>
    public static string MemberDir => Path.Combine(AccountsRoot, LocalIdentity.MemberId.ToString("N"));

    /// <summary>SQLite 数据库文件</summary>
    public static string DatabasePath => Path.Combine(MemberDir, "creatflow.db");

    /// <summary>自动备份目录</summary>
    public static string BackupsDir => Path.Combine(MemberDir, "backups");

    public static string DatabasePathFor(Guid memberId) =>
        Path.Combine(AccountsRoot, memberId.ToString("N"), "creatflow.db");

    /// <summary>SQLite 连接串：5 秒忙等（WAL 下 UI 与后台消费者并发写）</summary>
    public static string ConnectionString(string databasePath) =>
        $"Data Source={databasePath};Default Timeout=5";
}
