namespace CreatFlow.Domain.Entities;

/// <summary>
/// 平台会话 — 会员在某个平台上的登录凭证与身份信息。
///
/// 一个会员可在同一平台拥有多个会话（大号/小号），
/// 通过 account_label 区分，is_primary 标记默认发布账号。
///
/// 不继承 Aggregate（无领域事件），继承 Entity 足够。
/// </summary>
public class PlatformSession : Aggregate<PlatformSessionId>
{
    /// <summary>关联会员 ID</summary>
    public Guid MemberId { get; private set; }

    /// <summary>平台标识（douyin / kuaishou / xiaohongshu / wechatvideo）</summary>
    public string PlatformKey { get; private set; } = default!;

    // ── 用户自定义 ──

    /// <summary>用户自定义备注（"大号" / "公司号"），null 时显示平台昵称</summary>
    public string? AccountLabel { get; private set; }

    /// <summary>该平台下的默认发布账号（同平台唯一）</summary>
    public bool IsPrimary { get; private set; }

    // ── 平台侧身份（WebView2 登录后提取） ──

    /// <summary>平台用户 ID（抖音 uid / 小红书 user_id）</summary>
    public string? PlatformUid { get; private set; }

    /// <summary>平台昵称</summary>
    public string? PlatformName { get; private set; }

    /// <summary>头像 URL</summary>
    public string? PlatformAvatar { get; private set; }

    // ── 凭证 ──

    /// <summary>完整 Cookie 字符串（MEDIUMTEXT）</summary>
    public string? Cookie { get; private set; }

    /// <summary>localStorage 关键 key-value（JSON）</summary>
    public string? LocalStorageJson { get; private set; }

    /// <summary>平台特有扩展数据（JSON）</summary>
    public string? ExtraDataJson { get; private set; }

    // ── 状态 ──

    /// <summary>会话状态：0=有效 1=临期 2=过期</summary>
    public PlatformSessionStatus Status { get; private set; }

    /// <summary>登录成功时间（WebView2 提取 cookie 的时刻）</summary>
    public DateTime LoggedInAt { get; private set; }

    /// <summary>最近探活时间</summary>
    public DateTime? ProbedAt { get; private set; }

    /// <summary>探活结果</summary>
    public string? ProbedResult { get; private set; }

    /// <summary>预估过期时间（LoggedInAt + 保守天数）</summary>
    public DateTime? ExpiredAt { get; private set; }

    // ── ctor ──

    private PlatformSession() { }

    // ═══ Factory ═══

    public static PlatformSession Create(
        PlatformSessionId id,
        Guid memberId,
        string platformKey,
        string? cookie,
        string? localStorageJson,
        string? platformUid = null,
        string? platformName = null,
        string? platformAvatar = null,
        string? extraDataJson = null,
        int estimatedValidDays = 3)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(platformKey);

        var now = Clock.Now;
        return new PlatformSession
        {
            Id = id,
            MemberId = memberId,
            PlatformKey = platformKey,
            Cookie = cookie,
            LocalStorageJson = localStorageJson,
            PlatformUid = platformUid,
            PlatformName = platformName,
            PlatformAvatar = platformAvatar,
            ExtraDataJson = extraDataJson,
            Status = PlatformSessionStatus.Active,
            LoggedInAt = now,
            ExpiredAt = now.AddDays(estimatedValidDays),
            CreatedAt = now,
            LastModified = now,
        };
    }

    // ═══ Behaviors ═══

    /// <summary>
    /// 重新登录 / 刷新凭证：覆盖 cookie、localStorage、身份信息，
    /// 重置状态为 Active、刷新过期时间。
    /// </summary>
    public void SyncCredentials(
        string? cookie,
        string? localStorageJson,
        string? platformUid,
        string? platformName,
        string? platformAvatar,
        string? extraDataJson,
        int estimatedValidDays = 3)
    {
        var now = Clock.Now;
        Cookie = cookie;
        LocalStorageJson = localStorageJson;
        PlatformUid = platformUid;
        PlatformName = platformName;
        PlatformAvatar = platformAvatar;
        ExtraDataJson = extraDataJson;
        Status = PlatformSessionStatus.Active;
        LoggedInAt = now;
        ExpiredAt = now.AddDays(estimatedValidDays);
        ProbedAt = null;
        ProbedResult = null;
        LastModified = now;
    }

    /// <summary>修改自定义备注</summary>
    public void Rename(string? label)
    {
        AccountLabel = label;
        LastModified = Clock.Now;
    }

    /// <summary>设为主账号</summary>
    public void SetPrimary(bool isPrimary)
    {
        IsPrimary = isPrimary;
        LastModified = Clock.Now;
    }

    /// <summary>探活结果更新</summary>
    public void UpdateProbeResult(PlatformSessionStatus status, string? result)
    {
        Status = status;
        ProbedAt = Clock.Now;
        ProbedResult = result;
        LastModified = Clock.Now;
    }
}

/// <summary>
/// 平台会话状态
///   Active = 0       — 凭证有效
///   ExpiringSoon = 1 — 凭证即将过期（还能用但需提醒）
///   Expired = 2      — 凭证已失效，需重新登录
/// </summary>
public enum PlatformSessionStatus
{
    Active = 0,
    ExpiringSoon = 1,
    Expired = 2,
}
