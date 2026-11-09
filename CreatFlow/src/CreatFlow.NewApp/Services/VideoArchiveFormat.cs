namespace CreatFlow.NewApp.Services;

/// <summary>删除确认条的两种口径：能连平台一起删 / 只能删本地记录。</summary>
public enum DeleteScope
{
    /// <summary>会话在 + 有平台作品 ID —— 确认后平台上的那条也会消失</summary>
    PlatformAndLocal,

    /// <summary>账号已解绑（或没有平台作品 ID）—— 只能删本地记录，平台上的还在</summary>
    LocalOnly,

    /// <summary>
    /// 平台**没有接入程序内删除**（目前是 B站：删除要过 Geetest 人机验证）——
    /// 只清本地记录，并明确提示到平台侧删除。
    /// </summary>
    LocalOnlyPlatformUnsupported,
}

/// <summary>删除这件事对用户呈现的**最终结果**（平台侧 × 服务端侧两段结果收口后）。</summary>
public enum DeleteOutcome
{
    /// <summary>平台侧删掉 + 本地记录删掉</summary>
    Deleted,

    /// <summary>平台上本来就没有（手删过）→ 清掉脏记录</summary>
    AlreadyGone,

    /// <summary>账号已解绑 → 只清了本地记录</summary>
    UnboundCleaned,

    /// <summary>平台不支持程序内删除（B站）→ 只清了本地记录，平台视频未动</summary>
    LocalOnlyCleaned,

    /// <summary>平台登录过期（本轮登录后再删）</summary>
    NeedLogin,

    /// <summary>平台删除失败 —— 记录保留，可重试</summary>
    PlatformFailed,

    /// <summary>平台已删掉但本地记录没清掉 —— 可重试（重试会自愈）</summary>
    RecordCleanupFailed,
}

/// <summary>
/// 视频管理页的**展示纯函数**（`--selftest` 直接喂数据断言）。
///
/// 两条硬约束：
/// ① **不得引用任何 Avalonia 类型** —— 自检跑在 <c>BuildAvaloniaApp()</c> **之前**，
///    碰控件/画刷类型会炸在进程启动阶段（颜色一律以 hex 字符串返回，页面自己绑）。
/// ② 时间一律**由调用方注入 now**：`Clock.Now` 是北京时间但 `Kind=Unspecified`，
///    在纯函数里偷偷取当前时间就没法测「跨月/跨年算不算昨天」。
///
/// 放在 Services 而不是塞进 VM：删除结果文案（哪个结果算成功、哪个要保留记录）
/// 是这一页唯一有分支风险的地方，必须能被自检穷举。
/// </summary>
public static class VideoArchiveFormat
{
    // ═══ 分页 ═══

