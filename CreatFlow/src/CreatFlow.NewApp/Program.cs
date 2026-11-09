using Avalonia;
using Serilog;
using Serilog.Events;

namespace CreatFlow.NewApp;

internal sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // --smoke：自动化冒烟（P0+ 每阶段回归用）—— 窗口正常渲染 + 深浅色切换后自动退出，
        // 结果写 %TEMP%/newapp_smoke.txt；--smoke-login 变体：冒烟登录窗（视觉自检用）
        App.SmokeMode = args.Contains("--smoke") || args.Contains("--smoke-login");
        App.SmokeLoginWindow = args.Contains("--smoke-login");

        // --probe-douyin <url|id>：抖音「无水印源」探针（诊断工具，不进业务路径）——
        // 借已登录的 WebView2 档案在页面上下文里 fetch aweme/detail，把候选播放地址列全。
        // 结果写 %TEMP%\douyin_probe.json。桌面端必须先退出（档案目录不能同时被占用）。
        var probeIndex = Array.IndexOf(args, "--probe-douyin");
        if (probeIndex >= 0 && probeIndex + 1 < args.Length)
            App.ProbeDouyinTarget = args[probeIndex + 1];
        else
        {
            var inline = args.FirstOrDefault(a => a.StartsWith("--probe-douyin=", StringComparison.Ordinal));
            if (inline is not null) App.ProbeDouyinTarget = inline["--probe-douyin=".Length..];
        }

        var sessionIndex = Array.IndexOf(args, "--session");
        if (sessionIndex >= 0 && sessionIndex + 1 < args.Length
            && Guid.TryParse(args[sessionIndex + 1], out var probeSession))
            App.ProbeSessionId = probeSession;

        // ── 文件日志（与老 App 同目录、不同文件名，双存期不混写）──
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CreatFlow", "logs");
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                path: Path.Combine(logDir, "log-newapp-.log"),
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: 3 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                // 数上限只是**防日志风暴的硬闸**，保留策略由用户设置（log-settings.json）
                // 经 LogCleanup 决定 —— 原来写 31 会在忙碌的周里先于「保留 30 天」到达
                // （1~2 个文件/天），用户看到的是「设了 30 天实际只剩 ~20 天」。
                // 200 × 典型 0.4MB ≈ 80MB，只在日志风暴（异常刷屏）时才有机会走到。
                retainedFileCountLimit: 200,
                outputTemplate:
                "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
