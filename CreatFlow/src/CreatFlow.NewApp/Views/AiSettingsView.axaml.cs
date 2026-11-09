using Avalonia.Controls;
using CreatFlow.NewApp.ViewModels;

namespace CreatFlow.NewApp.Views;

/// <summary>
/// AI 设置页 —— 逻辑全在 VM，code-behind 只做剪贴板通道注入（队列页同款手法：
/// 剪贴板挂在 TopLevel 上，VM 拿不到，注入而不是在 Click 里强转 DataContext）。
/// </summary>
public partial class AiSettingsView : UserControl
{
    private AiSettingsViewModel? _vm;

    public AiSettingsView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) => WireVm();
        AttachedToVisualTree += (_, _) => WireVm();
        DetachedFromVisualTree += (_, _) => UnwireVm();
    }

    private void WireVm()
    {
        if (_vm == DataContext) return;
        UnwireVm();
        if (DataContext is not AiSettingsViewModel vm) return;

        vm.CopyToClipboardAsync = text =>
            TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;
        _vm = vm;
    }

    private void UnwireVm()
    {
        if (_vm is null) return;
        _vm.CopyToClipboardAsync = null;
        _vm = null;
    }
}
