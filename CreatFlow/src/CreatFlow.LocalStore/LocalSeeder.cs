namespace CreatFlow.LocalStore;

/// <summary>
/// 本地库种子数据：固定本机会员 + 六个平台配置。
/// 幂等（存在即跳过），每次启动都跑一遍 —— 老库升级时不覆盖用户数据；
/// 平台表按 key 补齐新增平台（B站 / 头条），老库也能看到。
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
        // 与联网版 DataSeeder.DefaultPlatforms 逐字一致（key / 名称 / 品牌色 / 预估有效天数 / 排序）
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
            // 2026-11-05：B站 / 头条（平台账号页绑定 + 发布链路）
            Platform.Create(PlatformId.Of(Guid.NewGuid()), PlatformNames.Bilibili, "B站", "#FB7299",
                "https://member.bilibili.com/platform/home", 3, true, 50),
            Platform.Create(PlatformId.Of(Guid.NewGuid()), PlatformNames.Toutiao, "头条", "#F04142",
                "https://mp.toutiao.com/profile_v4/index", 3, true, 60),
        };

        // 老库升级：平台表按 key 补齐缺的行（存在即跳过；用户改过的显示名/颜色/登录地址一律不动）
        var existing = (await db.Platform.Select(p => p.PlatformKey).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = platforms.Where(p => !existing.Contains(p.PlatformKey)).ToList();
        if (missing.Count == 0) return;

        db.Platform.AddRange(missing);
        await db.SaveChangesAsync(ct);
    }
}
