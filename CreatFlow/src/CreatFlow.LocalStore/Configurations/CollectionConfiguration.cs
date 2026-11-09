namespace CreatFlow.LocalStore.Configurations;

public class CollectionConfiguration : IEntityTypeConfiguration<Collection>
{
    public void Configure(EntityTypeBuilder<Collection> builder)
    {
        builder.ToTable("x_collections");

        // ---- Id: CollectionId -> CHAR(36) ----
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id)
            .HasConversion(id => id.Value, guid => CollectionId.Of(guid))
            .HasColumnName("id");

        // ---- MemberId: CHAR(36) ----
        builder.Property(c => c.MemberId)
            .HasColumnName("member_id")
            .IsRequired();

        // ---- ContentType: INT NOT NULL DEFAULT 0（2026-11-05 头条文章/视频合集） ----
        builder.Property(c => c.ContentType)
            .HasColumnName("content_type")
            .HasConversion<int>()
            .IsRequired()
            .HasDefaultValue(CollectionContentType.Video);

        // ---- Title: VARCHAR(100) ----
        builder.Property(c => c.Title)
            .HasColumnName("title")
            .IsRequired();

        // ---- Description: VARCHAR(500) NULL ----
        builder.Property(c => c.Description)
            .HasColumnName("description")
            .IsRequired(false);

        // ---- CoverImagePath: VARCHAR(500) NULL ----
        builder.Property(c => c.CoverImagePath)
            .HasColumnName("cover_image_path")
            .IsRequired(false);

        // ---- CoverImageUrl: VARCHAR(500) NULL ----
        builder.Property(c => c.CoverImageUrl)
            .HasColumnName("cover_image_url")
            .IsRequired(false);

        // ---- 基类字段 ----
        builder.Property(c => c.CreatedAt)
            .HasColumnName("created_at");

        builder.Property(c => c.LastModified)
            .HasColumnName("updated_at");

        builder.Ignore(c => c.CreatedBy);
        builder.Ignore(c => c.LastModifiedBy);

        // ---- 索引 ----
        builder.HasIndex(c => c.MemberId)
            .HasDatabaseName("idx_collections_member_id");
    }
}
