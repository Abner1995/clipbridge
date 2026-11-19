using CreatFlow.Platforms.Core;
using CreatFlow.Platforms.WechatVideo;

namespace CreatFlow.NewApp.Services;

/// <summary>平台侧「修改可见性」的归一化结果。</summary>
public enum PlatformVisibilityResult
{
    /// <summary>平台已接受修改</summary>
    Success,

    /// <summary>
    /// 这条记录指向的**会话已经不在了**（解绑、或重新登录换了新 session id）——
    /// 不是失败：平台侧无从下手，UI 应提示「账号已解绑」而不是「稍后重试」。
    /// </summary>
    SessionGone,

    /// <summary>凭证不可用 / 平台拒绝 / 网络失败 —— 记录保留，可重试（错误文案在 Error 里）</summary>
    Failed,
}

/// <summary>
/// 「已发布作品的可见性」—— 目前只有视频号有该能力
/// （<c>post_update_visible</c>，发布后独立操作，<c>post_create</c> 体里没有可见性字段）。
///
/// 与 <see cref="VideoArchiveDeleteService"/> 同构：按会话取凭证 → 调 SDK → 归一化结果。
/// 凭证字段的取法与发布/删除一致（deviceId 缺失时生成兜底值、finderId 来自 localStorage）。
///
/// 读回（<see cref="ReadVisibleTypesAsync"/>）同样走会话凭证 + <c>post_list</c>：
/// 平台列表项自带 <c>visibleType</c>，这是**当前可见性的唯一权威来源**，
/// 本地归档没有这一列（官方页的按钮文案也是按它算的）。
/// </summary>
public static class VideoArchiveVisibilityService
{
    /// <summary>读回时最多翻几页（一页 20 条 = 最多 60 条；视频管理一页最多 100 行，
    /// 更老的作品翻不到就按「未知」处理，不无限翻）</summary>
    public const int ReadMaxPages = 3;

    /// <summary>读回每页条数（与发布回查 <c>post_list</c> 同款）</summary>
    public const int ReadPageSize = 20;

