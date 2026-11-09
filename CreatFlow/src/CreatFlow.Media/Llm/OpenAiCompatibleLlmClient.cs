namespace CreatFlow.Media.Llm;

/// <summary>
/// OpenAI 兼容 LLM 客户端 — 一个实现通吃云端（DeepSeek/Qwen）+ 本地 Ollama
/// （localhost:11434/v1，key 填占位即可，Ollama 忽略）。零 NuGet 依赖，纯 HttpClient。
/// </summary>
public static class OpenAiCompatibleLlmClient
{
    /// <summary>
    /// 普通文本生成，返回模型回复内容。
    /// 默认关闭思考模式（thinking disabled）：模板化文案不需要深度推理，
    /// 开思考（DeepSeek V4 默认 high）会慢数倍且思考 token 按输出价计费。
    /// </summary>
    public static async Task<string> GenerateAsync(
        LlmOptions options, string prompt,
        double temperature = 0.7, int maxTokens = 2000,
        string? reasoningEffort = null, bool thinking = false,
        CancellationToken ct = default)
    {
        var url = options.BaseUrl.TrimEnd('/') + "/chat/completions";
        var bodyObj = new Dictionary<string, object?>
        {
            ["model"] = options.Model,
            ["messages"] = new[] { new { role = "user", content = prompt } },
            ["temperature"] = temperature,
            ["max_tokens"] = maxTokens,
            ["stream"] = false,
        };
        if (!string.IsNullOrEmpty(reasoningEffort)) bodyObj["reasoning_effort"] = reasoningEffort;
        if (!thinking) bodyObj["thinking"] = new { type = "disabled" }; // DeepSeek V4 系列：关思考 = 快 + 省 token（其余厂商忽略此字段）
        var body = JsonSerializer.Serialize(bodyObj);

        // 禁用系统代理：预设服务均为国内直连/本地（DeepSeek/Qwen/Ollama），系统代理会挂起 localhost 与直连请求
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromMinutes(3) };
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + (options.ApiKey ?? "ollama"));
        Log.Debug("[Llm] POST {Url} model={Model}", url, options.Model);

        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new IOException($"LLM 调用失败 HTTP {(int)resp.StatusCode}: {Truncate(text, 300)}");

        using var doc = JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            throw new IOException("LLM 响应缺少 choices: " + Truncate(text, 300));
        var content = choices[0].TryGetProperty("message", out var msg) &&
                      msg.TryGetProperty("content", out var c) ? c.GetString() : null;
        if (string.IsNullOrWhiteSpace(content))
        {
            // 推理模型（如 DeepSeek V4）会把 max_tokens 消耗在 reasoning 上，content 可能为空
            var reason = choices[0].TryGetProperty("finish_reason", out var fr) ? fr.GetString() : null;
            var hint = reason == "length"
                ? "（token 上限被推理过程耗尽，请换非推理模型或增大 max_tokens）"
                : "";
            throw new IOException($"LLM 响应 content 为空{hint}: " + Truncate(text, 300));
        }
        return content;
    }

    /// <summary>
    /// 获取官方可用模型列表（OpenAI 兼容 GET /models）。返回模型名列表，
    /// 供设置页下拉选择（DeepSeek/Qwen/Ollama 均支持）。
    /// </summary>
    public static async Task<List<string>> ListModelsAsync(LlmOptions options, CancellationToken ct = default)
    {
        var url = options.BaseUrl.TrimEnd('/') + "/models";
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + (options.ApiKey ?? "ollama"));
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new IOException($"获取模型列表失败 HTTP {(int)resp.StatusCode}: {Truncate(text, 200)}");
        using var doc = JsonDocument.Parse(text);
        var models = new List<string>();
        if (doc.RootElement.TryGetProperty("data", out var data))
            foreach (var m in data.EnumerateArray())
            {
                // OpenAI 兼容：data[].id；个别厂商用 name
                var id = m.TryGetProperty("id", out var idv) ? idv.GetString() : null;
                if (string.IsNullOrEmpty(id) && m.TryGetProperty("name", out var nv)) id = nv.GetString();
                if (!string.IsNullOrEmpty(id)) models.Add(id!);
            }
        return models;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
