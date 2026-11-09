using Avalonia.Controls;
using TheArtOfDev.HtmlRenderer.Avalonia;
using TheArtOfDev.HtmlRenderer.Core.Entities;

namespace CreatFlow.NewApp.Views;

public partial class ContactView : UserControl
{
    public ContactView()
    {
        InitializeComponent();

        // 事件签名是 HtmlRendererRoutedEventArgs<HtmlLinkClickedEventArgs>（不是普通 EventHandler），
        // XAML 里挂不上，统一在代码里订阅。
        HtmlBlock.LinkClicked += OnLinkClicked;
    }

    /// <summary>
    /// HTML 里的链接点击：HtmlRenderer 本身不导航 —— 统一交给系统默认浏览器/邮件客户端。
    /// 只放行 http/https/mailto（ContactContent.ResolveLink），javascript:/file: 等一律忽略。
    /// </summary>
    private void OnLinkClicked(
        object? sender,
        HtmlRendererRoutedEventArgs<HtmlLinkClickedEventArgs> e)
    {
        e.Event.Handled = true;

        var uri = ContactContent.ResolveLink(e.Event.Link);
        if (uri is null) return;

        try
        {
            if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
                _ = launcher.LaunchUriAsync(uri);
        }
        catch
        {
            // 没有默认浏览器/邮件客户端不算页面错误：内容本身仍可读
        }
    }
}
