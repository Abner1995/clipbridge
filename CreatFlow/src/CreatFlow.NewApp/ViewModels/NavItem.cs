using Material.Icons;

namespace CreatFlow.NewApp.ViewModels;

/// <summary>
/// 导航分组（侧栏「发布/创作/管理」节）。
/// </summary>
public sealed class NavSection
{
    public required string Title { get; init; }
    public required IReadOnlyList<NavItem> Items { get; init; }
}

/// <summary>
/// 单个导航项 —— 数据驱动菜单（不再每页写 GoToXxx/IsActive 样板）。
/// PageType 非空 = 可导航页面（切换时经 DI 解析并缓存实例，页面状态跨切换保留）；
/// PageType 为空 = 占位/建设中项（禁用 + Note 标注阶段）。
///
/// 需要**活徽章**（如上传队列的未完成计数）的项得能被就地更新，
/// 所以它是 ObservableObject 而不是纯 record。
/// </summary>
public sealed partial class NavItem : ObservableObject
{
    public required string Key { get; init; }
    public required string Title { get; init; }

    /// <summary>矢量图标（Material Design Icons，替代 emoji）</summary>
    public MaterialIconKind Icon { get; init; }

    /// <summary>对应页面 VM 类型（null = 占位项）</summary>
    public Type? PageType { get; init; }

    /// <summary>当前是否可点（过期/阶段门控；数据驱动由 ShellViewModel 按会话状态重建）</summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>会员过期时仍放行的页面（2026-10-11 用户规则：功能页面一律要登录且激活，
    /// Expired 只能看到「激活会员」「账号设置」—— 后者是账户信息 / 登出，不是功能页）</summary>
    public bool AllowedWhenExpired { get; init; }

    /// <summary>右侧小字标注（如「P2 重构中」）</summary>
    public string? Note { get; init; }

    // ── 活徽章（上传队列的未完成计数）──

    private string? _badgeText;

    /// <summary>徽章文字（null/空 = 不显示）。由页面推动（如队列统计变化）</summary>
    public string? BadgeText
    {
        get => _badgeText;
        set
        {
            if (!SetProperty(ref _badgeText, value)) return;
            OnPropertyChanged(nameof(HasBadge));
        }
    }

    public bool HasBadge => !string.IsNullOrWhiteSpace(BadgeText);
}
