namespace CreatFlow.NewApp.ViewModels;

/// <summary>
/// 定时刷新用的**集合对齐器**：把 <paramref name="target"/> 整成 <paramref name="ordered"/> 的形状，
/// **复用已有实例**，只做必要的 Remove / Insert / Move。
///
/// 为什么不能 `Clear()` + 重加（两页都踩过，2026-09-21~22）：
/// 换掉行对象 = 用户的勾选态、展开态、已加载的明细、滚动位置一起丢；
/// 而且 ItemsControl 会重建全部容器 —— 表现就是「页面一直在刷，什么都点不开」。
///
/// 用法：先把服务端数据「就地更新」进行对象（行 VM 上的 Apply），再按目标顺序调这里。
/// 只有引用相等的对象才算同一行，所以行 VM 必须有稳定身份（Id / RowKey）。
/// </summary>
public static class CollectionSync
{
    public static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> ordered)
    {
        // 先删多的（倒着删，避免边遍历边移位）
        var keep = new HashSet<T>(ordered);
        for (var i = target.Count - 1; i >= 0; i--)
            if (!keep.Contains(target[i])) target.RemoveAt(i);

        // 再按目标顺序补/挪：已经在别处的 Move 过来（Move 不换实例）
        for (var i = 0; i < ordered.Count; i++)
        {
            var want = ordered[i];
            var at = target.IndexOf(want);
            if (at < 0) target.Insert(i, want);
            else if (at != i) target.Move(at, i);
        }
    }
}
