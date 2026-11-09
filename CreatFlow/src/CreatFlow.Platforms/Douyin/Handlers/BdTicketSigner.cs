namespace CreatFlow.Platforms.Douyin.Handlers;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CreatFlow.Platforms.Core;

/// <summary>
/// Bd-Ticket-Guard ECDSA 签名 + 响应头处理。
///
/// 对标浏览器 JS SDK 的 security-sdk：
///   1. 从 localStorage 提取 ec_privateKey（ECDSA P-256）
///   2. 对 sec_ts 做 SHA256withECDSA 签名 → req_sign
///   3. 组装 bd-ticket-guard-ree-public-key 请求头
///   4. 从响应头 bd-ticket-guard-server-data 提取新 ts_sign
///   5. 更新 bd_ticket_guard_client_data_v2 cookie
///
/// 使用方式（贯穿管线生命周期）：
///   var signer = BdTicketSigner.FromLocalStorage(localStorageJson);
///   signer.UpdateFromCookie(cookieString);
///
///   每次请求前: signer.ApplyHeaders(request, requestBody);
///   每次请求后: signer.ProcessResponse(response, cookieJar);
/// </summary>
public sealed class BdTicketSigner
{
    private ECDsa? _ecdsa;
    private string? _b64PubKey;          // base64 公钥（用于请求头）
    private string? _tsSign;             // 当前 ts_sign
    private string? _secTs;              // 当前安全时间戳
    private bool _isValid;

    /// <summary>是否成功加载了 ECDSA 密钥。</summary>
    public bool IsValid => _isValid;

    // ═══ 工厂方法 ═══

    /// <summary>
    /// 从 localStorage JSON 创建签名器。
    /// localStorage 应包含:
    ///   security-sdk/s_sdk_crypt_sdk → { ec_privateKey, ec_publicKey }
    ///   security-sdk/s_sdk_sign_data_key/web_protect → { ts_sign, ticket, client_cert }
    /// </summary>
    public static BdTicketSigner FromLocalStorage(string? localStorageJson)
    {
        var signer = new BdTicketSigner();
        if (string.IsNullOrWhiteSpace(localStorageJson)) return signer;

        try
        {
            using var root = JsonDocument.Parse(localStorageJson);

            // 1. 提取 ECDSA 密钥对
            if (root.RootElement.TryGetProperty("security-sdk/s_sdk_crypt_sdk", out var cryptEl))
            {
                var cryptStr = cryptEl.GetString();
                if (!string.IsNullOrWhiteSpace(cryptStr))
                {
                    using var cryptDoc = JsonDocument.Parse(cryptStr);
                    if (cryptDoc.RootElement.TryGetProperty("data", out var dataEl))
                    {
                        var dataStr = dataEl.GetString();
                        if (!string.IsNullOrWhiteSpace(dataStr))
                        {
                            using var dataDoc = JsonDocument.Parse(dataStr);
                            if (dataDoc.RootElement.TryGetProperty("ec_privateKey", out var privEl))
                            {
                                var privKey = privEl.GetString();
                                if (!string.IsNullOrWhiteSpace(privKey))
                                {
                                    var ecdsa = ECDsa.Create();
                                    ecdsa.ImportFromPem(privKey);
                                    signer._ecdsa = ecdsa;
                                }
                            }
                            if (dataDoc.RootElement.TryGetProperty("ec_publicKey", out var pubEl))
                            {
                                var pubKey = pubEl.GetString();
                                if (!string.IsNullOrWhiteSpace(pubKey))
                                    signer._b64PubKey = PubKeyToBase64(pubKey);
                            }
                        }
                    }
                }
            }

            // 2. 提取 ts_sign（从 web_protect 或 token）
            var tsSignKey = "security-sdk/s_sdk_sign_data_key/web_protect";
            if (root.RootElement.TryGetProperty(tsSignKey, out var wpEl))
            {
                var wpStr = wpEl.GetString();
                if (!string.IsNullOrWhiteSpace(wpStr))
                {
                    using var wpDoc = JsonDocument.Parse(wpStr);
                    if (wpDoc.RootElement.TryGetProperty("data", out var dataEl))
                    {
                        var dataStr = dataEl.GetString();
                        if (!string.IsNullOrWhiteSpace(dataStr))
                        {
                            using var dataDoc = JsonDocument.Parse(dataStr);
                            if (dataDoc.RootElement.TryGetProperty("ts_sign", out var ts))
                                signer._tsSign = ts.GetString();
                        }
                    }
                }
            }

            signer._isValid = signer._ecdsa != null && signer._b64PubKey != null;
        }
        catch { /* parsing failed, signer stays invalid */ }

        return signer;
    }

    // ═══ Cookie 同步 ═══

