using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CreatFlow.NewApp.ViewModels;

namespace CreatFlow.NewApp.Views;

/// <summary>
/// 合集页 code-behind —— 仅承载 View 专属能力（文件选择 / 本地图片解码），
/// 业务全部在 CollectionViewModel（列表 + 整页编辑双视图）。
/// </summary>
public partial class CollectionView : UserControl
{
    private CollectionViewModel? _vm;

    public CollectionView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => WireVm();
        AttachedToVisualTree += (_, _) => WireVm();
        DetachedFromVisualTree += (_, _) => UnwireVm();
    }

    /// <summary>绑定 VM：注入封面选图回调（需 Window 的 StorageProvider）+ 监听路径变化刷预览。</summary>
    private void WireVm()
    {
        if (_vm == DataContext) return;
        UnwireVm();
        if (DataContext is not CollectionViewModel vm) return;

        _vm = vm;
        // 页面每次挂树只注入一次；回调闭包捕获本控件（TopLevel 解析需在树内）
        vm.BrowseCoverImageAsync ??= BrowseCoverAsync;
        vm.PropertyChanged += OnVmPropertyChanged;
        UpdateCoverPreview();
    }

    private void UnwireVm()
    {
        if (_vm == null) return;
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = null;
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CollectionViewModel.CoverImagePath))
            UpdateCoverPreview();
    }

    /// <summary>编辑器封面本地预览：路径存在且可解码 → 显示真图；否则回到占位（Image 隐藏）。</summary>
    private void UpdateCoverPreview()
    {
        if (CoverPreviewImage == null || _vm == null) return; // XAML 尚未构造完
        var path = _vm.CoverImagePath;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            try
            {
                using var fs = File.OpenRead(path);
                CoverPreviewImage.Source = new Bitmap(fs);
                CoverPreviewImage.IsVisible = true;
                return;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "封面预览解码失败: {Path}", path); // 占位回落
            }
        }
        CoverPreviewImage.Source = null;
        CoverPreviewImage.IsVisible = false;
    }

    /// <summary>系统文件选择（图片过滤器）→ 本地路径；取消返回 null。</summary>
    private async Task<string?> BrowseCoverAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return null;
        IReadOnlyList<IStorageFile> files;
        try
        {
            files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择合集封面图",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("图片文件")
                    {
                        Patterns = ["*.jpg", "*.jpeg", "*.png", "*.webp", "*.bmp"]
                    }
                ],
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "封面选择器打开失败");
            return null;
        }
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
