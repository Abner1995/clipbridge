using System.Text;
using Avalonia.Controls;
using Avalonia.Platform;
using CreatFlow.Platforms.Core;
using CreatFlow.Platforms.Kuaishou;
using CreatFlow.Platforms.Xiaohongshu;
using CreatFlow.Platforms.WechatVideo;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 真账号删除探针 —— <c>--probe-delete "douyin:&lt;id&gt;,kuaishou:&lt;id&gt;,…"</c> 运行。
/// **诊断工具，不进业务路径**：验收/核对留下的测试作品用它清理，平台侧删除后再不碰本地归档。
///
/// 走的是与「视频管理」页**同一条**平台删除路径（<see cref="VideoArchiveDeleteService"/>）：
/// 抖音 / 快手 / 小红书 / 头条 / 视频号可删；**B站要 Geetest 验证** → 只回报「不支持」。
///
/// 视频号的归档 ID 是 objectId，但 `post_create` 不回它 —— 用 <c>wechatvideo:clip:&lt;clipKey&gt;</c>
/// 让探针先走 <c>post_list</c> 反查 objectId（clipKey 见发布日志 `clip_video OK: clipKey=…`）。
///
/// 快手/小红书要页面签名宿主 —— 探针按需各开一个隐藏 WebView（该账号档案）。
/// 报告写 <c>%TEMP%\delete_probe.txt</c>。
/// </summary>
public static class DeleteProbe
{
    public static async Task<int> RunAsync(string spec, Guid? sessionId)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"═══ 真账号删除探针 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ═══");
        sb.AppendLine($"spec={spec}  session={sessionId?.ToString() ?? "-"}");

        var ctx = new SessionContext();
        if (ctx.MemberId == Guid.Empty)
        {
            sb.AppendLine("未登录 CreatFlow —— 先正常启动软件登录会员，再运行探针");
            return Write(sb, 3);
        }

        var store = new PlatformAccountStore(ctx);
        var sessions = store.Load();

        // `platform:videoId` 逗号分隔；视频号支持 `wechatvideo:clip:<clipKey>` 反查 objectId
        var items = spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part =>
            {
                var idx = part.IndexOf(':');
                return idx <= 0
                    ? (Platform: "", VideoId: "")
                    : (Platform: part[..idx].Trim(), VideoId: part[(idx + 1)..].Trim());
            })
            .Where(x => x.Platform.Length > 0 && x.VideoId.Length > 0)
            .ToList();

        if (items.Count == 0)
        {
            sb.AppendLine("spec 解析为空（格式：douyin:7706…,kuaishou:3xss…）");
            return Write(sb, 4);
        }

        var hosts = new List<(Window Window, NativeWebView View, string PlatformKey, Guid SessionId)>();
        try
        {
            var targets = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            foreach (var platform in items.Select(i => i.Platform).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var candidates = sessions.Where(s => s.PlatformKey == platform).ToList();
                var chosen = (sessionId is { } want ? candidates.FirstOrDefault(s => s.Id == want) : null)
                             ?? candidates.FirstOrDefault();
                if (chosen is null)
                {
                    sb.AppendLine($"  [{platform}] 没有绑定账号 —— 跳过");
                    continue;
                }
                targets[platform] = chosen.Id;
                sb.AppendLine($"  [{platform}] 账号 {chosen.PlatformName ?? chosen.PlatformUid}（{chosen.Id}）");
            }

            // 页面签名宿主（快手 sig3 / 小红书 x-s）
            foreach (var platform in targets.Keys)
            {
                string? url = platform switch
                {
                    PlatformNames.Kuaishou => "https://cp.kuaishou.com/article/manage/video",
                    PlatformNames.Xiaohongshu => "https://creator.xiaohongshu.com/publish/publish?source=official",
                    _ => null,
                };
                if (url is null) continue;
                hosts.Add(OpenHost(store, platform, targets[platform], url));
            }

            XhsPlatform.SetSigner((urlPath, bodyJson, sid, ct) =>
                XhsSignerService.SignAsync(sid, urlPath, bodyJson, ct));
            KuaishouPlatform.SetSigner((sid, reqJson, ct) =>
                KuaishouSignerService.SignAsync(sid, reqJson, ct));

            foreach (var host in hosts)
            {
                switch (host.PlatformKey)
                {
                    case PlatformNames.Kuaishou:
                        KuaishouWebCookies.Register(host.SessionId, new KuaishouCookieHost(host.View));
                        KuaishouSignerService.Register(host.SessionId, new KuaishouPageSigner(host.View));
                        break;
                    case PlatformNames.Xiaohongshu:
                        XhsSignerService.Register(host.SessionId, new XhsPageSigner(host.View));
                        break;
                }
                sb.AppendLine($"  页面宿主已注册：{host.PlatformKey}");
            }

            sb.AppendLine();
            sb.AppendLine("── 删除结果 ──");
            var okCount = 0;
            foreach (var (platform, rawId) in items)
            {
                if (!targets.TryGetValue(platform, out var sid))
                {
                    sb.AppendLine($"  {platform}:{rawId} → 跳过（无账号）");
                    continue;
                }

                var videoId = rawId;
                if (platform == PlatformNames.WechatVideo
                    && videoId.StartsWith("clip:", StringComparison.OrdinalIgnoreCase))
                {
                    // `clip:<clipKey>[=<描述里的关键词>]` —— 列表不带 clipKey 时按描述兜底匹配
                    var rest = videoId["clip:".Length..];
                    var hint = "";
                    var eq = rest.IndexOf('=');
                    if (eq > 0)
                    {
                        hint = rest[(eq + 1)..];
                        rest = rest[..eq];
                    }
                    videoId = await ResolveWechatObjectIdAsync(sb, sid, rest, hint);
                    if (videoId.Length == 0) continue;
                }

                var result = await VideoArchiveDeleteService.DeleteOnPlatformAsync(
                    platform, sid, videoId);
                if (result is PlatformDeleteResult.Success or PlatformDeleteResult.NotFound)
                    okCount++;
                sb.AppendLine($"  {platform}:{videoId} → {Describe(result)}");
            }

            sb.AppendLine();
            sb.AppendLine($"  合计：成功/已不存在 {okCount} / {items.Count}");
            return Write(sb, okCount == items.Count ? 0 : 1);
        }
        catch (Exception ex)
        {
            sb.AppendLine($"[失败] {ex.Message}");
            Log.Warning(ex, "[DeleteProbe] 删除探针异常");
            return Write(sb, 1);
        }
        finally
        {
            foreach (var host in hosts)
            {
                DouyinWebExecutor.Unregister(host.SessionId);
                KuaishouWebCookies.Unregister(host.SessionId);
                KuaishouSignerService.Unregister(host.SessionId);
                XhsSignerService.Unregister(host.SessionId);
            }
            foreach (var host in hosts)
            {
                try { host.Window.Close(); } catch { }
            }
        }
    }

    private static async Task<string> ResolveWechatObjectIdAsync(
        StringBuilder sb, Guid sessionId, string clipKey, string descriptionHint)
    {
        try
        {
            var creds = await PlatformSessionApi.GetCredentialsAsync(sessionId);
            if (creds?.Cookie is null)
            {
                sb.AppendLine($"  wechatvideo:clip:{clipKey} → 取凭证失败");
                return "";
            }

            var extra = CredentialFields.Extra(creds.ExtraDataJson);
            var deviceId = extra.GetValueOrDefault("deviceId")?.ToString()
                ?? extra.GetValueOrDefault("device_id")?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(deviceId))
                deviceId = Guid.NewGuid().ToString("N")[..32];

            var finderId = CredentialFields.LocalStorageField(
                creds.LocalStorageJson, "finder_username") ?? "";
            var options = new PlatformOptions
            {
                Environment = CredentialFields.EnvironmentSnapshot(creds.ExtraDataJson),
                SessionId = sessionId,
            };
            var svc = new WechatVideoContentService(options) { FinderId = finderId };
            var objectId = await svc.FindObjectIdByClipKeyAsync(creds.Cookie, deviceId, clipKey);

            // 兜底：post_list 的条目可能不带 clipKey（新作品没回填）—— 按描述关键词匹配最新一条
            if (string.IsNullOrWhiteSpace(objectId) && !string.IsNullOrWhiteSpace(descriptionHint))
            {
                var list = await svc.ListAsync(creds.Cookie, deviceId, page: 1, pageSize: 50);
                sb.AppendLine($"  [wechat post_list] total={list?.TotalCount} items={list?.Items?.Count ?? 0}"
                              + (list?.Items is { Count: > 0 }
                                  ? "：" + string.Join(" | ", list.Items.Take(5).Select(i =>
                                      $"{i.ObjectId}:{Truncate(i.Description ?? "", 20)}"
                                      + $"@{DateTimeOffset.FromUnixTimeSeconds(i.CreateTime).ToLocalTime():MM-dd HH:mm}"))
                                  : ""));
                var hit = list?.Items?
                    .Where(i => !string.IsNullOrWhiteSpace(i.ObjectId)
                        && (i.Description?.Contains(descriptionHint, StringComparison.Ordinal) ?? false))
                    .OrderByDescending(i => i.CreateTime)
                    .FirstOrDefault();
                if (hit?.ObjectId is { Length: > 0 } matched)
                {
                    objectId = matched;
                    sb.AppendLine($"  wechatvideo:clip:{clipKey} → 按描述「{descriptionHint}」匹配到 objectId={matched}");
                }
            }

            if (string.IsNullOrWhiteSpace(objectId))
            {
                sb.AppendLine($"  wechatvideo:clip:{clipKey} → post_list 没找到 objectId（稍后再试）");
                return "";
            }
            sb.AppendLine($"  wechatvideo:clip:{clipKey} → objectId={objectId}");
            return objectId;
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  wechatvideo:clip:{clipKey} → 反查异常：{ex.Message}");
            return "";
        }
    }

    private static string Truncate(string text, int max) =>
        text.Length > max ? text[..max] + "…" : text;

    private static string Describe(PlatformDeleteResult result) => result switch
    {
        PlatformDeleteResult.Success => "已删除",
        PlatformDeleteResult.NotFound => "平台已不存在",
        PlatformDeleteResult.Unauthorized => "凭证失效（需重新登录后重试）",
        PlatformDeleteResult.SessionGone => "会话已解绑",
        _ => "失败（可能需人工处理）",
    };

    private static (Window, NativeWebView, string, Guid) OpenHost(
        PlatformAccountStore store, string platformKey, Guid sessionId, string url)
    {
        var window = new Window
        {
            Title = $"删除探针 — {platformKey}",
            Width = 900,
            Height = 700,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowInTaskbar = false,
        };
        var view = new NativeWebView();
        window.Content = view;
        var profileDir = store.ProfileDir(sessionId);
        view.EnvironmentRequested += (_, args) =>
        {
            if (args is WindowsWebView2EnvironmentRequestedEventArgs wv)
            {
                wv.UserDataFolder = profileDir;
                wv.ProfileName = "main";
            }
        };
        window.Show();
        view.Navigate(new Uri(url));
        return (window, view, platformKey, sessionId);
    }

    private static int Write(StringBuilder sb, int exitCode)
    {
        var path = Path.Combine(Path.GetTempPath(), "delete_probe.txt");
        try { File.WriteAllText(path, sb.ToString()); } catch { /* 报告落盘失败不影响退出码 */ }
        Console.WriteLine(sb.ToString());
        Console.WriteLine($"报告：{path}");
        Log.CloseAndFlush();
        return exitCode;
    }
}
