using System.Text;
using Avalonia.Controls;
using Avalonia.Platform;
using CreatFlow.NewApp.Models;
using CreatFlow.Platforms.Core;
using CreatFlow.Platforms.Kuaishou;
using CreatFlow.Platforms.Xiaohongshu;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 真账号发布探针 —— <c>--probe-publish &lt;发布单元目录&gt;</c> 运行。**诊断工具，不进业务路径**。
///
/// 用途：阶段 7「字段落地核对」需要一批**提交字段已知**的新作品（2026-11-09 那批已被清理）。
/// 本探针把一份发布单元（mp4 + params.txt + cover.jpg）走一遍与自动发布**同一份**组装逻辑
/// （<see cref="PublishUnitSubmitter.BuildRequestAsync"/>），然后 <see cref="PublishPipeline.RunAsync"/>
/// 提交并立即执行 —— 等价于发布页「导入文件夹 → 提交 → 上传队列开跑」，但不经 UI。
///
/// 需要页面宿主的三个平台（抖音 bd_ticket_guard / 快手 sig3 / 小红书 x-s）由本探针各开一个
/// 隐藏 WebView（该账号档案）并注册宿主，与 DockTabHostView 的注册口径一致。
///
/// 用法：
///   <c>--probe-publish D:\path\to\unit</c>（六平台全发）
///   <c>--probe-publish D:\path\to\unit --platforms douyin,bilibili</c>
/// 可选 <c>--session &lt;guid&gt;</c>（只对匹配到的那一个平台生效，其余平台仍取第一个账号）；
/// <c>--toutiao-draft</c> 把头条目标改成 <c>PublishType=0</c>（存草稿），用于验证
/// 「视频生成图文 = 仅保存草稿」；<c>--wechat-event 名称|创建者昵称|活动ID</c> 给视频号目标注入
/// 官方活动三件套（探针没有活动选择器）；<c>--kuaishou-mention 昵称|userId</c> 给快手目标注入
/// @好友（userId 用 <c>--probe-kuaishou-work --mention 关键词</c> 搜到）—— 均为诊断专用，
/// 正常发布路径不传。
/// 前提：服务端在跑、本机已登录会员、各平台账号已登录（档案里有 Cookie）。
/// 报告写 <c>%TEMP%\publish_probe.txt</c>。
/// </summary>
public static class PublishProbe
{
    private sealed record HostWindow(Window Window, NativeWebView View, string PlatformKey, Guid SessionId);

    public static async Task<int> RunAsync(
        string unitDir, string? platformsCsv, Guid? sessionId, bool toutiaoDraft = false,
        string? wechatEvent = null, string? kuaishouMention = null,
        string? xhsGroup = null, string? xhsPkCover = null,
        IReadOnlyList<string>? xhsMarkers = null, string? xhsRoute = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"═══ 真账号发布探针 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ═══");
        sb.AppendLine($"unit={unitDir}  platforms={platformsCsv ?? "(全部)"}  session={sessionId?.ToString() ?? "-"}"
                      + (toutiaoDraft ? "  toutiao-draft=1" : "")
                      + (wechatEvent is { Length: > 0 } ? $"  wechat-event={wechatEvent}" : "")
                      + (kuaishouMention is { Length: > 0 } ? $"  kuaishou-mention={kuaishouMention}" : ""));

        var ctx = new SessionContext();
        if (ctx.MemberId == Guid.Empty)
        {
            sb.AppendLine("未登录 CreatFlow —— 先正常启动软件登录会员，再运行探针");
            return Write(sb, 3);
        }

        if (!Directory.Exists(unitDir))
        {
            sb.AppendLine($"目录不存在：{unitDir}");
            return Write(sb, 4);
        }

