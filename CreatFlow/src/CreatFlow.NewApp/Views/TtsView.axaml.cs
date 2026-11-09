using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CreatFlow.NewApp.ViewModels;

namespace CreatFlow.NewApp.Views;

/// <summary>
/// 配音页 code-behind —— 只承载 View 专属能力（保存目录选择器），业务全在
/// <see cref="TtsViewModel"/>（StorageProvider 挂在 TopLevel 上，VM 拿不到它）。
/// </summary>
public partial class TtsView : UserControl
{
    private TtsViewModel? _vm;

    public TtsView()
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
        if (DataContext is not TtsViewModel vm) return;

        vm.PickExportFolderAsync = PickExportFolderAsync;
        _vm = vm;
    }

    private void UnwireVm()
    {
        if (_vm is null) return;
        _vm.PickExportFolderAsync = null;
        _vm = null;
    }

    /// <summary>选默认保存目录（生成的 mp3 都落这里）</summary>
    private async Task<string?> PickExportFolderAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return null;

        try
        {
            var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择配音保存目录",
                AllowMultiple = false,
            });
            return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "配音保存目录选择器打开失败");
            return null;
        }
    }
}
