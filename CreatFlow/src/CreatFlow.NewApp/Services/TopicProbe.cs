using System.Text;
using System.Text.Json;
using CreatFlow.Platforms.Core;
using CreatFlow.Platforms.Topics;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 话题 / 活动真账号探针 —— <c>--probe-topics &lt;platform|all&gt;</c> 运行。
///
/// **诊断工具，不进业务路径**。用途：每平台把话题搜索 / 推荐 / 活动列表各拉一次，
/// 把原始结果写进 <c>%TEMP%\topic_probe.txt</c>，回答「这个平台的接口到底通不通、
/// 字段有没有变」。不发布、不上传、不改任何数据。
///
/// 前提：
///   · 服务端在跑（<c>scripts/dev.py --server-only</c> 或正常启动）；
///   · 本机已登录 CreatFlow 会员，且该平台至少绑定过一个账号；
///   · 小红书还需要 App 的签名宿主（WebView）—— 纯探针进程没有页面，会在报告里
///     如实写「签名不可用」而不是假装没结果。
///
/// 上传后才有上下文的平台（B站 upload_id / 头条 Vid / 快手 editSessionId）可用
/// <c>--upload-id=… --vid=… --edit-session=…</c> 传入；缺省时报告会写明「需要上传后上下文」。
/// </summary>
public static class TopicProbe
{
    public sealed class ProbeArgs
    {
        public string Platform { get; init; } = "all";
        public Guid? SessionId { get; init; }
        public string Keyword { get; init; } = "家居";
        public string? UploadId { get; init; }
        public string? VideoId { get; init; }
        public string? EditSessionId { get; init; }
    }

