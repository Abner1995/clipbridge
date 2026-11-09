namespace CreatFlow.LocalStore.Configurations;

public class PlatformSessionConfiguration : IEntityTypeConfiguration<PlatformSession>
{
    public void Configure(EntityTypeBuilder<PlatformSession> builder)
    {
        builder.ToTable("x_platform_sessions");

        // ---- Id: PlatformSessionId -> CHAR(36) ----
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id)
            .HasConversion(id => id.Value, guid => PlatformSessionId.Of(guid))
            .HasColumnName("id");

        // ---- MemberId: CHAR(36) ----
        builder.Property(s => s.MemberId)
            .HasColumnName("member_id")
            .IsRequired();

        // ---- PlatformKey: VARCHAR(20) ----
        builder.Property(s => s.PlatformKey)
            .HasColumnName("platform_key")
            .IsRequired();

        // ---- AccountLabel: VARCHAR(100) NULL ----
        builder.Property(s => s.AccountLabel)
            .HasColumnName("account_label")
            .IsRequired(false);

        // ---- IsPrimary: TINYINT(1) ----
        builder.Property(s => s.IsPrimary)
            .HasColumnName("is_primary")
            .IsRequired()
            .HasDefaultValue(false);

        // ---- PlatformUid: VARCHAR(100) NULL ----
        builder.Property(s => s.PlatformUid)
            .HasColumnName("platform_uid")
            .IsRequired(false);

        // ---- PlatformName: VARCHAR(200) NULL ----
        builder.Property(s => s.PlatformName)
            .HasColumnName("platform_name")
            .IsRequired(false);

        // ---- PlatformAvatar: VARCHAR(500) NULL ----
        builder.Property(s => s.PlatformAvatar)
            .HasColumnName("platform_avatar")
            .IsRequired(false);

        // ---- Cookie: MEDIUMTEXT NULL ----
        builder.Property(s => s.Cookie)
            .HasColumnName("cookie")
            .IsRequired(false);

        // ---- LocalStorageJson: JSON NULL ----
        builder.Property(s => s.LocalStorageJson)
            .HasColumnName("local_storage")
            .IsRequired(false);

        // ---- ExtraDataJson: JSON NULL ----
        builder.Property(s => s.ExtraDataJson)
            .HasColumnName("extra_data")
            .IsRequired(false);

        // ---- Status: TINYINT ----
        builder.Property(s => s.Status)
            .HasColumnName("status")
            .HasConversion<int>()
            .IsRequired()
            .HasDefaultValue(PlatformSessionStatus.Active);

        // ---- LoggedInAt: DATETIME(6) ----
        builder.Property(s => s.LoggedInAt)
            .HasColumnName("logged_in_at")
            .IsRequired();

        // ---- ProbedAt: DATETIME(6) NULL ----
        builder.Property(s => s.ProbedAt)
            .HasColumnName("probed_at")
            .IsRequired(false);

        // ---- ProbedResult: VARCHAR(50) NULL ----
        builder.Property(s => s.ProbedResult)
            .HasColumnName("probed_result")
            .IsRequired(false);

        // ---- ExpiredAt: DATETIME(6) NULL ----
        builder.Property(s => s.ExpiredAt)
            .HasColumnName("expired_at")
            .IsRequired(false);

        // ---- 基类字段 ----
        builder.Property(s => s.CreatedAt)
            .HasColumnName("created_at");

        builder.Property(s => s.LastModified)
            .HasColumnName("updated_at");

        builder.Ignore(s => s.CreatedBy);
        builder.Ignore(s => s.LastModifiedBy);

        // ---- 索引 ----
        builder.HasIndex(s => new { s.MemberId, s.PlatformKey })
            .HasDatabaseName("idx_platform_sessions_member_platform");

        builder.HasIndex(s => new { s.PlatformKey, s.Status })
            .HasDatabaseName("idx_platform_sessions_platform_status");
    }
}