    /// <summary>总页数（**至少 1**：0 条时也要显示「第 1 / 1 页」，不能是「第 1 / 0 页」）</summary>
    public static int TotalPages(int total, int pageSize) =>
        pageSize <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));

    public static bool HasPrev(int page) => page > 1;

    public static bool HasNext(int page, int total, int pageSize) =>
        page < TotalPages(total, pageSize);

    public static string PageText(int page, int total, int pageSize) =>
        $"第 {page} / {TotalPages(total, pageSize)} 页";

    /// <summary>
    /// 汇总条：`共 231 条已发布记录（含已从上传队列移除的）`。
    ///
    /// 平台筛选生效时**带上平台名**：Total 是筛选后的条数，不说明的话
    /// 「共 27 条」会和芯片上的「全部 108」看着像对不上（2026-11-04 用户截图）。
    /// </summary>
    public static string SummaryText(int total, string? platformName = null)
    {
        if (total == 0) return "还没有已发布记录";
        var scope = string.IsNullOrWhiteSpace(platformName) ? "" : $" · {platformName}";
        return $"共 {total} 条已发布记录{scope}（含已从上传队列移除的）";
    }

    /// <summary>月份切换时仍要能翻页，所以按「第 x / y 页」而不是「还有多少条」</summary>
    public static string ChipText(string platformName, int count) => $"{platformName} {count}";

    // ═══ 分组 / 时间 ═══

    /// <summary>天头：今天 / 昨天 / 前天 / 2026-09-18（**同一个时钟基准**，别拿本地时区再换算一次）</summary>
    public static string DayLabel(DateTime when, DateTime now)
    {
        var day = when.Date;
        var today = now.Date;
        if (day == today) return "今天";
        if (day == today.AddDays(-1)) return "昨天";
        if (day == today.AddDays(-2)) return "前天";
        return day.ToString("yyyy-MM-dd");
    }

    /// <summary>天头整行：`今天 · 12 条`</summary>
    public static string DayHeaderText(string dayLabel, int count) => $"{dayLabel} · {count} 条";

    /// <summary>行内时间（日期已由天头给出，行内只要时分）</summary>
    public static string TimeText(DateTime when) => when.ToString("HH:mm");

    // ═══ 行内容 ═══

    /// <summary>
    /// 标题回退链：平台侧标题 → 任务级通用标题 → 视频文件名（去扩展名）。
    /// 服务端两处都可能为 null：<c>Title</c> 是「这条平台记录自己的标题」（用户改了才有），
    /// <c>CommonTitle</c> 是提交时的通用标题；文件名兜底保证任何一行都有标题可读。
    /// </summary>
    public static string DisplayTitle(string? title, string? commonTitle, string? fileName)
    {
        if (!string.IsNullOrWhiteSpace(title)) return title!.Trim();
        if (!string.IsNullOrWhiteSpace(commonTitle)) return commonTitle!.Trim();
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var name = Path.GetFileNameWithoutExtension(fileName.Trim());
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        return "未命名视频";
    }

    /// <summary>
    /// 文件大小：`0 B / 1023 B / 1 KB / 48.2 MB / 1.2 GB`。
    /// 比队列页那份（只有 MB/GB）多两档 —— 归档页会遇到 0（父任务被删过）和几百 KB 的短素材，
    /// 显示成「0 MB / 0.3 MB」比「0 B / 320 KB」难读得多。
    /// </summary>
    public static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024L * 1024) return $"{bytes / 1024.0:0.#} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024:0.#} MB";
        return $"{bytes / 1024.0 / 1024 / 1024:0.#} GB";
    }

    /// <summary>
    /// 账号名。**解析不出就是「账号已解绑」**：会话解绑会把所有记录（含已发布）的
    /// `platform_session_id` 置空，账号名没有快照列，事后无从追溯（见设计文档「不在本期」）。
    /// </summary>
    public static string AccountText(string? resolvedLabel) =>
        string.IsNullOrWhiteSpace(resolvedLabel) ? "账号已解绑" : resolvedLabel!.Trim();

    /// <summary>副行：`抖音 · @主号 · 48.2 MB` + 有 ID 时再跟 ` · ID 7412…`</summary>
    public static string SubLine(string platformName, string accountText, long sizeBytes, string? videoId)
    {
        var line = $"{platformName} · {accountText} · {FormatSize(sizeBytes)}";
        return string.IsNullOrWhiteSpace(videoId) ? line : line + $" · ID {ShortId(videoId!)}";
    }

    /// <summary>平台作品 ID 截断显示（抖音 aweme_id 19 位、小红书 noteId 24 位，整行会被撑爆）</summary>
    public static string ShortId(string videoId, int keep = 8)
    {
        var id = videoId.Trim();
        return id.Length <= keep * 2 ? id : id[..keep] + "…" + id[^4..];
    }

    // ═══ 删除 ═══

    /// <summary>
    /// 这条能不能连平台一起删：会话**还在服务端** + 有平台作品 ID。
    ///
    /// <paramref name="sessionKnown"/> 为什么不能省：记录上的 `platform_session_id` 不一定被清干净 ——
    /// 实测库里有 3 条 8 月的记录，指向的会话早没了、但 id 还留在行上（建号级联清空是后加的）。
    /// 只看「id 非空」就会承诺「平台上的也会被移除」，而删除时拿凭证必然 404 ——
    /// 确认条说的和实际能做的**是两件事**，这正是老版最招人骂的地方。
    /// </summary>
    public static DeleteScope DeleteMode(
        string platformKey, Guid? sessionId, string? platformVideoId, bool sessionKnown = true)
    {
        // 平台没接删除链路（B站要 Geetest）→ 连「承诺删平台作品」都不能给
        if (!PlatformNames.SupportsVideoDeletion(platformKey))
            return DeleteScope.LocalOnlyPlatformUnsupported;

        return sessionKnown && sessionId is { } s && s != Guid.Empty
            && !string.IsNullOrWhiteSpace(platformVideoId)
            ? DeleteScope.PlatformAndLocal
            : DeleteScope.LocalOnly;
    }

    /// <summary>
    /// 确认条文案。**必须把「删什么、不删什么」写全**：老版的类注释与代码注释自相矛盾
    /// （一处说连本地视频文件一起删、一处说不删），用户只能靠猜。
    /// </summary>
    public static string DeleteStripText(DeleteScope scope) => scope switch
    {
        DeleteScope.PlatformAndLocal =>
            "删除后平台上的这条视频也会被移除，且不可恢复。本地视频文件不受影响。",
        DeleteScope.LocalOnlyPlatformUnsupported =>
            "B站不提供程序内删除（平台会要求人机验证）——这里只清理本地记录，"
            + "视频本身请到 B 站创作中心删除。本地视频文件不受影响。",
        _ =>
            "只能删除本地记录（账号已解绑或这条记录没有平台作品 ID）—— 平台上的视频不会被移除。",
    };

    public static string DeleteOutcomeText(DeleteOutcome outcome) => outcome switch
    {
        DeleteOutcome.Deleted => "已删除",
        DeleteOutcome.AlreadyGone => "平台上已不存在，已清理记录",
        DeleteOutcome.UnboundCleaned => "账号已解绑，已清理本地记录",
        DeleteOutcome.LocalOnlyCleaned =>
            "已清理本地记录（平台不提供程序内删除，视频仍在平台上）",
        DeleteOutcome.NeedLogin => "平台登录已过期，请重新登录后再删除",
        // 除抖音外，平台 SDK 只回 bool（快手失败还抛异常）—— 分不出 404 还是 401，
        // 所以文案要把「可能已不存在」说出来，别把不确定说成确定
        DeleteOutcome.PlatformFailed => "平台删除失败（可能已不存在），记录已保留，可重试",
        DeleteOutcome.RecordCleanupFailed => "平台上的视频已删除，但本地记录没清掉，可重试",
        _ => "删除失败",
    };

    /// <summary>这个结果要不要把行从列表里摘掉（摘 = 服务端那边确实已经没有了）</summary>
    public static bool RowRemoved(DeleteOutcome outcome) =>
        outcome is DeleteOutcome.Deleted or DeleteOutcome.AlreadyGone
            or DeleteOutcome.UnboundCleaned or DeleteOutcome.LocalOnlyCleaned;

    /// <summary>这个结果要不要报错误条（红底那种）—— 保留记录的那几种都是错，得让用户看见</summary>
    public static bool IsError(DeleteOutcome outcome) =>
        outcome is DeleteOutcome.NeedLogin or DeleteOutcome.PlatformFailed
            or DeleteOutcome.RecordCleanupFailed;

    /// <summary>
    /// 从归档记录的 <c>extra_data_json</c> 里取快手封面 Key（<c>ks_cover_key</c>）。
    ///
    /// 用途：ID 是早期兜底的 `mediaId` 时，两条删除端点都不认，只能靠封面 Key
    /// 回查作品列表拿真实 ID（见 `KuaishouVideoService.DeleteSmartAsync`）。
    /// 坏 JSON / 没这个键 / 值不是字符串一律返回 null —— 删除主路径不依赖它，
    /// 缺失只会让「mediaId 老记录」删不掉，不能让整页/整批崩掉。
    /// </summary>
    public static string? KuaishouCoverKey(string? extraDataJson)
    {
        if (string.IsNullOrWhiteSpace(extraDataJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(extraDataJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("ks_cover_key", out var el)
                && el.ValueKind == JsonValueKind.String)
            {
                var key = el.GetString();
                return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
            }
        }
        catch (JsonException)
        {
            // 坏档按「没有」处理（extra_data_json 是别的功能的载荷，不能因为它挡住删除）
        }
        return null;
    }

    /// <summary>
    /// 筛选芯片「全部」的计数：**各平台 facet 之和**。
    ///
    /// 不能拿服务端返回的 <c>Total</c>：它是**当前平台筛选下**的条数 ——
    /// 选中「视频号」时 Total=13，而抖音/快手/小红书各 13/14/13，页面上就会
    /// 出现「全部 13 / 快手 14」这种自相矛盾的数（2026-11-04 用户实测截图）。
    /// facet 计数只跟关键词、不跟平台筛选，相加正是「全部」该有的值。
    /// </summary>
    public static int AllChipCount(IEnumerable<PlatformCountItem> counts, int fallback)
    {
        var sum = 0;
        var any = false;
        foreach (var c in counts)
        {
            any = true;
            if (c.Count > 0) sum += c.Count;
        }
        return any ? sum : fallback;
    }

    // ═══ 批量删除 ═══

    /// <summary>
    /// 批量删除的**并行单元**：同一个 (平台, 会话) 串行、不同单元并行。
    ///
    /// 键里为什么带平台：抖音的 cookie 和快手的 cookie 是两套，同一账号跨平台互不影响，
    /// 没必要互相排队。会话为空/空 Guid（账号已解绑）一律归到 `-` 一档 ——
    /// 这类行本来就不调平台 API，归一组是为了让本地记录清理也串行，
    /// 免得几十个 DELETE 同时打服务端。
    ///
    /// ⚠️ 这个键同时是**风控边界**：老版实测「同账号并发上传会 403」（见
    /// <c>UploadQueueViewModel.ProcessQueueAsync</c> 的注释），删除沿用同一分组。
    /// </summary>
    public static string GroupKey(string platformKey, Guid? sessionId) =>
        // 平台 key 统一小写：大小写不一致会把同一个账号拆成两组，那就等于没限流
        $"{(string.IsNullOrWhiteSpace(platformKey) ? "?" : platformKey.Trim().ToLowerInvariant())}:"
        + (sessionId is { } s && s != Guid.Empty ? s.ToString("D") : "-");

    /// <summary>
    /// 「删除这条视频的全部副本」确认条。**必须说清「含本页之外的」** ——
    /// 分页单位是平台记录，同一个视频的副本会被切在相邻几页，
    /// 用户眼里只看到本页那几条，不说清楚就会以为只删本页。
    /// </summary>
    public static string WholeVideoStripText(int count) =>
        count <= 0
            ? "这条视频在平台上已经没有副本了。"
            : $"将删除这条视频在 {count} 个账号/平台上的副本（含本页之外的），且不可恢复。本地视频文件不受影响。";

    /// <summary>「全部账号」入口的按钮文字（带条数，用户点之前就知道要删几条）</summary>
    public static string WholeVideoButtonText(int count) =>
        count <= 1 ? "全部副本" : $"全部副本 {count}";
}
