namespace CreatFlow.LocalStore.Configurations;

public class PublishPlatformConfiguration : IEntityTypeConfiguration<PublishPlatform>
{
    public void Configure(EntityTypeBuilder<PublishPlatform> builder)
    {
        builder.ToTable("x_publish_platforms");

        // ---- Id: PublishPlatformId -> CHAR(36) ----
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id)
            .HasConversion(id => id.Value, guid => PublishPlatformId.Of(guid))
            .HasColumnName("id");

        // ---- PublishTaskId: CHAR(36) ----
        builder.Property(p => p.PublishTaskId)
            .HasConversion(id => id.Value, guid => PublishTaskId.Of(guid))
            .HasColumnName("publish_task_id")
            .IsRequired();

        // ---- PlatformKey: VARCHAR(50) ----
        builder.Property(p => p.PlatformKey)
            .HasColumnName("platform_key")
            .IsRequired();

        // ---- PlatformSessionId: CHAR(36) NULL ----
        builder.Property(p => p.PlatformSessionId)
            .HasColumnName("platform_session_id")
            .IsRequired(false);

        // ---- Title: VARCHAR(200) NULL ----
        builder.Property(p => p.Title)
            .HasColumnName("title")
            .IsRequired(false);

        // ---- Description: TEXT NULL ----
        builder.Property(p => p.Description)
            .HasColumnName("description")
            .IsRequired(false);

        // ---- CoverFilePath: VARCHAR(500) NULL ----
        builder.Property(p => p.CoverFilePath)
            .HasColumnName("cover_file_path")
            .IsRequired(false);

        // ---- CoverUrl: VARCHAR(500) NULL ----
        builder.Property(p => p.CoverUrl)
            .HasColumnName("cover_url")
            .IsRequired(false);

        // ---- CollectionId: CHAR(36) NULL ----
        builder.Property(p => p.CollectionId)
            .HasColumnName("collection_id")
            .IsRequired(false);

        // ---- ExtraDataJson: MEDIUMTEXT NULL ----
        builder.Property(p => p.ExtraDataJson)
            .HasColumnName("extra_data_json")
            .IsRequired(false);

        // ---- Status: TINYINT ----
        builder.Property(p => p.Status)
            .HasColumnName("status")
            .HasConversion<int>()
            .IsRequired()
            .HasDefaultValue(PublishPlatformStatus.Pending);

        // ---- ProgressPct: TINYINT ----
        builder.Property(p => p.ProgressPct)
            .HasColumnName("progress_pct")
            .IsRequired()
            .HasDefaultValue(0);

        // ---- ErrorMsg: VARCHAR(500) NULL ----
        builder.Property(p => p.ErrorMsg)
            .HasColumnName("error_msg")
            .IsRequired(false);

        // ---- RetryCount: INT ----
        builder.Property(p => p.RetryCount)
            .HasColumnName("retry_count")
            .IsRequired()
            .HasDefaultValue(0);

        // ---- HiddenInQueue: TINYINT(1) ----
        builder.Property(p => p.HiddenInQueue)
            .HasColumnName("hidden_in_queue")
            .IsRequired()
            .HasDefaultValue(false);

        // ---- PlatformVideoId: VARCHAR(100) NULL ----
        builder.Property(p => p.PlatformVideoId)
            .HasColumnName("platform_video_id")
            .IsRequired(false);

        // ---- PlatformVideoUrl: VARCHAR(500) NULL ----
        builder.Property(p => p.PlatformVideoUrl)
            .HasColumnName("platform_video_url")
            .IsRequired(false);

        // ---- PublishedAt: DATETIME NULL ----
        builder.Property(p => p.PublishedAt)
            .HasColumnName("published_at")
            .IsRequired(false);

        // ---- 基类字段 ----
        builder.Property(p => p.CreatedAt)
            .HasColumnName("created_at");

        builder.Property(p => p.LastModified)
            .HasColumnName("updated_at");

        builder.Ignore(p => p.CreatedBy);
        builder.Ignore(p => p.LastModifiedBy);

        // ---- 索引 ----
        builder.HasIndex(p => p.PublishTaskId)
            .HasDatabaseName("idx_publish_platforms_task_id");

        builder.HasIndex(p => p.PlatformKey)
            .HasDatabaseName("idx_publish_platforms_platform_key");

        builder.HasIndex(p => p.PlatformSessionId)
            .HasDatabaseName("idx_publish_platforms_session_id");

        builder.HasIndex(p => p.Status)
            .HasDatabaseName("idx_publish_platforms_status");
    }
}
