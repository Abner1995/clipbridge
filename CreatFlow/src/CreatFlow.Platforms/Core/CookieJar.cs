namespace CreatFlow.Platforms.Core;

/// <summary>
/// Cookie 容器 — 极简实现：用字符串拼接，无 Dictionary、无 lock、无 LINQ。
/// </summary>
public sealed class CookieJar
{
    private string _cookie;

    public CookieJar(string cookie)
    {
        _cookie = cookie ?? "";
    }

    public string GetCookieString()
    {
        return _cookie;
    }

    public void FeedSetCookies(IEnumerable<string> setCookieHeaders)
    {
        foreach (var header in setCookieHeaders)
        {
            if (string.IsNullOrWhiteSpace(header)) continue;
            var semi = header.IndexOf(';');
            var pair = semi >= 0 ? header[..semi] : header;
            if (pair.IndexOf('=') <= 0) continue;
            _cookie = _cookie + "; " + pair;
        }
    }
}
