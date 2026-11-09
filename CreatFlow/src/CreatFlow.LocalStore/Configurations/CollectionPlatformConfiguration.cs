namespace CreatFlow.LocalStore.Configurations;

public class CollectionPlatformConfiguration : IEntityTypeConfiguration<CollectionPlatform>
{
    public void Configure(EntityTypeBuilder<CollectionPlatform> builder)
    {
        builder.ToTable("x_collection_platforms");

        // ---- Id: CollectionPlatformId -> CHAR(36) ----
        builder.HasKey(cp => cp.Id);
        builder.Property(cp => cp.Id)
            .HasConversion(id => id.Value, guid => CollectionPlatformId.Of(guid))
            .HasColumnName("id");

        // ---- CollectionId: CHAR(36) ----
        builder.Property(cp => cp.CollectionId)
            .HasConversion(id => id.Value, guid => CollectionId.Of(guid))
            .HasColumnName("collection_id")
            .IsRequired();

        // ---- PlatformKey: VARCHAR(30) ----
        builder.Property(cp => cp.PlatformKey)
            .HasColumnName("platform_key")
            .IsRequired();

        // ---- PlatformSessionId: CHAR(36) NULL ----
        builder.Property(cp => cp.PlatformSessionId)
            .HasColumnName("platform_session_id")
            .IsRequired(false);

        // ---- PlatformCollectionId: VARCHAR(500) NULL ----
        builder.Property(cp => cp.PlatformCollectionId)
            .HasColumnName("platform_collection_id")
            .IsRequired(false);

        // ---- CollectionType: VARCHAR(50) NULL ----
        builder.Property(cp => cp.CollectionType)
            .HasColumnName("collection_type")
            .IsRequired(false);

        // ---- Title: VARCHAR(100) NULL ----
        builder.Property(cp => cp.Title)
            .HasColumnName("title")
            .IsRequired(false);

        // ---- Description: VARCHAR(500) NULL ----
        builder.Property(cp => cp.Description)
            .HasColumnName("description")
            .IsRequired(false);

        // ---- CoverImage: VARCHAR(500) NULL ----
        builder.Property(cp => cp.CoverImage)
            .HasColumnName("cover_image")
            .IsRequired(false);

        // ---- Status: TINYINT ----
        builder.Property(cp => cp.Status)
            .HasColumnName("status")
            .HasConversion<int>()
            .IsRequired()
            .HasDefaultValue(CollectionPlatformStatus.Pending);

        // ---- ErrorMsg: VARCHAR(500) NULL ----
        builder.Property(cp => cp.ErrorMsg)
            .HasColumnName("error_msg")
            .IsRequired(false);

        // ---- ExtraDataJson: JSON NULL ----
        builder.Property(cp => cp.ExtraDataJson)
            .HasColumnName("extra_data")
            .IsRequired(false);

        // ---- 基类字段 ----
        builder.Property(cp => cp.CreatedAt)
            .HasColumnName("created_at");

        builder.Property(cp => cp.LastModified)
            .HasColumnName("updated_at");

        builder.Ignore(cp => cp.CreatedBy);
        builder.Ignore(cp => cp.LastModifiedBy);

        // ---- 索引 ----
        builder.HasIndex(cp => cp.CollectionId)
            .HasDatabaseName("idx_collection_platforms_collection_id");

        builder.HasIndex(cp => cp.PlatformKey)
            .HasDatabaseName("idx_collection_platforms_platform_key");

        builder.HasIndex(cp => cp.PlatformSessionId)
            .HasDatabaseName("idx_collection_platforms_session_id");
    }
}
