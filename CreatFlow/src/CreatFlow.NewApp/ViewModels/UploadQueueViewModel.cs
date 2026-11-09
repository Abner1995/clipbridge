using System.ComponentModel;
using CreatFlow.NewApp.Models;
using CreatFlow.NewApp.Services;

namespace CreatFlow.NewApp.ViewModels;

/// <summary>
/// 上传队列页（设计文档 §2.4 / P3-4）—— 真实数据版，替掉 UploadQueueMockViewModel。
///
/// 三条定调：
///   ① **服务端状态是权威**，本地不另存队列 —— 整页数据来自 <c>POST /api/publish/platforms</c>
///      （任务 × 平台联合行），按 TaskId 聚合成「视频」卡，展开看平台×账号明细。
///   ② 本地只维护「谁在跑、谁被暂停」这类**服务端不知道**的瞬时状态：执行中不让服务端的
///      Pending 把进度清零，暂停不让人家的同步把行打回等待（老版就栽在这儿：点了暂停三秒后又自己跑）。
///   ③ 本页**是消费者**：提交只写库（发布页 SubmitAsync），上传+发布在这里按
///      (平台, 账号) 分组串行执行、组间并行 —— 同账号 60–90 秒间隔规避平台风控。
///
/// 消费者与页面同生命周期（VM 由 DI 单例持有），但**只在用户点过「开始上传」后才启动**
///（2026-10-22 用户需求：重开软件先刷新平台登录，再手动开始；不再随壳自动上传）。
/// </summary>
public sealed partial class UploadQueueViewModel : ViewModelBase, INavigationAware
{
    private readonly AccountDockViewModel _dock;

    /// <summary>taskId → 视频卡</summary>
    private readonly Dictionary<Guid, QueueTaskViewModel> _byTask = [];
    /// <summary>platformId → 明细行（扁平视图：认领、统计、横幅都按它算）</summary>
    private readonly Dictionary<Guid, QueuePlatformRow> _byPlatform = [];

    private readonly Dictionary<Guid, string> _accountLabels = [];   // sessionId → 账号名
    private readonly Dictionary<Guid, int> _sessionStatus = [];      // sessionId → 0 正常 / 1 即将过期 / 2 已过期
    private readonly Dictionary<string, string> _brandHex = [];      // platformKey → 品牌色
    private readonly Dictionary<string, DateTime> _lastRunAt = [];   // sessionKey → 该账号上一条跑完的时刻
    private readonly Dictionary<string, int> _gapSeconds = [];       // sessionKey → 本条间隔（60–90 随机）

    /// <summary>点了「去登录」的会话 —— 它恢复后自动重试名下失败/暂停的行</summary>
    private readonly HashSet<Guid> _awaitingLogin = [];

    private Timer? _pollTimer;
    private int _isRefreshing;
    private int _isConsuming;
    private CancellationTokenSource? _consumerCts;
    private bool _brandsLoaded;
    private bool _started;

    /// <summary>
    /// 本运行内用户是否已**手动点过「开始上传」**（2026-10-22 用户需求）。
    ///
    /// 启动即开壳时恒 false：软件崩溃/退出后重开，队列只同步列表、**不自动上传** ——
    /// 用户要先去「平台账号」确认登录凭证已刷新，再回来点「开始上传」。
    /// 不再落盘（每次运行都要重新确认；登出/撤权时 Stop 也会复位）。
    /// </summary>
    [ObservableProperty] private bool _uploadStarted;

    /// <summary>统计变化（侧栏徽章用）</summary>
    public Action? OnStatsChanged;

    public UploadQueueViewModel(AccountDockViewModel dock) => _dock = dock;

    // ═══════════════════════════════════════════════════════════════
    //  对外状态
    // ═══════════════════════════════════════════════════════════════

    public ObservableCollection<QueueTaskViewModel> Tasks { get; } = [];

    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private string _statusText = "";
    /// <summary>是否正忙（刷新/批量操作中）—— 按钮的可用性</summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>还没落定的明细行数（服务端 Pending/Uploading/… 都算）—— 侧栏徽章与「清空」都看它</summary>
    public int UnfinishedCount => _byPlatform.Values.Count(r =>
        r.State is not (QueueRowState.Published or QueueRowState.Failed));

    public int RunningCount => _byPlatform.Values.Count(r => r.IsRunning);
    public int WaitingCount => _byPlatform.Values.Count(r => r.State == QueueRowState.Waiting);
    public int PausedCount => _byPlatform.Values.Count(r => r.IsManuallyPaused);
    public int DoneCount => _byPlatform.Values.Count(r => r.IsPublished);
    public int FailedCount => _byPlatform.Values.Count(r => r.IsFailed);

    /// <summary>今日已完成（按 PublishedAt 归日 —— 老版同款口径；列表口径，清空后计数会降）</summary>
    public int TodayCompletedCount =>
        _byPlatform.Values.Count(r => r.IsPublished && r.PublishedAt?.Date == DateTime.Today);

    /// <summary>今日提交（按记录创建时间归日；与「完成」不同源，老版就是两个口径并存）</summary>
    public int TodayTotalCount =>
        _byPlatform.Values.Count(r => r.Item.CreatedAt.Date == DateTime.Today);

    /// <summary>壳底状态栏文案（老版「今日发布 x/y」；今天没有任务时给破折号）</summary>
    public string TodayPublishText =>
        TodayTotalCount > 0 ? $"今日发布 {TodayCompletedCount}/{TodayTotalCount}" : "今日发布 —";

    public string RunningText => $"进行中 {RunningCount}";
    public string WaitingText => $"等待 {WaitingCount}";
    public string DoneText => $"已完成 {DoneCount}";
    public string FailedText => $"失败 {FailedCount}";

    public bool HasCompleted => DoneCount > 0;
    public bool HasUnfinished => UnfinishedCount > 0;

    /// <summary>底部说明：把「为什么这么慢」讲清楚，免得用户以为卡死</summary>
    public string EtaText
    {
        get
        {
            const string tail = "同账号任务间隔 60–90 秒（规避平台风控）";
            if (UnfinishedCount == 0) return $"队列已空 · {tail}";

            // 同账号串行、不同账号并行 → 整队时间 = 最长的那个账号队列
            var worst = 0.0;
            foreach (var group in _byPlatform.Values
                         .Where(r => r.State is not (QueueRowState.Published or QueueRowState.Failed))
                         .GroupBy(r => r.SessionKey))
            {
                var perRow = 75 + UploadSeconds(group.First());
                worst = Math.Max(worst, group.Count() * perRow);
            }
            var minutes = Math.Max(1, (int)Math.Ceiling(worst / 60));
            return $"预计剩余 {minutes} 分钟 · {tail}";
        }
    }

    /// <summary>粗估单条上传耗时（按文件大小 / 2MB/s，平台侧转码不计）</summary>
    private static double UploadSeconds(QueuePlatformRow row)
    {
        try
        {
            var fi = new FileInfo(row.VideoFilePath);
            return fi.Exists ? Math.Min(600, fi.Length / 1024.0 / 1024 / 2) : 60;
        }
        catch { return 60; }
    }

