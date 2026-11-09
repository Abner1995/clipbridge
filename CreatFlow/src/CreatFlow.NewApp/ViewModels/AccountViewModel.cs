using Avalonia.Controls.ApplicationLifetimes;
using CreatFlow.LocalStore;
using Material.Icons;

namespace CreatFlow.NewApp.ViewModels;

/// <summary>
/// 账号设置页 ViewModel（业务自老 App 拷贝：会话信息 + 登出；无 emoji）。
/// 数据源为 SessionContext 单例 —— 订阅 MembershipChanged 即时刷新（激活/续费后无需重进页面）；
/// 登出经壳的 OnLogoutRequested（退出回登录窗）。
/// 过期用户仍可访问本页（导航 AllowedWhenExpired，老 App 语义）——
/// 「日志与存储」卡（2026-10-14 用户需求：日志自动清理）、「视频水印」卡（2026-10-21）与
/// 「上传提示音」卡（2026-10-22）也因此对过期用户可用：清本机磁盘 / 改本机偏好不是会员功能，
/// 不该被激活门卡住。单机版另有「数据与备份」与「清除本机数据」（doc 11.2 / 11.3，
/// 后者要二次确认 + 输入确认词 + 默认先导出，清完强制退出）。
/// </summary>
public partial class AccountViewModel : ViewModelBase, INavigationAware
{
    private readonly SessionContext _session;
    private readonly LogSettingsStore _logSettings;
    private readonly LogCleanupService _cleanup;
    private readonly WatermarkSettingsStore _watermark;
    private readonly SoundSettingsStore _sound;

    public AccountViewModel(SessionContext session, LogSettingsStore logSettings,
        LogCleanupService cleanup, WatermarkSettingsStore watermark, SoundSettingsStore sound)
    {
        _session = session;
        _logSettings = logSettings;
        _cleanup = cleanup;
        _watermark = watermark;
        _sound = sound;
        session.MembershipChanged += _ => RaiseAll();
    }

    public SessionContext Session => _session;

    /// <summary>页头图标（矢量）</summary>
    public MaterialIconKind HeaderIcon => MaterialIconKind.AccountCircle;

    public string Username => _session.Username;
    public string Initial => _session.Initial;
    public bool IsExpired => _session.IsExpired;

    /// <summary>
    /// 页头标题：联网版显示用户名；单机版没有账号概念，显示「本机设置」
    /// （2026-10-28 用户要求，与侧栏导航同名）。
    /// </summary>
    public string HeaderTitle => AppConfig.IsStandalone ? "本机设置" : Username;

    /// <summary>单机版：会员状态/到期/登录 IP 区块整块隐藏（没有这些概念）</summary>
    public bool ShowMembership => !AppConfig.IsStandalone;

    /// <summary>单机版：没有登录，退出登录按钮隐藏</summary>
    public bool ShowLogout => !AppConfig.IsStandalone;

    /// <summary>页头说明：单机版不提激活码，也不重复标题里的「设置」二字</summary>
    public string AccountSubtitle => AppConfig.IsStandalone
        ? "本机日志、视频水印、上传提示音与数据管理"
        : "账号设置 · 会话信息、本机日志、视频水印与上传提示音，如需激活码请前往「激活会员」";

    // ════════════ 数据与备份（单机版 · doc 11.2）════════════

    /// <summary>联网版数据在服务端，不需要本机导出/导入卡片</summary>
    public bool ShowBackup => AppConfig.IsStandalone;

    /// <summary>View 注入：选导出 zip 路径（null = 用户取消）</summary>
    public Func<Task<string?>>? PickExportPath { get; set; }

    /// <summary>View 注入：选导入 zip 文件（null = 用户取消）</summary>
    public Func<Task<string?>>? PickImportPath { get; set; }

    [ObservableProperty] private string _backupStatus = "";
    [ObservableProperty] private bool _backupBusy;
    [ObservableProperty] private bool _backupFailed;
    [ObservableProperty] private bool _isConfirmingImport;

    public bool HasBackupStatus => !string.IsNullOrEmpty(BackupStatus);
    public string BackupStatusHex => BackupFailed ? "#C62828"
        : BackupStatus.StartsWith("已导出", StringComparison.Ordinal)
          || BackupStatus.StartsWith("已导入", StringComparison.Ordinal) ? "#0A5C28" : "#86909C";

