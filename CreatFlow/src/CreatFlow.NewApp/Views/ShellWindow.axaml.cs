using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using CreatFlow.NewApp.ViewModels;

namespace CreatFlow.NewApp.Views;

public partial class ShellWindow : Window
{
    /// <summary>已访问页面 → 保活叠层 ContentControl（页面切走不销毁 → 页内 WebView 保活）</summary>
    private readonly Dictionary<ViewModelBase, ContentControl> _pageHosts = [];
    private readonly List<ContentControl> _hostOrder = [];

    public ShellWindow()
    {
        InitializeComponent();
        LoginWindow.LoadWindowIcon(this);

        DataContextChanged += OnDataContextChangedForPages;
    }

    private void OnDataContextChangedForPages(object? sender, EventArgs e)
    {
        DataContextChanged -= OnDataContextChangedForPages;
        if (DataContext is ShellViewModel vm)
        {
            vm.PropertyChanged += OnShellVmPropertyChanged;
            vm.OnPagePreloaded = EnsureHostOnly; // 静默预载：建隐藏宿主，不切可见性
            if (vm.CurrentPage != null) SyncPageOverlay(vm.CurrentPage);
        }
    }

    private void OnShellVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.CurrentPage)
            && DataContext is ShellViewModel vm && vm.CurrentPage != null)
        {
            SyncPageOverlay(vm.CurrentPage);
        }
    }

    /// <summary>
    /// 页面保活叠层：访问过的页面各自常驻一个 ContentControl（IsVisible 切换，不摘树）。
    /// 平台账号页内嵌账号 WebView —— 摘树会销毁 NativeWebView 控制器，必须保活。
    /// </summary>
    private void SyncPageOverlay(ViewModelBase page)
    {
        var host = EnsureHost(page);
        if (host == null) return;
        foreach (var h in _hostOrder)
        {
            var visible = ReferenceEquals(h, host);
            // 隐藏页面（尤其平台账号页里开着 WebView2）之前先把焦点收回 ——
            // 被隐藏的 WebView2 在窗口激活恢复焦点时会调用失效控制器的 MoveFocus，
            // E_INVALIDARG 从 WndProc 冒出去直接杀进程（2026-11-04 两次实机崩溃，见 WebViewFocusGuard）
            if (!visible && h.IsVisible) WebViewFocusGuard.ClearFocusInside(h);
            h.IsVisible = visible;
        }
    }

    /// <summary>静默预载页：只建隐藏宿主，不切换可见性（后台功能借页内控件用）</summary>
    private void EnsureHostOnly(ViewModelBase page) => EnsureHost(page);

    /// <summary>按需创建页面的保活宿主（隐藏）；已存在直接返回</summary>
    private ContentControl? EnsureHost(ViewModelBase page)
    {
        if (_pageHosts.TryGetValue(page, out var host)) return host;

        var view = this.FindDataTemplate(page)?.Build(page) as Control;
        if (view == null) return null;
        // ⚠️ Build() 直连绕过了 ContentPresenter —— 模板不会自动赋 DataContext，
        // 不设则页面继承壳的 ShellViewModel，所有 x:DataType 编译绑定求值强转崩溃
        // （症状:页面空白 + 底部进度条绑定失败默认可见「只剩进度条」）
        view.DataContext = page;
        host = new ContentControl
        {
            Content = view,
            IsVisible = false,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
        };
        _pageHosts[page] = host;
        _hostOrder.Add(host);
        PageHost.Children.Add(host);
        return host;
    }

    // ═══ 侧栏导航事件（多 ListBox 分组 → 点击直驱 Navigate，单向绑定做高亮） ═══

    private void NavList_ContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container?.DataContext is NavItem item)
            e.Container.IsEnabled = item.IsEnabled; // 占位/禁用项置灰
    }

    private void NavList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if ((sender as ListBox)?.SelectedItem is NavItem item)
            (DataContext as ShellViewModel)?.Navigate(item);
    }

    // ═══ 壳关闭：清空坞标签 → 页内 TabDockView 监听 Reset 逐个 Teardown WebView（档案保留） ═══

    protected override void OnClosed(EventArgs e)
    {
        (DataContext as ShellViewModel)?.Dock.CloseAllTabs(); // 触发宿主集合 Reset → TeardownAll
        if (DataContext is ShellViewModel vm) vm.PropertyChanged -= OnShellVmPropertyChanged;
        base.OnClosed(e);
    }
}
