using System.Diagnostics;
using CreatFlow.Platforms.Core;
using CreatFlow.Platforms.Download;
using Material.Icons;

namespace CreatFlow.NewApp.ViewModels;

/// <summary>
/// 视频下载页（DI 单例 = 页面 + 消费者同体，仿合成 / 配音队列）：
///  1. 粘贴链接 / 分享文案 → **批量解析预览**（不下载）→ 勾选 → 入队；
///  2. 本机并发下载（同平台串行、跨平台最多 <see cref="Concurrency"/> 条），
///     同一条作品重复下载落到同一路径（`{title} [{id}].{ext}`），天然幂等；
///  3. 解析与下载分离：播放地址带时效签名，超过 <see cref="FormatTtl"/> 会重新解析。
///
/// 许可门槛：页面入口由壳门控；消费者领取任务前再查 <see cref="FeatureGate"/>（页面开着、
/// 会员中途过期时兜底）。登出 / 撤权由 App 层调 <see cref="PauseBecause"/>。
/// </summary>
public partial class DownloadQueueViewModel : ViewModelBase
{
    /// <summary>全局并发（跨平台）。同平台仍串行 —— 签名/会话按账号共享，并发容易触发风控。</summary>
    public static int Concurrency { get; } = 2;

    /// <summary>解析结果的有效期：超过就重新解析（平台播放地址带签名，几十分钟就过期）。</summary>
    private static readonly TimeSpan FormatTtl = TimeSpan.FromMinutes(3);

    private const int MaxRetries = 3;

    private readonly DownloadQueueStore _store;
    private readonly SessionContext _session;
    private readonly DownloadEngine _engine;
    private readonly Func<PlatformAccountStore> _accounts;

    private readonly SemaphoreSlim _signal = new(0);
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    /// <summary>运行中任务的取消令牌（只在 UI 线程读写）</summary>
    private readonly Dictionary<DownloadTaskRow, CancellationTokenSource> _runCts = [];

    /// <summary>平台 → 解析上下文（Cookie / 指纹）缓存，避免每条都打一次凭据接口</summary>
    private readonly Dictionary<string, (ExtractContext Context, DateTime At)> _contextCache = [];

    private CancellationTokenSource? _loopCts;
    private bool _started;
    private bool _loaded;
    private bool _suppressPersist;
    private bool _switching;
    private Guid _loadedMember;

    /// <summary>许可停摆原因（App 层注销/撤权时设置；与用户主动暂停分开 —— 后者进存档，
    /// 前者只在内存，重新登录后由壳调 <see cref="Resume"/> 自动接续）</summary>
    private string? _gateReason;

    public DownloadQueueViewModel(
        DownloadQueueStore store,
        SessionContext session,
        DownloadEngine engine,
        Func<PlatformAccountStore> accounts)
    {
        _store = store;
        _session = session;
        _engine = engine;
        _accounts = accounts;

        Tasks.CollectionChanged += (_, _) =>
        {
            NotifySummary();
            NotifyPathPreview();
        };
        RootDirectory = OutputPathOptions.DefaultRoot();
        NotifyPathPreview();
    }

    // ═══ 页头 / 汇总 ═══

    public MaterialIconKind HeaderIcon => MaterialIconKind.Download;

    /// <summary>
    /// 抖音那条必须写出来：它的无水印源只能靠账号页面的签名拿，
    /// 账号窗口没开时不再退化成带水印的下载版，而是直接报错。
    /// </summary>
    public string HeaderHint =>
        $"批量下载到本机（同时最多 {Concurrency} 条，同平台串行）：先解析预览、勾选后再入队；" +
        "链接失效会自动重新解析，关掉重开能接着跑。抖音需先在「平台账号」点开抖音账号窗口（借用页面签名）。";

    public ObservableCollection<DownloadTaskRow> Tasks { get; } = [];

    /// <summary>统计变化回调（壳刷侧栏徽章）—— 都在 UI 线程触发</summary>
    public Action? OnStatsChanged { get; set; }

    public IReadOnlyList<string> Strategies { get; } =
        ["最高画质（音视频合一优先）", "最高画质（音视频分离）", "1080p 以内", "只要音频", "体积最小"];

