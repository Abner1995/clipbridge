using CreatFlow.Media.Tts;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 配音引擎清单（配音页 / 合成页 / 合成队列共用）：可用引擎 = 共享工厂除去已下架的 Edge 免费；
/// 引擎名匹配与提示文案也收在这里，几处调用不各写一份。
/// </summary>
public static class TtsEngines
{
    public const string KokoroName = "Kokoro 本地";

    /// <summary>已下架引擎：Edge 免费 —— 微软端点不稳定，2026-10-10 产品决策不再提供。
    /// 只在新版隐藏：共享工厂不动，老版 App 仍在双存期、从同一个 All 取引擎，两个应用互不踩。</summary>
    public const string RetiredEdgeName = "Edge 免费";

    /// <summary>可用引擎（顺序即界面顺序；工厂统一包了缓存装饰器，只能按显示名判断）</summary>
    public static IReadOnlyList<ITtsProvider> Available { get; } =
        [.. TtsProviderFactory.All.Where(p => p.Name != RetiredEdgeName)];

    /// <summary>引擎是不是 Kokoro 本地</summary>
    public static bool IsKokoro(ITtsProvider? provider) => provider?.Name == KokoroName;

    /// <summary>引擎选项的提示文案</summary>
    public static string NoteFor(string providerName) =>
        providerName == KokoroName ? "免费离线 · 需先在 AI 设置页下载模型" : "";
}