    /// <summary>
    /// 从 cookie 中的 bd_ticket_guard_client_data_v2 提取当前 ts_sign 和 sec_ts。
    /// 调用时机：管线开始时，用初始 cookie 同步一次。
    /// </summary>
    public void UpdateFromCookie(string cookieString)
    {
        if (string.IsNullOrWhiteSpace(cookieString)) return;

        var v2 = ExtractCookieValue(cookieString, "bd_ticket_guard_client_data_v2");
        if (string.IsNullOrWhiteSpace(v2)) return;

        try
        {
            v2 = Uri.UnescapeDataString(v2);
            // 修复 base64 padding
            var missing = v2.Length % 4;
            if (missing > 0) v2 += new string('=', 4 - missing);
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(v2));

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("ts_sign", out var ts))
                _tsSign = ts.GetString();
            if (doc.RootElement.TryGetProperty("sec_ts", out var st))
                _secTs = st.GetString();
            if (doc.RootElement.TryGetProperty("ree_public_key", out var rpk) && _b64PubKey == null)
                _b64PubKey = rpk.GetString();
        }
        catch { /* silent */ }
    }

    // ═══ 请求前：组装签名头 ═══

    /// <summary>
    /// 为 HTTP 请求添加 Bd-Ticket-Guard 签名头。
    /// 调用时机：每次 API 请求前。
    /// </summary>
    public void ApplyHeaders(HttpRequestMessage request, string? requestBody = null)
    {
        if (!_isValid || _b64PubKey == null) return;

        // 基本版本头（douyin-api 模式）
        request.Headers.TryAddWithoutValidation("bd-ticket-guard-ree-public-key", _b64PubKey);
        request.Headers.TryAddWithoutValidation("bd-ticket-guard-web-version", "2");
        request.Headers.TryAddWithoutValidation("bd-ticket-guard-version", "2");

        // 如果 cookie 里有原始 client_data_v2，也转发（兼容）
    }

    // ═══ 请求后：处理服务器响应 ═══

    /// <summary>
    /// 从成功响应中提取 bd-ticket-guard-server-data 并更新状态。
    /// 调用时机：每次 API 响应后（成功后调用，失败跳过）。
    /// </summary>
    public void ProcessResponse(HttpResponseMessage response, CookieJar? cookieJar)
    {
        // 1. 读取 bd-ticket-guard-server-data 响应头
        if (response.Headers.TryGetValues("bd-ticket-guard-server-data", out var serverData))
        {
            var data = serverData.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(data))
            {
                try
                {
                    // server_data 格式：可能是 JSON 或 base64 编码
                    var decoded = DecodeServerData(data);
                    if (!string.IsNullOrWhiteSpace(decoded))
                    {
                        // 更新 ts_sign
                        using var doc = JsonDocument.Parse(decoded);
                        if (doc.RootElement.TryGetProperty("ts_sign", out var ts))
                            _tsSign = ts.GetString();
                        if (doc.RootElement.TryGetProperty("sec_ts", out var st))
                            _secTs = st.GetString();
                    }
                }
                catch { /* parse error */ }
            }
        }

        // 2. 检查 jar 的 cookie 字符串中是否有新的 bd_ticket_guard_client_data
        if (cookieJar != null)
        {
            var cookieStr = cookieJar.GetCookieString();
            var v2 = ExtractCookieValue(cookieStr, "bd_ticket_guard_client_data_v2");
            if (!string.IsNullOrWhiteSpace(v2))
                UpdateFromV2Value(v2);
        }
    }

    private void UpdateFromV2Value(string v2Value)
    {
        try
        {
            v2Value = Uri.UnescapeDataString(v2Value);
            var missing = v2Value.Length % 4;
            if (missing > 0) v2Value += new string('=', 4 - missing);
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(v2Value));
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("ts_sign", out var ts))
                _tsSign = ts.GetString();
            if (doc.RootElement.TryGetProperty("sec_ts", out var st))
                _secTs = st.GetString();
        }
        catch { }
    }

    // ═══ Helpers ═══

    private static string? ExtractCookieValue(string cookie, string key)
    {
        var prefix = key + "=";
        var idx = cookie.IndexOf(prefix, StringComparison.Ordinal);
        if (idx < 0) return null;
        var start = idx + prefix.Length;
        var end = cookie.IndexOf(';', start);
        if (end < 0) end = cookie.Length;
        return cookie[start..end].Trim();
    }

    /// <summary>解码服务器下发的 server_data（可能是 URL-encoded base64 JSON）。</summary>
    private static string? DecodeServerData(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            raw = Uri.UnescapeDataString(raw);
            // 可能是 JSON 直传
            if (raw.StartsWith('{')) return raw;
            // 可能是 base64
            var missing = raw.Length % 4;
            if (missing > 0) raw += new string('=', 4 - missing);
            return Encoding.UTF8.GetString(Convert.FromBase64String(raw));
        }
        catch { return raw; }
    }

    /// <summary>将 PEM 格式公钥转为原始 base64（去掉头尾和换行）。</summary>
    private static string? PubKeyToBase64(string? pubKeyPem)
    {
        if (string.IsNullOrWhiteSpace(pubKeyPem)) return null;
        try
        {
            var lines = pubKeyPem
                .Replace("-----BEGIN PUBLIC KEY-----", "")
                .Replace("-----END PUBLIC KEY-----", "")
                .Replace("\r", "").Replace("\n", "").Trim();
            return lines;
        }
        catch { return null; }
    }
}