    public bool HasTasks => Tasks.Count > 0;
    public bool IsEmpty => Tasks.Count == 0;
    public int ReadyCount => Tasks.Count(t => t.Status == DownloadTaskStatus.Ready);
    public int QueuedCount => Tasks.Count(t => t.Status == DownloadTaskStatus.Queued);
    public int RunningCount => Tasks.Count(t => t.IsRunning);
    public int DoneCount => Tasks.Count(t => t.Status == DownloadTaskStatus.Done);
    public int FailedCount => Tasks.Count(t => t.Status is DownloadTaskStatus.Failed
        or DownloadTaskStatus.Interrupted);

    /// <summary>徽章：未落地条数（排队 + 运行 + 失败/中断）—— 预览态（Ready）不算，避免只是「看一眼」就冒红点</summary>
    public int BadgeCount => Tasks.Count(t => t.Status is DownloadTaskStatus.Queued
        or DownloadTaskStatus.Downloading or DownloadTaskStatus.Merging
        or DownloadTaskStatus.Failed or DownloadTaskStatus.Interrupted);

    public string SummaryText =>
        $"共 {Tasks.Count} 条 · 待下载 {ReadyCount} · 排队 {QueuedCount} · 下载中 {RunningCount} · " +
        $"完成 {DoneCount} · 失败/中断 {Tasks.Count(t => t.Status is DownloadTaskStatus.Failed or DownloadTaskStatus.Interrupted)}";

    /// <summary>消费者是否已启动（冒烟断言用）</summary>
    public bool IsConsumerStarted => _started;

    // ═══ 输入 / 路径设置 ═══

