using Serilog.Events;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// HTTP 客户端封装 — 所有 /api/* 请求自动附加 CB-SigV1 签名头 + JWT（自老 App 拷贝）。
///
/// 使用方式:
///   var result = await ApiClient.PostAsync<LoginResponse>("/api/auth/login", new LoginRequest("user", "pass"));
///   var heartbeat = await ApiClient.PostAsync<HeartbeatResponse>("/api/heartbeat");
/// </summary>
public static class ApiClient
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// 会员资格被服务端撤销时触发（过期或被禁用）。
    /// 参数为 "membership_expired" 或 "account_disabled"。
    /// App 层订阅此事件以切换到激活/登录。
    /// </summary>
    public static event Action<string>? OnMembershipRevoked;

    /// <summary>
    /// 本地分发钩子（单机版）—— 联网版永远为 null，行为完全不变。
    ///
    /// 参数 = (path, body, authenticated)；返回 null 表示「不接这条路径」按 HTTP 处理。
    /// 单机版在启动时设为 <see cref="LocalApiRouter.HandleAsync"/>，且**必须早于任何后台消费者启动**
    /// （壳的 StartConsumers 会立刻发请求，晚设会让首批请求走 HTTP 超时）。
    /// </summary>
    public static Func<string, object?, bool, Task<object?>>? LocalHandler;

    /// <summary>
    /// 本地结果 → 调用方要的类型。本地路由返回 Application DTO、调用方要客户端 DTO
    /// （属性名逐字一致），经 JSON 往返转换，不手写字段映射；同类型直接返回。
    /// </summary>
    private static T? AdaptLocalResult<T>(object? result)
    {
        if (result is null) return default;
        if (result is T typed) return typed;
        var json = JsonSerializer.Serialize(result, JsonOptions);
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    /// <summary>
    /// 发起 POST 请求（带 CB-SigV1 签名）。
    ///
    /// - 公开端点（login/register）：使用 AppSecret 签名
    /// - 认证端点（heartbeat/activate）：使用 ClientSecret 签名 + JWT Bearer
    /// </summary>
    public static async Task<T?> PostAsync<T>(string path, object? body = null, bool authenticated = true)
    {
        // 单机版：本地路由优先（不组签名、不发 HTTP）
        if (LocalHandler is { } local)
            return AdaptLocalResult<T>(await local(path, body, authenticated));

        var url = $"{AppConfig.ServerBaseUrl}{path}";

        // 构建请求体
        byte[]? bodyBytes = null;
        if (body != null)
        {
            var json = JsonSerializer.Serialize(body, JsonOptions);
            bodyBytes = Encoding.UTF8.GetBytes(json);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, url);

        // 添加 CB-SigV1 签名头
        var timestamp = ApiSigner.NowUnixSeconds();
        var nonce = ApiSigner.NewNonce();
        var secret = authenticated
            ? TokenStorage.Load()?.ClientSecret ?? AppConfig.AppSecret
            : AppConfig.AppSecret;

        var signature = ApiSigner.Compute(secret, "POST", path, timestamp, nonce, bodyBytes);

        request.Headers.Add("X-CB-Timestamp", timestamp.ToString());
        request.Headers.Add("X-CB-Nonce", nonce);
        request.Headers.Add("X-CB-Signature", signature);

        // 认证端点附加 JWT
        if (authenticated)
        {
            var session = TokenStorage.Load();
            if (session != null)
                request.Headers.Add("Authorization", $"Bearer {session.Token}");
        }

        // 附加请求体
        if (bodyBytes != null)
            request.Content = new ByteArrayContent(bodyBytes);
        if (request.Content is ByteArrayContent bac)
            bac.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        Log.Debug("API POST {Path}", path);
        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "API POST {Path} network error: {Error}", path, ex.Message);
            return default;
        }
        var responseJson = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            // 尝试反序列化错误信息
            try
            {
                var error = JsonSerializer.Deserialize<ErrorResponse>(responseJson, JsonOptions);
                var errorCode = error?.Error ?? "";
                // 全局拦截：服务端撤销会员资格 → 通知切换状态，不抛异常
                if (errorCode is "membership_expired" or "account_disabled")
                {
                    Log.Debug("API POST {Path} → {Status}: {Error}", path, (int)response.StatusCode, errorCode);
                    OnMembershipRevoked?.Invoke(errorCode);
                    return default;
                }
                // 4xx 是**业务结果**，不是故障：404 常见于「会话已解绑」（记录上留着失效的
                // sessionId，实测库里那 3 条 8 月的记录），调用方普遍会把它收成
                // 「账号已解绑」继续走。用 Error 级别记会让人以为出事了（用户就报过
                // 「总是报这个错误」）。5xx 才是真故障，留给 Error。
                var logLevel = response.StatusCode >= System.Net.HttpStatusCode.InternalServerError
                    ? LogEventLevel.Error : LogEventLevel.Warning;
                Log.Write(logLevel, "API {Method} {Path} → {Status}: {Body}",
                    "POST", path, (int)response.StatusCode,
                    responseJson.Length > 500 ? responseJson[..500] : responseJson);

                // ⚠️ **5xx 的响应体不能进 ex.Message**。GlobalExceptionHandler 在 Development 下
                // 会把 exception.ToString()（整段服务端堆栈）塞进 detail，而调用方普遍是
                // `"xxx失败：" + ex.Message` 直接上界面 —— 用户就会看到一段 .NET 调用栈
                //（2026-09-21 视频管理页批量删除踩过：异步任务页的失败原因里躺着一整段堆栈）。
                // 正文照旧记日志，给用户的只有一句人话。
                var message = response.StatusCode >= System.Net.HttpStatusCode.InternalServerError
                    ? $"服务端内部错误（HTTP {(int)response.StatusCode}）"
                    : errorCode.Length > 0 ? errorCode : responseJson;
                throw new ApiException((int)response.StatusCode, message);
            }
            catch (ApiException)
            {
                throw;
            }
            catch
            {
                Log.Error("API {Method} {Path} → {Status}: {Body}",
                    "POST", path, (int)response.StatusCode,
                    responseJson.Length > 500 ? responseJson[..500] : responseJson);
                throw new ApiException((int)response.StatusCode, responseJson);
            }
        }

        Log.Debug("API POST {Path} → {Status}", path, (int)response.StatusCode);
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent
            || string.IsNullOrWhiteSpace(responseJson))
            return default;
        try
        {
            return JsonSerializer.Deserialize<T>(responseJson, JsonOptions);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "API POST {Path} deserialize failed (expected {Type}): {Body}",
                path, typeof(T).Name,
                responseJson.Length > 300 ? responseJson[..300] : responseJson);
            return default;
        }
    }

    /// <summary>
    /// 发起 POST 请求（不抛异常版）— 用于心跳等需要自行处理 HTTP 状态的场景。
    /// 返回 (StatusCode, ResponseBody)。
    /// </summary>
    public static async Task<(int StatusCode, string Body)> PostRawAsync(
        string path, object? body = null, bool authenticated = true)
    {
        // 单机版没有服务端错误语义可透传：明确拒绝，别静默走 HTTP 超时
        if (LocalHandler is not null)
        {
            Log.Warning("单机版不支持的原始请求：{Path}", path);
            return (500, "单机版不支持该接口");
        }

        var url = $"{AppConfig.ServerBaseUrl}{path}";

        byte[]? bodyBytes = null;
        if (body != null)
        {
            var json = JsonSerializer.Serialize(body, JsonOptions);
            bodyBytes = Encoding.UTF8.GetBytes(json);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, url);

        var timestamp = ApiSigner.NowUnixSeconds();
        var nonce = ApiSigner.NewNonce();
        var secret = authenticated
            ? TokenStorage.Load()?.ClientSecret ?? AppConfig.AppSecret
            : AppConfig.AppSecret;

        var signature = ApiSigner.Compute(secret, "POST", path, timestamp, nonce, bodyBytes);

        request.Headers.Add("X-CB-Timestamp", timestamp.ToString());
        request.Headers.Add("X-CB-Nonce", nonce);
        request.Headers.Add("X-CB-Signature", signature);

        if (authenticated)
        {
            var session = TokenStorage.Load();
            if (session != null)
                request.Headers.Add("Authorization", $"Bearer {session.Token}");
        }

        if (bodyBytes != null)
            request.Content = new ByteArrayContent(bodyBytes);
        if (request.Content is ByteArrayContent bac)
            bac.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        var response = await Http.SendAsync(request);
        var responseJson = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, responseJson);
    }

    /// <summary>
    /// 发起 GET 请求（带 CB-SigV1 签名）。
    /// authenticated=true 时附加 JWT + ClientSecret 签名，用于需认证的端点。
    /// </summary>
    public static async Task<T?> GetAsync<T>(string path, bool authenticated = false)
    {
        // 单机版：本地路由优先（不组签名、不发 HTTP）
        if (LocalHandler is { } local)
            return AdaptLocalResult<T>(await local(path, null, authenticated));

        var url = $"{AppConfig.ServerBaseUrl}{path}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        var timestamp = ApiSigner.NowUnixSeconds();
        var nonce = ApiSigner.NewNonce();
        var secret = authenticated
            ? TokenStorage.Load()?.ClientSecret ?? AppConfig.AppSecret
            : AppConfig.AppSecret;

        var signature = ApiSigner.Compute(secret, "GET", path, timestamp, nonce, null);

        request.Headers.Add("X-CB-Timestamp", timestamp.ToString());
        request.Headers.Add("X-CB-Nonce", nonce);
        request.Headers.Add("X-CB-Signature", signature);

        if (authenticated)
        {
            var session = TokenStorage.Load();
            if (session != null)
                request.Headers.Add("Authorization", $"Bearer {session.Token}");
        }

        var response = await Http.SendAsync(request);
        var responseJson = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            try
            {
                var error = JsonSerializer.Deserialize<ErrorResponse>(responseJson, JsonOptions);
                throw new ApiException((int)response.StatusCode, error?.Error ?? responseJson);
            }
            catch (ApiException)
            {
                throw;
            }
            catch
            {
                throw new ApiException((int)response.StatusCode, responseJson);
            }
        }

        return JsonSerializer.Deserialize<T>(responseJson, JsonOptions);
    }
}

/// <summary>
/// API 调用异常 — StatusCode + 服务端错误信息。
/// </summary>
public class ApiException : Exception
{
    public int StatusCode { get; }

    public ApiException(int statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }
}