    partial void OnBackupStatusChanged(string value)
    {
        OnPropertyChanged(nameof(HasBackupStatus));
        OnPropertyChanged(nameof(BackupStatusHex));
    }

    partial void OnBackupFailedChanged(bool value) => OnPropertyChanged(nameof(BackupStatusHex));
    partial void OnIsConfirmingImportChanged(bool value) => OnPropertyChanged(nameof(ShowImportConfirm));

    /// <summary>导入确认条（覆盖本机数据，必须先摆条）</summary>
    public bool ShowImportConfirm => IsConfirmingImport;

    private void SetBackupStatus(bool ok, string text)
    {
        BackupFailed = !ok;
        BackupStatus = text;
    }

    [RelayCommand]
    private async Task ExportDataAsync()
    {
        if (BackupBusy) return;
        var path = PickExportPath is null ? null : await PickExportPath();
        if (string.IsNullOrWhiteSpace(path)) return;

        BackupBusy = true;
        SetBackupStatus(true, "正在导出…");
        try
        {
            var (file, count) = await LocalBackupService.ExportAsync(path);
            SetBackupStatus(true, $"已导出 {count} 个文件 → {Path.GetFileName(file)}");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Account] 导出数据失败");
            SetBackupStatus(false, "导出失败：" + ex.Message);
        }
        finally
        {
            BackupBusy = false;
        }
    }

    [RelayCommand]
    private void ImportDataBegin()
    {
        if (BackupBusy) return;
        IsConfirmingImport = true;
        SetBackupStatus(true, "");
    }

    [RelayCommand]
    private void ImportDataCancel() => IsConfirmingImport = false;

    [RelayCommand]
    private async Task ImportDataConfirmAsync()
    {
        if (BackupBusy) return;
        IsConfirmingImport = false;

        var path = PickImportPath is null ? null : await PickImportPath();
        if (string.IsNullOrWhiteSpace(path)) return;

        BackupBusy = true;
        SetBackupStatus(true, "正在导入…");
        try
        {
            var (ok, message) = await LocalBackupService.ImportAsync(path);
            SetBackupStatus(ok, message);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Account] 导入数据失败");
            SetBackupStatus(false, "导入失败：" + ex.Message);
        }
        finally
        {
            BackupBusy = false;
        }
    }

    // ════════════ 清除本机数据（单机版 · doc 11.3）════════════
    // 危险操作：只删本机会员目录（DB + JSON + backups + wv2），机器级模型/日志默认保留。
    // 二次确认 = 展开确认条 + 逐字输入确认词；清除成功后立即退出（继续跑会把数据写回来）。

    /// <summary>确认词：必须逐字输入才允许执行（防误点）</summary>
    public const string ClearConfirmKeyword = "清除";

    /// <summary>联网版数据在服务端，没有「清除本机数据」</summary>
    public bool ShowClearData => AppConfig.IsStandalone;

    [ObservableProperty] private bool _isConfirmingClear;
    [ObservableProperty] private string _clearConfirmWord = "";
    [ObservableProperty] private bool _clearExportBackup = true;
    [ObservableProperty] private bool _clearMachineLevel;
    [ObservableProperty] private string _clearStatus = "";
    [ObservableProperty] private bool _clearBusy;
    [ObservableProperty] private bool _clearFailed;

    public bool ShowClearConfirm => IsConfirmingClear;
    public bool CanConfirmClear =>
        string.Equals(ClearConfirmWord.Trim(), ClearConfirmKeyword, StringComparison.Ordinal);
    public bool HasClearStatus => !string.IsNullOrEmpty(ClearStatus);
    public string ClearStatusHex => ClearFailed ? "#C62828" : "#0A5C28";

    partial void OnIsConfirmingClearChanged(bool value) => OnPropertyChanged(nameof(ShowClearConfirm));
    partial void OnClearConfirmWordChanged(string value) => OnPropertyChanged(nameof(CanConfirmClear));
    partial void OnClearStatusChanged(string value) => OnPropertyChanged(nameof(HasClearStatus));
    partial void OnClearFailedChanged(bool value) => OnPropertyChanged(nameof(ClearStatusHex));

    /// <summary>退出方式（默认关应用；冒烟/测试可替换成计数回调）</summary>
    public Action? RequestShutdown { get; set; }

    private void SetClearStatus(bool ok, string text)
    {
        ClearFailed = !ok;
        ClearStatus = text;
    }

    [RelayCommand]
    private void ClearDataBegin()
    {
        if (ClearBusy) return;
        ClearConfirmWord = "";
        SetClearStatus(true, "");
        IsConfirmingClear = true;
    }

    [RelayCommand]
    private void ClearDataCancel() => IsConfirmingClear = false;

    [RelayCommand]
    private async Task ClearDataConfirmAsync()
    {
        if (ClearBusy || !CanConfirmClear) return;

        // 1) 默认先导出备份：用户取消选路径 = 取消整个清除（勾掉可跳过）
        if (ClearExportBackup)
        {
            var path = PickExportPath is null ? null : await PickExportPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                SetClearStatus(false, "已取消：未选择备份路径（勾掉「清除前导出备份」可跳过）");
                return;
            }

            ClearBusy = true;
            SetClearStatus(true, "正在导出备份…");
            try
            {
                await LocalBackupService.ExportAsync(path);
            }
            catch (Exception ex)
            {
                ClearBusy = false;
                Log.Warning(ex, "[Account] 清除前导出备份失败");
                SetClearStatus(false, "备份失败，已取消清除：" + ex.Message);
                return;
            }
        }

        // 2) 删本机会员目录（可选机器级模型/日志）；磁盘操作放池线程，不占 UI
        ClearBusy = true;
        SetClearStatus(true, "正在清除本机数据…");
        WipeResult result;
        try
        {
            result = await Task.Run(() => LocalDataWipe.ClearMemberData(ClearMachineLevel));
        }
        catch (Exception ex)
        {
            ClearBusy = false;
            Log.Warning(ex, "[Account] 清除本机数据失败");
            SetClearStatus(false, "清除失败：" + ex.Message);
            return;
        }

        if (!result.Ok)
        {
            ClearBusy = false;
            var names = string.Join("；", result.Failed.Take(3).Select(Path.GetFileName));
            SetClearStatus(false,
                $"部分文件未能删除（{names}）。请关闭平台账号窗口 / AI 相关窗口后重试。");
            return;
        }

        // 3) 成功：必须退出（继续跑会把库/档写回来）
        IsConfirmingClear = false;
        SetClearStatus(true, "已清除本机数据，软件即将退出…");
        await Task.Delay(1500);
        if (RequestShutdown is { } shutdown)
            shutdown();
        else
            (Avalonia.Application.Current?.ApplicationLifetime
                as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
    public string ExpireDateText => _session.ExpireDate?.ToString("yyyy-MM-dd HH:mm") ?? "未获取到到期时间";

    /// <summary>剩余天数行：已过期 / 今天到期 / 剩 N 天 / 未知</summary>
    public string DaysText => _session.DaysLeft switch
    {
        null => "",
        <= 0 => _session.IsExpired ? "已过期" : "今天到期",
        _ => $"剩 {_session.DaysLeft} 天"
    };

    /// <summary>≤3 天提醒升级（即将到期，页面橙字）</summary>
    public bool IsUrgent => !_session.IsExpired && _session.DaysLeft is >= 0 and <= 3;

    /// <summary>未过期才展示剩余天数（已过期由红徽章表达）</summary>
    public bool ShowDaysText => !_session.IsExpired;

    /// <summary>最近登录 IP（未记录 → --）</summary>
    public string LastLoginIpText => string.IsNullOrWhiteSpace(_session.LastLoginIp) ? "--" : _session.LastLoginIp;

    /// <summary>最近登录时间（未记录 → --）</summary>
    public string LastLoginTimeText => _session.LastLoginAt?.ToString("yyyy-MM-dd HH:mm") ?? "--";

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(Username));
        OnPropertyChanged(nameof(Initial));
        OnPropertyChanged(nameof(IsExpired));
        OnPropertyChanged(nameof(ExpireDateText));
        OnPropertyChanged(nameof(DaysText));
        OnPropertyChanged(nameof(IsUrgent));
        OnPropertyChanged(nameof(ShowDaysText));
        OnPropertyChanged(nameof(LastLoginIpText));
        OnPropertyChanged(nameof(LastLoginTimeText));
    }

    // ════════════ 日志与存储（2026-10-14 用户需求：日志自动清理，保留期可设置） ════════════

    /// <summary>设置卡的两档（用户拍板：默认 30，可改 15）</summary>
    public const int Ret15 = 15;
    public const int Ret30 = 30;

    /// <summary>当前保留天数（进页从存档载入；改档立即存盘 + 立即清理一次）</summary>
    [ObservableProperty] private int _retentionDays = LogSettingsStore.DefaultRetentionDays;

    /// <summary>本机日志占用（logs + crashes + 桌面遗留调试文件）</summary>
    [ObservableProperty] private string _storageText = "正在统计…";

    /// <summary>最近一次清理的结果文案（改档/手动清理后出现）</summary>
    [ObservableProperty] private string _cleanupStatus = "";

    [ObservableProperty] private bool _isCleaning;

    public bool HasCleanupStatus => !string.IsNullOrEmpty(CleanupStatus);
    partial void OnCleanupStatusChanged(string value) => OnPropertyChanged(nameof(HasCleanupStatus));

    /// <summary>两枚档位按钮的选中态（手改过存档的非常规值 → 两个都不亮，点一下即归位）</summary>
    public bool IsRet15 => RetentionDays == Ret15;
    public bool IsRet30 => RetentionDays == Ret30;
    partial void OnRetentionDaysChanged(int value)
    {
        OnPropertyChanged(nameof(IsRet15));
        OnPropertyChanged(nameof(IsRet30));
    }

    /// <summary>每次进页刷新（页面 VM 被壳缓存；占用随时在变）</summary>
    public void OnNavigatedTo()
    {
        _ = RefreshLogSettingsAsync();
        _ = RefreshWatermarkAsync();
        _ = RefreshSoundAsync();
    }

    private async Task RefreshLogSettingsAsync()
    {
        try
        {
            var data = await _logSettings.LoadAsync();
            RetentionDays = data.RetentionDays;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Account] 读日志保留设置失败");
        }
        RefreshStorage();
    }

    /// <summary>切换保留期：存盘 → 立即跑一次清理（用户拍板「立即生效」，不等下次启动）。
    /// 两档各一个无参命令 —— XAML 的 CommandParameter 是字符串，RelayCommand&lt;int&gt; 收
    /// 字符串会 InvalidCast（Avalonia 不做参数类型转换），别为省两行踩这个坑。</summary>
    [RelayCommand]
    private Task SelectRet15Async() => SelectRetentionAsync(Ret15);

    [RelayCommand]
    private Task SelectRet30Async() => SelectRetentionAsync(Ret30);

    private async Task SelectRetentionAsync(int days)
    {
        if (days == RetentionDays) return;
        RetentionDays = days;
        await _logSettings.SaveAsync(new LogSettingsData { RetentionDays = days });
        await RunCleanupAsync();
    }

    /// <summary>手动「立即清理」</summary>
    [RelayCommand]
    private Task CleanNowAsync() => RunCleanupAsync();

    private async Task RunCleanupAsync()
    {
        if (IsCleaning) return;
        IsCleaning = true;
        try
        {
            var result = await Task.Run(() => _cleanup.RunAsync());
            CleanupStatus = result.Summary;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Account] 日志清理失败");
            CleanupStatus = "清理失败，稍后再试";
        }
        finally
        {
            IsCleaning = false;
            RefreshStorage();
        }
    }

    private void RefreshStorage()
    {
        var (files, bytes) = _cleanup.Measure();
        StorageText = files == 0
            ? "当前没有日志文件"
            : $"当前占用 {files} 个文件 · {LogCleanupResult.FormatBytes(bytes)}";
    }

    // ════════════ 视频水印（2026-10-21 用户需求：合成视频烧水印，默认开启） ════════════

    /// <summary>载入时的会员 —— 保存按它写盘（换号/登出期间不会写到新会员档）</summary>
    private Guid _watermarkMember = Guid.Empty;
    private bool _watermarkLoaded;
    private bool _applyingWatermark;
    private long _watermarkEditVersion;

    /// <summary>串行化落盘：文本框逐字符绑定会并发起多次写（LocalJsonFile 的 .tmp 是固定名字）</summary>
    private readonly SemaphoreSlim _watermarkSaveLock = new(1, 1);

    [ObservableProperty] private bool _watermarkEnabled = true;
    [ObservableProperty] private string _watermarkText = "房淘惠";
    [ObservableProperty] private WatermarkPosition _watermarkPos = WatermarkPosition.TopRight;

    /// <summary>保存状态提示（卡片右上角）：改动后「正在保存…」→「已保存 · HH:mm:ss」，
    /// 写盘失败显示红字 —— 用户不用猜这次编辑有没有生效</summary>
    [ObservableProperty] private string _watermarkSaveStatus = "改动自动保存";
    [ObservableProperty] private bool _watermarkSaveFailed;

    public string WatermarkSaveStatusHex =>
        WatermarkSaveFailed ? "#C62828"
        : WatermarkSaveStatus.StartsWith("已保存", StringComparison.Ordinal) ? "#0A5C28"
        : "#86909C";

    partial void OnWatermarkSaveStatusChanged(string value)
        => OnPropertyChanged(nameof(WatermarkSaveStatusHex));
    partial void OnWatermarkSaveFailedChanged(bool value)
        => OnPropertyChanged(nameof(WatermarkSaveStatusHex));

    public bool IsWmTopLeft => WatermarkPos == WatermarkPosition.TopLeft;
    public bool IsWmTopRight => WatermarkPos == WatermarkPosition.TopRight;
    public bool IsWmBottomLeft => WatermarkPos == WatermarkPosition.BottomLeft;
    public bool IsWmBottomRight => WatermarkPos == WatermarkPosition.BottomRight;
    public bool IsWmRandom => WatermarkPos == WatermarkPosition.Random;

    /// <summary>开关开着但文字为空 → 提示「合成时不会加水印」</summary>
    public bool ShowWatermarkEmptyHint => WatermarkEnabled && string.IsNullOrWhiteSpace(WatermarkText);

    partial void OnWatermarkEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowWatermarkEmptyHint));
        QueueWatermarkSave();
    }

    partial void OnWatermarkTextChanged(string value)
    {
        OnPropertyChanged(nameof(ShowWatermarkEmptyHint));
        QueueWatermarkSave();
    }

    partial void OnWatermarkPosChanged(WatermarkPosition value)
    {
        OnPropertyChanged(nameof(IsWmTopLeft));
        OnPropertyChanged(nameof(IsWmTopRight));
        OnPropertyChanged(nameof(IsWmBottomLeft));
        OnPropertyChanged(nameof(IsWmBottomRight));
        OnPropertyChanged(nameof(IsWmRandom));
        QueueWatermarkSave();
    }

    // 档位按钮的教训：XAML 的 CommandParameter 是字符串，RelayCommand<T> 收字符串会 InvalidCast
    // （Avalonia 不做参数类型转换）—— 位置选择给五个无参命令
    [RelayCommand] private void SelectWmTopLeft() => WatermarkPos = WatermarkPosition.TopLeft;
    [RelayCommand] private void SelectWmTopRight() => WatermarkPos = WatermarkPosition.TopRight;
    [RelayCommand] private void SelectWmBottomLeft() => WatermarkPos = WatermarkPosition.BottomLeft;
    [RelayCommand] private void SelectWmBottomRight() => WatermarkPos = WatermarkPosition.BottomRight;
    [RelayCommand] private void SelectWmRandom() => WatermarkPos = WatermarkPosition.Random;

    private async Task RefreshWatermarkAsync()
    {
        _applyingWatermark = true;
        try
        {
            _watermarkMember = _watermark.CurrentMemberId;
            var s = await _watermark.LoadAsync();
            WatermarkEnabled = s.Enabled;
            WatermarkText = s.Text;
            WatermarkPos = s.Position;
            _watermarkEditVersion = 0;
            WatermarkSaveFailed = false;
            WatermarkSaveStatus = "改动自动保存";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Account] 读水印设置失败");
        }
        finally
        {
            _applyingWatermark = false;
            _watermarkLoaded = true;
        }
        OnPropertyChanged(nameof(ShowWatermarkEmptyHint));
    }

    private void QueueWatermarkSave()
    {
        if (!_watermarkLoaded || _applyingWatermark) return;
        var version = ++_watermarkEditVersion;
        WatermarkSaveFailed = false;
        WatermarkSaveStatus = "正在保存…";
        _ = SaveWatermarkAsync(version);
    }

    private async Task SaveWatermarkAsync(long version)
    {
        var ok = false;
        // 最后一次拿到锁时读到的是最新状态 —— 逐字符保存也一定收敛到最后一次编辑
        await _watermarkSaveLock.WaitAsync();
        try
        {
            ok = await _watermark.SaveAsync(_watermarkMember, new WatermarkSettings
            {
                Enabled = WatermarkEnabled,
                Text = WatermarkText,
                Position = WatermarkPos,
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Account] 保存水印设置失败");
        }
        finally
        {
            _watermarkSaveLock.Release();
        }

        // 有更新的编辑在排队/已保存 → 让最后那次落定状态（旧的「已保存」不闪在前面）
        if (version != _watermarkEditVersion) return;
        WatermarkSaveFailed = !ok;
        WatermarkSaveStatus = ok ? $"已保存 · {DateTime.Now:HH:mm:ss}" : "保存失败，请重试";
    }

    // ════════════ 上传提示音（2026-10-22 用户需求：成功/失败提示音，可关闭/调小/调大） ════════════

    /// <summary>载入时的会员 —— 保存按它写盘（换号/登出期间不会写到新会员档）</summary>
    private Guid _soundMember = Guid.Empty;
    private bool _soundLoaded;
    private bool _applyingSound;
    private long _soundEditVersion;

    /// <summary>串行化落盘（连点两档时保证最后落盘的是最后一次选择）</summary>
    private readonly SemaphoreSlim _soundSaveLock = new(1, 1);

    /// <summary>滑块值（0~100；Slider.Value 是 double，直接绑 int 要走转换，这里就存 double）</summary>
    [ObservableProperty] private double _soundVolumePercent = SoundSettingsStore.DefaultVolumePercent;

    /// <summary>保存状态提示（卡片右上角）：改动后「正在保存…」→「已保存 · HH:mm:ss」，失败红字</summary>
    [ObservableProperty] private string _soundSaveStatus = "改动自动保存";
    [ObservableProperty] private bool _soundSaveFailed;

    public string SoundSaveStatusHex =>
        SoundSaveFailed ? "#C62828"
        : SoundSaveStatus.StartsWith("已保存", StringComparison.Ordinal) ? "#0A5C28"
        : "#86909C";

    partial void OnSoundSaveStatusChanged(string value)
        => OnPropertyChanged(nameof(SoundSaveStatusHex));
    partial void OnSoundSaveFailedChanged(bool value)
        => OnPropertyChanged(nameof(SoundSaveStatusHex));

    /// <summary>0 = 关闭（试听按钮跟着禁用）</summary>
    public bool IsSoundOff => SoundVolumePercent <= 0;

    /// <summary>滑块右侧的读数（0 显示「关闭」）</summary>
    public string SoundVolumeText =>
        IsSoundOff ? "关闭" : $"{(int)Math.Round(SoundVolumePercent)}%";

    partial void OnSoundVolumePercentChanged(double value)
    {
        OnPropertyChanged(nameof(IsSoundOff));
        OnPropertyChanged(nameof(SoundVolumeText));

        // 拖动立即生效：上传消费者读的是这个静态值（不必等下次进壳）
        PublishSoundNotifier.VolumePercent = (int)Math.Round(value);
        if (_soundLoaded && !_applyingSound) QueueSoundSave();
    }

    /// <summary>试听成功音（按当前滑块的增益；0 时按钮在 XAML 里禁掉）</summary>
    [RelayCommand]
    private void PreviewSound() => PublishSoundNotifier.PlaySuccess();

    private async Task RefreshSoundAsync()
    {
        _applyingSound = true;
        try
        {
            _soundMember = _sound.CurrentMemberId;
            var s = await _sound.LoadAsync();
            SoundVolumePercent = s.VolumePercent;
            PublishSoundNotifier.VolumePercent = s.VolumePercent;
            _soundEditVersion = 0;
            SoundSaveFailed = false;
            SoundSaveStatus = "改动自动保存";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Account] 读提示音设置失败");
        }
        finally
        {
            _applyingSound = false;
            _soundLoaded = true;
        }
    }

    private void QueueSoundSave()
    {
        var version = ++_soundEditVersion;
        SoundSaveFailed = false;
        SoundSaveStatus = "正在保存…";
        _ = SaveSoundAsync(version);
    }

    private async Task SaveSoundAsync(long version)
    {
        var ok = false;
        // 最后一次拿到锁时读到的是最新档位 —— 连点两次也一定收敛到后点的那档
        await _soundSaveLock.WaitAsync();
        try
        {
            ok = await _sound.SaveAsync(_soundMember,
                new SoundSettings { VolumePercent = (int)Math.Round(SoundVolumePercent) });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Account] 保存提示音设置失败");
        }
        finally
        {
            _soundSaveLock.Release();
        }

        if (version != _soundEditVersion) return;
        SoundSaveFailed = !ok;
        SoundSaveStatus = ok ? $"已保存 · {DateTime.Now:HH:mm:ss}" : "保存失败，请重试";
    }
}
