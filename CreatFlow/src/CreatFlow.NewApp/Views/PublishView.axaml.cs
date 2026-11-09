using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using CreatFlow.NewApp.ViewModels;

namespace CreatFlow.NewApp.Views;

/// <summary>
/// 发布页 code-behind —— 只承载 View 专属能力（文件/文件夹选择器 + 拖放），业务全在
/// <see cref="PublishViewModel"/>（工作台 + 编辑器 + 预设 + 排序四视图）。
///
/// 为什么注入回调而不是让 VM 直接调 StorageProvider：StorageProvider 挂在 TopLevel 上，
/// VM 拿不到它。合集页用的是同一套手法（<c>CollectionView.BrowseCoverAsync</c>）。
/// </summary>
public partial class PublishView : UserControl
{
    private PublishViewModel? _vm;

    public PublishView()
    {
        InitializeComponent();

        // 整页接收拖放（导入卡上写着「或把文件夹拖到这里」，得真能拖）——
        // 挂整页而不是挂那张卡：卡片只有一行高，按提示去瞄它太苛刻。
        // 非工作台视图一律不收（DragEffects=None），免得在编辑器里拖进东西。
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);

        DataContextChanged += (_, _) => WireVm();
        AttachedToVisualTree += (_, _) => WireVm();
        DetachedFromVisualTree += (_, _) => UnwireVm();
    }

    /// <summary>拖放载荷里有没有本地文件/文件夹（且当前在工作台）</summary>
    private bool AcceptsDrag(DragEventArgs e) =>
        _vm?.ShowWorkbench == true && e.DataTransfer.Contains(DataFormat.File);

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        // 光标本身就是唯一的悬停反馈：Copy = 带加号的「可放下」，None = 禁止
        e.DragEffects = AcceptsDrag(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e) => e.Handled = true;

    /// <summary>平台卡「话题」面板的搜索框 —— 回车即搜索（2.6.3）</summary>
    private void OnTopicSearchKeyDown(object? sender, KeyEventArgs e) =>
        InvokeTopicCommand(sender, e, _vm?.SearchTopicSuggestionsCommand);

    /// <summary>平台卡「话题」面板的手动输入框 —— 回车即添加（2.6.3）</summary>
    private void OnTopicManualKeyDown(object? sender, KeyEventArgs e) =>
        InvokeTopicCommand(sender, e, _vm?.AddManualTopicCommand);

    private static void InvokeTopicCommand(
        object? sender, KeyEventArgs e, IRelayCommand<TargetRowViewModel?>? command)
    {
        if (e.Key != Key.Enter) return;
        if (sender is not Control { DataContext: TargetRowViewModel row }) return;
        e.Handled = true;
        command?.Execute(row);
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (!AcceptsDrag(e)) return;
        e.Handled = true;

        var paths = e.DataTransfer.TryGetFiles()?
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .ToList() ?? [];
        if (paths.Count > 0) _ = _vm!.ImportDroppedAsync(paths);
    }

    private void WireVm()
    {
        if (_vm == DataContext) return;
        UnwireVm();
        if (DataContext is not PublishViewModel vm) return;

        vm.FolderPicker = PickFoldersAsync;
        vm.ParentFolderPicker = PickParentFolderAsync;
        vm.VideoPicker = PickVideosAsync;
        vm.CoverPicker = PickCoverAsync;
        _vm = vm;
    }

    private void UnwireVm()
    {
        if (_vm is null) return;
        _vm.FolderPicker = null;
        _vm.ParentFolderPicker = null;
        _vm.VideoPicker = null;
        _vm.CoverPicker = null;
        _vm = null;
    }

    /// <summary>选「新建模板」的父目录（单选）—— 模板文件夹会建在它里面</summary>
    private async Task<string?> PickParentFolderAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return null;

        try
        {
            var start = await ResolveStartFolderAsync(top, _vm?.LastImportRoot);
            var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择放置模板的目录（会在里面建「发布单元模板」）",
                AllowMultiple = false,
                SuggestedStartLocation = start,
            });
            return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "模板父目录选择器打开失败");
            return null;
        }
    }

    /// <summary>
    /// 选导入目录（**可多选**）。起始位置优先用上次导入的目录 —— 日更场景这是高频入口，
    /// 每次都从「文档」开始点会烦人（设计文档 §9）。
    /// 条数上限不在这里卡：系统选择器给不了个数限制，统一由导入那一步截断并提示。
    /// </summary>
    private async Task<IReadOnlyList<string>> PickFoldersAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return [];

        try
        {
            var start = await ResolveStartFolderAsync(top, _vm?.LastImportRoot);
            var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择导入目录（可多选；子文件夹 = 一条视频，散装视频也各算一条）",
                AllowMultiple = true,
                SuggestedStartLocation = start,
            });
            return [.. folders.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!)];
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "导入文件夹选择器打开失败");
            return [];
        }
    }

    /// <summary>选一堆视频文件（每个文件一条）</summary>
    private async Task<IReadOnlyList<string>> PickVideosAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return [];

        try
        {
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择视频（可多选）",
                AllowMultiple = true,
                FileTypeFilter =
                [
                    new FilePickerFileType("视频文件")
                    {
                        Patterns = ["*.mp4", "*.mov", "*.mkv", "*.avi", "*.flv", "*.wmv", "*.m4v", "*.webm"]
                    }
                ],
            });
            return files.Select(f => f.TryGetLocalPath())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p!)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "视频文件选择器打开失败");
            return [];
        }
    }

    /// <summary>选封面图</summary>
    private async Task<string?> PickCoverAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return null;

        try
        {
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择封面图",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("图片文件")
                    {
                        Patterns = ["*.jpg", "*.jpeg", "*.png", "*.webp", "*.bmp"]
                    }
                ],
            });
            return files.Count > 0 ? files[0].TryGetLocalPath() : null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "封面选择器打开失败");
            return null;
        }
    }

    /// <summary>
    /// 把上次导入的目录换算成 IStorageFolder 当起始位置。
    /// 目录已被删/改名时返回 null（让系统用默认位置，不要拿一个不存在的路径去炸选择器）。
    /// </summary>
    private static async Task<IStorageFolder?> ResolveStartFolderAsync(TopLevel top, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            if (!Directory.Exists(path)) return null;
            return await top.StorageProvider.TryGetFolderFromPathAsync(path);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "起始目录解析失败 {Path}", path);
            return null;
        }
    }
}