    // ── 账号异常横幅（最高优先级交互，设计文档 §2.4 ①）──

    /// <summary>
    /// 会话是否**不可用**：已过期，或者压根不在会话表里（账号被解绑 / 重新登录换了新会话 id）。
    /// 会话表还没加载时（首轮或拉取失败）一律当可用 —— 一次网络抖动不该把整条队列挂起。
    /// </summary>
    private bool SessionUnavailable(Guid sessionId) =>
        _sessionStatus.Count > 0 && _sessionStatus.GetValueOrDefault(sessionId, 2) == 2;

    public bool HasLoginBanner => LoginBannerRows.Count > 0;

    /// <summary>账号不可用 + 名下还有没跑完的行 —— 这些行现在跑也白跑</summary>
    private List<QueuePlatformRow> LoginBannerRows => [.. _byPlatform.Values
        .Where(r => r.SessionId is { } sid && SessionUnavailable(sid)
            && r.State is not (QueueRowState.Published or QueueRowState.Failed))];

    public string LoginBannerText
    {
        get
        {
            var rows = LoginBannerRows;
            if (rows.Count == 0) return "";
            var first = rows[0];
            var others = rows.Select(r => r.SessionKey).Distinct().Count() - 1;
            var head = $"{first.PlatformName} · @{first.AccountLabel} 登录已过期或账号已解绑，{rows.Count} 个任务已暂停";
            return others > 0 ? $"{head}（另有 {others} 个账号同样不可用）" : head;
        }
    }

    /// <summary>横幅上的「去登录」按钮要开的会话</summary>
    public Guid? LoginBannerSessionId => LoginBannerRows.FirstOrDefault()?.SessionId;

    // ── 账号窗口未打开横幅（纯提示：任务照常跑，不拦；打开过即消失）──

    /// <summary>
    /// 本运行还没打开过窗口、名下又有未完成任务的行（提示看看就好，不拦任务）。
    /// 排除 SessionUnavailable 的 —— 那些归登录横幅管（两边都报就成了重复唠叨）。
    /// **依据是「打开过」不是「验证完成」**：后者要页面流量+跨安全闸，卡住时用户明明
    /// 打开了窗口提示也不走（2026-10-09 实测「已经登录了还提示」）。
    /// </summary>
    private List<QueuePlatformRow> VerifyBannerRows => [.. _byPlatform.Values
        .Where(r => r.SessionId is { } sid
            && AccountDockViewModel.NeedsAccountWindow(r.PlatformKey)
            && !_dock.IsSessionOpened(sid)
            && !SessionUnavailable(sid)
            && r.State is not (QueueRowState.Published or QueueRowState.Failed))];

    public bool HasVerifyBanner => VerifyBannerRows.Count > 0;

    public string VerifyBannerText
    {
        get
        {
            var rows = VerifyBannerRows;
            if (rows.Count == 0) return "";
            var accounts = rows.Select(r => $"{r.PlatformName} · @{r.AccountLabel}")
                .Distinct().ToList();
            var head = accounts.Count <= 3
                ? string.Join("、", accounts)
                : string.Join("、", accounts.Take(3)) + $" 等 {accounts.Count} 个账号";
            return $"{head}（{rows.Count} 个任务）：建议到「平台账号」点开这些账号窗口刷新登录凭证 —— " +
                   "平台风控严，凭证未刷新时发布可能被拒；打开过本提示即消失";
        }
    }

    /// <summary>横幅「去平台账号」—— 页 VM 不认识壳，导航能力由壳注入（与首页卡片同范式）。
    /// **不代开窗口**：三家都有扫码/风控，必须用户自己在「平台账号」里点开。</summary>
    public Action<string>? OnNavigateRequested { get; set; }

    [RelayCommand]
    private void GoOpenAccounts() => OnNavigateRequested?.Invoke("platform");

    // ═══════════════════════════════════════════════════════════════
    //  生命周期
    // ═══════════════════════════════════════════════════════════════

    public void OnNavigatedTo()
    {
        Start();
        _ = RefreshFromServerAsync();
    }

