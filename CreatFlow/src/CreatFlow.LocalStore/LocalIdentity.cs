namespace CreatFlow.LocalStore;

/// <summary>
/// 单机版固定本机会员身份 —— 没有登录/激活/到期，所有本地数据都挂在这一个 ID 下。
///
/// 为什么仍保留「会员」概念：Application 层业务服务全部按 memberId 隔离数据，
/// 单机版复用它就不必把业务规则再抄一遍。固定 ID 不会与联网版账号冲突
/// （正常会员 ID 是随机 Guid，这个前缀是明文标记）。
/// </summary>
public static class LocalIdentity
{
    /// <summary>本机会员 ID（写死；换机导入后仍是同一个 ID，数据直接可用）</summary>
    public static readonly Guid MemberId = Guid.Parse("C0DE0000-0000-4000-8000-000000000001");

    /// <summary>本机会员展示名（账号页/侧栏用）</summary>
    public const string Username = "本地用户";
}
