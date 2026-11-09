namespace CreatFlow.NewApp.Services;

/// <summary>
/// CB-SigV1 API 签名工具 — 桌面端和服务端共享完全相同的签名算法（自老 App 拷贝）。
///
/// 签名流程:
///   1. 构建规范化请求字符串: "{method}\n{path}\n{timestamp}\n{nonce}\n{body_sha256}"
///   2. HMAC-SHA256(secret, canonical_string) → Base64
///
/// 公开端点（login/register）使用 AppSecret 签名。
/// 认证端点（heartbeat/activate）使用 ClientSecret（登录后下发）。
/// </summary>
public static class ApiSigner
{
    /// <summary>时间戳允许偏差（和服务端一致：5 分钟窗口）</summary>
    public static readonly TimeSpan TimestampWindow = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 计算 CB-SigV1 签名
    /// </summary>
    /// <param name="secret">签名密钥（AppSecret 或 ClientSecret）</param>
    /// <param name="method">HTTP 方法，大写</param>
    /// <param name="path">请求路径，如 "/api/auth/login"</param>
    /// <param name="timestamp">Unix 秒时间戳</param>
    /// <param name="nonce">随机字符串，单次有效</param>
    /// <param name="body">请求体原始字节（可为空）</param>
    public static string Compute(string secret, string method, string path,
        long timestamp, string nonce, byte[]? body)
    {
        var bodySha256 = body is { Length: > 0 }
            ? Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant()
            : string.Empty;

        var canonical = $"{method.ToUpperInvariant()}\n{path}\n{timestamp}\n{nonce}\n{bodySha256}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToBase64String(hash);
    }

    /// <summary>生成随机 Nonce（UUID 无横线，和服务端一致）</summary>
    public static string NewNonce() => Guid.NewGuid().ToString("N");

    /// <summary>当前 Unix 秒时间戳</summary>
    public static long NowUnixSeconds() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
