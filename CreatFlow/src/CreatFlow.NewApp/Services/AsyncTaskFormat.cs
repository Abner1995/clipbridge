namespace CreatFlow.NewApp.Services;

/// <summary>
/// 子项状态的**客户端镜像**。
///
/// ⚠️ **数字是 wire 协议**，必须与服务端 <c>CreatFlow.Domain.Entities.AsyncTaskItemStatus</c>
/// 逐值对齐 —— 改数字等于改协议，服务端会静默按错的状态落库。
/// <see cref="AsyncTaskSelfTest"/> 里有把数字钉死的断言。
///
/// 为什么客户端要再定义一份而不是共用：NewApp 只引用 Shared/Platforms/Media，
/// 不引用 Domain（与其它 wire DTO 同款）。
/// </summary>
public enum TaskItemStatus
{
    Pending = 0,
    Deleted = 1,
    AlreadyGone = 2,
    UnboundCleaned = 3,
    NeedLogin = 4,
    PlatformFailed = 5,
    RecordCleanupFailed = 6,
}

/// <summary>
/// 异步任务的**客户端纯函数**（`--selftest` 直接喂数据断言）。
///
/// 两条硬约束同 <see cref="VideoArchiveFormat"/>：不引用 Avalonia 类型、时间由调用方注入。
/// </summary>
public static class AsyncTaskFormat
{
    // ═══ 目标类型（wire 数字，对齐服务端 AsyncTaskTargetKind）═══

    public const int TargetPublishPlatform = 1;
    public const int TargetCollectionPlatform = 2;

    // ═══ 任务类型（wire 数字，对齐服务端 AsyncTaskKind）═══

    public const int KindVideoDelete = 1;
    public const int KindCollectionDelete = 2;

    // ═══ 平台删除结果 → 回报的服务端状态 ═══

    /// <summary>
    /// 视频页的平台删除结果 → 回给服务端的状态。
    ///
    /// 与客户端 <see cref="DeleteOutcome"/> 是同一套判断 —— 两边必须一致，
    /// 否则「页面上说删掉了、任务页说失败了」这种自相矛盾就会出现。
    /// </summary>
    public static TaskItemStatus StatusFor(PlatformDeleteResult result) => result switch
    {
        PlatformDeleteResult.Success => TaskItemStatus.Deleted,
        PlatformDeleteResult.NotFound => TaskItemStatus.AlreadyGone,
        PlatformDeleteResult.SessionGone => TaskItemStatus.UnboundCleaned,
        PlatformDeleteResult.Unauthorized => TaskItemStatus.NeedLogin,
        _ => TaskItemStatus.PlatformFailed,
    };

    /// <summary>合集页的平台删除结果 → 回给服务端的状态（三档，平台 SDK 只回 bool）</summary>
    public static TaskItemStatus StatusFor(CollectionDeleteOutcome outcome) => outcome switch
    {
        CollectionDeleteOutcome.Deleted => TaskItemStatus.Deleted,
        // 会话已解绑：平台侧无从下手 → 算「已清」（否则这条合集永远删不掉）
        CollectionDeleteOutcome.UnboundCleaned => TaskItemStatus.UnboundCleaned,
        _ => TaskItemStatus.PlatformFailed,
    };

    /// <summary>这条子项是不是终态失败（任务页上要红色标出来、可重试）</summary>
    public static bool IsFailed(int status) =>
        status != (int)TaskItemStatus.Pending && !IsCleared(status);

    /// <summary>这条子项的平台侧是不是了结了</summary>
    public static bool IsCleared(int status) =>
        status is (int)TaskItemStatus.Deleted
                 or (int)TaskItemStatus.AlreadyGone
                 or (int)TaskItemStatus.UnboundCleaned;

    // ═══ 平台侧可删判定（消费者两个分支共用）═══

    /// <summary>
    /// 这条子项能不能拿去平台上删：**会话可用 + 有平台侧对象 ID**，缺一不可。
    ///
    /// 会话为 null（账号已解绑 / 老数据行 —— 建号级联清空是后加的，实测库里留着一批）
    /// 或空 Guid 还去凭证接口，服务端 <c>PlatformSessionId.Of</c> 当场抛 DomainException
    /// （2026-10-14 用户实测：合集「全选删除」整批卡在失败）。视频分支一直有这道守卫、
    /// 合集分支漏过一回 —— 收口成一个纯函数给两个分支共用，不再有「一个记得、一个忘了」。
    /// </summary>
    public static bool CanDeleteOnPlatform(Guid? sessionId, string? platformObjectId) =>
        sessionId is { } s && s != Guid.Empty
        && !string.IsNullOrWhiteSpace(platformObjectId);

    // ═══ 手动兜底与卡上警示（2026-10-14）═══

    /// <summary>
    /// 失败子项能不能走「确认已删除 → 清理记录」手动兜底：**只对「平台删除失败」开放**。
    ///
    /// 那是「平台可能其实删掉了但被误判」的场景（快手集合删除误报就是活例），
    /// 用户亲自核对平台后可以自己收尾 —— 否则一条误判就能把合集记录永久卡死在本地。
    /// 登录过期（重登后重试是正路）、本地清理失败（重试自愈）都不开这个口子。
    /// </summary>
    public static bool CanConfirmGone(int status) =>
        status == (int)TaskItemStatus.PlatformFailed;

    /// <summary>合集卡「删除未完成」警示条文案（卡上直接绑；让「卡为什么还在」有解释）</summary>
    public static string CollectionDeleteFailureText(int failedCount) =>
        $"删除未完成：{failedCount} 个账号没删掉，记录已保留";

