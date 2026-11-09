namespace CreatFlow.NewApp.Services;

/// <summary>
/// 登录后保存的会话信息（自老 App 拷贝）。
/// </summary>
public class StoredSession
{
    public string Token { get; init; } = "";
    public string Username { get; init; } = "";
    public DateTime ExpireDate { get; init; }
    public string ClientSecret { get; init; } = "";
    public Guid MemberId { get; init; }
    public string? LastLoginIp { get; init; }
    public DateTime? LastLoginAt { get; init; }
}

/// <summary>
/// 本地 JWT 加密持久化 — AES-256-GCM，跨平台（Windows / macOS / Linux）。
/// 存储与老 App 完全同路径（%LocalAppData%/CreatFlow/token.dat）→ 双存期共享登录态。
///
/// 新增（NewApp）：<see cref="InMemory"/> 会话 —「记住我」未勾选时只驻内存不落盘，
/// 关窗后失效（会话不过到重启）。
/// </summary>
public static class TokenStorage
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CreatFlow",
        "token.dat");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>仅内存会话（「记住我」未勾选）；优先于磁盘文件</summary>
    public static StoredSession? InMemory { get; private set; }

    /// <summary>从机器特征派生 AES 密钥（确定性的，不存任何地方）</summary>
    private static byte[] DeriveKey()
    {
        var entropy = $"CreatFlow.TokenStorage.v1|{Environment.MachineName}|{Environment.UserName}";
        return SHA256.HashData(Encoding.UTF8.GetBytes(entropy));
    }

    /// <summary>保存登录会话（AES-256-GCM 加密写入本地文件 + 置为当前会话）</summary>
    public static void Save(string token, string username, DateTime expireDate,
        string clientSecret, Guid memberId, string? lastLoginIp = null, DateTime? lastLoginAt = null)
    {
        var session = new StoredSession
        {
            Token = token,
            Username = username,
            ExpireDate = expireDate,
            ClientSecret = clientSecret,
            MemberId = memberId,
            LastLoginIp = lastLoginIp,
            LastLoginAt = lastLoginAt
        };

        var json = JsonSerializer.Serialize(session, JsonOptions);
        var plainBytes = Encoding.UTF8.GetBytes(json);

        // AES-256-GCM 加密
        var key = DeriveKey();
        var nonce = new byte[AesGcm.NonceByteSizes.MaxSize]; // 12 bytes
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];     // 16 bytes
        var ciphertext = new byte[plainBytes.Length];

        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, plainBytes, ciphertext, tag);

        // 写入格式: nonce(12) + tag(16) + ciphertext
        var output = new byte[nonce.Length + tag.Length + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, output, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, output, nonce.Length, tag.Length);
        Buffer.BlockCopy(ciphertext, 0, output, nonce.Length + tag.Length, ciphertext.Length);

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllBytes(FilePath, output);
        InMemory = session;
    }

    /// <summary>仅驻内存（「记住我」未勾选）——不写磁盘，进程退出即失效</summary>
    public static void SaveInMemory(StoredSession session) => InMemory = session;

    /// <summary>读取当前会话：内存优先，其次解密本地文件；均无返回 null</summary>
    public static StoredSession? Load() => InMemory ?? LoadFromFile();

    /// <summary>读取并解密本地会话，文件不存在或损坏返回 null</summary>
    private static StoredSession? LoadFromFile()
    {
        if (!File.Exists(FilePath))
            return null;

        try
        {
            var raw = File.ReadAllBytes(FilePath);

            // 最小长度: nonce(12) + tag(16) + 至少 1 字节密文
            if (raw.Length < 12 + 16 + 1)
                return null;

            var nonce = raw[..12];
            var tag = raw[12..28];
            var ciphertext = raw[28..];

            var key = DeriveKey();
            var plainBytes = new byte[ciphertext.Length];

            using var aes = new AesGcm(key, tag.Length);
            aes.Decrypt(nonce, ciphertext, tag, plainBytes);

            var json = Encoding.UTF8.GetString(plainBytes);
            return JsonSerializer.Deserialize<StoredSession>(json, JsonOptions);
        }
        catch
        {
            // 文件损坏 / 解密失败 / 机器环境变化 → 当作未登录
            return null;
        }
    }

    /// <summary>删除本地 token 文件 + 清内存会话（退出登录）</summary>
    public static void Clear()
    {
        InMemory = null;
        if (File.Exists(FilePath))
            File.Delete(FilePath);
    }

    /// <summary>
    /// 检查是否有有效的本地会话 — 存在 + JWT 未过期。
    /// 不依赖任何 NuGet JWT 库，直接解码 Base64 payload 取 exp。
    /// </summary>
    public static bool HasValidToken()
    {
        var session = Load();
        if (session == null)
            return false;

        var expiry = GetJwtExpiry(session.Token);
        return expiry != null && expiry > DateTime.UtcNow;
    }

    /// <summary>从 JWT payload 中提取 exp 字段</summary>
    private static DateTime? GetJwtExpiry(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 3)
                return null;

            var payload = NormalizeBase64(parts[1]);
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("exp", out var expProp))
                return null;

            var exp = expProp.GetInt64();
            return DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>JWT Base64Url → 标准 Base64（补充填充）</summary>
    private static string NormalizeBase64(string base64Url)
    {
        var s = base64Url.Replace('-', '+').Replace('_', '/');
        return (s.Length % 4) switch
        {
            2 => s + "==",
            3 => s + "=",
            _ => s
        };
    }
}
