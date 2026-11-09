namespace CreatFlow.LocalStore.Configurations;

public class AsyncTaskConfiguration : IEntityTypeConfiguration<AsyncTask>
{
    public void Configure(EntityTypeBuilder<AsyncTask> builder)
    {
        builder.ToTable("x_async_tasks");

        // ---- Id: AsyncTaskId -> CHAR(36) ----
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id)
            .HasConversion(id => id.Value, guid => AsyncTaskId.Of(guid))
            .HasColumnName("id");

        // ---- MemberId: CHAR(36) ----
        builder.Property(t => t.MemberId)
            .HasColumnName("member_id")
            .IsRequired();

        // ---- Kind: TINYINT ----
        builder.Property(t => t.Kind)
            .HasColumnName("kind")
            .HasConversion<int>()
            .IsRequired();

        // ---- Title: VARCHAR(200) ----
        builder.Property(t => t.Title)
            .HasColumnName("title")
            .IsRequired();

        // ---- Status: TINYINT ----
        builder.Property(t => t.Status)
            .HasColumnName("status")
            .HasConversion<int>()
            .IsRequired()
            .HasDefaultValue(AsyncTaskStatus.Pending);

        // ---- 冗余计数（列表页免聚合）----
        builder.Property(t => t.TotalCount)
            .HasColumnName("total_count").IsRequired()
            .HasDefaultValue(0);

        builder.Property(t => t.DoneCount)
            .HasColumnName("done_count").IsRequired()
            .HasDefaultValue(0);

        builder.Property(t => t.FailedCount)
            .HasColumnName("failed_count").IsRequired()
            .HasDefaultValue(0);

        // ---- 基类字段 ----
        builder.Property(t => t.CreatedAt)
            .HasColumnName("created_at");

        builder.Property(t => t.LastModified)
            .HasColumnName("updated_at");

        builder.Ignore(t => t.CreatedBy);
        builder.Ignore(t => t.LastModifiedBy);

        // ---- 忽略导航属性（子实体通过 Repository 独立查询；见 AsyncTask.Items 的注释）----
        builder.Ignore(t => t.Items);

        // ---- 索引 ----
        builder.HasIndex(t => t.MemberId).HasDatabaseName("idx_async_tasks_member_id");
        builder.HasIndex(t => t.Status).HasDatabaseName("idx_async_tasks_status");
        builder.HasIndex(t => t.CreatedAt).HasDatabaseName("idx_async_tasks_created_at");
    }
}
