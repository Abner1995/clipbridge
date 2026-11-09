namespace CreatFlow.NewApp.Services;

/// <summary>
/// 视频管理页的服务端调用集中点（与 <see cref="PlatformSessionApi"/> 同款：一个页面一个文件，
/// 页面 VM 里不出现裸路径字符串）。
///
/// 三条接口：
///   · 分页查询 —— 只返回已发布记录，带平台/关键词筛选与 facet 计数
///   · 删除记录 —— **硬删**，且「最后一条记录会把父任务一起删」；不幂等（重复删 = 404）
///   · 平台配置 / 会话列表 —— 筛选 chip 的中文名与品牌色、账号名解析
/// </summary>
public static class VideoArchiveApi
{
    /// <summary>分页查询。platformKey / keyword 为 null 表示不过滤（服务端忽略空串）</summary>
    public static Task<VideoArchivePage?> PageAsync(
        int page, int pageSize, string? platformKey = null, string? keyword = null) =>
        ApiClient.PostAsync<VideoArchivePage>(
            "/api/publish/platforms/page",
            new PlatformPageRequest(page, pageSize, platformKey, keyword));

    /// <summary>
    /// 删掉服务端记录。
    /// ⚠️ 字段名必须是 <c>PlatformId</c>（<see cref="PublishPlatformIdRequest"/>）——
    /// 队列页踩过：送 `PublishIdRequest(Id)` 字段名对不上，反序列化成 `Guid.Empty` → 404。
    /// </summary>
    public static Task<object?> DeleteRecordAsync(Guid platformId) =>
        ApiClient.PostAsync<object>(
            "/api/publish/platforms/delete", new PublishPlatformIdRequest(platformId));

    /// <summary>
    /// 列出一条视频的**全部已发布副本**（跨页）。
    /// 「删除该视频全部副本」靠它：分页单位是平台记录，一个视频的 4 条记录会被切在相邻两页，
    /// 只删本页那几条的话用户会以为删干净了、实际平台上还留着。
    /// </summary>
    public static async Task<List<VideoArchiveItem>> ByTaskAsync(Guid taskId)
    {
        var resp = await ApiClient.PostAsync<VideoArchiveByTaskResponse>(
            "/api/publish/platforms/by-task", new PublishTaskIdRequest(taskId));
        return resp?.Items ?? [];
    }

    /// <summary>平台配置（中文名 + 品牌色）—— 匿名可访问，也当服务端就绪探针</summary>
    public static Task<List<PlatformConfigItem>?> PlatformsAsync() =>
        ApiClient.GetAsync<List<PlatformConfigItem>>("/api/config/platforms");

    /// <summary>已绑定会话（解析「这条记录是哪个账号发的」）</summary>
    public static async Task<Dictionary<Guid, string>> AccountLabelsAsync()
    {
        var resp = await ApiClient.PostAsync<PlatformSessionListResponse>("/api/platform-sessions/list");
        return resp?.Sessions is { Count: > 0 } sessions
            ? AccountLabels.Map(sessions)
            : [];
    }
}
