using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CreatFlow.NewApp.ViewModels;

namespace CreatFlow.NewApp.Views;

/// <summary>
/// 合成队列页 code-behind —— 只承载 View 专属能力（文件夹选择器），业务全在
/// <see cref="ComposeQueueViewModel"/>（StorageProvider 挂在 TopLevel 上，VM 拿不到它）。
/// </summary>
public partial class ComposeQueueView : UserControl
{
    private ComposeQueueViewModel? _vm;

    public ComposeQueueView()
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
        if (DataContext is not ComposeQueueViewModel vm) return;

        vm.PickBatchFoldersAsync = PickBatchFoldersAsync;
        vm.PickTemplateFolderAsync = PickTemplateFolderAsync;
        _vm = vm;
    }

    private void UnwireVm()
    {
        if (_vm is null) return;
        _vm.PickBatchFoldersAsync = null;
        _vm.PickTemplateFolderAsync = null;
        _vm = null;
    }

    /// <summary>选「合成单元模板」生成在哪个文件夹（单选）</summary>
    private async Task<string?> PickTemplateFolderAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return null;

        try
        {
            var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择模板放在哪里（会在里面生成「合成单元模板」文件夹）",
            });
            return folders.Select(f => f.TryGetLocalPath()).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "模板父目录选择器打开失败");
            return null;
        }
    }

    /// <summary>选批量文件夹（可多选；单取一个总目录时按「带素材的子文件夹」展开）</summary>
    private async Task<IReadOnlyList<string>?> PickBatchFoldersAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return null;

        try
        {
            var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择文件夹（总目录：带素材的子文件夹各合成一条；可多选）",
                AllowMultiple = true,
            });
            return [.. folders.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!)];
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "批量文件夹选择器打开失败");
            return null;
        }
    }
}
