namespace CreatFlow.Application.Common.Interfaces;

/// <summary>
/// Repository for async task items — the per (视频/合集 × 平台 × 账号) units.
/// </summary>
public interface IAsyncTaskItemRepository : IRepository<AsyncTaskItem, AsyncTaskItemId>
{
    Task<List<AsyncTaskItem>> GetByTaskAsync(AsyncTaskId taskId, CancellationToken ct = default);

    /// <summary>
    /// 取**本任务内**同组键的子项（服务端判「这个合集是不是全清完了」用）。
    ///
    /// ⚠️ 必须带 <paramref name="taskId"/>：同一个合集可能先后被两次提交碰过
    /// （第一次部分失败 → 用户再删一次），只按 group_key 查会把上一次那批的
    /// 陈旧失败也算进来 —— 于是永远判不出「全清完」，合集永远删不掉。
    /// </summary>
    Task<List<AsyncTaskItem>> GetByTaskAndGroupAsync(
        AsyncTaskId taskId, Guid groupKey, CancellationToken ct = default);

    /// <summary>
    /// 某会员的某条任务的全部子项（归属校验走父任务）。
    /// </summary>
    Task<List<AsyncTaskItem>> GetByTaskForMemberAsync(
        AsyncTaskId taskId, Guid memberId, CancellationToken ct = default);

    /// <summary>
    /// 该会员所有**待执行**的子项（Pending）—— 桌面端消费者轮询认领用。
    /// 只回 Pending：Running 表示别的端/别的轮次已经拿走了，再认领一次会重复执行。
    /// </summary>
    Task<List<AsyncTaskItem>> GetPendingByMemberAsync(Guid memberId, CancellationToken ct = default);

    /// <summary>
    /// 该会员所有**在飞的视频目标 ID**（`x_publish_platforms.id`，子项处于 Pending）。
    /// 合集目标走 <see cref="GetInFlightCollectionGroupKeysAsync"/>，不要混进来。
    ///
    /// 视频页靠它决定「哪些行该藏起来」：行是乐观摘掉的，
    /// 可服务端那边还没删完 —— 不过滤的话翻页/切走再回来就会看见刚删掉的行复活。
    /// **只回在飞的**：终态失败的必须放回来让用户看见（不然就是老版 V2 那个「静默少删」）。
    /// </summary>
    Task<List<Guid>> GetInFlightTargetIdsAsync(Guid memberId, CancellationToken ct = default);

    /// <summary>
    /// 在飞的**合集 ID**（同组键）。
    ///
    /// 合集页要的不是「哪条账号记录在删」，而是「哪个合集在删」——
    /// 一张卡 = N 条账号记录，藏卡片按合集粒度，藏不掉也藏不对。
    /// </summary>
    Task<List<Guid>> GetInFlightCollectionGroupKeysAsync(
        Guid memberId, CancellationToken ct = default);

    /// <summary>
    /// **已停稳但还有失败子项**的合集组（group_key → 失败条数）。
    ///
    /// 与「在飞」相对：在飞的是 Pending（卡还在藏）；这里的是终态失败 ——
    /// 卡已经放回列表，但因「全组清完才删本地记录」被卡在本地。合集页据此挂
    /// 「删除未完成」警示条（用户要能看到「这张卡为什么还在、怎么处理」）。
    /// </summary>
    Task<List<(Guid GroupKey, int FailedCount)>> GetFailedCollectionDeleteGroupsAsync(
        Guid memberId, CancellationToken ct = default);

    /// <summary>删掉整条任务的全部子项（删任务时应用层级联 —— 无外键，只能手写）</summary>
    Task DeleteByTaskAsync(AsyncTaskId taskId, CancellationToken ct = default);

    /// <summary>批量删多条任务的子项（「清空已完成」用 —— 逐条删是 N 次往返）</summary>
    Task DeleteByTasksAsync(IReadOnlyList<AsyncTaskId> taskIds, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}
