using Material.Icons;

namespace CreatFlow.NewApp.ViewModels;

/// <summary>
/// 配音页 ViewModel（2026-10-10 按用户反馈重做为「列表式批量配音」）：
/// 「添加配音」加一条，每条独立设置文案 / 音色 / 语速 —— 可单条试听（合成后播放，不落盘），
/// 「开始批量合成」把每条按各自参数快照进「配音队列」并行生成 mp3。
/// 旧的「单条生成 + 共享音色语速的批量框」已撤 —— 单条 = 列表里只留一条。
///
/// 与合成页共用一份偏好文件（compose_settings.json）：本页写音色 / 语速（取最近编辑的一条，
/// 当新条目默认值）与导出目录；合成页的字段 Load 时记下、Save 时原样回写。
/// 试听与提交都要求登录且激活（<see cref="FeatureGate"/>）；队列消费者领取任务前还会再查一道。
/// </summary>
public partial class TtsViewModel : ViewModelBase, INavigationAware
{
    private readonly ComposeSettingsStore _store;
    private readonly SessionContext _session;
    private readonly TtsQueueViewModel _queue;
    private bool _settingsLoaded;
    private bool _applyingSettings;

    private readonly Mp3PreviewPlayer _player = new();
    private CancellationTokenSource? _previewCts;
    private int _previewRunId;          // 每次试听 / 停止自增：在飞的合成回调靠它认出自己已过期
    private string? _defaultVoiceId;    // 新条目默认音色（最近编辑的那条；进设置存档）
    private double _defaultSpeed = 1.0; // 新条目默认语速（同上）

    // 合成页的字段：本页不动它们 —— Load 时记下、Save 时原样回写（共用一份文件，互不清除）
    private double _keepBgmVolume = 0.25;
    private string? _keepOutputDirectory;
    private bool _keepIsWide;
    private double _keepPitch = 1.0;
    private string? _keepRoleId;   // 合成页选中的角色（角色库 Id）

    public TtsViewModel(ComposeSettingsStore store, SessionContext session, TtsQueueViewModel queue)
    {
        _store = store;
        _session = session;
        _queue = queue;
        // 可选音色清单 = 全部在架引擎的音色（2026-10-10 起只有 Kokoro）—— 一次性建好，
        // 所有行的 ComboBox 共用同一实例（ItemsSource 被整体替换会让 SelectedItem 推送失效，是踩过的坑）
        Voices = [.. TtsEngines.Available.SelectMany(p =>
            p.Voices.Select(v => new TtsVoiceOption(p.Name, v.Id, $"{v.DisplayName} · {(v.IsFemale ? "女" : "男")}")))];
        BatchRows.CollectionChanged += (_, _) =>
        {
            for (var i = 0; i < BatchRows.Count; i++) BatchRows[i].Number = i + 1;
            OnPropertyChanged(nameof(HasBatchRows));
            SubmitBatchCommand.NotifyCanExecuteChanged();
        };
        InsertRow(Voices.FirstOrDefault(), 1.0);   // 默认给一条空行
    }

    public MaterialIconKind HeaderIcon => MaterialIconKind.Microphone;

    // ═══ 配音列表（一条 = 文案 + 独立音色 + 独立语速） ═══

    public ObservableCollection<TtsBatchRow> BatchRows { get; } = [];

    /// <summary>可选音色（含所属引擎；页面级共享清单，所有行的下拉都指向它）</summary>
    public IReadOnlyList<TtsVoiceOption> Voices { get; }

    public bool HasBatchRows => BatchRows.Count > 0;

    private TtsVoiceOption? DefaultVoice =>
        Voices.FirstOrDefault(v => v.ShortName == _defaultVoiceId) ?? Voices.FirstOrDefault();

    [RelayCommand]
    private void AddBatchRow() => InsertRow(DefaultVoice, _defaultSpeed);

    [RelayCommand]
    private void RemoveBatchRow(TtsBatchRow row)
    {
        if (row.IsPreviewing) StopPreview();
        BatchRows.Remove(row);
    }