    /// <summary>
    /// 设置已发布作品的可见性。调用方必须先确认会话在、作品 ID 有
    /// （视频管理页只对满足条件的视频号行显示按钮）。
    /// </summary>
    /// <param name="visibleType">
    /// <see cref="WechatVideoContentService.VisibleTypePublic"/> /
    /// <see cref="WechatVideoContentService.VisibleTypePrivate"/>
    /// </param>
    /// <returns>成功时 Error 为 null；失败时 Error 为可直接展示的中文原因</returns>
    public static async Task<(PlatformVisibilityResult Result, string? Error)> SetOnPlatformAsync(
        string platformKey, Guid sessionId, string platformVideoId, int visibleType,
        CancellationToken ct = default)
    {
        if (!string.Equals(platformKey, PlatformNames.WechatVideo, StringComparison.OrdinalIgnoreCase))
            return (PlatformVisibilityResult.Failed, "只有视频号支持发布后修改可见性");

        if (sessionId == Guid.Empty)
            return (PlatformVisibilityResult.SessionGone, null);

        if (string.IsNullOrWhiteSpace(platformVideoId))
            return (PlatformVisibilityResult.Failed,
                "这条记录没有平台作品 ID，无法设置 —— 请到视频号助手手动修改");

        var (runtime, credResult, credError) = await TryBuildRuntimeAsync(sessionId);
        if (runtime is null) return (credResult, credError);

        try
        {
            var svc = new WechatVideoContentService(runtime.Options) { FinderId = runtime.FinderId };
            var error = await svc.UpdateVisibleAsync(
                runtime.Cookie, runtime.DeviceId, platformVideoId, visibleType, ct);

            return error is null
                ? (PlatformVisibilityResult.Success, null)
                : (PlatformVisibilityResult.Failed, error);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "[VideoArchive] 设置可见性异常 {Platform}/{Id}",
                platformKey, platformVideoId);
            return (PlatformVisibilityResult.Failed, "设置失败（请求异常），请稍后重试");
        }
    }

    /// <summary>
    /// 读回一批作品的**当前可见性**（`objectId → visibleType`）。
    ///
    /// 一次读一个会话（同一账号的多个作品只翻一次列表）。只返回能确认的
    /// 1/3 两种值；翻不到（作品太老 / 不在视频列表）或读失败的作品不出现在字典里，
    /// 由调用方按「未知」处理 —— **未知不等于公开**，UI 只应把它当安全默认。
    /// </summary>
    /// <returns>
    /// VisibleTypes：确认到的 `objectId → 1/3`；
    /// SessionGone：会话已解绑（调用方应把相关行的按钮收起，重试没有意义）
    /// </returns>
    public static async Task<(Dictionary<string, int> VisibleTypes, bool SessionGone)>
        ReadVisibleTypesAsync(
            string platformKey, Guid sessionId,
            IReadOnlyCollection<string> platformVideoIds,
            CancellationToken ct = default)
    {
        var empty = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!string.Equals(platformKey, PlatformNames.WechatVideo, StringComparison.OrdinalIgnoreCase))
            return (empty, false);

        if (sessionId == Guid.Empty) return (empty, false);

        var targets = platformVideoIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        if (targets.Count == 0) return (empty, false);

        var (runtime, credResult, _) = await TryBuildRuntimeAsync(sessionId);
        if (runtime is null)
            return (empty, credResult == PlatformVisibilityResult.SessionGone);

        var found = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            var svc = new WechatVideoContentService(runtime.Options) { FinderId = runtime.FinderId };
            for (var page = 1; page <= ReadMaxPages; page++)
            {
                var list = await svc.ListAsync(
                    runtime.Cookie, runtime.DeviceId, page, ReadPageSize, 11, ct);
                if (list is null) break;

                foreach (var item in list.Items)
                {
                    if (item.ObjectId is { Length: > 0 } objectId
                        && targets.Contains(objectId)
                        && item.VisibleType is WechatVideoContentService.VisibleTypePublic
                            or WechatVideoContentService.VisibleTypePrivate)
                    {
                        found[objectId] = item.VisibleType;
                    }
                }

                if (found.Count == targets.Count) break;
                if (!list.ContinueFlag || list.Items.Count == 0) break;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // 读回是背景增强：失败只影响按钮文案的准确度，不影响列表本身
            Log.Warning(ex, "[VideoArchive] 读回视频号可见性失败 session={SessionId}", sessionId);
        }

        return (found, false);
    }

    // ── 会话凭证 → 平台调用上下文（设置与读回共用）──

    private sealed record WechatRuntime(
        string Cookie, string DeviceId, string FinderId, PlatformOptions Options);

    private static async Task<(WechatRuntime? Runtime, PlatformVisibilityResult Result, string? Error)>
        TryBuildRuntimeAsync(Guid sessionId)
    {
        PlatformSessionCredentials? creds;
        try
        {
            creds = await PlatformSessionApi.GetCredentialsAsync(sessionId);
        }
        catch (ApiException ex) when (ex.StatusCode == 404)
        {
            Log.Information("[VideoArchive] 会话已不存在 session={SessionId} → 无法读/写可见性", sessionId);
            return (null, PlatformVisibilityResult.SessionGone, null);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[VideoArchive] 取会话凭证失败 session={SessionId}", sessionId);
            return (null, PlatformVisibilityResult.Failed, "读取账号凭证失败，请稍后重试");
        }

        if (creds is null || string.IsNullOrWhiteSpace(creds.Cookie))
        {
            Log.Warning("[VideoArchive] 会话凭证为空 session={SessionId}", sessionId);
            return (null, PlatformVisibilityResult.Failed,
                "账号凭证不可用，请到「平台账号」页重新登录视频号");
        }

        var options = new PlatformOptions
        {
            Environment = CredentialFields.EnvironmentSnapshot(creds.ExtraDataJson),
            SessionId = sessionId,
        };

        // deviceId 兜底生成（与发布/删除同款）：不带 deviceId 会被判成陌生设备
        var extra = CredentialFields.Extra(creds.ExtraDataJson);
        var deviceId = extra.GetValueOrDefault("deviceId")?.ToString()
            ?? extra.GetValueOrDefault("device_id")?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(deviceId))
            deviceId = Guid.NewGuid().ToString("N")[..32];

        var finderId = CredentialFields.LocalStorageField(
            creds.LocalStorageJson, "finder_username") ?? "";

        return (new WechatRuntime(creds.Cookie, deviceId, finderId, options),
            PlatformVisibilityResult.Success, null);
    }
}
