namespace CreatFlow.LocalStore.Configurations;

public class AsyncTaskItemConfiguration : IEntityTypeConfiguration<AsyncTaskItem>
{
    public void Configure(EntityTypeBuilder<AsyncTaskItem> builder)
    {
        builder.ToTable("x_async_task_item");

        // ---- Id: AsyncTaskItemId -> CHAR(36) ----
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id)
            .HasConversion(id => id.Value, guid => AsyncTaskItemId.Of(guid))
            .HasColumnName("id");

        // ---- TaskId: CHAR(36)，无外键（阿里规范：关联列只加普通索引）----
        builder.Property(i => i.TaskId)
            .HasConversion(id => id.Value, guid => AsyncTaskId.Of(guid))
            .HasColumnName("task_id")
            .IsRequired();

        builder.Property(i => i.TargetId)
            .HasColumnName("target_id").IsRequired();

        builder.Property(i => i.TargetKind)
            .HasColumnName("target_kind").HasConversion<int>().IsRequired();

        builder.Property(i => i.GroupKey)
            .HasColumnName("group_key").IsRequired();

        builder.Property(i => i.PlatformKey)
            .HasColumnName("platform_key").IsRequired();

        builder.Property(i => i.PlatformSessionId)
            .HasColumnName("platform_session_id").IsRequired(false);

        builder.Property(i => i.SessionLabel)
            .HasColumnName("session_label").IsRequired(false);

        builder.Property(i => i.PlatformObjectId)
            .HasColumnName("platform_object_id").IsRequired(false);

        // 合集内容类型快照（2026-11-05）：头条文章/视频合集删除走两套接口
        builder.Property(i => i.ContentType)
            .HasColumnName("content_type")
            .IsRequired()
            .HasDefaultValue(0);

        // ---- Status: TINYINT ----
        builder.Property(i => i.Status)
            .HasColumnName("status")
            .HasConversion<int>()
            .IsRequired()
            .HasDefaultValue(AsyncTaskItemStatus.Pending);

        builder.Property(i => i.ErrorMsg)
            .HasColumnName("error_msg").IsRequired(false);

        builder.Property(i => i.RetryCount)
            .HasColumnName("retry_count").IsRequired()
            .HasDefaultValue(0);

        builder.Property(i => i.FinishedAt)
            .HasColumnName("finished_at").IsRequired(false);

        // ---- 基类字段 ----
        builder.Property(i => i.CreatedAt)
            .HasColumnName("created_at");
        builder.Property(i => i.LastModified)
            .HasColumnName("updated_at");

        builder.Ignore(i => i.CreatedBy);
        builder.Ignore(i => i.LastModifiedBy);

        // ---- 索引 ----
        builder.HasIndex(i => i.TaskId).HasDatabaseName("idx_async_task_item_task_id");
        builder.HasIndex(i => i.GroupKey).HasDatabaseName("idx_async_task_item_group_key");
        builder.HasIndex(i => i.TargetId).HasDatabaseName("idx_async_task_item_target_id");
        builder.HasIndex(i => i.Status).HasDatabaseName("idx_async_task_item_status");
        builder.HasIndex(i => i.PlatformSessionId).HasDatabaseName("idx_async_task_item_session_id");
    }
}
