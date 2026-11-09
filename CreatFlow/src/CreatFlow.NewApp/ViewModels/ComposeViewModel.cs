using System.ComponentModel;
using CreatFlow.Media.Llm;
using Material.Icons;

namespace CreatFlow.NewApp.ViewModels;

/// <summary>
/// 合成视频页 —— 素材（图片/视频混排）→ 配音（内联 Kokoro 本地，可试听）→ 图片轮播 + 字幕 → MP4。
///
/// 自老 App ComposeViewModel 移植；2026-10-10 先砍掉独立配音页把控件内联到本页、同日又按用户决策
/// 恢复独立配音页 —— 分工：配音页负责「生成 mp3 文件」，本页内联控件负责「边配边合成」。
///
/// 产出即发布单元：mp4 直接写进 <c>&lt;输出目录&gt;/&lt;标题&gt;/</c>，并补 params.txt 与 cover.jpg
/// （<see cref="ComposeUnitWriter"/>）——发布页「导入文件夹」可直接识别。
///
/// 批次 2 起**单条合成也入队**：点「合成」= 组装快照 → 合成队列（串行消费者立刻捡起），
/// 页面交给队列后即复位、只留一行静态确认 —— 本页不做进度镜像，进度 / 结果 / 取消 / 重试
/// 都在「合成队列」页按行看（切页/关页合成照跑）。
/// 提交成功后**自动复位本次输入**（2026-10-11 用户要求，与配音页「提交后列表复位」对齐；
/// 同日反馈：镜像的流式状态「正在合成语音…」+ 灰按钮看起来像被队列锁住，故撤掉镜像只留静态确认）：
/// 任务已由队列兜住，页面腾给下一条；音色/语速/画幅/输出目录保留。
/// 试听 / 提交都要求登录且激活（<see cref="FeatureGate"/>）；队列消费者领取任务前还会再查一道。
/// </summary>
public partial class ComposeViewModel : ViewModelBase, INavigationAware
{
    private readonly ComposeSettingsStore _store;
    private readonly WatermarkSettingsStore _watermark;
    private readonly ComposeQueueViewModel _queue;
    private readonly SessionContext _session;
    private readonly ComposeRolesService _roleService;
    private readonly RoleOptimizer _roleOptimizer;
    private readonly AccountChipsService _chips;
    private readonly AccountGroupsService _groups;
    private bool _settingsLoaded;
    private bool _applyingSettings;

    // 试听播放器（播放引用由它自己持有，防 GC 中途回收）
    private CancellationTokenSource? _previewCts;
    private readonly Mp3PreviewPlayer _player = new();

