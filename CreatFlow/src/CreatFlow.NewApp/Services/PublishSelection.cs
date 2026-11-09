using CreatFlow.NewApp.Models;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 发布工作台「全选」的纯逻辑（2026-10-22 用户需求：待发布 N 条右侧全选）。
///
/// 抽成纯函数是为了能直测：VM 里剩下的只有重入闸与界面播报，
/// 「全部勾上才算全选」「批量勾选」这两条语义由自检钉住。
/// </summary>
public static class PublishSelection
{
    /// <summary>是否所有条目都勾上 —— 空列表恒 false（空列表的「全选」没有意义，也不该亮）</summary>
    public static bool AllSelected(IReadOnlyCollection<PublishDraft> drafts)
        => drafts.Count > 0 && drafts.All(d => d.IsSelected);

    /// <summary>整批设置勾选（逐条写 IsSelected，行自己的 PropertyChanged 会照常刷新计数）</summary>
    public static void SetAll(IEnumerable<PublishDraft> drafts, bool selected)
    {
        foreach (var draft in drafts) draft.IsSelected = selected;
    }
}
