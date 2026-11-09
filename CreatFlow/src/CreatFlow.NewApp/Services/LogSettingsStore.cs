namespace CreatFlow.NewApp.Services;

/// <summary>
/// 日志保留设置的存档形状（一份文件一个数字，2026-10-14 用户需求「日志自动清理，保留期可设置」）。
/// </summary>
public sealed record LogSettingsData
{
    /// <summary>日志/崩溃记录保留天数（默认 30；设置卡现给 15/30 两档）</summary>
    public int RetentionDays { get; init; } = LogSettingsStore.DefaultRetentionDays;
}

/// <summary>
/// 日志保留天数的本地持久化（账号设置页「日志与存储」卡读写；清理逻辑在 <see cref="LogCleanup"/>）。
///
/// 与 <see cref="AccountSelectionStore"/> 同规矩：LocalJsonFile 整文件覆写 + 读失败一律按默认，
/// 不阻断进页面。默认 = 30 天（用户拍板的默认值）。
/// </summary>
public sealed class LogSettingsStore
{
    /// <summary>默认保留期（用户拍板：默认 30 天，可改 15 天）</summary>
    public const int DefaultRetentionDays = 30;

    private const string FileName = "log-settings.json";

    private readonly LocalJsonFile _io;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LogSettingsStore(string? root = null)
        => _io = new LocalJsonFile(root ?? LocalJsonFile.DefaultRoot, "LogSettings");

    public string GetConfigPath() => _io.PathOf(FileName);

    /// <summary>
    /// 读设置。文件缺失/损坏 → 默认 30；**非法天数（手改成 0/-5/9999）也退默认** ——
    /// 不钳制到边界：钳出来的「9999 → 3650」是静默修正，用户看到的是没生效的错觉；
    /// 退默认（界面上还会显示实际生效值）更诚实。
    /// </summary>
    public async Task<LogSettingsData> LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var data = await _io.ReadAsync<LogSettingsData>(_io.PathOf(FileName), ct) ?? new();
            return data.RetentionDays is >= 1 and <= 3650
                ? data
                : data with { RetentionDays = DefaultRetentionDays };
        }
        finally { _gate.Release(); }
    }

    /// <summary>整份覆盖写（改一次保留期写一次，没有并发面）。</summary>
    public async Task SaveAsync(LogSettingsData data, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { await _io.WriteAsync(_io.PathOf(FileName), data, ct); }
        finally { _gate.Release(); }
    }
}
