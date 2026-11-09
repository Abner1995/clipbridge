namespace CreatFlow.LocalStore;

/// <summary>
/// 单机版本地业务库（SQLite）—— 表结构与联网版 MySQL 的 9 张业务表一致
/// （x_ 前缀、值对象转换器、无外键、索引同名），所以 Application 层服务可以原样复用。
///
/// 与 AppDbContext 的差异：
///   · 没有领域事件分发器（单机版不需要审计日志 handler；事件仍在实体上，不落库）
///   · 保留「created_at 只写一次」保护 —— Update() 全列更新会把创建时间改掉
/// </summary>
public class LocalDbContext : DbContext
{
    public LocalDbContext(DbContextOptions<LocalDbContext> options) : base(options)
    {
    }

    public DbSet<Member> Member { get; set; }
    public DbSet<Platform> Platform { get; set; }
    public DbSet<PlatformSession> PlatformSession { get; set; }
    public DbSet<PublishTask> PublishTask { get; set; }
    public DbSet<PublishPlatform> PublishPlatform { get; set; }
    public DbSet<Collection> Collection { get; set; }
    public DbSet<CollectionPlatform> CollectionPlatform { get; set; }
    public DbSet<AsyncTask> AsyncTask { get; set; }
    public DbSet<AsyncTaskItem> AsyncTaskItem { get; set; }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(LocalDbContext).Assembly);
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        ProtectCreatedAtOnUpdate();
        return await base.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// 将 Modified 实体的 CreatedAt 标记为未修改，使 UPDATE 语句不包含 created_at 列。
    /// 与 AppDbContext 同一套规则（实体基类统一用 CreatedAt 属性名）。
    /// </summary>
    private void ProtectCreatedAtOnUpdate()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State != EntityState.Modified) continue;

            var createdAt = entry.Metadata.FindProperty("CreatedAt");
            if (createdAt is not null)
                entry.Property(createdAt.Name).IsModified = false;
        }
    }
}
