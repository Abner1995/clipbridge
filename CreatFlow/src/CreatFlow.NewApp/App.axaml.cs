using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using CreatFlow.NewApp.Models;
using CreatFlow.NewApp.Services;
using CreatFlow.NewApp.ViewModels;
using CreatFlow.NewApp.Views;
using CreatFlow.LocalStore;
using CreatFlow.Media.Llm;
using CreatFlow.Platforms.Download;
using CreatFlow.Platforms.Kuaishou;
using CreatFlow.Platforms.Xiaohongshu;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.Tasks;

namespace CreatFlow.NewApp;

// 基线类型写全名：NewApp 现在引用 CreatFlow.Application 程序集，
// 裸写 Application 会被解析成命名空间 CreatFlow.Application（CS0118）
public partial class App : Avalonia.Application
{
    /// <summary>--smoke 自动冒烟模式（Program.Main 解析 args 后设置）</summary>
    public static bool SmokeMode;

    /// <summary>--smoke-login：冒烟登录窗（视觉自检）</summary>
    public static bool SmokeLoginWindow;

    /// <summary>
    /// --probe-douyin：抖音「无水印源」探针（诊断工具，不进业务路径）。
    /// 借已登录的 WebView2 档案，在页面上下文里 fetch aweme/detail，把候选播放地址全列出来
    /// —— 详见 <see cref="Services.DouyinDetailProbe"/>。值为作品 URL 或纯 ID；null = 非探针模式。
    /// </summary>
    public static string? ProbeDouyinTarget;

    /// <summary>--session：探针用哪个账号档案（不给则取最近更新的抖音账号）</summary>
    public static Guid? ProbeSessionId;

    /// <summary>--probe-kuaishou-work：快手回查探针（App 启动后开隐藏 WebView 跑）</summary>
    public static bool ProbeKuaishou;
    public static string? ProbeKuaishouKeyword;
    public static long? ProbeKuaishouPublishId;
    public static string? ProbeKuaishouPhoto;

    /// <summary>全局 DI 容器（App.OnFrameworkInitializationCompleted 构建）</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    private LoginWindow? _login;
    private ShellWindow? _shell;
    private bool _appQuit;
    private bool _switching;

    /// <summary>
    /// DI 注册：VM transient（每窗/每导航新实例），页面解析器 Func 全局单例。
    /// 数据驱动导航（ShellViewModel.Sections/NavItem）经解析器从容器取页面。
    /// SessionContext 为单例 —— 壳/激活页/首页共享同一状态源，激活成功后
    /// Refresh() 广播 MembershipChanged 让壳重算门控（transient 各自持快照无法联动）。
    /// </summary>
    private static ServiceProvider BuildServices()
    {
        var collection = new ServiceCollection();
        collection.AddSingleton<Func<Type, ViewModelBase>>(
            sp => pageType => (ViewModelBase)sp.GetRequiredService(pageType));
        collection.AddSingleton<SessionContext>();
        // 账号标签坞:App 级单例(壳/平台页/坞视图共享);档案目录经 storeFactory 现解析 → 换会员不串号
        collection.AddSingleton<AccountDockViewModel>();
        collection.AddSingleton<Func<PlatformAccountStore>>(
            sp => () => sp.GetRequiredService<PlatformAccountStore>());
        collection.AddTransient<LoginViewModel>();
        collection.AddTransient<ShellViewModel>();
        collection.AddTransient<HomePageViewModel>();
        collection.AddTransient<ActivationViewModel>();
        collection.AddTransient<DeviceViewModel>();
        collection.AddTransient<AnnouncementViewModel>();
        collection.AddTransient<DingTalkConfigViewModel>();
        collection.AddTransient<AccountViewModel>();
        collection.AddTransient<ContactViewModel>();
        collection.AddTransient<PlatformAccountStore>();
        collection.AddTransient<PlatformViewModel>();
        collection.AddTransient<CollectionViewModel>();
        // 账号勾选存档（发布页 / 合成页「自动发布」共用的「上次勾选」）：**会员级**路径
        // accounts/{memberId:N}/account-selection.json（offSessions 是会员自己的会话 ID，
        // 机器级共享时另一会员进页剪除遗骸会把自己这份误删——2026-10-15 批次 7）。
        // 冒烟换 TEMP 根 —— 冒烟特判会点账号芯片造景，不能碰用户的 account-selection.json
        collection.AddSingleton(sp => new AccountSelectionStore(
            App.SmokeMode ? Path.Combine(Path.GetTempPath(), "creatflow-smoke-selection") : null,
            () => sp.GetRequiredService<SessionContext>().MemberId));
        // 账号显示顺序（发布页排序页 / 平台页 / 账号坞 / 芯片装配共用一份）：会员级路径。
        // 冒烟换 TEMP 根 —— 发布页进页会真裁剪写盘（此前未换根，是一处静默隐患）
        collection.AddSingleton(sp => new AccountOrderStore(
            App.SmokeMode ? Path.Combine(Path.GetTempPath(), "creatflow-smoke-order") : null,
            () => sp.GetRequiredService<SessionContext>().MemberId));
        // 发布预设 + 未提交草稿（发布页 / 合成队列自动发布提交器共用同一实例）：会员级路径。
        // 冒烟换 TEMP 根 —— 发布页进页会真裁剪/落盘，不能碰用户档
        collection.AddSingleton(sp => new PublishDraftStore(
            App.SmokeMode ? Path.Combine(Path.GetTempPath(), "creatflow-smoke-publish") : null,
            () => sp.GetRequiredService<SessionContext>().MemberId));
        // 账号芯片装配 + 勾选读写（两页读同一份内存集合 → 「在哪边改都生效」；
        // 换号后首次使用自动整份重载，见服务类注释）
        collection.AddSingleton<AccountChipsService>();
        // 账号分组（2026-10-14：发布页/合成页组条 + 平台页分组卡；2026-10-15 上云）：会员级路径
        // {root}\accounts\{memberId:N}\account-groups.json（与 sessions.json 同「换号不串库」模型），
        // member 由 SessionContext 动态取（登出→换号登录时单例服务靠它检测重载）。
        // 冒烟换 TEMP 根 —— 冒烟会造分组景并执行「点组名」命令，不能碰用户的 account-groups.json
        collection.AddSingleton(sp => new AccountGroupStore(
            App.SmokeMode ? Path.Combine(Path.GetTempPath(), "creatflow-smoke-groups") : null,
            () => sp.GetRequiredService<SessionContext>().MemberId));
        collection.AddSingleton<AccountGroupsService>();
        // 日志保留设置 + 清理服务（账号设置页「日志与存储」卡 / 启动后台清理共用）：
        // 冒烟换 TEMP 根 —— 冒烟会真执行一次清理造景，不能碰用户日志文件（同一条纪律）
        collection.AddSingleton(_ => new LogSettingsStore(
            App.SmokeMode ? Path.Combine(Path.GetTempPath(), "creatflow-smoke-logsettings") : null));
        collection.AddSingleton(sp => new LogCleanupService(
            sp.GetRequiredService<LogSettingsStore>(),
            App.SmokeMode ? Path.Combine(Path.GetTempPath(), "creatflow-smoke-logs") : null));
        collection.AddTransient<PublishViewModel>();
        // 视频管理页：纯查询 + 按需删除，不持有跨页状态 → transient（页面实例由壳缓存）
        collection.AddTransient<VideoManageViewModel>();
        // 上传队列页是**消费者**（上传+发布在它那里跑），必须全应用一份：
        // 单例 → 壳/页面缓存/徽章看到的是同一个实例，且消费者的生命周期 = 应用生命周期
        collection.AddSingleton<UploadQueueViewModel>();
        // 删除任务的消费者同理 —— 它在后台跑批量删除，不挂在任何一个页面上
        //（用户点了删除就能走，异步任务页只是它的可见面）
        collection.AddSingleton<DeleteTaskConsumer>();
        // 异步任务页：纯查询 + 重试/删记录，不持有跨页状态 → transient（页面实例由壳缓存）
        collection.AddTransient<AsyncTaskViewModel>();
        // AI 设置页 + 本地 LLM：配置存储与 llama-server 宿主全应用各一份
        //（宿主是进程管理器，必须单例 —— 多个实例会起多个 server 互相抢端口/内存；
        //  它的生命周期 = 应用生命周期，热态保持/空闲卸载都在它内部）
        collection.AddSingleton<LlmConfigStore>();
        collection.AddSingleton<LlamaServerHost>();
        // 角色 AI 优化的薄服务（合成页按钮 / 批量「用 AI 优化每条」共用；只读配置，不写）
        collection.AddSingleton<RoleOptimizer>();
        collection.AddTransient<AiSettingsViewModel>();
        // 创作组：偏好（配音选择/输出目录/导出目录）全应用一份 —— 配音页与合成页共用；页面 VM transient
        collection.AddSingleton<ComposeSettingsStore>();
        // 视频水印偏好（账号设置页「视频水印」卡 / 合成页与队列批量入口读取）：
        // **会员级**路径 accounts/{memberId:N}/watermark-settings.json（换号不串品牌；
        // 文字是用户的品牌，机器级共享会让 A 会员的水印出现在 B 会员的视频里）。
        // 冒烟换 TEMP 根 —— 冒烟会改水印卡造景，不能碰用户的档
        collection.AddSingleton(sp => new WatermarkSettingsStore(
            App.SmokeMode ? Path.Combine(Path.GetTempPath(), "creatflow-smoke-watermark") : null,
            () => sp.GetRequiredService<SessionContext>().MemberId));
        // 上传结果提示音音量（账号设置页「上传提示音」卡，2026-10-22）：**会员级**路径
        // accounts/{memberId:N}/sound-settings.json（声音偏好跟着账号走）。
        // 冒烟换 TEMP 根 —— 冒烟会改音量档造景，不能碰用户的档
        collection.AddSingleton(sp => new SoundSettingsStore(
            App.SmokeMode ? Path.Combine(Path.GetTempPath(), "creatflow-smoke-sound") : null,
            () => sp.GetRequiredService<SessionContext>().MemberId));
        // 合成队列存储：**会员级**路径 accounts/{memberId:N}/compose_queue.json（任务快照含
        // 会员自己的账号目标；换号由 VM 检测后挂起切换，见 ComposeQueueViewModel）；
        // 冒烟换 TEMP 根 —— 冒烟特判会真入队造景，不能碰用户的队列（与「临时探针要还原用户数据」同一条纪律）
        collection.AddSingleton(sp => new ComposeQueueStore(
            App.SmokeMode ? Path.Combine(Path.GetTempPath(), "creatflow-smoke-queue") : null,
            () => sp.GetRequiredService<SessionContext>().MemberId));
        // 角色库存档（2026-10-15 上云）：会员级路径 accounts/{memberId:N}/compose_roles.json +
        // 云端同步缓存（见 ComposeRoleCloudSync）；冒烟换 TEMP 根 —— 冒烟特判会造角色景，
        // 不碰用户的 compose_roles.json（与队列存储同一条纪律）
        collection.AddSingleton(sp => new ComposeRoleStore(
            App.SmokeMode ? Path.Combine(Path.GetTempPath(), "creatflow-smoke-roles") : null,
            () => sp.GetRequiredService<SessionContext>().MemberId));
        // 角色库读写服务（AI 设置页编辑 / 合成两页读取，同一份内存；Changed → App 层静默推云端）
        collection.AddSingleton<ComposeRolesService>();
        // 合成队列页是**消费者**（本机串行合成在它那里跑）—— 与上传队列同构：单例，
        // 壳开壳即启动（用户点了合成就能走，队列自己跑完），徽章看的也是同一个实例
        collection.AddSingleton<ComposeQueueViewModel>();
        // 自动发布提交器（合成完 → 无头走发布页同一条管线提交进上传队列）：
        // 无头复用发布侧（PublishPipeline/PublishResolver 不进 DI —— 只有它一个消费者，内部 new；
        // 冒烟下永不被调用：合成消费者被 Start() 的 SmokeMode 闸挡在门外）
        collection.AddSingleton<PublishUnitSubmitter>();
        // 配音队列存储：与合成队列同规矩 —— 会员级路径 + 冒烟换 TEMP 根
        //（冒烟特判会真入队造景，不碰用户 tts_queue.json）
        collection.AddSingleton(sp => new TtsQueueStore(
            App.SmokeMode ? Path.Combine(Path.GetTempPath(), "creatflow-smoke-ttsqueue") : null,
            () => sp.GetRequiredService<SessionContext>().MemberId));
        // 配音队列页是**消费者**（本机并行配音在它那里跑）—— 同构：单例，壳开壳即启动；
        // 可暂停/继续（暂停态进存档），消费者领取前查许可（登录+激活，过期自动暂停）
        collection.AddSingleton<TtsQueueViewModel>();
        // 视频下载：解析器注册表 + ffmpeg 合并器（引擎无状态，单例即可）。
        // 队列存储走**会员级** accounts/{memberId:N}/download_queue.json（任务快照含登录态解析结果），
        // 冒烟换 TEMP 根 —— 冒烟会点解析/入队造景，不能碰用户队列
        collection.AddSingleton(_ => new DownloadEngine(
            DownloadExtractors.CreateDefault(), new FfmpegMediaMerger()));
        collection.AddSingleton(sp => new DownloadQueueStore(
            App.SmokeMode ? Path.Combine(Path.GetTempPath(), "creatflow-smoke-download") : null,
            () => sp.GetRequiredService<SessionContext>().MemberId));
        // 下载队列页是**消费者**（本机下载在它那里跑）—— 与合成/配音队列同构：单例，开壳即启动
        collection.AddSingleton<DownloadQueueViewModel>();
        collection.AddTransient<ComposeViewModel>();
        collection.AddTransient<TtsViewModel>();

        // ── 单机版：本地 SQLite 账本 + 本地 API 路由 ──
        // ⚠️ 接线必须早于任何后台消费者启动（壳的 StartConsumers 立刻发请求；
        //   晚设会让首批请求走 HTTP 等 15 秒超时）。
        // 建库/迁移在 PrepareLocalStore() 里先做（失败要弹错误窗、不进业务）。
        if (AppConfig.IsStandalone)
        {
            collection.AddLocalStore(LocalStorePaths.DatabasePath);
            ApiClient.LocalHandler = LocalApiRouter.HandleAsync;
        }

        return collection.BuildServiceProvider();
    }

