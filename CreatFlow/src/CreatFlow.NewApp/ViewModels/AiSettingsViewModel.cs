using System.Diagnostics;
using CreatFlow.Media.Llm;
using CreatFlow.Media.Tts;
using CreatFlow.NewApp.Services;
using Material.Icons;

namespace CreatFlow.NewApp.ViewModels;

/// <summary>
/// AI 设置页 —— 引擎选择（本地内置 llama-server【默认、零 key 开箱】/ DeepSeek / Qwen）
/// + 本地模型档位管理（4B 随包、8B 应用内下载）+ Kokoro 本地配音模型管理。
///
/// 配置存本机 %LocalAppData%/CreatFlow/llm.json，不上传服务器（<see cref="LlmConfigStore"/>）。
/// **本地引擎不是让用户装 Ollama**（2026-10-10 决策：Ollama 不再是产品路径，页面刻意不给入口）：
/// 随包 llama-server 子进程由 <see cref="LlamaServerHost"/> 托管（懒启动 + 热态保持），
/// 页面只做「状态展示 + 档位选择 + 下载」，不直接管进程。</summary>
public partial class AiSettingsViewModel : ViewModelBase, INavigationAware
{
    private readonly LlmConfigStore _store;
    private readonly LlamaServerHost _host;
    private readonly ComposeRolesService _roleService;

    private bool _loaded;
    private CancellationTokenSource? _downloadCts;

