namespace CreatFlow.Media.Llm;

/// <summary>
/// LLM 结构化输出解析 — 三连宽松解析（直接 JSON → markdown 代码块 → 找大括号）。
/// 兼容小模型（Ollama qwen2.5 等）输出不稳定的情况。
/// </summary>
public static class LlmJson
{
    /// <summary>从 LLM 回复中解析出 JSON 根元素（调用方用完即弃，Clone 已隔离 JsonDocument 生命周期）。</summary>
    public static JsonElement Parse(string text)
    {
        // 1. 直接解析
        try { using var d = JsonDocument.Parse(text); return d.RootElement.Clone(); } catch { }

        // 2. 从 ```json ... ``` 代码块提取
        var block = Regex.Match(text, @"```(?:json)?\s*([\s\S]+?)\s*```");
        if (block.Success)
        {
            try { using var d2 = JsonDocument.Parse(block.Groups[1].Value); return d2.RootElement.Clone(); } catch { }
        }

        // 3. 兜底：取首尾大括号之间的内容
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            try { using var d3 = JsonDocument.Parse(text[start..(end + 1)]); return d3.RootElement.Clone(); } catch { }
        }

        throw new FormatException("LLM 响应无法解析为 JSON: " + Truncate(text, 200));
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
