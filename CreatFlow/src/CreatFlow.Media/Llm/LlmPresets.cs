namespace CreatFlow.Media.Llm;

/// <summary>LLM 预设 — 选预设自动填充 baseUrl/model，ApiKeyUrl 直达注册申请页</summary>
public sealed record LlmPreset(string Name, string BaseUrl, string Model, string? ApiKeyUrl = null, string? DefaultApiKey = null);

/// <summary>
/// LLM 预设列表：DeepSeek（默认）/ Qwen / Ollama（本地）。
/// Ollama 的 key 为占位（OpenAI 协议要求，Ollama 忽略）。
/// </summary>
public static class LlmPresets
{
    public static readonly IReadOnlyList<LlmPreset> All = new LlmPreset[]
    {
        new("DeepSeek", "https://api.deepseek.com", "deepseek-v4-flash",
            "https://platform.deepseek.com/api_keys"),
        new("Qwen", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-max",
            "https://bailian.console.aliyun.com/?tab=model#/api-key"),
        // ⚠️ 必须 127.0.0.1 而非 localhost：Ollama 只监听 IPv4，.NET 的 localhost 优先解析 ::1 且走系统代理会挂起
        new("Ollama", "http://127.0.0.1:11434/v1", "qwen2.5:7b",
            "https://ollama.com/download", DefaultApiKey: "ollama"),
    };

    public static LlmPreset? Find(string? name) => All.FirstOrDefault(p => p.Name == name);
}
