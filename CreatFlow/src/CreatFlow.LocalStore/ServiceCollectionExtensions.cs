using Microsoft.Extensions.DependencyInjection;
using CreatFlow.Application.Common.Probes;

namespace CreatFlow.LocalStore;

/// <summary>
/// 单机版 DI 注册：本地库 + 9 个仓储 + 4 个 Application 业务服务（全部 Scoped）。
///
/// 业务服务原样复用联网版（Publish / Collection / AsyncTask / PlatformSession），
/// 所以「先删平台后删记录」「合集全组清完才删本地」「认领不改状态」这些规则只有一份实现。
/// 本地路由每次调用创建一个 scope，失败/成功都不会跨请求串状态。
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLocalStore(this IServiceCollection services, string databasePath)
    {
        services.AddDbContext<LocalDbContext>(options =>
            options.UseSqlite(LocalStorePaths.ConnectionString(databasePath)));

        // ── 仓储（与 Infrastructure 同实现，换 DbContext / 数据库方言）──
        services.AddScoped<IMemberRepository, MemberRepository>();
        services.AddScoped<IPlatformRepository, PlatformRepository>();
        services.AddScoped<IPlatformSessionRepository, PlatformSessionRepository>();
        services.AddScoped<IPublishTaskRepository, PublishTaskRepository>();
        services.AddScoped<IPublishPlatformRepository, PublishPlatformRepository>();
        services.AddScoped<ICollectionRepository, CollectionRepository>();
        services.AddScoped<ICollectionPlatformRepository, CollectionPlatformRepository>();
        services.AddScoped<IAsyncTaskRepository, AsyncTaskRepository>();
        services.AddScoped<IAsyncTaskItemRepository, AsyncTaskItemRepository>();

        // ── 平台探活（与联网版同一份实现，从 Infrastructure 搬到了 Application/Common/Probes）──
        // 抖音 / 视频号有 HTTP 探针；快手 / 小红书与联网版一致没有探针（Composite 返回 no_prober，
        // 保持原状态）—— 单机版不自创一套「假探活」。探针只读 Cookie 发页面请求，不经服务端。
        services.AddScoped<DouyinPlatformProber>(_ =>
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.Add("Accept-Language", "zh-CN,zh;q=0.9");
            return new DouyinPlatformProber(client);
        });
        services.AddScoped<WechatVideoPlatformProber>(_ =>
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.Add("Accept-Language", "zh-CN,zh;q=0.9");
            return new WechatVideoPlatformProber(client);
        });
        services.AddScoped<BilibiliPlatformProber>(_ =>
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.Add("Accept-Language", "zh-CN,zh;q=0.9");
            return new BilibiliPlatformProber(client);
        });
        services.AddScoped<ToutiaoPlatformProber>(_ =>
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.Add("Accept-Language", "zh-CN,zh;q=0.9");
            return new ToutiaoPlatformProber(client);
        });
        services.AddScoped<IPlatformProber>(sp => new CompositePlatformProber(
        [
            sp.GetRequiredService<DouyinPlatformProber>(),
            sp.GetRequiredService<WechatVideoPlatformProber>(),
            sp.GetRequiredService<BilibiliPlatformProber>(),
            sp.GetRequiredService<ToutiaoPlatformProber>(),
        ]));

        // ── Application 业务服务（单机版全量复用）──
        services.AddScoped<PlatformService>();
        services.AddScoped<PlatformSessionService>();
        services.AddScoped<PublishService>();
        services.AddScoped<CollectionService>();
        services.AddScoped<AsyncTaskService>();

        return services;
    }
}
