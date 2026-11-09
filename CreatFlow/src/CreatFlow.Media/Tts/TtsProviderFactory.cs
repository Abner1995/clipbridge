namespace CreatFlow.Media.Tts;

/// <summary>
/// TTS Provider 工厂 — 注册所有可用引擎，按 Name 选择。
/// 新增 Provider（如 Azure）只需在 All 里追加一行，UI 下拉自动出现。
/// </summary>
public static class TtsProviderFactory
{
    /// <summary>
    /// 全部可用引擎（顺序即下拉顺序，第一个为默认）。
    /// 统一包 CachingTtsProvider 装饰：同文案重复合成命中本地缓存，降低 Edge 免费端点调用频率。
    /// </summary>
    public static IReadOnlyList<ITtsProvider> All { get; } = [
        new CachingTtsProvider(new EdgeTtsProvider()),
        new CachingTtsProvider(new KokoroTtsProvider()),
    ];

    /// <summary>默认引擎（第一个）</summary>
    public static ITtsProvider Default => All[0];

    /// <summary>按 Name 查找，未知名称回退默认</summary>
    public static ITtsProvider Create(string? name) =>
        All.FirstOrDefault(p => p.Name == name) ?? Default;
}