        var videoPath = Directory.GetFiles(unitDir, "*.mp4", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (videoPath is null)
        {
            sb.AppendLine("目录里没有 mp4 文件");
            return Write(sb, 4);
        }

        // ── 目标账号：每个平台取第一个（--session 指定的那个优先） ──
        var store = new PlatformAccountStore(ctx);
        var sessions = store.Load();
        var wanted = (platformsCsv ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var order = new[]
        {
            PlatformNames.Douyin, PlatformNames.Kuaishou, PlatformNames.Xiaohongshu,
            PlatformNames.WechatVideo, PlatformNames.Bilibili, PlatformNames.Toutiao,
        };

        var targets = new List<AutoPublishTarget>();
        foreach (var key in order)
        {
            if (wanted.Count > 0 && !wanted.Contains(key)) continue;

            var candidates = sessions.Where(s => s.PlatformKey == key).ToList();
            var chosen = (sessionId is { } want ? candidates.FirstOrDefault(s => s.Id == want) : null)
                         ?? candidates.FirstOrDefault();
            if (chosen is null)
            {
                sb.AppendLine($"  [{key}] 没有绑定账号 —— 跳过");
                continue;
            }
            targets.Add(new AutoPublishTarget(key, chosen.Id, chosen.PlatformName ?? chosen.PlatformUid ?? "?"));
            sb.AppendLine($"  [{key}] 账号 {chosen.PlatformName ?? chosen.PlatformUid}（{chosen.Id}）");
        }

        if (targets.Count == 0)
        {
            sb.AppendLine("没有任何可发布的目标账号");
            return Write(sb, 5);
        }

        // ── 页面宿主（只在需要的平台开隐藏 WebView） ──
        var hosts = new List<HostWindow>();
        try
        {
            OpenHostIfNeeded(hosts, targets, store, PlatformNames.Douyin,
                DouyinWebExecutor.PublishPageUrl);
            OpenHostIfNeeded(hosts, targets, store, PlatformNames.Kuaishou,
                "https://cp.kuaishou.com/article/publish/video");
            OpenHostIfNeeded(hosts, targets, store, PlatformNames.Xiaohongshu,
                "https://creator.xiaohongshu.com/publish/publish?source=official");

            // 全局签名委托（与 App.axaml.cs 同款接线）
            XhsPlatform.SetSigner((urlPath, bodyJson, sid, ct) =>
                XhsSignerService.SignAsync(sid, urlPath, bodyJson, ct));
            KuaishouPlatform.SetSigner((sid, reqJson, ct) =>
                KuaishouSignerService.SignAsync(sid, reqJson, ct));

            foreach (var host in hosts)
            {
                switch (host.PlatformKey)
                {
                    case PlatformNames.Douyin:
                        DouyinWebExecutor.Register(host.SessionId, new DouyinPageExecutor(host.View));
                        break;
                    case PlatformNames.Kuaishou:
                        KuaishouWebCookies.Register(host.SessionId, new KuaishouCookieHost(host.View));
                        KuaishouSignerService.Register(host.SessionId, new KuaishouPageSigner(host.View));
                        break;
                    case PlatformNames.Xiaohongshu:
                        XhsSignerService.Register(host.SessionId, new XhsPageSigner(host.View));
                        break;
                }
                sb.AppendLine($"  页面宿主已注册：{host.PlatformKey}（{host.SessionId}）");
            }

            // ── 组装（与自动发布同一份逻辑） ──
            var store2 = new PublishDraftStore(memberId: () => ctx.MemberId);
            var submitter = new PublishUnitSubmitter(store2);
            var (built, error) = await submitter.BuildRequestAsync(unitDir, videoPath, targets);
            if (built is null)
            {
                sb.AppendLine($"[组装失败] {error}");
                return Write(sb, 6);
            }
            if (built.Trouble.Count > 0)
                sb.AppendLine($"  非致命提示：{string.Join("；", built.Trouble.Distinct())}");

            if (toutiaoDraft)
            {
                if (!TryForceToutiaoDraft(built.Request, out var draftError))
                {
                    sb.AppendLine($"[组装失败] {draftError}");
                    return Write(sb, 6);
                }
                sb.AppendLine("  头条目标已改为 PublishType=0（存草稿）—— 不发公开作品");
            }

            if (wechatEvent is { Length: > 0 })
            {
                if (!TryForceWechatEvent(built.Request, wechatEvent, out var eventError))
                {
                    sb.AppendLine($"[组装失败] {eventError}");
                    return Write(sb, 6);
                }
                sb.AppendLine($"  视频号活动已注入 objectDesc.event：{wechatEvent}");
            }

            if (kuaishouMention is { Length: > 0 })
            {
                if (!TryForceKuaishouMention(built.Request, kuaishouMention, out var mentionError))
                {
                    sb.AppendLine($"[组装失败] {mentionError}");
                    return Write(sb, 6);
                }
                sb.AppendLine($"  快手 @好友已注入 MentionUsers：{kuaishouMention}");
            }

            if (xhsGroup is { Length: > 0 })
            {
                if (!TryForceXhsGroup(built.Request, xhsGroup, out var groupError))
                {
                    sb.AppendLine($"[组装失败] {groupError}");
                    return Write(sb, 6);
                }
                sb.AppendLine($"  小红书群聊已注入 GroupBind：{xhsGroup}");
            }

            if (xhsPkCover is { Length: > 0 })
            {
                if (!TryForceXhsPkCover(built.Request, xhsPkCover, out var pkError))
                {
                    sb.AppendLine($"[组装失败] {pkError}");
                    return Write(sb, 6);
                }
                sb.AppendLine($"  小红书 PK 封面已注入：{xhsPkCover}");
            }

            if (xhsMarkers is { Count: > 0 })
            {
                if (!TryForceXhsMarkers(built.Request, xhsMarkers, out var markerError))
                {
                    sb.AppendLine($"[组装失败] {markerError}");
                    return Write(sb, 6);
                }
                sb.AppendLine($"  小红书视频标记已注入：{string.Join("；", xhsMarkers)}");
            }

            if (xhsRoute is { Length: > 0 })
            {
                if (!TryForceXhsRoute(built.Request, xhsRoute, out var routeError))
                {
                    sb.AppendLine($"[组装失败] {routeError}");
                    return Write(sb, 6);
                }
                sb.AppendLine($"  小红书路线已注入 Route：{xhsRoute}");
            }

            sb.AppendLine($"  组装完成：{built.Request.Targets.Count} 条平台记录，开始提交并执行…");

            // ── 提交 + 执行（等价于上传队列开跑） ──
            var results = new List<string>();
            var pipeline = new PublishPipeline { MetaProbe = new VideoMetaProbe() };
            pipeline.OnPlatformProgress = (key, stage, pct, text) =>
            {
                if (pct is 0 or 100 || stage is "publishing" or "submit_failed")
                    Log.Debug("[PublishProbe] {Platform} {Stage} {Pct}% {Text}", key, stage, pct, text);
            };
            pipeline.OnPlatformComplete = (key, ok, videoId, err) =>
            {
                results.Add($"{key} → {(ok ? "成功" : "失败")}  videoId={videoId ?? "-"}  err={err ?? "-"}");
                Log.Information("[PublishProbe] {Platform} 完成 success={Ok} videoId={VideoId} err={Err}",
                    key, ok, videoId, err);
            };

            await pipeline.RunAsync(built.Request);

            sb.AppendLine();
            sb.AppendLine("── 结果 ──");
            foreach (var line in results) sb.AppendLine("  " + line);
            var okCount = results.Count(r => r.Contains("成功"));
            sb.AppendLine($"  合计：成功 {okCount} / {results.Count}");
            return Write(sb, okCount == results.Count && results.Count > 0 ? 0 : 1);
        }
        catch (Exception ex)
        {
            sb.AppendLine($"[失败] {ex.Message}");
            Log.Warning(ex, "[PublishProbe] 发布探针异常");
            return Write(sb, 1);
        }
        finally
        {
            foreach (var host in hosts)
                Unregister(host.SessionId);
            foreach (var host in hosts)
            {
                try { host.Window.Close(); } catch { }
            }
        }
    }

    /// <summary>
    /// 诊断专用：把头条目标的载荷改成 <c>PublishType=0</c>（存草稿）。
    /// 只改提交请求里的 ExtraDataJson，不碰发布页/params 的业务字段。
    /// </summary>
    private static bool TryForceToutiaoDraft(SubmitPublishRequest request, out string? error)
    {
        error = null;
        var index = request.Targets.FindIndex(t => t.PlatformKey == PlatformNames.Toutiao);
        if (index < 0)
        {
            error = "没有头条目标 —— --toutiao-draft 需要 --platforms toutiao（或六平台全发）";
            return false;
        }

        var target = request.Targets[index];
        var payload = PublishPayload.Deserialize<ToutiaoPayload>(target.ExtraDataJson);
        if (payload is null)
        {
            error = "头条载荷解析失败，无法改成存草稿";
            return false;
        }

        request.Targets[index] = target with
        {
            ExtraDataJson = PublishPayload.Serialize(payload with { PublishType = 0 }),
        };
        return true;
    }

    /// <summary>
    /// 诊断专用：给视频号目标注入官方活动三件套 —— <c>--wechat-event 名称|创建者昵称|活动ID</c>。
    /// 正常发布路径中这三个值来自「官方活动」列表的选中项（<c>post/post_search_event</c> 快照），
    /// 探针没有 UI 选择器，故在组装完成后按同一份载荷形状补齐 ——
    /// 走的是与真实发布完全相同的 <see cref="WechatVideoPayload.Event"/> → 执行端映射 → wire。
    /// </summary>
    private static bool TryForceWechatEvent(
        SubmitPublishRequest request, string spec, out string? error)
    {
        error = null;
        var parts = spec.Split('|', StringSplitOptions.TrimEntries);
        if (parts.Length != 3 || parts.Any(p => p.Length == 0))
        {
            error = "--wechat-event 格式应为「名称|创建者昵称|活动ID」，三段都不能为空";
            return false;
        }

        var index = request.Targets.FindIndex(t => t.PlatformKey == PlatformNames.WechatVideo);
        if (index < 0)
        {
            error = "没有视频号目标 —— --wechat-event 需要 --platforms wechatvideo";
            return false;
        }

        var target = request.Targets[index];
        var payload = PublishPayload.Deserialize<WechatVideoPayload>(target.ExtraDataJson);
        if (payload is null)
        {
            error = "视频号载荷解析失败，无法注入活动";
            return false;
        }

        request.Targets[index] = target with
        {
            ExtraDataJson = PublishPayload.Serialize(payload with
            {
                Event = new WechatVideoEventRef(parts[2], parts[0], parts[1]),
            }),
        };
        return true;
    }

    /// <summary>
    /// 诊断专用：给快手目标注入 @好友 —— <c>--kuaishou-mention 昵称|userId</c>。
    /// 正常路径中 userId 来自候选面板（<c>at/search</c>）；探针没有选择器，
    /// 故在组装完成后按同一份载荷形状补齐 —— 走的是与真实发布完全相同的
    /// <see cref="KuaishouPayload.MentionUsers"/> → 执行端 at/choose + caption 文本 token。
    /// </summary>
    private static bool TryForceKuaishouMention(
        SubmitPublishRequest request, string spec, out string? error)
    {
        error = null;
        var parts = spec.Split('|', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || parts.Any(p => p.Length == 0)
            || !long.TryParse(parts[1], out var userId) || userId <= 0)
        {
            error = "--kuaishou-mention 格式应为「昵称|userId」";
            return false;
        }

        var index = request.Targets.FindIndex(t => t.PlatformKey == PlatformNames.Kuaishou);
        if (index < 0)
        {
            error = "没有快手目标 —— --kuaishou-mention 需要 --platforms kuaishou";
            return false;
        }

        var target = request.Targets[index];
        var payload = PublishPayload.Deserialize<KuaishouPayload>(target.ExtraDataJson);
        if (payload is null)
        {
            error = "快手载荷解析失败，无法注入 @好友";
            return false;
        }

        request.Targets[index] = target with
        {
            ExtraDataJson = PublishPayload.Serialize(payload with
            {
                MentionUsers = [new KuaishouMentionRef(userId, parts[0])],
            }),
        };
        return true;
    }

    /// <summary>
    /// 诊断专用：给小红书目标注入群聊 —— <c>--xhs-group 群ID|群名|描述|头像</c>。
    /// 正常路径中群聊来自「选择群聊」面板（<c>group_list</c>）；探针没有选择器。
    /// </summary>
    private static bool TryForceXhsGroup(
        SubmitPublishRequest request, string spec, out string? error)
    {
        error = null;
        var parts = spec.Split('|', StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0)
        {
            error = "--xhs-group 格式应为「群ID|群名|描述|头像」（后两段可空）";
            return false;
        }

        var index = request.Targets.FindIndex(t => t.PlatformKey == PlatformNames.Xiaohongshu);
        if (index < 0)
        {
            error = "没有小红书目标 —— --xhs-group 需要 --platforms xiaohongshu";
            return false;
        }

        var target = request.Targets[index];
        var payload = PublishPayload.Deserialize<XhsPayload>(target.ExtraDataJson);
        if (payload is null)
        {
            error = "小红书载荷解析失败，无法注入群聊";
            return false;
        }

        request.Targets[index] = target with
        {
            ExtraDataJson = PublishPayload.Serialize(payload with
            {
                GroupBind = new XhsGroupRef(
                    parts[0], parts[1],
                    parts.Length > 2 && parts[2].Length > 0 ? parts[2] : null,
                    parts.Length > 3 && parts[3].Length > 0 ? parts[3] : null),
            }),
        };
        return true;
    }

    /// <summary>
    /// 诊断专用：给小红书目标注入添加路线 —— <c>--xhs-route travelId|路线名|类型|段数</c>。
    /// 正常路径中路线先在编辑期 <c>save/travel</c> 建好（探针没有编辑器）。
    /// </summary>
    private static bool TryForceXhsRoute(
        SubmitPublishRequest request, string spec, out string? error)
    {
        error = null;
        var parts = spec.Split('|', StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0)
        {
            error = "--xhs-route 格式应为「travelId|路线名|类型(可空)|段数(可空)」";
            return false;
        }

        var index = request.Targets.FindIndex(t => t.PlatformKey == PlatformNames.Xiaohongshu);
        if (index < 0)
        {
            error = "没有小红书目标 —— --xhs-route 需要 --platforms xiaohongshu";
            return false;
        }

        var target = request.Targets[index];
        var payload = PublishPayload.Deserialize<XhsPayload>(target.ExtraDataJson);
        if (payload is null)
        {
            error = "小红书载荷解析失败，无法注入路线";
            return false;
        }

        var planAmount = parts.Length > 3 && int.TryParse(parts[3], out var plans) ? plans : 1;
        request.Targets[index] = target with
        {
            ExtraDataJson = PublishPayload.Serialize(payload with
            {
                Route = new XhsRouteRef(
                    parts[0],
                    parts[1],
                    parts.Length > 2 && parts[2].Length > 0 ? parts[2] : "singleRoute",
                    planAmount),
            }),
        };
        return true;
    }

    /// <summary>
    /// 诊断专用：给小红书目标注入 PK 封面附加图 —— <c>--xhs-pk-cover 路径1;路径2</c>。
    /// 执行端会逐张上传（同正常路径）。
    /// </summary>
    private static bool TryForceXhsPkCover(
        SubmitPublishRequest request, string spec, out string? error)
    {
        error = null;
        var paths = spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (paths.Length == 0)
        {
            error = "--xhs-pk-cover 至少要给一个图片路径";
            return false;
        }

        var index = request.Targets.FindIndex(t => t.PlatformKey == PlatformNames.Xiaohongshu);
        if (index < 0)
        {
            error = "没有小红书目标 —— --xhs-pk-cover 需要 --platforms xiaohongshu";
            return false;
        }

        var target = request.Targets[index];
        var payload = PublishPayload.Deserialize<XhsPayload>(target.ExtraDataJson);
        if (payload is null)
        {
            error = "小红书载荷解析失败，无法注入 PK 封面";
            return false;
        }

        request.Targets[index] = target with
        {
            ExtraDataJson = PublishPayload.Serialize(payload with
            {
                PkCoverEnabled = true,
                PkCoverImages = [.. paths],
            }),
        };
        return true;
    }

    /// <summary>
    /// 诊断专用：给小红书目标注入视频标记 —— <c>--xhs-marker 类型|ID|名称|起|止</c>（可重复）。
    /// 类型 = <c>user</c> / <c>location</c>；起止是秒。
    /// </summary>
    private static bool TryForceXhsMarkers(
        SubmitPublishRequest request, IReadOnlyList<string> specs, out string? error)
    {
        error = null;
        var markers = new List<XhsVideoMarker>();
        foreach (var spec in specs)
        {
            var parts = spec.Split('|', StringSplitOptions.TrimEntries);
            if (parts.Length != 5 || parts[0].Length == 0 || parts[1].Length == 0
                || parts[2].Length == 0
                || !long.TryParse(parts[3], out var start)
                || !long.TryParse(parts[4], out var end))
            {
                error = "--xhs-marker 格式应为「类型|ID|名称|起始秒|结束秒」，类型 = user / location";
                return false;
            }

            var type = parts[0].ToLowerInvariant();
            if (type is not ("user" or "location"))
            {
                error = "--xhs-marker 类型只能是 user（朋友）或 location（地点）";
                return false;
            }

            markers.Add(new XhsVideoMarker
            {
                Id = parts[1],
                Name = parts[2],
                Type = type,
                StartSeconds = Math.Max(0, start),
                EndSeconds = Math.Max(0, end),
            });
        }

        var index = request.Targets.FindIndex(t => t.PlatformKey == PlatformNames.Xiaohongshu);
        if (index < 0)
        {
            error = "没有小红书目标 —— --xhs-marker 需要 --platforms xiaohongshu";
            return false;
        }

        var target = request.Targets[index];
        var payload = PublishPayload.Deserialize<XhsPayload>(target.ExtraDataJson);
        if (payload is null)
        {
            error = "小红书载荷解析失败，无法注入视频标记";
            return false;
        }

        request.Targets[index] = target with
        {
            ExtraDataJson = PublishPayload.Serialize(payload with { Markers = markers }),
        };
        return true;
    }

    private static void Unregister(Guid sessionId)
    {
        DouyinWebExecutor.Unregister(sessionId);
        KuaishouWebCookies.Unregister(sessionId);
        KuaishouSignerService.Unregister(sessionId);
        XhsSignerService.Unregister(sessionId);
    }

    /// <summary>该平台在目标里才开隐藏 WebView（各账号档案独立，别共用）。</summary>
    private static void OpenHostIfNeeded(
        List<HostWindow> hosts, List<AutoPublishTarget> targets,
        PlatformAccountStore store, string platformKey, string url)
    {
        var target = targets.FirstOrDefault(t => t.PlatformKey == platformKey);
        if (target is null) return;

        var window = new Window
        {
            Title = $"发布探针 — {platformKey}",
            Width = 900,
            Height = 700,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowInTaskbar = false,
        };
        var view = new NativeWebView();
        window.Content = view;
        var profileDir = store.ProfileDir(target.SessionId);
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
        hosts.Add(new HostWindow(window, view, platformKey, target.SessionId));
    }

    private static int Write(StringBuilder sb, int exitCode)
    {
        var path = Path.Combine(Path.GetTempPath(), "publish_probe.txt");
        try { File.WriteAllText(path, sb.ToString()); } catch { /* 报告落盘失败不影响退出码 */ }
        Console.WriteLine(sb.ToString());
        Console.WriteLine($"报告：{path}");
        Log.CloseAndFlush();
        return exitCode;
    }
}
