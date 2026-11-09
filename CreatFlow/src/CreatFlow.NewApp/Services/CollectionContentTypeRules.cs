namespace CreatFlow.NewApp.Services;

/// <summary>
/// 合集内容类型规则（纯函数，自检用）。
///
/// 2026-11-05：头条把合集按内容形态拆成「视频合集 / 文章合集」两套，文章合集
/// 在其余五个平台没有对应物 —— 所以文章合集只能勾头条，视频合集可以跨平台。
/// 规则收口在这里，UI 约束与服务端兜底共用同一口径。
/// </summary>
public static class CollectionContentTypeRules
{
    /// <summary>0=视频 1=文章</summary>
    public static bool IsArticle(int contentType) =>
        contentType == (int)CollectionContentType.Article;

    /// <summary>该类型下该平台是否可选（文章 → 仅头条）</summary>
    public static bool CanSelectPlatform(int contentType, string platformKey) =>
        !IsArticle(contentType) || platformKey == PlatformNames.Toutiao;

    /// <summary>校验目标平台组合；合法返回 null，否则返回给用户看的文案。</summary>
    public static string? ValidateTargets(int contentType, IEnumerable<string> platformKeys)
    {
        if (!IsArticle(contentType)) return null;
        return platformKeys.Distinct().Any(k => k != PlatformNames.Toutiao)
            ? "文章合集仅支持今日头条，不能同时选择其他平台"
            : null;
    }

    /// <summary>类型给人看的标签（编辑器只读展示用）</summary>
    public static string Label(int contentType) =>
        IsArticle(contentType) ? "文章合集（仅头条）" : "视频合集";
}
