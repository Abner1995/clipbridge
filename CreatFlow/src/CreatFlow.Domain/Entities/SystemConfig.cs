namespace CreatFlow.Domain.Entities;

/// <summary>
/// 系统配置项（key-value 存储）。
/// 用于邮箱配置、上传限制、平台开关等动态配置。
/// 不是 DDD 聚合根（无业务不变量），但为了一致性沿用 Entity 模式。
/// </summary>
public class SystemConfig : Aggregate<SystemConfigId>
{
    /// <summary>配置键（如 email.host、upload.max_mb）</summary>
    public string Key { get; private set; } = default!;

    /// <summary>配置值（字符串存储，编辑器负责类型转换）</summary>
    public string Value { get; private set; } = default!;

    /// <summary>分组（system / email / upload / platform）</summary>
    public string Category { get; private set; } = "system";

    /// <summary>管理端显示的标签</summary>
    public string? Label { get; private set; }

    /// <summary>管理端控件类型（text / number / switch / json / password）</summary>
    public string Editor { get; private set; } = "text";

    /// <summary>排序</summary>
    public int SortOrder { get; private set; }

    /// <summary>是否暴露给桌面端（GET /api/config/public）</summary>
    public bool IsPublic { get; private set; }

    private SystemConfig() { }

    public static SystemConfig Create(
        SystemConfigId id,
        string key,
        string value,
        string category = "system",
        string? label = null,
        string editor = "text",
        int sortOrder = 0,
        bool isPublic = false)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(category);

        var now = Clock.Now;
        var cfg = new SystemConfig
        {
            Id = id,
            Key = key,
            Value = value,
            Category = category,
            Label = label,
            Editor = editor,
            SortOrder = sortOrder,
            IsPublic = isPublic,
            CreatedAt = now,
            LastModified = now,
        };

        return cfg;
    }

    /// <summary>更新配置值</summary>
    public void UpdateValue(string newValue)
    {
        Value = newValue;
        LastModified = Clock.Now;
        AddDomainEvent(new SystemConfigUpdatedEvent(Id, Key, Category));
    }
}
