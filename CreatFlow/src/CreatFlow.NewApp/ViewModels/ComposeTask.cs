namespace CreatFlow.NewApp.ViewModels;

/// <summary>
/// 合成队列里的一条任务（UI 状态机 + 持久化映射）—— 输入是快照（来自合成页或文件夹批量），
/// 入队后与页面状态解耦，执行只认 <see cref="ToJob"/> 产出的 ComposeJob。
/// 流转：待合成 → 合成中 → 已完成 / 失败 / 已取消（可重试）；重开时「合成中」标已中断（可重试）。
/// </summary>
public partial class ComposeTask : ObservableObject
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedAt { get; init; } = DateTime.Now;

    /// <summary>发布标题（发布单元文件夹名与 params「标题」）</summary>
    [ObservableProperty]
    private string _title = "";

    /// <summary>素材路径（顺序即播放顺序）</summary>
    public List<string> MaterialPaths { get; init; } = [];

    [ObservableProperty] private string? _text;

    /// <summary>角色名（角色库；仅展示与重试辨识 —— 最终文案已冻进 Text，执行不依赖它）</summary>
    [ObservableProperty] private string? _roleName;
    [ObservableProperty] private bool _useShots;
    [ObservableProperty] private string? _audioPath;
    [ObservableProperty] private string? _providerName;
    [ObservableProperty] private string? _voiceId;
    [ObservableProperty] private double _speed = 1.0;
    [ObservableProperty] private double _pitch = 1.0;
    [ObservableProperty] private string? _bgmPath;
    [ObservableProperty] private double _bgmVolume = 0.25;
    [ObservableProperty] private bool _isWide;

    /// <summary>水印快照（入队时冻结；老存档缺字段默认开 —— 见 ComposeTaskData）</summary>
    [ObservableProperty] private WatermarkSpec _watermark = WatermarkSpec.Default;

    [ObservableProperty] private string? _overlayTitle;
    [ObservableProperty] private string _outputDirectory = "";
    [ObservableProperty] private DateTime? _scheduledAt;

    // ── 运行态（同进存档：重开还能看到失败原因 / 中断位置）──

    [ObservableProperty] private ComposeTaskStatus _status = ComposeTaskStatus.Pending;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private int _progress;
    [ObservableProperty] private string? _outputPath;

    // ── 自动发布（合成 Done 之后的独立状态轴；空 targets = 未启用）──

    /// <summary>目标账号快照（入队时冻结；平台/账号解绑不影响已入队任务）</summary>
    public IReadOnlyList<AutoPublishTarget> AutoPublishTargets { get; init; } = [];
    [ObservableProperty] private AutoPublishState _autoPublishState = AutoPublishState.None;
    [ObservableProperty] private string _autoPublishText = "";

    partial void OnAutoPublishStateChanged(AutoPublishState value) => NotifyAutoPublish();
    partial void OnAutoPublishTextChanged(string value) => NotifyAutoPublish();

    private void NotifyAutoPublish()
    {
        OnPropertyChanged(nameof(HasAutoPublishText));
        OnPropertyChanged(nameof(ShowQueueLink));
        OnPropertyChanged(nameof(AutoPublishTextHex));
    }

    partial void OnStatusChanged(ComposeTaskStatus value) => NotifyComputed();
    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasMessage));
    partial void OnOutputPathChanged(string? value) => OnPropertyChanged(nameof(HasOutput));
    partial void OnAudioPathChanged(string? value) => OnPropertyChanged(nameof(VoiceSummary));
    partial void OnVoiceIdChanged(string? value) => OnPropertyChanged(nameof(VoiceSummary));
    partial void OnSpeedChanged(double value) => OnPropertyChanged(nameof(VoiceSummary));
    partial void OnRoleNameChanged(string? value)
    {
        OnPropertyChanged(nameof(HasRole));
        OnPropertyChanged(nameof(RoleText));
    }
    partial void OnScheduledAtChanged(DateTime? value)
    {
        OnPropertyChanged(nameof(HasSchedule));
        OnPropertyChanged(nameof(ScheduleText));
    }

    private void NotifyComputed()
    {
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(StatusText));
    }

    // ═══ 派生显示 / 按钮可用性 ═══

    /// <summary>待合成或合成中（正在占着队列）</summary>
    public bool IsActive => Status is ComposeTaskStatus.Pending or ComposeTaskStatus.Running;

    public bool IsRunning => Status == ComposeTaskStatus.Running;
    public bool IsDone => Status == ComposeTaskStatus.Done;
    public bool CanCancel => IsActive;
    public bool CanRetry => Status is ComposeTaskStatus.Failed or ComposeTaskStatus.Interrupted or ComposeTaskStatus.Cancelled;
    public bool CanRemove => !IsActive;
    public bool HasOutput => !string.IsNullOrEmpty(OutputPath);
    public bool HasMessage => !string.IsNullOrEmpty(StatusMessage);

    public string StatusText => Status switch
    {
        ComposeTaskStatus.Pending => "待合成",
        ComposeTaskStatus.Running => "合成中",
        ComposeTaskStatus.Done => "已完成",
        ComposeTaskStatus.Failed => "失败",
        ComposeTaskStatus.Cancelled => "已取消",
        _ => "已中断",
    };

    /// <summary>素材摘要："5 个素材（4 图 · 1 视频）"</summary>
    public string MaterialsSummary
    {
        get
        {
            var images = MaterialPaths.Count(MediaFiles.IsImage);
            var videos = MaterialPaths.Count - images;
            return $"{MaterialPaths.Count} 个素材（{images} 图 · {videos} 视频）";
        }
    }

    /// <summary>配音摘要："晓蓓 · 1.00x" / "本地音频"</summary>
    public string VoiceSummary
    {
        get
        {
            if (!string.IsNullOrEmpty(AudioPath)) return "本地音频";
            var name = TtsProviderFactory.Create(ProviderName).Voices
                .FirstOrDefault(v => v.Id == VoiceId)?.DisplayName ?? VoiceId ?? "默认音色";
            return $"{name} · {Speed:0.00}x";
        }
    }

    /// <summary>画面：横屏标记（列表副行用；竖屏不显示）</summary>
    public string AspectText => IsWide ? "16:9 横屏" : "9:16 竖屏";

    /// <summary>角色徽章（队列行副行；没角色不显示）</summary>
    public bool HasRole => !string.IsNullOrEmpty(RoleName);
    public string RoleText => $"角色：{RoleName}";

    // ── 定时发布（队列行副行独立提示；与 HasAutoPublish 无关 —— 「仅添加 + 定时」也得看得见）──

    public bool HasSchedule => ScheduledAt is not null;
    public string ScheduleText => ScheduledAt is { } t ? $"定时 {t:MM-dd HH:mm} 发布" : "";

    // ── 自动发布（队列行展示）──

    /// <summary>这条任务会（或已）自动发布 —— 副行小字，入队即可见（用户提交那一刻就要看到）</summary>
    public bool HasAutoPublish => AutoPublishTargets.Count > 0;
    public string AutoPublishSummary => $"自动发布 {AutoPublishTargets.Count} 个账号";

    public bool HasAutoPublishText => !string.IsNullOrEmpty(AutoPublishText);

    /// <summary>已提交 / 状态未知 → 给「去上传队列」入口（失败/未提交只需要看文案）</summary>
    public bool ShowQueueLink => AutoPublishState is AutoPublishState.Submitted or AutoPublishState.Uncertain;

    /// <summary>结果小字颜色（与 App.axaml 的 ColorSuccess / ColorError / TextSecondary 同值，改一处要同步）</summary>
    public string AutoPublishTextHex => AutoPublishState switch
    {
        AutoPublishState.Submitted => "#0A5C28",
        AutoPublishState.Failed => "#C62828",
        _ => "#4E5969",
    };

    // ═══ 映射 ═══

    /// <summary>执行快照（队列消费者把它交给 ComposeRunner；合成完的自动发布段也从它读 targets）。
    /// 注意：**不搬运 RoleName** —— runner 不读角色，最终文案已在 Text 里冻好（别当漏写"补齐"）。</summary>
    public ComposeJob ToJob() => new()
    {
        Materials = [.. MaterialPaths.Select(MediaFiles.ToItem)],
        OutputDirectory = OutputDirectory,
        PublishTitle = Title,
        OverlayTitle = OverlayTitle,
        Text = Text,
        UseShots = UseShots,
        AudioPath = AudioPath,
        ProviderName = ProviderName,
        VoiceId = VoiceId,
        Speed = Speed,
        Pitch = Pitch,
        BgmPath = BgmPath,
        BgmVolume = BgmVolume,
        IsWide = IsWide,
        Watermark = Watermark,
        ScheduledAt = ScheduledAt,
        // 重试路径也靠它保住 targets（与任务同时冻在入队那一刻）
        AutoPublishTargets = AutoPublishTargets,
    };

    public ComposeTaskData ToData() => new()
    {
        Id = Id,
        Title = Title,
        MaterialPaths = [.. MaterialPaths],
        Text = Text,
        RoleName = RoleName,
        UseShots = UseShots,
        AudioPath = AudioPath,
        ProviderName = ProviderName,
        VoiceId = VoiceId,
        Speed = Speed,
        Pitch = Pitch,
        BgmPath = BgmPath,
        BgmVolume = BgmVolume,
        IsWide = IsWide,
        Watermark = Watermark,
        OverlayTitle = OverlayTitle,
        OutputDirectory = OutputDirectory,
        ScheduledAt = ScheduledAt,
        Status = Status,
        StatusMessage = StatusMessage,
        Progress = Progress,
        OutputPath = OutputPath,
        CreatedAt = CreatedAt,
        AutoPublishTargets = [.. AutoPublishTargets],
        AutoPublishState = AutoPublishState,
        AutoPublishText = AutoPublishText,
    };

    public static ComposeTask FromData(ComposeTaskData d) => new()
    {
        Id = d.Id,
        Title = d.Title,
        MaterialPaths = [.. d.MaterialPaths],
        Text = d.Text,
        RoleName = d.RoleName,
        UseShots = d.UseShots,
        AudioPath = d.AudioPath,
        ProviderName = d.ProviderName,
        VoiceId = d.VoiceId,
        Speed = d.Speed,
        Pitch = d.Pitch,
        BgmPath = d.BgmPath,
        BgmVolume = d.BgmVolume,
        IsWide = d.IsWide,
        Watermark = d.Watermark,
        OverlayTitle = d.OverlayTitle,
        OutputDirectory = d.OutputDirectory,
        ScheduledAt = d.ScheduledAt,
        Status = d.Status,
        StatusMessage = d.StatusMessage,
        Progress = d.Progress,
        OutputPath = d.OutputPath,
        CreatedAt = d.CreatedAt,
        AutoPublishTargets = [.. d.AutoPublishTargets],
        AutoPublishState = d.AutoPublishState,
        AutoPublishText = d.AutoPublishText,
    };

    /// <summary>合成页单条入口：页面组装的执行快照</summary>
    public static ComposeTask FromJob(ComposeJob job) => new()
    {
        Title = job.PublishTitle,
        MaterialPaths = [.. job.Materials.Select(m => m.Path)],
        Text = job.Text,
        RoleName = job.RoleName,
        UseShots = job.UseShots,
        AudioPath = job.AudioPath,
        ProviderName = job.ProviderName,
        VoiceId = job.VoiceId,
        Speed = job.Speed,
        Pitch = job.Pitch,
        BgmPath = job.BgmPath,
        BgmVolume = job.BgmVolume,
        IsWide = job.IsWide,
        Watermark = job.Watermark,
        OverlayTitle = job.OverlayTitle,
        OutputDirectory = job.OutputDirectory,
        ScheduledAt = job.ScheduledAt,
        AutoPublishTargets = [.. job.AutoPublishTargets],
    };

    /// <summary>文件夹批量入口：扫描出的单元 + 当前偏好（引擎/音色/语速/背景音乐音量/输出目录/画幅）。
    /// 「音频优先于 资料.txt」「bgm 前缀 = 背景音乐」都已在扫描层定死（单元里 文案与配音不会同时有值）。
    /// roleName = 角色库选中的角色名（仅展示；套模板已在批量入口完成、冻进 unit.Text）。
    /// autoTargets = 批量确认条里定下的自动发布目标（null/空 = 本批不自动发布）。
    /// scheduledAt = 批次定时（整批同一时间点；写入每条发布单元的「发布时间」，null = 立即）。
    /// watermark = 入队时读到的水印偏好（null = 默认开 —— 老调用点/自检）。
    /// ⚠️ 新参一律加在**末位** —— 自检里是位置实参（ComposeSelfTest FromFolder(unit, settings, null, [t1])）。</summary>
    public static ComposeTask FromFolder(ComposeFolderUnit unit, ComposeSettings settings,
        string? roleName = null, IReadOnlyList<AutoPublishTarget>? autoTargets = null,
        DateTime? scheduledAt = null, WatermarkSpec? watermark = null) => new()
    {
        Title = unit.Title,
        MaterialPaths = [.. unit.MaterialPaths],
        Text = unit.Text,
        RoleName = roleName,
        AudioPath = unit.AudioPath,
        BgmPath = unit.BgmPath,
        ProviderName = settings.ProviderName,
        VoiceId = settings.VoiceId,
        Speed = settings.Speed,
        Pitch = settings.Pitch,
        BgmVolume = settings.BgmVolume,
        IsWide = settings.IsWide,
        Watermark = watermark ?? WatermarkSpec.Default,
        OutputDirectory = string.IsNullOrWhiteSpace(settings.OutputDirectory)
            ? ComposeViewModel.DefaultDirectory
            : settings.OutputDirectory,
        ScheduledAt = scheduledAt,
        AutoPublishTargets = autoTargets is null ? [] : [.. autoTargets],
    };
}
