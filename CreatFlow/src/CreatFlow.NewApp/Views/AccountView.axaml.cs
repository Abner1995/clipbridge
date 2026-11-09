using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CreatFlow.NewApp.ViewModels;

namespace CreatFlow.NewApp.Views;

/// <summary>
/// 账号设置页 code-behind —— 只承载 View 专属能力（数据备份的文件选择器），
/// 业务全在 <see cref="AccountViewModel"/>。StorageProvider 挂在 TopLevel 上，
/// VM 拿不到它，回调由这里注入（与合成页/合集页同款手法）。
/// </summary>
public partial class AccountView : UserControl
{
    private AccountViewModel? _vm;

    public AccountView()
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
        if (DataContext is not AccountViewModel vm) return;

        vm.PickExportPath = PickExportPathAsync;
        vm.PickImportPath = PickImportPathAsync;
        _vm = vm;
    }

    private void UnwireVm()
    {
        if (_vm is null) return;
        _vm.PickExportPath = null;
        _vm.PickImportPath = null;
        _vm = null;
    }

    private async Task<string?> PickExportPathAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return null;

        try
        {
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出 CreatFlow 数据备份",
                SuggestedFileName = $"CreatFlow-备份-{DateTime.Now:yyyyMMdd-HHmm}.zip",
                DefaultExtension = "zip",
                FileTypeChoices =
                [
                    new FilePickerFileType("CreatFlow 备份") { Patterns = ["*.zip"] },
                ],
            });
            return file?.TryGetLocalPath();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Account] 选择导出路径失败");
            return null;
        }
    }

    private async Task<string?> PickImportPathAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return null;

        try
        {
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择要导入的 CreatFlow 备份",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("CreatFlow 备份") { Patterns = ["*.zip"] },
                ],
            });
            return files.Count > 0 ? files[0].TryGetLocalPath() : null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Account] 选择导入文件失败");
            return null;
        }
    }
}
