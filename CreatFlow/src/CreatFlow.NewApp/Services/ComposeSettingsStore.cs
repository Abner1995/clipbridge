namespace CreatFlow.NewApp.Services;

/// <summary>合成页上次使用的偏好（配音 + 输出目录 + 画幅 + BGM 音量）。都是非敏感信息，明文存本机。
/// public：公共 VM（ComposeViewModel）的构造函数要引用存储类型，不能比它更窄。</summary>
public sealed class ComposeSettings
{
    /// <summary>引擎名（TtsProviderFactory.All 里的 Name）</summary>
    public string? ProviderName { get; init; }

    /// <summary>音色标识（Kokoro = 引擎内 sid；老版存量数据里是 Edge 的 ShortName，对不上就回落列表第一个）</summary>
    public string? VoiceId { get; init; }

    /// <summary>合成页选中的角色（ComposeRole.Id；null/空 = 不使用角色）。
    /// ⚠️ 本文件配音页与合成页共用 —— 配音页 Save 必须原样回写（TtsViewModel._keepRoleId），否则会被擦掉</summary>
    public string? RoleId { get; init; }

    public double Speed { get; init; } = 1.0;
    public double Pitch { get; init; } = 1.0;
    public double BgmVolume { get; init; } = 0.25;

    /// <summary>输出目录（合成产物落成发布单元的地方）</summary>
    public string? OutputDirectory { get; init; }

    /// <summary>配音页「保存配音」的默认目录（未设置 = 下载目录）</summary>
    public string? ExportDirectory { get; init; }

    public bool IsWide { get; init; }
}

/// <summary>
/// 创作组偏好持久化（配音页 / 合成页共用一份 voice 偏好）—— %LocalAppData%/CreatFlow/compose_settings.json。
///
/// ⚠️ 两个页面写同一份文件：各自的 Save 要把**没归自己的字段原样回写**（各自在 Load 时记下），
/// 否则后保存的页面会清掉另一页刚改的项（如配音页清掉合成输出目录）。
///
/// 首次读取时**一次性回落到老版配音页的 tts_settings.json**（字段兼容：VoiceShortName / Speed /
/// Pitch / ProviderName / SaveDirectory）——老版用户升级过来时语速等偏好不用重选（引擎/音色名对不上就回落当前引擎）。
/// 只读不写：保存一律写新文件，老文件原样不动（老 App 还在双存期，两个应用别互相踩）。
/// </summary>
public sealed class ComposeSettingsStore
{
    private const string FileName = "compose_settings.json";
    private const string LegacyFileName = "tts_settings.json";

    private readonly string _root;
    private readonly LocalJsonFile _file;

    /// <param name="root">状态根目录（测试注入临时目录；生产传 null = %LocalAppData%\CreatFlow）</param>
    public ComposeSettingsStore(string? root = null)
    {
        _root = root ?? LocalJsonFile.DefaultRoot;
        _file = new LocalJsonFile(_root, "Compose");
    }

    public string GetConfigPath() => _file.PathOf(FileName);

    public async Task<ComposeSettings> LoadAsync(CancellationToken ct = default)
        => await _file.ReadAsync<ComposeSettings>(GetConfigPath(), ct).ConfigureAwait(false)
           ?? await LoadLegacyAsync(ct).ConfigureAwait(false)
           ?? new ComposeSettings();

    public async Task SaveAsync(ComposeSettings settings, CancellationToken ct = default)
        => await _file.WriteAsync(GetConfigPath(), settings, ct).ConfigureAwait(false);

    /// <summary>老版 tts_settings.json 只读兜底。字段名兼容（camelCase 反序列化大小写不敏感）。</summary>
    private async Task<ComposeSettings?> LoadLegacyAsync(CancellationToken ct)
    {
        var legacy = await _file
            .ReadAsync<LegacyTtsSettings>(Path.Combine(_root, LegacyFileName), ct)
            .ConfigureAwait(false);
        if (legacy is null) return null;

        Log.Debug("[Compose] 沿用老版配音偏好（tts_settings.json）：{Provider} / {Voice}",
            legacy.ProviderName, legacy.VoiceShortName);
        return new ComposeSettings
        {
            ProviderName = legacy.ProviderName,
            VoiceId = legacy.VoiceShortName,
            Speed = legacy.Speed,
            Pitch = legacy.Pitch,
            // SaveDirectory 是老版「导出 mp3」目录 → 配音页「保存配音」默认目录（与合成输出目录是两回事）
            ExportDirectory = legacy.SaveDirectory,
        };
    }

    /// <summary>老版 TtsSettings 的读取镜像（只取新版用得上的字段）</summary>
    private sealed class LegacyTtsSettings
    {
        public string? VoiceShortName { get; set; }
        public double Speed { get; set; } = 1.0;
        public double Pitch { get; set; } = 1.0;
        public string? ProviderName { get; set; }
        public string? SaveDirectory { get; set; }
    }
}
