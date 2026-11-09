namespace CreatFlow.LocalStore.Configurations;

public class PublishTaskConfiguration : IEntityTypeConfiguration<PublishTask>
{
    public void Configure(EntityTypeBuilder<PublishTask> builder)
    {
        builder.ToTable("x_publish_tasks");

        // ---- Id: PublishTaskId -> CHAR(36) ----
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id)
            .HasConversion(id => id.Value, guid => PublishTaskId.Of(guid))
            .HasColumnName("id");

        // ---- MemberId: CHAR(36) ----
        builder.Property(t => t.MemberId)
            .HasColumnName("member_id")
            .IsRequired();

        // ---- VideoFilePath: VARCHAR(500) ----
        builder.Property(t => t.VideoFilePath)
            .HasColumnName("video_file_path")
            .IsRequired();

        // ---- VideoFileName: VARCHAR(200) ----
        builder.Property(t => t.VideoFileName)
            .HasColumnName("video_file_name")
            .IsRequired();

        // ---- VideoFileSize: BIGINT ----
        builder.Property(t => t.VideoFileSize)
            .HasColumnName("video_file_size")
            .IsRequired()
            .HasDefaultValue(0L);

        // ---- VideoHash: VARCHAR(64) ----
        builder.Property(t => t.VideoHash)
            .HasColumnName("video_hash")
            .IsRequired();

        // ---- VideoDurationS: INT NULL ----
        builder.Property(t => t.VideoDurationS)
            .HasColumnName("video_duration_s")
            .IsRequired(false);

        // ---- VideoWidth: INT NULL ----
        builder.Property(t => t.VideoWidth)
            .HasColumnName("video_width")
            .IsRequired(false);

        // ---- VideoHeight: INT NULL ----
        builder.Property(t => t.VideoHeight)
            .HasColumnName("video_height")
            .IsRequired(false);

        // ---- CoverFilePath: VARCHAR(500) NULL ----
        builder.Property(t => t.CoverFilePath)
            .HasColumnName("cover_file_path")
            .IsRequired(false);

        // ---- CommonTitle: VARCHAR(200) NULL ----
        builder.Property(t => t.CommonTitle)
            .HasColumnName("common_title")
            .IsRequired(false);

        // ---- CommonDesc: TEXT NULL ----
        builder.Property(t => t.CommonDesc)
            .HasColumnName("common_desc")
            .IsRequired(false);

        // ---- ScheduledAt: DATETIME NULL ----
        builder.Property(t => t.ScheduledAt)
            .HasColumnName("scheduled_at")
            .IsRequired(false);

        // ---- Status: TINYINT ----
        builder.Property(t => t.Status)
            .HasColumnName("status")
            .HasConversion<int>()
            .IsRequired()
            .HasDefaultValue(PublishTaskStatus.Pending);

        // ---- 基类字段 ----
        builder.Property(t => t.CreatedAt)
            .HasColumnName("created_at");

        builder.Property(t => t.LastModified)
            .HasColumnName("updated_at");

        builder.Ignore(t => t.CreatedBy);
        builder.Ignore(t => t.LastModifiedBy);

        // ---- 忽略导航属性（子实体通过 Repository 独立查询） ----
        builder.Ignore(t => t.Platforms);

        // ---- 索引 ----
        builder.HasIndex(t => t.MemberId)
            .HasDatabaseName("idx_publish_tasks_member_id");

        builder.HasIndex(t => t.Status)
            .HasDatabaseName("idx_publish_tasks_status");

        builder.HasIndex(t => t.CreatedAt)
            .HasDatabaseName("idx_publish_tasks_created_at");
    }
}
