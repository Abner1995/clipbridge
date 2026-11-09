using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace CreatFlow.Platforms.Kuaishou.Handlers;

/// <summary>
/// 快手签名 Handler — 拦截请求，计算 sig3/sig4 并附加到 URL。
///
/// 签名算法（对应 Python fangai/kuaishou/sign.py）：
///   1. 提取 URL query params → 按 key 排序 → format: key1=val1key2=val2...
///   2. 拼接 JSON body → MD5 hash → sig3 input
///   3. 调用 App 层注入的 WebView2 签名器（页面 webpack 里的 sig3/sig4 模块）
///
/// sig4 路由（substring match）：
///   - /rest/cp/works/v2/video/pc/edit/info
///   - /rest/cp/works/v2/video/pc/submit
///   - /rest/cp/works/v2/common/pc/nearby
///   - /rest/zt/location/wi/poi/search
/// 其他所有路径使用 sig3。
/// </summary>
internal sealed class KuaishouSigHandler : DelegatingHandler
{
    /// <summary>请求所属平台会话 — 签名器据此定位「同账号的 WebView2 档案页」（可为空）。</summary>
    private readonly Guid? _sessionId;

    /// <summary>
    /// App 层注入的签名委托。
    /// 参数: 会话 ID + JSON 格式的签名请求（{action, url, type, params}）
    /// 返回: 签名字符串（如 "__NS_sig3=abc123..." 或 "__NS_hxfalcon=...&caver=2"）
    /// 未注入或返回 null → 本次请求裸发（平台会拒绝，由上层回报失败原因；与小红书同口径）。
    /// </summary>
    public static Func<Guid?, string, CancellationToken, Task<string?>>? SignerDelegate { get; set; }

    private static readonly HashSet<string> Sig4Paths = new(StringComparer.Ordinal)
    {
        "/rest/cp/works/v2/video/pc/edit/info",
        "/rest/cp/works/v2/video/pc/submit",
        "/rest/cp/works/v2/common/pc/nearby",
        "/rest/zt/location/wi/poi/search",             // __NS_hxfalcon + caver
        "/rest/v2/creator/pc/fanstop/money/account/type", // from Python _needs_sig4
    };

    public KuaishouSigHandler(Guid? sessionId = null)
    {
        _sessionId = sessionId;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri is null)
            return await base.SendAsync(request, ct);

        // multipart/form-data **不签名**：2026-11-05 抓包的作品编辑提交
        // （/rest/cp/works/v2/video/pc/edit/submit，multipart）URL 上没有
        // __NS_sig3 / __NS_hxfalcon —— 页面里 FormData 请求走的是无签名通道。
        // 封面图上传（cover/edit/cover/upload）同为 postForm({file})，照此处理。
        if (request.Content is MultipartFormDataContent)
            return await base.SendAsync(request, ct);

        // Read body content for signing
        string? bodyJson = null;
        if (request.Content is not null)
        {
            var bodyBytes = await request.Content.ReadAsByteArrayAsync(ct);
            bodyJson = Encoding.UTF8.GetString(bodyBytes);
        }

        // Determine sig3 vs sig4
        var path = request.RequestUri.AbsolutePath;
        var needsSig4 = Sig4Paths.Any(sig4Path => path.Contains(sig4Path, StringComparison.OrdinalIgnoreCase));
        var action = needsSig4 ? "sig4" : "sig3";

        // Compute MD5 input (same for both sig3 and sig4)
        // Format: sorted_query_params_key=val + JSON_body → MD5
        var requestMd5 = ComputeRequestMd5(request.RequestUri, request.RequestUri.Query, bodyJson);

        // Build sign request
        var signReq = new SignRequest
        {
            Action = action,
            Url = request.RequestUri.ToString(), // Full URL with query params (needed for sig4: kpn/subBiz/kpf)
            Type = "json",
            Params = bodyJson is not null
                ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(bodyJson)
                : [],
        };

