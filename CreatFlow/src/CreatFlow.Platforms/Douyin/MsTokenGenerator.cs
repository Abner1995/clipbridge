namespace CreatFlow.Platforms.Douyin;

/// <summary>
/// msToken 生成器 — 对标 douyin-api 的 get_ms_token()。
///
/// 格式: 120 字符，字符集 A-Za-z0-9=（标准 Base64 字符集，非 URL-safe）。
/// douyin-api 优先使用 cookie 中的 msToken，无 cookie 时随机生成。
///
/// 我们当前为随机模式（无 WebView2 实时提取），格式对齐 douyin-api。
/// </summary>
public static class MsTokenGenerator
{
    private const string BaseChars = "ABCDEFGHIGKLMNOPQRSTUVWXYZabcdefghigklmnopqrstuvwxyz0123456789=";
    private const int TokenLength = 120;

    /// <summary>
    /// 生成随机 msToken（120 字符，标准 Base64 字符集）。
    /// 每次调用返回不同值。
    /// </summary>
    public static string Generate()
    {
        var chars = new char[TokenLength];
        for (int i = 0; i < TokenLength; i++)
            chars[i] = BaseChars[Random.Shared.Next(BaseChars.Length)];
        return new string(chars);
    }
}
