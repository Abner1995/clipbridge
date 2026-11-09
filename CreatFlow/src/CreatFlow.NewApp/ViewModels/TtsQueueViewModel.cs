using System.Diagnostics;
using Material.Icons;

namespace CreatFlow.NewApp.ViewModels;

/// <summary>
/// 配音队列页 ViewModel（DI 单例 = 页面 + 消费者同体，仿 ComposeQueueViewModel）：
/// **并行执行**配音任务（同时最多 <see cref="Parallelism"/> 条 —— Kokoro 单次推理吃 2 线程，
/// 核数一半留余量），任务快照落 tts_queue.json —— 关掉再开：排队中的续跑、
/// 合成中标「已中断」可重试；队列可**暂停 / 继续**（暂停态进存档：暂停后关掉重开不会自己跑起来）。
/// 配音页列表提交的任务从这里入队，执行走 <see cref="TtsRunner"/>，与页面状态解耦（关掉页面照跑）。
///
/// 许可门槛（用户要求：必须登录且激活之后才可以使用）：消费者领取任务前查一次，拦下就自动暂停；
/// 退出登录 / 账号被禁用时 App 层直接调 <see cref="PauseBecause"/>。
/// </summary>
public partial class TtsQueueViewModel : ViewModelBase
{
    private readonly TtsQueueStore _store;
    private readonly SessionContext _session;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    /// <summary>每条运行中任务的取消令牌（只允许在 UI 线程上读写 —— 领取 / 取消 / 收尾都经 Dispatcher）</summary>
    private readonly Dictionary<TtsTask, CancellationTokenSource> _runCts = [];

    private CancellationTokenSource? _loopCts;
    private bool _started;
    private bool _loaded;
    private bool _suppressPersist;   // 载入存档期间赋值（IsPaused）不落盘 —— 此刻 Tasks 还没装好，写出去就是空队列

    /// <summary>内存队列的**属主会员**（批次 7：落盘一律带它 —— 任务在换号挂起期间跑完也不写错目录）。
    /// 只在 UI 线程写（InitAsync 同步段 / 换装段）。</summary>
    private Guid _loadedMember;

    /// <summary>换号挂起目标（非 null = 已检出换会员、等空闲切换；UI 线程读写）。挂起期间不领新任务。</summary>
    private Guid? _targetMember;

    /// <summary>换号落地闸（并行多 worker 只放一个进来做换装，WaitAsync(0) 抢不到就直接走）</summary>
    private readonly SemaphoreSlim _switchGate = new(1, 1);