        // 调用 App 层注入的 WebView2 签名器（页面 webpack 里的 sig3/sig4 模块）。
        // 未注入 / 返回 null → 本次请求裸发，平台拒绝后由上层回报失败原因（与小红书同口径）。
        string? signedUrl = null;
        if (SignerDelegate is not null)
        {
            var reqJson = JsonSerializer.Serialize(signReq, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            });
            signedUrl = await SignerDelegate(_sessionId, reqJson, ct);
            Serilog.Log.Debug("[KuaishouSig] WebView2 signer returned: {HasSig}", !string.IsNullOrWhiteSpace(signedUrl));
        }
        if (string.IsNullOrWhiteSpace(signedUrl))
        {
            Serilog.Log.Warning("[KuaishouSig] 无可用签名（会话 {Session}，路径 {Path}）—— 本次请求裸发",
                _sessionId, request.RequestUri.AbsolutePath);
        }

        if (!string.IsNullOrWhiteSpace(signedUrl))
        {
            // 签名器返回 FULL URL with signature appended, not just the sig string.
            // Extract only the signature part (__NS_sig3=... or __NS_hxfalcon=...&caver=...).
            var sig = "";
            if (signedUrl.Contains('?'))
            {
                var query = signedUrl.Split('?', 2)[1];
                // Keep only sig-related params
                var parts = query.Split('&')
                    .Where(p => p.StartsWith("__NS_sig3=") || p.StartsWith("__NS_hxfalcon=") || p.StartsWith("caver="))
                    .ToArray();
                sig = string.Join("&", parts);
            }

            // Guard: if sig is empty or looks like a URL (signer returned bare URL), skip
            if (!string.IsNullOrWhiteSpace(sig) && !sig.Contains("://"))
            {
                var uri = request.RequestUri.ToString();
                var separator = uri.Contains('?') ? "&" : "?";
                request.RequestUri = new Uri($"{uri}{separator}{sig}");
            }
            else if (string.IsNullOrWhiteSpace(sig))
            {
                Serilog.Log.Warning("[KuaishouSig] Empty signature extracted for {Path}", request.RequestUri?.AbsolutePath);
            }
        }
        else
        {
            Serilog.Log.Warning("[KuaishouSig] NO signature for {Path}", request.RequestUri?.AbsolutePath);
        }

        return await base.SendAsync(request, ct);
    }

    /// <summary>
    /// 计算请求的 MD5 值（对应 Python sign.py requestToMd5）。
    ///
    /// 算法：
    ///   1. 解析 query string → dict
    ///   2. 按 key ASCII 排序
    ///   3. format: key1=URIencode(val1)key2=URIencode(val2)...
    ///   4. 拼接 JSON body → MD5
    /// </summary>
    internal static string ComputeRequestMd5(Uri uri, string queryString, string? jsonBody)
    {
        // Parse query params
        var queryParams = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(queryString))
        {
            var qs = queryString.TrimStart('?');
            foreach (var entry in qs.Split('&'))
            {
                if (string.IsNullOrEmpty(entry)) continue;
                var parts = entry.Split('=', 2);
                var key = Uri.UnescapeDataString(parts[0]);
                var value = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
                if (!string.IsNullOrEmpty(key))
                    queryParams[key] = value;
            }
        }

        // Build sorted string: key1=URL_encoded_value1key2=URL_encoded_value2...
        var sb = new StringBuilder();
        foreach (var kv in queryParams)
        {
            sb.Append(kv.Key);
            sb.Append('=');
            sb.Append(Uri.EscapeDataString(kv.Value));
        }

        // Append JSON body
        sb.Append(jsonBody ?? "");

        // MD5
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexStringLower(hash);
    }

    // ═══ Inner types ═══

    private sealed class SignRequest
    {
        [JsonPropertyName("action")]
        public string Action { get; set; } = "";

        [JsonPropertyName("url")]
        public string Url { get; set; } = "";

        [JsonPropertyName("type")]
        public string Type { get; set; } = "json";

        [JsonPropertyName("params")]
        public Dictionary<string, JsonElement>? Params { get; set; }
    }
}
