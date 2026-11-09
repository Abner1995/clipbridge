namespace CreatFlow.LocalStore;

/// <summary>
/// 本地库种子数据：固定本机会员 + 四个平台配置。
/// 幂等（存在即跳过），每次启动都跑一遍 —— 老库升级时不覆盖用户数据。
/// </summary>
public static class LocalSeeder
{
    public static async Task EnsureSeedAsync(LocalDbContext db, CancellationToken ct = default)
    {
        await EnsureMemberAsync(db, ct);
        await EnsurePlatformsAsync(db, ct);
    }

    private static async Task EnsureMemberAsync(LocalDbContext db, CancellationToken ct)
    {
        if (await db.Member.AnyAsync(m => m.Id == MemberId.Of(LocalIdentity.MemberId), ct))
            return;

        // 单机版不做登录校验：密码哈希是占位值（不可用于任何登录），
        // 到期时间给足 100 年（业务层若读 IsExpired 也永远放行）。
        var member = Member.Create(
            MemberId.Of(LocalIdentity.MemberId),
            new Username(LocalIdentity.Username),
            new PasswordHash("local-standalone-no-login"),
            PhoneNumber.Of("13800000000"),
            Email.Of("local@creatflow.local"),
            durationDays: 36500);

        db.Member.Add(member);
        await db.SaveChangesAsync(ct);
    }

    private static async Task EnsurePlatformsAsync(LocalDbContext db, CancellationToken ct)
    {
        if (await db.Platform.AnyAsync(ct))
            return;

        // 与联网版 DataSeeder.SeedPlatformsAsync 逐字一致（key / 名称 / 品牌色 / 预估有效天数 / 排序）
        var platforms = new List<Platform>
        {
            Platform.Create(PlatformId.Of(Guid.NewGuid()), "douyin", "抖音", "#FF0049",
                "https://creator.douyin.com", 3, true, 10),
            Platform.Create(PlatformId.Of(Guid.NewGuid()), "kuaishou", "快手", "#FF4906",
                "https://cp.kuaishou.com", 5, true, 20),
            Platform.Create(PlatformId.Of(Guid.NewGuid()), "xiaohongshu", "小红书", "#FE2C55",
                "https://creator.xiaohongshu.com", 3, true, 30),
            Platform.Create(PlatformId.Of(Guid.NewGuid()), "wechatvideo", "视频号", "#07C160",
                "https://channels.weixin.qq.com", 2, true, 40),
        };

        db.Platform.AddRange(platforms);
        await db.SaveChangesAsync(ct);
    }
}
