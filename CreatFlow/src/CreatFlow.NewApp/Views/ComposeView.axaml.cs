using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CreatFlow.NewApp.ViewModels;

namespace CreatFlow.NewApp.Views;

/// <summary>
/// 合成页 code-behind —— 只承载 View 专属能力（文件/文件夹选择器），业务全在
/// <see cref="ComposeViewModel"/>。回调注入手法与发布页/合集页一致：
/// StorageProvider 挂在 TopLevel 上，VM 拿不到它。
/// </summary>
public partial class ComposeView : UserControl
{
    private ComposeViewModel? _vm;

    public ComposeView()
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
        if (DataContext is not ComposeViewModel vm) return;

        vm.PickMediaAsync = PickMediaAsync;
        vm.PickLocalAudioAsync = PickLocalAudioAsync;
        vm.PickBgmAsync = PickBgmAsync;
        vm.PickOutputFolderAsync = PickOutputFolderAsync;
        _vm = vm;
    }

    private void UnwireVm()
    {
        if (_vm is null) return;
        _vm.PickMediaAsync = null;
        _vm.PickLocalAudioAsync = null;
        _vm.PickBgmAsync = null;
        _vm.PickOutputFolderAsync = null;
        _vm = null;
    }

    /// <summary>选素材（图片 + 视频，可多选；真假类型由 VM 按扩展名兜底判断）</summary>
    private async Task<IReadOnlyList<string>?> PickMediaAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return null;

        try
        {
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择素材（图片 / 视频，可多选；顺序即播放顺序）",
                AllowMultiple = true,
                FileTypeFilter =
                [
                    new FilePickerFileType("图片与视频")
                    {
                        Patterns =
                        [
                            "*.jpg", "*.jpeg", "*.png", "*.webp", "*.bmp",
                            "*.mp4", "*.mov", "*.mkv", "*.avi", "*.flv", "*.wmv", "*.m4v", "*.webm",
                        ]
                    },
                    new FilePickerFileType("图片") { Patterns = ["*.jpg", "*.jpeg", "*.png", "*.webp", "*.bmp"] },
                    new FilePickerFileType("视频") { Patterns = ["*.mp4", "*.mov", "*.mkv", "*.avi", "*.flv", "*.wmv", "*.m4v", "*.webm"] },
                ],
            });
            return [.. files.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!)];
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "素材选择器打开失败");
            return null;
        }
    }

    /// <summary>选本地配音音频（mp3 / wav / m4a / aac）</summary>
    private async Task<string?> PickLocalAudioAsync() => await PickAudioAsync("选择本地配音文件");

    /// <summary>选背景音乐（比配音多收 flac —— 素材库里常见）</summary>
    private async Task<string?> PickBgmAsync() => await PickAudioAsync("选择背景音乐");

    private async Task<string?> PickAudioAsync(string title)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return null;

        try
        {
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("音频文件")
                    {
                        Patterns = ["*.mp3", "*.wav", "*.m4a", "*.aac", "*.flac"],
                    }
                ],
            });
            return files.Count > 0 ? files[0].TryGetLocalPath() : null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "音频选择器打开失败");
            return null;
        }
    }

    /// <summary>选输出目录（起始位置 = 当前输出目录，不存在则让系统用默认位置）</summary>
    private async Task<string?> PickOutputFolderAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return null;

        try
        {
            IStorageFolder? start = null;
            if (!string.IsNullOrWhiteSpace(_vm?.OutputDirectory) && Directory.Exists(_vm.OutputDirectory))
                start = await top.StorageProvider.TryGetFolderFromPathAsync(_vm.OutputDirectory);

            var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择输出目录（每条视频在其中落成一个发布单元子文件夹）",
                AllowMultiple = false,
                SuggestedStartLocation = start,
            });
            return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "输出目录选择器打开失败");
            return null;
        }
    }
}