#if DEBUG
            .WriteTo.Console(
                outputTemplate:
                "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
#endif
            .CreateLogger();

        // ── 全局崩溃兜底（2026-11-04 补）──
        // WebView2 的焦点恢复异常是从 WndProc（原生回调）里冒出去的，进程被 CLR 直接终结，
        // 此前既没有 crashes 文件、Serilog 也没留下任何一行（用户实机两次崩溃都查无记录）。
        // 这里只做「记下来 + 落盘」，不改崩溃语义。
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            Log.Fatal(ex, "未处理异常，进程即将退出");
            WriteCrashFile(ex);
            Log.CloseAndFlush();
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            // 观察掉，别让后台任务的异常在 GC 时升级成进程崩溃
            Log.Error(e.Exception, "未观察到的后台任务异常（已标记观察，不终止进程）");
            e.SetObserved();
        };

        Log.Information("CreatFlow.NewApp starting — {Version} on {OS}",
            AppConfig.AppVersion, Environment.OSVersion);

        // --probe-topics <platform|all>：话题/活动真账号探针（诊断工具，不进业务路径）——
        // 每平台把 搜索/推荐/活动列表 各拉一次，报告写 %TEMP%\topic_probe.txt。
        // 可选：--session <guid> --keyword 家居 --upload-id=…（B站） --vid=…（头条）
        //       --edit-session=…（快手）。账号缺省取本地镜像里该平台第一个账号。
        {
            var topicsIndex = Array.IndexOf(args, "--probe-topics");
            if (topicsIndex >= 0 && topicsIndex + 1 < args.Length)
            {
                string? Arg(string name)
                {
                    var i = Array.IndexOf(args, name);
                    if (i >= 0 && i + 1 < args.Length) return args[i + 1];
                    var prefix = name + "=";
                    var inline = args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal));
                    return inline?[prefix.Length..];
                }

                var probeArgs = new Services.TopicProbe.ProbeArgs
                {
                    Platform = args[topicsIndex + 1],
                    SessionId = App.ProbeSessionId,
                    Keyword = Arg("--keyword") ?? "家居",
                    UploadId = Arg("--upload-id"),
                    VideoId = Arg("--vid"),
                    EditSessionId = Arg("--edit-session"),
                };
                var code = Services.TopicProbe.RunAsync(probeArgs).GetAwaiter().GetResult();
                Environment.Exit(code);
            }
        }

        // --probe-bilibili <bvid>：B站稿件详情回查（诊断工具，不进业务路径）——
        // 拉 /x/vupre/web/archive/view，确认 add/v3 下发的 topic_id / mission_id / dtime
        // 是否真的落到稿件上；报告写 %TEMP%\bilibili_probe.txt。可选 --session <guid>。
        // 前提：服务端在跑（会话 Cookie 在服务端库里）。
        {
            var biliIndex = Array.IndexOf(args, "--probe-bilibili");
            string? biliBvid = null;
            if (biliIndex >= 0 && biliIndex + 1 < args.Length
                && !args[biliIndex + 1].StartsWith("--", StringComparison.Ordinal))
            {
                biliBvid = args[biliIndex + 1];
            }
            else
            {
                var inline = args.FirstOrDefault(a =>
                    a.StartsWith("--probe-bilibili=", StringComparison.Ordinal));
                if (inline is not null) biliBvid = inline["--probe-bilibili=".Length..];
            }

            if (!string.IsNullOrWhiteSpace(biliBvid))
            {
                var code = Services.BilibiliArchiveProbe
                    .RunAsync(biliBvid!, App.ProbeSessionId).GetAwaiter().GetResult();
                Environment.Exit(code);
            }
        }

        // --probe-douyin-item <itemId>：抖音作品详情（创作中心）回查 —— 诊断工具，不进业务路径。
        // Web 端 aweme/detail 的活动字段为空；这里拉 /web/api/media/item/info/，把响应里所有
        // 名字含 activity 的字段原样写进 %TEMP%\douyin_item_probe.txt（验活动是否挂上）。
        {
            var itemIndex = Array.IndexOf(args, "--probe-douyin-item");
            string? douyinItem = null;
            if (itemIndex >= 0 && itemIndex + 1 < args.Length
                && !args[itemIndex + 1].StartsWith("--", StringComparison.Ordinal))
            {
                douyinItem = args[itemIndex + 1];
            }
            else
            {
                var inline = args.FirstOrDefault(a =>
                    a.StartsWith("--probe-douyin-item=", StringComparison.Ordinal));
                if (inline is not null) douyinItem = inline["--probe-douyin-item=".Length..];
            }

            if (!string.IsNullOrWhiteSpace(douyinItem))
            {
                var code = Services.DouyinItemInfoProbe
                    .RunAsync(douyinItem!, App.ProbeSessionId).GetAwaiter().GetResult();
                Environment.Exit(code);
            }
        }

        // --probe-toutiao-item <itemId>：头条作品详情（EditItem）回读 —— 诊断工具，不进业务路径。
        // 回读 HashtagList（结构化话题是否真的挂上）与 VideoInfo.Vid（hashtag/rec 推荐要用），
        // 报告写 %TEMP%\toutiao_item_probe.txt，原始 JSON 另存 .json。
        {
            var ttIndex = Array.IndexOf(args, "--probe-toutiao-item");
            string? toutiaoItem = null;
            if (ttIndex >= 0 && ttIndex + 1 < args.Length
                && !args[ttIndex + 1].StartsWith("--", StringComparison.Ordinal))
            {
                toutiaoItem = args[ttIndex + 1];
            }
            else
            {
                var inline = args.FirstOrDefault(a =>
                    a.StartsWith("--probe-toutiao-item=", StringComparison.Ordinal));
                if (inline is not null) toutiaoItem = inline["--probe-toutiao-item=".Length..];
            }

            if (!string.IsNullOrWhiteSpace(toutiaoItem))
            {
                var code = Services.ToutiaoItemProbe
                    .RunAsync(toutiaoItem!, App.ProbeSessionId).GetAwaiter().GetResult();
                Environment.Exit(code);
            }
        }

        // --probe-kuaishou-work：快手作品列表/分享页回查 —— 诊断工具，不进业务路径。
        // Avalonia 启动后由 App 开一个隐藏 WebView（快手签名模块只在页面里），
        // 跑完写 %TEMP%\kuaishou_work_probe.txt 再退出。
        // 可选 --keyword <词> / --publish-id <id> / --photo <photoId>（分享页 INIT_STATE）。
        {
            if (args.Contains("--probe-kuaishou-work"))
            {
                string? Arg(string name)
                {
                    var i = Array.IndexOf(args, name);
                    if (i >= 0 && i + 1 < args.Length) return args[i + 1];
                    var prefix = name + "=";
                    var inline = args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal));
                    return inline?[prefix.Length..];
                }

                App.ProbeKuaishou = true;
                App.ProbeKuaishouKeyword = Arg("--keyword");
                App.ProbeKuaishouPublishId = long.TryParse(Arg("--publish-id"), out var pid) ? pid : null;
                App.ProbeKuaishouPhoto = Arg("--photo");
            }
        }

        // --selftest：发布页底座自检（映射矩阵 / 本地持久化 / 视频探测）
        //            + 视频管理页自检（分页边界 / 天头 / 标题回退 / 删除结果口径 / 批量分组）
        //            + 异步任务自检（wire 数字钉死 / 平台结果→回报状态的翻译 / 合集确认条文案）
        //            + 合成页自检（发布单元 params.txt 格式契约 / 就地更新规矩 / 偏好往返）
        //            + 配音队列自检（存储恢复规则 / 登录+激活许可门槛 / mp3 文件命名）
        //            + 日志清理自检（按天保留边界 / 锁文件跳过 / 保留期设置往返）
        //            + 账号分组自检（存档往返 / 组名校验 / 恰好相等高亮 / 整组勾选一次落盘）
        //            + 本机存档会员级化自检（六档会员路径 / A·B 隔离 / 旧档采纳 / 换号重载）
        //            + 角色库上云自检（会员级包装档 / 裸数组旧档采纳 / dirty-revision 生命周期 / 纯映射）
        //            + 上传提示音音量自检（百分比持久化 / 会员隔离 / 坏档退默认 / 增益映射）
        //            + 合集标题规则自检（20 字上限 / 只允许文字数字空格 / 符号拦截）
        //            + 联系我们内容自检（公开配置取值 / 多邮箱拆行 / 兜底 HTML / 链接白名单）
        //            + 单机版本地库自检（建库种子 / 发布·合集·批量删除·会话全链路 / 备份 / 路由覆盖 / 并发）。
        // 这些是纯本地逻辑 —— UI 必须成对启动桌面端+服务端才能看，底座不用。
        // 退出码 = 失败项数（0 = 全过），便于脚本判定。
        if (args.Contains("--selftest"))
        {
            var failures = Services.PublishSelfTest.RunAsync().GetAwaiter().GetResult()
                         + Services.VideoArchiveSelfTest.Run()
                         + Services.AsyncTaskSelfTest.Run()
                         + Services.LlmSelfTest.Run()
                         + Services.ComposeSelfTest.Run()
                         + Services.LogCleanupSelfTest.Run()
                         + Services.AccountGroupSelfTest.Run()
                         + Services.LocalStoreMemberScopeSelfTest.Run()
                         + Services.ComposeRoleCloudSelfTest.Run()
                         + Services.DownloadSelfTest.Run()
                         + Services.SoundSettingsSelfTest.Run()
                         + Services.CollectionTitleSelfTest.Run()
                         + Services.CollectionContentTypeSelfTest.Run()
                         + Services.ContactSelfTest.Run()
                         + Services.DouyinPublishGuidanceSelfTest.Run()
                         + Services.TopicSelfTest.Run()
                         + Services.LocalStoreSelfTest.Run();
            Log.CloseAndFlush();
            Environment.Exit(failures);
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application terminated unexpectedly");
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "newapp_crash.txt"), ex.ToString()); } catch { }
            throw;
        }
        finally
        {
            Log.Information("CreatFlow.NewApp shutting down");
            Log.CloseAndFlush();
        }
    }

    /// <summary>
    /// 崩溃落盘：<c>%LocalAppData%\CreatFlow\crashes\crash_*.log</c>（与老版同目录、同前缀，
    /// 账号设置页的日志清理服务按龄扫它）。Serilog 写失败时这里是最后一道 —— 尽力而为，绝不抛。
    /// </summary>
    private static void WriteCrashFile(Exception? ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CreatFlow", "crashes");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir,
                $"crash_{DateTime.Now:yyyyMMddHHmmss}_{Guid.NewGuid().ToString("N")[..8]}.log");
            File.WriteAllText(file, $"""
                Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}
                Version: {AppConfig.AppVersion}
                Type: {ex?.GetType().FullName}
                Message: {ex?.Message}
                StackTrace:
                {ex?.StackTrace}
                """);
        }
        catch
        {
            // 崩溃路径上不允许再抛
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
