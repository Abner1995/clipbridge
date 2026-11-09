namespace CreatFlow.NewApp.ViewModels;

/// <summary>
/// 配音页列表里的一条配音（点「添加配音」加一条；一条 = 一条队列任务）。
/// 每条独立设置文案 / 音色 / 语速 —— 提交时按行快照进任务；试听也在行内进行（合成后播放，不落盘）。
/// </summary>
public partial class TtsBatchRow : ObservableObject
{
    /// <summary>列表序号（1 起；增删后由页面重排）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NumberLabel))]
    private int _number = 1;

    [ObservableProperty]
    private string _text = "";

    /// <summary>本条音色（含所属引擎 —— 试听 / 提交都按它执行）</summary>
    [ObservableProperty]
    private TtsVoiceOption? _voice;

    /// <summary>本条语速倍率（0.5–2.0）</summary>
    [ObservableProperty]
    private double _speed = 1.0;

    /// <summary>试听进行中（合成或播放）；按钮文字随它切「试听 / 停止」</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewButtonText))]
    private bool _isPreviewing;

    /// <summary>行内状态小字：试听进度 / 失败原因（播完保留「合成完成」提示，内容一改就作废）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusText))]
    private string _statusText = "";

    [ObservableProperty]
    private bool _isStatusError;

    public string NumberLabel => $"第 {Number} 条";
    public bool HasText => !string.IsNullOrWhiteSpace(Text);
    public string PreviewButtonText => IsPreviewing ? "停止" : "试听";
    public bool HasStatusText => !string.IsNullOrEmpty(StatusText);

    // 试听缓存（key 由页面 VM 组装：文案 + 音色 + 语速）：内容没变 → 再点试听直接重放，不重复合成
    internal string? PreviewKey;
    internal byte[]? PreviewAudio;

    partial void OnTextChanged(string value) => OnPropertyChanged(nameof(HasText));
}