    [ObservableProperty] private string _inputText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PathPreview))]
    private string _rootDirectory = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PathPreview))]
    private string _pathTemplate = OutputPathResolver.DefaultTemplate;

    [ObservableProperty] private int _strategyIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseButtonText))]
    private bool _isPaused;

    public string PauseButtonText => IsPaused ? "继续" : "暂停";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusNote))]
    private string _statusNote = "";

    public bool HasStatusNote => !string.IsNullOrWhiteSpace(StatusNote);

    [ObservableProperty] private bool _isStatusError;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _allSelected;

    /// <summary>路径模板实时预览（用第一条任务或示例数据渲染）</summary>
    public string PathPreview { get; private set; } = "";

    /// <summary>选文件夹回调（View 注入 —— StorageProvider 在 TopLevel 上）</summary>
    public Func<Task<string?>>? FolderPicker { get; set; }

    // ═══ 生命周期 ═══

    public void Start()
    {
        // 冒烟只巡检：消费者会真发网络请求并写盘 —— 与合成/配音队列同一道闸
        if (App.SmokeMode) return;

        if (!_started)
        {
            _started = true;
            _loopCts = new CancellationTokenSource();
            _ = InitAsync(_loopCts.Token);
            return;
        }

        // 壳每次开壳都会调 Start：换会员时由消费循环在空闲后换装
        if (_loaded && _store.CurrentMemberId != _loadedMember)
            _signal.Release(Concurrency);
    }

    private async Task InitAsync(CancellationToken ct)
    {
        _loaded = true;
        _loadedMember = _store.CurrentMemberId;   // ★ 第一个 await 之前锁属主
        var state = DownloadQueueStore.ApplyRestoreRules(await _store.LoadAsync(_loadedMember, ct));
        ApplyState(state);
        CleanupOrphanTempDirs(RootDirectory);

        for (var i = 0; i < Concurrency; i++)
            _ = ConsumeLoopAsync(ct);
        _signal.Release(Concurrency);
    }

    /// <summary>清理超过 7 天的半成品临时目录（`.downloads/{platform}-{id}` —— 崩溃/移除任务的遗留）。</summary>
    private static void CleanupOrphanTempDirs(string rootDirectory)
    {
        try
        {
            var tempRoot = Path.Combine(Path.GetFullPath(rootDirectory), ".downloads");
            if (!Directory.Exists(tempRoot)) return;

            var cutoff = DateTime.Now.AddDays(-7);
            foreach (var dir in Directory.EnumerateDirectories(tempRoot))
            {
                try
                {
                    if (Directory.GetLastWriteTime(dir) < cutoff)
                        Directory.Delete(dir, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 正在用 / 没权限：跳过，下次启动再看
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Download] 清理下载临时目录失败");
        }
    }

    private void ApplyState(DownloadQueueState state)
    {
        _suppressPersist = true;
        try
        {
            RootDirectory = string.IsNullOrWhiteSpace(state.RootDirectory)
                ? OutputPathOptions.DefaultRoot()
                : state.RootDirectory;
            PathTemplate = string.IsNullOrWhiteSpace(state.PathTemplate)
                ? OutputPathResolver.DefaultTemplate
                : state.PathTemplate;
            StrategyIndex = (int)state.Strategy;
            IsPaused = state.IsPaused;

            Tasks.Clear();
            foreach (var data in state.Tasks)
                Tasks.Add(DownloadTaskRow.FromData(data));
        }
        finally { _suppressPersist = false; }

        NotifySummary();
        NotifyPathPreview();
    }

    // ═══ 换会员（空闲时整体换装） ═══

    private async Task TrySwitchMemberAsync()
    {
        if (_switching || Tasks.Any(t => t.IsRunning)) return;
        var target = _store.CurrentMemberId;
        if (target == _loadedMember) return;

        _switching = true;
        try
        {
            await _saveLock.WaitAsync();   // 排空在飞落盘（此前每笔都写老会员的档）
            _saveLock.Release();

            var state = DownloadQueueStore.ApplyRestoreRules(await _store.LoadAsync(target));
            ApplyState(state);
            _loadedMember = target;
            StatusNote = "已切换到当前账号的下载队列";
            IsStatusError = false;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Download] 换会员载入队列失败");
            StatusNote = "切换账号时载入下载队列失败：" + ex.Message;
            IsStatusError = true;
        }
        finally { _switching = false; }
    }

    // ═══ 消费循环 ═══

    private async Task ConsumeLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(TimeSpan.FromSeconds(2), ct);
            }
            catch (OperationCanceledException) { break; }

            if (App.SmokeMode || IsPaused || _gateReason is not null) continue;

            if (FeatureGate.BlockReason(_session.IsExpired, _session.Username, App.SmokeMode, "视频下载") is { } block)
            {
                _gateReason = block;
                StatusNote = block;
                IsStatusError = true;
                continue;
            }

            if (_store.CurrentMemberId != _loadedMember)
            {
                await TrySwitchMemberAsync();
                continue;
            }

            while (PickNext() is { } row)
                await RunOneAsync(row, ct);
        }
    }

    /// <summary>取队首：同平台只允许一条在跑（其余平台可并行）。</summary>
    private DownloadTaskRow? PickNext()
    {
        var runningPlatforms = Tasks.Where(t => t.IsRunning)
            .Select(t => t.Platform)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;

        return Tasks.FirstOrDefault(t => t.Status == DownloadTaskStatus.Queued
            && !runningPlatforms.Contains(t.Platform)
            && (t.RetryAfter is null || t.RetryAfter <= now));
    }

    private async Task RunOneAsync(DownloadTaskRow row, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _runCts[row] = cts;
        row.Status = DownloadTaskStatus.Downloading;
        row.StatusMessage = "解析地址…";
        row.ErrorKind = null;
        row.RetryAfter = null;
        NotifySummary();
        await PersistAsync();

        try
        {
            var context = await BuildContextAsync(row.Platform, cts.Token);

            VideoInfo info;
            if (row.Formats.Count == 0 || DateTime.UtcNow - row.ExtractedAt > FormatTtl)
            {
                info = await _engine.ExtractAsync(row.Url, context, cts.Token);
                row.ApplyInfo(info);
            }
            else
            {
                info = row.ToVideoInfo();
            }

            var selection = FormatSelector.Select(info.Formats, row.Strategy);
            var options = new OutputPathOptions
            {
                RootDirectory = RootDirectory,
                Template = PathTemplate,
            };
            var progress = new Progress<DownloadProgress>(row.ApplyProgress);
            var outcome = await _engine.DownloadAsync(
                info, selection.Video, selection.Audio, options,
                selection.Video.ResolutionText, progress, cts.Token);

            row.Status = DownloadTaskStatus.Done;
            row.ProgressPercent = 100;
            row.OutputPath = outcome.FilePath;
            row.FinishedAt = DateTime.Now;
            row.StatusMessage = $"已保存：{outcome.FilePath}";
        }
        catch (OperationCanceledException)
        {
            row.Status = DownloadTaskStatus.Cancelled;
            row.StatusMessage = "已取消";
            row.FinishedAt = DateTime.Now;
        }
        catch (DownloadException ex)
        {
            HandleFailure(row, ex);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Download] 任务失败 {Url}", row.Url);
            row.Status = DownloadTaskStatus.Failed;
            row.ErrorKind = nameof(DownloadErrorKind.Unknown);
            row.StatusMessage = ex.Message;
            row.FinishedAt = DateTime.Now;
        }
        finally
        {
            _runCts.Remove(row);
            NotifySummary();
            await PersistAsync();
        }
    }

    /// <summary>
    /// 失败分流（4.6）：
    ///  · UrlExpired → 清掉流快照，重新解析后重排（不原样重试）；
    ///  · 可重试（网络/5xx/429）→ 重排并退避；
    ///  · 其余 → 直接失败，把平台原文给用户。
    /// </summary>
    private static void HandleFailure(DownloadTaskRow row, DownloadException ex)
    {
        row.ErrorKind = ex.Kind.ToString();
        row.FinishedAt = DateTime.Now;

        if (ex.NeedsReExtract && row.RetryCount < MaxRetries)
        {
            row.RetryCount++;
            row.Formats = [];
            row.Status = DownloadTaskStatus.Queued;
            row.RetryAfter = DateTime.UtcNow.AddSeconds(2);
            row.FinishedAt = null;
            row.StatusMessage = "播放地址过期，正在重新解析…";
            return;
        }

        if (ex.IsRetryable && row.RetryCount < MaxRetries)
        {
            row.RetryCount++;
            var backoff = Math.Pow(2, row.RetryCount - 1) * 5;   // 5s / 10s / 20s
            row.Status = DownloadTaskStatus.Queued;
            row.RetryAfter = DateTime.UtcNow.AddSeconds(backoff);
            row.FinishedAt = null;
            row.StatusMessage = $"第 {row.RetryCount} 次重试将在 {backoff:0}s 后进行：{ex.Message}";
            return;
        }

        row.Status = DownloadTaskStatus.Failed;
        row.StatusMessage = ex.Message;
    }

    // ═══ 解析上下文（登录态 / 指纹） ═══

    private async Task<ExtractContext> BuildContextAsync(string platform, CancellationToken ct)
    {
        if (_contextCache.TryGetValue(platform, out var cached)
            && DateTime.UtcNow - cached.At < TimeSpan.FromMinutes(5))
            return cached.Context;

        string? cookie = null;
        Guid? sessionId = null;
        PlatformEnvironmentSnapshot? env = null;

        try
        {
            var accounts = _accounts().Load()
                .Where(a => a.PlatformKey.Equals(platform, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(a => a.UpdatedAt)
                .ToList();

            // 抖音优先挑「账号窗口已经打开」的那个会话 —— 浏览器兜底只认已注册的 WebView 宿主
            var account = platform == PlatformNames.Douyin
                ? accounts.FirstOrDefault(a => DouyinWebExecutor.Has(a.Id)) ?? accounts.FirstOrDefault()
                : accounts.FirstOrDefault();

            if (account is not null)
            {
                // 会话 ID 先记下：即使凭据接口失败，WebView2 账号页仍可能开着（浏览器兜底要用）
                sessionId = account.Id;
                var credentials = await PlatformSessionApi.GetCredentialsAsync(account.Id);
                if (credentials is not null)
                {
                    cookie = credentials.Cookie;
                    env = CredentialFields.EnvironmentSnapshot(credentials.ExtraDataJson);
                }
            }
        }
        catch (Exception ex)
        {
            // 离线 / 服务端不可用时按游客解析：多数平台仍能拿到公开作品
            Log.Warning(ex, "[Download] 读取 {Platform} 会话失败，按游客解析", platform);
        }

        var context = new ExtractContext
        {
            Cookie = cookie,
            Environment = env,
            UserAgent = env?.UserAgent,
            // 抖音详情接口受 Argus/uifid 保护，纯 HTTP 必然 403 —— 用同账号的 WebView2 账号页抓
            BrowserExtract = sessionId is { } sid && platform == PlatformNames.Douyin
                ? (url, token) => DouyinWebExecutor.ExecuteExtractPageAsync(sid, url, token)
                : null,
        };
        _contextCache[platform] = (context, DateTime.UtcNow);
        return context;
    }

    // ═══ 命令：解析 ═══

    [RelayCommand]
    private async Task ParseAsync()
    {
        if (App.SmokeMode)
        {
            StatusNote = "冒烟模式不发起网络请求（解析被拦下）";
            IsStatusError = true;
            return;
        }
        if (FeatureGate.BlockReason(_session.IsExpired, _session.Username, App.SmokeMode, "视频下载") is { } block)
        {
            StatusNote = block;
            IsStatusError = true;
            return;
        }

        var urls = ShareText.ExtractUrls(InputText);
        if (urls.Count == 0)
        {
            StatusNote = "没有识别到链接 —— 每行一个 URL，或直接粘贴分享文案";
            IsStatusError = true;
            return;
        }

        IsBusy = true;
        StatusNote = $"正在解析 {urls.Count} 条链接…";
        IsStatusError = false;

        // 并发 2 解析，但结果按输入顺序装回（用户粘贴的顺序就是列表顺序）
        using var gate = new SemaphoreSlim(2);
        var parsed = await Task.WhenAll(urls.Select(async (url, index) =>
        {
            await gate.WaitAsync();
            try { return (Index: index, Result: await ParseOneAsync(url)); }
            finally { gate.Release(); }
        }));

        var added = 0;
        var duplicate = 0;
        var failed = 0;
        var errors = new List<string>();

        foreach (var (_, result) in parsed.OrderBy(p => p.Index))
        {
            if (result.Info is null)
            {
                failed++;
                if (errors.Count < 3) errors.Add($"{result.Url}：{result.Error}");
                continue;
            }

            var info = result.Info;
            var exists = Tasks.Any(t => t.Platform.Equals(info.Platform, StringComparison.OrdinalIgnoreCase)
                                        && t.VideoId.Equals(info.VideoId, StringComparison.Ordinal));
            if (exists)
            {
                duplicate++;
                continue;
            }

            var row = DownloadTaskRow.FromInfo(info, (FormatStrategy)StrategyIndex);
            Tasks.Add(row);
            added++;
        }

        IsBusy = false;
        if (added > 0) InputText = "";

        StatusNote = added == 0 && failed == 0 && duplicate > 0
            ? $"这 {duplicate} 条都已经在列表里了"
            : $"解析完成：新增 {added} 条" +
              (duplicate > 0 ? $"，跳过重复 {duplicate} 条" : "") +
              (failed > 0 ? $"，失败 {failed} 条（{string.Join("；", errors)}）" : "");
        IsStatusError = added == 0 && failed > 0;

        NotifySummary();
        await PersistAsync();
        _signal.Release(Concurrency);
    }

    private async Task<ParseResult> ParseOneAsync(string url)
    {
        try
        {
            var extractor = _engine.GetExtractor(url);
            var context = await BuildContextAsync(extractor.Platform, CancellationToken.None);
            var info = await _engine.ExtractAsync(url, context, CancellationToken.None);
            return new ParseResult(url, info, null);
        }
        catch (DownloadException ex)
        {
            return new ParseResult(url, null, ex.Message);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Download] 解析失败 {Url}", url);
            return new ParseResult(url, null, ex.Message);
        }
    }

    private sealed record ParseResult(string Url, VideoInfo? Info, string? Error);

    // ═══ 命令：队列控制 ═══

    [RelayCommand]
    private async Task StartAsync()
    {
        if (App.SmokeMode)
        {
            StatusNote = "冒烟模式不执行下载（入队被拦下）";
            IsStatusError = true;
            return;
        }
        if (FeatureGate.BlockReason(_session.IsExpired, _session.Username, App.SmokeMode, "视频下载") is { } block)
        {
            StatusNote = block;
            IsStatusError = true;
            return;
        }

        var targets = Tasks.Where(t => t.IsSelected && IsStartable(t)).ToList();
        if (targets.Count == 0)
            targets = Tasks.Where(IsStartable).ToList();

        if (targets.Count == 0)
        {
            StatusNote = "没有可下载的任务 —— 先点「解析链接」，再勾选后开始下载";
            IsStatusError = true;
            return;
        }

        foreach (var row in targets) ResetToQueued(row, "已加入队列");
        IsPaused = false;
        StatusNote = $"已加入队列 {targets.Count} 条";
        IsStatusError = false;
        NotifySummary();
        await PersistAsync();
        _signal.Release(Concurrency);
    }

    private static bool IsStartable(DownloadTaskRow row) =>
        row.Status is DownloadTaskStatus.Ready or DownloadTaskStatus.Interrupted
            or DownloadTaskStatus.Failed or DownloadTaskStatus.Cancelled;

    private static void ResetToQueued(DownloadTaskRow row, string message)
    {
        row.Status = DownloadTaskStatus.Queued;
        row.StatusMessage = message;
        row.ErrorKind = null;
        row.RetryAfter = null;
        row.ProgressPercent = 0;
        row.ReceivedBytes = 0;
        row.TotalBytes = null;
        row.SpeedText = "";
        row.OutputPath = null;
        row.FinishedAt = null;
        row.RetryCount = 0;
    }

    [RelayCommand]
    private async Task TogglePauseAsync()
    {
        IsPaused = !IsPaused;
        StatusNote = IsPaused
            ? "已暂停 —— 正在下载的跑完就停，不再领取新任务（暂停态会记住）"
            : "已继续";
        IsStatusError = false;
        await PersistAsync();
        if (!IsPaused) _signal.Release(Concurrency);
    }

    [RelayCommand]
    private async Task RetryFailedAsync()
    {
        var failed = Tasks.Where(t => t.Status is DownloadTaskStatus.Failed or DownloadTaskStatus.Interrupted).ToList();
        if (failed.Count == 0)
        {
            StatusNote = "没有失败 / 中断的任务";
            IsStatusError = false;
            return;
        }
        foreach (var row in failed) ResetToQueued(row, "已重新排队");
        IsPaused = false;
        StatusNote = $"已重新排队 {failed.Count} 条";
        IsStatusError = false;
        NotifySummary();
        await PersistAsync();
        _signal.Release(Concurrency);
    }

    [RelayCommand]
    private async Task ClearFinishedAsync()
    {
        var finished = Tasks.Where(t => t.Status is DownloadTaskStatus.Done or DownloadTaskStatus.Cancelled).ToList();
        foreach (var row in finished) Tasks.Remove(row);
        StatusNote = finished.Count == 0 ? "没有已完成的任务" : $"已清空 {finished.Count} 条已完成记录（文件保留在磁盘上）";
        IsStatusError = false;
        NotifySummary();
        await PersistAsync();
    }

    [RelayCommand]
    private async Task RemoveSelectedAsync()
    {
        var removable = Tasks.Where(t => t.IsSelected && !t.IsRunning).ToList();
        foreach (var row in removable)
        {
            CancelRow(row);
            Tasks.Remove(row);
        }
        StatusNote = removable.Count == 0 ? "没有可移除的任务（下载中的先取消）" : $"已移除 {removable.Count} 条记录（文件保留）";
        IsStatusError = false;
        NotifySummary();
        await PersistAsync();
    }

    [RelayCommand]
    private async Task CancelAsync(DownloadTaskRow? row)
    {
        if (row is null) return;
        CancelRow(row);
        NotifySummary();
        await PersistAsync();
    }

    private void CancelRow(DownloadTaskRow row)
    {
        if (_runCts.TryGetValue(row, out var cts))
        {
            cts.Cancel();
            return;
        }
        if (row.Status is DownloadTaskStatus.Queued or DownloadTaskStatus.Ready)
        {
            row.Status = DownloadTaskStatus.Cancelled;
            row.StatusMessage = "已取消";
            row.FinishedAt = DateTime.Now;
        }
    }

    [RelayCommand]
    private async Task RetryAsync(DownloadTaskRow? row)
    {
        if (row?.CanRetry != true) return;
        ResetToQueued(row, "已重新排队");
        IsPaused = false;
        NotifySummary();
        await PersistAsync();
        _signal.Release(1);
    }

    [RelayCommand]
    private async Task RemoveAsync(DownloadTaskRow? row)
    {
        if (row is null || row.IsRunning) return;
        CancelRow(row);
        Tasks.Remove(row);
        NotifySummary();
        await PersistAsync();
    }

    [RelayCommand]
    private void OpenFolder(DownloadTaskRow? row)
    {
        var path = row?.OutputPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Download] 打开文件夹失败 {Path}", path);
        }
    }

    /// <summary>用系统默认播放器打开成品（UseShellExecute 才会走文件关联）。</summary>
    [RelayCommand]
    private void PlayFile(DownloadTaskRow? row)
    {
        var path = row?.OutputPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Download] 打开文件失败 {Path}", path);
            row!.StatusMessage = "打不开这个文件（可能没有关联的播放器）";
        }
    }

    [RelayCommand]
    private async Task BrowseFolderAsync()
    {
        if (FolderPicker is null) return;
        var picked = await FolderPicker();
        if (!string.IsNullOrWhiteSpace(picked)) RootDirectory = picked!;
    }

    // ═══ 设置变更 ═══

    partial void OnRootDirectoryChanged(string value)
    {
        NotifyPathPreview();
        if (!_suppressPersist) _ = PersistAsync();
    }

    partial void OnPathTemplateChanged(string value)
    {
        NotifyPathPreview();
        if (!_suppressPersist) _ = PersistAsync();
    }

    partial void OnStrategyIndexChanged(int value)
    {
        var strategy = (FormatStrategy)Math.Clamp(value, 0, 4);
        foreach (var row in Tasks.Where(t => t.Status == DownloadTaskStatus.Ready))
            row.Strategy = strategy;
        if (!_suppressPersist) _ = PersistAsync();
    }

    partial void OnIsPausedChanged(bool value) => NotifySummary();

    partial void OnAllSelectedChanged(bool value)
    {
        foreach (var row in Tasks) row.IsSelected = value;
    }

    // ═══ 汇总 / 预览 / 落盘 ═══

    private void NotifySummary()
    {
        OnPropertyChanged(nameof(HasTasks));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(BadgeCount));
        OnStatsChanged?.Invoke();
    }

    private void NotifyPathPreview()
    {
        try
        {
            var sample = Tasks.FirstOrDefault()?.ToVideoInfo() ?? new VideoInfo
            {
                Platform = PlatformNames.Douyin,
                VideoId = "741234567890",
                SourceUrl = "https://example.invalid/",
                Title = "示例标题",
                Author = "示例作者",
            };
            var relative = OutputPathResolver.PreviewRelative(
                PathTemplate, sample, DateTime.Now, "mp4");
            PathPreview = "示例：" + relative;
        }
        catch (Exception ex)
        {
            PathPreview = "模板错误：" + ex.Message;
        }
        OnPropertyChanged(nameof(PathPreview));
    }

    private async Task PersistAsync()
    {
        if (!_loaded || _suppressPersist) return;

        var snapshot = new DownloadQueueState
        {
            IsPaused = IsPaused,
            RootDirectory = RootDirectory,
            PathTemplate = PathTemplate,
            Strategy = (FormatStrategy)Math.Clamp(StrategyIndex, 0, 4),
            Tasks = [.. Tasks.Select(t => t.ToData())],
        };

        await _saveLock.WaitAsync();
        try { await _store.SaveAsync(snapshot, _loadedMember); }
        finally { _saveLock.Release(); }
    }

    /// <summary>App 层：登出 / 撤权时停摆（只在内存，不改用户主动暂停的存档）。</summary>
    public void PauseBecause(string reason)
    {
        _gateReason = reason;
        StatusNote = reason;
        IsStatusError = true;
    }

    /// <summary>清掉许可停摆并踢一脚（壳在重新登录 / 会员恢复时调用；无停摆时是空操作）。</summary>
    public void Resume()
    {
        if (_gateReason is null) return;
        if (StatusNote == _gateReason) StatusNote = "";
        _gateReason = null;
        IsStatusError = false;
        _signal.Release(Concurrency);
    }
}