    public static async Task<int> RunAsync(ProbeArgs args)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"═══ 话题 / 活动探针 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ═══");
        sb.AppendLine($"平台={args.Platform}  关键词={args.Keyword}  "
                      + $"upload_id={args.UploadId ?? "-"}  vid={args.VideoId ?? "-"}  "
                      + $"editSessionId={args.EditSessionId ?? "-"}");

        var platforms = string.Equals(args.Platform, "all", StringComparison.OrdinalIgnoreCase)
            ? PlatformNames.All.ToList()
            : [args.Platform];
        var unknown = platforms.Where(p => !PlatformNames.All.Contains(p)).ToList();
        if (unknown.Count > 0)
        {
            sb.AppendLine($"未知平台：{string.Join(", ", unknown)}（可用：all,{string.Join(",", PlatformNames.All)}）");
            return Write(sb, 2);
        }

        var sessionCtx = new SessionContext();
        if (sessionCtx.MemberId == Guid.Empty)
        {
            sb.AppendLine("未登录 CreatFlow —— 先正常启动软件登录会员，再运行探针");
            return Write(sb, 3);
        }

        var store = new PlatformAccountStore(sessionCtx);
        var local = store.Load();
        var anyOk = false;

        foreach (var platform in platforms)
        {
            sb.AppendLine();
            sb.AppendLine($"──── {platform}（{PlatformNames.GetDisplayName(platform)}）────");

            var sessions = local.Where(r => r.PlatformKey == platform).ToList();
            var chosen = args.SessionId is { } want
                ? sessions.FirstOrDefault(s => s.Id == want)
                : sessions.FirstOrDefault();
            if (chosen is null && args.SessionId is { } explicitId)
            {
                // 本地镜像里没有也允许显式指定（跨设备恢复后的档案可能还没登记本地）
                var creds0 = await PlatformSessionApi.GetCredentialsAsync(explicitId);
                if (creds0 is not null && creds0.PlatformKey == platform)
                {
                    await ProbeOneAsync(sb, platform, explicitId, creds0, args);
                    continue;
                }
            }
            if (chosen is null)
            {
                sb.AppendLine($"  [SKIP] 本地没有该平台的账号（sessions.json）—— 先绑定/登录一次");
                continue;
            }

            PlatformSessionCredentials? creds;
            try
            {
                creds = await PlatformSessionApi.GetCredentialsAsync(chosen.Id);
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  [SKIP] 取凭证异常（服务端在跑吗？）：{ex.Message}");
                continue;
            }
            if (creds?.Cookie is null)
            {
                sb.AppendLine($"  [SKIP] 取凭证失败或 Cookie 为空（服务端在跑吗？）：{chosen.Id}");
                continue;
            }

            await ProbeOneAsync(sb, platform, chosen.Id, creds, args);
            anyOk = true;
        }

        return Write(sb, anyOk ? 0 : 1);
    }

    private static async Task ProbeOneAsync(
        StringBuilder sb, string platform, Guid sessionId,
        PlatformSessionCredentials creds, ProbeArgs args)
    {
        var options = new PlatformOptions
        {
            Environment = ParseEnv(creds.ExtraDataJson),
            SessionId = sessionId,
        };
        var credentials = new PlatformCredentials(creds.Cookie ?? "", creds.ExtraDataJson);
        var topic = TopicServiceCatalog.CreateTopicService(platform, options);
        var activity = TopicServiceCatalog.CreateActivityService(platform, options);
        var cap = topic.Capability;

        sb.AppendLine($"  会话 {sessionId}  账号 {AccountLabelOf(creds)}");
        sb.AppendLine($"  能力 search={cap.SupportsSearch} recommend={cap.SupportsRecommend} "
                      + $"activities={cap.SupportsActivities} recommendNeedsUpload={cap.RecommendNeedsUpload}");
        if (cap.NoInterfaceReason is { Length: > 0 } reason)
            sb.AppendLine($"  说明：{reason}");

        var query = new TopicQuery
        {
            Keyword = args.Keyword,
            Title = args.Keyword,
            UploadId = args.UploadId,
            TrackId = null,
            VideoId = args.VideoId,
            EditSessionId = args.EditSessionId,
            Count = 20,
        };

        if (cap.SupportsSearch)
        {
            var r = await SafeAsync(() => topic.SearchAsync(credentials, query, CancellationToken.None));
            DumpTopics(sb, "搜索", r);
        }

        if (cap.SupportsRecommend)
        {
            var r = await SafeAsync(() => topic.RecommendAsync(credentials, query, CancellationToken.None));
            DumpTopics(sb, "推荐", r);
        }

        var tagSvc = TopicServiceCatalog.CreateTagService(platform, options);
        if (tagSvc is not null)
        {
            var r = await SafeAsync(() => tagSvc.SuggestTagsAsync(credentials, query, CancellationToken.None));
            DumpTopics(sb, "标签推荐", r);
        }

        if (activity.Supported)
        {
            var r = await SafeAsync(() => activity.ListAsync(
                credentials, new ActivityQuery
                {
                    Count = 20,
                    Title = args.Keyword,
                    UploadId = args.UploadId,
                }, CancellationToken.None));
            if (r.Ok)
            {
                sb.AppendLine($"  [活动] {r.Items.Count} 条");
                foreach (var a in r.Items.Take(20))
                {
                    sb.AppendLine($"    · {a.Id}  {a.Name}  奖励={a.Reward ?? "-"}  "
                                  + $"截止={a.EndTime:yyyy-MM-dd HH:mm}  已结束={a.IsEnded}  "
                                  + $"话题={a.Topics.Count}"
                                  + (a.Topics.Count > 0
                                      ? " [" + string.Join(" / ", a.Topics.Select(t =>
                                          $"{t.Name}={t.PlatformId ?? "(无)"}")) + "]"
                                      : ""));
                }
            }
            else sb.AppendLine($"  [活动][失败] {r.Error}");

            var cats = await SafeAsync(() => activity.GetCategoriesAsync(credentials, CancellationToken.None));
            if (cats is not null)
                sb.AppendLine($"  [活动分类] {cats.Count} 个："
                              + string.Join(" / ", cats.Take(12).Select(c => c.Name)));
        }
        else sb.AppendLine("  [活动] 该平台不支持");

        // 抖音额外探一次「活动中心」列表（与 media/activity/get 是两套字段）
        if (platform == PlatformNames.Douyin && topic is DouyinTopicApi douyin)
        {
            var r = await SafeAsync(() => douyin.ListActivityCenterAsync(
                credentials, new ActivityQuery(), CancellationToken.None));
            if (r.Ok) sb.AppendLine($"  [活动中心] {r.Items.Count} 条（show_name 口径）");
            else sb.AppendLine($"  [活动中心][失败] {r.Error}");
        }

        // 快手额外拉筛选字典
        if (platform == PlatformNames.Kuaishou && topic is KuaishouTopicApi ks)
        {
            var f = await SafeAsync(() => ks.GetFiltersAsync(credentials, CancellationToken.None));
            if (f is not null)
                sb.AppendLine($"  [活动筛选] 分类 {f.Categories.Count} / 奖励 {f.RewardTypes.Count} "
                              + $"/ 排序 {f.SortTypes.Count} / 类型 {f.TagTypes.Count}");
        }

        static string AccountLabelOf(PlatformSessionCredentials c) =>
            c.PlatformName ?? c.PlatformUid ?? "?";
    }

    private static void DumpTopics(StringBuilder sb, string label, TopicFetchResult? r)
    {
        if (r is null)
        {
            sb.AppendLine($"  [{label}][失败] 调用异常（详见日志）");
            return;
        }
        if (!r.Ok)
        {
            sb.AppendLine($"  [{label}][失败] {r.Error}");
            return;
        }
        sb.AppendLine($"  [{label}] {r.Items.Count} 条");
        foreach (var t in r.Items.Take(15))
        {
            sb.AppendLine($"    · {t.Name}  id={t.PlatformId ?? "(无)"}  "
                          + $"浏览={t.ViewCount}  discuss={t.DiscussText ?? "-"}  "
                          + $"mission={t.MissionId}");
        }
    }

    /// <summary>探针里任何异常都只记录、不中断（一个平台失败不影响下一个）。</summary>
    private static async Task<T?> SafeAsync<T>(Func<Task<T>> call) where T : class
    {
        try { return await call(); }
        catch (Exception ex)
        {
            Log.Warning(ex, "[TopicProbe] 调用失败");
            return null;
        }
    }

    private static int Write(StringBuilder sb, int exitCode)
    {
        var path = Path.Combine(Path.GetTempPath(), "topic_probe.txt");
        try { File.WriteAllText(path, sb.ToString()); } catch { /* 报告落盘失败不影响退出码 */ }
        Console.WriteLine(sb.ToString());
        Console.WriteLine($"报告：{path}");
        Log.CloseAndFlush();
        return exitCode;
    }

    /// <summary>extra_data_json 里的 env 快照（与 PublishResolver.ParseEnv 同口径）。</summary>
    private static PlatformEnvironmentSnapshot? ParseEnv(string? extraDataJson)
    {
        if (string.IsNullOrWhiteSpace(extraDataJson)) return null;
        try
        {
            var extra = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(extraDataJson);
            if (extra is null || !extra.TryGetValue("env", out var raw)) return null;
            var json = raw.ToString();
            if (string.IsNullOrWhiteSpace(json)) return null;
            var snap = JsonSerializer.Deserialize<PlatformEnvironmentSnapshot>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return snap is { IsValid: true } ? snap : null;
        }
        catch { return null; }
    }
}
