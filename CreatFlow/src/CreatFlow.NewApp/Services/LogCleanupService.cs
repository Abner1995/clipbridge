namespace CreatFlow.NewApp.Services;

/// <summary>一次清理的结果（页面状态行与冒烟断言用）。</summary>
public sealed record LogCleanupResult(int Deleted, int Skipped, long Bytes)
{
    /// <summary>用户可读的一句话总结</summary>
    public string Summary =>
        Deleted == 0 && Skipped == 0
            ? "没有超过保留期的日志"
            : Skipped == 0
                ? $"已清理 {Deleted} 个文件（{FormatBytes(Bytes)}）"
                : $"已清理 {Deleted} 个文件（{FormatBytes(Bytes)}），{Skipped} 个正被占用，下次再试";

    /// <summary>字节（B）—— 只需「几 KB / 几 MB」量级，不做更细的单位</summary>
    internal static string FormatBytes(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024.0 / 1024.0:0.#} MB"
        : bytes >= 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes} B";
}

/// <summary>
/// 日志清理的编排：真实路径组合（logs 目录 / crashes 目录 / 桌面遗留调试日志）+ 读设置 +
/// 汇总结果。纯逻辑在 <see cref="LogCleanup"/>（自检钉行为），这里只负责「在哪儿跑、跑多大」。
///
/// 触发点三处（改任何一处前先读这里）：启动后台跑一次（App.axaml.cs，与登录态无关的
/// 本机卫生）、账号设置页改保留期立即跑、页面「立即清理」按钮手动跑。
///
/// <paramref name="root"/> 覆写 = 自检/冒烟的 TEMP 夹具（与 AccountSelectionStore(root)
/// 同款手法）；覆写时桌面遗留日志也落在 root 下（同样的代码路径，只是换了根）。
/// </summary>
public sealed class LogCleanupService
{
    private readonly LogSettingsStore _settings;
    private readonly string _logsDir;
    private readonly string _crashesDir;
    private readonly string _desktopDebugLog;

    public LogCleanupService(LogSettingsStore settings, string? root = null)
    {
        _settings = settings;
        _logsDir = Path.Combine(root ?? LocalJsonFile.DefaultRoot, "logs");
        _crashesDir = Path.Combine(root ?? LocalJsonFile.DefaultRoot, "crashes");
        _desktopDebugLog = root is null
            ? LogCleanup.DefaultDesktopDebugLog
            : Path.Combine(root, "creatflow_debug.log");
    }

    /// <summary>日志根目录（冒烟 TEMP 根守卫断言用）</summary>
    public string LogsDir => _logsDir;

    /// <summary>按设置的保留天数跑一轮清理（三处合计）。</summary>
    public async Task<LogCleanupResult> RunAsync(CancellationToken ct = default)
    {
        var days = (await _settings.LoadAsync(ct)).RetentionDays;
        var cutoff = DateTime.Now.AddDays(-days);

        var (d1, s1, b1) = LogCleanup.PurgeDir(_logsDir, "*.log", cutoff);
        var (d2, s2, b2) = LogCleanup.PurgeDir(_crashesDir, "crash_*.log", cutoff);
        var (d3, b3) = LogCleanup.PurgeFileIfOlder(_desktopDebugLog, cutoff);

        return new LogCleanupResult(d1 + d2 + (d3 ? 1 : 0), s1 + s2, b1 + b2 + b3);
    }

    /// <summary>当前占用（logs + crashes + 桌面遗留文件），账号设置页显示用。不抛。</summary>
    public (int Files, long Bytes) Measure()
    {
        var (f1, b1) = LogCleanup.MeasureDir(_logsDir);
        var (f2, b2) = LogCleanup.MeasureDir(_crashesDir);
        long f3 = 0, b3 = 0;
        try
        {
            var info = new FileInfo(_desktopDebugLog);
            if (info.Exists) { f3 = 1; b3 = info.Length; }
        }
        catch { /* 读不到就不计 */ }
        return ((int)(f1 + f2 + f3), b1 + b2 + b3);
    }
}
