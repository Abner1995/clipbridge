using Avalonia.Controls;

namespace CreatFlow.NewApp.Views;

/// <summary>
/// 配音队列页 code-behind —— 无 View 专属能力（没有文件选择器），业务全在
/// <see cref="ViewModels.TtsQueueViewModel"/>。
/// </summary>
public partial class TtsQueueView : UserControl
{
    public TtsQueueView() => InitializeComponent();
}
