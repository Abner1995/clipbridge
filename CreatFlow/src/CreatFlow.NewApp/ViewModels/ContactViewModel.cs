using Material.Icons;
using Serilog;

namespace CreatFlow.NewApp.ViewModels;

/// <summary>
/// 「联系我们」页 ViewModel。
///
/// 内容来自服务端公开配置 GET /api/config/public 的 <c>contact.content</c>（HTML 富文本），
/// 管理员在后台「系统设置 → 联系我们」随时改，桌面端每次进页重拉（INavigationAware），
/// 不用发版。服务器不可用/未配置时回落本地 appsettings（AppConfig.ContactEmails/WorkHours）。
///
/// 渲染在 View 层用 Avalonia.HtmlRenderer（不执行脚本）；链接点击由 View 拦截，
/// 只放行 http/https/mailto（ContactContent.ResolveLink）。
/// </summary>
public partial class ContactViewModel : ViewModelBase, INavigationAware
{
    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _html = ContactContent.BuildFallbackHtml();

    [ObservableProperty]
    private string? _notice;

    /// <summary>页头图标（矢量）</summary>
    public MaterialIconKind HeaderIcon => MaterialIconKind.Headset;

    // ── 单机版：没有服务端，内容直接来自 appsettings，用原生控件排版 ──
    // （2026-10-28 用户要求：优化样式、去掉刷新按钮。HTML 渲染器的 Padding
    //   会把首行/每行开头裁掉，本机内容改走原生 TextBlock/SelectableTextBlock。）

    /// <summary>单机版：显示本机联系方式卡（原生排版）</summary>
    public bool ShowLocalCard => AppConfig.IsStandalone;

    /// <summary>联网版：显示服务端 HTML 富文本卡</summary>
    public bool ShowHtml => !AppConfig.IsStandalone;

    /// <summary>联网版才有刷新（单机版内容来自本地 appsettings，刷新没有意义）</summary>
    public bool ShowRefresh => !AppConfig.IsStandalone;

    /// <summary>本机内容：引导句 / 多邮箱（逐个换行） / 工作时间</summary>
    public string IntroText => "如需帮助，请通过以下任一方式联系我们：";
    public string EmailsText => string.Join("\n", AppConfig.ContactEmails);
    public string WorkHours => AppConfig.ContactWorkHours;

    public bool HasNotice => !string.IsNullOrEmpty(Notice);
    public bool IsEmpty => string.IsNullOrWhiteSpace(Html);

    /// <summary>空态只在联网版出现（单机版永远有本机卡，且没有可点的刷新）</summary>
    public bool ShowEmpty => !AppConfig.IsStandalone && IsEmpty;

    partial void OnNoticeChanged(string? value) => OnPropertyChanged(nameof(HasNotice));

    partial void OnHtmlChanged(string value)
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ShowEmpty));
    }

    public void OnNavigatedTo()
    {
        if (!IsLoading) LoadCommand.Execute(null);
    }

    [RelayCommand]
    private async Task Load()
    {
        if (IsLoading) return;
        IsLoading = true;
        Notice = null;

        // 冒烟不碰网络：直接展示本地兜底（同时覆盖「服务器不可用」的降级路径）
        if (App.SmokeMode)
        {
            Html = ContactContent.BuildFallbackHtml();
            Notice = "冒烟模式：显示本地默认联系方式";
            IsLoading = false;
            return;
        }

        // 单机版没有服务端富文本：直接用 appsettings 兜底（不发请求，也没有"连不上"的提示）
        if (AppConfig.IsStandalone)
        {
            Html = ContactContent.BuildFallbackHtml();
            IsLoading = false;
            return;
        }

        try
        {
            var resp = await ApiClient.GetAsync<PublicConfigResponse>(
                "/api/config/public", authenticated: false);
            var html = ContactContent.ExtractHtml(resp);

            if (html is null)
            {
                Html = ContactContent.BuildFallbackHtml();
                Notice = "服务器还没有配置联系方式，当前显示本地默认内容。";
            }
            else
            {
                Html = html;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[Contact] 拉取联系我们内容失败，回落本地默认");
            Html = ContactContent.BuildFallbackHtml();
            Notice = "暂时连不上服务器，当前显示本地默认内容。";
        }
        finally
        {
            IsLoading = false;
        }
    }
}
