namespace CreatFlow.Domain.Entities;

using CreatFlow.Domain.ValueObjects;

/// <summary>
/// 公告聚合根。管理员创建后推送到桌面端展示。
/// </summary>
public class Announcement : Aggregate<AnnouncementId>
{
    public string Title { get; private set; } = default!;
    public string Content { get; private set; } = default!;
    public AnnouncementType Type { get; private set; }
    public bool IsActive { get; private set; } = true;
    public AdminId CreatorId { get; private set; } = default!;

    private Announcement() { }

    public static Announcement Create(
        AnnouncementId id,
        string title,
        string content,
        AnnouncementType type,
        AdminId createdBy)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(createdBy);

        if (string.IsNullOrWhiteSpace(title) || title.Length > 200)
            throw new DomainException("公告标题不能为空且不超过 200 个字符。");
        if (string.IsNullOrWhiteSpace(content))
            throw new DomainException("公告内容不能为空。");

        var now = Clock.Now;
        var announcement = new Announcement
        {
            Id = id,
            Title = title.Trim(),
            Content = content.Trim(),
            Type = type,
            IsActive = true,
            CreatorId = createdBy,
            CreatedAt = now,
            LastModified = now
        };

        announcement.AddDomainEvent(new AnnouncementCreatedEvent(id, title.Trim(), type));
        return announcement;
    }

    public void Edit(string title, string content, AnnouncementType type)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 200)
            throw new DomainException("公告标题不能为空且不超过 200 个字符。");
        if (string.IsNullOrWhiteSpace(content))
            throw new DomainException("公告内容不能为空。");

        Title = title.Trim();
        Content = content.Trim();
        Type = type;
        LastModified = Clock.Now;
        AddDomainEvent(new AnnouncementUpdatedEvent(Id, Title));
    }

    public void Activate()
    {
        if (IsActive) return;
        IsActive = true;
        LastModified = Clock.Now;
    }

    public void Deactivate()
    {
        if (!IsActive) return;
        IsActive = false;
        LastModified = Clock.Now;
    }
}

public enum AnnouncementType
{
    Info = 0,
    Warning = 1,
    Urgent = 2
}
