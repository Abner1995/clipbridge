namespace CreatFlow.Media.Llm;

/// <summary>
/// 用户 AI 配置（本机 llm.json，不上传服务器）。
/// <para><c>Engine</c>：<c>"local"</c>（内置 llama-server，**默认、零 key 开箱**）
/// ｜云端预设名（<c>"DeepSeek"</c> / <c>"Qwen"</c>）。</para>
/// <para>旧格式（只有 baseUrl/apiKey/model，无 engine）由存储层按 baseUrl 匹配预设反推，其余落本地。
/// 老版连 Ollama 的配置（11434）一并落本地 —— Ollama 不再是新版的产品路径（2026-10-10 决策）。</para>
/// </summary>
public sealed record LlmConfig(
    string Engine = LlmEngineResolver.LocalEngine,
    string? LocalTierKey = null,   // 本地引擎档位（LlmModelStore.Tiers 的 Key；null = 默认档）
    string? ApiKey = null,
    string? Model = null,          // 云端模型名
    string? BaseUrl = null);       // 预留（云端留空用预设）

/// <summary>引擎名常量与「配置 → LlmOptions」解析（本地引擎会确保 server 已就绪）。</summary>
public static class LlmEngineResolver
{
    public const string LocalEngine = "local";

    /// <summary>
    /// 解析成可直接喂 <see cref="OpenAiCompatibleLlmClient"/> 的 <see cref="LlmOptions"/>。
    /// cfg 为 null（首次运行/未配置）→ 走本地引擎默认档（零 key 开箱）。
    /// </summary>
    /// <exception cref="InvalidOperationException">本地引擎/模型未就绪 —— 文案可直接给用户看（引导去 AI 设置页）。</exception>
    public static async Task<LlmOptions> ResolveAsync(LlmConfig? cfg, LlamaServerHost host, CancellationToken ct = default)
    {
        cfg ??= new LlmConfig();

        if (cfg.Engine == LocalEngine)
        {
            var path = LlmModelStore.ResolveAnyPath(cfg.LocalTierKey)
                ?? throw new InvalidOperationException(
                    "本地模型未就绪：请到「AI 设置」页下载模型（或运行 scripts/download_llama.ps1 获取随包模型）");
            var baseUrl = await host.EnsureRunningAsync(path, ct).ConfigureAwait(false);
            return new LlmOptions(baseUrl, "local", Path.GetFileNameWithoutExtension(path));
        }

        var cloud = LlmPresets.Find(cfg.Engine)
            ?? throw new InvalidOperationException($"未知的 AI 引擎：{cfg.Engine}（请到「AI 设置」页重新选择）");
        return new LlmOptions(cloud.BaseUrl,
            Require(cfg.ApiKey, $"请到「AI 设置」页填写 {cloud.Name} 的 API Key"),
            string.IsNullOrWhiteSpace(cfg.Model) ? cloud.Model : cfg.Model);
    }

    private static string Require(string? value, string message) =>
        string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException(message) : value;
}
