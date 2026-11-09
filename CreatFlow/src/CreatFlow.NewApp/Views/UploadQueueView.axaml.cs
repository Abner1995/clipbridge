using Avalonia.Controls;
using CreatFlow.NewApp.ViewModels;

namespace CreatFlow.NewApp.Views;

/// <summary>
/// 上传队列页 —— 逻辑全在 VM（含后台消费循环），code-behind 只做加载 + 剪贴板通道注入。
/// </summary>
public partial class UploadQueueView : UserControl
{
    private UploadQueueViewModel? _vm;

    public UploadQueueView()
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
        if (DataContext is not UploadQueueViewModel vm) return;

        // 剪贴板挂在 TopLevel 上，VM 拿不到 —— 与发布页的文件选择器注入同一手法；
        // 注入而不是在按钮 Click 里强转 DataContext：VM 能被探针直接测
        vm.ClipboardWriter = text =>
            TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;
        _vm = vm;
    }

    private void UnwireVm()
    {
        if (_vm is null) return;
        _vm.ClipboardWriter = null;
        _vm = null;
    }
}
