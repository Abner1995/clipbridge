using Avalonia.Controls;
using Avalonia.Input;
using CreatFlow.NewApp.ViewModels;

namespace CreatFlow.NewApp.Views;

public partial class PlatformView : UserControl
{
    public PlatformView()
    {
        InitializeComponent();
    }

    /// <summary>平台列表点击：直接驱动 VM（避免多级 SelectedItem 双向绑定写回坑，与导航同款）</summary>
    private void PlatformList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox lb || lb.SelectedItem is not PlatformGroup group) return;
        (DataContext as PlatformViewModel)!.SelectedGroup = group;
    }

    /// <summary>点标签 = 激活该账号 WebView（Chrome 心智；首次激活懒加载建宿主）</summary>
    private void PlatformTab_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: AccountDockTabViewModel tab }) return;
        (DataContext as PlatformViewModel)?.Dock.Activate(tab);
    }

    /// <summary>× = 关闭标签（Ready 后释放渲染进程但档案保留；空壳登录取消自动回滚）</summary>
    private async void PlatformTabClose_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: AccountDockTabViewModel tab }) return;
        var dock = (DataContext as PlatformViewModel)?.Dock;
        if (dock == null) return;
        try { await dock.CloseTabAsync(tab); }
        catch (Exception ex) { Log.Warning(ex, "Close dock tab failed"); }
    }
}
