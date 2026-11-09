namespace CreatFlow.NewApp.Services;

/// <summary>
/// 日志按天清理的**纯函数核心**（自检直接对 TEMP 夹具钉行为，组合真实路径的编排在
/// <see cref="LogCleanupService"/>）。
///
/// 既往问题的背景：三个 Serilog 都只有**文件数**上限（retainedFileCountLimit），
/// 没有按天清理 —— 老 App 崩溃日志（crashes\crash_*.log）是「上传成功才删」，传不上去就
/// 永久堆积。2026-10-14 用户要求「保留最近 N 天」。
///
/// 两条纪律：
///  · **永不抛**：清理是打扫卫生，任何文件删不掉（被占用/权限）都只跳过，
///    绝不能让启动流程或页面命令炸掉。当前正在写的日志文件被 Serilog 持有 ——
///    那是**预期**的跳过路径，不是异常。
///  · **严格早于 cutoff 才删**：mtime == cutoff 保留（「保留最近 N 天」的字面语义）。
/// </summary>
public static class LogCleanup
{
    /// <summary>本机日志目录。NewApp 的 <c>log-newapp-*.log</c> 与老 App 的 <c>log-*.log</c>
    /// 同住这里（CLAUDE.md 有记）——用 <c>*.log</c> 通配一网打尽，两个前缀都覆盖。</summary>
    public static string DefaultLogsDir { get; } = Path.Combine(LocalJsonFile.DefaultRoot, "logs");

    /// <summary>崩溃记录目录（老 App CrashReporter 写 crash_*.log；NewApp 无崩溃落盘）</summary>
    public static string DefaultCrashesDir { get; } = Path.Combine(LocalJsonFile.DefaultRoot, "crashes");

    /// <summary>
    /// 桌面遗留调试日志（老版 DouyinCollectionService.LogToFile 的无界追加，写在用户桌面上）。
    /// 源头已于 2026-10-14 收口到 Serilog（见 DouyinCollectionService.LogToFile），
    /// 这里只负责把存量扫掉 —— 文件还在被写时 mtime 是新的，天然不在删除范围。
    /// </summary>
    public static string DefaultDesktopDebugLog { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        "creatflow_debug.log");

    /// <summary>
    /// 删 <paramref name="dir"/> 下匹配 <paramref name="pattern"/> 且最后写入时间早于
    /// <paramref name="cutoff"/> 的文件。目录不存在 / 单文件被占用 → 跳过，永不抛。
    /// </summary>
    /// <returns>删除数 / 跳过数（被占用等）/ 删除字节数（UI 文案用）</returns>
    public static (int Deleted, int Skipped, long Bytes) PurgeDir(
        string dir, string pattern, DateTime cutoff)
    {
        var deleted = 0;
        var skipped = 0;
        long bytes = 0;

        string[] files;
        try
        {
            if (!Directory.Exists(dir)) return (0, 0, 0);
            files = Directory.GetFiles(dir, pattern);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[LogCleanup] 列举 {Dir} 失败，跳过", dir);
            return (0, 0, 0);
        }

        foreach (var file in files)
        {
            try
            {
                var info = new FileInfo(file);
                if (info.LastWriteTime >= cutoff) continue;   // 严格早于才删
                var len = info.Length;
                info.Delete();
                deleted++;
                bytes += len;
            }
            catch (Exception ex)
            {
                // 被占用（正在写的日志）/ 权限不足：预期路径，静默跳过
                skipped++;
                Log.Debug(ex, "[LogCleanup] 跳过 {File}", Path.GetFileName(file));
            }
        }
        return (deleted, skipped, bytes);
    }

    /// <summary>单文件版（桌面遗留调试日志）：早于 cutoff 才删。</summary>
    /// <returns>是否真删了 + 删掉的字节数（空文件也要算「删了 1 个」——不能用字节数判断）</returns>
    public static (bool Deleted, long Bytes) PurgeFileIfOlder(string path, DateTime cutoff)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.LastWriteTime >= cutoff) return (false, 0);
            var len = info.Length;
            info.Delete();
            return (true, len);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[LogCleanup] 跳过 {File}", Path.GetFileName(path));
            return (false, 0);
        }
    }

    /// <summary>目录占用（账号设置页「当前占用」一行）。不抛。</summary>
    public static (int Files, long Bytes) MeasureDir(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return (0, 0);
            var files = Directory.GetFiles(dir, "*.log");
            long bytes = 0;
            foreach (var f in files)
            {
                try { bytes += new FileInfo(f).Length; } catch { /* 单文件读不到就不计 */ }
            }
            return (files.Length, bytes);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[LogCleanup] 统计 {Dir} 失败", dir);
            return (0, 0);
        }
    }
}
