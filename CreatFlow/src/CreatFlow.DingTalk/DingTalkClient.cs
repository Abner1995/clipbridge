using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Web;

namespace CreatFlow.DingTalk;

/// <summary>
/// 钉钉自定义机器人群消息客户端。
///
/// 签名算法（HMAC-SHA256）:
///   timestamp = now_ms()
///   string_to_sign = $"{timestamp}\n{secret}"
///   sign = url_encode(base64(hmac_sha256(secret, string_to_sign)))
///   url = $"https://oapi.dingtalk.com/robot/send?access_token={key}&timestamp={ts}&sign={sign}"
///
/// 参考: https://open.dingtalk.com/document/orgapp/customize-robot-security-settings
/// </summary>
public static class DingTalkClient
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private const string BaseUrl = "https://oapi.dingtalk.com/robot/send";

    /// <summary>
    /// 发送文本消息到钉钉群机器人。
    /// </summary>
    /// <param name="webhookKey">机器人的 access_token（从 Webhook URL 中提取）</param>
    /// <param name="secret">加签密钥</param>
    /// <param name="content">消息内容</param>
    /// <param name="atMobiles">@ 的手机号列表</param>
    /// <param name="atUserIds">@ 的钉钉用户ID列表</param>
    /// <param name="isAtAll">是否 @ 所有人</param>
    /// <returns>(success, errorMessage)</returns>
    public static async Task<(bool Success, string? Error)> SendTextAsync(
        string webhookKey,
        string secret,
        string content,
        string[]? atMobiles = null,
        string[]? atUserIds = null,
        bool isAtAll = false)
    {
        var body = new
        {
            msgtype = "text",
            text = new { content },
            at = new
            {
                atMobiles = atMobiles ?? [],
                atUserIds = atUserIds ?? [],
                isAtAll = isAtAll
            }
        };

        return await SendAsync(webhookKey, secret, body);
    }

    /// <summary>
    /// 发送 Markdown 消息到钉钉群机器人。
    /// </summary>
    /// <param name="webhookKey">机器人的 access_token</param>
    /// <param name="secret">加签密钥</param>
    /// <param name="title">消息标题</param>
    /// <param name="text">Markdown 内容</param>
    /// <param name="atMobiles">@ 的手机号列表</param>
    /// <param name="atUserIds">@ 的钉钉用户ID列表</param>
    /// <param name="isAtAll">是否 @ 所有人</param>
    /// <returns>(success, errorMessage)</returns>
    public static async Task<(bool Success, string? Error)> SendMarkdownAsync(
        string webhookKey,
        string secret,
        string title,
        string text,
        string[]? atMobiles = null,
        string[]? atUserIds = null,
        bool isAtAll = false)
    {
        var body = new
        {
            msgtype = "markdown",
            markdown = new { title, text },
            at = new
            {
                atMobiles = atMobiles ?? [],
                atUserIds = atUserIds ?? [],
                isAtAll = isAtAll
            }
        };

        return await SendAsync(webhookKey, secret, body);
    }

    /// <summary>
    /// 发送消息（内部方法，处理签名 + HTTP POST）。
    /// </summary>
    private static async Task<(bool Success, string? Error)> SendAsync(
        string webhookKey, string secret, object body)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var sign = ComputeSign(timestamp, secret);
        var url = $"{BaseUrl}?access_token={webhookKey}&timestamp={timestamp}&sign={sign}";

        try
        {
            var response = await Http.PostAsJsonAsync(url, body);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                return (false, $"HTTP {(int)response.StatusCode}: {errorBody}");
            }

            var result = await response.Content.ReadFromJsonAsync<DingTalkResponse>();
            if (result == null)
                return (false, "钉钉返回空响应");

            if (result.Errcode != 0)
                return (false, $"钉钉错误 [{result.Errcode}]: {result.Errmsg}");

            return (true, null);
        }
        catch (TaskCanceledException)
        {
            return (false, "请求超时，请检查网络或 Webhook 地址是否正确");
        }
        catch (HttpRequestException ex)
        {
            return (false, $"网络错误: {ex.Message}");
        }
        catch (Exception ex)
        {
            return (false, $"发送失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 计算钉钉加签签名。
    /// sign = URL-encode( base64( HMAC-SHA256(secret, "{timestamp}\n{secret}") ) )
    /// </summary>
    public static string ComputeSign(string timestamp, string secret)
    {
        var stringToSign = $"{timestamp}\n{secret}";
        var secretBytes = Encoding.UTF8.GetBytes(secret);
        var signBytes = Encoding.UTF8.GetBytes(stringToSign);
        using var hmac = new HMACSHA256(secretBytes);
        var hash = hmac.ComputeHash(signBytes);
        var sign = Convert.ToBase64String(hash);
        return HttpUtility.UrlEncode(sign);
    }
}

/// <summary>
/// 钉钉 API 通用响应。
/// </summary>
internal sealed record DingTalkResponse
{
    public int Errcode { get; init; }
    public string Errmsg { get; init; } = "";
}
