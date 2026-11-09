namespace CreatFlow.Platforms.WechatVideo;

using CreatFlow.Platforms.Core;
using CreatFlow.Platforms.WechatVideo.Requests;
using Serilog;

/// <summary>
/// 微信视频号 API 客户端 — 封装 channels.weixin.qq.com 内容管理 API 的通用请求逻辑。
///
/// 所有接口均为 POST，需要 Cookie + device_id 认证。
/// 对标 Python fangai/wechatvideo/api.py 的 WechatAPI 类。
///
/// 自动注入上报参数：timestamp, _log_finder_uin, _log_finder_id, scene, reqScene 等。
/// </summary>
internal sealed class WechatVideoApiClient : IDisposable
{
    private readonly PlatformHttpClient _http;
    private readonly PlatformOptions _options;
    private readonly string _cookie;
    private readonly string _deviceId;
    private readonly string _aid;
    private readonly string? _uin;
    private readonly string _finderId;

    /// <summary>默认 finder_id 兜底值</summary>
    private const string DefaultFinderId =
        "v2_060000231003b20faec8cae08e1cc3d1c70ded37b077cfefe945fdb82dfe6641a73b44b04c16@finder";
    private const string ApiPrefix = "/micro/content/cgi-bin/mmfinderassistant-bin";

    /// <summary>对标 Python json.dumps — 不转义 &lt; &gt; &amp; 等 ASCII 字符</summary>
    private static readonly JsonSerializerOptions WechatJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public WechatVideoApiClient(
        PlatformOptions options,
        string cookie,
        string deviceId,
        string? uin = null,
        string? aid = null,
        string? finderId = null)
    {
        _options = options;
        _cookie = cookie;
        _deviceId = deviceId;
        _uin = uin;
        _finderId = string.IsNullOrWhiteSpace(finderId) ? DefaultFinderId : finderId;
        _aid = aid ?? Guid.NewGuid().ToString("N");

        // Handler 管道: WechatVideoHeaders → CookieAuth → Logging → HTTP
        // WechatVideoHeadersHandler 注入 finger-print-device-id + X-WECHAT-UIN
        var wxHandler = new WechatVideoHeadersHandler(_deviceId, () => WechatUin);
        var cookieHandler = new Core.Handlers.CookieAuthHandler(cookie);
        var loggingHandler = new Core.Handlers.LoggingHandler(null);
        wxHandler.InnerHandler = cookieHandler;
        cookieHandler.InnerHandler = loggingHandler;

        _http = new PlatformHttpClient(wxHandler, options, null,
            origin: "https://channels.weixin.qq.com",
            referer: "https://channels.weixin.qq.com/micro/content/post/list?tab=collection");
    }