    /// <summary>
    /// 单机版建库 / 迁移 / 种子。失败返回 false —— 调用方弹错误窗并**不启动业务**
    /// （doc 6.5：保留原库 + 备份，绝不删库重建）。
    /// </summary>
    private bool PrepareLocalStore()
    {
        // 冒烟走 TEMP 根：绝不碰用户的 creatflow.db（与六档 JSON 存档同一条纪律）
        if (SmokeMode)
            LocalStorePaths.AccountsRootOverride =
                Path.Combine(Path.GetTempPath(), "creatflow-smoke-localstore", "accounts");

        try
        {
            LocalStoreInitializer.EnsureInitialized(
                appVersion: AppConfig.AppVersion,
                onError: ex => Log.Warning(ex, "[LocalStore] 自动备份失败"));
            Log.Information("[LocalStore] 单机版本地库已启用：{Path}", LocalStorePaths.DatabasePath);
            return true;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "[LocalStore] 本地数据库初始化失败");
            try
            {
                File.WriteAllText(
                    Path.Combine(Path.GetTempPath(), "creatflow_localstore_fatal.txt"),
                    ex.ToString());
            }
            catch { /* 报告文件写不出也不影响错误窗 */ }

            ShowFatalWindow(
                "本地数据库初始化失败",
                $"{ex.Message}\n\n原数据库与升级前备份均已保留（未删除、未重建）。\n"
                + $"数据目录：{LocalStorePaths.MemberDir}\n"
                + "请备份该目录后联系售后；详细信息见 %TEMP%\\creatflow_localstore_fatal.txt");
            return false;
        }
    }

    /// <summary>启动失败错误窗（不依赖 Shell/主题，纯代码构造）</summary>
    private void ShowFatalWindow(string title, string message)
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        var window = new Window
        {
            Title = "CreatFlow 单机版 · " + title,
            Width = 620,
            Height = 280,
            CanResize = false,
            Content = new Border
            {
                Padding = new Thickness(24),
                Child = new TextBlock
                {
                    Text = message,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    FontSize = 13,
                    Foreground = Avalonia.Media.Brushes.DarkRed,
                }
            }
        };

        desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
        desktop.MainWindow = window;
        window.Show();
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    // ════════════ 输入框右键：保住选区 ════════════

    /// <summary>右键时记下的「输入框 + 选区」（AppInputTheme 的右键菜单打开后用它把选区塞回去）</summary>
    private static (TextBox Box, int Start, int End)? _pendingSelection;

    /// <summary>
    /// 右键输入框请求菜单时先记下选区 —— Avalonia 的 ContextMenu 弹出会夺走焦点，而 TextBox
    /// 失焦即清空选区，于是「先选中 → 右键 → 复制」这条最常见的复制路径静默失效（菜单点了没反应）。
    /// 记完等菜单 Opened（弹出层已接完焦点）再塞回去，见 OnInputMenuOpened。
    /// </summary>
    private static void RememberSelectionForMenu(TextBox tb)
        => _pendingSelection = (tb, tb.SelectionStart, tb.SelectionEnd);

    /// <summary>AppInputTheme 输入框模板内 Border：右键按下先记选区（此刻选区还在）</summary>
    private void OnInputPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border { TemplatedParent: TextBox tb }
            && e.GetCurrentPoint(tb).Properties.IsRightButtonPressed)
        {
            RememberSelectionForMenu(tb);
        }
    }

    /// <summary>AppInputTheme 右键菜单 Opened：恢复右键前的选区，让菜单里的复制/剪切有内容可操作</summary>
    private void OnInputMenuOpened(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_pendingSelection is not { } pending) return;
        _pendingSelection = null;
        if (pending.End > pending.Start && pending.Box.SelectionStart == pending.Box.SelectionEnd)
        {
            pending.Box.SelectionStart = pending.Start;
            pending.Box.SelectionEnd = pending.End;
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // 单机版：先建库/迁移（失败弹错误窗、不进业务，doc 6.5）
        if (AppConfig.IsStandalone && !PrepareLocalStore())
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        Services = BuildServices();

        // 账号分组 / 角色库上云（2026-10-15）：本地任一改动 → 静默整表推云端（唯一接线点，只接一次）。
        // 冒烟不接 = 第一道闸（第二道在各自 CloudSync 入口的 SmokeMode 判断）——
        // 冒烟会造分组/角色景，接了会把「冒烟宠物 / 冒烟房淘惠」推到本机真实登录的云端账号上。
        if (!SmokeMode)
        {
            var groupsSvc = Services.GetRequiredService<AccountGroupsService>();
            groupsSvc.Changed += () => _ = AccountGroupCloudSync.PushAsync(groupsSvc);
            var rolesSvc = Services.GetRequiredService<ComposeRolesService>();
            rolesSvc.Changed += () => _ = ComposeRoleCloudSync.PushAsync(rolesSvc);
        }

        // 小红书 x-s / x-s-common 签名（mnsv2 只在页面里，纯 C# 算不出来）——
        // 转 XhsSignerService：用请求所属账号的坞内 WebView2 档案页签，签不了返回 null
        //（老版靠登录后常驻的隐藏签名窗；坞时代改由账号标签页承担）。
        XhsPlatform.SetSigner((urlPath, bodyJson, sessionId, ct) =>
            XhsSignerService.SignAsync(sessionId, urlPath, bodyJson, ct));

        // 快手 sig3/sig4 签名（模块只在页面 webpack 里，纯 C# 算不出来）——
        // 转 KuaishouSignerService：用请求所属账号的坞内 WebView2 档案页签，
        // 替代已移除的 node.exe + chunk.js 回退（chunk.js 是快手平台版权代码，不能随开源仓库分发）。
        KuaishouPlatform.SetSigner((sessionId, reqJson, ct) =>
            KuaishouSignerService.SignAsync(sessionId, reqJson, ct));

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 手动管理窗口生命周期（登录窗 ↔ 主窗），不用默认 OnLastWindowClose
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            // 退出时释放 DI 单例（LlamaServerHost 要杀本地 llama-server 子进程；
            // 它另有 ProcessExit 兜底 —— 崩退/任务管理器结束也能收尸，这里是正规路径）
            desktop.Exit += (_, _) => (Services as IDisposable)?.Dispose();

            if (ProbeDouyinTarget is { } probeTarget)
            {
                // 探针：只开一个 WebView 窗口跑诊断，不走登录/心跳/消费者
                //（--probe-douyin 见 Services/DouyinDetailProbe）
                _ = RunProbeAsync(desktop, probeTarget);
            }
            else if (ProbeKuaishou)
            {
                // 快手回查探针：签名模块只在页面里 → 开隐藏 WebView 跑
                _ = RunKuaishouProbeAsync(desktop);
            }
            else if (SmokeMode)
            {
                RunSmokeAsync(desktop);
            }
            else
            {
                // 服务端 403 membership_expired / account_disabled 全局拦截
                // （单机版没有服务端，不订阅 —— 也没有"撤销会员"这回事）
                if (!AppConfig.IsStandalone)
                {
                    ApiClient.OnMembershipRevoked += reason =>
                        Dispatcher.UIThread.Post(() => OnMembershipRevoked(reason));
                }
                // 本机日志按保留期清理（2026-10-14「日志自动清理」）——机器级维护，与登录态
                // 无关（登录窗阶段也跑）；桌面端常重启，启动这一跑就是「按天保留」的主执行点。
                // 后台执行不卡启动；冒烟走 RunSmokeAsync 分支，天然到不了这里（另有 TEMP 根）
                _ = Task.Run(async () =>
                {
                    try { await Services.GetRequiredService<LogCleanupService>().RunAsync(); }
                    catch (Exception ex) { Log.Warning(ex, "[LogCleanup] 启动清理失败"); }
                });
                _ = StartupAsync(desktop);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    // ════════════ 抖音「无水印源」探针（--probe-douyin） ════════════

    /// <summary>
    /// 跑一次诊断然后把进程退出（退出码 = 探针结论，见 <see cref="DouyinDetailProbe.RunAsync"/>）。
    /// 整个流程都在 UI 线程的同步上下文里跑 —— WebView 的 InvokeScript / Show 只能在那儿。
    /// </summary>
    private async Task RunProbeAsync(IClassicDesktopStyleApplicationLifetime desktop, string target)
    {
        var code = 9;
        try
        {
            code = await DouyinDetailProbe.RunAsync(target, ProbeSessionId);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DouyinProbe] 未捕获异常");
        }
        finally
        {
            Log.Information("[DouyinProbe] 退出码 {Code}", code);
            Log.CloseAndFlush();
            desktop.Shutdown(code);
        }
    }

    private async Task RunKuaishouProbeAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var code = 9;
        try
        {
            code = await KuaishouWorkProbe.RunAsync(
                ProbeKuaishouKeyword, ProbeKuaishouPublishId, ProbeKuaishouPhoto, ProbeSessionId);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[KuaishouProbe] 未捕获异常");
        }
        finally
        {
            Log.Information("[KuaishouProbe] 退出码 {Code}", code);
            Log.CloseAndFlush();
            desktop.Shutdown(code);
        }
    }

    // ════════════ 启动编排（QQ 式：无会话 → 登录表单；有会话 → 自动验证后直进主窗） ════════════

    private async Task StartupAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        // 单机版：本地库已在 PrepareLocalStore 初始化，没有登录/心跳/设备注册 —— 直接进壳
        if (AppConfig.IsStandalone)
        {
            OpenShellAfterLogin();
            return;
        }

        if (!TokenStorage.HasValidToken())
        {
            ShowLoginWindow(desktop, autoCheck: false);
            return;
        }

        var login = ShowLoginWindow(desktop, autoCheck: true);
        login.Vm.ShowForm("正在验证登录状态…", isError: false);

        try
        {
            var (statusCode, body) = await ApiClient.PostRawAsync("/api/heartbeat");

            if (statusCode == 200)
            {
                var heartbeat = JsonSerializer.Deserialize<HeartbeatResponse>(body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (heartbeat == null)
                {
                    login.Vm.EndAutoCheck("服务器返回异常，请重新登录", isError: true);
                    return;
                }
                if (heartbeat.IsDisabled)
                {
                    TokenStorage.Clear();
                    login.Vm.EndAutoCheck("账号已被禁用，请联系管理员", isError: true);
                    return;
                }

                // 同步服务端到期时间（续费后本地更新；含 NewSecret 轮换）
                var session = TokenStorage.Load();
                if (session != null && heartbeat.ExpireDate != session.ExpireDate)
                {
                    TokenStorage.Save(session.Token, session.Username, heartbeat.ExpireDate,
                        heartbeat.NewSecret ?? session.ClientSecret, session.MemberId);
                }
                OpenShellAfterLogin();
            }
            else if (statusCode == 401)
            {
                TokenStorage.Clear();
                login.Vm.EndAutoCheck("登录已过期，请重新登录", isError: true);
            }
            else
            {
                // 其他状态码（5xx 等）→ 与断网同处理：本地会话未过期则离线进入
                OpenShellOfflineIfPossible(login);
            }
        }
        catch
        {
            OpenShellOfflineIfPossible(login);
        }
    }

    /// <summary>断网/服务不可用：本地 JWT 未过期 + 会员未过期 → 离线放行，否则回落表单</summary>
    private void OpenShellOfflineIfPossible(LoginWindow login)
    {
        var session = TokenStorage.Load();
        if (session != null && session.ExpireDate >= DateTime.Now)
        {
            OpenShellAfterLogin();
        }
        else
        {
            login.Vm.EndAutoCheck(session == null
                ? "无法连接服务器，请检查网络后重试"
                : "无法连接服务器，且本地会员已过期，请联网后重试", isError: true);
        }
    }

    /// <summary>打开主窗（壳内按本地会话展示 Active/Expired 门控）</summary>
    private void OpenShellAfterLogin()
    {
        if (_appQuit) return;
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;

        // 会话单例：登录/心跳同步/踢出重建都可能换了 token.dat —— 建壳前强制重载，
        // 确保壳与页面拿到的门控状态与本次会话一致
        Services.GetRequiredService<SessionContext>().Refresh();

        // 提示音音量：上传是在后台消费者里跑的，用户不一定打开过账号设置页 ——
        // 登录进壳时就把当前会员的音量档灌进 PublishSoundNotifier（失败静默沿用默认）。
        _ = LoadSoundSettingsAsync();

        // 档案延迟删除补删(解绑/回滚时渲染进程未退留下的 pending marker):
        // 建壳前触发 —— 任何 WebView2 创建之前(此刻无实例,删除必然成功)
        var memberId = Services.GetRequiredService<SessionContext>().MemberId;
        if (memberId != Guid.Empty)
            _ = Task.Run(() => AccountProfileCleanup.RunPendingCleanup(memberId));

        var shell = new ShellWindow
        {
            DataContext = Services.GetRequiredService<ShellViewModel>()
        };
        var vm = (ShellViewModel)shell.DataContext;
        vm.OnLogoutRequested = Logout;
        _shell = shell;
        shell.Closed += OnShellClosed;

        desktop.MainWindow = shell;
        shell.Show();

        // 已绑定账号吸附为标签（Chrome 心智:账号=标签;懒加载不建 WebView,点开才导航）
        // 失败静默:平台页「打开」/下次启动兜底
        var dock = Services.GetRequiredService<AccountDockViewModel>();
        _ = dock.EnsureBoundSessionsAsync();

        // 账号分组 / 角色库：登录/启动后拉云端（云端权威;本地有未推送改动则编排器改为先推、失败静默）。
        // 覆盖自动登录 / 离线放行 / 手动登录 / 会员恢复重建壳全部路径。
        // 角色库：升级首登会在 EnsureLoadedAsync 里一次性采纳旧机器级档（标 dirty）→ 这里先推上云。
        _ = AccountGroupCloudSync.PullAsync(Services.GetRequiredService<AccountGroupsService>());
        _ = ComposeRoleCloudSync.PullAsync(Services.GetRequiredService<ComposeRolesService>());

        // 小红书 x-s 签名宿主热身：签名要靠页面里的 mnsv2，而坞内 WebView 懒加载，
        // 且 WebView 宿主控件只存在于「平台账号」页（坞的宿主层嵌在该页里）。
        // → 开壳即静默预载平台页（建隐藏控件树、不切页不显示）；需要签名时让坞把该标签
        //   热起来（建 WebView + 导航），全程用户都停在原来的页面上。
        // 壳关闭时摘掉 HostWarmer —— 坞随壳回收。
        vm.PreloadPageSilently("platform");
        XhsSignerService.HostWarmer = dock.EnsureSignHostReadyAsync;
        KuaishouSignerService.HostWarmer = dock.EnsureSignHostReadyAsync;

        // 抖音 passport 二次验证（authlv）/ 上传凭证 status_code=8：发布被拦时把该账号的标签亮出来，
        // 并落到发布页 —— 验证由发布动作触发，停在首页够不着（2026-10-28 实测 + 平台帮助中心口径）。
        WireDouyinVerifyRequester(dock, vm);

        // 抖音 passport 二次验证（authlv）/ 上传凭证 status_code=8：发布被拦时把该账号的标签亮出来，
        // 并落到发布页 —— 验证由发布动作触发，停在首页够不着（2026-10-28 实测 + 平台帮助中心口径）。
        WireDouyinVerifyRequester(dock, vm);

        // 登录窗已完成使命
        _login?.Close();
        _login = null;
    }

    /// <summary>
    /// 登录进壳时把当前会员的提示音音量灌进 <see cref="PublishSoundNotifier"/>。
    /// 读档失败静默沿用默认档 —— 音量是锦上添花，不能阻断进壳。
    /// </summary>
    private static async Task LoadSoundSettingsAsync()
    {
        try
        {
            var store = Services.GetRequiredService<SoundSettingsStore>();
            PublishSoundNotifier.VolumePercent = (await store.LoadAsync()).VolumePercent;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[App] 读取提示音设置失败（沿用默认音量）");
        }
    }

    /// <summary>
    /// 抖音身份验证引导的接线（开壳 / 冒烟两条路径共用 —— 冒烟自建壳，不走 OpenShellAfterLogin）。
    ///
    /// passport authlv / 上传凭证 status_code=8 被拦时：激活该账号的标签、切到平台页、
    /// 把标签页落到发布页（验证由发布动作触发，落在发布页用户离「手动发一条」只差一次点击）。
    /// 节流在 DouyinWebExecutor：每账号每次运行只引导一次，批量发布不会反复把用户拽离当前页。
    /// </summary>
    private void WireDouyinVerifyRequester(AccountDockViewModel dock, ShellViewModel shell)
    {
        DouyinWebExecutor.VerifyRequester = async (sessionId, _) =>
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var tab = dock.Find(sessionId);
                if (tab is null)
                {
                    Log.Debug("[Douyin] 会话 {Session} 不在账号坞标签里 —— 无法引导验证", sessionId);
                    return false;
                }

                dock.Activate(tab);
                tab.StatusText = "抖音要求完成身份验证 —— 请在本页发布一条视频完成验证";
                tab.RequestNavigate(DouyinWebExecutor.PublishPageUrl);
                shell.NavigateToKey("platform");
                return true;
            });
    }

    // ════════════ 窗口事件 ════════════

    private void OnLoginClosed(object? sender, EventArgs e)
    {
        if (_shell is not { IsVisible: true } && !_switching)
        {
            _appQuit = true;
            (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        }
    }

    private void OnShellClosed(object? sender, EventArgs e)
    {
        XhsSignerService.HostWarmer = null; // 坞随壳回收：别再等不存在的签名宿主
        KuaishouSignerService.HostWarmer = null;
        DouyinWebExecutor.VerifyRequester = null; // 同上：引导要亮账号坞，坞没了就别再引导
        if (_login is not { IsVisible: true } && !_switching)
        {
            _appQuit = true;
            (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        }
    }

    /// <summary>登出：清会话 → 回登录窗（关闭主窗不触发退出）</summary>
    private void Logout()
    {
        TokenStorage.Clear();
        // 队列消费者停掉：登出后凭证已失效，再跑下去只会一路报「取会话凭证失败」
        // （服务端任务停在 Pending，下次登录自动接续 —— 状态权威在服务端）
        Services.GetRequiredService<UploadQueueViewModel>().Stop();
        Services.GetRequiredService<DeleteTaskConsumer>().Stop();
        Services.GetRequiredService<AsyncTaskViewModel>().Stop();   // 停掉它的 5 秒轮询
        // 配音 / 合成队列是纯本机任务，但「凡是功能页面，都必须登录且激活才能使用」：
        // 登出即停摆（任务留在队里，重新登录后接着跑）
        Services.GetRequiredService<TtsQueueViewModel>().PauseBecause(
            "已退出登录，配音队列已暂停 —— 重新登录后点「继续」");
        Services.GetRequiredService<ComposeQueueViewModel>().PauseBecause(
            "已退出登录，合成队列已暂停 —— 重新登录后自动接着跑");
        Services.GetRequiredService<DownloadQueueViewModel>().PauseBecause(
            "已退出登录，视频下载已暂停 —— 重新登录后自动接着跑");
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        var login = ShowLoginWindow(desktop, autoCheck: false);
        login.Vm.ShowForm("已退出登录", isError: false);
        _shell?.Close();
        _shell = null;
    }

    /// <summary>建登录窗并显示（autoCheck = 自动验证转圈模式）</summary>
    private LoginWindow ShowLoginWindow(IClassicDesktopStyleApplicationLifetime desktop, bool autoCheck)
    {
        var w = new LoginWindow(Services.GetRequiredService<LoginViewModel>());
        w.Closed += OnLoginClosed;
        _login = w;
        w.Vm.OnLoginSucceeded = OpenShellAfterLogin;
        if (autoCheck) w.Vm.BeginAutoCheck();

        desktop.MainWindow = w;
        w.Show();
        return w;
    }

    // ════════════ 服务端撤销会员资格 ════════════

    private void OnMembershipRevoked(string reason)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;

        // 会员被撤销（禁用/过期）后，消费循环不能再跑：它会拿着已经不被接受的凭证
        // 继续上传发布，而且过期用户的「上传队列」入口是灰的 —— 用户连「暂停全部」都够不到。
        // 服务端任务停在 Pending，恢复会员后下次启动自动接续。
        Services.GetRequiredService<UploadQueueViewModel>().Stop();
        Services.GetRequiredService<DeleteTaskConsumer>().Stop();
        Services.GetRequiredService<AsyncTaskViewModel>().Stop();
        // 配音 / 合成队列是纯本机任务，但「凡是功能页面，都必须登录且激活才能使用」：
        // 撤销会员即停摆（任务留在队里，恢复会员后接着跑）
        Services.GetRequiredService<TtsQueueViewModel>().PauseBecause(
            "会员状态异常，配音队列已暂停 —— 恢复后点「继续」");
        Services.GetRequiredService<ComposeQueueViewModel>().PauseBecause(
            "会员状态异常，合成队列已暂停 —— 恢复后自动接着跑");
        Services.GetRequiredService<DownloadQueueViewModel>().PauseBecause(
            "会员状态异常，视频下载已暂停 —— 恢复后自动接着跑");

        if (reason == "account_disabled")
        {
            TokenStorage.Clear();
            _switching = true;
            _shell?.Close();
            _shell = null;
            _switching = false;
            if (_login is not { IsVisible: true })
            {
                var login = ShowLoginWindow(desktop, autoCheck: false);
                login.Vm.ShowForm("账号已被禁用，请联系管理员", isError: true);
            }
        }
        else // membership_expired → 本地记为已过期 → 重建壳（顶部红横幅引导激活）
        {
            var s = TokenStorage.Load();
            if (s != null)
            {
                TokenStorage.Save(s.Token, s.Username, DateTime.MinValue,
                    s.ClientSecret, s.MemberId);
            }
            _switching = true;
            _shell?.Close();
            _shell = null;
            _switching = false;
            OpenShellAfterLogin();
        }
    }

    // ════════════ --smoke 冒烟 ════════════

    /// <summary>
    /// 等可视树里出现满足条件的元素（默认最多 5 秒）。
    /// 冒烟里凡是「等模板/组条实例化」的地方都该用它，而不是固定 <c>Task.Delay</c> ——
    /// Release + 混淆构建的 JIT 更慢，固定 200~400ms 会间歇性不够
    /// （2026-10-26 打包门禁实测：「按钮没接上命令」类断言连炸）。
    /// </summary>
    private static async Task<bool> WaitVisualAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            if (condition()) return true;
            if (Environment.TickCount64 >= deadline) return false;
            await Task.Delay(50);
        }
    }

    private async void RunSmokeAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            Window target;
            if (SmokeLoginWindow)
            {
                target = new LoginWindow(Services.GetRequiredService<LoginViewModel>());
                desktop.MainWindow = target;
                target.Show();
            }
            else
            {
                target = new ShellWindow
                {
                    DataContext = Services.GetRequiredService<ShellViewModel>()
                };
                desktop.MainWindow = target;
                target.Show();

                // 逐页巡检：切遍所有真实页面（真页=PageType 非空），
                // 页面模板/主题运行时错误在此暴露，不在用户验收时炸
                if (target.DataContext is ShellViewModel shell)
                {
                    // 与真开壳同源的接线（冒烟自建壳、不走 OpenShellAfterLogin）——
                    // 断言在下方的平台页段
                    WireDouyinVerifyRequester(
                        Services.GetRequiredService<AccountDockViewModel>(), shell);

                    // 角色库造景（2026-10-11 批次 4；2026-10-15 上云后改会员级包装档）：
                    // TEMP 根里放「冒烟房淘惠」（有人设）与「冒烟无设」（空人设），
                    // 供 AI 设置页 / 合成页 / 合成队列页三处特判断言用；**全程零真 LLM**（没人执行优化命令）。
                    // 双断言（TEMP 根 + 会员目录）；会员档与旧档都清 —— 上轮残留的旧档会被「一次性采纳」
                    // 灌进夹具，打乱行数断言（与分组/六档段同一条纪律）。
                    var roleStore = Services.GetRequiredService<ComposeRoleStore>();
                    if (!roleStore.GetConfigPath().Contains("creatflow-smoke-roles", StringComparison.OrdinalIgnoreCase)
                        || !roleStore.GetConfigPath().Contains(@"\accounts\", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            "[smoke] 角色存储没走 TEMP 根/会员目录（会碰用户文件）：" + roleStore.GetConfigPath());
                    try { File.Delete(roleStore.GetConfigPath()); } catch { }
                    try { File.Delete(roleStore.GetLegacyPath()); } catch { }
                    var smokeDefaults = (await roleStore.LoadAsync()).Roles;
                    if (smokeDefaults.Count != 2)
                        throw new InvalidOperationException($"[smoke] 内置角色示例不是 2 条：{smokeDefaults.Count}");
                    await roleStore.SaveAsync(new ComposeRolesData
                    {
                        Roles =
                        [
                            .. smokeDefaults,
                            new ComposeRole
                            {
                                Id = "smoke-role",
                                Name = "冒烟房淘惠",
                                Template = "您好，我是冒烟。{内容}。欢迎私信。",
                                Persona = "你是冒烟测试主播，用第一人称说话。",
                            },
                            new ComposeRole { Id = "smoke-nopersona", Name = "冒烟无设", Template = "{内容}", Persona = "" },
                        ],
                    });
                    Console.WriteLine("[smoke] 角色库：TEMP 造景 4 条（内置 2 + 冒烟 2）OK");

                    // 账号勾选 + 自动发布开关存档（发布页 / 合成页共享）：必须同样走 TEMP 根 ——
                    // 冒烟会在合成页切开关造景，不能碰用户的 account-selection.json。
                    // 批次 7 起会员级（accounts/{会员}/ 下），双断言；会员档 + 旧档都要清
                    var selStore = Services.GetRequiredService<AccountSelectionStore>();
                    if (!selStore.GetConfigPath().Contains("creatflow-smoke-selection", StringComparison.OrdinalIgnoreCase)
                        || !selStore.GetConfigPath().Contains(@"\accounts\", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            "[smoke] 勾选存档没走 TEMP 根/会员目录（会碰用户文件）：" + selStore.GetConfigPath());
                    try { File.Delete(selStore.GetConfigPath()); } catch { }
                    try { File.Delete(selStore.GetLegacyPath()); } catch { }
                    Console.WriteLine("[smoke] 勾选存档：TEMP 根 OK");

                    // 账号排序 + 发布预设/草稿（批次 7 新入 TEMP 根；发布页进页会真裁剪/落盘写盘，
                    // 此前未换根是一处会碰用户档的隐患）：双断言 + 会员档与旧档都清
                    var orderStore = Services.GetRequiredService<AccountOrderStore>();
                    if (!orderStore.GetConfigPath().Contains("creatflow-smoke-order", StringComparison.OrdinalIgnoreCase)
                        || !orderStore.GetConfigPath().Contains(@"\accounts\", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            "[smoke] 账号排序没走 TEMP 根/会员目录：" + orderStore.GetConfigPath());
                    try { File.Delete(orderStore.GetConfigPath()); } catch { }
                    try { File.Delete(orderStore.GetLegacyPath()); } catch { }
                    var pubStore = Services.GetRequiredService<PublishDraftStore>();
                    if (!pubStore.GetConfigPath().Contains("creatflow-smoke-publish", StringComparison.OrdinalIgnoreCase)
                        || !pubStore.GetConfigPath().Contains(@"\accounts\", StringComparison.OrdinalIgnoreCase)
                        || !pubStore.GetDraftsPath().Contains("creatflow-smoke-publish", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            "[smoke] 发布预设/草稿没走 TEMP 根/会员目录：" + pubStore.GetConfigPath());
                    try { File.Delete(pubStore.GetConfigPath()); } catch { }
                    try { File.Delete(pubStore.GetLegacyPath()); } catch { }
                    try { File.Delete(pubStore.GetDraftsPath()); } catch { }
                    try { File.Delete(pubStore.GetLegacyDraftsPath()); } catch { }
                    Console.WriteLine("[smoke] 排序 / 发布预设与草稿：TEMP 根 + 会员目录 OK");

                    // 账号分组存档 + 日志设置/清理（2026-10-14 两项新功能）：同样必须走 TEMP 根 ——
                    // 冒烟会造分组景、真执行一次日志清理，绝不能碰用户文件
                    var groupStore = Services.GetRequiredService<AccountGroupStore>();
                    if (!groupStore.GetConfigPath().Contains("creatflow-smoke-groups", StringComparison.OrdinalIgnoreCase)
                        || !groupStore.GetConfigPath().Contains(@"\accounts\", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            "[smoke] 分组存档没走 TEMP 根/会员目录（会碰用户文件）：" + groupStore.GetConfigPath());
                    // 会员档 + 旧机器级档都要清（旧档残留会被「一次性采纳」灌进夹具，打乱行数断言）
                    try { File.Delete(groupStore.GetConfigPath()); } catch { }
                    try { File.Delete(groupStore.GetLegacyPath()); } catch { }
                    var logSettingsStore = Services.GetRequiredService<LogSettingsStore>();
                    if (!logSettingsStore.GetConfigPath().Contains("creatflow-smoke-logsettings", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            "[smoke] 日志设置没走 TEMP 根（会碰用户文件）：" + logSettingsStore.GetConfigPath());
                    try { File.Delete(logSettingsStore.GetConfigPath()); } catch { }   // 防上轮残留让命令早退
                    var cleanupSvc = Services.GetRequiredService<LogCleanupService>();
                    if (!cleanupSvc.LogsDir.Contains("creatflow-smoke-logs", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            "[smoke] 日志清理没走 TEMP 根（会碰用户日志）：" + cleanupSvc.LogsDir);

                    // 上传提示音音量（2026-10-22）：会员级 + 冒烟 TEMP 根（账号页会改档造景）
                    var sndStore = Services.GetRequiredService<SoundSettingsStore>();
                    var sndPath = sndStore.GetConfigPath(sndStore.CurrentMemberId);
                    if (!sndPath.Contains("creatflow-smoke-sound", StringComparison.OrdinalIgnoreCase)
                        || !sndPath.Contains(@"\accounts\", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            "[smoke] 提示音设置没走 TEMP 根/会员目录（会碰用户文件）：" + sndPath);
                    try { File.Delete(sndPath); } catch { }   // 防上轮残留打乱默认档断言
                    Console.WriteLine("[smoke] 分组存档 / 日志设置与清理 / 提示音设置：TEMP 根 OK");

                    // 两个队列存储（批次 7 起会员级）：TEMP 根 + 会员目录双断言；会员/旧档都清
                    //（旧档残留会被「一次性采纳」灌进夹具，打乱队列行数断言，与分组段同一条纪律）
                    var cqStore = Services.GetRequiredService<ComposeQueueStore>();
                    if (!cqStore.GetConfigPath().Contains("creatflow-smoke-queue", StringComparison.OrdinalIgnoreCase)
                        || !cqStore.GetConfigPath().Contains(@"\accounts\", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            "[smoke] 合成队列没走 TEMP 根/会员目录（会碰用户队列）：" + cqStore.GetConfigPath());
                    try { File.Delete(cqStore.GetConfigPath()); } catch { }
                    try { File.Delete(cqStore.GetLegacyPath()); } catch { }
                    var tqStore = Services.GetRequiredService<TtsQueueStore>();
                    if (!tqStore.GetConfigPath().Contains("creatflow-smoke-ttsqueue", StringComparison.OrdinalIgnoreCase)
                        || !tqStore.GetConfigPath().Contains(@"\accounts\", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            "[smoke] 配音队列没走 TEMP 根/会员目录（会碰用户队列）：" + tqStore.GetConfigPath());
                    try { File.Delete(tqStore.GetConfigPath()); } catch { }
                    try { File.Delete(tqStore.GetLegacyPath()); } catch { }

                    // 下载队列（2026-10-21）：同规矩 —— TEMP 根 + 会员目录 + 消费者被 SmokeMode 闸住
                    var dlStore = Services.GetRequiredService<DownloadQueueStore>();
                    if (!dlStore.GetConfigPath().Contains("creatflow-smoke-download", StringComparison.OrdinalIgnoreCase)
                        || !dlStore.GetConfigPath().Contains(@"\accounts\", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            "[smoke] 下载队列没走 TEMP 根/会员目录（会碰用户队列）：" + dlStore.GetConfigPath());
                    try { File.Delete(dlStore.GetConfigPath()); } catch { }
                    try { File.Delete(dlStore.GetLegacyPath()); } catch { }

                    var dlVm = Services.GetRequiredService<DownloadQueueViewModel>();
                    if (dlVm.IsConsumerStarted)
                        throw new InvalidOperationException("[smoke] 下载消费者在冒烟里启动了（会真发网络请求）");
                    Console.WriteLine("[smoke] 三个队列存储：TEMP 根 + 会员目录 OK（下载消费者未启动）");

                    // ── 单机版（2026-10-28）：本地库 + 导航过滤 + 本地路由真跑 ──
                    // 冒烟是 TEMP 库，绝不能碰用户 creatflow.db；导航里不该出现
                    // 激活 / 设备 / 公告 / 钉钉（它们没有单机版含义）
                    if (AppConfig.IsStandalone)
                    {
                        var localDb = LocalStorePaths.DatabasePath;
                        if (!localDb.Contains("creatflow-smoke-localstore", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException(
                                "[smoke] 单机版数据库没走 TEMP 根（会碰用户数据）：" + localDb);
                        if (!File.Exists(localDb))
                            throw new InvalidOperationException("[smoke] 单机版数据库未建库：" + localDb);

                        var navKeys = shell.Sections.SelectMany(s => s.Items).Select(i => i.Key).ToList();
                        var leaked = navKeys.Where(k => k is "activate" or "device" or "announce" or "dingtalk").ToList();
                        if (leaked.Count > 0)
                            throw new InvalidOperationException(
                                "[smoke] 单机版导航仍含服务端专属项：" + string.Join(",", leaked));

                        var platforms = ApiClient.GetAsync<List<PlatformConfigItem>>("/api/config/platforms")
                            .GetAwaiter().GetResult();
                        if (platforms is not { Count: 4 })
                            throw new InvalidOperationException(
                                $"[smoke] 本地平台配置不是 4 条：{platforms?.Count}");

                        var standaloneAccountNav = shell.Sections.SelectMany(s => s.Items).First(i => i.Key == "account");
                        shell.Navigate(standaloneAccountNav);
                        await Task.Delay(300);
                        if (shell.CurrentPage is AccountViewModel av && (av.ShowMembership || av.ShowLogout))
                            throw new InvalidOperationException(
                                "[smoke] 单机版账号页仍显示会员区块或退出登录");

                        Console.WriteLine($"[smoke] 单机版：TEMP 本地库 {Path.GetFileName(localDb)} + "
                                          + "导航过滤（无激活/设备/公告/钉钉）+ 平台配置 4 条 + 账号页无会员区块 OK");
                    }

                    var pages = shell.Sections.SelectMany(s => s.Items)
                        .Where(i => i.PageType != null).ToArray();
                    foreach (var page in pages)
                    {
                        shell.Navigate(page);
                        await Task.Delay(350); // 等页面实例化渲染
                    }
                    // 视频下载页：冒烟闸必须挡住真解析（否则会带着夹具去请求真实平台），
                    // 路径预览是纯函数 —— 页面建好就该有值
                    if (pages.FirstOrDefault(i => i.Key == "download") is { } download)
                    {
                        shell.Navigate(download);
                        await Task.Delay(300);
                        if (shell.CurrentPage is DownloadQueueViewModel dv)
                        {
                            dv.InputText = "https://www.douyin.com/video/741234567890";
                            await dv.ParseCommand.ExecuteAsync(null);
                            if (dv.Tasks.Count != 0)
                                throw new InvalidOperationException("[smoke] 下载页在冒烟里发起了真实解析");
                            if (string.IsNullOrWhiteSpace(dv.PathPreview))
                                throw new InvalidOperationException("[smoke] 下载页路径预览为空（模板渲染没接上）");
                            Console.WriteLine("[smoke] 视频下载：解析被冒烟闸拦下、路径预览非空");
                        }
                    }
                    // 合集页编辑器视图特判：默认只在列表态渲染，打开一次编辑器暴露表单/平台卡模板错误
                    if (pages.FirstOrDefault(i => i.Key == "collection") is { } collection)
                    {
                        shell.Navigate(collection);
                        await Task.Delay(300);
                        if (shell.CurrentPage is CollectionViewModel cv)
                        {
                            await cv.ShowCreateDialogCommand.ExecuteAsync(null);
                            await Task.Delay(600); // 表单 + 平台目标渲染（无网络时走失败文案，不崩）

                            // 合集标题规则（2026-10-22：最多 20 字、不能含符号）——
                            // 坏标题在 Save 的网络调用之前就返回，零请求
                            cv.CollectionTitle = "冒烟合集#1";
                            await Task.Delay(150);
                            if (!cv.HasTitleHint || !cv.TitleHintText.Contains("符号"))
                                throw new InvalidOperationException(
                                    "[smoke] 合集标题符号提示没出来：" + cv.TitleHintText);
                            await cv.SaveCommand.ExecuteAsync(null);
                            if (!cv.FormError.Contains("符号"))
                                throw new InvalidOperationException(
                                    "[smoke] 合集标题带符号没拦住：" + cv.FormError);
                            cv.CollectionTitle = "冒烟合集";
                            if (cv.HasTitleHint)
                                throw new InvalidOperationException(
                                    "[smoke] 合法标题仍有提示：" + cv.TitleHintText);
                            Console.WriteLine("[smoke] 合集页：标题符号拦截 + 实时提示 OK（零请求）");

                            // ── 合集类型（2026-11-05）：新建默认视频；文章 → 仅头条可选；
                            //    切回视频 → 全部恢复。只走本地状态，零请求 ──
                            if (!cv.IsVideoCollection || cv.IsArticleCollection
                                || cv.IsContentTypeLocked || !cv.ShowContentTypePicker)
                                throw new InvalidOperationException(
                                    "[smoke] 新建合集默认不是「视频合集」");
                            cv.SetArticleTypeCommand.Execute(null);
                            if (!cv.IsArticleCollection || cv.IsVideoCollection
                                || cv.IsContentTypeLocked || !cv.ShowContentTypePicker
                                || !cv.ContentTypeLabel.Contains("文章"))
                                throw new InvalidOperationException(
                                    "[smoke] 切文章合集后类型状态不对");
                            if (cv.PlatformTargets.Count > 0)
                            {
                                var nonTt = cv.PlatformTargets
                                    .Where(t => t.PlatformKey != PlatformNames.Toutiao).ToList();
                                if (nonTt.Any(t => t.IsEnabled || t.IsSelected))
                                    throw new InvalidOperationException(
                                        "[smoke] 文章合集下仍有非头条平台可选");
                                var tt = cv.PlatformTargets
                                    .FirstOrDefault(t => t.PlatformKey == PlatformNames.Toutiao);
                                if (tt is not null && (!tt.IsEnabled || !tt.IsSelected))
                                    throw new InvalidOperationException(
                                        "[smoke] 文章合集没锁定头条平台");
                            }
                            cv.SetVideoTypeCommand.Execute(null);
                            if (!cv.IsVideoCollection
                                || cv.PlatformTargets.Any(t => !t.IsEnabled))
                                throw new InvalidOperationException(
                                    "[smoke] 切回视频合集后平台没恢复可选");

                            // 两个类型胶囊必须真的接到命令（只验 VM 状态不够 —— 绑定接错时
                            // 页面上的按钮点了没反应，与「类型切换失效」同观感）
                            var typeCmds = target.GetVisualDescendants().OfType<Button>()
                                .Select(b => b.Command).ToList();
                            if (!typeCmds.Any(c => ReferenceEquals(c, cv.SetArticleTypeCommand))
                                || !typeCmds.Any(c => ReferenceEquals(c, cv.SetVideoTypeCommand)))
                                throw new InvalidOperationException(
                                    "[smoke] 视频/文章类型按钮没接上命令");
                            Console.WriteLine("[smoke] 合集页：视频/文章类型切换 + 平台禁用/恢复 + 按钮接线 OK（零请求）");

                            cv.BackToListCommand.Execute(null);
                            await Task.Delay(200);

                            // 造景一张「删除未完成」卡（无服务端时列表是空的）：警示条文案 + 去处理接线。
                            // 只查按钮命令，**不点「去处理」**（真跳页会打断后续巡检）
                            var fakeCard = new CollectionCard(
                                new CollectionItem(Guid.NewGuid(), "冒烟合集卡", null, null, null,
                                    0, 0, DateTime.Now, null, null),
                                new Dictionary<string, PlatformLookup>(),
                                new Dictionary<string, IReadOnlyList<Guid>>(),
                                new Dictionary<Guid, string>(),
                                new Dictionary<string, bool>());
                            fakeCard.FailedDeleteCount = 2;
                            cv.Cards.Add(fakeCard);
                            // 强制播报一次 HasCollections：卡片面板 IsVisible 绑着它，离线（没有服务端、
                            // 列表此前为空）时面板是收起的 —— Avalonia 不给折叠容器实例化项模板，
                            // 「去处理」按钮就进不了可视树（上一轮只在连着服务端时验过，离线补这一笔）
                            cv.IsLoading = true;
                            cv.IsLoading = false;
                            await Task.Delay(300);
                            if (!fakeCard.HasDeleteFailure
                                || !fakeCard.DeleteFailureText.Contains("2 个账号", StringComparison.Ordinal))
                                throw new InvalidOperationException(
                                    "[smoke] 删除未完成警示条文案不对：" + fakeCard.DeleteFailureText);
                            if (cv.OnNavigateRequested is null)
                                throw new InvalidOperationException("[smoke] 合集页没拿到 OnNavigateRequested（壳没注入）");
                            var cvCmds = target.GetVisualDescendants().OfType<Button>().Select(b => b.Command).ToList();
                            if (!cvCmds.Any(c => ReferenceEquals(c, cv.GoTasksCommand)))
                                throw new InvalidOperationException("[smoke] 「去处理」按钮没接上命令");
                            cv.Cards.Remove(fakeCard);
                            Console.WriteLine("[smoke] 合集页：删除未完成警示条（文案 + 去处理接线）OK");
                        }
                    }
                    // 异步任务页特判：明细行是折叠的（默认态渲染不到），展开一条把模板带出来。
                    // 页面上只有「重试失败项 / 删除记录」—— 都不会触发真实的平台删除
                    //（真干活的是消费者，冒烟里它压根没启动）。
                    if (pages.FirstOrDefault(i => i.Key == "tasks") is { } tasksPage)
                    {
                        shell.Navigate(tasksPage);
                        await Task.Delay(1200);   // 等列表拉回来 + 卡片模板渲染
                        if (shell.CurrentPage is AsyncTaskViewModel tv)
                        {
                            var first = tv.Tasks.FirstOrDefault();
                            if (first is not null)
                            {
                                // 展开一条：子项行默认折叠，不展开模板就渲染不到。
                                // 这条断言守着「展开还活着」—— 2026-09-22 之前三角挂过命令，
                                // 与 TwoWay 绑定各翻一次、互相抵消，点上去毫无反应（页面看着像死的）；
                                // 拉明细挂在 IsExpanded 的变化上，这里置 true 就等于「点了三角」。
                                first.IsExpanded = true;
                                await Task.Delay(800);   // 明细拉取 + 子项行渲染
                                if (!first.HasItems)
                                    throw new InvalidOperationException("异步任务展开后没拉到明细");
                                Console.WriteLine($"[smoke] 异步任务展开：明细 {first.Items.Count} 行");

                                // 站够一个 5 秒轮询周期：**刷新不得把展开态与明细刷掉**。
                                // 老写法是 Clear + 重加（行对象换新）→ 三角自己合上、明细消失，
                                // 用户看到的就是「页面一直在刷、三角点不开」（2026-09-22 报的问题）
                                await Task.Delay(6000);
                                if (!first.IsExpanded || !first.HasItems || !tv.Tasks.Contains(first))
                                    throw new InvalidOperationException(
                                        "5 秒轮询把展开态刷掉了：" +
                                        $"IsExpanded={first.IsExpanded} 明细={first.Items.Count} " +
                                        $"仍在列表={tv.Tasks.Contains(first)}");
                                Console.WriteLine("[smoke] 5 秒轮询后展开态还在（行对象被复用、没重建）");
                            }
                            Console.WriteLine($"[smoke] 异步任务：{tv.Tasks.Count} 条，" +
                                              $"空态={tv.IsEmpty} 错误=" +
                                              (tv.HasError ? tv.Error : "(无)"));

                            // ── 造景两条（无服务端时列表空）：① 失败子项的「已在平台删除 → 确认清理」
                            // 确认条；② 有失败项任务的「删除记录」确认条。全部只走本地状态机，
                            // **零请求**（真点「确认清理记录」「仍要删除」会打服务端；冒烟只打开、取消）
                            var fakeTask = new DeleteTaskRow { Id = Guid.NewGuid(), CreatedAt = DateTime.Now };
                            fakeTask.Apply(new ServerAsyncTask(fakeTask.Id,
                                AsyncTaskFormat.KindCollectionDelete, "删除合集", "冒烟合集",
                                3, "部分失败", 4, 4, 1, DateTime.Now, null));
                            var fakeItem = DeleteTaskItemRow.From(new ServerAsyncTaskItem(
                                Guid.NewGuid(), fakeTask.Id, Guid.NewGuid(),
                                AsyncTaskFormat.TargetCollectionPlatform, Guid.NewGuid(),
                                "kuaishou", "快手", Guid.NewGuid(), "冒烟账号", "264379443",
                                (int)TaskItemStatus.PlatformFailed,
                                "平台删除失败（可能已不存在），记录已保留，可重试",
                                "Kuaishou /rest/cp/works/v2/collection/delete: null data in response",
                                0, null, DateTime.Now), new Dictionary<string, string>());
                            fakeTask.Items.Add(fakeItem);
                            fakeTask.NotifyItemsChanged();
                            fakeTask.IsExpanded = true;   // 明细默认折叠，展开才能把子项模板带出来
                            tv.Tasks.Add(fakeTask);
                            // 等模板渲染（展开还会去拉一次明细，无服务端时报错静默）。
                            // **在等待循环里就地快照，断言用这份快照** —— 页面每 5 秒刷一次，
                            // 「先等、后取」之间有个「刚在、取时没了」的窗口（2026-10-26 门禁偶发 1/14）。
                            if (!fakeItem.CanConfirmGone || !fakeItem.CanRetry)
                                throw new InvalidOperationException(
                                    "[smoke] 失败子项没有「已在平台删除」入口（或丢了「重试」）");
                            var expectCmds = new (System.Windows.Input.ICommand? Cmd, string Label)[]
                            {
                                (tv.ConfirmGoneBeginCommand, "已在平台删除"),
                                (tv.ConfirmGoneCommand, "确认清理记录"),
                                (tv.ConfirmGoneCancelCommand, "确认条-取消"),
                                (tv.CancelDeleteTaskCommand, "删记录-取消"),
                                (tv.DeleteTaskCommand, "删除记录"),
                            };
                            List<System.Windows.Input.ICommand?> tCmds = [];
                            await WaitVisualAsync(() =>
                            {
                                tCmds = target.GetVisualDescendants().OfType<Button>()
                                    .Select(b => b.Command).ToList();
                                return expectCmds.All(e => e.Cmd is null
                                    || tCmds.Any(c => ReferenceEquals(c, e.Cmd)));
                            });
                            foreach (var (cmd, label) in expectCmds)
                            {
                                if (cmd is not null && !tCmds.Any(c => ReferenceEquals(c, cmd)))
                                    throw new InvalidOperationException($"[smoke] 异步任务「{label}」按钮没接上命令");
                            }
                            tv.ConfirmGoneBeginCommand.Execute(fakeItem);
                            if (!fakeItem.IsConfirmingGone)
                                throw new InvalidOperationException("[smoke] 「已在平台删除」没摆出确认条");
                            tv.ConfirmGoneCancelCommand.Execute(fakeItem);
                            if (fakeItem.IsConfirmingGone)
                                throw new InvalidOperationException("[smoke] 确认条「取消」没复位");

                            tv.DeleteTaskCommand.Execute(fakeTask);   // 有失败项 → 只摆条，不发请求
                            if (!fakeTask.IsConfirmingDelete)
                                throw new InvalidOperationException(
                                    "[smoke] 有失败项的任务点「删除记录」没先摆确认条");
                            tv.CancelDeleteTaskCommand.Execute(fakeTask);
                            if (fakeTask.IsConfirmingDelete)
                                throw new InvalidOperationException("[smoke] 删记录确认条「取消」没复位");
                            tv.Tasks.Remove(fakeTask);
                            Console.WriteLine("[smoke] 异步任务：确认已删除条 + 删记录确认条（接线 + 取消复位，零请求）OK");
                        }
                    }

                    // 视频管理页特判：确认条、勾选盒、批量工具条默认都不在默认态（页面渲染不到它们）。
                    // **只展开不确认** —— 冒烟里点一次「确认删除」就是真删用户的作品。
                    if (pages.FirstOrDefault(i => i.Key == "video") is { } video)
                    {
                        shell.Navigate(video);
                        await Task.Delay(300);
                        if (shell.CurrentPage is VideoManageViewModel vv)
                        {
                            // 每页条数芯片的 CommandParameter 是 int（PageSizeChip.Size）。
                            // CommunityToolkit 的 RelayCommand<T> 不做类型转换：命令参数写成 string?
                            // 时 CanExecute(int) 直接抛 ArgumentException，点「50 / 100」永远没反应
                            // （2026-11-04 修过一次，这里钉住）
                            try
                            {
                                if (!vv.SetPageSizeCommand.CanExecute(50))
                                    throw new InvalidOperationException("[smoke] 视频页「每页 50」命令不可用");
                            }
                            catch (ArgumentException ex)
                            {
                                throw new InvalidOperationException(
                                    "[smoke] 视频页每页条数命令不认 int 参数（CommandParameter 类型不匹配）", ex);
                            }

                            var row = vv.Rows.FirstOrDefault(r => r.IsRecord);
                            if (row is not null)
                            {
                                // 多选：勾选盒的 :checked 态 + 工具条（「删除选中」只在有勾选时才活）
                                row.IsSelected = true;
                                await Task.Delay(250);
                                vv.SelectAllOnPageCommand.Execute(null);
                                await Task.Delay(200);
                                vv.ClearSelectionCommand.Execute(null);
                                await Task.Delay(150);

                                // 勾选态必须扛得住重查（后台删除期间每 4 秒重查一次走的就是这条路）：
                                // 老写法 Clear + 重加会换掉行对象，勾好的几条每 4 秒被清一次（2026-09-22 实测）
                                row.IsSelected = true;
                                await Task.Delay(200);
                                await vv.RefreshCommand.ExecuteAsync(null);
                                await Task.Delay(600);
                                if (!vv.Rows.Contains(row) || !row.IsSelected)
                                    throw new InvalidOperationException(
                                        "视频页重查后勾选态丢了：" +
                                        $"仍在列表={vv.Rows.Contains(row)} 勾选={row.IsSelected}");
                                Console.WriteLine("[smoke] 视频页重查后勾选态还在（行对象被复用）");

                                vv.DeleteBeginCommand.Execute(row);
                                await Task.Delay(300); // 确认条 + 两个按钮渲染
                                vv.DeleteCancelCommand.Execute(row);
                                await Task.Delay(150);

                                // 「全部副本」那条（跨页删除用的，与单条确认条互斥）
                                await vv.DeleteWholeVideoCommand.ExecuteAsync(row);
                                await Task.Delay(400);
                                vv.DeleteCancelCommand.Execute(row);
                                await Task.Delay(150);
                            }
                        }
                    }
                    // 上传队列页特判：明细行（行级 ✕ 放弃在模板里）与撤回确认条都不在默认态（卡片默认折叠）。
                    // **只展开、只开关确认条、立即取消** —— 冒烟里点一次「确认撤回」就是真删队列记录；
                    // 空态/未登录时整段跳过（跟视频页一样按数据有无决定深度）。
                    // 消费者在冒烟里压根没启动（UploadQueueViewModel.Start 有 SmokeMode 闸），队列是静止的。
                    if (pages.FirstOrDefault(i => i.Key == "queue") is { } queue)
                    {
                        shell.Navigate(queue);
                        await Task.Delay(1500);   // 等服务端队列拉回 + 卡片渲染（无网络走空态，不崩）

                        // 汇总条按钮**与队列数据无关**（空队列也渲染）—— 「删除选中」在这里单独验接线，
                        // 无网络/空队列时也能兜住（「暂停选中 / 继续选中」只有有卡时才验，见下）
                        if (shell.CurrentPage is UploadQueueViewModel qvEmpty)
                        {
                            // 2026-10-22：开壳不再自动上传 —— 消费者的唯一入口是「开始上传」
                            if (qvEmpty.UploadStarted)
                                throw new InvalidOperationException(
                                    "[smoke] 上传队列在用户点「开始上传」前就启动了");
                            if (!target.GetVisualDescendants().OfType<Button>()
                                .Any(b => ReferenceEquals(b.Command, qvEmpty.StartUploadCommand)))
                                throw new InvalidOperationException(
                                    "[smoke] 队列「开始上传」按钮没接上命令");

                            if (!target.GetVisualDescendants().OfType<Button>()
                                .Any(b => ReferenceEquals(b.Command, qvEmpty.DeleteSelectedCommand)))
                                throw new InvalidOperationException(
                                    "[smoke] 队列汇总条的「删除选中」按钮没接上命令");
                            Console.WriteLine("[smoke] 队列：默认不自动上传 + 「开始上传/删除选中」接线 OK（空队列也验）");
                        }

                        if (shell.CurrentPage is UploadQueueViewModel qv
                            && qv.Tasks.FirstOrDefault() is { } qcard)
                        {
                            qcard.IsExpanded = true;   // 明细行默认折叠，不展开模板就渲染不到
                            await Task.Delay(300);

                            // 编译绑定保证属性路径存在，但 $parent[UserControl] 的**运行时强转**
                            // 接不上时命令是 null、按钮静默失灵（发布页踩过）—— 按命令实例找按钮，找不到就报
                            if (qcard.HasUnpublished
                                && !target.GetVisualDescendants().OfType<Button>()
                                    .Any(b => ReferenceEquals(b.Command, qv.DiscardRowCommand)))
                                throw new InvalidOperationException("[smoke] 队列明细里的「✕ 放弃」按钮没接上命令");

                            if (qcard.ShowWithdrawAsk)
                            {
                                qv.AskWithdrawTaskCommand.Execute(qcard);
                                await Task.Delay(250);
                                if (!qcard.ShowWithdrawConfirm)
                                    throw new InvalidOperationException("[smoke] 点撤回后确认条没出来");
                                if (!target.GetVisualDescendants().OfType<Button>()
                                    .Any(b => ReferenceEquals(b.Command, qv.ConfirmWithdrawTaskCommand)))
                                    throw new InvalidOperationException("[smoke] 撤回确认条里的按钮没接上命令");
                                qv.CancelWithdrawTaskCommand.Execute(qcard);
                                await Task.Delay(150);
                            }

                            // ── 新增按钮接线（$parent[UserControl] 运行时强转接不上时命令是 null、
                            //    按钮静默失灵）—— 按命令实例找按钮，找不到就报 ──
                            {
                                var btns = target.GetVisualDescendants().OfType<Button>().ToList();
                                void RequireBtn(System.Windows.Input.ICommand? cmd, string label)
                                {
                                    if (cmd is not null && !btns.Any(b => ReferenceEquals(b.Command, cmd)))
                                        throw new InvalidOperationException($"[smoke] 队列「{label}」按钮没接上命令");
                                }
                                RequireBtn(qv.PauseRowCommand, "暂停");
                                RequireBtn(qv.ResumeRowCommand, "继续");
                                RequireBtn(qv.ToggleErrorDetailCommand, "详情");
                                RequireBtn(qv.CopyErrorCommand, "复制错误");
                                RequireBtn(qv.PauseSelectedCommand, "暂停选中");
                                RequireBtn(qv.ResumeSelectedCommand, "继续选中");
                                RequireBtn(qv.DeleteSelectedCommand, "删除选中");
                            }

                            // 行级暂停/继续往返（消费者在冒烟里被闸住，队列静止；Paused/Waiting 都是本地态，不碰服务端）
                            if (qcard.Rows.FirstOrDefault(r => r.ShowPauseButton) is { } probeRow)
                            {
                                qv.PauseRowCommand.Execute(probeRow);
                                await Task.Delay(120);
                                var pausedOk = probeRow.State == QueueRowState.Paused && probeRow.ShowResumeButton;
                                qv.ResumeRowCommand.Execute(probeRow);
                                await Task.Delay(120);
                                var resumedOk = probeRow.State == QueueRowState.Waiting;
                                if (!pausedOk || !resumedOk)
                                    throw new InvalidOperationException(
                                        $"[smoke] 行级暂停/继续往返失败：暂停={pausedOk} 继续={resumedOk}");
                                Console.WriteLine("[smoke] 队列行级暂停/继续往返 OK");
                            }

                            // 勾选与全选联动（走完清干净，不留勾选态）
                            if (qcard.Rows.Count > 0)
                            {
                                qv.AllSelected = true;
                                await Task.Delay(120);
                                var allOn = qcard.Rows.All(r => r.IsSelected);
                                qcard.Rows[0].IsSelected = false;
                                await Task.Delay(120);
                                var fellBack = !qv.AllSelected;
                                qv.AllSelected = false;
                                if (!allOn || !fellBack)
                                    throw new InvalidOperationException(
                                        $"[smoke] 勾选/全选联动失败：全选={allOn} 取消一行后全选态={fellBack}");
                                Console.WriteLine("[smoke] 队列勾选/全选联动 OK");
                            }

                            Console.WriteLine($"[smoke] 队列页：{qv.Tasks.Count} 张卡，" +
                                              $"未发布行={qcard.UnpublishedCount} 撤回按钮={qcard.ShowWithdrawAsk}，" +
                                              $"隐藏按钮={qcard.HasPublished} 已展开={qcard.IsExpanded}，" +
                                              $"窗口未打开横幅={qv.HasVerifyBanner}");
                        }
                    }

                    // 壳底状态栏中列「今日发布 x/y」（队列页 RefreshStats → OnStatsChanged 推送）——
                    // 没有任务时是「今日发布 —」，但绝不能是空串（接线断了就是这个症状）
                    Console.WriteLine($"[smoke] 壳底状态栏：「{shell.TodayPublishText}」");
                    if (!shell.TodayPublishText.StartsWith("今日发布", StringComparison.Ordinal))
                        throw new InvalidOperationException("[smoke] 壳底「今日发布」文案为空或不对：" + shell.TodayPublishText);

                    // 发布页特判：「账号窗口未打开」提示条是数据相关的（未验证账号 × 已勾选草稿）——
                    // 有就点一次「去平台账号」把跳页链路走通（纯导航，不代开窗口），没有就只打印状态
                    if (pages.FirstOrDefault(i => i.Key == "publish") is { } publish)
                    {
                        shell.Navigate(publish);
                        await Task.Delay(1000);   // 等草稿恢复 + 账号拉回（提示条依赖两者）
                        if (shell.CurrentPage is PublishViewModel pv)
                        {
                            Console.WriteLine($"[smoke] 发布页：草稿 {pv.Drafts.Count} 条，" +
                                              $"窗口未打开提示={pv.HasVerifyPrompt}");

                            // ── 转载作品选择器接线 + 定时默认落点（都在编辑器视图里；改的是内存态，
                            //    不点保存不落盘，收尾还原）。**不执行选择器命令** —— 那会真发凭证请求 ──
                            if (pv.Drafts.FirstOrDefault() is { } draft0)
                            {
                                var priorKind = draft0.Declaration.Kind;
                                var priorInside = draft0.Declaration.DouyinRepostFromInside;
                                var wasScheduled = draft0.IsScheduled;
                                var priorAt = draft0.ScheduledAt;

                                pv.EditDraftCommand.Execute(draft0);
                                await Task.Delay(300);
                                pv.SelectDeclarationCommand.Execute(CreatFlow.NewApp.Models.DeclarationKind.Repost);
                                pv.DouyinRepostFromInside = true;
                                await Task.Delay(200);
                                if (!pv.ShowDouyinRepostItemPicker)
                                    throw new InvalidOperationException("[smoke] 转载=抖音作品时「选择我的作品」按钮没出现");
                                if (!target.GetVisualDescendants().OfType<Button>()
                                        .Any(b => ReferenceEquals(b.Command, pv.PickDouyinRepostItemCommand)))
                                    throw new InvalidOperationException("[smoke] 发布页「选择我的作品…」没接上命令");

                                pv.SelectScheduleModeCommand.Execute(true);
                                await Task.Delay(150);
                                var sched = pv.SelectedDraft?.ScheduledAt;
                                if (priorAt is null && (sched is null || sched < DateTime.Now.AddHours(2)
                                    || sched.Value.Minute != 0 || sched.Value.Second != 0))
                                    throw new InvalidOperationException($"[smoke] 定时默认落点不对：{sched}");
                                Console.WriteLine($"[smoke] 发布页：作品选择器接线 OK；定时默认 {sched:HH:mm}（≥now+2h）");

                                // 收尾还原（别给后续目检留一个被改过的编辑器）
                                pv.SelectScheduleModeCommand.Execute(wasScheduled);
                                draft0.ScheduledAt = priorAt;
                                pv.DouyinRepostFromInside = priorInside;
                                pv.SelectDeclarationCommand.Execute(priorKind);
                                pv.BackToListCommand.Execute(null);
                                await Task.Delay(200);

                                // 「待发布 N 条」右侧全选（2026-10-22）：全勾 → 取消一条回落 → 还原原勾选
                                var priorSelection = pv.Drafts.Select(d => d.IsSelected).ToList();
                                pv.AllDraftsSelected = true;
                                await Task.Delay(150);
                                var allOn = pv.Drafts.All(d => d.IsSelected);
                                pv.Drafts[0].IsSelected = false;
                                await Task.Delay(150);
                                var fellBack = !pv.AllDraftsSelected;
                                for (var i = 0; i < pv.Drafts.Count; i++)
                                    pv.Drafts[i].IsSelected = priorSelection[i];
                                await Task.Delay(150);
                                if (!allOn || !fellBack)
                                    throw new InvalidOperationException(
                                        $"[smoke] 发布页全选联动失败：全选={allOn} 取消一条后回退={fellBack}");
                                Console.WriteLine("[smoke] 发布页「全选」联动 OK");
                            }

                            // ── 平台卡「话题」块（2.6.3）：TEMP 造景、**零网络** ──
                            // 验四件事：能力矩阵（抖音可搜 / 视频号无接口说明）、面板接线、
                            // 手动话题增删、旧字段 Topics 的桥接（2.6.5 之前 builder 读它）。
                            // 不动真草稿：造一条只有两个假目标的临时草稿，验完直接退回列表。
                            var topicDraft = new PublishDraft(
                                Path.Combine(Path.GetTempPath(), "smoke-topic.mp4"))
                            {
                                Title = "冒烟话题",
                            };
                            topicDraft.Targets.Add(new PublishTarget(
                                PlatformNames.Douyin, Guid.NewGuid(), "冒烟抖音号"));
                            topicDraft.Targets.Add(new PublishTarget(
                                PlatformNames.WechatVideo, Guid.NewGuid(), "冒烟视频号"));
                            // B站（阶段 4.6）：分区只读展示
                            topicDraft.Targets.Add(new PublishTarget(
                                PlatformNames.Bilibili, Guid.NewGuid(), "冒烟B站号"));
                            // 头条（阶段 5.6）：赞赏 / 长尾
                            topicDraft.Targets.Add(new PublishTarget(
                                PlatformNames.Toutiao, Guid.NewGuid(), "冒烟头条号"));
                            pv.EditDraftCommand.Execute(topicDraft);
                            await Task.Delay(250);

                            var dyRow = pv.TargetRows.FirstOrDefault(r =>
                                r.Target.PlatformKey == PlatformNames.Douyin);
                            var wxRow = pv.TargetRows.FirstOrDefault(r =>
                                r.Target.PlatformKey == PlatformNames.WechatVideo);
                            if (dyRow is null || wxRow is null)
                                throw new InvalidOperationException("[smoke] 话题块：造景目标没进平台卡");

                            var biliRow = pv.TargetRows.FirstOrDefault(r =>
                                r.Target.PlatformKey == PlatformNames.Bilibili);
                            if (biliRow is null || !biliRow.IsBilibili
                                || !biliRow.BilibiliPartitionText.Contains("21 / 1015", StringComparison.Ordinal))
                            {
                                throw new InvalidOperationException(
                                    "[smoke] B站平台卡分区展示不对："
                                    + (biliRow?.BilibiliPartitionText ?? "(没有 B站平台卡)"));
                            }

                            // B站长尾（4.6）：标签解析 + 默认开关 + 写回 Params（零网络）
                            biliRow.BilibiliTagsText = "家居, 室内设计、家装";
                            if (!biliRow.Params.Tags.SequenceEqual(["家居", "室内设计", "家装"]))
                                throw new InvalidOperationException(
                                    "[smoke] B站标签解析不对：" + string.Join("/", biliRow.Params.Tags));
                            if (!biliRow.BilibiliWatermark || !biliRow.BilibiliAllowComment)
                                throw new InvalidOperationException(
                                    "[smoke] B站默认开关不对（水印 / 允许评论默认应为开）");
                            biliRow.BilibiliWatermark = false;
                            biliRow.BilibiliAllowComment = false;
                            if (biliRow.Params.Bilibili?.Watermark != false
                                || biliRow.Params.AllowComment != false)
                                throw new InvalidOperationException("[smoke] B站长尾开关没写回 Params");
                            Console.WriteLine(
                                "[smoke] 发布页：B站卡（分区只读 / 标签解析 / 开关落 Params）OK");

                            // 头条长尾（5.6）：赞赏 / 模板写回 Params（零网络）
                            var ttRow = pv.TargetRows.FirstOrDefault(r =>
                                r.Target.PlatformKey == PlatformNames.Toutiao);
                            if (ttRow is null || !ttRow.IsToutiao)
                                throw new InvalidOperationException("[smoke] 没有头条平台卡");
                            ttRow.ToutiaoPraise = true;
                            ttRow.ToutiaoTemplateText = "模板X";
                            if (ttRow.Params.Praise != true
                                || ttRow.Params.Toutiao?.TemplateName != "模板X")
                                throw new InvalidOperationException(
                                    "[smoke] 头条长尾没写回 Params（赞赏/模板）");
                            Console.WriteLine(
                                "[smoke] 发布页：头条卡（赞赏 / 模板写回 Params）OK");
                            if (!dyRow.TopicSupportsSearch || dyRow.TopicSupportsRecommend || !dyRow.TopicHasApi)
                                throw new InvalidOperationException(
                                    "[smoke] 抖音话题能力不对："
                                    + $"search={dyRow.TopicSupportsSearch} recommend={dyRow.TopicSupportsRecommend} api={dyRow.TopicHasApi}");
                            if (wxRow.TopicHasApi || wxRow.TopicSupportsSearch || wxRow.TopicSupportsRecommend
                                || !wxRow.TopicApiHint.Contains("没有话题列表接口", StringComparison.Ordinal))
                                throw new InvalidOperationException(
                                    "[smoke] 视频号应显示「无话题接口」说明：" + wxRow.TopicApiHint);
                            // 2026-11-06 用户要求：右侧平台卡话题块已删除，只保留左侧全局话题卡。
                            // 这里只验「左卡在、右块没了」；全局卡的手动添加会触发联邦解析（真网络），
                            // 冒烟零网络不点它。
                            if (!target.GetVisualDescendants().OfType<Button>()
                                    .Any(b => ReferenceEquals(b.Command, pv.AddTopicCommand)))
                                throw new InvalidOperationException("[smoke] 左侧话题卡「添加」按钮没接上命令");
                            if (target.GetVisualDescendants().OfType<Button>()
                                    .Any(b => ReferenceEquals(b.Command, pv.ToggleTopicPanelCommand)))
                                throw new InvalidOperationException(
                                    "[smoke] 平台卡话题块应已删除，但仍找到「添加话题」按钮");
                            Console.WriteLine(
                                "[smoke] 发布页：左侧全局话题卡保留 / 右侧平台话题块已删除 OK");

                            // ── 全局话题卡「平台推荐」（2026-11-09 恢复）──
                            // 旧入口随右侧平台卡话题块一起被删，`RecommendTopicSuggestionsCommand`
                            // 成了没有 UI 可达路径的死命令（小红书 / B站的「按标题推荐」等于没接）。
                            // 这里验三件事：行只收有推荐能力的平台（B站/头条进，抖音/视频号不进）、
                            // 「获取推荐」按钮接线、候选选中 → 只写进该账号结构化话题（不灌全局文本），
                            // 芯片可移除。**零网络**：候选直接注入，不执行拉取命令。
                            var biliRecommendRow = pv.RecommendRows.FirstOrDefault(
                                r => r.Target.PlatformKey == PlatformNames.Bilibili);
                            if (biliRecommendRow is null
                                || pv.RecommendRows.Any(r => r.Target.PlatformKey == PlatformNames.Douyin)
                                || pv.RecommendRows.Any(r => r.Target.PlatformKey == PlatformNames.WechatVideo))
                                throw new InvalidOperationException(
                                    "[smoke] 平台推荐区能力矩阵不对：rows="
                                    + string.Join(",", pv.RecommendRows.Select(r => r.Target.PlatformKey)));
                            if (!biliRecommendRow.TopicSupportsRecommend
                                || biliRecommendRow.TopicRecommendNeedsUpload)
                                throw new InvalidOperationException("[smoke] B站推荐行标记不对");
                            if (!target.GetVisualDescendants().OfType<Button>()
                                    .Any(b => ReferenceEquals(b.Command, pv.RecommendTopicSuggestionsCommand)))
                                throw new InvalidOperationException(
                                    "[smoke] 平台推荐区「获取推荐」按钮没接上命令");

                            biliRecommendRow.SetTopicSuggestions(
                                [new CreatFlow.Platforms.Topics.TopicSuggestion
                                {
                                    Name = "冒烟推荐话题",
                                    PlatformId = "smoke-topic-1",
                                }], fromRecommend: true);
                            await Task.Delay(200);
                            var smokePick = biliRecommendRow.TopicSuggestions.FirstOrDefault();
                            if (smokePick is null || !smokePick.FromRecommend)
                                throw new InvalidOperationException("[smoke] 推荐候选没装上");
                            pv.PickTopicSuggestionCommand.Execute(smokePick);
                            await Task.Delay(150);
                            if (!biliRecommendRow.Params.TopicRefs.Any(t =>
                                    t.Name == "冒烟推荐话题" && t.Id == "smoke-topic-1"))
                                throw new InvalidOperationException(
                                    "[smoke] 推荐候选没写进结构化话题（TopicRefs）");
                            if (topicDraft.Topics.Contains("冒烟推荐话题"))
                                throw new InvalidOperationException(
                                    "[smoke] 推荐候选不该灌进全局纯文本话题（draft.Topics）");
                            var smokeChip = biliRecommendRow.TopicChips.FirstOrDefault(
                                c => c.Topic.Name == "冒烟推荐话题");
                            if (smokeChip is null)
                                throw new InvalidOperationException("[smoke] 推荐话题芯片没出现");
                            pv.RemoveTopicChipCommand.Execute(smokeChip);
                            await Task.Delay(150);
                            if (biliRecommendRow.Params.TopicRefs.Any(t => t.Name == "冒烟推荐话题"))
                                throw new InvalidOperationException("[smoke] 推荐话题芯片移除没生效");
                            Console.WriteLine(
                                "[smoke] 发布页：全局话题卡「平台推荐」（能力矩阵 / 接线 / 结构化话题增删）OK");

                            // ── 全局「官方活动」区（2.6.9）：TEMP 造景、**零网络** ──
                            // 2026-11-06 用户要求把活动合并到公共区：每平台一行。
                            // 验六件事：能力矩阵（抖音有行 / 视频号没有行）、面板接线、
                            // 单选 + 已结束不可选、选中自动带关联话题（TopicRefs + 旧字段桥接）、
                            // 选择作用到该平台所有账号、清除活动同时移除自动带入的话题。
                            // 造景候选带封面 URL 也没关系 —— 封面加载只在「获取活动列表」命令里发起，
                            // 这里直接 SetActivityOptions 不走网络。
                            var dyActivityRow = pv.ActivityRows.FirstOrDefault(
                                r => r.PlatformKey == PlatformNames.Douyin);
                            if (dyActivityRow is null
                                || pv.ActivityRows.Any(r => r.PlatformKey == PlatformNames.WechatVideo))
                                throw new InvalidOperationException(
                                    "[smoke] 全局活动区能力矩阵不对：rows="
                                    + string.Join(",", pv.ActivityRows.Select(r => r.PlatformKey)));
                            if (!target.GetVisualDescendants().OfType<Button>()
                                    .Any(b => ReferenceEquals(b.Command, pv.ToggleActivityPanelCommand)))
                                throw new InvalidOperationException("[smoke] 全局活动区「选择活动」按钮没接上命令");

                            pv.ToggleActivityPanelCommand.Execute(dyActivityRow);
                            await Task.Delay(100);
                            if (!dyActivityRow.IsActivityPanelOpen
                                || dyActivityRow.ActivityPanelButtonText != "收起")
                                throw new InvalidOperationException("[smoke] 活动面板没展开");
                            if (!target.GetVisualDescendants().OfType<Button>()
                                    .Any(b => ReferenceEquals(b.Command, pv.FetchActivitiesCommand)))
                                throw new InvalidOperationException("[smoke] 活动面板「获取活动列表」没接上命令");

                            dyActivityRow.SetActivityOptions(
                            [
                                new CreatFlow.Platforms.Topics.PlatformActivity
                                {
                                    Id = "act-live",
                                    Name = "夏日创作大赛",
                                    Reward = "有奖活动",
                                    EndTime = DateTimeOffset.Now.AddDays(7),
                                    HotScore = 4_800_000,
                                    Topics =
                                    [
                                        new CreatFlow.Platforms.Topics.TopicSuggestion
                                        {
                                            Name = "夏日",
                                            PlatformId = "t-1001",
                                            ViewCount = 126_000_000,
                                        },
                                    ],
                                },
                                new CreatFlow.Platforms.Topics.PlatformActivity
                                {
                                    Id = "act-second",
                                    Name = "秋冬家装季",
                                    EndTime = DateTimeOffset.Now.AddDays(3),
                                    Topics =
                                    [
                                        new CreatFlow.Platforms.Topics.TopicSuggestion
                                        {
                                            Name = "家装",
                                            PlatformId = "t-2001",
                                        },
                                    ],
                                },
                                new CreatFlow.Platforms.Topics.PlatformActivity
                                {
                                    Id = "act-ended",
                                    Name = "过期活动",
                                    EndTime = DateTimeOffset.Now.AddDays(-1),
                                },
                            ]);
                            await Task.Delay(100);
                            if (dyActivityRow.ActivityOptions.Count != 3 || !dyActivityRow.HasActivityOptions)
                                throw new InvalidOperationException(
                                    $"[smoke] 活动候选没进列表：{dyActivityRow.ActivityOptions.Count}");
                            var liveCard = dyActivityRow.ActivityOptions[0];
                            var secondCard = dyActivityRow.ActivityOptions[1];
                            var endedCard = dyActivityRow.ActivityOptions[2];
                            if (liveCard.Activity.Id != "act-live" || !liveCard.CanSelect || liveCard.IsEnded
                                || !endedCard.IsEnded || endedCard.CanSelect
                                || !liveCard.HasReward || !liveCard.HasMetric || !liveCard.HasDeadline)
                                throw new InvalidOperationException(
                                    "[smoke] 活动卡片字段/可选态不对（进行中应在前、已结束灰显）");

                            // 已结束的点了不能选上
                            pv.PickActivityCommand.Execute(endedCard);
                            await Task.Delay(100);
                            if (dyRow.Params.Activities.Count != 0 || dyActivityRow.ActivityChips.Count != 0
                                || !dyActivityRow.ActivityStatusIsError)
                                throw new InvalidOperationException("[smoke] 已结束活动不该被选中");

                            pv.PickActivityCommand.Execute(liveCard);
                            await Task.Delay(100);
                            if (dyRow.Params.Activities.Count != 1
                                || dyRow.Params.Activities[0].Id != "act-live"
                                || dyActivityRow.ActivityChips.Count != 1 || !liveCard.IsSelected)
                                throw new InvalidOperationException(
                                    "[smoke] 选中活动没落进 Params.Activities / 芯片");
                            if (!dyRow.Params.TopicRefs.Any(t => t.Name == "夏日" && t.Id == "t-1001"
                                    && t.Source == TopicRefSources.Activity))
                                throw new InvalidOperationException(
                                    "[smoke] 选中活动没有自动带入关联话题（TopicRefs）");
                            if (!dyRow.Params.Topics.Any(t => t.Name == "夏日" && t.PlatformId == "t-1001"))
                                throw new InvalidOperationException(
                                    "[smoke] 活动话题没有桥接旧 Topics（2.6.5 之前 builder 读它）");
                            if (!dyRow.Params.ActivityIds.SequenceEqual(["act-live"]))
                                throw new InvalidOperationException(
                                    "[smoke] 选中的活动没有桥接旧 ActivityIds");
                            if (!dyActivityRow.ActivityStatusText.Contains("自动带入", StringComparison.Ordinal))
                                throw new InvalidOperationException(
                                    "[smoke] 选中活动后没说明自动带入了几个话题：" + dyActivityRow.ActivityStatusText);

                            // 换选另一个活动：旧活动自动带的话题要摘掉、新活动的带进来（单选语义）
                            pv.PickActivityCommand.Execute(secondCard);
                            await Task.Delay(100);
                            if (dyRow.Params.Activities.Count != 1
                                || dyRow.Params.Activities[0].Id != "act-second"
                                || !dyRow.Params.ActivityIds.SequenceEqual(["act-second"])
                                || dyRow.Params.TopicRefs.Any(t => t.Name == "夏日")
                                || dyRow.Params.Topics.Any(t => t.Name == "夏日")
                                || !dyRow.Params.TopicRefs.Any(t => t.Name == "家装"
                                    && t.Source == TopicRefSources.Activity))
                                throw new InvalidOperationException(
                                    "[smoke] 换选活动后旧话题没摘掉 / 新活动话题没带进来");

                            if (!target.GetVisualDescendants().OfType<Button>()
                                    .Any(b => ReferenceEquals(b.Command, pv.ClearActivityCommand)))
                                throw new InvalidOperationException("[smoke] 活动芯片的 × 没接上命令");
                            pv.ClearActivityCommand.Execute(dyActivityRow.ActivityChips[0]);
                            await Task.Delay(100);
                            if (dyRow.Params.Activities.Count != 0 || dyRow.Params.ActivityIds.Count != 0
                                || dyActivityRow.ActivityChips.Count != 0
                                || dyRow.Params.TopicRefs.Any(t => t.Name is "夏日" or "家装")
                                || dyRow.Params.Topics.Any(t => t.Name is "夏日" or "家装"))
                                throw new InvalidOperationException("[smoke] 清除活动后仍有残留（活动/旧 ID/自动话题）");
                            Console.WriteLine(
                                "[smoke] 发布页：全局官方活动区（每平台一行 / 单选 / 已结束灰显 / 自动带话题 / 清除）OK");

                            pv.BackToListCommand.Execute(null);
                            await Task.Delay(150);

                            if (pv.HasVerifyPrompt)
                            {
                                if (pv.OnNavigateRequested is null)
                                    throw new InvalidOperationException(
                                        "[smoke] 发布页没拿到 OnNavigateRequested（壳没注入）");
                                pv.GoOpenAccountWindowsCommand.Execute(null);
                                await Task.Delay(400);
                                if (shell.CurrentPage is not PlatformViewModel)
                                    throw new InvalidOperationException(
                                        "[smoke] 发布页「去平台账号」没跳到平台页，当前=" +
                                        shell.CurrentPage?.GetType().Name);
                                Console.WriteLine("[smoke] 发布页「去平台账号」跳转 OK");
                            }
                        }
                    }

                    // 合成页特判：素材行模板与底部操作只有列表非空才渲染 —— 用仓库样例素材造景
                    //（只读文件）。ComposeCommand 只入队不真跑（消费者被 SmokeMode 闸住），
                    // 验「提交后页面复位 + 静态确认 + 任务进队列」，断言完取消移除（见下）。
                    if (pages.FirstOrDefault(i => i.Key == "compose") is { } compose)
                    {
                        shell.Navigate(compose);
                        await Task.Delay(700);
                        if (shell.CurrentPage is ComposeViewModel cv)
                        {
                            var samples = FindComposeSamples();
                            if (samples.Count > 0)
                            {
                                cv.AddMaterials(samples);
                                await Task.Delay(400);

                                var cmds = target.GetVisualDescendants().OfType<Button>()
                                    .Select(b => b.Command).ToList();
                                foreach (var (cmd, label) in new (System.Windows.Input.ICommand?, string)[]
                                         {
                                             (cv.MoveItemUpCommand, "上移"),
                                             (cv.MoveItemDownCommand, "下移"),
                                             (cv.RemoveItemCommand, "移除"),
                                         })
                                {
                                    if (cmd is not null && !cmds.Any(c => ReferenceEquals(c, cmd)))
                                        throw new InvalidOperationException(
                                            $"[smoke] 合成页素材行「{label}」按钮没接上命令");
                                }

                                var first = cv.Items[0];
                                cv.MoveItemDownCommand.Execute(first);
                                var movedDown = cv.Items[1] == first;
                                cv.MoveItemUpCommand.Execute(first);
                                var movedBack = cv.Items[0] == first;
                                if (!movedDown || !movedBack)
                                    throw new InvalidOperationException(
                                        $"[smoke] 合成页素材上移/下移往返失败：下移={movedDown} 上移={movedBack}");
                                Console.WriteLine($"[smoke] 合成页：素材 {cv.Items.Count} 个（{cv.ItemsSummary}），"
                                                  + "行按钮接线 + 移动往返 OK");

                                // 角色库（2026-10-11 批次 4）：套模板 = 最终文案预览 + 提交快照；
                                // **只断言本地可判定状态，绝不执行 OptimizeTextCommand**（那是真 LLM）
                                var smokeRole = cv.Roles.FirstOrDefault(r => r.Name == "冒烟房淘惠")
                                    ?? throw new InvalidOperationException(
                                        "[smoke] 合成页角色下拉没有造景角色：" + string.Join('/', cv.Roles.Select(r => r.Name)));
                                cv.SelectedRole = smokeRole;
                                cv.Text = "冒烟原始文案。";
                                await Task.Delay(150);
                                if (cv.FinalText != "您好，我是冒烟。冒烟原始文案。欢迎私信。")
                                    throw new InvalidOperationException("[smoke] 合成页最终文案没套模板：" + cv.FinalText);
                                if (!cv.OptimizeTextCommand.CanExecute(null))
                                    throw new InvalidOperationException("[smoke] 有人设的角色 AI 优化按钮不该禁用");
                                if (!target.GetVisualDescendants().OfType<TextBox>()
                                        .Any(tb => tb.IsReadOnly && tb.Text == cv.FinalText))
                                    throw new InvalidOperationException("[smoke] 「最终文案」只读预览框没接上数据");
                                if (!target.GetVisualDescendants().OfType<Button>()
                                        .Any(b => ReferenceEquals(b.Command, cv.OptimizeTextCommand)))
                                    throw new InvalidOperationException("[smoke] AI 优化按钮没接上命令");
                                if (cv.IsOptimizing || cv.HasOptimizeResult)
                                    throw new InvalidOperationException("[smoke] 冒烟里不该有优化在途/结果");
                                cv.SelectedRole = cv.Roles.First(r => r.Name == "冒烟无设");
                                await Task.Delay(100);
                                if (cv.OptimizeTextCommand.CanExecute(null))
                                    throw new InvalidOperationException("[smoke] 无设角色不该能 AI 优化");
                                if (cv.FinalText != "冒烟原始文案。")
                                    throw new InvalidOperationException("[smoke] 无占位模板的最终文案不对：" + cv.FinalText);

                                // 合成完成后自动发布（2026-10-11 批次 5）：开关落盘往返（TEMP 存档）
                                // + 开但无账号时提交被拦（fail-fast，防「以为发了其实没发」）。
                                // 无网络时芯片为空是确定态；服务端在且用户绑过账号时只断言摘要不误报。
                                var selStore2 = Services.GetRequiredService<AccountSelectionStore>();
                                cv.AutoPublish = true;
                                await Task.Delay(300);
                                if (!(await selStore2.LoadAsync()).AutoPublish)
                                    throw new InvalidOperationException("[smoke] 自动发布开关没落盘到 TEMP 存档");
                                if (cv.TargetChips.Count == 0)
                                {
                                    if (!cv.AutoPublishSummary.Contains("没有勾选任何账号", StringComparison.Ordinal)
                                        || !cv.IsAutoPublishSummaryError)
                                        throw new InvalidOperationException(
                                            "[smoke] 无账号时摘要没给红字提示：" + cv.AutoPublishSummary);
                                    if (cv.ComposeCommand.CanExecute(null))
                                    {
                                        await cv.ComposeCommand.ExecuteAsync(null);
                                        if (!cv.StatusMessage.Contains("没有勾选任何账号", StringComparison.Ordinal))
                                            throw new InvalidOperationException(
                                                "[smoke] 自动发布无账号没拦下提交：" + cv.StatusMessage);
                                    }
                                }
                                else if (cv.IsAutoPublishSummaryError)
                                {
                                    throw new InvalidOperationException("[smoke] 有账号时摘要不该是错误态");
                                }
                                cv.AutoPublish = false;
                                await Task.Delay(300);
                                if ((await selStore2.LoadAsync()).AutoPublish)
                                    throw new InvalidOperationException("[smoke] 自动发布开关复位没落盘");
                                Console.WriteLine($"[smoke] 合成页：自动发布开关落盘往返 + "
                                                  + $"{(cv.TargetChips.Count == 0 ? "无账号 fail-fast 拦截" : "有账号摘要正常")} OK");

                                // 提交后页面应自动复位本次输入（2026-10-11 用户要求：任务由队列兜住，页面腾给下一条；
                                // 与配音页「提交后列表复位」对齐）。真执行 ComposeCommand：入队进 TEMP 队列
                                //（消费者被 SmokeMode 闸住不会跑），断言完取消 + 移除，别影响下游合成队列的计数断言。
                                if (!KokoroModels.IsReady)
                                {
                                    Console.WriteLine("[smoke] 合成页：Kokoro 模型未下载，跳过提交复位断言");
                                }
                                else
                                {
                                    var composeQ = Services.GetRequiredService<ComposeQueueViewModel>();
                                    cv.PublishTitle = "冒烟复位";
                                    cv.OverlayTitle = "冒烟叠加";
                                    cv.BgmPath = "冒烟bgm.mp3";
                                    cv.SelectedRole = smokeRole;   // 回到有人设的角色：提交快照要带走套模板结果 + RoleName
                                    cv.Text = "冒烟提交文案。";
                                    // 水印（2026-10-21）：账号设置页偏好 → 提交时读 → 冻进任务快照
                                    var wmStore = Services.GetRequiredService<WatermarkSettingsStore>();
                                    await wmStore.SaveAsync(new WatermarkSettings
                                    {
                                        Enabled = true,
                                        Text = "冒烟水印",
                                        Position = WatermarkPosition.BottomLeft,
                                    });
                                    if (!cv.ComposeCommand.CanExecute(null))
                                        throw new InvalidOperationException("[smoke] 合成页提交前按钮不可用（素材/文案/标题已备齐）");
                                    await cv.ComposeCommand.ExecuteAsync(null);
                                    await Task.Delay(200);
                                    if (cv.Items.Count != 0 || cv.Text.Length != 0 || cv.PublishTitle.Length != 0
                                        || cv.OverlayTitle is not null || cv.BgmPath is not null)
                                        throw new InvalidOperationException(
                                            $"[smoke] 合成提交后页面没复位：素材={cv.Items.Count} 文案字数={cv.Text.Length} 标题「{cv.PublishTitle}」");
                                    if (!cv.StatusMessage.Contains("已提交到「合成队列」"))
                                        throw new InvalidOperationException("[smoke] 合成页提交后没有静态确认语：" + cv.StatusMessage);

                                    var enq = composeQ.Tasks.FirstOrDefault(t => t.Title == "冒烟复位");
                                    if (enq is null || enq.Status != ComposeTaskStatus.Pending)
                                        throw new InvalidOperationException(
                                            $"[smoke] 合成提交没进队列：{(enq is null ? "队列里没找到" : enq.Status.ToString())}");
                                    if (enq.Text != "您好，我是冒烟。冒烟提交文案。欢迎私信。" || enq.RoleName != "冒烟房淘惠")
                                        throw new InvalidOperationException(
                                            $"[smoke] 角色没随任务快照：text={enq.Text} role={enq.RoleName ?? "(无)"}");
                                    if (enq.Watermark is not { Enabled: true, Text: "冒烟水印", Position: WatermarkPosition.BottomLeft })
                                        throw new InvalidOperationException(
                                            $"[smoke] 水印没随任务快照：{enq.Watermark.Enabled}/{enq.Watermark.Text}/{enq.Watermark.Position}");
                                    if (cv.SelectedRole?.Name != "冒烟房淘惠")
                                        throw new InvalidOperationException("[smoke] 提交后角色选择该保留（与音色同理）");

                                    // 队列里排着一条也不锁页面：重新备料立即可再提交（串行排队即可）——
                                    // 旧行为「正在合成」会把按钮锁到上一条跑完（分钟级）才能备料
                                    cv.AddMaterials(samples);
                                    cv.Text = "第二条冒烟文案。";
                                    if (!cv.ComposeCommand.CanExecute(null))
                                        throw new InvalidOperationException("[smoke] 队列有任务时合成页不能继续备下一条（按钮被锁死）");

                                    // 清场：页面复位（「清空参数」按钮）+ 队列那条取消移除
                                    cv.ClearAllCommand.Execute(null);
                                    if (cv.HasOptimizeResult || cv.IsOptimizing || cv.HasOptimizeMessage)
                                        throw new InvalidOperationException("[smoke] 清空后优化状态没清干净");
                                    composeQ.Cancel(enq);
                                    composeQ.RemoveTaskCommand.Execute(enq);
                                    Console.WriteLine("[smoke] 合成页：提交后自动复位本次输入 + 角色快照（Text 套模板 + RoleName）+ 可继续备下一条 OK");
                                }
                            }
                            else
                            {
                                Console.WriteLine("[smoke] 合成页：没找到样例素材，跳过造景");
                            }
                        }
                    }

                    // 分组服务提前拿到（2026-10-15：合成队列段也要造景组条 —— 组条命令会真查库）；
                    // 平台页 / 发布页 / 合成页的组条断言在后面继续用它
                    var groupsSvc = Services.GetRequiredService<AccountGroupsService>();

                    // 合成队列特判：TEMP 造景一个批量文件夹，走真扫描 + 真入队；再走一遍「新建模板」
                    // 落盘（ffmpeg 现画示例图）+ 扫描回读（2026-10-11 新增）。
                    //（**消费者被 SmokeMode 闸住不会真跑**；队列存储在冒烟下走 TEMP 根，不碰用户 compose_queue.json），
                    // 断言任务生成正确后清掉造景（取消 → 移除）。
                    if (pages.FirstOrDefault(i => i.Key == "composequeue") is { } cqNav)
                    {
                        shell.Navigate(cqNav);
                        await Task.Delay(500);
                        if (shell.CurrentPage is ComposeQueueViewModel cqv)
                        {
                            if (cqv.IsConsumerStarted)
                                throw new InvalidOperationException("[smoke] 合成队列消费者不该在冒烟里启动");

                            // 冒烟确定性：明确刷一次角色表（不靠导航时序），并先落回「不使用角色」——
                            // 批量断言不随用户 compose_settings.json 里记的角色漂移
                            cqv.OnNavigatedTo();
                            await Task.Delay(400);
                            cqv.SelectedRole = ComposeRole.None;

                            // 水印（2026-10-21）：批量入队时读会员档 → 冻进每条任务快照
                            var wmStore = Services.GetRequiredService<WatermarkSettingsStore>();
                            await wmStore.SaveAsync(new WatermarkSettings
                            {
                                Enabled = true,
                                Text = "批量冒烟",
                                Position = WatermarkPosition.TopLeft,
                            });

                            var root = Path.Combine(Path.GetTempPath(), "cf-smoke-batch-" + Guid.NewGuid().ToString("N")[..8]);
                            var queueGroupId = Guid.NewGuid().ToString("N");   // 队列段自建临时组（finally 里删掉）
                            try
                            {
                                // 一号房：3 图（1/2/10 —— 验自然序）+ 资料.txt（带头注释）
                                // 二号房：文案 + 音频 + bgm + 1 图（有文案 → 文案配音 + 字幕；音频收编成背景乐、bgm 前缀优先）
                                // 三号房：有图缺配音（应跳过并给出原因）
                                var one = Path.Combine(root, "一号房");
                                var two = Path.Combine(root, "二号房");
                                var three = Path.Combine(root, "三号房");
                                Directory.CreateDirectory(one);
                                Directory.CreateDirectory(two);
                                Directory.CreateDirectory(three);
                                File.WriteAllText(Path.Combine(one, "1.jpg"), "x");
                                File.WriteAllText(Path.Combine(one, "2.jpg"), "x");
                                File.WriteAllText(Path.Combine(one, "10.jpg"), "x");
                                File.WriteAllText(Path.Combine(one, "资料.txt"), "# 注释行不读\n这是测试文案。");
                                File.WriteAllText(Path.Combine(two, "a.mp3"), "x");
                                File.WriteAllText(Path.Combine(two, "bgm.mp3"), "x");
                                File.WriteAllText(Path.Combine(two, "1.png"), "x");
                                File.WriteAllText(Path.Combine(two, "资料.txt"), "二号房也写文案。");
                                File.WriteAllText(Path.Combine(three, "1.jpg"), "x");

                                var added = await cqv.AddFolderBatchAsync([root]);
                                var tasks = cqv.Tasks.ToList();
                                var t1 = tasks.FirstOrDefault(t => t.Title == "一号房");
                                var t2 = tasks.FirstOrDefault(t => t.Title == "二号房");
                                if (added != 2 || tasks.Count != 2 || t1 is null || t2 is null)
                                    throw new InvalidOperationException(
                                        $"[smoke] 合成队列批量添加异常：added={added} tasks={tasks.Count}");
                                if (t1.MaterialPaths.Count != 3
                                    || !t1.MaterialPaths[0].EndsWith("1.jpg", StringComparison.OrdinalIgnoreCase)
                                    || !t1.MaterialPaths[1].EndsWith("2.jpg", StringComparison.OrdinalIgnoreCase)
                                    || !t1.MaterialPaths[2].EndsWith("10.jpg", StringComparison.OrdinalIgnoreCase))
                                    throw new InvalidOperationException(
                                        "[smoke] 合成队列素材自然序不对：" +
                                        string.Join(",", t1.MaterialPaths.Select(Path.GetFileName)));
                                if (t1.Text != "这是测试文案。" || t1.AudioPath is not null)
                                    throw new InvalidOperationException($"[smoke] 资料.txt 文案（去 # 注释行）没跟上：{t1.Text}");
                                if (t2.AudioPath is not null || t2.Text != "二号房也写文案。"
                                    || t2.BgmPath is null || !t2.BgmPath.EndsWith("bgm.mp3", StringComparison.OrdinalIgnoreCase))
                                    throw new InvalidOperationException(
                                        $"[smoke] 有文案时音频收编 / bgm 命名约定不对：audio={Path.GetFileName(t2.AudioPath ?? "(无)")} bgm={t2.BgmPath ?? "(无)"}");
                                if (!cqv.StatusNote.Contains("跳过 1 个"))
                                    throw new InvalidOperationException("[smoke] 跳过提示不对：" + cqv.StatusNote);
                                if (t1.Watermark is not { Enabled: true, Text: "批量冒烟", Position: WatermarkPosition.TopLeft }
                                    || t2.Watermark != t1.Watermark)
                                    throw new InvalidOperationException(
                                        $"[smoke] 批量任务没带水印快照：{t1.Watermark.Text}/{t1.Watermark.Position}");

                                // 收尾：取消 → 移除造景任务（队列静止时 Pending 不可直接移除）
                                foreach (var t in tasks)
                                {
                                    cqv.Cancel(t);
                                    cqv.RemoveTaskCommand.Execute(t);
                                }
                                Console.WriteLine("[smoke] 合成队列：批量 2 条（自然序 + 资料.txt 注释/文案优先 + bgm 收编 + 跳过 1）OK");

                                // 角色库（2026-10-11 批次 4）：选角色再批量一轮 —— 台词模板套进快照 + RoleName 落上；
                                // **不勾「用 AI 优化每条」**（勾了会真调 LLM，冒烟绝不碰）
                                cqv.SelectedRole = cqv.Roles.FirstOrDefault(r => r.Name == "冒烟房淘惠")
                                    ?? throw new InvalidOperationException("[smoke] 合成队列角色下拉没有造景角色");
                                if (cqv.UseAiOptimize)
                                    throw new InvalidOperationException("[smoke] 批量 AI 优化默认该是不勾");
                                var added2 = await cqv.AddFolderBatchAsync([root]);
                                var r1 = cqv.Tasks.FirstOrDefault(t => t.Title == "一号房");
                                if (added2 != 2 || r1 is null
                                    || r1.Text != "您好，我是冒烟。这是测试文案。欢迎私信。"
                                    || r1.RoleName != "冒烟房淘惠")
                                    throw new InvalidOperationException(
                                        $"[smoke] 批量角色没套上：added={added2} text={r1?.Text} role={r1?.RoleName}");
                                if (!cqv.StatusNote.Contains("已套用角色「冒烟房淘惠」"))
                                    throw new InvalidOperationException("[smoke] 批量状态栏没写角色提示：" + cqv.StatusNote);
                                foreach (var t in cqv.Tasks.ToList())
                                {
                                    cqv.Cancel(t);
                                    cqv.RemoveTaskCommand.Execute(t);
                                }
                                cqv.SelectedRole = ComposeRole.None;   // 复原：后面的「模板扫描回读」断言要用原文
                                Console.WriteLine("[smoke] 合成队列：批量角色套模板（快照 Text + RoleName + 状态栏）OK");

                                // 新建模板：真落盘（ffmpeg 现画 3 张示例图）+ 真扫描回读 ——
                                // 「生成文件夹 → 用户替换素材 → 整批导入」整条链路的往返
                                var tplParent = Path.Combine(root, "模板目录");
                                Directory.CreateDirectory(tplParent);
                                if (!await cqv.CreateTemplateAtAsync(tplParent))
                                    throw new InvalidOperationException("[smoke] 新建模板失败：" + cqv.StatusNote);
                                var tplFolder = Path.Combine(tplParent, ComposeUnitTemplate.FolderName);
                                if (!File.Exists(Path.Combine(tplFolder, ComposeUnitTemplate.ReadmeFileName))
                                    || !File.Exists(Path.Combine(tplFolder, ComposeFolderScanner.TextFileName))
                                    || !File.Exists(Path.Combine(tplFolder, "1.jpg")))
                                    throw new InvalidOperationException("[smoke] 模板文件没落全：" + cqv.StatusNote);
                                if (!cqv.HasTemplateFolder)
                                    throw new InvalidOperationException("[smoke] 「打开模板文件夹」按钮的可见条件没亮");

                                var tplAdded = await cqv.AddFolderBatchAsync([tplParent]);
                                var tplTask = cqv.Tasks.FirstOrDefault(t => t.Title == ComposeUnitTemplate.FolderName);
                                if (tplAdded != 1 || tplTask is null || tplTask.Text != ComposeUnitTemplate.SampleCopy
                                    || tplTask.MaterialPaths.Count != ComposeUnitTemplate.SampleImageCount)
                                    throw new InvalidOperationException(
                                        $"[smoke] 模板没被批量扫描吃下：added={tplAdded} text={tplTask?.Text ?? "(无)"}");
                                cqv.Cancel(tplTask);
                                cqv.RemoveTaskCommand.Execute(tplTask);
                                Console.WriteLine("[smoke] 合成队列：新建模板落盘 + 扫描回读 OK");

                                // 自动发布（2026-10-11 批次 5）：批量确认条（文案 + 三按钮接线 + 取消复位）
                                // + 假目标批量快照端到端 + 结果展示「去上传队列」接线。
                                // **零网络零提交**：只走本地命令与快照（消费者被 SmokeMode 闸住）
                                cqv.ShowBatchConfirm([root], 2, 3);
                                if (!cqv.IsConfirmingBatch
                                    || !cqv.BatchConfirmText.Contains("2 条", StringComparison.Ordinal)
                                    || !cqv.BatchConfirmText.Contains("3 个账号", StringComparison.Ordinal)
                                    || !cqv.BatchConfirmText.Contains("立即发布", StringComparison.Ordinal))
                                    throw new InvalidOperationException("[smoke] 批量确认条文案不对：" + cqv.BatchConfirmText);
                                await Task.Delay(250);   // 等确认条渲染出来再找按钮
                                var cqCmds = target.GetVisualDescendants().OfType<Button>().Select(b => b.Command).ToList();
                                foreach (var (cmd, label) in new (System.Windows.Input.ICommand?, string)[]
                                         {
                                             (cqv.ConfirmBatchAddCommand, "确认并自动发布"),
                                             (cqv.AddBatchWithoutPublishCommand, "仅添加"),
                                             (cqv.CancelBatchAddCommand, "取消"),
                                         })
                                {
                                    if (cmd is not null && !cqCmds.Any(c => ReferenceEquals(c, cmd)))
                                        throw new InvalidOperationException($"[smoke] 批量确认条「{label}」按钮没接上命令");
                                }
                                cqv.CancelBatchAddCommand.Execute(null);
                                await Task.Delay(100);
                                if (cqv.IsConfirmingBatch || !cqv.StatusNote.Contains("已取消本批添加", StringComparison.Ordinal))
                                    throw new InvalidOperationException("[smoke] 确认条「取消」没复位");
                                Console.WriteLine("[smoke] 合成队列：批量确认条（文案 + 三按钮接线 + 取消复位）OK");

                                var fakeTargets = new List<AutoPublishTarget>
                                {
                                    new("douyin", Guid.NewGuid(), "冒烟甲"),
                                    new("xiaohongshu", Guid.NewGuid(), "冒烟乙"),
                                };
                                var addedAp = await cqv.AddFolderBatchAsync([root], fakeTargets);
                                var apTask = cqv.Tasks.FirstOrDefault(t => t.Title == "一号房");
                                if (addedAp != 2 || apTask is null
                                    || apTask.AutoPublishTargets.Count != 2 || !apTask.HasAutoPublish)
                                    throw new InvalidOperationException(
                                        $"[smoke] 批量带 targets 没进快照：added={addedAp} targets={apTask?.AutoPublishTargets.Count}");
                                if (!cqv.StatusNote.Contains("将在合成后自动发布到 2 个账号", StringComparison.Ordinal))
                                    throw new InvalidOperationException("[smoke] 批量状态栏没写自动发布提示：" + cqv.StatusNote);
                                if (ComposeTask.FromData(apTask.ToData()).AutoPublishTargets.Count != 2)
                                    throw new InvalidOperationException("[smoke] 自动发布 targets 存档往返丢失");
                                apTask.AutoPublishState = AutoPublishState.Submitted;
                                apTask.AutoPublishText = "已进上传队列 · 2 个账号（2 条平台记录）";
                                await Task.Delay(250);
                                if (cqv.OnNavigateRequested is null)
                                    throw new InvalidOperationException("[smoke] 合成队列没拿到 OnNavigateRequested（壳没注入）");
                                cqCmds = target.GetVisualDescendants().OfType<Button>().Select(b => b.Command).ToList();
                                if (!cqCmds.Any(c => ReferenceEquals(c, cqv.GoUploadQueueCommand)))
                                    throw new InvalidOperationException("[smoke] 「去上传队列」按钮没接上命令");
                                foreach (var t in cqv.Tasks.ToList()) { cqv.Cancel(t); cqv.RemoveTaskCommand.Execute(t); }
                                Console.WriteLine("[smoke] 合成队列：批量假目标快照 + 结果展示「去上传队列」接线 OK");

                                // 队列页自动发布配置区 + 批量定时（2026-10-15 批次 6）：开关落盘往返（TEMP 存档）
                                // + 组条一键勾选（真经 AccountGroupsService 造组）+ 定时默认/摘要/确认条文案
                                // + 定时控件在可视树 + 确认入队快照带 ScheduledAt。
                                // 零网络：先 await 排干导航触发的在飞刷新，再塞造景假芯片；消费者被闸住不跑。
                                await cqv.DrainNavigatedAsync();
                                var qa = Guid.NewGuid();
                                var qb = Guid.NewGuid();
                                var qc = Guid.NewGuid();
                                cqv.TargetChips.Clear();
                                foreach (var (sid, plat, name) in new (Guid Sid, string Plat, string Name)[]
                                         {
                                             (qa, "douyin", "抖音"),
                                             (qb, "kuaishou", "快手"),
                                             (qc, "douyin", "抖音"),
                                         })
                                {
                                    cqv.TargetChips.Add(new AccountChipViewModel
                                    {
                                        SessionId = sid,
                                        PlatformKey = plat,
                                        PlatformName = name,
                                        AccountLabel = "冒烟号",
                                        BrandHex = "#6C5CE7",
                                    });
                                }
                                cqv.AutoPublish = true;
                                await Task.Delay(300);
                                if (!(await selStore.LoadAsync()).AutoPublish)
                                    throw new InvalidOperationException("[smoke] 队列页自动发布开关没落盘到 TEMP 存档");

                                await groupsSvc.SaveAsync(new AccountGroup
                                {
                                    Id = queueGroupId,
                                    Name = "冒烟队列组",
                                    Sessions = [qa, qb],
                                });
                                await cqv.RefreshGroupChoicesAsync();
                                var queueGroup = cqv.GroupChoices.FirstOrDefault(c => c.Name == "冒烟队列组")
                                    ?? throw new InvalidOperationException("[smoke] 队列页组条没有造景组");
                                // 等组条实例化 —— 等待循环里就地快照，断言用这份快照（避免「等到了、取时没了」）
                                List<System.Windows.Input.ICommand?> qCmds = [];
                                await WaitVisualAsync(() =>
                                {
                                    qCmds = target.GetVisualDescendants().OfType<Button>()
                                        .Select(b => b.Command).ToList();
                                    return qCmds.Any(c => ReferenceEquals(c, cqv.ApplyTargetGroupCommand));
                                });
                                if (!qCmds.Any(c => ReferenceEquals(c, cqv.ApplyTargetGroupCommand)))
                                    throw new InvalidOperationException("[smoke] 队列页组条按钮没接上命令");
                                await cqv.ApplyTargetGroupCommand.ExecuteAsync(queueGroup);
                                var qOn = cqv.TargetChips.Where(c => c.IsOn).Select(c => c.SessionId).ToHashSet();
                                if (!qOn.SetEquals([qa, qb]) || !queueGroup.IsActive)
                                    throw new InvalidOperationException("[smoke] 队列页点组名没把勾选换成该组");
                                if (!cqv.AutoPublishSummary.Contains("将发布到 2 个账号", StringComparison.Ordinal))
                                    throw new InvalidOperationException("[smoke] 队列页摘要没联动：" + cqv.AutoPublishSummary);

                                // 定时（批次级）：勾选给默认明天 20:00 → 摘要带「定时」→ 确认条带「将于」→ 控件在可视树
                                cqv.IsScheduled = true;
                                var qSched = cqv.ScheduledAtOrNull
                                    ?? throw new InvalidOperationException("[smoke] 勾定时没给默认日期时间");
                                if (!cqv.AutoPublishSummary.Contains("定时", StringComparison.Ordinal))
                                    throw new InvalidOperationException("[smoke] 摘要没带定时：" + cqv.AutoPublishSummary);
                                cqv.ShowBatchConfirm([root], 2, 2, null, qSched);
                                if (!cqv.BatchConfirmText.Contains("将于", StringComparison.Ordinal))
                                    throw new InvalidOperationException("[smoke] 确认条没带定时：" + cqv.BatchConfirmText);
                                await Task.Delay(250);
                                if (!target.GetVisualDescendants().OfType<DatePicker>().Any()
                                    || !target.GetVisualDescendants().OfType<TimePicker>().Any())
                                    throw new InvalidOperationException("[smoke] 队列页定时控件没进可视树");
                                cqv.CancelBatchAddCommand.Execute(null);

                                // 确认入队全链路：目标 + 定时一起进任务快照
                                var qTargets = new List<AutoPublishTarget>
                                {
                                    new("douyin", qa, "冒烟号"),
                                    new("kuaishou", qb, "冒烟号"),
                                };
                                cqv.ShowBatchConfirm([root], 2, 2, qTargets, qSched);
                                await cqv.ConfirmBatchAddCommand.ExecuteAsync(null);
                                await Task.Delay(200);
                                var qTasks = cqv.Tasks.Where(t => t.Title is "一号房" or "二号房").ToList();
                                if (qTasks.Count != 2
                                    || qTasks.Any(t => t.ScheduledAt != qSched || !t.HasSchedule
                                        || t.AutoPublishTargets.Count != 2))
                                    throw new InvalidOperationException(
                                        $"[smoke] 队列页批量快照没带定时/目标：n={qTasks.Count} sched={qTasks.FirstOrDefault()?.ScheduledAt}");
                                foreach (var t in qTasks) { cqv.Cancel(t); cqv.RemoveTaskCommand.Execute(t); }

                                // 落盘往返（批次 7：会员级队列档）：清场后读回归零（Persist 是 fire-and-forget，先等一下）
                                await Task.Delay(250);
                                var qBack = await Services.GetRequiredService<ComposeQueueStore>().LoadAsync();
                                if (qBack.Count != 0)
                                    throw new InvalidOperationException("[smoke] 合成队列清场后落盘没归零：count=" + qBack.Count);

                                // 清场并复位（别把开关/定时/假芯片留给后续段）
                                cqv.IsScheduled = false;
                                cqv.AutoPublish = false;
                                await Task.Delay(300);
                                if ((await selStore.LoadAsync()).AutoPublish)
                                    throw new InvalidOperationException("[smoke] 队列页自动发布开关复位没落盘");
                                cqv.TargetChips.Clear();
                                Console.WriteLine("[smoke] 合成队列：自动发布配置区（开关落盘 + 组条一键勾选 + 定时确认条/快照 + 控件接线）OK");
                            }
                            finally
                            {
                                try { await groupsSvc.DeleteAsync(queueGroupId); } catch { }
                                try { Directory.Delete(root, recursive: true); } catch { }
                            }
                        }
                    }

                    // AI 设置页特判：角色库卡片 —— 增/存/改/删全走 TEMP 存储（**不发任何 LLM 请求**）。
                    // 放在合成/队列特判之后：那两处要用「冒烟无设」（空人设）断言禁用态，这里最后删它。
                    if (pages.FirstOrDefault(i => i.Key == "ai") is { } aiNav)
                    {
                        var roleService = Services.GetRequiredService<ComposeRolesService>();
                        shell.Navigate(aiNav);
                        await Task.Delay(500);
                        if (shell.CurrentPage is AiSettingsViewModel av)
                        {
                            if (av.RoleRows.Count != 4)
                                throw new InvalidOperationException($"[smoke] 角色卡行数不对：{av.RoleRows.Count}");

                            // 新建 → 填 → 保存 → 落盘；编辑态下「保存/取消」按钮接线一道断言
                            av.AddRoleCommand.Execute(null);
                            var draft = av.RoleRows[^1];
                            if (!draft.IsEditing || !draft.IsNew)
                                throw new InvalidOperationException("[smoke] 新建角色没进编辑态");
                            await Task.Delay(250);
                            var aiCmds = target.GetVisualDescendants().OfType<Button>().Select(b => b.Command).ToList();
                            foreach (var (cmd, label) in new (System.Windows.Input.ICommand?, string)[]
                                     {
                                         (av.AddRoleCommand, "新建角色"),
                                         (av.SaveRoleCommand, "保存"),
                                         (av.CancelEditRoleCommand, "取消"),
                                     })
                            {
                                if (cmd is not null && !aiCmds.Any(c => ReferenceEquals(c, cmd)))
                                    throw new InvalidOperationException($"[smoke] AI 设置角色卡「{label}」按钮没接上命令");
                            }
                            draft.Name = "冒烟新角色";
                            draft.Template = "嗨。{内容}。";
                            draft.Persona = "随便说说。";
                            await av.SaveRoleCommand.ExecuteAsync(draft);
                            var reread = roleService.Roles;   // 批次 8 起角色卡走共享服务（写完即内存可见）
                            if (draft.IsEditing || reread.Count != 5 || reread.All(r => r.Name != "冒烟新角色"))
                                throw new InvalidOperationException(
                                    $"[smoke] 新建角色没落盘：editing={draft.IsEditing} count={reread.Count}");

                            // 编辑 → 取消回滚（不落盘）
                            var one = av.RoleRows.First(r => r.Id == "smoke-role");
                            av.BeginEditRoleCommand.Execute(one);
                            var keepName = one.Name;
                            one.Name = "改一改";
                            av.CancelEditRoleCommand.Execute(one);
                            if (one.Name != keepName || one.IsEditing)
                                throw new InvalidOperationException("[smoke] 角色编辑「取消」没回滚");

                            // 删除两段式：出现确认条 → 取消 → 再现 → 确认 → 落盘
                            var del = av.RoleRows.First(r => r.Id == "smoke-nopersona");
                            av.BeginDeleteRoleCommand.Execute(del);
                            if (!del.IsConfirmingDelete)
                                throw new InvalidOperationException("[smoke] 删除确认条没出现");
                            av.CancelDeleteRoleCommand.Execute(del);
                            if (del.IsConfirmingDelete)
                                throw new InvalidOperationException("[smoke] 删除「取消」没生效");
                            av.BeginDeleteRoleCommand.Execute(del);
                            await av.ConfirmDeleteRoleCommand.ExecuteAsync(del);
                            reread = roleService.Roles;
                            if (av.RoleRows.Any(r => r.Id == "smoke-nopersona") || reread.Any(r => r.Id == "smoke-nopersona"))
                                throw new InvalidOperationException("[smoke] 角色删除没落盘");
                            if (!av.RoleStatusMessage.Contains("已删除角色"))
                                throw new InvalidOperationException("[smoke] 角色卡状态条没写删除结果：" + av.RoleStatusMessage);

                            // 折叠态「编辑/删除」按钮接线
                            await Task.Delay(250);
                            aiCmds = target.GetVisualDescendants().OfType<Button>().Select(b => b.Command).ToList();
                            foreach (var (cmd, label) in new (System.Windows.Input.ICommand?, string)[]
                                     {
                                         (av.BeginEditRoleCommand, "编辑"),
                                         (av.BeginDeleteRoleCommand, "删除"),
                                     })
                            {
                                if (cmd is not null && !aiCmds.Any(c => ReferenceEquals(c, cmd)))
                                    throw new InvalidOperationException($"[smoke] AI 设置角色卡「{label}」按钮没接上命令");
                            }
                            Console.WriteLine("[smoke] AI 设置页：角色库增/存/改/删 + 两段式删除 + 按钮接线 OK");

                            // 角色上云（2026-10-15 批次 8）：本地改动置 dirty → 云端整表替换落地清 dirty
                            // → 真读盘核对（TEMP 落盘、零网络 —— 编排器被 SmokeMode 闸住）。
                            // ⚠️ 本段之后不得再断言 av.RoleRows（行集合已是旧内容）。
                            if (!roleService.IsDirty)
                                throw new InvalidOperationException("[smoke] 角色本地改动后没置 dirty");
                            await roleService.ReplaceFromCloudAsync(
                            [
                                new ComposeRole
                                {
                                    Id = "smoke-cloud-role",
                                    Name = "冒烟云端角色",
                                    Template = "{内容}",
                                    Persona = "",
                                },
                            ]);
                            if (roleService.IsDirty)
                                throw new InvalidOperationException("[smoke] 角色云端替换后没清 dirty");
                            var roleReload = await roleStore.LoadAsync();   // store 无缓存：真读盘
                            if (roleReload.Roles.Count != 1 || roleReload.Roles[0].Name != "冒烟云端角色" || roleReload.Dirty)
                                throw new InvalidOperationException(
                                    $"[smoke] 角色云端替换没落盘：count={roleReload.Roles.Count} dirty={roleReload.Dirty}");
                            Console.WriteLine("[smoke] 角色上云：整表替换 + dirty 生命周期（TEMP 落盘，零网络）OK");
                        }
                    }

                    // 配音页特判：列表式批量 —— 加行 → 按行设音色/语速 → 真提交 → 断言进配音队列
                    //（按行快照 + 空行跳过 + 列表复位）；暂停/继续往返、勾选批量删除各走一遍，最后清场。
                    //（消费者被 SmokeMode 闸住不会真跑配音；队列存储在冒烟下走 TEMP 根，不碰用户 tts_queue.json）
                    if (pages.FirstOrDefault(i => i.Key == "tts") is { } ttsNav)
                    {
                        shell.Navigate(ttsNav);
                        await Task.Delay(400);
                        if (shell.CurrentPage is TtsViewModel tv)
                        {
                            var ttsQ = Services.GetRequiredService<TtsQueueViewModel>();
                            if (ttsQ.IsConsumerStarted)
                                throw new InvalidOperationException("[smoke] 配音队列消费者不该在冒烟里启动");

                            if (!KokoroModels.IsReady)
                            {
                                // 提交前会校验 Kokoro 就绪 —— 模型没下载的机器上跳过（不误报红）
                                Console.WriteLine("[smoke] 配音页批量：Kokoro 模型未下载，跳过提交断言");
                            }
                            else
                            {
                                var ttsVoices = tv.Voices;
                                if (ttsVoices.Count == 0)
                                    throw new InvalidOperationException("[smoke] 配音页没有可选音色");

                                var before = ttsQ.Tasks.Count;
                                tv.BatchRows.Clear();
                                tv.AddBatchRowCommand.Execute(null);
                                tv.AddBatchRowCommand.Execute(null);
                                tv.AddBatchRowCommand.Execute(null);
                                // 新条目必须自带默认音色 —— 提交要按行定位引擎（旧版踩过「拿不到引擎回落 Edge」）
                                if (tv.BatchRows.Any(r => r.Voice is null))
                                    throw new InvalidOperationException("[smoke] 新条目没带默认音色");
                                tv.BatchRows[0].Text = "第一条测试文案。";
                                tv.BatchRows[1].Text = "第二条测试文案。";   // 第三行留空：提交时应跳过
                                tv.BatchRows[0].Speed = 1.2;                 // 音色 / 语速按行快照（用户要求每条独立）
                                tv.BatchRows[1].Speed = 0.9;
                                if (ttsVoices.Count > 1) tv.BatchRows[1].Voice = ttsVoices[^1];
                                tv.SubmitBatchCommand.Execute(null);

                                var added = ttsQ.Tasks.Count - before;
                                var t0 = added > 0 ? ttsQ.Tasks.Skip(before).First() : null;
                                var t1 = added > 1 ? ttsQ.Tasks.Skip(before + 1).First() : null;
                                if (added != 2 || t0 is null || t1 is null
                                    || t0.Text != "第一条测试文案。" || t0.Status != TtsTaskStatus.Pending)
                                    throw new InvalidOperationException(
                                        $"[smoke] 配音批量提交异常：added={added} t0={t0?.Status}");
                                if (Math.Abs(t0.Speed - 1.2) > 0.001 || Math.Abs(t1.Speed - 0.9) > 0.001
                                    || t0.ProviderName != ttsVoices[0].ProviderName)
                                    throw new InvalidOperationException(
                                        $"[smoke] 配音按行快照不对：t0 语速 {t0.Speed} / t1 语速 {t1.Speed} / 引擎 {t0.ProviderName}");
                                if (ttsVoices.Count > 1 && t1.VoiceId != ttsVoices[^1].ShortName)
                                    throw new InvalidOperationException("[smoke] 第二条音色没按行快照：" + t1.VoiceId);
                                if (string.IsNullOrEmpty(t0.SuggestedName) || !t0.SuggestedName.StartsWith("配音"))
                                    throw new InvalidOperationException("[smoke] 配音建议文件名不对：" + t0.SuggestedName);
                                if (tv.BatchRows.Count != 1 || tv.BatchRows[0].HasText || tv.BatchRows[0].Voice is null)
                                    throw new InvalidOperationException("[smoke] 提交后没复位成一条空行（带默认音色）");

                                // 暂停 / 继续 往返
                                ttsQ.TogglePauseCommand.Execute(null);
                                var pausedOk = ttsQ.IsPaused && ttsQ.PauseButtonText == "继续";
                                ttsQ.TogglePauseCommand.Execute(null);
                                if (!pausedOk || ttsQ.IsPaused)
                                    throw new InvalidOperationException("[smoke] 暂停/继续没来回");

                                // 勾选批量删除：排队中的先取消再删
                                foreach (var t in ttsQ.Tasks.Skip(before).ToList()) t.IsSelected = true;
                                ttsQ.DeleteSelectedCommand.Execute(null);
                                if (ttsQ.Tasks.Count != before)
                                    throw new InvalidOperationException(
                                        $"[smoke] 批量删除后还剩 {ttsQ.Tasks.Count - before} 条");
                                // 落盘往返（批次 7：会员级队列档）：删除后读回与内存一致（Persist 是 fire-and-forget）
                                await Task.Delay(250);
                                var tqBack = await Services.GetRequiredService<TtsQueueStore>().LoadAsync();
                                if (tqBack.Tasks.Count != before)
                                    throw new InvalidOperationException(
                                        $"[smoke] 配音队列落盘与内存不一致：disk={tqBack.Tasks.Count} mem={before}");
                                Console.WriteLine("[smoke] 配音页批量：提交 2 条（空行跳过 · 音色语速按行快照）→ 暂停/继续 → 勾选批量删除 OK");
                            }
                        }
                    }

                    // ── 日志清理（2026-10-14）：账号设置页「日志与存储」卡 —— 档位落盘 + 「立即清理」真执行 ──
                    // 清理服务在冒烟下走 TEMP 根（上面守卫断言过），这里造一个超期日志让它真被删
                    if (pages.FirstOrDefault(i => i.Key == "account") is { } accountNav)
                    {
                        shell.Navigate(accountNav);
                        await Task.Delay(300);
                        if (shell.CurrentPage is AccountViewModel av)
                        {
                            Directory.CreateDirectory(cleanupSvc.LogsDir);
                            var staleLog = Path.Combine(cleanupSvc.LogsDir, "log-newapp-smoke-old.log");
                            await File.WriteAllTextAsync(staleLog, "smoke");
                            File.SetLastWriteTime(staleLog, DateTime.Now.AddDays(-40));

                            var accountCmds = target.GetVisualDescendants().OfType<Button>()
                                .Select(b => b.Command).ToList();
                            foreach (var (cmd, label) in new (System.Windows.Input.ICommand? Cmd, string Label)[]
                                     {
                                         (av.SelectRet15Command, "保留15天"),
                                         (av.SelectRet30Command, "保留30天"),
                                         (av.CleanNowCommand, "立即清理"),
                                     })
                            {
                                if (cmd is not null && !accountCmds.Any(c => ReferenceEquals(c, cmd)))
                                    throw new InvalidOperationException($"[smoke] 日志卡「{label}」按钮没接上命令");
                            }

                            // 切 15 天：立即清理一次（顺带断言 40 天前的文件真被删 + 状态出数字）
                            await av.SelectRet15Command.ExecuteAsync(null);
                            if ((await logSettingsStore.LoadAsync()).RetentionDays != 15)
                                throw new InvalidOperationException("[smoke] 切 15 天没落盘");
                            if (File.Exists(staleLog))
                                throw new InvalidOperationException("[smoke] 改保留期没顺带清掉超期日志");
                            if (!av.CleanupStatus.Contains("已清理"))
                                throw new InvalidOperationException("[smoke] 清理状态文案不对：" + av.CleanupStatus);

                            await av.SelectRet30Command.ExecuteAsync(null);
                            if ((await logSettingsStore.LoadAsync()).RetentionDays != 30)
                                throw new InvalidOperationException("[smoke] 保留期复位 30 没落盘");
                            Console.WriteLine("[smoke] 账号设置页：日志卡（档位落盘 + 立即清理真删 + 接线）OK");

                            // 视频水印卡（2026-10-21）：载入 → 五角接线 → 改一笔 TEMP 落盘往返
                            var wmStore = Services.GetRequiredService<WatermarkSettingsStore>();
                            var wmBefore = await wmStore.LoadAsync();
                            if (av.WatermarkEnabled != wmBefore.Enabled || av.WatermarkText != wmBefore.Text
                                || av.WatermarkPos != wmBefore.Position)
                                throw new InvalidOperationException(
                                    $"[smoke] 账号页水印没载入：{av.WatermarkEnabled}/{av.WatermarkText}/{av.WatermarkPos}");
                            if (av.WatermarkSaveStatus != "改动自动保存" || av.WatermarkSaveStatusHex != "#86909C")
                                throw new InvalidOperationException(
                                    $"[smoke] 水印卡初始保存提示不对：{av.WatermarkSaveStatus}/{av.WatermarkSaveStatusHex}");

                            var wmCmds = target.GetVisualDescendants().OfType<Button>()
                                .Select(b => b.Command).ToList();
                            foreach (var (cmd, label) in new (System.Windows.Input.ICommand? Cmd, string Label)[]
                                     {
                                         (av.SelectWmTopLeftCommand, "水印左上"),
                                         (av.SelectWmTopRightCommand, "水印右上"),
                                         (av.SelectWmBottomLeftCommand, "水印左下"),
                                         (av.SelectWmBottomRightCommand, "水印右下"),
                                         (av.SelectWmRandomCommand, "水印随机"),
                                     })
                            {
                                if (cmd is not null && !wmCmds.Any(c => ReferenceEquals(c, cmd)))
                                    throw new InvalidOperationException($"[smoke] 水印卡「{label}」按钮没接上命令");
                            }

                            av.SelectWmRandomCommand.Execute(null);
                            av.WatermarkText = "冒烟水印二";
                            av.WatermarkEnabled = false;
                            await Task.Delay(300);   // 保存是 fire-and-forget（串行化落盘）
                            var wmBack = await wmStore.LoadAsync();
                            if (wmBack is not { Enabled: false, Text: "冒烟水印二", Position: WatermarkPosition.Random })
                                throw new InvalidOperationException(
                                    $"[smoke] 水印卡没落盘：{wmBack.Enabled}/{wmBack.Text}/{wmBack.Position}");
                            if (!av.WatermarkSaveStatus.StartsWith("已保存", StringComparison.Ordinal)
                                || av.WatermarkSaveFailed || av.WatermarkSaveStatusHex != "#0A5C28")
                                throw new InvalidOperationException(
                                    $"[smoke] 水印卡保存提示不对：{av.WatermarkSaveStatus}/{av.WatermarkSaveStatusHex}");
                            Console.WriteLine("[smoke] 账号设置页：视频水印卡（载入/五角接线/TEMP 落盘往返/保存提示）OK");

                            // 上传提示音卡（2026-10-22 滑块版）：载入 → 滑块 + 试听接线 → 拖到 35%
                            // → TEMP 落盘往返 → 静态音量立即生效。**不执行试听**（冒烟会真出声）
                            var sndBefore = await sndStore.LoadAsync();
                            if (Math.Abs(av.SoundVolumePercent - sndBefore.VolumePercent) > 0.001)
                                throw new InvalidOperationException(
                                    $"[smoke] 提示音卡没载入：{av.SoundVolumePercent} != {sndBefore.VolumePercent}");
                            if (av.IsSoundOff != (sndBefore.VolumePercent == 0))
                                throw new InvalidOperationException(
                                    $"[smoke] 提示音「关闭」判定不对：{sndBefore.VolumePercent}%");

                            var sndSlider = target.GetVisualDescendants().OfType<Slider>()
                                .FirstOrDefault(s => s.Minimum == 0 && s.Maximum == 100);
                            if (sndSlider is null)
                                throw new InvalidOperationException("[smoke] 提示音卡的音量滑块没渲染出来");
                            if (!target.GetVisualDescendants().OfType<Button>()
                                    .Any(b => ReferenceEquals(b.Command, av.PreviewSoundCommand)))
                                throw new InvalidOperationException("[smoke] 提示音卡「试听」按钮没接上命令");

                            av.SoundVolumePercent = 35;
                            await Task.Delay(300);   // 保存是 fire-and-forget（串行化落盘）
                            var sndBack = await sndStore.LoadAsync();
                            if (sndBack.VolumePercent != 35)
                                throw new InvalidOperationException(
                                    $"[smoke] 提示音滑块没落盘：{sndBack.VolumePercent}%");
                            if (PublishSoundNotifier.VolumePercent != 35)
                                throw new InvalidOperationException(
                                    $"[smoke] 提示音滑块没立即生效：{PublishSoundNotifier.VolumePercent}%");
                            if (Math.Abs(sndSlider.Value - 35) > 0.001 || av.SoundVolumeText != "35%")
                                throw new InvalidOperationException(
                                    $"[smoke] 提示音滑块视图没跟上：{sndSlider.Value} / {av.SoundVolumeText}");
                            if (!av.SoundSaveStatus.StartsWith("已保存", StringComparison.Ordinal)
                                || av.SoundSaveFailed || av.SoundSaveStatusHex != "#0A5C28")
                                throw new InvalidOperationException(
                                    $"[smoke] 提示音保存提示不对：{av.SoundSaveStatus}/{av.SoundSaveStatusHex}");

                            // 复位默认 70%（后续没有依赖声音的断言，纯粹不留造景）
                            av.SoundVolumePercent = SoundSettingsStore.DefaultVolumePercent;
                            await Task.Delay(250);
                            Console.WriteLine("[smoke] 账号设置页：上传提示音卡（滑块载入/接线/TEMP 落盘往返/立即生效）OK");

                            // 清除本机数据卡（2026-10-28，doc 11.3）：只验可见性 + 确认词闸 + 接线。
                            // **绝不执行确认命令** —— 它会删 TEMP 会员目录并触发退出，后续冒烟段还要用库。
                            if (AppConfig.IsStandalone)
                            {
                                if (!av.ShowClearData || av.ShowClearConfirm)
                                    throw new InvalidOperationException("[smoke] 清除本机数据卡初始状态不对");
                                if (!av.ClearExportBackup || av.ClearMachineLevel)
                                    throw new InvalidOperationException(
                                        "[smoke] 清除选项默认值不对（应默认导出备份、不删机器级）");
                                if (!target.GetVisualDescendants().OfType<Button>()
                                        .Any(b => ReferenceEquals(b.Command, av.ClearDataBeginCommand)))
                                    throw new InvalidOperationException("[smoke] 清除数据卡「清除本机数据」按钮没接上命令");

                                av.ClearDataBeginCommand.Execute(null);
                                if (!av.ShowClearConfirm || av.CanConfirmClear)
                                    throw new InvalidOperationException("[smoke] 清除确认条/空确认词闸不对");
                                var clearCmds = target.GetVisualDescendants().OfType<Button>()
                                    .Select(b => b.Command).ToList();
                                foreach (var (cmd, label) in new (System.Windows.Input.ICommand? Cmd, string Label)[]
                                         {
                                             (av.ClearDataCancelCommand, "取消清除"),
                                             (av.ClearDataConfirmCommand, "确认清除并退出"),
                                         })
                                {
                                    if (cmd is not null && !clearCmds.Any(c => ReferenceEquals(c, cmd)))
                                        throw new InvalidOperationException($"[smoke] 清除数据卡「{label}」按钮没接上命令");
                                }

                                av.ClearConfirmWord = "清除";
                                if (!av.CanConfirmClear)
                                    throw new InvalidOperationException("[smoke] 输入确认词后仍不可确认");
                                av.ClearConfirmWord = "清除数据";
                                if (av.CanConfirmClear)
                                    throw new InvalidOperationException("[smoke] 错误确认词也能确认");
                                av.ClearDataCancelCommand.Execute(null);
                                if (av.ShowClearConfirm)
                                    throw new InvalidOperationException("[smoke] 取消后确认条没收起");
                                Console.WriteLine("[smoke] 账号设置页：清除本机数据卡（可见/确认词闸/接线，未执行清除）OK");
                            }
                        }
                    }

                    // ── 联系我们（2026-10-22）：服务端公开配置 contact.content 的消费端 ──
                    // 冒烟不碰网络（VM 的 SmokeMode 分支直接回落本地）；这里守住：
                    // 导航在账号设置下、过期放行、本地兜底可读、多邮箱分行、刷新按钮接线。
                    if (pages.FirstOrDefault(i => i.Key == "contact") is { } contactNav)
                    {
                        if (!contactNav.AllowedWhenExpired)
                            throw new InvalidOperationException("[smoke] 联系我们页没对过期用户放行");
                        shell.Navigate(contactNav);
                        await Task.Delay(300);
                        if (shell.CurrentPage is not ContactViewModel cv)
                            throw new InvalidOperationException("[smoke] 联系我们页没建起来");
                        if (string.IsNullOrWhiteSpace(cv.Html))
                            throw new InvalidOperationException("[smoke] 联系我们页兜底内容为空");
                        foreach (var mail in AppConfig.ContactEmails)
                        {
                            if (!cv.Html.Contains(mail))
                                throw new InvalidOperationException(
                                    $"[smoke] 联系我们兜底内容缺邮箱：{mail}");
                        }
                        if (AppConfig.ContactEmails.Count > 1 && !cv.Html.Contains("<br/>"))
                            throw new InvalidOperationException("[smoke] 多邮箱没有分行（缺 <br/>）");
                        if (AppConfig.IsStandalone)
                        {
                            // 单机版（2026-10-28）：原生联系卡 + 无刷新按钮；内容来自 appsettings
                            var localCard = target.GetVisualDescendants().OfType<Border>()
                                .FirstOrDefault(b => b.Name == "LocalCard");
                            if (localCard is null || !localCard.IsVisible
                                || localCard.Bounds.Width <= 0 || localCard.Bounds.Height <= 0)
                                throw new InvalidOperationException(
                                    $"[smoke] 单机版联系卡没显示：{localCard?.Bounds}");
                            foreach (var mail in AppConfig.ContactEmails)
                                if (!cv.EmailsText.Contains(mail))
                                    throw new InvalidOperationException($"[smoke] 单机版联系卡缺邮箱：{mail}");
                            if (string.IsNullOrWhiteSpace(cv.WorkHours))
                                throw new InvalidOperationException("[smoke] 单机版联系卡缺工作时间");
                            var refresh = target.GetVisualDescendants().OfType<Button>()
                                .FirstOrDefault(b => ReferenceEquals(b.Command, cv.LoadCommand));
                            if (refresh is { IsVisible: true })
                                throw new InvalidOperationException("[smoke] 单机版还显示刷新按钮");
                            Console.WriteLine(
                                "[smoke] 联系我们（单机版）：导航/本地卡/邮箱与工作时间/无刷新按钮 OK（零网络）");
                        }
                        else
                        {
                            if (!target.GetVisualDescendants().OfType<Button>()
                                    .Any(b => ReferenceEquals(b.Command, cv.LoadCommand)))
                                throw new InvalidOperationException("[smoke] 联系我们页刷新按钮没接上命令");

                            // 富文本控件必须真在可视树里且量出了尺寸（AutoSizeHeightOnly 没生效就是 0 高）
                            var htmlBlock = target.GetVisualDescendants()
                                .OfType<TheArtOfDev.HtmlRenderer.Avalonia.HtmlLabel>()
                                .FirstOrDefault(h => h.Text == cv.Html);
                            if (htmlBlock is null || htmlBlock.Bounds.Width <= 0 || htmlBlock.Bounds.Height <= 0)
                                throw new InvalidOperationException(
                                    $"[smoke] 联系我们富文本控件没渲染出尺寸：{htmlBlock?.Bounds}");

                            Console.WriteLine("[smoke] 联系我们（联网版）：富文本尺寸/刷新接线 OK（零网络）");
                        }
                    }

                    // ── 账号分组（2026-10-14）：平台页分组卡 + 发布页/合成页组条 ──
                    //（groupsSvc 已上提到合成队列段之前 —— 那里也要造景组条）
                    var smokeGroupId = Guid.NewGuid().ToString("N");

                    if (pages.FirstOrDefault(i => i.Key == "platform") is { } platformNav)
                    {
                        shell.Navigate(platformNav);
                        await Task.Delay(400);
                        if (shell.CurrentPage is PlatformViewModel plv)
                        {
                            await groupsSvc.SaveAsync(new AccountGroup
                            {
                                Id = smokeGroupId,
                                Name = "冒烟宠物",
                                Sessions = [Guid.NewGuid(), Guid.NewGuid()],   // 假会话（离线也测管理流）
                            });
                            // 直接刷（离线时 LoadAsync 早退到不了这里）；不剪除 —— 假成员不在册
                            await plv.RefreshGroupsAsync(pruneAllowed: false);
                            if (plv.GroupRows.Count != 1 || plv.GroupRows[0].Name != "冒烟宠物")
                                throw new InvalidOperationException(
                                    $"[smoke] 分组卡行不对：{plv.GroupRows.Count} 行");
                            await Task.Delay(300);   // 等行模板实例化（可视树断言在布局之后）

                            var plCmds = target.GetVisualDescendants().OfType<Button>()
                                .Select(b => b.Command).ToList();
                            foreach (var (cmd, label) in new (System.Windows.Input.ICommand? Cmd, string Label)[]
                                     {
                                         (plv.AddGroupCommand, "新建分组"),
                                         (plv.BeginEditGroupCommand, "编辑"),
                                         (plv.BeginDeleteGroupCommand, "删除"),
                                         (plv.SaveGroupCommand, "保存"),
                                         (plv.CancelEditGroupCommand, "取消"),
                                         (plv.ConfirmDeleteGroupCommand, "确认删除"),
                                     })
                            {
                                if (cmd is not null && !plCmds.Any(c => ReferenceEquals(c, cmd)))
                                    throw new InvalidOperationException($"[smoke] 分组卡「{label}」按钮没接上命令");
                            }

                            // 新建 → 填名 → 保存（TEMP 落盘多一组；账号清单离线时为空，只验管理流）
                            plv.AddGroupCommand.Execute(null);
                            if (plv.GroupRows.Count != 2 || !plv.GroupRows[1].IsEditing || !plv.GroupRows[1].IsNew)
                                throw new InvalidOperationException("[smoke] 新建分组没进编辑态");
                            plv.GroupRows[1].Name = "冒烟房产";
                            await plv.SaveGroupCommand.ExecuteAsync(plv.GroupRows[1]);
                            if ((await groupStore.LoadAsync()).Groups.Count != 2)
                                throw new InvalidOperationException("[smoke] 保存分组没落盘");

                            // 删除两段式：取消复位 → 再现确认 → 真删
                            var delRow = plv.GroupRows.First(r => r.Name == "冒烟房产");
                            plv.BeginDeleteGroupCommand.Execute(delRow);
                            if (!delRow.IsConfirmingDelete)
                                throw new InvalidOperationException("[smoke] 分组删除没摆确认条");
                            plv.CancelDeleteGroupCommand.Execute(delRow);
                            if (delRow.IsConfirmingDelete)
                                throw new InvalidOperationException("[smoke] 分组删除确认条没复位");
                            plv.BeginDeleteGroupCommand.Execute(delRow);
                            await plv.ConfirmDeleteGroupCommand.ExecuteAsync(delRow);
                            if ((await groupStore.LoadAsync()).Groups.Count != 1)
                                throw new InvalidOperationException("[smoke] 确认删除后存档没少");
                            Console.WriteLine("[smoke] 平台页：分组卡（新建/保存/两段删除 + 接线，TEMP 落盘）OK");
                        }
                    }

                    // 抖音身份验证引导（2026-10-29）：passport authlv / 上传凭证 status_code=8 被拦时，
                    // 要把该账号标签亮出来并落到发布页 —— 回调由 App 开壳时挂上，漏挂就只剩干巴巴的错误文案。
                    if (DouyinWebExecutor.VerifyRequester is null)
                        throw new InvalidOperationException("[smoke] 抖音验证引导没接线（VerifyRequester 为空）");
                    Console.WriteLine("[smoke] 抖音验证引导：VerifyRequester 已接线 OK");

                    if (pages.FirstOrDefault(i => i.Key == "publish") is { } publishNav)
                    {
                        shell.Navigate(publishNav);
                        await Task.Delay(300);
                        if (shell.CurrentPage is PublishViewModel pv2)
                        {
                            // 先排干导航触发的在飞加载（首次进页 = LoadAsync，里头还会重建账号芯片）
                            // 再造景 —— 晚到的重建会把假芯片洗掉，组条断言就会间歇性炸
                            await pv2.DrainNavigatedAsync();

                            var g1 = Guid.NewGuid();
                            var g2 = Guid.NewGuid();
                            var g3 = Guid.NewGuid();
                            await groupsSvc.SaveAsync(new AccountGroup
                            {
                                Id = smokeGroupId,
                                Name = "冒烟宠物",
                                Sessions = [g1, g2],
                            });

                            pv2.GlobalTargets.Clear();
                            foreach (var (sid, plat, name) in new (Guid Sid, string Plat, string Name)[]
                                     {
                                         (g1, "douyin", "抖音"),
                                         (g2, "kuaishou", "快手"),
                                         (g3, "douyin", "抖音"),
                                     })
                            {
                                pv2.GlobalTargets.Add(new AccountChipViewModel
                                {
                                    SessionId = sid,
                                    PlatformKey = plat,
                                    PlatformName = name,
                                    AccountLabel = "冒烟号",
                                    BrandHex = "#6C5CE7",
                                });
                            }
                            await pv2.RefreshGroupChoicesAsync();
                            if (pv2.GroupChoices.Count != 2 || !pv2.HasGroupChoices)
                                throw new InvalidOperationException(
                                    $"[smoke] 发布页组条格数不对：{pv2.GroupChoices.Count}");
                            var petChoice = pv2.GroupChoices.First(c => c.Name == "冒烟宠物");
                            if (petChoice.Text != "冒烟宠物 2")
                                throw new InvalidOperationException("[smoke] 组格文案不对：" + petChoice.Text);
                            // 等组条实例化 —— 等待循环里就地快照，断言用这份快照
                            List<System.Windows.Input.ICommand?> pbCmds = [];
                            await WaitVisualAsync(() =>
                            {
                                pbCmds = target.GetVisualDescendants().OfType<Button>()
                                    .Select(b => b.Command).ToList();
                                return pbCmds.Any(c => ReferenceEquals(c, pv2.ApplyGroupCommand));
                            });
                            if (!pbCmds.Any(c => ReferenceEquals(c, pv2.ApplyGroupCommand)))
                                throw new InvalidOperationException("[smoke] 发布页组条按钮没接上命令");

                            await pv2.ApplyGroupCommand.ExecuteAsync(petChoice);
                            var onNow = pv2.GlobalTargets.Where(c => c.IsOn)
                                .Select(c => c.SessionId).ToHashSet();
                            if (!onNow.SetEquals([g1, g2]))
                                throw new InvalidOperationException("[smoke] 点组名没把勾选换成该组");
                            if (pv2.TargetCountText != "2 / 3 个账号")
                                throw new InvalidOperationException("[smoke] 计数没跟着变：" + pv2.TargetCountText);
                            if (!petChoice.IsActive)
                                throw new InvalidOperationException("[smoke] 点完该组没高亮");
                            var selNow = await selStore.LoadAsync();
                            if (selNow.OffSessions.Count != 1 || selNow.OffSessions[0] != g3)
                                throw new InvalidOperationException(
                                    "[smoke] 整组勾选没一次落盘（OffSessions=" +
                                    string.Join(",", selNow.OffSessions) + "）");

                            await pv2.ApplyGroupCommand.ExecuteAsync(pv2.GroupChoices[0]);   // 全部
                            if (pv2.GlobalTargets.Any(c => !c.IsOn) || !pv2.GroupChoices[0].IsActive)
                                throw new InvalidOperationException("[smoke] 「全部」没全勾/没高亮");
                            if ((await selStore.LoadAsync()).OffSessions.Count != 0)
                                throw new InvalidOperationException("[smoke] 「全部」后 OffSessions 没清空");

                            // 手动微调 → 都不恰好相等 → 高亮全灭
                            pv2.GlobalTargets[0].IsOn = false;
                            if (pv2.GroupChoices.Any(c => c.IsActive))
                                throw new InvalidOperationException("[smoke] 部分勾选不该有任何组高亮");

                            pv2.GlobalTargets.Clear();   // 清场（假芯片别留给后续断言）
                            Console.WriteLine("[smoke] 发布页：分组组条（一键只勾该组 + 一次落盘 + 高亮）OK");
                        }
                    }

                    if (pages.FirstOrDefault(i => i.Key == "compose") is { } composeNav2)
                    {
                        shell.Navigate(composeNav2);
                        await Task.Delay(300);
                        if (shell.CurrentPage is ComposeViewModel cv2)
                        {
                            // 同发布页：OnNavigatedTo 的芯片刷新是 fire-and-forget，
                            // 不排干就造景，晚到的刷新会把假芯片/组条高亮一起洗掉
                            await cv2.DrainNavigatedAsync();

                            var c1 = Guid.NewGuid();
                            var c2 = Guid.NewGuid();
                            var c3 = Guid.NewGuid();   // 组外芯片：让「该组 ≠ 全部」——全勾时高亮的是「全部」格
                            await groupsSvc.SaveAsync(new AccountGroup
                            {
                                Id = smokeGroupId,
                                Name = "冒烟宠物",
                                Sessions = [c1, c2],
                            });

                            cv2.TargetChips.Clear();
                            foreach (var (sid, plat, name) in new (Guid Sid, string Plat, string Name)[]
                                     {
                                         (c1, "douyin", "抖音"),
                                         (c2, "kuaishou", "快手"),
                                         (c3, "douyin", "抖音"),
                                     })
                            {
                                cv2.TargetChips.Add(new AccountChipViewModel
                                {
                                    SessionId = sid,
                                    PlatformKey = plat,
                                    PlatformName = name,
                                    AccountLabel = "冒烟号",
                                    BrandHex = "#6C5CE7",
                                });
                            }
                            cv2.AutoPublish = true;   // 摘要联动断言要开着
                            await cv2.RefreshGroupChoicesAsync();
                            var petC = cv2.GroupChoices.FirstOrDefault(c => c.Name == "冒烟宠物");
                            if (petC is null)
                                throw new InvalidOperationException("[smoke] 合成页组条没有造景组");
                            // 等组条实例化 —— 等待循环里就地快照，断言用这份快照
                            List<System.Windows.Input.ICommand?> cbCmds = [];
                            await WaitVisualAsync(() =>
                            {
                                cbCmds = target.GetVisualDescendants().OfType<Button>()
                                    .Select(b => b.Command).ToList();
                                return cbCmds.Any(c => ReferenceEquals(c, cv2.ApplyTargetGroupCommand));
                            });
                            if (!cbCmds.Any(c => ReferenceEquals(c, cv2.ApplyTargetGroupCommand)))
                                throw new InvalidOperationException("[smoke] 合成页组条按钮没接上命令");

                            await cv2.ApplyTargetGroupCommand.ExecuteAsync(petC);
                            var onC = cv2.TargetChips.Where(c => c.IsOn).Select(c => c.SessionId).ToHashSet();
                            if (!onC.SetEquals([c1, c2]) || !petC.IsActive)
                                throw new InvalidOperationException("[smoke] 合成页点组名没把勾选换成该组");
                            if (!cv2.AutoPublishSummary.Contains("将发布到 2 个账号"))
                                throw new InvalidOperationException("[smoke] 摘要没联动：" + cv2.AutoPublishSummary);

                            cv2.AutoPublish = false;   // 复位，别留脏状态
                            cv2.TargetChips.Clear();
                            Console.WriteLine("[smoke] 合成页：分组组条（一键只勾该组 + 摘要联动）OK");
                        }
                    }

                    // 清场：分组存档归零（TEMP 根，本就没有用户数据）
                    await groupsSvc.DeleteAsync(smokeGroupId);

                    // ── 分组上云（2026-10-15）：整表替换 + dirty 生命周期（TEMP 落盘，零网络）──
                    // 推拉编排（AccountGroupCloudSync）在冒烟里整体被闸（不接 Changed + 入口 SmokeMode），
                    // 这里只钉本地语义：改动置 dirty → 云端替换整体落地 + 清 dirty → 跨实例读盘验证。
                    if (!groupsSvc.IsDirty)
                        throw new InvalidOperationException("[smoke] 本地改动后没置 dirty");
                    var cloudGroupId = Guid.NewGuid().ToString("N");
                    await groupsSvc.ReplaceFromCloudAsync([
                        new AccountGroup { Id = cloudGroupId, Name = "冒烟云端组", Sessions = [Guid.NewGuid()] }]);
                    if (groupsSvc.IsDirty)
                        throw new InvalidOperationException("[smoke] 云端替换后没清 dirty");
                    var reloaded = await groupStore.LoadAsync();   // store 无缓存：真读盘
                    if (reloaded.Groups.Count != 1 || reloaded.Groups[0].Name != "冒烟云端组" || reloaded.Dirty)
                        throw new InvalidOperationException(
                            $"[smoke] 云端替换没落盘/没清 dirty：{reloaded.Groups.Count} 组");
                    await groupsSvc.DeleteAsync(cloudGroupId);      // 再清场
                    Console.WriteLine("[smoke] 分组上云：整表替换 + dirty 生命周期（TEMP 落盘，零网络）OK");

                    shell.Navigate(pages.FirstOrDefault(i => i.Key == "home")!);
                    await Task.Delay(500);

                    // 首页入口卡特判：卡片点入是唯一的「页 VM 请求壳跳页」路径
                    //（HomePageViewModel.OnNavigateRequested，壳在 Navigate 里注入）——
                    // 静默失效只有肉眼能发现，所以逐个执行入口命令、断言落到目标页。
                    // 过期会员断言的是「被拦住」：Navigate 本身不看 IsEnabled，拦截在 VM 的命令里。
                    if (shell.CurrentPage is HomePageViewModel home)
                    {
                        // 平台账号卡：2026-11-05 用户要求补齐 B站/头条（6 平台）
                        if (home.Platforms.Count != 6
                            || home.Platforms.All(p => p.PlatformKey != PlatformNames.Bilibili)
                            || home.Platforms.All(p => p.PlatformKey != PlatformNames.Toutiao))
                        {
                            throw new InvalidOperationException(
                                $"[smoke] 首页平台卡应为 6 平台（含 B站/头条），实际 {home.Platforms.Count}");
                        }
                        Console.WriteLine("[smoke] 首页平台卡：6 平台（含 B站/头条）");

                        foreach (var (key, pageType) in new (string, Type)[]
                                 {
                                     ("publish", typeof(PublishViewModel)),
                                     ("queue", typeof(UploadQueueViewModel)),
                                     ("tasks", typeof(AsyncTaskViewModel)),
                                     ("video", typeof(VideoManageViewModel)),
                                     ("tts", typeof(TtsViewModel)),
                                     ("compose", typeof(ComposeViewModel)),
                                     ("ai", typeof(AiSettingsViewModel)),
                                 })
                        {
                            home.GoCommand.Execute(key);
                            await Task.Delay(300);
                            var expected = home.IsExpired ? typeof(HomePageViewModel) : pageType;
                            if (shell.CurrentPage?.GetType() != expected)
                            {
                                throw new InvalidOperationException(
                                    $"[smoke] 首页入口 {key}: 期望 {expected.Name}，" +
                                    $"实际 {shell.CurrentPage?.GetType().Name}");
                            }
                        }
                        Console.WriteLine($"[smoke] 首页入口卡：7 个入口都落到目标页（过期会员={home.IsExpired}）");
                        shell.Navigate(pages.First(i => i.Key == "home")!); // 回首页目检
                        await Task.Delay(250);
                    }

                    // ── 清除本机数据：完整路径真执行一次（TEMP 夹具；导出 → 清除 → 请求退出）──
                    // 放在最后：清完库就没了，后面的断言不再碰本地数据；RequestShutdown 换成计数，
                    // 真正的应用退出由冒烟自己的 desktop.Shutdown() 完成。
                    if (AppConfig.IsStandalone)
                    {
                        shell.Navigate(pages.First(i => i.Key == "account"));
                        await Task.Delay(350);
                        if (shell.CurrentPage is not AccountViewModel wipeVm)
                            throw new InvalidOperationException("[smoke] 清除数据：账号页没建起来");

                        var backupZip = Path.Combine(Path.GetTempPath(), "cf-smoke-clear-backup.zip");
                        try { File.Delete(backupZip); } catch { }
                        var shutdownRequested = false;
                        wipeVm.RequestShutdown = () => shutdownRequested = true;
                        wipeVm.PickExportPath = () => Task.FromResult<string?>(backupZip);
                        wipeVm.ClearExportBackup = true;     // 走「先导出再清除」的推荐路径
                        wipeVm.ClearMachineLevel = false;
                        wipeVm.ClearDataBeginCommand.Execute(null);
                        wipeVm.ClearConfirmWord = AccountViewModel.ClearConfirmKeyword;
                        await wipeVm.ClearDataConfirmCommand.ExecuteAsync(null);

                        if (!shutdownRequested)
                            throw new InvalidOperationException("[smoke] 清除数据：完成后没请求退出");
                        if (Directory.Exists(LocalStorePaths.MemberDir))
                            throw new InvalidOperationException("[smoke] 清除数据：会员目录仍在");
                        if (!File.Exists(backupZip) || new FileInfo(backupZip).Length == 0)
                            throw new InvalidOperationException("[smoke] 清除数据：清除前的导出备份不存在");
                        Console.WriteLine("[smoke] 清除本机数据：导出备份 → 删会员目录 → 请求退出（TEMP 真执行）OK");
                    }
                }
            }

            await Task.Delay(2000); // 留足最后页面目检时间

            RequestedThemeVariant = ThemeVariant.Dark;
            await Task.Delay(500);
            RequestedThemeVariant = ThemeVariant.Light;
            await Task.Delay(500);

            var ok = $"SMOKE OK styles={Styles.Count} window={target.Width:0}x{target.Height:0} title={target.Title}";
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "newapp_smoke.txt"), ok);
            desktop.Shutdown();
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "newapp_smoke.txt"),
                    "SMOKE FAIL " + ex);
            }
            catch { }
            desktop.Shutdown();
        }
    }

    /// <summary>
    /// 冒烟造景用的样例素材（仓库 video/ 目录）：1 个视频 + 2 张图。
    /// 只读、不写；找不到（CI/打包环境）就返回空，调用方跳过造景。
    /// 相对路径手法与 PublishSelfTest.FindSampleVideo 一致（bin 往上数到仓库根）。
    /// </summary>
    private static IReadOnlyList<string> FindComposeSamples()
    {
        var roots = new[]
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", ".."),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "..", ".."),
        };
        foreach (var root in roots)
        {
            try
            {
                var full = Path.GetFullPath(root);
                var video = Path.Combine(full, "video", "video", "1", "1.mp4");
                var imgDir = Path.Combine(full, "video", "img");
                if (!File.Exists(video) || !Directory.Exists(imgDir)) continue;
                var imgs = Directory.EnumerateFiles(imgDir, "*.png")
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Take(2).ToList();
                if (imgs.Count == 0) continue;
                return [video, .. imgs];
            }
            catch { /* 路径不存在/无权限 —— 试下一个 */ }
        }
        return [];
    }

}