    private void InsertRow(TtsVoiceOption? voice, double speed)
    {
        var row = new TtsBatchRow { Voice = voice, Speed = speed };
        row.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(TtsBatchRow.Text):
                    SubmitBatchCommand.NotifyCanExecuteChanged();
                    break;
                case nameof(TtsBatchRow.Voice):
                    if (row.Voice is not null) RememberDefaults(row.Voice.ShortName, row.Speed);
                    break;
                case nameof(TtsBatchRow.Speed):
                    RememberDefaults(row.Voice?.ShortName ?? _defaultVoiceId, row.Speed);
                    break;
                default:
                    return;
            }
            // 内容变了 → 旧的试听提示作废（报错留到下次操作覆盖；试听缓存由 key 比对自然失效）
            if (!row.IsStatusError) row.StatusText = "";
        };
        BatchRows.Add(row);
    }

    /// <summary>把某条的音色 / 语速记为「新条目默认值」并落盘（与合成页共用这份偏好）</summary>
    private void RememberDefaults(string? voiceId, double speed)
    {
        if (_applyingSettings) return;
        if (voiceId is not null) _defaultVoiceId = voiceId;
        _defaultSpeed = speed;
        _ = SaveSettingsAsync();
    }

    // ═══ 单条试听（合成后播放，不落盘；同一条内容没变时直接重放缓存） ═══

    /// <summary>试听一条（再点一次停止：合成中取消 / 播放中停掉）。同一时刻只跑一条。</summary>
    [RelayCommand]
    private async Task PreviewRow(TtsBatchRow row)
    {
        if (row.IsPreviewing) { StopPreview(); return; }

        if (string.IsNullOrWhiteSpace(row.Text)) { SetRowStatus(row, "先输入文案再试听", error: true); return; }
        // 许可门槛（用户规则：凡是功能页面，都必须登录且激活才能使用）—— 页面开着、会员中途过期时兜住
        if (FeatureGate.BlockReason(_session.IsExpired, _session.Username, App.SmokeMode, "配音功能") is { } block)
        {
            SetRowStatus(row, block, error: true);
            return;
        }
        var voice = row.Voice ?? Voices.FirstOrDefault();
        if (voice is null)
        {
            SetRowStatus(row, "没有可用的配音音色：请在「AI 设置」页检查 Kokoro 模型", error: true);
            return;
        }
        var provider = TtsProviderFactory.Create(voice.ProviderName);
        if (TtsEngines.IsKokoro(provider) && !KokoroModels.IsReady)
        {
            SetRowStatus(row, "Kokoro 本地模型未下载：请到「AI 设置」页下载模型后再试听", error: true);
            return;
        }

        StopPreview();   // 收掉别的行（同一时刻只跑一条试听）

        var key = PreviewKeyOf(row, voice);
        if (row.PreviewKey == key && row.PreviewAudio is { Length: > 0 })
        {
            StartPlayback(row);
            return;
        }

        var runId = ++_previewRunId;
        _previewCts = new CancellationTokenSource();
        var cts = _previewCts;
        row.IsPreviewing = true;
        SetRowStatus(row, "正在合成…", error: false);
        try
        {
            var result = await provider.SynthesizeAsync(
                new TtsRequest(row.Text.Trim(), voice.ShortName, null, row.Speed, _keepPitch, IncludeSubtitles: false),
                null, cts.Token);
            if (runId != _previewRunId || !row.IsPreviewing) return;   // 已被停止 / 换了行
            row.PreviewKey = key;
            row.PreviewAudio = result.Audio;
            SetRowStatus(row, $"合成完成（{result.Audio.Length / 1024.0:0.#} KB）", error: false);
            StartPlayback(row);
        }
        catch (OperationCanceledException)
        {
            if (runId == _previewRunId) { row.IsPreviewing = false; row.StatusText = ""; }
        }
        catch (Exception ex)
        {
            if (runId == _previewRunId) { row.IsPreviewing = false; SetRowStatus(row, "试听失败：" + ex.Message, error: true); }
        }
        finally
        {
            cts.Dispose();
            if (ReferenceEquals(_previewCts, cts)) _previewCts = null;
        }
    }

    private static string PreviewKeyOf(TtsBatchRow row, TtsVoiceOption voice) =>
        $"{row.Text.Trim()}|{voice.ShortName}|{row.Speed:0.00}";

    private void StartPlayback(TtsBatchRow row)
    {
        row.IsPreviewing = true;
        _player.Play(row.PreviewAudio!, () =>
        {
            // 播完自动回落（换行 / 停止走的是播放器静默中断，不会回调到这里）
            row.IsPreviewing = false;
        });
    }

    /// <summary>收掉当前试听（合成中取消 / 播放中停掉 / 删行、提交前清场）—— 瞬时提示清掉，缓存保留</summary>
    private void StopPreview()
    {
        _previewRunId++;             // 在飞的合成回调就此作废
        _previewCts?.Cancel();
        _player.Stop();
        foreach (var r in BatchRows.Where(r => r.IsPreviewing))
        {
            r.IsPreviewing = false;
            r.StatusText = "";
        }
    }

    // ═══ 批量提交（按条快照进「配音队列」） ═══

    private bool CanSubmitBatch => BatchRows.Any(r => r.HasText);

    /// <summary>把列表里所有非空条目提交进「配音队列」：每条按各自的音色 / 语速快照，提交后列表复位成一条空行。</summary>
    [RelayCommand(CanExecute = nameof(CanSubmitBatch))]
    private void SubmitBatch()
    {
        // 许可门槛（用户规则：凡是功能页面，都必须登录且激活才能使用）
        if (FeatureGate.BlockReason(_session.IsExpired, _session.Username, App.SmokeMode, "配音功能") is { } block)
        {
            SetBatchError(block);
            return;
        }
        if (Voices.Count == 0)
        {
            SetBatchError("没有可用的配音音色：请在「AI 设置」页检查 Kokoro 模型");
            return;
        }
        if (TtsEngines.Available.Any(TtsEngines.IsKokoro) && !KokoroModels.IsReady)
        {
            SetBatchError("Kokoro 本地模型未下载：请到「AI 设置」页下载模型后再生成");
            return;
        }
        // 换号挂起：队列属主与当前账号不一致，此刻提交会被拒（等切换完成再交；批次 7）
        if (_queue.IsMemberSwitchPending)
        {
            SetBatchError("正在切换账号 —— 在途配音跑完后才能提交新任务");
            return;
        }

        StopPreview();   // 提交后列表要复位 —— 先收掉可能正在播放的试听，别留下没有界面的孤儿行

        var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
        var exportDir = ResolveSaveDirectory();
        var tasks = new List<TtsTask>();
        var index = 0;
        foreach (var row in BatchRows)
        {
            var text = row.Text.Trim();
            if (text.Length == 0) continue;
            index++;
            var voice = row.Voice ?? Voices[0];
            tasks.Add(TtsTask.FromSubmit(
                text,
                TtsRunner.Snippet(text, 20),
                voice.ProviderName,
                voice.ShortName,
                row.Speed,
                _keepPitch,
                exportDir,
                TtsRunner.SuggestName(text, stamp, index)));
        }

        var added = _queue.Enqueue(tasks);
        if (added == 0 && tasks.Count > 0)
        {
            // 入队被拒（换号挂起的竞态窗口）—— 行不清空，文案给队列状态，别误导成"跳过空文案"
            SetBatchError(_queue.StatusNote);
            return;
        }
        var skipped = BatchRows.Count - added;
        BatchRows.Clear();
        InsertRow(DefaultVoice, _defaultSpeed);   // 复位成一条空行，音色 / 语速沿用最近用的
        SetBatchStatus($"已提交 {added} 条到「配音队列」（音色 / 语速按条快照）"
            + (skipped > 0 ? $"；跳过 {skipped} 条空文案" : "")
            + $" —— mp3 存到 {exportDir}，到队列页看进度（可暂停 / 继续 / 删除）");
    }

    // ═══ 状态条 ═══

    [ObservableProperty]
    private string _batchStatus = "";

    [ObservableProperty]
    private bool _isBatchStatusError;

    public bool HasBatchStatus => !string.IsNullOrEmpty(BatchStatus);
    partial void OnBatchStatusChanged(string value) => OnPropertyChanged(nameof(HasBatchStatus));

    private void SetBatchStatus(string msg)
    {
        BatchStatus = msg;
        IsBatchStatusError = false;
    }

    private void SetBatchError(string msg)
    {
        BatchStatus = msg;
        IsBatchStatusError = true;
    }

    private static void SetRowStatus(TtsBatchRow row, string text, bool error)
    {
        row.StatusText = text;
        row.IsStatusError = error;
    }

    // ═══ 注入（View 提供 StorageProvider） ═══

    /// <summary>选择默认保存目录</summary>
    public Func<Task<string?>>? PickExportFolderAsync { get; set; }

    /// <summary>跳「配音队列」—— 页 VM 不认识壳，导航能力由壳注入（与合成页同范式）</summary>
    public Action<string>? OnNavigateRequested { get; set; }

    [RelayCommand]
    private void GoQueue() => OnNavigateRequested?.Invoke("ttsqueue");

    // ═══ 保存目录 / 偏好 ═══

    /// <summary>生成的 mp3 的默认保存目录（空 = 下载目录兜底）</summary>
    [ObservableProperty]
    private string _exportDirectory = "";

    partial void OnExportDirectoryChanged(string value)
    {
        OnPropertyChanged(nameof(SaveTargetText));
        _ = SaveSettingsAsync();
    }

    /// <summary>当前默认保存目标（设置目录还在就用它，否则下载目录）</summary>
    public string SaveTargetText => ResolveSaveDirectory();

    [RelayCommand]
    private async Task PickExportFolder()
    {
        var dir = PickExportFolderAsync == null ? null : await PickExportFolderAsync();
        if (!string.IsNullOrEmpty(dir)) ExportDirectory = dir;
    }

    /// <summary>默认保存目录：配音页设置（目录还在）→ 下载目录 → 桌面</summary>
    public string ResolveSaveDirectory()
    {
        if (!string.IsNullOrEmpty(ExportDirectory) && Directory.Exists(ExportDirectory)) return ExportDirectory;
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(downloads)) return downloads;
        return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    }

    /// <summary>进页加载偏好一次（保留本次会话里未保存的编辑不被切页清掉）</summary>
    public void OnNavigatedTo()
    {
        if (_settingsLoaded) return;
        _settingsLoaded = true;
        _ = LoadSettingsAsync();
    }

    private async Task LoadSettingsAsync()
    {
        _applyingSettings = true;
        try
        {
            var s = await _store.LoadAsync();
            _defaultVoiceId = s.VoiceId;
            _defaultSpeed = Math.Clamp(s.Speed, 0.5, 2.0);
            ExportDirectory = s.ExportDirectory ?? "";
            // 合成页的字段：记下原样回写
            _keepPitch = Math.Clamp(s.Pitch, 0.5, 2.0);
            _keepBgmVolume = Math.Clamp(s.BgmVolume, 0, 1);
            _keepOutputDirectory = s.OutputDirectory;
            _keepIsWide = s.IsWide;
            _keepRoleId = s.RoleId;

            // 首行还是空的就套用上次的默认音色 / 语速（用户已经打字了就不动）
            if (BatchRows.Count == 1 && !BatchRows[0].HasText)
            {
                BatchRows[0].Voice = DefaultVoice;
                BatchRows[0].Speed = _defaultSpeed;
            }
            OnPropertyChanged(nameof(SaveTargetText));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Tts] 读取配音偏好失败，用默认值");
        }
        finally
        {
            _applyingSettings = false;
        }
    }

    /// <summary>偏好落盘（首次加载完成前不动盘；加载过程中被赋值的属性也不触发保存；冒烟不写用户文件）</summary>
    private async Task SaveSettingsAsync()
    {
        if (!_settingsLoaded || _applyingSettings) return;
        if (App.SmokeMode) return;   // 冒烟会在页面上改音色 / 语速造景，别写进用户真实偏好
        try
        {
            await _store.SaveAsync(new ComposeSettings
            {
                ProviderName = DefaultVoice?.ProviderName,
                VoiceId = _defaultVoiceId,
                Speed = _defaultSpeed,
                Pitch = _keepPitch,
                BgmVolume = _keepBgmVolume,
                OutputDirectory = _keepOutputDirectory,
                RoleId = _keepRoleId,
                ExportDirectory = string.IsNullOrWhiteSpace(ExportDirectory) ? null : ExportDirectory,
                IsWide = _keepIsWide,
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Tts] 保存配音偏好失败");
        }
    }
}
