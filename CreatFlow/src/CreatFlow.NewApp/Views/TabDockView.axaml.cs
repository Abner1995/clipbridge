using System.Collections.Specialized;
using CreatFlow.NewApp.ViewModels;

namespace CreatFlow.NewApp.Views;

/// <summary>
/// 账号 WebView 宿主层 —— 与壳导航页 ContentControl 同格叠放(标签坞形态 A)。
///
/// DataContext = AccountDockViewModel(ShellWindow 绑定注入)。code-behind 按
/// Dock.Tabs 集合命令式同步 DockTabHostView(WebView 生命周期需确定性控制,
/// 与平台项目 code-behind WebView 先例一致):
///   - Add    → 建宿主 + DataContext=tab + 入 HostPanel(懒加载:首显才导航)
///   - Remove → host.Teardown()(退订+close controller)+ 摘树,**档案目录保留**
/// 切标签只切宿主根 IsVisible,不摘树不重建。
/// </summary>
public partial class TabDockView : UserControl
{
    private AccountDockViewModel? _dock;
    private readonly Dictionary<AccountDockTabViewModel, DockTabHostView> _hosts = [];

    public TabDockView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        UnsubscribeDock();
        _dock = DataContext as AccountDockViewModel;
        if (_dock == null) return;

        _dock.Tabs.CollectionChanged += OnTabsChanged;
        foreach (var tab in _dock.Tabs.ToList())
            AddHost(tab);
    }

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var tab in _hosts.Keys.ToList())
                RemoveHost(tab);
            return;
        }
        if (e.NewItems != null)
            foreach (AccountDockTabViewModel tab in e.NewItems)
                AddHost(tab);
        if (e.OldItems != null)
            foreach (AccountDockTabViewModel tab in e.OldItems)
                RemoveHost(tab);
    }

    private void AddHost(AccountDockTabViewModel tab)
    {
        if (_hosts.ContainsKey(tab)) return;
        var host = new DockTabHostView { DataContext = tab };
        _hosts[tab] = host;
        HostPanel.Children.Add(host);
    }

    private void RemoveHost(AccountDockTabViewModel tab)
    {
        if (!_hosts.TryGetValue(tab, out var host)) return;
        _hosts.Remove(tab);
        host.Teardown();                       // 退订 + best-effort close controller
        HostPanel.Children.Remove(host);       // 摘树(档案保留,重开秒回登录态)
    }

    /// <summary>壳 OnClosed 时全量释放(不删档案);随后由 Dock VM CloseAllTabs 清集合</summary>
    public void TeardownAllBestEffort()
    {
        foreach (var host in _hosts.Values.ToList())
        {
            host.Teardown();
            HostPanel.Children.Remove(host);
        }
        _hosts.Clear();
        UnsubscribeDock();
    }

    private void UnsubscribeDock()
    {
        if (_dock != null)
            _dock.Tabs.CollectionChanged -= OnTabsChanged;
        _dock = null;
    }
}
