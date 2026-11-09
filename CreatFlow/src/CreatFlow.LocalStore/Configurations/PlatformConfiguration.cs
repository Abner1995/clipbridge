namespace CreatFlow.LocalStore.Configurations;

public class PlatformConfiguration : IEntityTypeConfiguration<Platform>
{
    public void Configure(EntityTypeBuilder<Platform> builder)
    {
        builder.ToTable("x_platforms");

        // ---- Id: PlatformId -> CHAR(36) ----
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id)
            .HasConversion(
                id => id.Value,
                guid => PlatformId.Of(guid))
            .HasColumnName("id");

        // ---- PlatformKey: VARCHAR(50) UNIQUE ----
        builder.Property(p => p.PlatformKey)
            .HasColumnName("platform_key")
            .IsRequired();

        builder.HasIndex(p => p.PlatformKey).IsUnique();

        // ---- DisplayName: VARCHAR(50) ----
        builder.Property(p => p.DisplayName)
            .HasColumnName("display_name")
            .IsRequired();

        // ---- BrandColorHex: VARCHAR(10) ----
        builder.Property(p => p.BrandColorHex)
            .HasColumnName("brand_color_hex")
            .IsRequired();

        // ---- LoginUrl: VARCHAR(500) ----
        builder.Property(p => p.LoginUrl)
            .HasColumnName("login_url")
            .IsRequired();

        // ---- EstimatedValidDays: INT ----
        builder.Property(p => p.EstimatedValidDays)
            .HasColumnName("estimated_valid_days")
            .IsRequired()
            .HasDefaultValue(3);

        // ---- IsEnabled: TINYINT(1) ----
        builder.Property(p => p.IsEnabled)
            .HasColumnName("is_enabled")
            .IsRequired()
            .HasDefaultValue(true);

        // ---- SortOrder: INT ----
        builder.Property(p => p.SortOrder)
            .HasColumnName("sort_order")
            .IsRequired()
            .HasDefaultValue(0);

        // ---- 基类字段 ----
        builder.Property(p => p.CreatedAt)
            .HasColumnName("created_at");

        builder.Property(p => p.LastModified)
            .HasColumnName("updated_at");

        builder.Ignore(p => p.CreatedBy);
        builder.Ignore(p => p.LastModifiedBy);
        builder.Ignore(p => p.DomainEvents);
    }
}
