namespace CreatFlow.Media.Llm;

/// <summary>LLM 连接配置（用户自配三件套，OpenAI 兼容协议）</summary>
public sealed record LlmOptions(string BaseUrl, string? ApiKey, string Model);