    /// <summary>
    /// 发送 POST 请求到内容管理 API，自动注入上报参数。
    /// </summary>
    public async Task<WechatApiResponse> PostAsync(
        string path,
        object body,
        string? pageUrl = null,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var ridHex = nowSec.ToString("x");
        var ridRand = string.Concat(Enumerable.Range(0, 8).Select(_ =>
            "0123456789abcdef"[Random.Shared.Next(16)]));
        var rid = $"{ridHex}-{ridRand}";

        // 对标 Python api.py _post: 非 create/list 路径默认用 platform/post/create
        pageUrl ??= "https://channels.weixin.qq.com/platform/post/create";

        var qs = string.Join("&", new Dictionary<string, string>
        {
            ["_aid"] = _aid,
            ["_rid"] = rid,
            ["_pageUrl"] = pageUrl,
        }.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

        var url = $"{_options.WechatVideoApiHost}{ApiPrefix}{path}?{qs}";

        // 自动注入上报参数
        var bodyDict = body as IDictionary<string, object?>
            ?? JsonSerializer.Deserialize<Dictionary<string, object?>>(
                JsonSerializer.Serialize(body),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        bodyDict.TryAdd("timestamp", now.ToString());
        bodyDict.TryAdd("_log_finder_uin", "");
        bodyDict.TryAdd("_log_finder_id", _finderId);
        // 对标 Python post_create: rawKeyBuff 传空字符串（非 null）
        bodyDict.TryAdd("rawKeyBuff", "");
        bodyDict.TryAdd("pluginSessionId", null!);
        bodyDict.TryAdd("scene", 7);
        bodyDict.TryAdd("reqScene", 7);

        // 对标 Python json.dumps：不转义 & < > 等字符。
        // System.Text.Json 默认把 & 转成 &，微信服务端可能做原始字符串校验
        var bodyJson = JsonSerializer.Serialize(bodyDict, WechatJsonOptions);
        Log.Debug("[WechatVideo] POST {Url} deviceId={DevId} body={Body}", url,
            string.IsNullOrWhiteSpace(_deviceId) ? "(empty!)" : _deviceId[..Math.Min(8, _deviceId.Length)] + "...",
            bodyJson);

        // 使用 PostJsonRawAsync 而非 PostJsonAsync — 后者有 SnakeCaseLower 策略，
        // 会把 clipOriginVideoInfo → clip_origin_video_info，微信 API 不认 camelCase→snake
        var json = await _http.PostJsonRawAsync(url, bodyJson, ct);
        var resp = JsonSerializer.Deserialize<WechatApiResponse>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (resp?.ErrCode != 0)
        {
            Log.Warning("[WechatVideo] {Path} failed: errCode={Code}, errMsg={Msg}, respBody={Body}",
                path, resp?.ErrCode, resp?.ErrMsg, json?.Length > 500 ? json[..500] : json);
        }

        return resp ?? new WechatApiResponse { ErrCode = -1, ErrMsg = "deserialize failed" };
    }

    /// <summary>X-WECHAT-UIN header 值 — 优先用传入的 uin，否则从 Cookie 提取 wxuin</summary>
    public string WechatUin
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_uin)) return _uin;
            // 用 IndexOf 直接搜 — Split("; ") 可能因分隔符格式（URL编码等）匹配不到
            const string key = "wxuin=";
            var idx = _cookie.IndexOf(key, StringComparison.Ordinal);
            Log.Debug("[WechatVideo] WechatUin: cookieLen={Len}, wxuin idx={Idx}", _cookie.Length, idx);
            if (idx < 0) return "";
            var end = _cookie.IndexOf(';', idx + key.Length);
            var result = end < 0
                ? _cookie[(idx + key.Length)..]
                : _cookie[(idx + key.Length)..end];
            Log.Debug("[WechatVideo] WechatUin: extracted={Uin}", result);
            return result;
        }
    }

    public string DeviceId => _deviceId;

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// 微信视频号专用头注入 Handler — 在管道最前端注入：
///   finger-print-device-id — 设备指纹（必须，否则 401）
///   X-WECHAT-UIN           — 用户 UIN（部分接口必须，如 clip_video）
///   Origin / Referer       — 视频号域名（PlatformHttpClient 默认值是抖音的，必须覆盖）
/// </summary>
internal sealed class WechatVideoHeadersHandler : DelegatingHandler
{
    private readonly string _deviceId;
    private readonly Func<string> _uinProvider;

    public WechatVideoHeadersHandler(string deviceId, string? uin = null)
    {
        _deviceId = deviceId;
        _uinProvider = () => uin ?? "";
    }

    public WechatVideoHeadersHandler(string deviceId, Func<string> uinProvider)
    {
        _deviceId = deviceId;
        _uinProvider = uinProvider;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_deviceId))
            request.Headers.TryAddWithoutValidation("finger-print-device-id", _deviceId);

        var uin = _uinProvider();
        if (!string.IsNullOrWhiteSpace(uin))
            request.Headers.TryAddWithoutValidation("X-WECHAT-UIN", uin);

        return await base.SendAsync(request, ct);
    }
}
