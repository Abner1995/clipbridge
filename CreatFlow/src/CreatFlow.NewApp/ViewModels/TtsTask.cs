namespace CreatFlow.NewApp.ViewModels;

/// <summary>
/// 配音队列里的一条任务（UI 状态机 + 持久化映射）—— 输入是提交时的快照，
/// 入队后与配音页状态解耦，执行只认 <see cref="ToJob"/> 产出的 TtsJob。
/// 流转：待合成 → 合成中 → 已完成 / 失败 / 已取消（可重试）；重开时「合成中」标已中断（可重试）。
/// </summary>
public partial class TtsTask : ObservableObject
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedAt { get; init; } = DateTime.Now;

    /// <summary>列表标题 = 文案摘要（首行前 20 字）</summary>
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string? _providerName;
    [ObservableProperty] private string? _voiceId;
    [ObservableProperty] private double _speed = 1.0;
    [ObservableProperty] private double _pitch = 1.0;
    [ObservableProperty] private string _exportDirectory = "";
    [ObservableProperty] private string _suggestedName = "";

    // ── 运行态（同进存档：重开还能看到失败原因 / 中断位置）──

    [ObservableProperty] private TtsTaskStatus _status = TtsTaskStatus.Pending;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string? _outputPath;

    /// <summary>勾选态（批量删除用；纯 UI 短期态，不进存档）</summary>
    [ObservableProperty] private bool _isSelected;

    partial void OnStatusChanged(TtsTaskStatus value) => NotifyComputed();
    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasMessage));
    partial void OnOutputPathChanged(string? value) => OnPropertyChanged(nameof(HasOutput));
    partial void OnVoiceIdChanged(string? value) => OnPropertyChanged(nameof(VoiceSummary));
    partial void OnSpeedChanged(double value) => OnPropertyChanged(nameof(VoiceSummary));

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
    public bool IsActive => Status is TtsTaskStatus.Pending or TtsTaskStatus.Running;

    public bool IsRunning => Status == TtsTaskStatus.Running;
    public bool IsDone => Status == TtsTaskStatus.Done;
    public bool CanCancel => IsActive;
    public bool CanRetry => Status is TtsTaskStatus.Failed or TtsTaskStatus.Interrupted or TtsTaskStatus.Cancelled;
    public bool CanRemove => !IsActive;
    public bool HasOutput => !string.IsNullOrEmpty(OutputPath);
    public bool HasMessage => !string.IsNullOrEmpty(StatusMessage);

    public string StatusText => Status switch
    {
        TtsTaskStatus.Pending => "待合成",
        TtsTaskStatus.Running => "合成中",
        TtsTaskStatus.Done => "已完成",
        TtsTaskStatus.Failed => "失败",
        TtsTaskStatus.Cancelled => "已取消",
        _ => "已中断",
    };

    /// <summary>配音摘要："晓蓓 · 1.00x"（同合成队列口径）</summary>
    public string VoiceSummary
    {
        get
        {
            var name = TtsProviderFactory.Create(ProviderName).Voices
                .FirstOrDefault(v => v.Id == VoiceId)?.DisplayName ?? VoiceId ?? "默认音色";
            return $"{name} · {Speed:0.00}x";
        }
    }

    // ═══ 映射 ═══

    /// <summary>执行快照（队列消费者只把它交给 TtsRunner）</summary>
    public TtsJob ToJob() => new()
    {
        Text = Text,
        ProviderName = ProviderName,
        VoiceId = VoiceId,
        Speed = Speed,
        Pitch = Pitch,
        ExportDirectory = ExportDirectory,
        SuggestedName = SuggestedName,
    };

    public TtsTaskData ToData() => new()
    {
        Id = Id,
        Title = Title,
        Text = Text,
        ProviderName = ProviderName,
        VoiceId = VoiceId,
        Speed = Speed,
        Pitch = Pitch,
        ExportDirectory = ExportDirectory,
        SuggestedName = SuggestedName,
        Status = Status,
        StatusMessage = StatusMessage,
        OutputPath = OutputPath,
        CreatedAt = CreatedAt,
    };

    public static TtsTask FromData(TtsTaskData d) => new()
    {
        Id = d.Id,
        Title = d.Title,
        Text = d.Text,
        ProviderName = d.ProviderName,
        VoiceId = d.VoiceId,
        Speed = d.Speed,
        Pitch = d.Pitch,
        ExportDirectory = d.ExportDirectory,
        SuggestedName = d.SuggestedName,
        Status = d.Status,
        StatusMessage = d.StatusMessage,
        OutputPath = d.OutputPath,
        CreatedAt = d.CreatedAt,
    };

    /// <summary>配音页批量提交入口：一个文本框 = 一条（标题 / 文件名在提交时定死）</summary>
    public static TtsTask FromSubmit(string text, string title, string? providerName, string? voiceId,
        double speed, double pitch, string exportDirectory, string suggestedName) => new()
    {
        Title = title,
        Text = text,
        ProviderName = providerName,
        VoiceId = voiceId,
        Speed = speed,
        Pitch = pitch,
        ExportDirectory = exportDirectory,
        SuggestedName = suggestedName,
    };
}