    public ComposeViewModel(ComposeSettingsStore store, WatermarkSettingsStore watermark,
        ComposeQueueViewModel queue,
        SessionContext session, ComposeRolesService roleService, RoleOptimizer roleOptimizer,
        AccountChipsService chips, AccountGroupsService groups)
    {
        _store = store;
        _watermark = watermark;
        _queue = queue;
        _session = session;
        _roleService = roleService;
        _roleOptimizer = roleOptimizer;
        _chips = chips;
        _groups = groups;

        // 自动发布账号芯片：勾选变化 → 转报共享服务（发布页读同一份，「在哪边改都生效」）+ 播报摘要
        TargetChips.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is null) return;
            foreach (AccountChipViewModel chip in e.NewItems)
                chip.PropertyChanged += OnTargetChipPropertyChanged;
            RaiseAutoPublishSummary();
        };
        // 可用引擎 = 共享工厂除去已下架的 Edge 免费（配音页同款清单）
        Engines = [.. TtsEngines.Available.Select(p => new ComposeEngineOption(p, TtsEngines.NoteFor(p.Name)))];
        AudioSources =
        [
            new("text", "文本合成配音", "文案 → 配音 + 字幕"),
            new("local", "本地音频文件", "用现成音频当配音"),
        ];
        Aspects =
        [
            new("portrait", "9:16 竖屏", "抖音 / 快手 / 视频号"),
            new("wide", "16:9 横屏", "B 站 / 西瓜"),
        ];
        AudioSources[0].IsSelected = true;
        Aspects[0].IsSelected = true;

        // 计算属性不会自己播报（多视图常驻可视树的坑）——集合变化手动刷
        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(ItemsSummary));
            ComposeCommand.NotifyCanExecuteChanged();
        };

        SelectedEngine = Engines[0];
    }

    public MaterialIconKind HeaderIcon => MaterialIconKind.VideoOutline;

    // ═══ 选项组（自绘单选行；发布页 / AI 设置页同款模式） ═══

    public IReadOnlyList<ComposeEngineOption> Engines { get; }
    public IReadOnlyList<ComposeChoice> AudioSources { get; }
    public IReadOnlyList<ComposeChoice> Aspects { get; }

    /// <summary>点选引擎（不给选项自身写命令 —— 「命令 + TwoWay 各翻一次互相抵消」是实测过的坑）</summary>
    [RelayCommand]
    private void SelectEngine(ComposeEngineOption option) => SelectedEngine = option;

    [RelayCommand]
    private void SelectSource(ComposeChoice option) => UseLocalAudio = option.Key == "local";

    [RelayCommand]
    private void SelectAspect(ComposeChoice option) => IsWide = option.Key == "wide";

    // ═══ 配音 ═══

    [ObservableProperty]
    private ComposeEngineOption? _selectedEngine;

    partial void OnSelectedEngineChanged(ComposeEngineOption? value)
    {
        foreach (var e in Engines) e.IsSelected = ReferenceEquals(e, value);

        // 引擎切换 → 音色列表联动：由引擎自带的音色表转成下拉项
        Voices = value?.Provider is { Voices.Count: > 0 } p
            ? [.. p.Voices.Select(v => new TtsVoiceOption(p.Name, v.Id, $"{v.DisplayName} · {(v.IsFemale ? "女" : "男")}"))]
            : [];
        SelectedVoice = Voices.FirstOrDefault();
        _ = SaveSettingsAsync();
    }

    [ObservableProperty]
    private List<TtsVoiceOption> _voices = [];

    [ObservableProperty]
    private TtsVoiceOption? _selectedVoice;

    partial void OnSelectedVoiceChanged(TtsVoiceOption? value) => _ = SaveSettingsAsync();

    /// <summary>语速倍率（0.5–2.0）</summary>
    [ObservableProperty]
    private double _speed = 1.0;

    partial void OnSpeedChanged(double value) => _ = SaveSettingsAsync();

    /// <summary>音调倍率 —— Kokoro 不支持，音调控件已随 Edge 下架移除（2026-10-10）。
    /// 字段保留：设置存档兼容（老数据里的值原样读回）与缓存 key 稳定。</summary>
    [ObservableProperty]
    private double _pitch = 1.0;

    partial void OnPitchChanged(double value) => _ = SaveSettingsAsync();

    /// <summary>true = 用本地音频文件；false = 文本合成配音（默认）</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ComposeCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewCommand))]
    private bool _useLocalAudio;

    /// <summary>文本合成配音（UseLocalAudio 的反向，供视图显隐绑定）</summary>
    public bool UseTextAudio => !UseLocalAudio;

    partial void OnUseLocalAudioChanged(bool value)
    {
        OnPropertyChanged(nameof(UseTextAudio));
        AudioSources[0].IsSelected = !value;
        AudioSources[1].IsSelected = value;
    }

    /// <summary>
    /// AI 分镜模式：文案按行拆句（句数 = 素材数），逐句配音、图片镜=句长、视频镜完整展示。
    /// 关闭 = 整段配音模式（与老版 v1 行为一致）。
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ComposeCommand))]
    private bool _useShots;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ComposeCommand))]
    private string? _localAudioPath;

    /// <summary>要配音的文案（整段模式 = 全文；分镜模式 = 一行一句）</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ComposeCommand))]
    [NotifyCanExecuteChangedFor(nameof(OptimizeTextCommand))]
    private string _text = "";

    partial void OnTextChanged(string value)
    {
        NotifyFinalTextChanged();
        // 手动改文案 = 之前的优化结果作废（防"应用了旧结果却以为在点评新文案"）
        if (!IsOptimizing)
        {
            OptimizeResult = "";
            OptimizeResultTitle = "";
            OptimizeMessage = "";
            IsOptimizeError = false;
        }
    }

    // ═══ 角色库 / AI 优化（2026-10-11 批次 4） ═══

    /// <summary>可选角色（首位哨兵「（不使用角色）」；进页全量刷新，跨页改角色库后回来能立刻看到）</summary>
    [ObservableProperty]
    private IReadOnlyList<ComposeRole> _roles = [ComposeRole.None];

    [ObservableProperty]
    private ComposeRole? _selectedRole = ComposeRole.None;

    /// <summary>设置里记的角色 Id（用户手选后同步）——进页刷新时按它找回选择、角色被删落回哨兵</summary>
    private string? _savedRoleId;

    partial void OnSelectedRoleChanged(ComposeRole? value)
    {
        _savedRoleId = value is { IsNone: false } r ? r.Id : null;
        OnPropertyChanged(nameof(SelectedRoleIndex));
        NotifyFinalTextChanged();
        OptimizeTextCommand.NotifyCanExecuteChanged();
        _ = SaveSettingsAsync();
    }

    /// <summary>角色下拉的 SelectedIndex 代理 —— 列表会被重建的组合框必须走 SelectedIndex：
    /// SelectedItem 值匹配推送在替换 ItemsSource 后会失效（2026-08 快手地区级联踩过的坑）。</summary>
    public int SelectedRoleIndex
    {
        get
        {
            if (SelectedRole is null) return -1;
            for (var i = 0; i < Roles.Count; i++)
                if (Roles[i].Id == SelectedRole.Id) return i;
            return 0;
        }
        set
        {
            if (value >= 0 && value < Roles.Count) SelectedRole = Roles[value];   // 换源回写 -1 → 忽略
        }
    }

    /// <summary>生效角色（哨兵 = null）</summary>
    private ComposeRole? ActiveRole => SelectedRole is { IsNone: false } r ? r : null;

    /// <summary>最终文案（配音 + 字幕的真实文本）= 台词模板套在文案上 —— 提交校验 / 只读预览 /
    /// 试听取样 / 入队快照四处共用这一份（单一真源，任何一处自己拼模板都会漂移）</summary>
    public string FinalText => ComposeRoleTemplate.Apply(ActiveRole?.Template, Text, UseShots);

    public string FinalTextHint => ActiveRole is { } role
        ? $"已套用角色「{role.Name}」的台词模板 —— 提交的配音与字幕就是这份文本"
          + (UseShots && role.Template.Contains('\n') ? "；分镜模式下模板按单行套用（保证一行 = 一镜）" : "")
        : "";

    public bool HasRoleWrap => ActiveRole is not null && Text.Length > 0;

    private void NotifyFinalTextChanged()
    {
        OnPropertyChanged(nameof(FinalText));
        OnPropertyChanged(nameof(FinalTextHint));
        OnPropertyChanged(nameof(HasRoleWrap));
        OnPropertyChanged(nameof(CanShowPromptPreview));
        OnPropertyChanged(nameof(PromptPreviewText));
        if (!CanShowPromptPreview) ShowPromptPreview = false;
    }

    // ── AI 优化（结果预览后手动应用；顺序：先优化裸文案、再套模板 —— 模板绝不进 LLM） ──

    private CancellationTokenSource? _optimizeCts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OptimizeButtonText))]
    [NotifyPropertyChangedFor(nameof(HasOptimizeResult))]
    [NotifyCanExecuteChangedFor(nameof(OptimizeTextCommand))]
    private bool _isOptimizing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOptimizeResult))]
    private string _optimizeResult = "";

    [ObservableProperty]
    private string _optimizeResultTitle = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOptimizeMessage))]
    private string _optimizeMessage = "";

    [ObservableProperty]
    private bool _isOptimizeError;

    public bool HasOptimizeResult => IsOptimizing || OptimizeResult.Length > 0;
    public bool HasOptimizeMessage => !string.IsNullOrEmpty(OptimizeMessage);
    public string OptimizeButtonText => IsOptimizing ? "取消优化" : "AI 优化文案";

    /// <summary>「查看 AI 优化提示词」开关（只有角色有人设才可看）</summary>
    [ObservableProperty]
    private bool _showPromptPreview;

    public bool CanShowPromptPreview => ActiveRole is { HasPersona: true };
    public string PromptPreviewText => ActiveRole is { HasPersona: true } role
        ? LlmTextOptimizer.BuildPrompt(role.Persona, role.Knowledge, Text)
        : "";

    /// <summary>AI 优化可用性：文本合成 + 有文案 + 角色有人设（优化中 = 可点取消）</summary>
    private bool CanOptimizeText =>
        !UseLocalAudio && !string.IsNullOrWhiteSpace(Text) &&
        (IsOptimizing || ActiveRole is { HasPersona: true });

    /// <summary>AI 优化文案：结果进「优化结果」区预览、原文不动；再点 = 取消（首次要加载本地模型，慢）。</summary>
    [RelayCommand(CanExecute = nameof(CanOptimizeText))]
    private async Task OptimizeTextAsync()
    {
        if (IsOptimizing) { StopOptimize(); return; }

        // 许可门槛（用户规则：凡是功能页面，都必须登录且激活才能使用）—— AI 优化花算力，同样挡一道
        if (FeatureGate.BlockReason(_session.IsExpired, _session.Username, App.SmokeMode, "AI 文案优化") is { } block)
        {
            SetOptimizeMessage(block, isError: true);
            return;
        }
        var role = ActiveRole;
        if (role is null || !role.HasPersona)
        {
            SetOptimizeMessage("先选一个填了人设提示词的角色（到「AI 设置 → 角色库」里填）", isError: true);
            return;
        }

        _optimizeCts?.Cancel();
        _optimizeCts = new CancellationTokenSource();
        var ct = _optimizeCts.Token;
        IsOptimizing = true;
        OptimizeResult = "";
        OptimizeResultTitle = "";
        SetOptimizeMessage("正在按角色人设优化文案…（首次使用本地引擎需加载模型，可能要几十秒）", isError: false);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await _roleOptimizer.OptimizeAsync(role.Persona, role.Knowledge, Text, ct);
            if (ct.IsCancellationRequested) return;   // 取消后别写回（清空/提交可能刚清了状态）
            sw.Stop();
            OptimizeResult = result;
            OptimizeResultTitle = $"优化结果（{sw.Elapsed.TotalSeconds:0.#}s）· 原文未改动";
            var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
            var rawLines = Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
            var lineWarn = UseShots && lines != rawLines;
            SetOptimizeMessage(lineWarn
                    ? $"满意就点「应用」替换上面的文案；注意：优化结果 {lines} 行 ≠ 原文 {rawLines} 行，分镜模式（句数 = 素材数）提交前请改成一致"
                    : "满意就点「应用」替换上面的文案（不满可再点一次「AI 优化文案」重新生成）",
                isError: lineWarn);
        }
        catch (OperationCanceledException)
        {
            // 再点一次 = 取消；无需动作
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested) SetOptimizeMessage("优化失败：" + ex.Message, isError: true);
        }
        finally
        {
            if (!ct.IsCancellationRequested) IsOptimizing = false;
        }
    }

    private void SetOptimizeMessage(string msg, bool isError)
    {
        OptimizeMessage = msg;
        IsOptimizeError = isError;
    }

    /// <summary>应用优化结果：替换文案框（最终文案预览自动刷新、结果区随之清空）</summary>
    [RelayCommand]
    private void ApplyOptimizeResult()
    {
        if (OptimizeResult.Length == 0) return;
        Text = OptimizeResult;
        SetOptimizeMessage("已应用到文案框（可继续手动修改）", isError: false);
    }

    [RelayCommand]
    private void DiscardOptimizeResult()
    {
        OptimizeResult = "";
        OptimizeResultTitle = "";
        OptimizeMessage = "";
        IsOptimizeError = false;
    }

    /// <summary>停止优化并清干净（清空参数 / 提交 / 再点按钮都走这里）</summary>
    private void StopOptimize()
    {
        _optimizeCts?.Cancel();
        IsOptimizing = false;
        OptimizeResult = "";
        OptimizeResultTitle = "";
        OptimizeMessage = "";
        IsOptimizeError = false;
    }

    /// <summary>跳「AI 设置」（角色库在那边管理）—— 与跳队列同范式（导航能力由壳注入）</summary>
    [RelayCommand]
    private void GoRoleLibrary() => OnNavigateRequested?.Invoke("ai");

    private async Task RefreshRolesAsync()
    {
        try
        {
            // 角色库走共享服务（批次 8 上云）：换会员自动重载；AI 设置页改完切过来即时可见
            await _roleService.EnsureLoadedAsync();
            var fresh = new List<ComposeRole> { ComposeRole.None };
            fresh.AddRange(_roleService.Roles);
            // 只在角色库真变了时重建列表（值相等就跳过 —— 少触发控件换源；级联组合框的换源有已知坑）
            if (!Roles.SequenceEqual(fresh)) Roles = fresh;
            // 按 Id 找回当前选择（角色被删 → 落回哨兵）；首次进页用设置里记的角色
            var targetId = SelectedRole is { IsNone: false } r ? r.Id : _savedRoleId;
            var next = targetId is { Length: > 0 }
                ? fresh.FirstOrDefault(x => x.Id == targetId) ?? ComposeRole.None
                : ComposeRole.None;
            if (!Equals(SelectedRole, next)) SelectedRole = next;   // 值相同不赋值（免无谓写盘/通知）
            OnPropertyChanged(nameof(SelectedRoleIndex));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Compose] 读取角色库失败");
        }
    }

    // ═══ 素材 ═══

    /// <summary>素材列表（图片/视频混排，顺序即播放顺序）</summary>
    public ObservableCollection<ComposeMediaItem> Items { get; } = [];

    public bool HasItems => Items.Count > 0;

    public string ItemsSummary => Items.Count == 0
        ? "还没有素材"
        : $"{Items.Count} 个素材（{Items.Count(i => i.Kind == MediaKind.Image)} 图 · {Items.Count(i => i.Kind == MediaKind.Video)} 视频）";

    // ═══ 标题 / 背景音乐 / 画幅 ═══

    /// <summary>发布标题：发布单元文件夹名与 params.txt 的「标题」（发布页可改）</summary>
    [ObservableProperty]
    private string _publishTitle = "";

    /// <summary>画面叠加标题（可选，视频前 3 秒大字；与发布标题是两回事）</summary>
    [ObservableProperty]
    private string? _overlayTitle;

    [ObservableProperty]
    private string? _bgmPath;

    /// <summary>BGM 音量 0~1（默认 0.25，不盖配音）</summary>
    [ObservableProperty]
    private double _bgmVolume = 0.25;

    partial void OnBgmVolumeChanged(double value) => _ = SaveSettingsAsync();

    public bool HasBgm => !string.IsNullOrEmpty(BgmPath);
    partial void OnBgmPathChanged(string? value) => OnPropertyChanged(nameof(HasBgm));

    /// <summary>true = 16:9 横屏；false = 9:16 竖屏（默认）</summary>
    [ObservableProperty]
    private bool _isWide;

    partial void OnIsWideChanged(bool value)
    {
        Aspects[0].IsSelected = !value;
        Aspects[1].IsSelected = value;
        _ = SaveSettingsAsync();
    }

    // ═══ 输出 / 定时发布 ═══

    /// <summary>输出根目录（默认 用户/Videos/CreatFlow；每条视频在其中落成一个发布单元子文件夹）</summary>
    [ObservableProperty]
    private string _outputDirectory = DefaultDirectory;

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos", "CreatFlow");

    /// <summary>定时发布（写入发布单元的「发布时间」；不勾 = 立即）</summary>
    [ObservableProperty]
    private bool _isScheduled;

    [ObservableProperty]
    private DateTimeOffset? _scheduledDate;

    [ObservableProperty]
    private TimeSpan? _scheduledTime;

    partial void OnIsScheduledChanged(bool value)
    {
        // 勾上时给个顺手的默认：明天 20:00（发布页定时窗口是 2 小时~14 天，明天 20 点稳在窗口内）
        if (value && ScheduledDate is null)
        {
            ScheduledDate = DateTime.Today.AddDays(1);
            ScheduledTime ??= new TimeSpan(20, 0, 0);
        }
    }

    /// <summary>定时时刻（勾了定时但日期/时间不全 → null，合成前会拦）</summary>
    public DateTime? ScheduledAtOrNull =>
        IsScheduled && ScheduledDate is { } d && ScheduledTime is { } t ? d.Date.Add(t) : null;

    // ═══ 合成完成后自动发布（2026-10-11 用户拍板：账号芯片与发布页同款、共享「上次勾选」）═══

    /// <summary>目标账号芯片（装配与勾选存档在 <see cref="AccountChipsService"/> —— 与发布页同一份）</summary>
    public ObservableCollection<AccountChipViewModel> TargetChips { get; } = [];

    /// <summary>「合成完成后自动发布」开关（存共享存档；进页时从服务载入）</summary>
    [ObservableProperty]
    private bool _autoPublish;

    partial void OnAutoPublishChanged(bool value)
    {
        _ = _chips.SetAutoPublishAsync(value);
        RaiseAutoPublishSummary();
    }

    public bool HasTargetChips => TargetChips.Count > 0;

    /// <summary>自动发布摘要（开关旁一行字）—— 开且没勾账号 = 红色警示（提交会被拦）</summary>
    public string AutoPublishSummary
    {
        get
        {
            if (!AutoPublish)
                return "关闭 —— 合成产物照常落到输出目录，可在「发布视频」页导入发布";
            var on = TargetChips.Count(t => t.IsOn && t.CanPublish);
            return on > 0
                ? $"将发布到 {on} 个账号（共 {on} 条平台记录）· 标题与定时来自本页，简介/话题等跟随发布单元与账号预设"
                : "已开启但没有勾选任何账号 —— 勾一个账号，或关掉这个开关（现在点「开始合成」会被拦下）";
        }
    }

    /// <summary>红字警示：开了自动发布却一个账号都没勾（**含一个账号都没绑**的情形 —— 空列表全不勾也是真空）
    /// 与 <see cref="AutoPublishSummary"/> 的判据必须同源（on == 0），否则「文字在警告、颜色却是灰的」</summary>
    public bool IsAutoPublishSummaryError =>
        AutoPublish && TargetChips.Where(t => t.CanPublish).All(t => !t.IsOn);

    private void OnTargetChipPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AccountChipViewModel.IsOn)
            || sender is not AccountChipViewModel chip) return;

        // 一键勾选（组条）期间：逐颗记账由整批的 ReplaceSelectionAsync 接管 ——
        // 摘要/高亮也在批量结束后补一次（见 ApplyTargetGroupAsync）
        if (_applyingGroup) return;

        // 勾选进共享存档（发布页读同一份）；本页只播报摘要
        _chips.RecordToggle(chip.SessionId, chip.IsOn);
        RaiseAutoPublishSummary();
        RefreshActiveGroup();   // 手动微调后「哪一组正被应用」的高亮跟着走
    }

    // ═══ 账号分组组条（2026-10-14 用户需求：点组名 = 一键只勾选该组）═══
    // —— 与发布页 GlobalTargets 那份同款（组条 XAML 亦然），改一处要同步另一处

    /// <summary>组条数据源（第 0 格恒为「全部」哨兵；没有真分组时整条隐藏）</summary>
    public ObservableCollection<AccountGroupChoiceViewModel> GroupChoices { get; } = [];

    /// <summary>有没有真分组（只有「全部」一格 = 没分组）</summary>
    public bool HasGroupChoices => GroupChoices.Count > 1;

    /// <summary>一键勾选进行中（循环内短路逐颗记账；循环结束后统一补摘要/高亮）</summary>
    private bool _applyingGroup;

    /// <summary>点一格：把勾选整体替换成「该格成员 / 全部」。所有芯片保持可见，可再手动微调。</summary>
    [RelayCommand]
    private async Task ApplyTargetGroupAsync(AccountGroupChoiceViewModel? choice)
    {
        if (choice is null || choice.IsEmpty) return;

        var all = TargetChips.Where(c => c.CanPublish).Select(c => c.SessionId).ToList();
        HashSet<Guid> on;
        if (choice.Id == AccountGroups.AllGroupsId)
        {
            on = [.. all];
        }
        else
        {
            var group = _groups.Groups.FirstOrDefault(g => g.Id == choice.Id);
            if (group is null) return;   // 组刚在平台页被删（组条还没刷新）——静默忽略
            on = AccountGroups.Members(group, all);
        }

        // 同步循环改 IsOn（循环内**不得 await** —— ReplaceSelectionAsync 的契约）
        _applyingGroup = true;
        try
        {
            foreach (var c in TargetChips.Where(c => c.CanPublish)) c.IsOn = on.Contains(c.SessionId);
        }
        finally { _applyingGroup = false; }

        await _chips.ReplaceSelectionAsync(on, all);
        RaiseAutoPublishSummary();   // 「将发布到 N 个账号」立刻跟着变
        RefreshActiveGroup();
    }

    /// <summary>组条重建（进页/账号芯片刷新时：分母与成员存活都变了）。空组也列出（灰着）。
    /// internal：冒烟特判直接调（离线时不会被 RefreshTargetChipsAsync 失败挡在半路）。</summary>
    internal async Task RefreshGroupChoicesAsync()
    {
        await _groups.EnsureLoadedAsync();
        var all = TargetChips.Where(c => c.CanPublish).Select(c => c.SessionId).ToList();

        GroupChoices.Clear();
        GroupChoices.Add(new AccountGroupChoiceViewModel
        {
            Id = AccountGroups.AllGroupsId,
            Name = "全部",
            Count = all.Count,
        });
        foreach (var g in _groups.Groups)
        {
            GroupChoices.Add(new AccountGroupChoiceViewModel
            {
                Id = g.Id,
                Name = g.Name,
                Count = AccountGroups.Members(g, all).Count,
            });
        }
        OnPropertyChanged(nameof(HasGroupChoices));
        RefreshActiveGroup();
    }

    /// <summary>高亮重算：**就地改属性**（批量勾选一次循环会触发 N 次 —— 重建集合会 N 次闪 UI）</summary>
    private void RefreshActiveGroup()
    {
        var all = TargetChips.Where(c => c.CanPublish).Select(c => c.SessionId).ToList();
        var on = TargetChips.Where(c => c.IsOn && c.CanPublish).Select(c => c.SessionId).ToList();
        var active = AccountGroups.ActiveGroupId(_groups.Groups, on, all);
        foreach (var ch in GroupChoices) ch.IsActive = ch.Id == active;
    }

    private void RaiseAutoPublishSummary()
    {
        OnPropertyChanged(nameof(AutoPublishSummary));
        OnPropertyChanged(nameof(IsAutoPublishSummaryError));
        OnPropertyChanged(nameof(HasTargetChips));
    }

    /// <summary>进页刷新账号芯片（账号可能刚在平台页绑定/解绑；勾选态来自共享存档）</summary>
    private async Task RefreshTargetChipsAsync()
    {
        try
        {
            await _chips.EnsureLoadedAsync();
            AutoPublish = _chips.AutoPublish;   // 另一页可能刚改过开关
            var chips = await _chips.BuildAsync();
            if (chips is null) return;          // 接口失败：保留现有列表

            TargetChips.Clear();
            foreach (var chip in chips) TargetChips.Add(chip);
            RaiseAutoPublishSummary();
            await RefreshGroupChoicesAsync();   // 组条的成员存活/分母都跟着这次刷新走
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Compose] 刷新自动发布账号失败");
        }
    }

    // ═══ 状态 ═══

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private bool _isStatusError;

    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviewCommand))]
    private bool _isPreviewing;

    [ObservableProperty]
    private string _previewHint = "";

    public bool HasPreviewHint => !string.IsNullOrEmpty(PreviewHint);
    partial void OnPreviewHintChanged(string value) => OnPropertyChanged(nameof(HasPreviewHint));

    public string PreviewButtonText => IsPreviewing ? "停止试听" : "试听";
    partial void OnIsPreviewingChanged(bool value) => OnPropertyChanged(nameof(PreviewButtonText));

    // ═══ 注入（View 提供 StorageProvider；PublishView 同款手法） ═══

    /// <summary>选择素材（图片/视频，可多选）</summary>
    public Func<Task<IReadOnlyList<string>?>>? PickMediaAsync { get; set; }

    public Func<Task<string?>>? PickLocalAudioAsync { get; set; }
    public Func<Task<string?>>? PickBgmAsync { get; set; }
    public Func<Task<string?>>? PickOutputFolderAsync { get; set; }

    // ═══ 偏好加载 / 保存 ═══

    /// <summary>进页加载的在飞任务（角色 / 账号芯片 / 首次偏好）</summary>
    private Task _navigatedTask = Task.CompletedTask;

    /// <summary>
    /// 排干进页加载。给 <c>--smoke</c>：造景假芯片前必须等它落定 ——
    /// 进页是 fire-and-forget，晚到的账号芯片重建会把造景洗掉，
    /// 组条断言就会间歇性炸「合成页点组名没把勾选换成该组」（2026-10-26 打包门禁实测）。
    /// 永不抛。
    /// </summary>
    internal async Task DrainNavigatedAsync()
    {
        try { await _navigatedTask; } catch { /* 进页失败不该把调用方带崩 */ }
    }

    /// <summary>进页加载偏好一次（保留本次会话里未合成的编辑不被切页清掉）</summary>
    public void OnNavigatedTo()
    {
        var roles = RefreshRolesAsync();        // 每次进页刷新角色列表（角色库可能刚在 AI 设置页改过）
        var chips = RefreshTargetChipsAsync();  // 账号芯片同理（账号勾选可能刚在发布页改过 —— 共享存档）
        var settings = Task.CompletedTask;
        if (!_settingsLoaded)
        {
            _settingsLoaded = true;
            settings = LoadSettingsAsync();
        }
        _navigatedTask = Task.WhenAll(roles, chips, settings);
    }

    private async Task LoadSettingsAsync()
    {
        _applyingSettings = true;
        try
        {
            var s = await _store.LoadAsync();
            OutputDirectory = string.IsNullOrWhiteSpace(s.OutputDirectory) ? DefaultDirectory : s.OutputDirectory;
            IsWide = s.IsWide;
            Speed = Math.Clamp(s.Speed, 0.5, 2.0);
            Pitch = Math.Clamp(s.Pitch, 0.5, 2.0);
            BgmVolume = Math.Clamp(s.BgmVolume, 0, 1);

            // 引擎切换会重建音色列表 —— 先定引擎，再按 Id 找回上次选的音色
            SelectedEngine = Engines.FirstOrDefault(e => e.Provider.Name == s.ProviderName) ?? Engines[0];
            if (!string.IsNullOrEmpty(s.VoiceId))
                SelectedVoice = Voices.FirstOrDefault(v => v.ShortName == s.VoiceId) ?? SelectedVoice;

            // 角色选择（按 Id 找回；角色列表可能还没加载完 —— RefreshRolesAsync 完成时会按 _savedRoleId 再兜一次）
            _savedRoleId = s.RoleId;
            if (s.RoleId is { Length: > 0 } rid && SelectedRole is { IsNone: true })
            {
                var match = Roles.FirstOrDefault(x => x.Id == rid);
                if (match is not null) SelectedRole = match;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Compose] 读取合成偏好失败，用默认值");
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
        if (App.SmokeMode) return;   // 冒烟特判会点「开始合成」造景（提交后走到这里），别写用户真实偏好
        try
        {
            await _store.SaveAsync(new ComposeSettings
            {
                ProviderName = SelectedEngine?.Provider.Name,
                VoiceId = SelectedVoice?.ShortName,
                Speed = Speed,
                Pitch = Pitch,
                BgmVolume = BgmVolume,
                OutputDirectory = OutputDirectory,
                IsWide = IsWide,
                RoleId = SelectedRole is { IsNone: false } r ? r.Id : null,
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Compose] 保存合成偏好失败");
        }
    }

    // ═══ 试听 ═══

    /// <summary>试听可用性 —— 只看配音来源（文本合成才可试听）。2026-10-11 起不再看「本页是否
    /// 正在合成别的视频」：本页收敛为纯编辑器（进度在队列页），试听与队列互不相干。</summary>
    private bool CanPreview => !UseLocalAudio;

    /// <summary>试听：用当前引擎/音色/语速合成一小段（文案首句）就地播放；再点一次停止。</summary>
    [RelayCommand(CanExecute = nameof(CanPreview))]
    private async Task PreviewAsync()
    {
        if (IsPreviewing) { StopPreview(); return; }

        // 许可门槛（用户规则：凡是功能页面，都必须登录且激活才能使用）—— 页面开着、会员中途过期时兜住
        if (FeatureGate.BlockReason(_session.IsExpired, _session.Username, App.SmokeMode, "合成功能") is { } block)
        {
            SetError(block);
            return;
        }

        var engine = SelectedEngine ?? Engines[0];
        if (TtsEngines.IsKokoro(engine.Provider) && !KokoroModels.IsReady)
        {
            SetError("Kokoro 本地模型未下载：请到「AI 设置」页下载模型后再试听");
            return;
        }
        var voice = SelectedVoice ?? Voices.FirstOrDefault();
        if (voice is null)
        {
            SetError("没有可用的配音音色：请在「AI 设置」页检查 Kokoro 模型");
            return;
        }

        _previewCts?.Cancel();
        _previewCts = new CancellationTokenSource();
        var ct = _previewCts.Token;
        _player.Stop();
        IsPreviewing = true;
        PreviewHint = "正在合成试听…";
        try
        {
            var result = await engine.Provider.SynthesizeAsync(
                new TtsRequest(SampleText(), voice.ShortName, null, Speed, Pitch),
                msg => Dispatcher.UIThread.Post(() => { if (IsPreviewing) PreviewHint = msg; }),
                ct);
            if (ct.IsCancellationRequested) return;
            _player.Play(result.Audio, OnPreviewFinished);
            PreviewHint = "播放中（再点一次停止）";
            await SaveSettingsAsync();
        }
        catch (OperationCanceledException)
        {
            // 用户再点一次 = 停止；这里无需动作
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested) SetError("试听失败：" + ex.Message);
        }
        finally
        {
            // 播放是异步继续的：播放期间 IsPreviewing 保持 true，播完的回调里收尾
            if (!ct.IsCancellationRequested && !_player.IsPlaying)
            {
                IsPreviewing = false;
                PreviewHint = "";
            }
        }
    }

    /// <summary>试听文本：**最终文案**（套模板后）第一句 —— 试听与成片一致（能听到角色开场白）；
    /// 没有文案时给一句示例</summary>
    private string SampleText()
    {
        var first = FinalText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(first) ? "大家好，欢迎来到我的频道。" : first;
    }

    /// <summary>播完自动收尾（Mp3PreviewPlayer 已把回调切回 UI 线程）</summary>
    private void OnPreviewFinished()
    {
        IsPreviewing = false;
        PreviewHint = "";
    }

    /// <summary>停止试听（合成/清空/再点按钮都走这里）</summary>
    private void StopPreview()
    {
        _previewCts?.Cancel();
        _player.Stop();
        IsPreviewing = false;
        PreviewHint = "";
    }

    // ═══ 素材操作 ═══

    [RelayCommand]
    private async Task PickMedia()
    {
        var files = PickMediaAsync == null ? null : await PickMediaAsync();
        if (files is null) return;
        AddMaterials(files);
    }

    /// <summary>把一批素材加进列表（选择器与冒烟造景共用；按路径去重）</summary>
    public void AddMaterials(IEnumerable<string> paths)
    {
        foreach (var f in paths)
        {
            var kind = IsVideoFile(f) ? MediaKind.Video : MediaKind.Image;
            if (Items.All(x => x.Path != f)) Items.Add(new ComposeMediaItem(f, kind));
        }
    }

    /// <summary>按扩展名判断素材类型（选择器不做过滤兜底，这里兜）</summary>
    private static bool IsVideoFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is
            ".mp4" or ".mov" or ".avi" or ".mkv" or ".wmv" or ".flv" or ".m4v" or ".webm";

    [RelayCommand]
    private void MoveItemUp(ComposeMediaItem item)
    {
        var idx = Items.IndexOf(item);
        if (idx > 0) Items.Move(idx, idx - 1);
    }

    [RelayCommand]
    private void MoveItemDown(ComposeMediaItem item)
    {
        var idx = Items.IndexOf(item);
        if (idx >= 0 && idx < Items.Count - 1) Items.Move(idx, idx + 1);
    }

    [RelayCommand]
    private void RemoveItem(ComposeMediaItem item) => Items.Remove(item);

    [RelayCommand]
    private async Task PickLocalAudio()
    {
        var path = PickLocalAudioAsync == null ? null : await PickLocalAudioAsync();
        if (!string.IsNullOrEmpty(path)) LocalAudioPath = path;
    }

    [RelayCommand]
    private async Task PickBgm()
    {
        var path = PickBgmAsync == null ? null : await PickBgmAsync();
        if (!string.IsNullOrEmpty(path)) BgmPath = path;
    }

    [RelayCommand]
    private void ClearBgm() => BgmPath = null;

    [RelayCommand]
    private async Task PickOutputFolder()
    {
        var path = PickOutputFolderAsync == null ? null : await PickOutputFolderAsync();
        if (string.IsNullOrEmpty(path)) return;
        OutputDirectory = path;
        await SaveSettingsAsync();
    }

    /// <summary>
    /// 清空本次合成参数（输入清单见 <see cref="ClearComposeInputs"/>，另加音色语速音调与状态条复位）。
    /// 保留画幅与输出目录（偏好设置，非本次合成参数）。
    /// </summary>
    [RelayCommand]
    private void ClearAll()
    {
        StopPreview();
        ClearComposeInputs();
        SelectedVoice = Voices.FirstOrDefault();
        Speed = 1.0;
        Pitch = 1.0;
        StatusMessage = "";
        IsStatusError = false;
    }

    /// <summary>本次合成输入的清空清单 —— 「清空参数」按钮与提交后的自动复位共用（两处必须同清单，
    /// 否则「开始合成」吐出来的参数和「清空参数」清的不是一回事）：素材/文案/两个标题/BGM/
    /// 本地音频/配音来源/分镜/定时 + 在途 AI 优化（停掉并清结果，防幽灵结果）。
    /// 不碰偏好（角色/音色/语速/画幅/输出目录）与状态条（提交时写静态确认）。</summary>
    private void ClearComposeInputs()
    {
        StopOptimize();
        Text = "";
        Items.Clear();
        BgmPath = null;
        OverlayTitle = null;
        PublishTitle = "";
        LocalAudioPath = null;
        UseLocalAudio = false;
        UseShots = false;
        IsScheduled = false;
        ScheduledDate = null;
        ScheduledTime = null;
    }

    // ═══ 合成 ═══

    /// <summary>能不能提交 —— 只看输入齐不齐：合成去队列排队（串行），页面随时可备下一条并提交；
    /// 旧行为看「正在合成」把按钮锁到上一条跑完（分钟级），2026-10-11 与配音页对齐后去掉。</summary>
    private bool CanCompose => Items.Count > 0 &&
        (UseLocalAudio ? !string.IsNullOrEmpty(LocalAudioPath) : !string.IsNullOrWhiteSpace(Text));

    /// <summary>
    /// 单条合成 = 入队即跑：把当前输入组装成快照交给合成队列（串行消费者立刻捡起）。
    /// 提交成功后复位本次输入（<see cref="ClearComposeInputs"/>）并只留一行静态确认 ——
    /// 进度 / 结果到「合成队列」页看，本页不再镜像（切页/关页合成照跑，队列是权威）。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCompose))]
    private async Task ComposeAsync()
    {
        // 许可门槛（用户规则：凡是功能页面，都必须登录且激活才能使用）—— 页面开着、会员中途过期时兜住
        if (FeatureGate.BlockReason(_session.IsExpired, _session.Username, App.SmokeMode, "合成功能") is { } block)
        {
            SetError(block);
            return;
        }
        if (!ValidateForCompose(out var error))
        {
            SetError(error);
            return;
        }
        StopPreview();

        // 水印偏好每次提交时读（账号设置页改完回来立即生效）；读失败按默认开，不挡合成
        var watermark = WatermarkSpec.Default;
        try { watermark = (await _watermark.LoadAsync()).ToSpec(); }
        catch (Exception ex) { Log.Warning(ex, "[Compose] 读水印设置失败，按默认开启"); }

        var task = _queue.Enqueue(CompileJob(watermark));
        if (task is null)
        {
            // 换号挂起期间入队被拒（队列属主与当前账号不一致，等切换完成再交）—— 输入保留
            SetError(_queue.StatusNote);
            return;
        }
        var ahead = _queue.PendingAheadOf(task);   // 提交那一刻前面还排着几条（含正在跑的那条）
        ClearComposeInputs();   // 提交成功即复位：任务由队列兜住，页面腾给下一条（配音页提交后列表复位同款）
        StatusMessage = "已提交到「合成队列」" + (ahead > 0 ? $"（前面还有 {ahead} 条）" : "")
            + " —— 本页参数已清空，接着备下一条即可；进度与结果到「合成队列」页看（关掉本页也会继续跑）";
        IsStatusError = false;
        await SaveSettingsAsync();
    }

    /// <summary>把当前页面输入组装成执行快照（入队后与页面状态解耦）</summary>
    private ComposeJob CompileJob(WatermarkSpec watermark)
    {
        var engine = SelectedEngine ?? Engines[0];
        return new ComposeJob
        {
            Materials = [.. Items],
            OutputDirectory = OutputDirectory.Trim(),
            PublishTitle = PublishTitle,
            OverlayTitle = OverlayTitle,
            Text = UseLocalAudio ? null : FinalText,   // 套模板后的最终文案（模板只在本地套这一次）
            RoleName = UseLocalAudio ? null : ActiveRole?.Name,
            UseShots = UseShots && !UseLocalAudio,
            AudioPath = UseLocalAudio ? LocalAudioPath : null,
            ProviderName = engine.Provider.Name,
            VoiceId = SelectedVoice?.ShortName,
            Speed = Speed,
            Pitch = Pitch,
            BgmPath = BgmPath,
            BgmVolume = BgmVolume,
            IsWide = IsWide,
            Watermark = watermark,
            ScheduledAt = ScheduledAtOrNull,
            // 自动发布目标快照（入队即冻结；没开/没勾 = 空 = 不自动发布）
            AutoPublishTargets = AutoPublish
                ? [.. TargetChips.Where(t => t.IsOn && t.CanPublish)
                    .Select(t => new AutoPublishTarget(t.PlatformKey, t.SessionId, t.AccountLabel, t.IsLoggedIn))]
                : [],
        };
    }

    /// <summary>跳「合成队列」—— 页 VM 不认识壳，导航能力由壳注入（与首页卡片同范式）</summary>
    public Action<string>? OnNavigateRequested { get; set; }

    [RelayCommand]
    private void GoQueue() => OnNavigateRequested?.Invoke("composequeue");

    /// <summary>合成前校验（文案/素材/引擎/输出目录/定时），返回 false 时给出人话原因</summary>
    private bool ValidateForCompose(out string error)
    {
        error = "";
        if (Items.Count == 0) { error = "请先添加素材（图片或视频）"; return false; }
        if (UseShots && UseLocalAudio)
        {
            error = "「AI 分镜」需要文本配音：请切回「文本合成配音」，或取消「AI 分镜」";
            return false;
        }
        if (UseLocalAudio)
        {
            if (string.IsNullOrEmpty(LocalAudioPath)) { error = "请选择本地配音文件"; return false; }
        }
        else if (UseShots)
        {
            // 用「最终文案」拆行校验：runner 拆的就是它（角色模板换行会把句数改掉，早拦防运行期越界）
            var lines = FinalText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length != Items.Count)
            {
                var rawLines = Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
                error = $"「AI 分镜」要求文案句数 = 素材数：现在 {lines.Length} 句 / {Items.Count} 个素材（一行一句）"
                    + (ActiveRole is not null && lines.Length != rawLines
                        ? "；角色模板里的换行会被算成新的一句（模板建议写在一行里）"
                        : "");
                return false;
            }
        }
        else if (string.IsNullOrWhiteSpace(Text))
        {
            error = "请输入要配音的文案";
            return false;
        }

        if (!UseLocalAudio && TtsEngines.IsKokoro(SelectedEngine?.Provider) && !KokoroModels.IsReady)
        {
            error = "Kokoro 本地模型未下载：请到「AI 设置」页下载模型后再合成";
            return false;
        }
        if (string.IsNullOrWhiteSpace(OutputDirectory)) { error = "请先选择输出目录"; return false; }
        if (IsScheduled && ScheduledAtOrNull is null)
        {
            error = "「定时发布」需要同时选日期和时间（或取消勾选）";
            return false;
        }
        if (AutoPublish)
        {
            if (TargetChips.All(t => !t.IsOn))
            {
                error = "「合成完成后自动发布」已开启，但没有勾选任何账号 —— 勾一个账号，或关掉这个开关";
                return false;
            }
            // 自动发布时定时必须落在发布页同一窗口内（2 小时~14 天；预检口径单一来源）——
            // 页面就拦下，别等排队跑完才在队列行里失败
            if (ScheduledAtOrNull is { } at
                && PublishPreflight.ScheduleWindowError(at, DateTime.Now) is { } windowError)
            {
                error = $"自动发布的{windowError}";
                return false;
            }
        }
        return true;
    }

    // ═══ 小工具 ═══

    private void SetError(string msg)
    {
        StatusMessage = msg;
        IsStatusError = true;
    }
}

/// <summary>
/// 合成页的通用单选（配音来源 / 画幅）：自绘 ToggleButton + IsSelected 单项态
/// —— 发布页 / AI 设置页同款模式（不给选项自身写命令，避免「命令 + TwoWay 各翻一次互相抵消」的坑）。
/// </summary>
public partial class ComposeChoice(string key, string title, string note) : ObservableObject
{
    public string Key { get; } = key;
    public string Title { get; } = title;
    public string Note { get; } = note;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>配音引擎选项（自带 Provider 引用，选中即取 <c>Provider.Voices</c> / <c>SupportsPitch</c>）</summary>
public partial class ComposeEngineOption(ITtsProvider provider, string note) : ObservableObject
{
    public ITtsProvider Provider { get; } = provider;
    public string Title => Provider.Name;
    public string Note { get; } = note;

    [ObservableProperty]
    private bool _isSelected;
}
