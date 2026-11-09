namespace CreatFlow.Platforms.Xiaohongshu;

/// <summary>
/// 小红书视频管理服务 — 列表、删除。
///
/// 对标 Python examples/upload_publish_xiaohongshu.py 的 cmd_list() / cmd_delete()。
///
/// API:
///   List:   GET /api/galaxy/v2/creator/note/user/posted (galaxy API on creator host)
///   Delete: POST /web_api/sns/capa/postgw/note/delete
/// </summary>
public sealed class XhsVideoService
{
    private readonly PlatformOptions _options;
    private readonly ILogger<XhsVideoService>? _logger;

    public XhsVideoService(PlatformOptions options, ILogger<XhsVideoService>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// 获取已发布视频列表。
    /// </summary>
    /// <param name="cookie">Cookie</param>
    /// <param name="page">页码（0-based）</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>视频条目列表</returns>
    public async Task<List<XhsVideoItem>> ListAsync(
        string cookie, int page = 0, CancellationToken ct = default)
    {
        using var client = new XhsApiClient(cookie, _options,
            _logger as ILogger<PlatformHttpClient>);

        try
        {
            // Galaxy API on creator host
            var resp = await client.PostCreatorAsync<XhsVideoListResponse>(
                "/api/galaxy/v2/creator/note/user/posted",
                new { tab = "0", page = page.ToString() }, ct);

            return resp?.Data?.Notes ?? [];
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("401") || ex.Message.Contains("403"))
        {
            _logger?.LogWarning("XHS video list: auth failed, session may be expired");
            return [];
        }
    }

    /// <summary>
    /// 删除指定笔记。
    /// </summary>
    /// <param name="cookie">Cookie</param>
    /// <param name="noteId">笔记 ID</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>true 删除成功</returns>
    public async Task<bool> DeleteAsync(
        string cookie, string noteId, CancellationToken ct = default)
    {
        using var client = new XhsApiClient(cookie, _options,
            _logger as ILogger<PlatformHttpClient>);

        try
        {
            // 注意：该接口要 snake_case 的 note_id（note/detail 反而用 noteId，平台不一致）。
            // 且 XhsApiClient 的 CamelCase 命名策略会把匿名对象 note_id 也转成 noteId，
            // 所以用字典（字典 key 不经过命名策略），和 XhsPublishService/XhsCollectionService 一致。
            var resp = await client.PostEdithAsync<XhsDeleteNoteResponse>(
                "/web_api/sns/capa/postgw/note/delete",
                new Dictionary<string, object> { ["note_id"] = noteId }, ct);

            if (!resp.Success)
            {
                _logger?.LogWarning("XHS video delete rejected: noteId={NoteId}", noteId);
                return false;
            }

            _logger?.LogInformation("XHS video deleted: noteId={NoteId}", noteId);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "XHS video delete failed: noteId={NoteId}", noteId);
            return false;
        }
    }
}
