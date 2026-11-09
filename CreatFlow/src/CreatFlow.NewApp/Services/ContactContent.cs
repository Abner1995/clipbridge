using System.Text.Json;
using CreatFlow.NewApp.Models;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 「联系我们」页内容解析（纯函数，无网络/无 UI，便于自检）。
///
/// 内容源：服务端系统配置 <c>contact.content</c>（category=contact, public=true, HTML）。
/// 管理员在 /admin/settings →「联系我们」里随时改；桌面端每次进页拉一次。
/// 服务器不可用或未配置时，回落本地 appsettings 的 Contact:Email / Contact:WorkHours，
/// 保证页面永远有可读内容。
///
/// 安全边界：HTML 交给 Avalonia.HtmlRenderer 渲染 —— 它是纯排版引擎，不执行 JavaScript；
/// 链接在 View 层统一拦截，只放行 http/https/mailto（见 <see cref="ResolveLink"/>）。
/// </summary>
internal static class ContactContent
{
    /// <summary>服务端配置键（公开配置字典按完整 key 返回）</summary>
    public const string ConfigKey = "contact.content";

    /// <summary>从 /api/config/public 响应里取 HTML；没有/空串返回 null</summary>
    public static string? ExtractHtml(PublicConfigResponse? response)
    {
        if (response?.Contact is null) return null;
        if (!response.Contact.TryGetValue(ConfigKey, out var value)) return null;

        var html = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            // 理论上只有字符串；真出现对象/数组时保留原文，至少不是静默空页
            _ => value.GetRawText(),
        };

        return string.IsNullOrWhiteSpace(html) ? null : html;
    }

    /// <summary>本地兜底内容（服务器不可用/未配置时）</summary>
    public static string BuildFallbackHtml() =>
        BuildFallbackHtml(AppConfig.ContactEmails, AppConfig.ContactWorkHours);

    /// <summary>本地兜底内容（可注入，便于自检）——多个邮箱必须各占一行</summary>
    public static string BuildFallbackHtml(IReadOnlyList<string> emails, string workHours)
    {
        var emailLines = string.Join("<br/>", emails.Select(EscapeHtml));
        var hours = EscapeHtml(workHours);

        return "<p>如需帮助，请通过以下任一方式联系我们：</p>"
             + $"<p><b>客服邮箱</b><br/>{emailLines}</p>"
             + $"<p><b>工作时间</b><br/>{hours}</p>";
    }

    /// <summary>
    /// 把 HTML 里的 href 归一到可打开的 Uri：
    /// http/https 原样；mailto: 原样；裸 www. 补 https；其余（javascript:/file: 等）一律拒绝。
    /// </summary>
    public static Uri? ResolveLink(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var link = raw.Trim();

        if (link.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            return Uri.TryCreate(link, UriKind.Absolute, out var mail) ? mail : null;

        if (link.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            link = "https://" + link;

        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)) return null;
        return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps ? uri : null;
    }

    /// <summary>HTML 文本转义（兜底内容里的邮箱/工作时间）</summary>
    public static string EscapeHtml(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&#39;");
    }
}