    public AiSettingsViewModel(LlmConfigStore store, LlamaServerHost host, ComposeRolesService roleService)
    {
        _store = store;
        _host = host;
        _roleService = roleService;
        _localTiers = [.. LlmModelStore.Tiers];
        _selectedTier = LlmModelStore.Default;
        SelectedEngine = Engines[0];   // 默认本地引擎（零 key 开箱）
        RefreshLocalStatus();
        RoleRows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRoleRows));
    }

    public MaterialIconKind HeaderIcon => MaterialIconKind.AutoAwesome;

    // ═══ 引擎选择 ═══

    /// <summary>引擎选项（本地 + 云端预设；Ollama 刻意不在列 —— 2026-10-10 决策：不再要求用户装 Ollama）</summary>
    public IReadOnlyList<EngineOption> Engines { get; } =
    [
        new(LlmEngineResolver.LocalEngine, "本地（推荐）", "内置引擎，无需 API Key，离线可用"),
        .. LlmPresets.All
            .Where(p => p.Name != "Ollama")
            .Select(p => new EngineOption(p.Name, p.Name, p.ApiKeyUrl == null ? "" : "需 API Key（云端）")),
    ];

    [ObservableProperty]
    private EngineOption _selectedEngine = null!;

    public bool IsLocal => SelectedEngine?.Key == LlmEngineResolver.LocalEngine;
    public bool IsCloud => !IsLocal;

    partial void OnSelectedEngineChanged(EngineOption value)
    {
        // 单项态同步（自绘 ToggleButton 的 IsChecked 绑 IsSelected；不给选项自身写命令，
        // 避免"命令 + TwoWay 各翻一次互相抵消"的坑，见 AsyncTask 展开箭头的教训）
        foreach (var e in Engines) e.IsSelected = ReferenceEquals(e, value);
        OnPropertyChanged(nameof(IsLocal));
        OnPropertyChanged(nameof(IsCloud));
        SelectedPreset = IsCloud ? LlmPresets.Find(value.Key) : null;
        NotifyEffectiveSummary();
    }

    /// <summary>点选引擎（分段按钮命令）</summary>
    [RelayCommand]
    private void SelectEngine(EngineOption option) => SelectedEngine = option;

    /// <summary>引擎切换的即时预览（不必等保存）——注意：语言/摘要文案只读 _cfg 的内存镜像</summary>
    private void NotifyEffectiveSummary() => OnPropertyChanged(nameof(EffectiveSummary));

    /// <summary>页面顶部「当前生效」摘要（本地 = 选中的档位；云端 = 预设 + 模型）</summary>
    public string EffectiveSummary
    {
        get
        {
            if (SelectedEngine?.Key == LlmEngineResolver.LocalEngine)
            {
                var tier = SelectedTier ?? LlmModelStore.Default;
                return $"本地 · {tier.DisplayName}" + (LlmModelStore.IsReady(tier) ? "" : "（未下载）");
            }
            return $"{SelectedEngine?.Key} · {(string.IsNullOrWhiteSpace(Model) ? "未选模型" : Model)}"
                + (string.IsNullOrWhiteSpace(ApiKey) ? "（未填 API Key）" : "");
        }
    }

    // ═══ 本地引擎（内置 llama-server + 模型档位） ═══

    /// <summary>推理引擎（llama-server.exe）是否随包就位 —— 缺失只在开发期（没跑 download_llama.ps1）出现</summary>
    public bool IsEngineMissing => !LlamaServerHost.IsEngineReady;

    [ObservableProperty]
    private List<LlmModelTier> _localTiers;

    [ObservableProperty]
    private LlmModelTier? _selectedTier;

    public string TierSizeText => SelectedTier == null ? "" : $"{SelectedTier.SizeGb} GB";

    public string TierStatusText
    {
        get
        {
            if (SelectedTier == null) return "";
            var path = LlmModelStore.ResolvePath(SelectedTier);
            if (path == null) return "未下载";
            return path.StartsWith(LlmModelStore.LocalDir, StringComparison.OrdinalIgnoreCase)
                ? "已下载（用户档）"
                : "已就绪（随包附带）";
        }
    }

    public bool IsTierReady => SelectedTier != null && LlmModelStore.IsReady(SelectedTier);
    public bool CanDownload => !IsDownloading && SelectedTier != null && !IsTierReady;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private int _downloadProgress;

    [ObservableProperty]
    private string _downloadMessage = "";

    partial void OnIsDownloadingChanged(bool value) => NotifyTierChanged();
    partial void OnSelectedTierChanged(LlmModelTier? value)
    {
        NotifyTierChanged();
        NotifyEffectiveSummary();
    }

    private void NotifyTierChanged()
    {
        OnPropertyChanged(nameof(TierSizeText));
        OnPropertyChanged(nameof(TierStatusText));
        OnPropertyChanged(nameof(IsTierReady));
        OnPropertyChanged(nameof(CanDownload));
        DownloadTierCommand.NotifyCanExecuteChanged();
    }

    /// <summary>本地 server 运行状态（跑一次 <see cref="LlamaServerHost"/> 查询刷新）</summary>
    [ObservableProperty]
    private string _localServerStatus = "";

    public bool IsLocalServerRunning => _host.IsRunning;

    /// <summary>刷新本地状态（档位就绪 + server 运行态）——进页/下载完成/启停后调用</summary>
    private void RefreshLocalStatus()
    {
        LocalServerStatus = _host switch
        {
            { IsRunning: true, RunningModelPath: { } p } =>
                $"运行中 · {Path.GetFileNameWithoutExtension(p)}（空闲 {_host.IdleTimeout.TotalMinutes:0} 分钟后自动卸载）",
            _ => "未启动（首次使用 AI 文案时自动加载）",
        };
        OnPropertyChanged(nameof(IsLocalServerRunning));
        OnPropertyChanged(nameof(IsEngineMissing));
        NotifyTierChanged();
    }

    private bool CanDownloadTier() => !IsDownloading && SelectedTier != null && !IsTierReady;

    /// <summary>下载选中档位（断点续传；取消保留 .part，重下续传）</summary>
    [RelayCommand(CanExecute = nameof(CanDownloadTier))]
    private async Task DownloadTierAsync()
    {
        var tier = SelectedTier;
        if (tier == null) return;
        _downloadCts?.Cancel();
        _downloadCts = new CancellationTokenSource();
        var ct = _downloadCts.Token;

        IsDownloading = true;
        DownloadProgress = 0;
        DownloadMessage = $"正在下载 {tier.DisplayName}（{tier.SizeGb} GB，支持断点续传）…";
        try
        {
            await Task.Run(() => LlmModelStore.DownloadAsync(tier, p =>
                Dispatcher.UIThread.Post(() =>
                {
                    DownloadProgress = p;
                    DownloadMessage = p >= 99 ? "正在校验…" : $"正在下载 {tier.DisplayName} {p}%…";
                }), ct), ct);
            DownloadMessage = $"{tier.DisplayName} 下载完成";
            SetSuccess(DownloadMessage);
        }
        catch (OperationCanceledException)
        {
            DownloadMessage = "下载已取消（已下载部分保留，可继续）";
        }
        catch (Exception ex)
        {
            DownloadMessage = "下载失败：" + ex.Message;
            SetError(DownloadMessage);
        }
        finally
        {
            IsDownloading = false;
            RefreshLocalStatus();
        }
    }

    [RelayCommand]
    private void CancelDownloadTier() => _downloadCts?.Cancel();

    /// <summary>预热：立刻把本地模型加载起来（不预热也行，首次生成时自动加载）</summary>
    [RelayCommand]
    private async Task StartLocal()
    {
        if (LlmModelStore.ResolveAnyPath(SelectedTier?.Key) is not { } path)
        {
            SetError("模型未就绪：请先下载或选择已就绪的档位");
            return;
        }
        IsLoading = true;
        StatusMessage = "正在加载本地模型…";
        IsStatusError = false;
        try
        {
            await _host.EnsureRunningAsync(path);
            SetSuccess("本地模型已加载");
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
        finally
        {
            IsLoading = false;
            RefreshLocalStatus();
        }
    }

    /// <summary>卸载本地模型（释放内存；下次使用会自动重新加载）</summary>
    [RelayCommand]
    private async Task StopLocal()
    {
        await _host.StopAsync();
        SetSuccess("本地模型已卸载");
        RefreshLocalStatus();
    }

    // ═══ 云端（DeepSeek / Qwen） ═══

    [ObservableProperty]
    private LlmPreset? _selectedPreset;

    [ObservableProperty]
    private string _apiKey = "";

    /// <summary>模型名（可手输，或从官方 /models 选择）</summary>
    [ObservableProperty]
    private string _model = "";

    partial void OnModelChanged(string value) => NotifyEffectiveSummary();

    /// <summary>官方模型列表（GET {baseUrl}/models，点刷新加载）</summary>
    [ObservableProperty]
    private List<string> _modelOptions = [];

    /// <summary>模型下拉是否有候选（bool 包装：Avalonia 的 IsVisible 不吃 int 绑定）</summary>
    public bool HasModelOptions => ModelOptions.Count > 0;
    partial void OnModelOptionsChanged(List<string> value) => OnPropertyChanged(nameof(HasModelOptions));

    public string ApiKeyUrl => SelectedPreset?.ApiKeyUrl ?? "";
    public bool HasApiKeyUrl => !string.IsNullOrEmpty(ApiKeyUrl);

    [RelayCommand]
    private void OpenApiKeyPage() => OpenUrl(ApiKeyUrl, "打开注册页失败");

    /// <summary>从官方拉模型列表（云端引擎，需已填 Key）</summary>
    [RelayCommand]
    private async Task RefreshModels()
    {
        var preset = SelectedPreset;
        if (preset == null) return;
        if (string.IsNullOrWhiteSpace(ApiKey)) { SetError("请先填 API Key 再获取模型列表"); return; }

        IsLoading = true;
        try
        {
            var options = new LlmOptions(preset.BaseUrl, ApiKey.Trim(), Model.Trim());
            ModelOptions = await OpenAiCompatibleLlmClient.ListModelsAsync(options);
            if (ModelOptions.Count == 0) { SetError("官方未返回模型列表，可手动输入模型名"); return; }
            if (!ModelOptions.Contains(Model)) Model = ModelOptions[0];
            SetSuccess($"获取到 {ModelOptions.Count} 个模型，请下拉选择");
        }
        catch (Exception ex)
        {
            SetError($"获取失败：{ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ═══ Kokoro 本地配音模型（TTS，非 LLM；老版 AI 设置页同款） ═══

    [ObservableProperty]
    private bool _kokoroReady = KokoroModels.IsReady;

    [ObservableProperty]
    private bool _isKokoroDownloading;

    [ObservableProperty]
    private int _kokoroDownloadProgress;

    [ObservableProperty]
    private string _kokoroMessage = "";

    public string KokoroStatusText => KokoroReady
        ? "已就绪（合成页可选「Kokoro 本地」引擎）"
        : IsKokoroDownloading ? "下载中…" : "未下载（~147MB，免费离线配音）";
    partial void OnKokoroReadyChanged(bool value) => OnPropertyChanged(nameof(KokoroStatusText));
    partial void OnIsKokoroDownloadingChanged(bool value) => OnPropertyChanged(nameof(KokoroStatusText));

    /// <summary>下载 Kokoro 模型（断点续传 + 镜像回退 + 自动解压）</summary>
    [RelayCommand]
    private async Task DownloadKokoro()
    {
        IsKokoroDownloading = true;
        KokoroDownloadProgress = 0;
        KokoroMessage = "正在下载模型（约 147MB）…";
        try
        {
            await KokoroModels.DownloadAsync(p =>
            {
                KokoroDownloadProgress = p;
                KokoroMessage = p >= 99 ? "正在解压…" : $"正在下载模型 {p}%…";
            });
            KokoroReady = true;
            KokoroMessage = "模型就绪";
        }
        catch (OperationCanceledException)
        {
            KokoroMessage = "下载已取消（已下载部分保留，可继续）";
        }
        catch (Exception ex)
        {
            KokoroMessage = "下载失败：" + ex.Message;
        }
        finally
        {
            IsKokoroDownloading = false;
        }
    }

    // ═══ 角色库（2026-10-11 批次 4：合成页套模板 / AI 优化的人设来源） ═══

    /// <summary>角色列表（行 VM：展示 + 行内展开编辑；只有「保存」过的行才落盘）</summary>
    public ObservableCollection<ComposeRoleRow> RoleRows { get; } = [];

    public bool HasRoleRows => RoleRows.Count > 0;

    /// <summary>角色卡专属状态条（与上面配置「保存/测试」的状态条分开，互不顶掉）</summary>
    [ObservableProperty] private string _roleStatusMessage = "";
    [ObservableProperty] private bool _isRoleStatusError;
    public bool HasRoleStatus => !string.IsNullOrEmpty(RoleStatusMessage);
    partial void OnRoleStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasRoleStatus));

    public string RoleStorePath => _roleService.GetConfigPath();

    private async Task LoadRolesAsync()
    {
        try
        {
            // 角色库走共享服务（批次 8 上云）：恢复的编辑态不被换会员重载打断由调用方守卫
            await _roleService.EnsureLoadedAsync();
            RoleRows.Clear();
            foreach (var r in _roleService.Roles) RoleRows.Add(ComposeRoleRow.FromRole(r));
        }
        catch (Exception ex)
        {
            SetRoleStatus("读取角色库失败：" + ex.Message, isError: true);
        }
    }

    /// <summary>整体落盘（只含保存过的行 —— 未保存的新草稿不进存档，防空 Id 角色混进去）。
    /// 走服务：写完即置 dirty → App 层静默推云端（批次 8）。</summary>
    private async Task PersistRolesAsync(string successMessage)
    {
        try
        {
            await _roleService.SaveAsync([.. RoleRows.Where(r => !r.IsNew).Select(r => r.ToRole())]);
            SetRoleStatus(successMessage, isError: false);
        }
        catch (Exception ex)
        {
            SetRoleStatus("保存角色失败：" + ex.Message, isError: true);
        }
    }

    private void SetRoleStatus(string msg, bool isError)
    {
        RoleStatusMessage = msg;
        IsRoleStatusError = isError;
    }

    [RelayCommand]
    private void AddRole()
    {
        if (RoleRows.Count >= 200)   // 与服务端 MaxRoles 一致（防云端推送永远 400）
        {
            SetRoleStatus("角色数量已达上限（200）", isError: true);
            return;
        }
        RoleRows.Add(ComposeRoleRow.NewDraft());
        SetRoleStatus("填好名称与内容后点「保存」", isError: false);
    }

    [RelayCommand]
    private void BeginEditRole(ComposeRoleRow row) => row.BeginEdit();

    [RelayCommand]
    private void CancelEditRole(ComposeRoleRow row)
    {
        if (row.IsNew) RoleRows.Remove(row);   // 新建取消 = 整行移除（草稿从未落盘）
        else row.CancelEdit();
    }

    [RelayCommand]
    private async Task SaveRole(ComposeRoleRow row)
    {
        if (!row.TryCommit()) return;   // 行内 Error 已提示
        await PersistRolesAsync($"已保存角色「{row.ToRole().Name}」");
    }

    [RelayCommand]
    private void BeginDeleteRole(ComposeRoleRow row)
    {
        // 同一时刻只开一条确认条（项目惯例：原地确认、不弹窗）
        foreach (var r in RoleRows) r.IsConfirmingDelete = ReferenceEquals(r, row);
    }

    [RelayCommand]
    private void CancelDeleteRole(ComposeRoleRow row) => row.IsConfirmingDelete = false;

    [RelayCommand]
    private async Task ConfirmDeleteRole(ComposeRoleRow row)
    {
        var name = row.ToRole().Name;
        RoleRows.Remove(row);
        await PersistRolesAsync($"已删除角色「{name}」");
    }

    // ═══ 保存 / 测试生成 / 状态 ═══

    /// <summary>剪贴板写入（壳注入）</summary>
    public Func<string, Task>? CopyToClipboardAsync { get; set; }

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private bool _isStatusError;

    [ObservableProperty]
    private bool _isLoading;

    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);
    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    /// <summary>进页加载配置一次（保留用户未保存的编辑不被切页清掉）；
    /// 角色库每次进页拉云端 + 从服务内存重建（编辑/删除确认态时不动，防打断手头操作）。</summary>
    public void OnNavigatedTo()
    {
        RefreshLocalStatus();
        _ = EnterAsync();
    }

    private async Task EnterAsync()
    {
        if (!_loaded)
        {
            _loaded = true;
            await LoadAsync();
        }

        // 角色库云端拉取（批次 8；冒烟被闸秒返回、零网络）—— 多设备差异进页收敛
        await ComposeRoleCloudSync.PullAsync(_roleService);

        // 手头有编辑或删除确认 → 不重建行（重建会把未保存输入/确认态吞掉）
        if (RoleRows.Any(r => r.IsEditing || r.IsConfirmingDelete)) return;
        await LoadRolesAsync();
    }

    private async Task LoadAsync()
    {
        var cfg = await _store.LoadAsync();
        // 老配置里的 Ollama 引擎已由存储层反推成本地（Ollama 不在新版引擎列）——
        // FirstOrDefault 未命中时回落 Engines[0]=本地，双保险
        SelectedEngine = Engines.FirstOrDefault(e => e.Key == cfg.Engine) ?? Engines[0];
        SelectedTier = LlmModelStore.Find(cfg.LocalTierKey) ?? LlmModelStore.Default;
        ApiKey = cfg.ApiKey ?? "";
        Model = cfg.Model ?? SelectedPreset?.Model ?? "";
        NotifyEffectiveSummary();
        await LoadRolesAsync();
    }

    /// <summary>保存配置（引擎 / 档位 / Key / 模型一并落盘）</summary>
    [RelayCommand]
    private async Task SaveConfig()
    {
        if (SelectedEngine == null) { SetError("请选择引擎"); return; }
        if (IsCloud && string.IsNullOrWhiteSpace(ApiKey)) { SetError("请输入 API Key（或改用本地引擎）"); return; }
        if (!IsLocal && string.IsNullOrWhiteSpace(Model)) { SetError("请输入模型名"); return; }

        IsLoading = true;
        try
        {
            await _store.SaveAsync(new LlmConfig(
                Engine: SelectedEngine.Key,
                LocalTierKey: IsLocal ? SelectedTier?.Key : null,
                ApiKey: IsLocal ? null : ApiKey.Trim(),
                Model: IsLocal ? null : Model.Trim()));
            SetSuccess($"已保存到本机（{_store.GetConfigPath()}）");
            NotifyEffectiveSummary();
        }
        catch (Exception ex)
        {
            SetError($"保存失败: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>测试生成：用当前选中的引擎发一句简单请求（本地引擎会顺带完成模型加载）。</summary>
    [RelayCommand]
    private async Task TestGenerate()
    {
        if (SelectedEngine == null) { SetError("请选择引擎"); return; }

        IsLoading = true;
        StatusMessage = IsLocal ? "正在加载本地模型并测试（首次较慢）…" : "正在测试…";
        IsStatusError = false;
        try
        {
            // 引擎选择生效走内存态（不强制先保存）：本地用选中档位，云端用输入框现值
            var cfg = new LlmConfig(
                Engine: SelectedEngine.Key,
                LocalTierKey: IsLocal ? SelectedTier?.Key : null,
                ApiKey: IsCloud ? ApiKey.Trim() : null,
                Model: IsLocal ? null : Model.Trim());
            var options = await LlmEngineResolver.ResolveAsync(cfg, _host);
            var sw = Stopwatch.StartNew();
            // maxTokens 256：推理模型的 reasoning 会消耗 token，10 会被吃光导致 content 空
            var reply = await OpenAiCompatibleLlmClient.GenerateAsync(
                options, "请回复：正常", temperature: 0.2, maxTokens: 256);
            sw.Stop();
            SetSuccess($"测试成功（{sw.Elapsed.TotalSeconds:0.#}s）：{reply.Trim()}");
        }
        catch (Exception ex)
        {
            SetError($"测试失败：{ex.Message}");
        }
        finally
        {
            IsLoading = false;
            RefreshLocalStatus();
        }
    }

    private void OpenUrl(string url, string errorPrefix)
    {
        if (string.IsNullOrEmpty(url)) return;
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch (Exception ex) { SetError(errorPrefix + "：" + ex.Message); }
    }

    private void SetSuccess(string msg) { StatusMessage = msg; IsStatusError = false; }
    private void SetError(string msg) { StatusMessage = msg; IsStatusError = true; }
}

/// <summary>引擎选项（分段单选按钮绑定：自绘 ToggleButton + IsSelected 单项态，发布页声明组同款）</summary>
public partial class EngineOption(string key, string title, string note) : ObservableObject
{
    public string Key { get; } = key;
    public string Title { get; } = title;
    public string Note { get; } = note;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>角色库卡片的一行（展示 + 行内展开编辑草稿）——「保存」才把草稿转正并整体落盘，取消回滚；
/// 新建行取消 = 整行移除（从未落盘）。</summary>
public partial class ComposeRoleRow : ObservableObject
{
    private ComposeRole _saved = new();

    /// <summary>角色 Id（内置固定 / 用户新建在「保存」时生成；新草稿保存前为空）</summary>
    public string Id => _saved.Id;

    /// <summary>新建草稿行（未「保存」前不进存档）</summary>
    public bool IsNew { get; private set; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _template = "";
    [ObservableProperty] private string _persona = "";
    [ObservableProperty] private string _knowledge = "";
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private bool _isConfirmingDelete;
    [ObservableProperty] private string _error = "";

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(SummaryText));
    partial void OnTemplateChanged(string value) => OnPropertyChanged(nameof(SummaryText));
    partial void OnPersonaChanged(string value) => OnPropertyChanged(nameof(SummaryText));
    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>折叠态摘要（模板概况 + 能不能 AI 优化）</summary>
    public string SummaryText
    {
        get
        {
            var tpl = string.IsNullOrWhiteSpace(Template) ? "未设置台词模板（直接用文案）" : Template.Trim();
            var ability = string.IsNullOrWhiteSpace(Persona) ? "未设人设 · 不能 AI 优化" : "可 AI 优化";
            return $"{tpl} · {ability}";
        }
    }

    public static ComposeRoleRow FromRole(ComposeRole role)
    {
        var row = new ComposeRoleRow
        {
            Name = role.Name,
            Template = role.Template,
            Persona = role.Persona,
            Knowledge = role.Knowledge,
        };
        row._saved = role;
        return row;
    }

    public static ComposeRoleRow NewDraft()
    {
        var row = new ComposeRoleRow();
        row.IsNew = true;
        row.IsEditing = true;
        return row;
    }

    public void BeginEdit()
    {
        Name = _saved.Name;
        Template = _saved.Template;
        Persona = _saved.Persona;
        Knowledge = _saved.Knowledge;
        Error = "";
        IsEditing = true;
    }

    public void CancelEdit()
    {
        Name = _saved.Name;
        Template = _saved.Template;
        Persona = _saved.Persona;
        Knowledge = _saved.Knowledge;
        Error = "";
        IsEditing = false;
    }

    /// <summary>校验（名称必填 + 服务端同款上限）并转正；false = 校验不过（行内 Error 已提示）。
    /// 上限与 ComposeRoleSetService 一致：本地先拦，防"本地保存成功但云端推送永远 400 静默失败"。</summary>
    public bool TryCommit()
    {
        var name = Name.Trim();
        if (name.Length == 0)
        {
            Error = "请填角色名称";
            return false;
        }
        if (name.Length > 100)
        {
            Error = "角色名称过长（≤100 字）";
            return false;
        }
        if (Template.Trim().Length > 20_000)
        {
            Error = "台词模板过长（≤2 万字）";
            return false;
        }
        if (Persona.Trim().Length > 50_000)
        {
            Error = "人设提示词过长（≤5 万字）";
            return false;
        }
        if (Knowledge.Trim().Length > 200_000)
        {
            Error = "知识库过长（≤20 万字）";
            return false;
        }
        _saved = new ComposeRole
        {
            Id = IsNew || _saved.Id.Length == 0 ? Guid.NewGuid().ToString("N") : _saved.Id,
            Name = name,
            Template = Template.Trim(),
            Persona = Persona.Trim(),
            Knowledge = Knowledge.Trim(),
        };
        Name = _saved.Name;
        Template = _saved.Template;
        Persona = _saved.Persona;
        Knowledge = _saved.Knowledge;
        Error = "";
        IsEditing = false;
        IsNew = false;
        return true;
    }

    public ComposeRole ToRole() => _saved;
}