    /// <summary>
    /// 「删除记录」确认条文案（任务有失败项时先摆这条）——
    /// 删掉任务 = 丢掉失败项的重试入口，被它们卡住的本地记录就再没有出口了，必须说清。
    /// </summary>
    public static string DeleteRecordConfirmText(int failedCount) =>
        $"该任务还有 {failedCount} 条没删除成功。删掉任务记录后，这些条目将失去重试入口，" +
        "被它们卡住的本地记录也会一直没有出口。确定仍要删除？";

    // ═══ 任务页配色（自检可直接断言 hex，页面只管绑）═══

    /// <summary>任务整体状态点：等进行中=品牌紫、完成=绿、部分失败=橙、失败=红、已取消=灰</summary>
    public static string TaskDotHex(int status) => status switch
    {
        0 or 1 => "#6C5CE7",   // Pending / Running
        2 => "#00B42A",        // Completed
        3 => "#FF7D00",        // PartialFailed —— 橙色：「有东西没删掉」但不算全盘失败
        4 => "#F53F3F",        // Failed
        _ => "#C9CDD4",        // Cancelled
    };

    public static string TaskPillBgHex(int status) => status switch
    {
        0 or 1 => "#F4F1FE",
        2 => "#EBF7EE",
        3 => "#FFF7E6",
        4 => "#FDECEA",
        _ => "#F2F3F5",
    };

    public static string TaskPillFgHex(int status) => status switch
    {
        0 or 1 => "#4B3BC3",
        2 => "#0A5C28",
        3 => "#B45F06",
        4 => "#C62828",
        _ => "#86909C",
    };

    /// <summary>子项状态点色（与视频页六态同一套语义）</summary>
    public static string ItemDotHex(int status) => status switch
    {
        (int)TaskItemStatus.Pending => "#6C5CE7",
        (int)TaskItemStatus.Deleted => "#00B42A",
        (int)TaskItemStatus.AlreadyGone => "#00B42A",
        // 解绑也算「了结了」，但平台上的那份可能还在 —— 用灰的，别用绿的假装删干净了
        (int)TaskItemStatus.UnboundCleaned => "#C9CDD4",
        _ => "#F53F3F",
    };

    // ═══ 任务页文案 ═══

    /// <summary>任务行的时间（今天只给时分，其余给日期）</summary>
    public static string DateText(DateTime when, DateTime now)
    {
        var day = when.Date;
        var today = now.Date;
        if (day == today) return $"今天 {when:HH:mm}";
        if (day == today.AddDays(-1)) return $"昨天 {when:HH:mm}";
        return when.ToString("yyyy-MM-dd HH:mm");
    }

    /// <summary>子项的副行：`抖音 · 主号`（重试过的话把次数也带上，用户才知道自动重试过了）</summary>
    public static string ItemSubText(string platformName, string? accountLabel, int retryCount)
    {
        var account = string.IsNullOrWhiteSpace(accountLabel) ? "账号已解绑" : accountLabel!.Trim();
        var line = $"{platformName} · {account}";
        return retryCount > 0 ? $"{line} · 第 {retryCount + 1} 次尝试" : line;
    }

    /// <summary>任务进度：`删除 12 个作品` / `8 / 12`（进度条比例）</summary>
    public static double ProgressValue(int done, int total) =>
        total <= 0 ? 0 : Math.Clamp(done * 100.0 / total, 0, 100);

    /// <summary>任务行的计数文案（失败数不为 0 时必须写出来，否则用户不知道有东西没删掉）</summary>
    public static string CountText(int done, int total, int failed)
    {
        if (total <= 0) return "";
        var text = $"{done} / {total}";
        return failed > 0 ? $"{text} · {failed} 个失败" : text;
    }

    /// <summary>导航徽章：未完成任务数 + 失败条数</summary>
    public static string? BadgeText(int unsettled, int failed)
    {
        var n = unsettled + failed;
        return n > 0 ? n.ToString() : null;
    }

    /// <summary>
    /// 提交用的任务标题。视频删除按「几个作品」、合集删除按合集的个数 ——
    /// 用户看到的应该是「我删了几条内容」，不是「我发了几条请求」。
    /// </summary>
    public static string SubmitTitle(string kindName, int itemCount, string? subject = null) =>
        string.IsNullOrWhiteSpace(subject)
            ? $"{kindName} {itemCount} 项"
            : $"{kindName}「{subject}」";

    /// <summary>
    /// 自检用：把 wire 数字钉死。这些值改了服务端就认错状态，
    /// 而且是**静默**认错（没有编译期保护），所以必须有断言守着。
    /// </summary>
    public static IReadOnlyList<(string Name, int Value)> WireValues { get; } =
    [
        (nameof(TaskItemStatus.Pending), (int)TaskItemStatus.Pending),
        (nameof(TaskItemStatus.Deleted), (int)TaskItemStatus.Deleted),
        (nameof(TaskItemStatus.AlreadyGone), (int)TaskItemStatus.AlreadyGone),
        (nameof(TaskItemStatus.UnboundCleaned), (int)TaskItemStatus.UnboundCleaned),
        (nameof(TaskItemStatus.NeedLogin), (int)TaskItemStatus.NeedLogin),
        (nameof(TaskItemStatus.PlatformFailed), (int)TaskItemStatus.PlatformFailed),
        (nameof(TaskItemStatus.RecordCleanupFailed), (int)TaskItemStatus.RecordCleanupFailed),
    ];
}
