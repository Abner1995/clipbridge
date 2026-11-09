namespace CreatFlow.LocalStore.Configurations;

public class MemberConfiguration : IEntityTypeConfiguration<Member>
{
    public void Configure(EntityTypeBuilder<Member> builder)
    {
        builder.ToTable("x_member");

        // ---- Id: MemberId → CHAR(36) ----
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id)
            .HasConversion(
                id => id.Value,               // MemberId → Guid 写入
                guid => MemberId.Of(guid))     // Guid → MemberId 读出
            .HasColumnName("id");

        // ---- Username: 值对象 → VARCHAR(50) ----
        builder.Property(m => m.Username)
            .HasConversion(
                u => u.Value,                  // Username → string
                s => new Username(s))           // string → Username (走校验)
            .HasColumnName("username")
            .HasMaxLength(50)
            .IsRequired();

        builder.HasIndex(m => m.Username).IsUnique();

        // ---- PasswordHash: 值对象 → VARCHAR(255) ----
        builder.Property(m => m.PasswordHash)
            .HasConversion(
                p => p.Hash,                   // PasswordHash → string
                h => new PasswordHash(h))       // string → PasswordHash
            .HasColumnName("password_hash")
            .HasMaxLength(255)
            .IsRequired();

        // ---- PhoneNumber: 值对象 → VARCHAR(20) ----
        builder.Property(m => m.PhoneNumber)
            .HasConversion(
                p => p.Number,                 // PhoneNumber → string
                s => PhoneNumber.Of(s))         // string → PhoneNumber
            .HasColumnName("phone")
            .HasMaxLength(20)
            .IsRequired();

        builder.HasIndex(m => m.PhoneNumber).IsUnique();

        // ---- Email: 值对象 → VARCHAR(320) ----
        builder.Property(m => m.Email)
            .HasConversion(
                e => e.Address,                // Email → string
                s => Email.Of(s))               // string → Email
            .HasColumnName("email")
            .HasMaxLength(320)
            .IsRequired();

        // ---- ExpireDate ----
        builder.Property(m => m.ExpireDate)
            .HasColumnName("expire_date")
            .IsRequired();

        // ---- IsActive ----
        builder.Property(m => m.IsActive)
            .HasColumnName("is_active")
            .HasDefaultValue(true)
            .IsRequired();

        // ---- LastLoginIp ----
        builder.Property(m => m.LastLoginIp)
            .HasColumnName("last_login_ip")
            .HasMaxLength(45);

        // ---- LastLoginAt ----
        builder.Property(m => m.LastLoginAt)
            .HasColumnName("last_login_at");

        // ---- 基类 Entity 字段 ----
        builder.Property(m => m.CreatedAt)
            .HasColumnName("created_at");

        builder.Property(m => m.LastModified)
            .HasColumnName("updated_at");

        // 暂未使用审计字段
        builder.Ignore(m => m.CreatedBy);
        builder.Ignore(m => m.LastModifiedBy);

        // 领域事件不持久化（内存中短暂存在，用完即清）
        builder.Ignore(m => m.DomainEvents);
    }
}