    /// <summary>幂等启动：只拉起轮询，**不自动开始上传**（消费循环由用户点「开始上传」启动）。</summary>
    public void Start()
    {
        if (_started) return;

        // ⚠️ `--smoke` 是**视觉巡检**，但它会真的建壳、真的轮询 —— 而消费者会真的上传/发布。
        // 开发机上 token.dat 通常是活的，巡检一跑就会把队列里的任务真发出去，
        // 4 秒后 Shutdown 还把进程连同上传一起杀掉（服务端停在 Pending）。
        // 同 PublishSelfTest 里那条「队列页跑起来会真上传」的顾虑，这里必须挡在启动之前。
        if (App.SmokeMode) return;

        _started = true;

        // 5 秒后首刷，之后每 30 秒同步一次服务端状态（老版同款节奏）。
        // 只同步、不上传：重开软件时平台登录凭证可能已过期，自动开跑就是连串失败
        //（2026-10-22 用户实测）。用户先刷新登录、再手动点「开始上传」。
        _pollTimer = new Timer(async _ => await RefreshFromServerAsync(),
            null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// 登出/会员失效时停掉后台线程（App 层调用）。
    ///
    /// **必须连本地视图一起清**：本 VM 是单例，换会员后壳重建复用同一个实例 ——
    /// 不清的话，新会员第一次认领就会去执行上一个会员的待发布行（行里带着上一个会员的
    /// sessionId），并把结果报在他自己的账下。与账号坞「登出即 CloseAllTabs」同一条规矩。
    /// </summary>
    public void Stop()
    {
        _pollTimer?.Dispose();
        _pollTimer = null;
        StopConsumer();
        _started = false;
        UploadStarted = false;   // 下一次登录/开壳必须重新手动确认

        Tasks.Clear();
        _byTask.Clear();
        _byPlatform.Clear();
        _accountLabels.Clear();
        _sessionStatus.Clear();
        _brandHex.Clear();
        _lastRunAt.Clear();
        _gapSeconds.Clear();
        _awaitingLogin.Clear();
        _brandsLoaded = false;
        RefreshStats();
    }

    // ═══════════════════════════════════════════════════════════════
    //  服务端同步（服务端状态是权威）
    // ═══════════════════════════════════════════════════════════════

    public async Task RefreshFromServerAsync()
    {
        if (Interlocked.Exchange(ref _isRefreshing, 1) == 1) return;   // 防重入（30s 轮询 + 手动刷新撞车）
        try
        {
            var resp = await ApiClient.PostAsync<PlatformListResponse>("/api/publish/platforms");

            // ⚠️ 取不到就当**这次同步没发生**，绝不能当「服务端一条都没有」：
            // 传输失败/超时/会员被撤销时 PostAsync 返回 null，照常往下走会把本地行全当过期清掉，
            // 页面瞬间变「队列是空的」、徽章归零，而上传还在跑 —— 而且下一次同步会把同一批
            // PlatformId 当新行重建，被消费循环**再传一遍**（老版这句 early-return 是刻意留的）
            if (resp?.Platforms is not { Count: > 0 }) return;
            var platforms = resp.Platforms;

            // ⚠️ 网络取数在池线程上，但**共享字典只在 UI 线程改** ——
            // 轮询线程写、UI 线程读同一个 Dictionary 是竞态（轻则读到写坏的表）。
            var brands = _brandsLoaded ? null : await FetchBrandsAsync();
            var sessions = await FetchSessionsAsync();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (brands is { Count: > 0 })
                {
                    foreach (var cfg in brands)
                        if (!string.IsNullOrWhiteSpace(cfg.BrandColorHex))
                            _brandHex[cfg.PlatformKey] = cfg.BrandColorHex;
                    _brandsLoaded = true;
                }

                foreach (var s in sessions ?? [])
                {
                    // 账号名的回退链（备注 → 平台昵称 → 未命名）收在 AccountLabels：
                    // 少写「昵称」这一层就会满屏「未命名账号」（实测库里 account_label 全是 NULL）
                    _accountLabels[s.Id] = AccountLabels.DisplayName(s);
                    _sessionStatus[s.Id] = s.Status;
                }

                Merge(platforms);
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Queue] 队列同步失败（保留本地视图）");
        }
        finally
        {
            _isRefreshing = 0;
        }
    }

    /// <summary>品牌色走配置（与发布页/账号坞同源），失败静默用兜底</summary>
    private static async Task<List<PlatformConfigItem>?> FetchBrandsAsync()
    {
        try { return await ApiClient.GetAsync<List<PlatformConfigItem>>("/api/config/platforms"); }
        catch (Exception ex) { Log.Debug(ex, "[Queue] 平台品牌色拉取失败"); return null; }
    }

    /// <summary>账号名 + 登录状态（平台记录里只有 sessionId，两样都得从会话表来）</summary>
    private static async Task<List<PlatformSessionItem>?> FetchSessionsAsync()
    {
        try
        {
            var resp = await ApiClient.PostAsync<PlatformSessionListResponse>("/api/platform-sessions/list");
            return resp?.Sessions;
        }
        catch (Exception ex) { Log.Debug(ex, "[Queue] 会话列表拉取失败"); return null; }
    }

    /// <summary>把服务端的联合行并进本地视图（UI 线程）</summary>
    private void Merge(IReadOnlyList<ServerPlatformItem> platforms)
    {
        var seen = new HashSet<Guid>();

        foreach (var group in platforms.GroupBy(p => p.TaskId))
        {
            var rows = group.ToList();
            if (!_byTask.TryGetValue(group.Key, out var task))
            {
                var first = rows[0];
                task = new QueueTaskViewModel(group.Key, first.VideoFilePath, first.VideoFileName,
                    first.Title, first.CreatedAt);
                _byTask[group.Key] = task;
                Tasks.Insert(0, task);
            }

            foreach (var item in rows)
            {
                seen.Add(item.PlatformId);
                if (_byPlatform.TryGetValue(item.PlatformId, out var row))
                {
                    row.ApplyServer(item.Status, item.ProgressPct, item.ErrorMsg);
                    // 账号名每次同步都刷一遍：用户在平台页改了备注，队列里的旧名字要跟着走
                    if (_accountLabels.TryGetValue(item.PlatformSessionId ?? Guid.Empty, out var label))
                        row.AccountLabel = label;
                }
                else
                {
                    var fresh = new QueuePlatformRow(item,
                        _brandHex.GetValueOrDefault(item.PlatformKey, BrandFallback(item.PlatformKey)),
                        _accountLabels.GetValueOrDefault(item.PlatformSessionId ?? Guid.Empty, ""));
                    // 勾选态联动要听行的 PropertyChanged（全选反推）；行对象在 30s 同步间复用，订阅不重复挂
                    fresh.PropertyChanged += OnRowPropertyChanged;
                    _byPlatform[item.PlatformId] = fresh;
                    task.Rows.Add(fresh);
                }
            }

            SortRows(task);
            task.RaiseDerived();
        }

        // 服务端删掉/隐藏掉的行，本地也要走（视频管理页删记录、别的端操作过）
        foreach (var id in _byPlatform.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            var row = _byPlatform[id];
            _byPlatform.Remove(id);
            var owner = _byTask.Values.FirstOrDefault(t => t.Rows.Contains(row));
            owner?.Rows.Remove(row);
            owner?.RaiseDerived();   // 少了一行，「3 / 4 成功」这类聚合文案要跟着重算
            if (owner is { Rows.Count: 0 }) { Tasks.Remove(owner); _byTask.Remove(owner.TaskId); }
        }

        SortTasks();
        RefreshStats();
        _ = RecoverOfflineAccountsAsync();
    }

    private static void SortRows(QueueTaskViewModel task)
    {
        var ordered = task.Rows.OrderBy(r => r.Item.CreatedAt).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var cur = task.Rows.IndexOf(ordered[i]);
            if (cur != i) task.Rows.Move(cur, i);
        }
    }

