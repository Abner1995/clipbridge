using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CreatFlow.NewApp.ViewModels;

namespace CreatFlow.NewApp.Views;

/// <summary>
/// 视频下载页 code-behind —— 只承载 View 专属能力（下载目录选择器），
/// 业务全在 <see cref="DownloadQueueViewModel"/>（StorageProvider 挂在 TopLevel 上）。
/// </summary>
public partial class DownloadQueueView : UserControl
{
    private DownloadQueueViewModel? _vm;

    public DownloadQueueView()
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
        if (DataContext is not DownloadQueueViewModel vm) return;

        vm.FolderPicker = PickFolderAsync;
        _vm = vm;
    }

    private void UnwireVm()
    {
        if (_vm is null) return;
        _vm.FolderPicker = null;
        _vm = null;
    }

    private async Task<string?> PickFolderAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return null;

        try
        {
            var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择下载目录",
                AllowMultiple = false,
            });
            return folders.Select(f => f.TryGetLocalPath())
                .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "下载目录选择器打开失败");
            return null;
        }
    }
}
