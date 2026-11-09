using CreatFlow.LocalStore;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 当前会员会话上下文 —— 壳与页面共用的状态源（**DI 单例**）。
/// 构造/Refresh 时从 <see cref="TokenStorage"/> 重载；
/// 会员信息变化（激活成功、到期变更）后调用 <see cref="Refresh"/>，
/// 广播 <see cref="MembershipChanged"/>（壳监听 → 重算导航门控 + 过期用户激活后自动跳首页）。
/// 登出→重登、服务端心跳同步到期时间后，由 App 层在建壳前统一 Refresh（单例不会自然过期）。
/// </summary>
public sealed class SessionContext
{
    public SessionContext() => Reload();

    private Guid _memberId;
    private string _username = "";
    private DateTime? _expireDate;
    private string? _lastLoginIp;
    private DateTime? _lastLoginAt;

    /// <summary>会员信息变化通知；参数 = 变化前的 IsExpired（壳据此决定是否自动跳首页）</summary>
    public event Action<bool>? MembershipChanged;

    /// <summary>从 TokenStorage 重载本地会话并广播变化（同一 UI 线程调用）</summary>
    public void Refresh()
    {
        var wasExpired = IsExpired;
        Reload();
        MembershipChanged?.Invoke(wasExpired);
    }

    private void Reload()
    {
        // 单机版：固定本机会员，无到期、无登录 IP —— 不读 token.dat
        // （TokenStorage 的机器绑定加密会随换机失效，单机版不该被它影响）
        if (AppConfig.IsStandalone)
        {
            _username = LocalIdentity.Username;
            _memberId = LocalIdentity.MemberId;
            _expireDate = null;
            _lastLoginIp = null;
            _lastLoginAt = null;
            return;
        }

        var s = TokenStorage.Load();
        _username = s?.Username ?? "";
        _memberId = s?.MemberId ?? Guid.Empty;
        _expireDate = s?.ExpireDate;
        _lastLoginIp = s?.LastLoginIp;
        _lastLoginAt = s?.LastLoginAt;
    }

    public Guid MemberId => _memberId;
    public string Username => _username;

    /// <summary>会员到期时间（null = 未知）</summary>
    public DateTime? ExpireDate => _expireDate;

    /// <summary>最近一次服务端记录的登录 IP（账号设置页展示）</summary>
    public string? LastLoginIp => _lastLoginIp;

    /// <summary>最近一次服务端记录的登录时间（账号设置页展示）</summary>
    public DateTime? LastLoginAt => _lastLoginAt;

    /// <summary>已过期（壳门控 + 横幅；与老 App 一致按时刻比较 —— 到期当日即过期）</summary>
    public bool IsExpired => ExpireDate != null && ExpireDate.Value < DateTime.Now;

    /// <summary>剩余天数（负数 = 已过期天数；null = 未知）</summary>
    public int? DaysLeft => ExpireDate == null
        ? null
        : (int)Math.Ceiling((ExpireDate.Value - DateTime.Now).TotalDays);

    /// <summary>到期文本（状态栏/横幅/首页展示）</summary>
    public string ExpireText => ExpireDate == null
        ? "会员状态未知"
        : IsExpired
            ? $"会员已于 {ExpireDate:yyyy-MM-dd HH:mm} 过期"
            : $"会员有效期至 {ExpireDate:yyyy-MM-dd HH:mm}（剩 {DaysLeft} 天）";

    /// <summary>头像占位首字母</summary>
    public string Initial => Username.Length > 0 ? Username[..1].ToUpperInvariant() : "?";

    /// <summary>当前客户端版本</summary>
    public string AppVersion => AppConfig.AppVersion;
}
