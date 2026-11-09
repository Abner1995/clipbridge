using CreatFlow.Media.Llm;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// AI 配置持久化 —— %LocalAppData%/CreatFlow/llm.json（用户自配 key / 引擎选择，仅存本机明文）。
///
/// 与老版共享同一份文件：老格式（只有 baseUrl/apiKey/model、无 engine）直接可读 ——
/// 按 baseUrl 反推引擎（11434 → Ollama，其余匹配预设）；保存云端/Ollama 时仍写 baseUrl，
/// 双存期老版照常读。本地引擎的 baseUrl 是运行时随机端口，不落盘（写 null）。
/// </summary>
public sealed class LlmConfigStore
{
    private const string FileName = "llm.json";

    private readonly LocalJsonFile _io;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LlmConfigStore(string? root = null)
        => _io = new LocalJsonFile(root ?? LocalJsonFile.DefaultRoot, "LlmConfig");

    /// <summary>配置文件路径（设置页显示用）</summary>
    public string GetConfigPath() => _io.PathOf(FileName);

    /// <summary>读配置；文件缺失/损坏 → 默认配置（本地引擎，零 key 开箱）</summary>
    public async Task<LlmConfig> LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var file = await _io.ReadAsync<LlmConfigFile>(_io.PathOf(FileName), ct);
            return file?.ToConfig() ?? new LlmConfig();
        }
        finally { _gate.Release(); }
    }

    /// <summary>存配置（云端/Ollama 同时写 baseUrl 供老版读取）</summary>
    public async Task SaveAsync(LlmConfig cfg, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { await _io.WriteAsync(_io.PathOf(FileName), LlmConfigFile.FromConfig(cfg), ct); }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// 磁盘字段 → 配置：显式 engine 原样用；**无 engine（老格式）按 baseUrl 反推**。
    /// 纯函数 —— --selftest 直接验反推规则（老版配置兼容是硬约束，不能靠人工复现）。
    /// </summary>
    public static LlmConfig MapFromFile(
        string? engine, string? localTierKey, string? apiKey, string? model, string? baseUrl)
        => new(
            Engine: string.IsNullOrEmpty(engine) ? InferLegacyEngine(baseUrl) : engine,
            LocalTierKey: localTierKey, ApiKey: apiKey, Model: model, BaseUrl: baseUrl);

    /// <summary>老格式反推：baseUrl 匹配**云端**预设 → 该预设；其余（含空 / Ollama 11434 / 未知）→ 本地。
    /// Ollama 不再是新版产品路径（2026-10-10 决策：不再要求用户装 Ollama），老配置静默落本地随包引擎。
    /// ⚠️ 匹配时必须排除 Ollama 预设 —— LlmPresets 里留着它是给老版用的，不排除会把老配置推回一个新版已下线的引擎。</summary>
    private static string InferLegacyEngine(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return LlmEngineResolver.LocalEngine;
        return LlmPresets.All.FirstOrDefault(p =>
            p.Name != "Ollama" &&
            string.Equals(p.BaseUrl, baseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))?.Name
            ?? LlmEngineResolver.LocalEngine;
    }

    /// <summary>磁盘格式（Engine 可空 —— 区分"老格式没写"与"显式选本地"，反推见 MapFromFile）</summary>
    private sealed record LlmConfigFile(
        string? Engine, string? LocalTierKey, string? ApiKey, string? Model, string? BaseUrl)
    {
        public LlmConfig ToConfig() => MapFromFile(Engine, LocalTierKey, ApiKey, Model, BaseUrl);

        public static LlmConfigFile FromConfig(LlmConfig c)
        {
            // 本地引擎端口每次运行都变，不落 baseUrl；云端写预设地址（老版照常可读）
            var baseUrl = c.Engine == LlmEngineResolver.LocalEngine
                ? null
                : LlmPresets.Find(c.Engine)?.BaseUrl;
            return new LlmConfigFile(c.Engine, c.LocalTierKey, c.ApiKey, c.Model, baseUrl);
        }
    }
}