    /// <summary>失败置顶（设计文档 §2.4 ②），其余按提交时间倒序</summary>
    private void SortTasks()
    {
        var ordered = Tasks
            .OrderByDescending(t => t.HasFailure && !t.IsSettled)
            .ThenByDescending(t => t.CreatedAt)
            .ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var cur = Tasks.IndexOf(ordered[i]);
            if (cur != i) Tasks.Move(cur, i);
        }
    }

    private static string BrandFallback(string platformKey) => platformKey switch
    {
        PlatformNames.Douyin => "#FF0049",
        PlatformNames.Kuaishou => "#FF4906",
        PlatformNames.WechatVideo => "#07C160",
        PlatformNames.Xiaohongshu => "#FE2C55",
        _ => "#6C5CE7",
    };

    private void RefreshStats()
    {
        IsEmpty = _byPlatform.Count == 0;
        OnPropertyChanged(nameof(UnfinishedCount));
        OnPropertyChanged(nameof(RunningCount));
        OnPropertyChanged(nameof(WaitingCount));
        OnPropertyChanged(nameof(PausedCount));
        OnPropertyChanged(nameof(DoneCount));
        OnPropertyChanged(nameof(FailedCount));
        OnPropertyChanged(nameof(RunningText));
        OnPropertyChanged(nameof(WaitingText));
        OnPropertyChanged(nameof(DoneText));
        OnPropertyChanged(nameof(FailedText));
        OnPropertyChanged(nameof(HasCompleted));
        OnPropertyChanged(nameof(HasUnfinished));
        OnPropertyChanged(nameof(EtaText));
        OnPropertyChanged(nameof(HasLoginBanner));
        OnPropertyChanged(nameof(LoginBannerText));
        OnPropertyChanged(nameof(LoginBannerSessionId));
        OnPropertyChanged(nameof(HasVerifyBanner));
        OnPropertyChanged(nameof(VerifyBannerText));
        ClearCompletedCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(TodayCompletedCount));
        OnPropertyChanged(nameof(TodayTotalCount));
        OnPropertyChanged(nameof(TodayPublishText));
        OnPropertyChanged(nameof(ShowStartBanner));   // 「先刷新登录再开始上传」横幅跟着任务数走
        OnPropertyChanged(nameof(StartBannerText));
        SyncAllSelectedFromRows();   // 增删行后全选态要重算（行数 0 时回 false）
        OnStatsChanged?.Invoke();
    }

    // ═══════════════════════════════════════════════════════════════
    //  消费循环（本页就是执行者）
    // ═══════════════════════════════════════════════════════════════

    private void StartConsumer()
    {
        // 冒烟是视觉巡检：消费者会真上传/发布（见 Start 的闸注释）。挡在这里而不是只挡 Start——
        // 「继续」「重试」等命令也会走到这条路上，冒烟里执行它们不能把队列真跑起来
        if (App.SmokeMode) return;

        // 本运行还没点过「开始上传」：所有自动/行级路径都到这里为止，队列保持静止。
        // 用户点「开始上传」后才放行（2026-10-22 用户需求：重开软件必须先刷新登录再手动启动）。
        if (!UploadStarted) return;

        if (Interlocked.Exchange(ref _isConsuming, 1) == 1) return;
        _consumerCts = new CancellationTokenSource();
        var token = _consumerCts.Token;

        _ = Task.Run(async () =>
        {
            try { await ProcessQueueAsync(token); }
            catch (OperationCanceledException) { /* 正常停机 */ }
            catch (Exception ex) { Log.Error(ex, "[Queue] 消费循环异常退出"); }
            finally { Interlocked.Exchange(ref _isConsuming, 0); }
        }, token);
    }

    private void StopConsumer()
    {
        var cts = _consumerCts;
        _consumerCts = null;
        cts?.Cancel();
        // ⚠️ 不能紧接着 Dispose：消费线程此刻正拿着由它派生的令牌（Task.Delay / 链接令牌 /
        // SDK 的 ct.Register），释放后会抛 ObjectDisposedException 把循环整个带走。
        // CTS 不带计时器时不需要主动释放，取消 + 丢引用就够。
        // 同时把「正在消费」的闸门放开 —— 否则登出再登录时 Start() 会因为闸门还关着而静默不启动。
        Interlocked.Exchange(ref _isConsuming, 0);
    }

    /// <summary>
    /// 消费循环：每 2 秒认领一次「每个账号队首那条」并行执行。
    ///
    /// 分组策略（老版实测结论）：同 (平台, 账号) 串行且间隔 60–90 秒 —— 共享 cookie，
    /// 并发会 403；不同账号/平台之间独立，并行即可，不需要互相错开。
    /// 冷却期内的账号**不认领**，队首行显示「同账号前序任务间隔 Ns」—— 用户才知道不是卡住了。
    /// </summary>
    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            List<QueuePlatformRow> batch;
            try
            {
                batch = await Dispatcher.UIThread.InvokeAsync(ClaimReady);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Queue] 认领失败，稍后重试");
                await Task.Delay(3000, ct);
                continue;
            }

            if (batch.Count == 0)
            {
                await Task.Delay(2000, ct);
                continue;
            }

            // 顺手把任务引用挂到行上：「放弃正在跑的行」要靠它才等得到执行线程真退出
            await Task.WhenAll(batch.Select(r =>
            {
                var task = ExecuteRowAsync(r, ct);
                r.RunningTask = task;
                return task;
            }));
        }
    }

    /// <summary>每个账号取队首一条（UI 线程）：跳过集合里/冷却期内的账号</summary>
    private List<QueuePlatformRow> ClaimReady()
    {
        var claimed = new List<QueuePlatformRow>();
        var now = DateTime.Now;

        foreach (var group in _byPlatform.Values
                     .Where(r => r.State == QueueRowState.Waiting && !r.IsWithdrawing)
                     .GroupBy(r => r.SessionKey))
        {
            var head = group.OrderBy(r => r.Item.CreatedAt).First();

            if (_lastRunAt.TryGetValue(group.Key, out var last))
            {
                var gap = _gapSeconds.GetValueOrDefault(group.Key, 60);
                var left = (int)Math.Ceiling((last.AddSeconds(gap) - now).TotalSeconds);
                if (left > 0)
                {
                    head.DetailText = $"同账号前序任务间隔 {left}s";
                    continue;   // 冷却中：这个账号本轮不认领
                }
            }

            // 账号掉线/解绑 → 挂起（不执行、不计入冷却），横幅会提示去登录。
            // pausedBySession 记住「这不是用户点的暂停」—— 账号一恢复就自动重排
            if (head.SessionId is { } sid && SessionUnavailable(sid))
            {
                head.ApplyLocal(QueueRowState.Paused, 0, "登录已过期或账号已解绑，请重新登录",
                    pausedBySession: true);
                continue;
            }

            head.ApplyLocal(QueueRowState.Uploading, 0, "准备中…");
            claimed.Add(head);
        }

        if (claimed.Count > 0) { SortTasks(); RefreshStats(); }
        return claimed;
    }

    /// <summary>
    /// 执行一条平台记录。由消费循环与手动重试共用。
    ///
    /// 取消语义（与老版一致）：
    ///   · 外层 ct 取消（App 退出）→ 回退等待，向上抛，循环结束
    ///   · 本行独立源取消（用户暂停）→ 状态已被 Pause 置为暂停，直接返回
    /// </summary>
    private async Task ExecuteRowAsync(QueuePlatformRow row, CancellationToken ct)
    {
        if (row.State == QueueRowState.Paused) return;

        using var rowCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        row.TaskCts = rowCts;

        // 每次执行一份管线实例：进度回调只带平台键、不带记录 Id，
        // 多账号并行时会串台 —— 闭包绑到本行才是干净的（老版同样一笔一实例）。
        var pipeline = new PublishPipeline
        {
            // 视频号没有自定义封面时会拿首帧当封面 —— 少了 MetaProbe 就退回那张 1×1 绿图
            // （§4.2 特意删掉的缺陷，不接上等于把它请回来）
            MetaProbe = new VideoMetaProbe(),
            // 提示音：失败立即响、成功等整条视频收尾时响一次（节奏由管线决定，见 PublishPipeline.Notify）
            OnNotify = PublishSoundNotifier.Notify,
            OnPlatformProgress = (_, _, pct, message) => Dispatcher.UIThread.Post(() =>
            {
                if (row.TaskCts != rowCts || row.State == QueueRowState.Paused) return;   // 丢弃迟到回调
                row.ApplyLocal(pct >= 100 ? QueueRowState.Transcoding : QueueRowState.Uploading, pct, message);
                RefreshStats();
            }),
        };

        try
        {
            var (success, videoId, error, _) = await pipeline.RetryPlatformAsync(
                row.VideoFilePath, row.Item, rowCts.Token);

            // 行 CTS 已取消（暂停 / 删除选中）：平台可能把取消包成「失败结果」返回 ——
            // 保持暂停/撤回中状态，绝不覆盖成失败（2026-10-22 抖音/视频号实测：
            // 取消时底层抛 HttpRequestException 被 SDK 吞成失败结果，管线/队列各补一道）。
            if (rowCts.IsCancellationRequested)
            {
                Log.Debug("[Queue] {Platform} {Id} 已按用户要求停止（忽略平台返回的失败结果）",
                    row.PlatformKey, row.PlatformId);
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (row.TaskCts != rowCts) return;
                row.ApplyLocal(success ? QueueRowState.Published : QueueRowState.Failed,
                    success ? 100 : 0, success ? null : error);
                FinishRow(row);
            });

            Log.Information("[Queue] {Platform} {Id} 执行结果 success={Success} videoId={VideoId} err={Error}",
                row.PlatformKey, row.PlatformId, success, videoId, error);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // App 退出/停机：回退等待，向上抛，循环结束
            await Dispatcher.UIThread.InvokeAsync(() => row.ApplyLocal(QueueRowState.Waiting, 0, null));
            throw;
        }
        catch (OperationCanceledException)
        {
            // 外层没取消，却说「被取消」——两种可能，靠**本行自己的令牌**区分：
            //   · rowCts 被取消 → 用户点了暂停：状态由暂停命令置好，直接返回
            //   · 没人取消过任何令牌 → 是 HttpClient 超时（30s）抛的 OCE。
            //     不能当「暂停」咽掉：那样这一行永远停在「上传中 N%」，服务端也没收到失败，
            //     重试/撤回都点不动（它们只对 Failed/Pending 开放）
            if (rowCts.IsCancellationRequested) return;

            Log.Warning("[Queue] {Platform} {Id} 请求被中断（超时？）", row.PlatformKey, row.PlatformId);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                row.ApplyLocal(QueueRowState.Failed, 0, "请求超时或被平台中断，可重试");
                FinishRow(row);
            });
        }
        catch (Exception ex) when (rowCts.IsCancellationRequested)
        {
            // 用户暂停 / 「删除选中」：取消已经请求过了，但平台 SDK 可能把取消包成普通异常
            // （2026-10-22 快手分片：所有 endpoint 都撞到取消 → "Chunk X failed on all endpoints"）。
            // 行状态已由暂停/撤回路径置好，**不能覆盖成失败**；也不该按错误刷日志。
            Log.Debug(ex, "[Queue] {Platform} {Id} 已按用户要求停止", row.PlatformKey, row.PlatformId);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Queue] {Platform} {Id} 执行异常", row.PlatformKey, row.PlatformId);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                row.ApplyLocal(QueueRowState.Failed, 0, $"执行异常：{ex.Message}");
                FinishRow(row);
            });
        }
        finally
        {
            if (row.TaskCts == rowCts) row.TaskCts = null;
        }
    }

    /// <summary>一条跑完：记冷却起点并给这个账号抽下一个间隔（UI 线程调用）</summary>
    private void FinishRow(QueuePlatformRow row)
    {
        _lastRunAt[row.SessionKey] = DateTime.Now;
        _gapSeconds[row.SessionKey] = Random.Shared.Next(60, 91);
        RefreshStats();
    }

    // ═══════════════════════════════════════════════════════════════
    //  操作
    // ═══════════════════════════════════════════════════════════════

    /// <summary>暂停一条行（置暂停 + 取消在途执行）—— 执行体的用户取消路径会静默退出，状态由这里先置好</summary>
    private static void PauseOne(QueuePlatformRow row)
    {
        row.TaskCts?.Cancel();
        row.ApplyLocal(QueueRowState.Paused, 0, "已暂停");
    }

    // ── 手动启动（2026-10-22 用户需求：重开软件不自动上传，先刷新登录再手动开始）──

    /// <summary>未启动且队列有活 → 顶部横幅引导（先刷新登录，再开始上传）</summary>
    public bool ShowStartBanner => !UploadStarted && HasUnfinished;

    public string StartBannerText =>
        $"队列未启动：软件重开后不会自动上传。请先在「平台账号」确认登录状态已刷新，"
        + $"再点「开始上传」（{UnfinishedCount} 个任务待上传）";

    partial void OnUploadStartedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowStartBanner));
        OnPropertyChanged(nameof(StartBannerText));
    }

    /// <summary>
    /// 用户手动启动上传 —— 本运行内开跑的唯一入口。
    /// 开壳/登出重登/崩溃重开都回到「未启动」；启动后本轮新入队的任务自动执行。
    /// </summary>
    [RelayCommand]
    private void StartUpload()
    {
        if (UploadStarted) return;
        UploadStarted = true;
        StatusText = UnfinishedCount > 0
            ? $"上传已启动 · {UnfinishedCount} 个任务待执行"
            : "上传已启动（当前没有待上传任务）";
        RefreshStats();
        StartConsumer();
    }

    /// <summary>暂停全部（正在跑的那条也会被取消，回到等待由用户决定）</summary>
    [RelayCommand]
    private void PauseAll()
    {
        foreach (var row in _byPlatform.Values.Where(r => r.State == QueueRowState.Waiting).ToList())
            PauseOne(row);
        // 正在跑的：取消本次执行，取消后回到等待 → 这里再置暂停，避免它又自己跑起来
        foreach (var row in _byPlatform.Values.Where(r => r.IsRunning).ToList())
            PauseOne(row);
        StatusText = "已暂停全部（队列里的行不再自动开始）";
        RefreshStats();
    }

    /// <summary>行级「暂停」：只停这一条，不删记录（老版单行 ⏸ 的对应物）</summary>
    [RelayCommand]
    private void PauseRow(QueuePlatformRow? row)
    {
        // 撤回中的行由撤回流程自行 Cancel + 置「正在停止…」，别插手
        if (row is null || row.IsWithdrawing || !row.ShowPauseButton) return;
        PauseOne(row);
        StatusText = $"已暂停 {row.PlatformName} · {row.AccountLabel}";
        RefreshStats();
    }

    /// <summary>把一条手动暂停的行放回等待（立即意图：清掉该账号冷却，照重试的规矩）</summary>
    private void ResumeOne(QueuePlatformRow row)
    {
        row.ApplyLocal(QueueRowState.Waiting, 0, null);
        _lastRunAt.Remove(row.SessionKey);
    }

    /// <summary>
    /// 行级「继续」：仅**用户手动暂停**的行。账号掉线挂起的行（PausedBySession）不给手动恢复 ——
    /// 会话没好，点多少次都会被下一轮认领重新挂起；它由登录恢复路径（RecoverOfflineAccountsAsync）自动重排。
    /// </summary>
    [RelayCommand]
    private void ResumeRow(QueuePlatformRow? row)
    {
        if (row is null || !row.ShowResumeButton) return;
        ResumeOne(row);
        StatusText = $"已继续 {row.PlatformName} · {row.AccountLabel}";
        RefreshStats();
        StartConsumer();
    }

    /// <summary>继续全部（登录已过期的账号会在认领时被重新挂起，不会白跑）</summary>
    [RelayCommand]
    private void ResumeAll()
    {
        // 只放用户手动暂停的：PausedBySession 的行在这里放出去只会被下一轮认领重新挂起
        //（旧写法连标志都清了，白走一圈）—— 它由账号恢复路径自动重排
        foreach (var row in _byPlatform.Values.Where(r => r.ShowResumeButton).ToList())
            ResumeOne(row);
        StatusText = "";
        RefreshStats();
        StartConsumer();
    }

    // ═══════════════════════════════════════════════════════════════
    //  勾选（批量暂停 / 继续）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>全选开关（汇总条上的勾选盒）—— 与各行 IsSelected 双向联动，重入用 _syncingSelection 挡</summary>
    [ObservableProperty] private bool _allSelected;
    private bool _syncingSelection;

    partial void OnAllSelectedChanged(bool value)
    {
        if (_syncingSelection) return;
        _syncingSelection = true;
        foreach (var row in _byPlatform.Values) row.IsSelected = value;
        _syncingSelection = false;
        NotifySelection();
    }

    /// <summary>行勾选变化 → 反推全选态（照老版 SelectAll 双向联动的做法）</summary>
    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QueuePlatformRow.IsSelected) && !_syncingSelection)
            SyncAllSelectedFromRows();
    }

    private void SyncAllSelectedFromRows()
    {
        var all = _byPlatform.Count > 0 && _byPlatform.Values.All(r => r.IsSelected);
        if (AllSelected != all)
        {
            _syncingSelection = true;
            AllSelected = all;
            _syncingSelection = false;
        }
        NotifySelection();
    }

    public int SelectedCount => _byPlatform.Values.Count(r => r.IsSelected);
    public bool HasSelection => SelectedCount > 0;

    private void NotifySelection()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        PauseSelectedCommand.NotifyCanExecuteChanged();
        ResumeSelectedCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    /// <summary>批量暂停：只动勾选的行（等待/进行中的才有意义，其余自动跳过）</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void PauseSelected()
    {
        var rows = _byPlatform.Values
            .Where(r => r.IsSelected && !r.IsWithdrawing && r.ShowPauseButton).ToList();
        foreach (var row in rows) PauseOne(row);
        StatusText = rows.Count > 0
            ? $"已暂停选中的 {rows.Count} 条"
            : "勾选的行里没有可暂停的（只有等待中 / 进行中的行能暂停）";
        RefreshStats();
    }

    /// <summary>批量继续：只动勾选的手动暂停行（账号掉线挂起的走自动重排，见 ShowResumeButton）</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ResumeSelected()
    {
        var rows = _byPlatform.Values.Where(r => r.IsSelected && r.ShowResumeButton).ToList();
        foreach (var row in rows) ResumeOne(row);
        StatusText = rows.Count > 0
            ? $"已继续选中的 {rows.Count} 条"
            : "勾选的行里没有可继续的（只有手动暂停的行能继续）";
        RefreshStats();
        if (rows.Count > 0) StartConsumer();
    }

    /// <summary>
    /// 批量删除勾选的行（汇总条「删除选中」）。
    ///
    /// 按行的落点分流，与逐行操作同一套语义：
    ///   · 已发布 → 软删（隐藏）—— 记录是平台侧视频的唯一凭据，硬删会变成幽灵作品；
    ///   · 其余（等待/暂停/失败/正在跑）→ 走 <see cref="DiscardRowsAsync"/>：正在跑的先停再定落点
    ///     （停的瞬间真发出去的会转隐藏，这条守卫只在 DiscardRowsAsync 里有一份，别在这里重写）。
    /// 撤回中的行跳过（删除请求还在路上，再点一次只会打架）。
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteSelectedAsync()
    {
        var selected = _byPlatform.Values
            .Where(r => r.IsSelected && !r.IsWithdrawing)
            .ToList();
        if (selected.Count == 0)
        {
            StatusText = "勾选的行里没有可删除的（撤回中的行请稍候）";
            return;
        }

        var published = selected.Where(r => r.IsPublished).ToList();
        var others = selected.Where(r => !r.IsPublished).ToList();

        var hidden = 0;
        if (published.Count > 0)
        {
            IsBusy = true;
            try { hidden = await PostPerRowAsync(published, "/api/publish/platforms/hide"); }
            finally { IsBusy = false; }
        }

        if (others.Count > 0)
        {
            await DiscardRowsAsync(others,
                hidden > 0 ? $"已隐藏 {hidden} 条已发布记录" : null);
            return;
        }

        // 全是已发布的：DiscardRowsAsync 只收未发布行，收尾文案在这里给
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            RefreshStats();
            StatusText = hidden == published.Count
                ? $"已隐藏 {hidden} 条已发布记录"
                : $"已隐藏 {hidden} 条，{published.Count - hidden} 条失败（可再试）";
        });
    }

    /// <summary>行级「详情」：展开/收起失败全文（只对失败行有效）</summary>
    [RelayCommand]
    private void ToggleErrorDetail(QueuePlatformRow? row)
    {
        if (row is null || !row.IsFailed) return;
        row.IsErrorExpanded = !row.IsErrorExpanded;
    }

    /// <summary>剪贴板写入通道（View 挂载时注入 —— VM 拿不到 TopLevel；探针可注入假实现直测）</summary>
    public Func<string, Task>? ClipboardWriter { get; set; }

    /// <summary>「复制错误」：全文 + 元信息进剪贴板（照老版失败详情弹窗；失败静默，不值得为复制报错打扰用户）</summary>
    [RelayCommand]
    private async Task CopyErrorAsync(QueuePlatformRow? row)
    {
        if (row is null || ClipboardWriter is null) return;
        try
        {
            await ClipboardWriter(row.CopyText);
            StatusText = "已复制错误详情";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Queue] 复制错误详情失败");
        }
    }

    private bool CanClearCompleted() => HasCompleted;

    /// <summary>清空已完成 —— 走服务端软删除（HiddenInQueue），视频管理页仍能查到历史</summary>
    [RelayCommand(CanExecute = nameof(CanClearCompleted))]
    private async Task ClearCompletedAsync()
    {
        var done = _byPlatform.Values.Where(r => r.IsPublished).ToList();
        if (done.Count == 0) return;

        IsBusy = true;
        try
        {
            var hidden = await PostPerRowAsync(done, "/api/publish/platforms/hide");
            await Dispatcher.UIThread.InvokeAsync(RefreshStats);
            StatusText = hidden == done.Count
                ? $"已清空 {done.Count} 条已完成记录"
                : $"已清空 {hidden} 条，{done.Count - hidden} 条失败（可再试）";
        }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// 整卡「隐藏」（已发布的行）—— 逐行软删。
    ///
    /// 可见性从「全部成功（IsDone）」放宽到「有已发布行」：成功+失败混合的卡以前
    /// 隐藏（要全成功）和撤回（只收等待/暂停）两边都够不着，失败行永远收不掉（死卡）。
    /// 现在混合卡可以先把已发布部分收走，剩下的失败行用「撤回」清掉。
    /// </summary>
    [RelayCommand]
    private async Task HideTaskAsync(QueueTaskViewModel? task)
    {
        if (task is null) return;
        var rows = task.Rows.Where(r => r.IsPublished).ToList();
        if (rows.Count == 0) return;

        IsBusy = true;
        try
        {
            var hidden = await PostPerRowAsync(rows, "/api/publish/platforms/hide");
            await Dispatcher.UIThread.InvokeAsync(RefreshStats);
            StatusText = hidden == rows.Count
                ? $"已隐藏 {hidden} 条"
                : $"已隐藏 {hidden} 条，{rows.Count - hidden} 条失败（可再试）";
        }
        finally { IsBusy = false; }
    }

    /// <summary>单行重试：先让服务端把这条置回 Pending，再交给消费循环（不绕开限速）</summary>
    [RelayCommand]
    private async Task RetryRowAsync(QueuePlatformRow? row)
    {
        if (row is null) return;
        await RetryRowsAsync([row]);
    }

    /// <summary>整卡「全部重试」</summary>
    [RelayCommand]
    private async Task RetryTaskAsync(QueueTaskViewModel? task)
    {
        if (task is null) return;
        await RetryRowsAsync([.. task.Rows.Where(r => r.IsFailed)]);
    }

    /// <summary>把若干行重新排队（服务端置回 Pending + 本地回等待）。返回是否**真的**排上了。</summary>
    private async Task<bool> RetryRowsAsync(IReadOnlyList<QueuePlatformRow> rows)
    {
        if (rows.Count == 0) return false;
        IsBusy = true;
        try
        {
            foreach (var row in rows)
                // ⚠️ 服务端 /api/publish/retry 绑的是 RetryPublishRequest(**PlatformId**)；
                // 送 PublishIdRequest（字段名 id）会反序列化成 Guid.Empty → 404，三条重试路径全废
                await ApiClient.PostAsync<object>("/api/publish/retry",
                    new PublishPlatformIdRequest(row.PlatformId));

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var row in rows) row.ApplyLocal(QueueRowState.Waiting, 0, null);
                // 重试是立即意图 —— 不继承上次的冷却，让消费循环马上认领
                foreach (var key in rows.Select(r => r.SessionKey).Distinct()) _lastRunAt.Remove(key);
                StatusText = $"已重新排队 {rows.Count} 条";
                RefreshStats();
            });
            StartConsumer();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Queue] 重试失败");
            StatusText = "重试失败：" + ex.Message;
            return false;
        }
        finally { IsBusy = false; }
    }

    // ═══════════════════════════════════════════════════════════════
    //  放弃 / 撤回（「不想传了」的唯一出口）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>行级「✕ 放弃」：这一条不想传了</summary>
    [RelayCommand]
    private Task DiscardRowAsync(QueuePlatformRow? row)
    {
        // IsWithdrawing 挡的是「删除请求在路上的窗口里再点一次」（第二次进来行已经没了或正在删）
        if (row is null || row.IsPublished || row.IsWithdrawing) return Task.CompletedTask;
        return DiscardRowsAsync([row]);
    }

    /// <summary>卡片「撤回」第一步：亮出确认条（卡级一次可能删多行，破坏性操作要过一步）</summary>
    [RelayCommand]
    private void AskWithdrawTask(QueueTaskViewModel? task)
    {
        if (task is null || !task.HasUnpublished) return;
        task.IsConfirmingWithdraw = true;
    }

    [RelayCommand]
    private void CancelWithdrawTask(QueueTaskViewModel? task)
    {
        if (task is null) return;
        task.IsConfirmingWithdraw = false;
    }

    /// <summary>卡片「撤回」确认：卡内所有未发布成功的行（含失败、含正在跑的）</summary>
    [RelayCommand]
    private async Task ConfirmWithdrawTaskAsync(QueueTaskViewModel? task)
    {
        if (task is null) return;
        // 先收确认条再动手：删除期间网络慢也点不了第二下
        task.IsConfirmingWithdraw = false;
        var rows = task.Rows.Where(r => !r.IsPublished).ToList();
        if (rows.Count > 0) await DiscardRowsAsync(rows);
    }

    /// <summary>
    /// 放弃若干行 —— 队列页的「删除」语义都在这里，按行的**落点**分三种：
    ///   · 等待/暂停/失败 → 硬删记录（平台侧没有成品，删了无副作用）
    ///   · 正在跑 → **先停再删**：取消执行、等执行线程退出再定 ——
    ///     取消是协作式的，如果它已经走到「发布提交」之后，平台侧就真收到了视频，
    ///     那一刻行会照常落成 Published：这种**不能删**（这条记录是平台侧那个视频的
    ///     唯一凭据，删了视频管理页再也看不到 = 幽灵作品），转成隐藏（软删，历史保留）
    ///   · 已发布 → 不进这个方法（「✕」不显示；收走已发布用卡片「隐藏」）
    /// </summary>
    /// <param name="statusPrefix">批量入口（「删除选中」同时收了已发布行）的文案前缀 ——
    /// 已发布那段由调用方先隐藏掉，这里的 parts 只报告未发布行的结果</param>
    private async Task DiscardRowsAsync(IReadOnlyList<QueuePlatformRow> rows, string? statusPrefix = null)
    {
        if (rows.Count == 0) return;

        // ① 先上「撤回中」标记：删除请求在路上的窗口里，消费循环（2 秒一轮）不许认领这些行
        foreach (var row in rows) row.IsWithdrawing = true;

        // ② 正在跑的：取消 + 置「正在停止…」，然后并行等各自的执行线程退出。
        //    等完按落点分流 —— 这一步也是「删除会不会误伤已发出的视频」的判定点
        var running = rows.Where(r => r.IsRunning).ToList();
        foreach (var row in running)
        {
            row.TaskCts?.Cancel();
            row.ApplyLocal(QueueRowState.Paused, 0, "正在停止…");
        }

        await Task.WhenAll(running.Select(async row =>
        {
            var task = row.RunningTask;
            // RunningTask 由消费线程在任务启动的瞬间赋值，存在微秒级窗口 —— 补一拍再读，
            // 免得把「刚要开跑」的行当成「没在跑」直接删（执行线程入口还有 Paused 检查兜底）
            if (task is null) { await Task.Delay(150); task = row.RunningTask; }
            if (task is null) return;
            try { await task; }
            catch { /* 执行线程的异常它自己的 catch 处理完了（置失败/取消返回），这里只等它退出 */ }
        }));

        // ③ 分流：跑着跑着真发出去的转隐藏，其余硬删
        var toHide = rows.Where(r => r.State == QueueRowState.Published).ToList();
        var toDelete = rows.Where(r => r.State != QueueRowState.Published).ToList();

        IsBusy = true;
        try
        {
            var hidden = await PostPerRowAsync(toHide, "/api/publish/platforms/hide");
            var deleted = await PostPerRowAsync(toDelete, "/api/publish/platforms/delete");

            var parts = new List<string>();
            if (deleted > 0) parts.Add($"已放弃 {deleted} 条");
            if (hidden > 0) parts.Add($"{hidden} 条已发布成功，转为隐藏");
            var failedCount = rows.Count - deleted - hidden;
            if (failedCount > 0) parts.Add($"{failedCount} 条操作失败，可再试");

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // 没删成的行要把标记摘掉，否则它永远等不到认领（用户会看到一条永远不动的「等待中」）；
                // 停下来的行改写详情，别让它一直挂着「正在停止…」
                foreach (var row in rows.Where(r => _byPlatform.ContainsKey(r.PlatformId)))
                {
                    row.IsWithdrawing = false;
                    if (row.DetailText == "正在停止…")
                        row.ApplyLocal(QueueRowState.Paused, 0, "已停止，记录未删成，可再放弃");
                }
                RefreshStats();
                if (statusPrefix is not null) parts.Insert(0, statusPrefix);
                StatusText = string.Join("；", parts);
            });
        }
        finally { IsBusy = false; }
    }

    // ── 解绑专用：停稳 + 清行 + 失败回滚（平台账号页解绑一个账号时调用）──

    /// <summary>
    /// 解绑第一步：把该会话在队列里的行**停稳**。先全置「撤回中」再干别的
    /// （先于任何 await —— 消费循环 2 秒一轮不许在这期间认领；只靠 Paused 不行：
    /// 手动「继续全部」会把 Paused 行复活），再对跑中的行取消 + 等执行线程退出。
    /// 取消太晚、已发出去的行照旧落 Published —— 正确语义（解绑会把记录一并删掉，
    /// 平台侧已发布的内容按设计保留）。
    /// 失败路径必须调 <see cref="AbortSessionStop"/> 回滚，否则这些行永远等不到认领。
    /// </summary>
    public async Task StopSessionAsync(Guid sessionId)
    {
        var rows = _byPlatform.Values.Where(r => r.SessionId == sessionId).ToList();
        if (rows.Count == 0) return;

        foreach (var row in rows) row.IsWithdrawing = true;

        var running = rows.Where(r => r.IsRunning).ToList();
        foreach (var row in running)
        {
            row.TaskCts?.Cancel();
            row.ApplyLocal(QueueRowState.Paused, 0, "正在停止…");
        }

        await Task.WhenAll(running.Select(async row =>
        {
            var task = row.RunningTask;
            // 微秒级窗口：任务刚启动的瞬间 RunningTask 还没赋值 —— 补一拍再读（同 DiscardRowsAsync）
            if (task is null) { await Task.Delay(150); task = row.RunningTask; }
            if (task is null) return;
            try { await task; }
            catch { /* 执行线程自己的 catch 处理完了，这里只等它退出 */ }
        }));
    }

    /// <summary>
    /// 解绑失败/中止的回滚：摘掉「撤回中」标记、改写停下来的文案（照 DiscardRowsAsync 的收尾纪律）。
    /// 不做的话这些行永远等不到认领（用户会看到永远不动的「等待中」）。无行时 no-op。
    /// </summary>
    public void AbortSessionStop(Guid sessionId)
    {
        foreach (var row in _byPlatform.Values.Where(r => r.SessionId == sessionId).ToList())
        {
            row.IsWithdrawing = false;
            if (row.DetailText == "正在停止…")
                row.ApplyLocal(QueueRowState.Paused, 0, "已停止（解绑未完成，可重试）");
        }
    }

    /// <summary>
    /// 解绑成功后清掉该会话的本地行与相关字典项（服务端那边已把记录删了）。
    /// platformKey 由调用方传（平台账号页的会话知道自己的平台）—— 行删空后没法再从行上取。
    /// 残留窗口：30 秒轮询里「赶在删除前发出」的陈旧响应可能把行短暂复活，下轮自愈。
    /// </summary>
    public void PurgeSession(Guid sessionId, string platformKey)
    {
        foreach (var row in _byPlatform.Values.Where(r => r.SessionId == sessionId).ToList())
            RemoveRow(row);

        _accountLabels.Remove(sessionId);
        _sessionStatus.Remove(sessionId);
        _awaitingLogin.Remove(sessionId);

        var key = PublishDraftStore.KeyOf(platformKey, sessionId);
        _lastRunAt.Remove(key);
        _gapSeconds.Remove(key);

        RefreshStats();
    }

    /// <summary>
    /// 逐行调服务端并就地移除，返回成功条数；失败的行**留在本地**（可再试）。
    ///
    /// ⚠️ 返回 null 必须当失败：PostAsync 在断网/会员被撤时返回 null **而不抛异常** ——
    /// 只看「没抛」的话，没删成的行会被本地抹掉，30 秒后同步又原样冒出来（用户视角：删了又回来）
    /// </summary>
    private async Task<int> PostPerRowAsync(IReadOnlyList<QueuePlatformRow> rows, string path)
    {
        var ok = 0;
        foreach (var row in rows)
        {
            try
            {
                var resp = await ApiClient.PostAsync<object>(path, new PublishPlatformIdRequest(row.PlatformId));
                if (resp is null)
                {
                    Log.Warning("[Queue] 服务端未确认 {Path}（{Id}）—— 网络失败？", path, row.PlatformId);
                    continue;
                }
                await Dispatcher.UIThread.InvokeAsync(() => RemoveRow(row));
                ok++;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Queue] {Path} 失败 {Platform} {Id}", path, row.PlatformKey, row.PlatformId);
            }
        }
        return ok;
    }

    /// <summary>横幅「去登录」：打开账号坞对应标签，登录成功后自动重试该账号下的失败/暂停行</summary>
    [RelayCommand]
    private void GoLogin()
    {
        var row = LoginBannerRows.FirstOrDefault();
        if (row?.SessionId is not { } sessionId) return;

        _awaitingLogin.Add(sessionId);
        _dock.OpenOrActivate(sessionId, row.PlatformKey, row.PlatformName,
            row.BrandHex, "", row.AccountLabel, forceLogin: true);
        StatusText = "已打开登录页 —— 登录成功后会自动重试该账号下的任务";
    }

    /// <summary>
    /// 账号恢复登录后，把它名下「因过期而挂起/失败」的行重新排队。
    ///
    /// 判据是**会话状态本身**（不再是「已过期」），不是「用户点过横幅」——
    /// 用户完全可能去「平台账号」页重新登录，那条路也得自动接上，否则任务要等到
    /// 用户自己想起来点「继续全部」才动。
    /// </summary>
    private async Task RecoverOfflineAccountsAsync()
    {
        if (_sessionStatus.Count == 0) return;

        bool Healthy(Guid sid) => !SessionUnavailable(sid);

        var rows = _byPlatform.Values
            .Where(r => r.SessionId is { } sid && Healthy(sid)
                && (r.PausedBySession
                    || (_awaitingLogin.Contains(sid) && r.State == QueueRowState.Failed)))
            .ToList();

        foreach (var id in _awaitingLogin.Where(Healthy).ToList()) _awaitingLogin.Remove(id);
        if (rows.Count == 0) return;

        Log.Information("[Queue] 账号已恢复登录，自动重排 {Count} 条", rows.Count);
        // 排上了才报「已恢复」：RetryRowsAsync 失败时它自己写的是「重试失败：…」，
        // 这里再盖一句成功文案就是骗人（而且每 30 秒重复一次）
        if (await RetryRowsAsync(rows))
            StatusText = $"账号已恢复，自动重试 {rows.Count} 条";
    }

    // ═══════════════════════════════════════════════════════════════
    //  工具
    // ═══════════════════════════════════════════════════════════════

    private void RemoveRow(QueuePlatformRow row)
    {
        _byPlatform.Remove(row.PlatformId);
        var owner = _byTask.Values.FirstOrDefault(t => t.Rows.Contains(row));
        if (owner is null) return;
        owner.Rows.Remove(row);
        owner.RaiseDerived();
        if (owner.Rows.Count == 0) { Tasks.Remove(owner); _byTask.Remove(owner.TaskId); }
    }
}
