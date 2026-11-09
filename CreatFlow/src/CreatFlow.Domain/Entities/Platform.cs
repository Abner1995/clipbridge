namespace CreatFlow.Domain.Entities;

/// <summary>
/// 平台配置实体。
/// 存储各短视频平台的关键参数（名称、颜色、登录地址、Cookie 有效期等），
/// 替代桌面端硬编码和 x_system_configs 中的 platform.*_enabled 开关。
/// </summary>
public class Platform : Aggregate<PlatformId>
{
    /// <summary>平台标识（douyin / kuaishou / xiaohongshu / wechatvideo），不可变</summary>
    public string PlatformKey { get; private set; } = default!;

    /// <summary>平台中文名（抖音 / 快手 / 小红书 / 视频号）</summary>
    public string DisplayName { get; private set; } = default!;

    /// <summary>品牌色 Hex（#FF0049 等）</summary>
    public string BrandColorHex { get; private set; } = default!;

    /// <summary>平台登录页 URL（WebView2 跳转目标）</summary>
    public string LoginUrl { get; private set; } = default!;

    /// <summary>Cookie 预估有效天数</summary>
    public int EstimatedValidDays { get; private set; }

    /// <summary>是否启用（桌面端只显示已启用的平台）</summary>
    public bool IsEnabled { get; private set; }

    /// <summary>显示排序</summary>
    public int SortOrder { get; private set; }

    private Platform() { }

    public static Platform Create(
        PlatformId id,
        string platformKey,
        string displayName,
        string brandColorHex,
        string loginUrl,
        int estimatedValidDays,
        bool isEnabled = true,
        int sortOrder = 0)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(platformKey);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(brandColorHex);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(loginUrl);

        var now = Clock.Now;
        var platform = new Platform
        {
            Id = id,
            PlatformKey = platformKey,
            DisplayName = displayName,
            BrandColorHex = brandColorHex,
            LoginUrl = loginUrl,
            EstimatedValidDays = estimatedValidDays,
            IsEnabled = isEnabled,
            SortOrder = sortOrder,
            CreatedAt = now,
            LastModified = now,
        };

        return platform;
    }

    /// <summary>批量更新平台配置（管理端编辑）</summary>
    public void UpdateConfig(
        string displayName,
        string brandColorHex,
        string loginUrl,
        int estimatedValidDays,
        int sortOrder)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(brandColorHex);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(loginUrl);

        DisplayName = displayName;
        BrandColorHex = brandColorHex;
        LoginUrl = loginUrl;
        EstimatedValidDays = estimatedValidDays;
        SortOrder = sortOrder;
        LastModified = Clock.Now;
    }

    /// <summary>切换启用/禁用</summary>
    public void SetEnabled(bool enabled)
    {
        IsEnabled = enabled;
        LastModified = Clock.Now;
    }
}
