namespace CreatFlow.NewApp.Services;

/// <summary>
/// 功能页面的许可门槛（用户规则：凡是功能页面，都必须登录且激活才能使用）—— 纯函数，--selftest 直接断言。
/// 壳级门控（导航灰显 / 首页卡置灰 / 过期登录时后台执行器不启动）已挡正常入口；
/// 这里给「页面已经开着、会员中途过期 / 退出登录」等边角兜底：
/// 页面上的试听 / 提交前查它，本机队列消费者领取任务前也查它（拦下就停摆，许可恢复后接续）。
/// </summary>
public static class FeatureGate
{
    /// <summary>返回拦截原因；null = 放行。feature = 功能名（如「配音功能」），拼进提示语。
    /// smokeMode = 冒烟验证台：许可证不可得，闸门让路（TEMP 造景、不真跑）。</summary>
    public static string? BlockReason(bool isExpired, string? username, bool smokeMode, string feature)
    {
        if (smokeMode) return null;
        // 单机版没有许可体系：本机会员永远有效，闸门放行（保留函数签名，页面不用改）
        if (AppConfig.IsStandalone) return null;
        return OnlineBlockReason(isExpired, username, feature);
    }

    /// <summary>
    /// 联网版许可判定本身（纯函数）—— 单机版入口直接放行，但自检仍要能钉住这套语义，
    /// 所以把判定单独暴露出来（两版共用同一份实现，避免分支各自漂移）。
    /// </summary>
    public static string? OnlineBlockReason(bool isExpired, string? username, string feature)
    {
        if (string.IsNullOrEmpty(username)) return $"请先登录并激活会员后再使用{feature}";
        if (isExpired) return $"会员已过期，{feature}已停用 —— 请在「激活会员」页续费激活后再用";
        return null;
    }
}
