namespace CreatFlow.NewApp.Services;

/// <summary>
/// 合集平台侧删除的**归一化结果**（平台 SDK 那一侧的返回形状）。
///
/// 只有三档，不是漏了「已不存在」—— 各平台的合集删除接口返回形状不一（多数只回 bool，
/// 头条回 `DeleteCollectionResult`，见 <see cref="CollectionSyncService"/> 里的 Delete*Async），
/// 分不出 404 还是 401 还是限流；要区分得先解析各平台错误码，
/// 那是另一期的事（与视频页文案里「可能已不存在」同源的精度限制）。
/// </summary>
public enum CollectionDeleteOutcome
{
    /// <summary>平台侧已删除</summary>
    Deleted,

    /// <summary>
    /// 这条记录指向的会话已经不在服务端（解绑 / 重登换了 session id）——
    /// 平台侧**无从下手**，且永远下不了手，所以归到「视为成功」而不是「失败」。
    /// 归成失败的话这条合集记录永远删不掉（8 个账号里 1 个解绑 = 僵尸合集）。
    ///
    /// ⚠️ 客户端只是**报**这个事实（见 <see cref="AsyncTaskFormat.StatusFor(CollectionDeleteOutcome)"/>），
    /// 「算不算已清」的判定在服务端（`AsyncTaskService` + `AsyncTaskItemStatuses`）。
    /// </summary>
    UnboundCleaned,

    /// <summary>平台拒绝 / 网络异常 —— 记录保留，可重试</summary>
    Failed,
}

/// <summary>
/// 合集删除的**纯函数**（`--selftest` 直接喂数据断言）。
///
/// 硬约束同 <see cref="VideoArchiveFormat"/>：不得引用任何 Avalonia 类型
/// （自检跑在 <c>BuildAvaloniaApp()</c> 之前）。
///
/// 这里**只剩确认条文案**了：P2 把「全部账号删光才删本地记录」的判定搬到了服务端
/// （客户端可能根本不在这里，而且「最后一条是谁报的」也不确定，只有服务端判得了）。
/// 文案留在客户端是因为它要在用户点删除**之前**就显示出来，等不了服务端。
/// </summary>
public static class CollectionDeleteFormat
{
    /// <summary>
    /// 删除确认条文案。**必须写清「什么情况下不删本地记录」** ——
    /// 这里以前写的是「平台侧删除失败时仍会删除本地记录」，2026-09-21 语义反转，
    /// 现在是「全部成功后才删除本地记录」。
    /// </summary>
    public static string StripText(int accountCount) => accountCount <= 0
        ? "将从本地删除这条合集记录（平台侧没有已同步的账号，无需在平台上删除）。"
        : $"将从 {accountCount} 个账号对应的平台删除这条合集，全部成功后才删除本地记录；"
          + "有账号删不掉时记录会保留，可重试。已加入合集的作品不受影响。";
}