    /// <summary>并行度：核数一半（2–4）—— 16 核机器 = 4 条同时合成（2026-10-10 用户要求并行，实测共享
    /// OfflineTts 实例并发合成安全；合成队列仍串行 —— 那是 ffmpeg 重活）</summary>
    public static int Parallelism { get; } = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);

    public TtsQueueViewModel(TtsQueueStore store, SessionContext session)
    {
        _store = store;
        _session = session;
        Tasks.CollectionChanged += (_, _) => NotifySummary();
    }

    public MaterialIconKind HeaderIcon => MaterialIconKind.MusicNote;

    /// <summary>页头副标题（带并行度 —— 写明取自本机逻辑核数，用户要求「根据当前电脑性能进行」）</summary>
    public string HeaderHint =>
        $"批量配音在本机并行合成（本机 {Environment.ProcessorCount} 逻辑核，同时最多 {Parallelism} 条）：可暂停 / 继续；关掉再开能接着跑，合成中被中断的可以重试";

    public ObservableCollection<TtsTask> Tasks { get; } = [];

    /// <summary>统计变化回调（壳刷侧栏徽章）—— 都在 UI 线程触发</summary>
    public Action? OnStatsChanged { get; set; }

    // ═══ 生命周期（壳开壳即启动） ═══

    public void Start()
    {
        // ⚠️ 冒烟只巡检：消费者会真跑配音（往导出目录写 mp3）—— 与合成队列同一道闸，
        // 必须挡在启动之前；冒烟里队列是静止的（入队可用，不执行）
        if (App.SmokeMode) return;

        if (!_started)
        {
            _started = true;
            _loopCts = new CancellationTokenSource();
            _ = InitAsync(_loopCts.Token);
            return;
        }

        // 壳每次打开（登录/自动登录/恢复重建）都会调 Start —— 非首次时检查换会员：
        // 检出后挂起，等在途任务收尾，由消费 worker 载入新会员的队列（批次 7）
        RequestMemberSwitchIfNeeded();
    }

    /// <summary>消费者是否已启动（冒烟断言用：冒烟里必须为 false）</summary>
    public bool IsConsumerStarted => _started;

    /// <summary>换号挂起中（页面预检用：挂起期间不能提交新任务 —— 入队会被拒）</summary>
    public bool IsMemberSwitchPending => _targetMember is not null;

    /// <summary>
    /// 换会员检测（<see cref="Start"/> 每次开壳调用，幂等）：
    /// 属主变了 → 挂起切换并踢一脚信号（空闲时立刻在消费循环落地）；
    /// A→B→A 快速来回 → 撤销挂起（属主其实没变）。
    /// </summary>
    private void RequestMemberSwitchIfNeeded()
    {
        if (!_loaded) return;   // 首载还没完成：InitAsync 会按当前会员装
        var current = _store.CurrentMemberId;
        if (current == _loadedMember)
        {
            _targetMember = null;   // 切回来了：撤销挂起
            return;
        }
        if (_targetMember == current) return;
        _targetMember = current;
        StatusNote = "已切换账号 —— 在途任务跑完后自动载入新账号的队列（期间不能提交新任务）";
        IsStatusError = false;
        _signal.Release(Parallelism);
    }

    /// <summary>
    /// 换号落地（消费 worker 每轮开头调；多 worker 用 <c>_switchGate</c> 只放一个进来）：
    /// 空闲（全体 worker 收工）→ 排空在飞落盘 → 整档换装（含 IsPaused 按新会员的档恢复）。
    /// 不空闲 → 直接返回，下轮再看 —— 「正在合成的那几条跑完停住」同登出暂停语义。
    /// </summary>
    private async Task TryCompleteMemberSwitchAsync()
    {
        if (_targetMember is null) return;   // 快速路径（权威判定在下面的 UI 委托里）
        if (!await _switchGate.WaitAsync(0)) return;
        try
        {
            var ctx = await Dispatcher.UIThread.InvokeAsync(() => (
                Target: _targetMember,
                Idle: _runCts.Count == 0 && Tasks.All(t => t.Status != TtsTaskStatus.Running)));
            if (ctx.Target is not { } target || !ctx.Idle) return;

            // 排空在飞落盘（此前每笔快照都带老会员 id，落的是老会员的档）
            await _saveLock.WaitAsync();
            _saveLock.Release();

            // 按挂起的**目标会员**显式载入（不用 CurrentMemberId —— 期间又换号也不漂移）
            var state = TtsQueueStore.ApplyRestoreRules(await _store.LoadAsync(target));

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_targetMember != target) return;   // 期间被撤销（或目标漂移）→ 放弃本次换装
                Tasks.Clear();
                _suppressPersist = true;               // 换装赋值 IsPaused 不落盘（此刻 Tasks 刚清空）
                IsPaused = state.IsPaused;
                _suppressPersist = false;
                foreach (var d in state.Tasks)
                {
                    var task = TtsTask.FromData(d);
                    Attach(task);
                    Tasks.Add(task);
                }
                _loadedMember = target;
                _targetMember = null;
                if (StatusNote.StartsWith("已切换账号", StringComparison.Ordinal)) StatusNote = "";
            });
            _signal.Release(Parallelism);   // 新队列的待合成马上开跑（暂停态在领取处被拦）
        }
        finally { _switchGate.Release(); }
    }

    private async Task InitAsync(CancellationToken ct)
    {
        if (!_loaded)
        {
            _loaded = true;
            _loadedMember = _store.CurrentMemberId;   // ★ 同步先锁属主（第一个 await 之前）：本队列归它
            var state = TtsQueueStore.ApplyRestoreRules(await _store.LoadAsync(_loadedMember, ct));
            _suppressPersist = true;
            IsPaused = state.IsPaused;
            _suppressPersist = false;
            foreach (var d in state.Tasks)
            {
                var task = TtsTask.FromData(d);
                Attach(task);
                Tasks.Add(task);
            }
        }
        // 并行度个消费 worker：重活在池里跑，UI 只收编组后的更新
        for (var i = 0; i < Parallelism; i++)
            _ = Task.Run(() => ConsumeLoopAsync(ct), ct);
        _signal.Release(Parallelism);   // 启动踢一脚：待合成任务马上开跑（暂停态在领取处被拦）
    }

    // ═══ 统计 / 徽章 ═══

    public int PendingCount => Tasks.Count(t => t.Status == TtsTaskStatus.Pending);
    public int RunningCount => Tasks.Count(t => t.Status == TtsTaskStatus.Running);
    public int DoneCount => Tasks.Count(t => t.Status == TtsTaskStatus.Done);
    public int FailedCount => Tasks.Count(t => t.Status is TtsTaskStatus.Failed or TtsTaskStatus.Interrupted);

    /// <summary>徽章数 = 还没落地的（失败/中断也算，同合成队列的理由：全跑完但有条失败时徽章不能消失）</summary>
    public int BadgeCount => PendingCount + RunningCount + FailedCount;

    public bool HasTasks => Tasks.Count > 0;
    public bool IsEmpty => Tasks.Count == 0;

    public string SummaryText => Tasks.Count == 0
        ? "队列为空 —— 到「配音」页「批量配音」加文本框后点「开始批量合成」"
        : $"共 {Tasks.Count} 条：待合成 {PendingCount} · 合成中 {RunningCount} · 完成 {DoneCount} · 失败 {FailedCount}"
          + (IsPaused ? " · 已暂停" : "");

    private void NotifySummary()
    {
        OnPropertyChanged(nameof(HasTasks));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(RunningCount));
        OnPropertyChanged(nameof(DoneCount));
        OnPropertyChanged(nameof(FailedCount));
        OnPropertyChanged(nameof(BadgeCount));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(CanDeleteSelected));
        OnPropertyChanged(nameof(AllSelected));
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        OnStatsChanged?.Invoke();
    }

    private void Attach(TtsTask task) =>
        task.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TtsTask.Status))
            {
                NotifySummary();
            }
            else if (e.PropertyName == nameof(TtsTask.IsSelected))
            {
                OnPropertyChanged(nameof(SelectedCount));
                OnPropertyChanged(nameof(CanDeleteSelected));
                OnPropertyChanged(nameof(AllSelected));
                DeleteSelectedCommand.NotifyCanExecuteChanged();
            }
        };

    // ═══ 入队 ═══

    /// <summary>配音页批量提交（入队即跑：队列空闲且未暂停时消费者立刻捡起）。返回加入条数。
    /// **换号挂起期间返回 0** —— 此刻入队会落进旧属主的档，等切换完成再交（批次 7）。</summary>
    public int Enqueue(IReadOnlyCollection<TtsTask> tasks)
    {
        if (_targetMember is not null)
        {
            StatusNote = "账号切换中 —— 在途任务跑完后才能提交新任务";
            IsStatusError = false;
            return 0;
        }

        foreach (var task in tasks)
        {
            Attach(task);
            Tasks.Add(task);
        }
        if (tasks.Count == 0) return 0;
        Persist();
        // 按条数踢（最多踢满 worker 数）：几个 worker 睡着时也能同时醒过来各自领一条
        _signal.Release(Math.Min(tasks.Count, Parallelism));
        return tasks.Count;
    }

    // ═══ 暂停 / 继续 ═══

    /// <summary>true = 暂停（队列不再领取新任务；正在合成的跑完停住）。暂停态进存档。</summary>
    [ObservableProperty]
    private bool _isPaused;

    public string PauseButtonText => IsPaused ? "继续" : "暂停";

    partial void OnIsPausedChanged(bool value)
    {
        OnPropertyChanged(nameof(PauseButtonText));
        OnPropertyChanged(nameof(SummaryText));
        if (_suppressPersist) return;
        Persist();
        if (!value) _signal.Release(Parallelism);   // 点「继续」立刻踢一脚（全体 worker），不等 30s 兜底轮询
    }

    [RelayCommand]
    private void TogglePause() => IsPaused = !IsPaused;

    /// <summary>带原因的暂停（App 层调用：退出登录 / 服务端撤销会员）—— 原因显示在状态条</summary>
    public void PauseBecause(string reason)
    {
        IsPaused = true;
        StatusNote = reason;
        IsStatusError = false;
    }

    // ═══ 任务操作 ═══

    /// <summary>取消：排队中 → 直接标已取消；合成中 → 只断那一条的令牌（并行下互不影响）</summary>
    public void Cancel(TtsTask task)
    {
        switch (task.Status)
        {
            case TtsTaskStatus.Pending:
                task.StatusMessage = "已取消";
                task.Status = TtsTaskStatus.Cancelled;
                Persist();
                break;
            case TtsTaskStatus.Running:
                if (_runCts.TryGetValue(task, out var runCts)) runCts.Cancel();
                break;
        }
    }

    [RelayCommand]
    private void CancelTask(TtsTask task) => Cancel(task);

    [RelayCommand]
    private void RetryTask(TtsTask task)
    {
        if (!task.CanRetry) return;
        task.OutputPath = null;
        task.StatusMessage = "";
        task.Status = TtsTaskStatus.Pending;
        Persist();
        _signal.Release();
    }

    [RelayCommand]
    private void RemoveTask(TtsTask task)
    {
        if (!task.CanRemove) return;
        Tasks.Remove(task);
        Persist();
    }

    [RelayCommand]
    private void ClearFinished()
    {
        foreach (var t in Tasks.Where(t => t.Status is TtsTaskStatus.Done or TtsTaskStatus.Cancelled).ToList())
            Tasks.Remove(t);
        Persist();
    }

    // ═══ 勾选批量删除 ═══

    public int SelectedCount => Tasks.Count(t => t.IsSelected);
    public bool CanDeleteSelected => SelectedCount > 0;

    /// <summary>全选开关（表头勾选盒 TwoWay）—— 空队列恒 false</summary>
    public bool AllSelected
    {
        get => HasTasks && Tasks.All(t => t.IsSelected);
        set
        {
            foreach (var t in Tasks) t.IsSelected = value;
            OnPropertyChanged();
        }
    }

    /// <summary>删除勾选：待合成的先取消再删（删除意图就是清掉）；正在合成的删不了（留在队里，提示原因）</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private void DeleteSelected()
    {
        var removed = 0;
        var skipped = 0;
        foreach (var task in Tasks.Where(t => t.IsSelected).ToList())
        {
            if (task.Status == TtsTaskStatus.Pending)
            {
                task.StatusMessage = "已取消";
                task.Status = TtsTaskStatus.Cancelled;
            }
            if (!task.CanRemove) { skipped++; continue; }
            Tasks.Remove(task);
            removed++;
        }
        Persist();
        if (skipped > 0)
        {
            StatusNote = $"已删除 {removed} 条；{skipped} 条正在合成，未删除（可先取消再删）";
            IsStatusError = false;
        }
    }

    [RelayCommand]
    private void OpenTaskFolder(TtsTask task)
    {
        if (string.IsNullOrEmpty(task.OutputPath)) return;
        var dir = Path.GetDirectoryName(task.OutputPath);
        if (dir is null || !ShellLauncher.RevealFolder(dir))
        {
            StatusNote = "打开文件夹失败：文件可能已被移动或删除";
            IsStatusError = true;
        }
    }

    [RelayCommand]
    private void PlayTask(TtsTask task)
    {
        if (string.IsNullOrEmpty(task.OutputPath)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = task.OutputPath, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusNote = "打开播放器失败：" + ex.Message;
            IsStatusError = true;
        }
    }

    // ═══ 状态条 ═══

    [ObservableProperty]
    private string _statusNote = "";

    [ObservableProperty]
    private bool _isStatusError;

    public bool HasStatusNote => !string.IsNullOrEmpty(StatusNote);
    partial void OnStatusNoteChanged(string value) => OnPropertyChanged(nameof(HasStatusNote));

    // ═══ 消费循环（并行度个 worker：跑完一条立刻捡下一条） ═══

    private async Task ConsumeLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var ran = false;
            try
            {
                await TryCompleteMemberSwitchAsync();   // 换号挂起且已空闲 → 先换档再干活
                ran = await RunNextAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Log.Warning(ex, "[TtsQueue] 消费循环异常（继续跑下一条）");
            }

            // 刚跑完一条就立刻看还有没有下一条 —— 老写法跑完先睡 30s 等信号，
            // 排队几条时每条之间都空转 30 秒（2026-10-10 用户实测发现：2 条提交后第二条干等）
            if (ran) continue;
            try
            {
                // 空转靠入队 / 「继续」信号唤醒；30s 兜底轮询（万一有状态变更漏了信号也能捡回来）
                await _signal.WaitAsync(TimeSpan.FromSeconds(30), ct);
            }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>捡一条待合成并跑完；返回 false = 没活干。领取在 UI 线程上原子完成（多 worker 不会抢同一条）。</summary>
    private async Task<bool> RunNextAsync(CancellationToken ct)
    {
        var picked = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            // 暂停态在 UI 线程上再确认一次（用户随时可能点「暂停」）
            if (IsPaused) return ((TtsTask Task, TtsJob Job, CancellationTokenSource Cts)?)null;
            // 换号挂起：不领新任务（取消/移除/清空不受影响 —— 都写老会员的档，正确）
            if (_targetMember is not null) return ((TtsTask Task, TtsJob Job, CancellationTokenSource Cts)?)null;
            var next = Tasks.FirstOrDefault(t => t.Status == TtsTaskStatus.Pending);
            if (next is null) return null;

            // 许可门槛：没登录 / 会员过期 → 自动暂停，任务留在队里等恢复（本机队列也不能无证跑）
            if (FeatureGate.BlockReason(_session.IsExpired, _session.Username, App.SmokeMode, "配音功能") is { } block)
            {
                IsPaused = true;
                StatusNote = block + "。队列已暂停 —— 续费激活后点「继续」";
                IsStatusError = false;
                return null;
            }

            var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _runCts[next] = runCts;
            next.StatusMessage = "正在开始…";
            next.Status = TtsTaskStatus.Running;
            return (next, next.ToJob(), runCts);
        });
        if (picked is null) return false;
        var (task, job, runCts) = picked.Value;

        Persist();   // 先把「合成中」落盘：此刻进程退出 → 下次启动标已中断

        try
        {
            var path = await TtsRunner.RunAsync(job,
                msg => Dispatcher.UIThread.Post(() =>
                {
                    if (task.Status != TtsTaskStatus.Running) return;
                    task.StatusMessage = msg;
                }), runCts.Token);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                task.OutputPath = path;
                task.StatusMessage = "合成完成：" + Path.GetFileName(path);
                task.Status = TtsTaskStatus.Done;
            });
        }
        catch (OperationCanceledException)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                task.StatusMessage = "已取消";
                task.Status = TtsTaskStatus.Cancelled;
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                task.StatusMessage = ex.Message;
                task.Status = TtsTaskStatus.Failed;
            });
        }
        finally
        {
            // 摘令牌 + 置空 + 释放都在 UI 线程上做：与「取消」命令互斥（两边都在这条线上排队）
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_runCts.TryGetValue(task, out var registered) && ReferenceEquals(registered, runCts))
                    _runCts.Remove(task);
                runCts.Dispose();
            });
            Persist();
        }
        return true;
    }

    // ═══ 落盘 ═══

    /// <summary>落盘（先在当前线程快照，再异步写；写操作本身串行化防两个写撞同一临时文件）</summary>
    private void Persist()
    {
        if (_suppressPersist) return;
        if (Dispatcher.UIThread.CheckAccess()) FirePersist();
        else Dispatcher.UIThread.Post(FirePersist);
    }

    private void FirePersist()
    {
        // UI 线程上同时取快照与属主：任务在换号挂起期间收尾 → 仍落老会员的档
        var snapshot = new TtsQueueState
        {
            Tasks = Tasks.Select(t => t.ToData()).ToList(),
            IsPaused = IsPaused,
        };
        _ = SaveSnapshotAsync(snapshot, _loadedMember);
    }

    private async Task SaveSnapshotAsync(TtsQueueState snapshot, Guid member)
    {
        await _saveLock.WaitAsync();
        try
        {
            await _store.SaveAsync(snapshot, member);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[TtsQueue] 队列落盘失败");
        }
        finally
        {
            _saveLock.Release();
        }
    }
}
