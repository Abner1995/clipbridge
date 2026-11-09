namespace CreatFlow.LocalStore;

/// <summary>
/// 本地库启动编排：建目录 → 开 WAL → （升级前）备份 → 迁移 → 种子 → 每日自动备份。
///
/// 纪律：
///   · 迁移失败直接抛出并保留原库 + 备份，**绝不删库重建**（删除入口全在本地库里）；
///   · 备份用 SQLite 官方 <c>VACUUM INTO</c>，得到一致性快照（不能直接拷 .db —— 有 WAL）。
/// </summary>
public static class LocalStoreInitializer
{
    /// <summary>自动备份保留份数（每日 + 升级前各算一类，各自滚动）</summary>
    private const int KeepAutoBackups = 7;

    /// <summary>
    /// 同步初始化（进程启动时调用一次，早于任何页面/消费者）。
    /// 返回实际使用的数据库路径。
    /// </summary>
    public static string EnsureInitialized(string? databasePath = null, string? appVersion = null, Action<Exception>? onError = null)
    {
        var path = databasePath ?? LocalStorePaths.DatabasePath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var db = CreateContext(path);

        // WAL 是数据库级持久属性（写进文件头），只需设一次；忙等在连接串里（Default Timeout）
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");

        if (db.Database.GetPendingMigrations().Any())
        {
            // 老库升级前留一份快照（新库没有可备份的内容）
            if (db.Database.GetAppliedMigrations().Any())
                Backup(db, $"pre-upgrade-{Sanitize(appVersion ?? "unknown")}");

            db.Database.Migrate();
        }

        LocalSeeder.EnsureSeedAsync(db).GetAwaiter().GetResult();
        EnsureDailyBackup(db, onError);

        return path;
    }

    /// <summary>按连接串建一个独立上下文（自检 / 初始化用；业务请求走 DI scope）</summary>
    public static LocalDbContext CreateContext(string databasePath)
    {
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(LocalStorePaths.ConnectionString(databasePath))
            .Options;
        return new LocalDbContext(options);
    }

    /// <summary>
    /// 用快照替换当前库（导入数据）。调用方应已备份当前库并提示用户重启。
    ///
    /// 必须清空连接池：EF/ADO 池里可能还持有旧文件的句柄，Windows 下直接覆盖会失败；
    /// WAL/SHM 也要一并删掉 —— 留着的话下次打开会把旧事务日志回放进新库。
    /// </summary>
    public static void ReplaceDatabase(string databasePath, string snapshotFile)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var dir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        File.Copy(snapshotFile, databasePath, overwrite: true);

        foreach (var sidecar in new[] { databasePath + "-wal", databasePath + "-shm" })
        {
            try { File.Delete(sidecar); } catch { /* 不存在或被占用：下次打开时重建 */ }
        }
    }

    /// <summary>
    /// 立刻做一份 <c>VACUUM INTO</c> 快照，返回备份文件路径。
    /// targetDir 缺省 = 本机会员 backups\；tag 会拼进文件名。
    /// </summary>
    public static string Backup(LocalDbContext db, string tag, string? targetDir = null)
    {
        var dir = targetDir ?? LocalStorePaths.BackupsDir;
        Directory.CreateDirectory(dir);

        var file = Path.Combine(dir, $"{Sanitize(tag)}-{DateTime.Now:yyyyMMdd-HHmmss}.db");
        // 同一秒重复调用：加序号，绝不覆盖已有备份
        var n = 1;
        while (File.Exists(file))
            file = Path.Combine(dir, $"{Sanitize(tag)}-{DateTime.Now:yyyyMMdd-HHmmss}-{n++}.db");

        // 路径里的单引号要转义（SQLite 字符串字面量）；VACUUM INTO 的目标不能已存在。
        // VACUUM INTO 不接受参数占位符，只能拼字符串 —— 路径来自本机固定目录，已转义。
#pragma warning disable EF1002
        var escaped = file.Replace("'", "''");
        db.Database.ExecuteSqlRaw($"VACUUM INTO '{escaped}';");
#pragma warning restore EF1002
        return file;
    }

    /// <summary>距上次自动备份 ≥ 24h 才做；只滚动清理 <paramref name="autoPrefix"/> 开头的旧备份</summary>
    private static void EnsureDailyBackup(LocalDbContext db, Action<Exception>? onError)
    {
        var dir = LocalStorePaths.BackupsDir;
        try
        {
            Directory.CreateDirectory(dir);
            var last = Directory.GetFiles(dir, "auto-*.db")
                .Select(File.GetLastWriteTime)
                .DefaultIfEmpty(DateTime.MinValue)
                .Max();
            if ((DateTime.Now - last).TotalHours < 24) return;

            Backup(db, "auto");
            PruneAutoBackups(dir);
        }
        catch (Exception ex)
        {
            // 备份失败不阻断启动：业务可用性优先，日志/界面由调用方决定是否提示
            onError?.Invoke(ex);
        }
    }

    private static void PruneAutoBackups(string dir)
    {
        var stale = Directory.GetFiles(dir, "auto-*.db")
            .OrderByDescending(File.GetLastWriteTime)
            .Skip(KeepAutoBackups);
        foreach (var f in stale)
        {
            try { File.Delete(f); } catch { /* 被占用就留到下次 */ }
        }
    }

    private static string Sanitize(string s) =>
        string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
