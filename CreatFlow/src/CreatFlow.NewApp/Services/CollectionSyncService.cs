using CreatFlow.NewApp.Models;
using CreatFlow.Platforms.Abstractions.Collections;
using CreatFlow.Platforms.Core;
using CreatFlow.Platforms.Douyin;
using CreatFlow.Platforms.Kuaishou;
using CreatFlow.Platforms.Toutiao;
using CreatFlow.Platforms.WechatVideo;
using CreatFlow.Platforms.Xiaohongshu;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 合集同步编排器 — 在桌面端并行调用各平台 SDK 创建/更新/删除合集，逐平台回报服务端。
/// 自 CreatFlow.App 移植（2026-09-09）；旧版写桌面调试文件，现走全局 Serilog。
///
/// 流程：
///   1. 从服务端获取会话凭证（Cookie/csrfToken/msToken/a_bogus/apiPh/deviceId）
///   2. 根据平台 key 路由到对应的 Platform SDK
///   3. 并行执行（Task.WhenAll），每个平台独立完成 → 立即回报服务端（/api/collections/report）
///   4. 返回所有平台的同步进度供 UI 展示
/// </summary>
public class CollectionSyncService
{
    /// <summary>
    /// 并行同步合集到多个平台（创建）。
    /// </summary>
    /// <param name="targets">平台目标列表（CollectionPlatform 记录 + 会话 ID）</param>
    /// <param name="coverImagePath">本地封面图文件路径（null = 跳过封面上传）</param>
    /// <param name="title">合集标题</param>
    /// <param name="description">合集简介</param>
    /// <param name="onProgressChanged">进度回调（UI 线程安全）</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>每个平台的最终同步结果</returns>
    public async Task<List<PlatformSyncProgress>> SyncToPlatformsAsync(
        List<SyncTarget> targets,
        string? coverImagePath,
        string title,
        string? description,
        Action<PlatformSyncProgress>? onProgressChanged = null,
        CancellationToken ct = default)
    {
        var progressList = targets.Select(t => new PlatformSyncProgress
        {
            PlatformKey = t.PlatformKey,
            SessionId = t.SessionId,
            DisplayName = t.DisplayName,
            AccountName = t.AccountName,
        }).ToList();

        // 并行执行：每个平台独立跑，不互相阻塞
        var tasks = targets.Select((target, i) =>
            SyncOneAsync(target, progressList[i], coverImagePath, title, description, onProgressChanged, ct));

        await Task.WhenAll(tasks);
        return progressList;
    }

    // ═══ Update to Platforms ═══

    /// <summary>
    /// 并行更新各平台上的合集元数据（标题/简介/封面）。
    /// 每个平台独立跑，不互相阻塞。封面图上传复用和创建一样的管线。
    /// </summary>
    /// <param name="targets">平台更新目标列表（含 platformCollectionId）</param>
    /// <param name="newCoverImagePath">新封面图本地路径（null = 不更换封面）</param>
    /// <param name="title">新标题</param>
    /// <param name="description">新简介</param>
    /// <param name="onProgressChanged">进度回调（UI 线程安全）</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>每个平台的更新结果</returns>
    public async Task<List<PlatformSyncProgress>> SyncUpdateToPlatformsAsync(
        List<UpdateTarget> targets,
        string? newCoverImagePath,
        string title,
        string? description,
        Action<PlatformSyncProgress>? onProgressChanged = null,
        CancellationToken ct = default)
    {
        var progressList = targets.Select(t => new PlatformSyncProgress
        {
            PlatformKey = t.PlatformKey,
            SessionId = t.SessionId,
            DisplayName = t.DisplayName,
            AccountName = t.AccountName,
        }).ToList();

        var tasks = targets.Select((target, i) =>
            UpdateOneAsync(target, progressList[i], newCoverImagePath,
                title, description, onProgressChanged, ct));

        await Task.WhenAll(tasks);
        return progressList;
    }

    /// <summary>
    /// 更新单个平台合集：获取凭证 → SDK Update → Report。
    /// </summary>
    private async Task UpdateOneAsync(
        UpdateTarget target,
        PlatformSyncProgress progress,
        string? newCoverImagePath,
        string title,
        string? description,
        Action<PlatformSyncProgress>? onProgressChanged,
        CancellationToken ct)
    {
        try
        {
            UpdateStep(progress, PlatformSyncStep.FetchingCredentials, onProgressChanged);
            var creds = await ApiClient.PostAsync<PlatformSessionCredentials>(
                "/api/platform-sessions/credentials",
                new GetCredentialsRequest(target.SessionId));

            if (creds?.Cookie == null)
            {
                SetFailed(progress, "未找到登录凭证，请重新登录该平台", onProgressChanged);
                await ReportAsync(target.PlatformId, false, null, progress.ErrorMsg);
                return;
            }

            switch (target.PlatformKey)
            {
                case PlatformNames.Douyin:
                    await UpdateDouyinAsync(target, progress, creds, newCoverImagePath,
                        title, description, onProgressChanged, ct);
                    break;

                case PlatformNames.WechatVideo:
                    await UpdateWechatVideoAsync(target, progress, creds,
                        title, description, onProgressChanged, ct);
                    break;

                case PlatformNames.Kuaishou:
                    await UpdateKuaishouAsync(target, progress, creds,
                        title, description, onProgressChanged, ct);
                    break;

                case PlatformNames.Xiaohongshu:
                    await UpdateXiaohongshuAsync(target, progress, creds,
                        title, description, onProgressChanged, ct);
                    break;

                case PlatformNames.Toutiao:
                    if (target.IsArticle)
                        await UpdateToutiaoArticleAsync(target, progress, creds, newCoverImagePath,
                            title, onProgressChanged, ct);
                    else
                        await UpdateToutiaoAsync(target, progress, creds, newCoverImagePath,
                            title, description, onProgressChanged, ct);
                    break;

                default:
                    SetSkipped(progress, $"{target.DisplayName} SDK 尚未实现", onProgressChanged);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            SetFailed(progress, "操作已取消", onProgressChanged);
        }
        catch (Exception ex)
        {
            SetFailed(progress, ex.Message, onProgressChanged);
            await TryReportAsync(target.PlatformId, false, null, progress.ErrorMsg);
        }
    }

    // ═══ Delete from Platforms ═══
    //
    // 删除**只有一条执行路径**：DeleteOnPlatformAsync（下面那个）。
    // 早先还有一条「交互式：并行删一批 + 逐平台汇报进度」的 DeleteFromPlatformsAsync，
    // P2 把批量删除改成走后台任务之后它就没调用方了，已删 ——
    // 留着的风险是两条路径迟早对「已解绑算不算成功」这类边界给出不同结论。

    /// <summary>
    /// 只删平台侧、不接 UI 回调 —— **异步任务消费者用**（它在后台跑，没有进度面板可喂）。
    /// 逻辑与交互式删除完全同一条（都走 <see cref="DeleteOneAsync"/>），
    /// 免得两条路径对「已解绑」这类边界给出不同结论。
    /// </summary>
    public async Task<PlatformDeleteReport> DeleteOnPlatformAsync(
        string platformKey, Guid sessionId, string platformCollectionId,
        string displayName, string accountName, CancellationToken ct = default,
        bool isArticle = false)
    {
        var progress = new PlatformSyncProgress
        {
            PlatformKey = platformKey,
            SessionId = sessionId,
            DisplayName = displayName,
            AccountName = accountName,
        };
        var target = new DeleteTarget(
            platformKey, displayName, accountName, platformCollectionId, sessionId, isArticle);
        return new PlatformDeleteReport(progress, await DeleteOneAsync(target, progress, null, ct));
    }

    /// <summary>
    /// 删除单个平台合集：获取凭证 → SDK Delete → 回调。返回归一化结果（见
    /// <see cref="CollectionDeleteOutcome"/>）。
    ///
    /// 各平台 Delete*Async 的返回形状不一（多数只回 bool），所以结果统一从它们推进的
    /// <see cref="PlatformSyncProgress.Step"/> 反推（Deleted = 成功，其余 = 失败）——
    /// 这样各平台实现不用改，精度限制也与视频页同源。
    /// </summary>
    private async Task<CollectionDeleteOutcome> DeleteOneAsync(
        DeleteTarget target,
        PlatformSyncProgress progress,
        Action<PlatformSyncProgress>? onProgressChanged,
        CancellationToken ct)
    {
        try
        {
            // ── 1. 获取凭证 ──
            UpdateStep(progress, PlatformSyncStep.FetchingCredentials, onProgressChanged);
            PlatformSessionCredentials? creds;
            try
            {
                creds = await ApiClient.PostAsync<PlatformSessionCredentials>(
                    "/api/platform-sessions/credentials",
                    new GetCredentialsRequest(target.SessionId));
            }
            catch (ApiException ex) when (ex.StatusCode == 404)
            {
                // 会话已经不在了（解绑 / 重登换了新 session id）→ 平台侧**永远**删不掉。
                // 这不算失败：算失败的话，8 个账号里只要有 1 个解绑过，
                // 这条合集记录就再也删不掉了（本地记录被一条不可能完成的任务永久卡住）。
                SetSkipped(progress, "账号已解绑，平台侧无从删除", onProgressChanged);
                return CollectionDeleteOutcome.UnboundCleaned;
            }

            if (creds?.Cookie == null)
            {
                SetFailed(progress, "未找到登录凭证", onProgressChanged);
                return CollectionDeleteOutcome.Failed;
            }

            // ── 2. 路由到平台 SDK ──
            switch (target.PlatformKey)
            {
                case PlatformNames.Douyin:
                    await DeleteDouyinAsync(progress, creds, target.PlatformCollectionId, onProgressChanged, ct);
                    break;

                case PlatformNames.WechatVideo:
                    await DeleteWechatVideoAsync(progress, creds, target.PlatformCollectionId, onProgressChanged, ct);
                    break;

                case PlatformNames.Kuaishou:
                    await DeleteKuaishouAsync(progress, creds, target.PlatformCollectionId, onProgressChanged, ct);
                    break;

                case PlatformNames.Xiaohongshu:
                    await DeleteXiaohongshuAsync(progress, creds, target.PlatformCollectionId,
                        target.SessionId, onProgressChanged, ct);
                    break;

                case PlatformNames.Toutiao:
                    if (target.IsArticle)
                        await DeleteToutiaoArticleAsync(progress, creds, target.PlatformCollectionId,
                            onProgressChanged, ct);
                    else
                        await DeleteToutiaoAsync(progress, creds, target.PlatformCollectionId,
                            onProgressChanged, ct);
                    break;

                default:
                    // 未支持的平台**不能算已清**：删不掉就不该删本地记录，
                    // 否则平台上的那个合集再也没人管（RecordArchive 同款保守口径）
                    SetFailed(progress, $"{target.DisplayName} SDK 尚未实现", onProgressChanged);
                    break;
            }

            return progress.Step == PlatformSyncStep.Deleted
                ? CollectionDeleteOutcome.Deleted
                : CollectionDeleteOutcome.Failed;
        }
        catch (OperationCanceledException)
        {
            SetFailed(progress, "操作已取消", onProgressChanged);
            return CollectionDeleteOutcome.Failed;
        }
        catch (Exception ex)
        {
            SetFailed(progress, ex.Message, onProgressChanged);
            return CollectionDeleteOutcome.Failed;
        }
    }

    /// <summary>
    /// 同步单个平台：获取凭证 → SDK 调用 → 回报服务端。
    /// </summary>
    private async Task SyncOneAsync(
        SyncTarget target,
        PlatformSyncProgress progress,
        string? coverImagePath,
        string title,
        string? description,
        Action<PlatformSyncProgress>? onProgressChanged,
        CancellationToken ct)
    {
        try
        {
            // ── 1. 获取会话凭证 ──
            UpdateStep(progress, PlatformSyncStep.FetchingCredentials, onProgressChanged);
            var creds = await ApiClient.PostAsync<PlatformSessionCredentials>(
                "/api/platform-sessions/credentials",
                new GetCredentialsRequest(target.SessionId));

            if (creds?.Cookie == null)
            {
                SetFailed(progress, "未找到登录凭证，请重新登录该平台", onProgressChanged);
                await ReportAsync(target.PlatformId, false, null, progress.ErrorMsg);
                return;
            }

            // ── 2. 路由到平台 SDK ──
            switch (target.PlatformKey)
            {
                case PlatformNames.Douyin:
                    await SyncDouyinAsync(target, progress, creds, coverImagePath,
                        title, description, onProgressChanged, ct);
                    break;

                case PlatformNames.WechatVideo:
                    await SyncWechatVideoAsync(target, progress, creds,
                        title, description, onProgressChanged, ct);
                    break;

                case PlatformNames.Kuaishou:
                    await SyncKuaishouAsync(target, progress, creds,
                        title, description, onProgressChanged, ct);
                    break;

                case PlatformNames.Xiaohongshu:
                    await SyncXiaohongshuAsync(target, progress, creds,
                        title, description, onProgressChanged, ct);
                    break;

                case PlatformNames.Toutiao:
                    if (target.IsArticle)
                        await SyncToutiaoArticleAsync(target, progress, creds, coverImagePath,
                            title, onProgressChanged, ct);
                    else
                        await SyncToutiaoAsync(target, progress, creds, coverImagePath,
                            title, description, onProgressChanged, ct);
                    break;

                default:
                    // 其他平台 SDK 尚未实现，标记跳过
                    SetSkipped(progress, $"{target.DisplayName} SDK 尚未实现", onProgressChanged);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            SetFailed(progress, "操作已取消", onProgressChanged);
        }
        catch (Exception ex)
        {
            SetFailed(progress, ex.Message, onProgressChanged);
            await TryReportAsync(target.PlatformId, false, null, progress.ErrorMsg);
        }
    }

    // ═══ Douyin（抖音：STS + 封面上传 + Detail 回查 CDN URL，四平台中最重） ═══

    /// <summary>
    /// 抖音合集创建管线：RefreshSts → UploadCover → Create → Detail(取CDN封面URL) → Report。
    /// </summary>
    private async Task SyncDouyinAsync(
        SyncTarget target,
        PlatformSyncProgress progress,
        PlatformSessionCredentials creds,
        string? coverImagePath,
        string title,
        string? description,
        Action<PlatformSyncProgress>? onProgressChanged,
        CancellationToken ct)
    {
        var options = new PlatformOptions { Environment = CredentialFields.EnvironmentSnapshot(creds.ExtraDataJson) };
        var dy = new DouyinPlatform(options);

        // 解析 ExtraDataJson 中的安全 token
        var extra = ParseExtraData(creds.ExtraDataJson);
        var csrfToken = extra.GetValueOrDefault("csrfToken")?.ToString();
        var msToken = extra.GetValueOrDefault("msToken")?.ToString();
        var aBogus = extra.GetValueOrDefault("aBogus")?.ToString();

        // ── 2a. RefreshSts ──
        UpdateStep(progress, PlatformSyncStep.RefreshingSts, onProgressChanged);
        var sts = await dy.Auth.RefreshStsAsync(creds.Cookie!, msToken, aBogus, ct);
        if (sts == null)
        {
            SetFailed(progress, "STS 凭证刷新失败，请重新登录抖音", onProgressChanged);
            await ReportAsync(target.PlatformId, false, null, progress.ErrorMsg);
            return;
        }

        // ── 2b. UploadCover (if cover image provided) ──
        string? coverTosKey = null;
        if (!string.IsNullOrWhiteSpace(coverImagePath) && File.Exists(coverImagePath))
        {
            UpdateStep(progress, PlatformSyncStep.UploadingCover, onProgressChanged);
            var imageData = await File.ReadAllBytesAsync(coverImagePath, ct);
            var fileName = Path.GetFileName(coverImagePath);

            coverTosKey = await dy.Images.UploadAsync(
                imageData, fileName,
                sts.AccessKeyId, sts.SecretAccessKey, sts.SessionToken, ct);

            if (coverTosKey == null)
            {
                SetFailed(progress, "封面图上传 TOS 失败", onProgressChanged);
                await ReportAsync(target.PlatformId, false, null, progress.ErrorMsg);
                return;
            }
        }

        // ── 2c. Create collection ──
        UpdateStep(progress, PlatformSyncStep.Creating, onProgressChanged);
        var createInfo = new CreateCollectionInfo(
            Title: title,
            Description: description,
            CoverTosKey: coverTosKey,
            ItemIds: null,
            CsrfToken: csrfToken,
            MsToken: msToken,
            ABogus: aBogus);

        var result = await dy.Collections.CreateAsync(creds.Cookie!, createInfo, ct);

        if (!result.Success)
        {
            SetFailed(progress, result.ErrorMessage ?? "创建合集失败", onProgressChanged);
            await ReportAsync(target.PlatformId, false, null, result.ErrorMessage);
            return;
        }

        progress.PlatformCollectionId = result.PlatformCollectionId;
        Log.Debug("SyncDouyin CreateAsync OK: mixId={MixId}, coverTosKey={CoverTosKey}",
            result.PlatformCollectionId, coverTosKey);

        // ── 2d. Fetch detail to get CDN cover URL (not TOS key) ──
        string? coverUrl = null;
        try
        {
            var detail = await dy.Collections.DetailAsync(
                creds.Cookie!, result.PlatformCollectionId,
                csrfToken, msToken, aBogus, ct);

            if (detail != null)
            {
                // 优先使用可访问的 CDN URL（url_list[0]）
                coverUrl = detail.CoverCdnUrl ?? coverTosKey;
                // 用平台返回的真实 title/description 覆盖用户输入
                title = detail.MixName;
                description = detail.Description;
            }
            else
            {
                coverUrl = coverTosKey;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "SyncDouyin DetailAsync failed, fallback to coverTosKey");
            coverUrl = coverTosKey;
        }

        UpdateStep(progress, PlatformSyncStep.Created, onProgressChanged);

        // ── 3. Report to server ──
        await ReportAsync(target.PlatformId, true, result.PlatformCollectionId, null,
            title: title, description: description, coverImage: coverUrl);
    }

    /// <summary>
    /// 抖音合集更新管线：RefreshSts → (新封面时)UploadCover → Update → Detail(CDN URL) → Report。
    /// </summary>
    private async Task UpdateDouyinAsync(
        UpdateTarget target,
        PlatformSyncProgress progress,
        PlatformSessionCredentials creds,
        string? newCoverImagePath,
        string title,
        string? description,
        Action<PlatformSyncProgress>? onProgressChanged,
        CancellationToken ct)
    {
        var options = new PlatformOptions { Environment = CredentialFields.EnvironmentSnapshot(creds.ExtraDataJson) };
        var dy = new DouyinPlatform(options);

        var extra = ParseExtraData(creds.ExtraDataJson);
        var csrfToken = extra.GetValueOrDefault("csrfToken")?.ToString();
        var msToken = extra.GetValueOrDefault("msToken")?.ToString();
        var aBogus = extra.GetValueOrDefault("aBogus")?.ToString();

        // ── RefreshSts ──
        UpdateStep(progress, PlatformSyncStep.RefreshingSts, onProgressChanged);
        var sts = await dy.Auth.RefreshStsAsync(creds.Cookie!, msToken, aBogus, ct);
        if (sts == null)
        {
            SetFailed(progress, "STS 凭证刷新失败，请重新登录抖音", onProgressChanged);
            await ReportAsync(target.PlatformId, false, null, progress.ErrorMsg);
            return;
        }

        // ── UploadCover (only if new cover image provided) ──
        string? coverTosKey = null;
        bool coverChanged = false;
        if (!string.IsNullOrWhiteSpace(newCoverImagePath) && File.Exists(newCoverImagePath))
        {
            UpdateStep(progress, PlatformSyncStep.UploadingCover, onProgressChanged);
            var imageData = await File.ReadAllBytesAsync(newCoverImagePath, ct);
            var fileName = Path.GetFileName(newCoverImagePath);

            coverTosKey = await dy.Images.UploadAsync(
                imageData, fileName,
                sts.AccessKeyId, sts.SecretAccessKey, sts.SessionToken, ct);

            if (coverTosKey == null)
            {
                SetFailed(progress, "封面图上传 TOS 失败", onProgressChanged);
                await ReportAsync(target.PlatformId, false, null, progress.ErrorMsg);
                return;
            }
            coverChanged = true;
        }

        // ── Update collection ──
        UpdateStep(progress, PlatformSyncStep.Creating, onProgressChanged);
        var updateInfo = new UpdateCollectionInfo(
            PlatformCollectionId: target.PlatformCollectionId,
            Title: title,
            Description: description,
            CoverTosKey: coverTosKey,
            CsrfToken: csrfToken,
            MsToken: msToken,
            ABogus: aBogus);

        var result = await dy.Collections.UpdateAsync(creds.Cookie!, updateInfo, ct);

        if (!result.Success)
        {
            SetFailed(progress, result.ErrorMessage ?? "更新合集失败", onProgressChanged);
            await ReportAsync(target.PlatformId, false, null, result.ErrorMessage);
            return;
        }

        Log.Debug("UpdateDouyin UpdateAsync OK: mixId={MixId}, coverChanged={CoverChanged}",
            target.PlatformCollectionId, coverChanged);

        // ── Fetch detail to get CDN cover URL (if cover changed) ──
        string? coverUrl = null;
        if (coverChanged)
        {
            try
            {
                var detail = await dy.Collections.DetailAsync(
                    creds.Cookie!, target.PlatformCollectionId,
                    csrfToken, msToken, aBogus, ct);

                if (detail != null)
                {
                    coverUrl = detail.CoverCdnUrl ?? coverTosKey;
                    Log.Debug("UpdateDouyin DetailAsync OK: CoverCdnUrl={CoverCdnUrl}",
                        detail.CoverCdnUrl ?? "NULL");
                    title = detail.MixName;
                    description = detail.Description;
                }
                else
                {
                    coverUrl = coverTosKey;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "UpdateDouyin DetailAsync failed, using coverTosKey fallback");
                coverUrl = coverTosKey;
            }
        }

        UpdateStep(progress, PlatformSyncStep.Created, onProgressChanged);

        // ── Report to server ──
        await ReportAsync(target.PlatformId, true, target.PlatformCollectionId, null,
            title: title, description: description, coverImage: coverUrl);
    }

    /// <summary>抖音合集删除：POST /web/api/mix/destroy。</summary>
    private async Task DeleteDouyinAsync(
        PlatformSyncProgress progress,
        PlatformSessionCredentials creds,
        string platformCollectionId,
        Action<PlatformSyncProgress>? onProgressChanged,
        CancellationToken ct)
    {
        var options = new PlatformOptions { Environment = CredentialFields.EnvironmentSnapshot(creds.ExtraDataJson) };
        var dy = new DouyinPlatform(options);

        var extra = ParseExtraData(creds.ExtraDataJson);
        var csrfToken = extra.GetValueOrDefault("csrfToken")?.ToString();
        var msToken = extra.GetValueOrDefault("msToken")?.ToString();
        var aBogus = extra.GetValueOrDefault("aBogus")?.ToString();

        UpdateStep(progress, PlatformSyncStep.Deleting, onProgressChanged);
        // 删除单独一个 step —— 借用 Creating 会让用户在删除时看到「创建中…」

        var info = new DeleteCollectionInfo(
            PlatformCollectionId: platformCollectionId,
            CsrfToken: csrfToken,
            MsToken: msToken,
            ABogus: aBogus);

        var result = await dy.Collections.DeleteAsync(creds.Cookie!, info, ct);

        if (result.Success)
        {
            UpdateStep(progress, PlatformSyncStep.Deleted, onProgressChanged);
            // 同上，不能借用 Created（否则删完显示「已创建」）
        }
        else
        {
            SetFailed(progress, result.ErrorMessage ?? "删除合集失败", onProgressChanged);
        }
    }

    // ═══ WechatVideo（视频号：device_id 复用 CsrfToken 字段；无封面） ═══

    /// <summary>
    /// 视频号合集创建 — 比抖音简单：
    /// 无需 STS 刷新、无需封面上传、无需 Detail 回查。
    /// device_id 从 ExtraDataJson 读取，伪装成 CsrfToken 传入 SDK。
    /// </summary>
    private async Task SyncWechatVideoAsync(
        SyncTarget target,
        PlatformSyncProgress progress,
        PlatformSessionCredentials creds,
        string title,
        string? description,
        Action<PlatformSyncProgress>? onProgressChanged,
        CancellationToken ct)
    {
        var options = new PlatformOptions { Environment = CredentialFields.EnvironmentSnapshot(creds.ExtraDataJson) };
        var wx = new WechatVideoPlatform(options);

        var extra = ParseExtraData(creds.ExtraDataJson);
        var deviceId = extra.GetValueOrDefault("deviceId")?.ToString() ?? "";

        Log.Debug("SyncWechatVideo ExtraDataJson keys=[{Keys}] deviceIdLen={DeviceIdLen} cookieLen={CookieLen}",
            string.Join(",", extra.Keys), deviceId.Length, creds.Cookie?.Length ?? 0);

        // ── Create collection ──
        UpdateStep(progress, PlatformSyncStep.Creating, onProgressChanged);
        var createInfo = new CreateCollectionInfo(
            Title: title,
            Description: description,
            CoverTosKey: null,    // 视频号不支持合集封面图
            ItemIds: null,
            CsrfToken: deviceId,  // 复用 CsrfToken 字段传 device_id
            MsToken: null,
            ABogus: null);

        var result = await wx.Collections.CreateAsync(creds.Cookie!, createInfo, ct);

        if (!result.Success)
        {
            SetFailed(progress, result.ErrorMessage ?? "创建合集失败", onProgressChanged);
            await ReportAsync(target.PlatformId, false, null, result.ErrorMessage);
            return;
        }

        progress.PlatformCollectionId = result.PlatformCollectionId;
        Log.Debug("SyncWechatVideo CreateAsync OK: collectionId={CollectionId}",
            result.PlatformCollectionId);

        UpdateStep(progress, PlatformSyncStep.Created, onProgressChanged);

        // ── Report to server ──
        await ReportAsync(target.PlatformId, true, result.PlatformCollectionId, null,
            title: title, description: description);
    }

    /// <summary>
    /// 视频号合集更新 — 仅更新标题，不支持封面图、不支持 CDN URL 回查。
    /// </summary>
    private async Task UpdateWechatVideoAsync(
        UpdateTarget target,
        PlatformSyncProgress progress,
        PlatformSessionCredentials creds,
        string title,
        string? description,
        Action<PlatformSyncProgress>? onProgressChanged,
        CancellationToken ct)
    {
        var options = new PlatformOptions { Environment = CredentialFields.EnvironmentSnapshot(creds.ExtraDataJson) };
        var wx = new WechatVideoPlatform(options);

        var extra = ParseExtraData(creds.ExtraDataJson);
        var deviceId = extra.GetValueOrDefault("deviceId")?.ToString() ?? "";

        UpdateStep(progress, PlatformSyncStep.Creating, onProgressChanged);
        var updateInfo = new UpdateCollectionInfo(
            PlatformCollectionId: target.PlatformCollectionId,
            Title: title,
            Description: description,
            CoverTosKey: null,
            CsrfToken: deviceId,
            MsToken: null,
            ABogus: null);

        var result = await wx.Collections.UpdateAsync(creds.Cookie!, updateInfo, ct);

        if (!result.Success)
        {
            SetFailed(progress, result.ErrorMessage ?? "更新合集失败", onProgressChanged);
            await ReportAsync(target.PlatformId, false, null, result.ErrorMessage);
            return;
        }

        Log.Debug("UpdateWechatVideo UpdateAsync OK: collectionId={CollectionId}",
            target.PlatformCollectionId);
        UpdateStep(progress, PlatformSyncStep.Created, onProgressChanged);

        await ReportAsync(target.PlatformId, true, target.PlatformCollectionId, null,
            title: title, description: description);
    }

    /// <summary>视频号合集删除 — POST /collection/delete_collection</summary>
    private async Task DeleteWechatVideoAsync(
        PlatformSyncProgress progress,
        PlatformSessionCredentials creds,
        string platformCollectionId,
        Action<PlatformSyncProgress>? onProgressChanged,
        CancellationToken ct)
    {
        var options = new PlatformOptions { Environment = CredentialFields.EnvironmentSnapshot(creds.ExtraDataJson) };
        var wx = new WechatVideoPlatform(options);

        var extra = ParseExtraData(creds.ExtraDataJson);
        var deviceId = extra.GetValueOrDefault("deviceId")?.ToString() ?? "";

        UpdateStep(progress, PlatformSyncStep.Deleting, onProgressChanged);
        var info = new DeleteCollectionInfo(
            PlatformCollectionId: platformCollectionId,
            CsrfToken: deviceId,
            MsToken: null,
            ABogus: null);

        var result = await wx.Collections.DeleteAsync(creds.Cookie!, info, ct);

        if (result.Success)
        {
            Log.Debug("DeleteWechatVideo DeleteAsync OK: collectionId={CollectionId}",
                platformCollectionId);
            UpdateStep(progress, PlatformSyncStep.Deleted, onProgressChanged);
        }
        else
        {
            SetFailed(progress, result.ErrorMessage ?? "删除合集失败", onProgressChanged);
        }
    }

    // ═══ Kuaishou（快手：api_ph 复用 CsrfToken 字段；无封面） ═══

    /// <summary>
    /// 快手合集创建 — 比抖音简单：
    /// 无需 STS 刷新、无需封面上传（快手不支持合集封面）、无需 Detail 回查。
    /// api_ph 从 ExtraDataJson 读取，复用 CsrfToken 字段传入 SDK。
    /// </summary>
    private async Task SyncKuaishouAsync(
        SyncTarget target,
        PlatformSyncProgress progress,
        PlatformSessionCredentials creds,
        string title,
        string? description,
        Action<PlatformSyncProgress>? onProgressChanged,
        CancellationToken ct)
    {
        // sig3/sig4 必须先预热签名宿主（模块只在页面 webpack 里，纯 C# 无法复算）。
        await KuaishouSignerService.PrepareAsync(target.SessionId, ct);
        var options = new PlatformOptions
        {
            Environment = CredentialFields.EnvironmentSnapshot(creds.ExtraDataJson),
            SessionId = target.SessionId,
        };
        var ks = new KuaishouPlatform(options);

        var extra = ParseExtraData(creds.ExtraDataJson);
        var apiPh = extra.GetValueOrDefault("apiPh")?.ToString();

        Log.Debug("SyncKuaishou ExtraDataJson keys=[{Keys}] hasApiPh={HasApiPh} cookieLen={CookieLen}",
            string.Join(",", extra.Keys), !string.IsNullOrWhiteSpace(apiPh),
            creds.Cookie?.Length ?? 0);

        // ── Create collection ──
        UpdateStep(progress, PlatformSyncStep.Creating, onProgressChanged);
        var createInfo = new CreateCollectionInfo(
            Title: title,
            Description: description,
            CoverTosKey: null,    // 快手不支持合集封面图
            ItemIds: null,
            CsrfToken: apiPh,     // 复用 CsrfToken 字段传 api_ph
            MsToken: null,
            ABogus: null);

        var result = await ks.Collections.CreateAsync(creds.Cookie!, createInfo, ct);

        if (!result.Success)
        {
            SetFailed(progress, result.ErrorMessage ?? "创建合集失败", onProgressChanged);
            await ReportAsync(target.PlatformId, false, null, result.ErrorMessage);
            return;
        }

        progress.PlatformCollectionId = result.PlatformCollectionId;
        Log.Debug("SyncKuaishou CreateAsync OK: collectionId={CollectionId}",
            result.PlatformCollectionId);

        UpdateStep(progress, PlatformSyncStep.Created, onProgressChanged);

        // ── Report to server ──
        await ReportAsync(target.PlatformId, true, result.PlatformCollectionId, null,
            title: title, description: description);
    }

    /// <summary>
    /// 快手合集更新 — 仅更新标题和描述。
    /// 快手不支持合集封面图、不支持移入/移出作品。
    /// </summary>
    private async Task UpdateKuaishouAsync(
        UpdateTarget target,
        PlatformSyncProgress progress,
        PlatformSessionCredentials creds,
        string title,
        string? description,
        Action<PlatformSyncProgress>? onProgressChanged,
        CancellationToken ct)
    {
        await KuaishouSignerService.PrepareAsync(target.SessionId, ct);
        var options = new PlatformOptions
        {
            Environment = CredentialFields.EnvironmentSnapshot(creds.ExtraDataJson),
            SessionId = target.SessionId,
        };
        var ks = new KuaishouPlatform(options);

        var extra = ParseExtraData(creds.ExtraDataJson);
        var apiPh = extra.GetValueOrDefault("apiPh")?.ToString();

        UpdateStep(progress, PlatformSyncStep.Creating, onProgressChanged);
        var updateInfo = new UpdateCollectionInfo(
            PlatformCollectionId: target.PlatformCollectionId,
            Title: title,
            Description: description,
            CoverTosKey: null,
            CsrfToken: apiPh,     // 复用 CsrfToken 字段传 api_ph
            MsToken: null,
            ABogus: null);

        var result = await ks.Collections.UpdateAsync(creds.Cookie!, updateInfo, ct);

        if (!result.Success)
        {
            SetFailed(progress, result.ErrorMessage ?? "更新合集失败", onProgressChanged);
            await ReportAsync(target.PlatformId, false, null, result.ErrorMessage);
            return;
        }

        Log.Debug("UpdateKuaishou UpdateAsync OK: collectionId={CollectionId}",
            target.PlatformCollectionId);
        UpdateStep(progress, PlatformSyncStep.Created, onProgressChanged);

        await ReportAsync(target.PlatformId, true, target.PlatformCollectionId, null,
            title: title, description: description);
    }

    /// <summary>快手合集删除 — POST /rest/cp/works/v2/collection/delete</summary>
    private async Task DeleteKuaishouAsync(
        PlatformSyncProgress progress,
        PlatformSessionCredentials creds,
        string platformCollectionId,
        Action<PlatformSyncProgress>? onProgressChanged,
        CancellationToken ct)
    {
        await KuaishouSignerService.PrepareAsync(creds.Id, ct);
        var options = new PlatformOptions
        {
            Environment = CredentialFields.EnvironmentSnapshot(creds.ExtraDataJson),
            SessionId = creds.Id,
        };
        var ks = new KuaishouPlatform(options);

        var extra = ParseExtraData(creds.ExtraDataJson);
        var apiPh = extra.GetValueOrDefault("apiPh")?.ToString();

        UpdateStep(progress, PlatformSyncStep.Deleting, onProgressChanged);
        var info = new DeleteCollectionInfo(
            PlatformCollectionId: platformCollectionId,
            CsrfToken: apiPh,     // 复用 CsrfToken 字段传 api_ph
            MsToken: null,
            ABogus: null);

        var result = await ks.Collections.DeleteAsync(creds.Cookie!, info, ct);

        if (result.Success)
        {
            Log.Debug("DeleteKuaishou DeleteAsync OK: collectionId={CollectionId}",
                platformCollectionId);
            UpdateStep(progress, PlatformSyncStep.Deleted, onProgressChanged);
        }
        else
        {
            SetFailed(progress, result.ErrorMessage ?? "删除合集失败", onProgressChanged);
        }
    }

    // ═══ Xiaohongshu（小红书：无封面/无 STS，创建最简） ═══

    /// <summary>
    /// 小红书合集创建 — 比抖音更简单：
    /// 无需 STS 刷新、封面上传、Detail 回查。
    /// x-s 签名经 XhsPlatform.SetSigner 走 App 层 WebView2（本方法只传会话上下文）。
    ///
    /// 先查后建（2026-09-12）：创建前先按标题列账号已有合集 —— 命中即收养其 ID。
    /// 平台侧已有同名合集时创建必然失败（重名/重复），且「建成功但回报失败」的重试
    /// 会造重复合集；收养让整个流程幂等，也让用户手工建过的合集能被纳管。
    /// </summary>
    private async Task SyncXiaohongshuAsync(
        SyncTarget target,
        PlatformSyncProgress progress,
        PlatformSessionCredentials creds,
        string title,
        string? description,
        Action<PlatformSyncProgress>? onProgressChanged,
        CancellationToken ct)
    {
        // 会话 ID 透传给签名器 —— 签名的 WebView2 档案与请求 Cookie 同账号
        var options = new PlatformOptions
        {
            Environment = CredentialFields.EnvironmentSnapshot(creds.ExtraDataJson),
            SessionId = target.SessionId,
        };
        var xhs = new XhsPlatform(options);

        Log.Debug("SyncXiaohongshu cookieLen={CookieLen}", creds.Cookie?.Length ?? 0);

        // ── 预热签名宿主：x-s 要页面里的 mnsv2，冷启动要建 WebView + 加载页面（十几秒）——
        // 提前做，别压在 HttpClient 超时窗口里。失败不阻断：请求裸发由平台判（会回报 406）。
        UpdateStep(progress, PlatformSyncStep.Creating, onProgressChanged);
        await XhsSignerService.PrepareAsync(target.SessionId, ct);

        // ── 0. 先查：同标题合集已存在（本端曾建过/用户手工建过）→ 收养，不再重复创建 ──
        var existingId = await FindXhsCollectionByNameAsync(xhs, creds.Cookie!, title, ct);
        if (existingId != null)
        {
            Log.Information("SyncXiaohongshu 已有同名合集，直接收养：{Id}", existingId);
            progress.PlatformCollectionId = existingId;
            UpdateStep(progress, PlatformSyncStep.Created, onProgressChanged);
            await ReportAsync(target.PlatformId, true, existingId, null,
                title: title, description: description);
            return;
        }

        // ── 1. 创建 ──
        var createInfo = new CreateCollectionInfo(
            Title: title,
            Description: description,
            CoverTosKey: null,    // 小红书创建合集不支持封面图（更新时支持）
            ItemIds: null,
            CsrfToken: null,
            MsToken: null,
            ABogus: null);

        var result = await xhs.Collections.CreateAsync(creds.Cookie!, createInfo, ct);

        if (!result.Success)
        {
            Log.Warning("SyncXiaohongshu CreateAsync FAILED: {Error}", result.ErrorMessage);
            SetFailed(progress, result.ErrorMessage ?? "创建合集失败", onProgressChanged);
            await ReportAsync(target.PlatformId, false, null, result.ErrorMessage);
            return;
        }

        progress.PlatformCollectionId = result.PlatformCollectionId;
        Log.Debug("SyncXiaohongshu CreateAsync OK: collectionId={CollectionId}",
            result.PlatformCollectionId);

        UpdateStep(progress, PlatformSyncStep.Created, onProgressChanged);

        // ── 2. Report to server ──
        await ReportAsync(target.PlatformId, true, result.PlatformCollectionId, null,
            title: title, description: description);
    }

    /// <summary>
    /// 按标题在账号合集列表里找同名合集（最多翻 5 页 ≈60 条）；找到返回其 ID，否则 null。
    /// 标题比较去首尾空白、区分大小写（平台合集名区分大小写展示）。
    /// 列表调用失败按「没找到」处理（创建流程继续，错误留给创建步骤回报）。
    /// </summary>
    private static async Task<string?> FindXhsCollectionByNameAsync(
        XhsPlatform xhs, string cookie, string title, CancellationToken ct)
    {
        try
        {
            var wanted = title.Trim();
            if (wanted.Length == 0) return null;

            long cursor = 0;
            for (var page = 0; page < 5; page++)
            {
                var list = await xhs.Collections.ListAsync(cookie, cursor, 12, ct);
                Log.Debug("SyncXiaohongshu 已有合集第 {Page} 页 {Count} 个：[{Names}] HasMore={HasMore}",
                    page + 1, list.Items.Count,
                    string.Join(" / ", list.Items.Select(i => i.Title)), list.HasMore);
                var hit = list.Items.FirstOrDefault(i => i.Title.Trim() == wanted);
                if (hit != null) return hit.PlatformCollectionId;
                if (!list.HasMore) break;
                cursor = list.NextCursor;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "SyncXiaohongshu 列表查询失败（按未建处理）");
        }
        return null;
    }

    /// <summary>
    /// 小红书合集更新 — 标题 + 描述。
    /// 小红书创建时无封面，更新时可传 coverTosKey 作为 image.fieldId。
    /// </summary>
    private async Task UpdateXiaohongshuAsync(
        UpdateTarget target,
        PlatformSyncProgress progress,
        PlatformSessionCredentials creds,
        string title,
        string? description,
        Action<PlatformSyncProgress>? onProgressChanged,
        CancellationToken ct)
    {
        var options = new PlatformOptions
        {
            Environment = CredentialFields.EnvironmentSnapshot(creds.ExtraDataJson),
            SessionId = target.SessionId,
        };
        var xhs = new XhsPlatform(options);

        UpdateStep(progress, PlatformSyncStep.Creating, onProgressChanged);
        await XhsSignerService.PrepareAsync(target.SessionId, ct); // 预热签名宿主（冷启动建 WebView + 加载 mnsv2）
        var updateInfo = new UpdateCollectionInfo(
            PlatformCollectionId: target.PlatformCollectionId,
            Title: title,
            Description: description,
            CoverTosKey: null,
            CsrfToken: null,
            MsToken: null,
            ABogus: null);

        var result = await xhs.Collections.UpdateAsync(creds.Cookie!, updateInfo, ct);

        if (!result.Success)
        {
            SetFailed(progress, result.ErrorMessage ?? "更新合集失败", onProgressChanged);
            await ReportAsync(target.PlatformId, false, null, result.ErrorMessage);
            return;
        }

        Log.Debug("UpdateXiaohongshu UpdateAsync OK: collectionId={CollectionId}",
            target.PlatformCollectionId);
        UpdateStep(progress, PlatformSyncStep.Created, onProgressChanged);

        await ReportAsync(target.PlatformId, true, target.PlatformCollectionId, null,
            title: title, description: description);
    }

    /// <summary>小红书合集删除 — POST /api/sns/v1/note/collection/pc/delete</summary>
    private async Task DeleteXiaohongshuAsync(
        PlatformSyncProgress progress,
        PlatformSessionCredentials creds,
        string platformCollectionId,
        Guid sessionId,
        Action<PlatformSyncProgress>? onProgressChanged,
        CancellationToken ct)
    {
        var options = new PlatformOptions
        {
            Environment = CredentialFields.EnvironmentSnapshot(creds.ExtraDataJson),
            SessionId = sessionId,
        };
        var xhs = new XhsPlatform(options);

        UpdateStep(progress, PlatformSyncStep.Deleting, onProgressChanged);
        await XhsSignerService.PrepareAsync(sessionId, ct); // 预热签名宿主（冷启动建 WebView + 加载 mnsv2）
        var info = new DeleteCollectionInfo(
            PlatformCollectionId: platformCollectionId,
            CsrfToken: null,
            MsToken: null,
            ABogus: null);

        var result = await xhs.Collections.DeleteAsync(creds.Cookie!, info, ct);

        if (result.Success)
        {
            Log.Debug("DeleteXiaohongshu DeleteAsync OK: collectionId={CollectionId}",
                platformCollectionId);
            UpdateStep(progress, PlatformSyncStep.Deleted, onProgressChanged);
        }
        else
        {
            SetFailed(progress, result.ErrorMessage ?? "删除合集失败", onProgressChanged);
        }
    }

    // ═══ Toutiao（头条：视频合集；封面必传；CSRF 来自 WebView 抓取） ═══

    /// <summary>
    /// 头条合集创建：CSRF 校验 → 封面上传（getAuthKey + TOS）→ PublishPseries。
    /// 头条没有「合集简介」字段（description 只用于本地记录，不下发）。
    /// </summary>
    private async Task SyncToutiaoAsync(
        SyncTarget target,
        PlatformSyncProgress progress,
        PlatformSessionCredentials creds,
        string? coverImagePath,
        string title,
        string? description,
        Action<PlatformSyncProgress>? onProgressChanged,
        CancellationToken ct)
    {
        var extra = ParseExtraData(creds.ExtraDataJson);
        var csrf = extra.GetValueOrDefault("csrfToken")?.ToString();
        if (string.IsNullOrWhiteSpace(csrf))
        {
            SetFailed(progress, "缺少头条 CSRF 凭证：请打开头条账号窗口（加载完页面后点「完成登录」）刷新凭证，再重试", onProgressChanged);
            await ReportAsync(target.PlatformId, false, null, progress.ErrorMsg);
            return;
        }
        if (string.IsNullOrWhiteSpace(coverImagePath) || !File.Exists(coverImagePath))
        {
            SetFailed(progress, "头条合集需要选择封面图", onProgressChanged);
            await ReportAsync(target.PlatformId, false, null, progress.ErrorMsg);
            return;
        }
        if (string.IsNullOrWhiteSpace(creds.PlatformUid))
        {
            SetFailed(progress, "缺少头条用户 ID，请重新登录头条账号", onProgressChanged);
            await ReportAsync(target.PlatformId, false, null, progress.ErrorMsg);
            return;
        }

        var options = new PlatformOptions
        {
            Environment = CredentialFields.EnvironmentSnapshot(creds.ExtraDataJson),
        };
        var tt = new ToutiaoPlatform(options);

        // ── 封面上传 ──
        UpdateStep(progress, PlatformSyncStep.UploadingCover, onProgressChanged);
        var imageData = await File.ReadAllBytesAsync(coverImagePath, ct);
        var cover = await tt.Images.UploadAsync(
            creds.Cookie!, imageData, creds.PlatformUid!, csrf, ct);
        if (cover is null)
        {
            SetFailed(progress, "头条合集封面上传失败（TOS 凭证或网络异常）", onProgressChanged);
            await ReportAsync(target.PlatformId, false, null, progress.ErrorMsg);
            return;
        }

        // ── 创建合集 ──
        UpdateStep(progress, PlatformSyncStep.Creating, onProgressChanged);
        var createInfo = new CreateCollectionInfo(
            Title: title,
            Description: description,
            CoverTosKey: cover.Uri,
            ItemIds: null,
            CsrfToken: csrf,
            MsToken: null,
            ABogus: null,
            CoverWidth: cover.Width,
            CoverHeight: cover.Height);

        var result = await tt.Collections.CreateAsync(creds.Cookie!, createInfo, ct);
        if (!result.Success)
        {
            SetFailed(progress, result.ErrorMessage ?? "创建合集失败", onProgressChanged);
            await ReportAsync(target.PlatformId, false, null, result.ErrorMessage);
            return;
        }

        progress.PlatformCollectionId = result.PlatformCollectionId;
        UpdateStep(progress, PlatformSyncStep.Created, onProgressChanged);

        var coverUrl = await TryGetToutiaoCoverUrlAsync(
            tt, creds.Cookie!, result.PlatformCollectionId, ct) ?? cover.Uri;
        await ReportAsync(target.PlatformId, true, result.PlatformCollectionId, null,
            title: title, description: description, coverImage: coverUrl);
    }

    /// <summary>头条合集更新：没换封面时不重传，服务内部查详情回填当前封面。</summary>
    private async Task UpdateToutiaoAsync(
        UpdateTarget target,
        PlatformSyncProgress progress,
        PlatformSessionCredentials creds,
        string? newCoverImagePath,
        string title,
        string? description,
        Action<PlatformSyncProgress>? onProgressChanged,
        CancellationToken ct)
    {
        var extra = ParseExtraData(creds.ExtraDataJson);
        var csrf = extra.GetValueOrDefault("csrfToken")?.ToString();
        if (string.IsNullOrWhiteSpace(csrf))
        {
            SetFailed(progress, "缺少头条 CSRF 凭证：请打开头条账号窗口（加载完页面后点「完成登录」）刷新凭证，再重试", onProgressChanged);
            await ReportAsync(target.PlatformId, false, null, progress.ErrorMsg);
            return;
        }

        var options = new PlatformOptions
        {
            Environment = CredentialFields.EnvironmentSnapshot(creds.ExtraDataJson),
        };
        var tt = new ToutiaoPlatform(options);

        string? coverUri = null;
        var coverWidth = 0;
        var coverHeight = 0;
        if (!string.IsNullOrWhiteSpace(newCoverImagePath) && File.Exists(newCoverImagePath))
        {
            if (string.IsNullOrWhiteSpace(creds.PlatformUid))
            {
                SetFailed(progress, "缺少头条用户 ID，请重新登录头条账号", onProgressChanged);
                await ReportAsync(target.PlatformId, false, null, progress.ErrorMsg);
                return;
            }

            UpdateStep(progress, PlatformSyncStep.UploadingCover, onProgressChanged);
            var imageData = await File.ReadAllBytesAsync(newCoverImagePath, ct);
            var cover = await tt.Images.UploadAsync(
                creds.Cookie!, imageData, creds.PlatformUid!, csrf, ct);
            if (cover is null)
            {
                SetFailed(progress, "头条合集封面上传失败（TOS 凭证或网络异常）", onProgressChanged);
                await ReportAsync(target.PlatformId, false, null, progress.ErrorMsg);
                return;
            }
            coverUri = cover.Uri;
            coverWidth = cover.Width;
            coverHeight = cover.Height;
        }

        UpdateStep(progress, PlatformSyncStep.Creating, onProgressChanged);
        var updateInfo = new UpdateCollectionInfo(
            PlatformCollectionId: target.PlatformCollectionId,
            Title: title,
            Description: description,
            CoverTosKey: coverUri,
            CsrfToken: csrf,
            MsToken: null,
            ABogus: null,
            CoverWidth: coverWidth,
            CoverHeight: coverHeight);

        var result = await tt.Collections.UpdateAsync(creds.Cookie!, updateInfo, ct);
        if (!result.Success)
        {
            SetFailed(progress, result.ErrorMessage ?? "更新合集失败", onProgressChanged);
            await ReportAsync(target.PlatformId, false, null, result.ErrorMessage);
            return;
        }

        UpdateStep(progress, PlatformSyncStep.Created, onProgressChanged);
        var coverUrl = await TryGetToutiaoCoverUrlAsync(
            tt, creds.Cookie!, target.PlatformCollectionId, ct) ?? coverUri;
        await ReportAsync(target.PlatformId, true, target.PlatformCollectionId, null,
            title: title, description: description, coverImage: coverUrl);
    }

    /// <summary>头条合集删除 — POST /xigua/api/pSeries/DeletePseries/。</summary>
    private async Task DeleteToutiaoAsync(
        PlatformSyncProgress progress,
        PlatformSessionCredentials creds,
        string platformCollectionId,
        Action<PlatformSyncProgress>? onProgressChanged,
        CancellationToken ct)
    {
        var extra = ParseExtraData(creds.ExtraDataJson);
        var csrf = extra.GetValueOrDefault("csrfToken")?.ToString();
        if (string.IsNullOrWhiteSpace(csrf))
        {
            SetFailed(progress, "缺少头条 CSRF 凭证：请打开头条账号窗口（加载完页面后点「完成登录」）刷新凭证，再重试", onProgressChanged);
            return;
        }

        var options = new PlatformOptions
        {
            Environment = CredentialFields.EnvironmentSnapshot(creds.ExtraDataJson),
        };
        var tt = new ToutiaoPlatform(options);

        UpdateStep(progress, PlatformSyncStep.Deleting, onProgressChanged);
        var info = new DeleteCollectionInfo(
            PlatformCollectionId: platformCollectionId,
            CsrfToken: csrf,
            MsToken: null,
            ABogus: null);

        var result = await tt.Collections.DeleteAsync(creds.Cookie!, info, ct);
        if (result.Success)
        {
            UpdateStep(progress, PlatformSyncStep.Deleted, onProgressChanged);
        }
        else
        {
            SetFailed(progress, result.ErrorMessage ?? "删除合集失败", onProgressChanged);
        }
    }

    /// <summary>创建/更新后回查列表拿带签名的封面 URL（失败不影响主流程）。</summary>
    private static async Task<string?> TryGetToutiaoCoverUrlAsync(
        ToutiaoPlatform tt, string cookie, string albumId, CancellationToken ct)
    {
        try
        {
            var list = await tt.Collections.ListAsync(cookie, 0, 20, ct);
            var hit = list.Items.FirstOrDefault(i => i.PlatformCollectionId == albumId);
            return hit?.CoverUrl;
        }
        catch
        {
            return null;
        }
    }

    // ═══ Toutiao 文章合集（/mp/fe_api/collection/*；封面 /spice/image upload_source=20020010） ═══

    /// <summary>
    /// 头条**文章合集**创建：封面走 /spice/image（不是视频那条 TOS），
    /// 创建接口可能 HTTP 500 但实际成功 —— 由 ToutiaoArticleCollectionService 回查列表判定。
    /// 文章合集没有简介字段，description 不下发。
    /// </summary>
    private async Task SyncToutiaoArticleAsync(
        SyncTarget target,
        PlatformSyncProgress progress,
        PlatformSessionCredentials creds,
        string? coverImagePath,
        string title,
        Action<PlatformSyncProgress>? onProgressChanged,
        CancellationToken ct)
    {
        var extra = ParseExtraData(creds.ExtraDataJson);
        var csrf = extra.GetValueOrDefault("csrfToken")?.ToString();
        if (string.IsNullOrWhiteSpace(csrf))
        {
            SetFailed(progress, "缺少头条 CSRF 凭证：请打开头条账号窗口（加载完页面后点「完成登录」）刷新凭证，再重试", onProgressChanged);
            await ReportAsync(target.PlatformId, false, null, progress.ErrorMsg);
            return;
        }
        if (string.IsNullOrWhiteSpace(coverImagePath) || !File.Exists(coverImagePath))
        {
            SetFailed(progress, "文章合集需要选择封面图", onProgressChanged);
            await ReportAsync(target.PlatformId, false, null, progress.ErrorMsg);
            return;
        }

        var options = new PlatformOptions
        {
            Environment = CredentialFields.EnvironmentSnapshot(creds.ExtraDataJson),
        };
        var tt = new ToutiaoPlatform(options);

        // ── 封面：/spice/image（multipart 字段 image；与视频合集 TOS 不是一条链路） ──
        UpdateStep(progress, PlatformSyncStep.UploadingCover, onProgressChanged);
        var imageData = await File.ReadAllBytesAsync(coverImagePath, ct);
        var cover = await tt.Images.UploadArticleCoverAsync(creds.Cookie!, imageData, csrf, ct);
        if (cover is null)
        {
            SetFailed(progress, "文章合集封面上传失败（/spice/image 或网络异常）", onProgressChanged);
            await ReportAsync(target.PlatformId, false, null, progress.ErrorMsg);
            return;
        }

        // ── 创建（HTTP 500 但已创建的情况由服务内回查列表处理） ──
        UpdateStep(progress, PlatformSyncStep.Creating, onProgressChanged);
        var createInfo = new CreateCollectionInfo(
            Title: title,
            Description: null,
            CoverTosKey: cover.Uri,
            ItemIds: null,
            CsrfToken: csrf,
            MsToken: null,
            ABogus: null,
            CoverWidth: cover.Width,
            CoverHeight: cover.Height,
            CoverUrl: cover.Url,
            CoverOriginUri: cover.OriginUri);

        var result = await tt.ArticleCollections.CreateAsync(creds.Cookie!, createInfo, ct);
        if (!result.Success)
        {
            SetFailed(progress, result.ErrorMessage ?? "创建文章合集失败", onProgressChanged);
            await ReportAsync(target.PlatformId, false, null, result.ErrorMessage);
            return;
        }

        progress.PlatformCollectionId = result.PlatformCollectionId;
        UpdateStep(progress, PlatformSyncStep.Created, onProgressChanged);
        await ReportAsync(target.PlatformId, true, result.PlatformCollectionId, null,
            title: title, description: null, coverImage: cover.Url);
    }

    /// <summary>头条文章合集更新：没换封面时由服务内部查详情回填当前封面 + item_ids。</summary>
    private async Task UpdateToutiaoArticleAsync(
        UpdateTarget target,
        PlatformSyncProgress progress,
        PlatformSessionCredentials creds,
        string? newCoverImagePath,
        string title,
        Action<PlatformSyncProgress>? onProgressChanged,
        CancellationToken ct)
    {
        var extra = ParseExtraData(creds.ExtraDataJson);
        var csrf = extra.GetValueOrDefault("csrfToken")?.ToString();
        if (string.IsNullOrWhiteSpace(csrf))
        {
            SetFailed(progress, "缺少头条 CSRF 凭证：请打开头条账号窗口（加载完页面后点「完成登录」）刷新凭证，再重试", onProgressChanged);
            await ReportAsync(target.PlatformId, false, null, progress.ErrorMsg);
            return;
        }

        var options = new PlatformOptions
        {
            Environment = CredentialFields.EnvironmentSnapshot(creds.ExtraDataJson),
        };
        var tt = new ToutiaoPlatform(options);

        string? coverUri = null;
        string? coverUrl = null;
        string? coverOriginUri = null;
        var coverWidth = 0;
        var coverHeight = 0;
        if (!string.IsNullOrWhiteSpace(newCoverImagePath) && File.Exists(newCoverImagePath))
        {
            UpdateStep(progress, PlatformSyncStep.UploadingCover, onProgressChanged);
            var imageData = await File.ReadAllBytesAsync(newCoverImagePath, ct);
            var cover = await tt.Images.UploadArticleCoverAsync(creds.Cookie!, imageData, csrf, ct);
            if (cover is null)
            {
                SetFailed(progress, "文章合集封面上传失败（/spice/image 或网络异常）", onProgressChanged);
                await ReportAsync(target.PlatformId, false, null, progress.ErrorMsg);
                return;
            }
            coverUri = cover.Uri;
            coverUrl = cover.Url;
            coverOriginUri = cover.OriginUri;
            coverWidth = cover.Width;
            coverHeight = cover.Height;
        }

        UpdateStep(progress, PlatformSyncStep.Creating, onProgressChanged);
        var updateInfo = new UpdateCollectionInfo(
            PlatformCollectionId: target.PlatformCollectionId,
            Title: title,
            Description: null,
            CoverTosKey: coverUri,
            CsrfToken: csrf,
            MsToken: null,
            ABogus: null,
            CoverWidth: coverWidth,
            CoverHeight: coverHeight,
            CoverUrl: coverUrl,
            CoverOriginUri: coverOriginUri);

        var result = await tt.ArticleCollections.UpdateAsync(creds.Cookie!, updateInfo, ct);
        if (!result.Success)
        {
            SetFailed(progress, result.ErrorMessage ?? "更新文章合集失败", onProgressChanged);
            await ReportAsync(target.PlatformId, false, null, result.ErrorMessage);
            return;
        }

        UpdateStep(progress, PlatformSyncStep.Created, onProgressChanged);
        await ReportAsync(target.PlatformId, true, target.PlatformCollectionId, null,
            title: title, description: null, coverImage: coverUrl);
    }

    /// <summary>头条文章合集删除 — POST /mp/fe_api/collection/delete（服务内部处理审核版本重删）。</summary>
    private async Task DeleteToutiaoArticleAsync(
        PlatformSyncProgress progress,
        PlatformSessionCredentials creds,
        string platformCollectionId,
        Action<PlatformSyncProgress>? onProgressChanged,
        CancellationToken ct)
    {
        var extra = ParseExtraData(creds.ExtraDataJson);
        var csrf = extra.GetValueOrDefault("csrfToken")?.ToString();
        if (string.IsNullOrWhiteSpace(csrf))
        {
            SetFailed(progress, "缺少头条 CSRF 凭证：请打开头条账号窗口（加载完页面后点「完成登录」）刷新凭证，再重试", onProgressChanged);
            return;
        }

        var options = new PlatformOptions
        {
            Environment = CredentialFields.EnvironmentSnapshot(creds.ExtraDataJson),
        };
        var tt = new ToutiaoPlatform(options);

        UpdateStep(progress, PlatformSyncStep.Deleting, onProgressChanged);
        var info = new DeleteCollectionInfo(
            PlatformCollectionId: platformCollectionId,
            CsrfToken: csrf,
            MsToken: null,
            ABogus: null);

        var result = await tt.ArticleCollections.DeleteAsync(creds.Cookie!, info, ct);
        if (result.Success)
            UpdateStep(progress, PlatformSyncStep.Deleted, onProgressChanged);
        else
            SetFailed(progress, result.ErrorMessage ?? "删除文章合集失败", onProgressChanged);
    }

    // ═══ Helpers ═══

    private static void UpdateStep(PlatformSyncProgress progress, PlatformSyncStep step,
        Action<PlatformSyncProgress>? callback)
    {
        progress.Step = step;
        callback?.Invoke(progress);
    }

    private static void SetFailed(PlatformSyncProgress progress, string msg,
        Action<PlatformSyncProgress>? callback)
    {
        progress.Step = PlatformSyncStep.Failed;
        progress.ErrorMsg = msg;
        callback?.Invoke(progress);
    }

    private static void SetSkipped(PlatformSyncProgress progress, string msg,
        Action<PlatformSyncProgress>? callback)
    {
        progress.Step = PlatformSyncStep.Skipped;
        progress.ErrorMsg = msg;
        callback?.Invoke(progress);
    }

    private static async Task ReportAsync(Guid platformId, bool success,
        string? platformCollectionId, string? errorMsg,
        string? title = null, string? description = null, string? coverImage = null)
    {
        try
        {
            await ApiClient.PostAsync<object>("/api/collections/report",
                new ReportCollectionRequest(platformId, success,
                    success ? platformCollectionId : null,
                    success ? null : errorMsg,
                    title, description, coverImage));
        }
        catch
        {
            // 回报失败不阻塞 — 下次列表刷新时用户会看到 pending 状态
        }
    }

    private static async Task TryReportAsync(Guid platformId, bool success,
        string? platformCollectionId, string? errorMsg,
        string? title = null, string? description = null, string? coverImage = null)
    {
        try { await ReportAsync(platformId, success, platformCollectionId, errorMsg,
            title, description, coverImage); }
        catch { /* best effort */ }
    }

    /// <summary>解析 ExtraDataJson 为字典。</summary>
    private static Dictionary<string, object?> ParseExtraData(string? extraDataJson)
    {
        if (string.IsNullOrWhiteSpace(extraDataJson)) return new();
        try
        {
            var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(extraDataJson);
            return dict ?? new();
        }
        catch { return new(); }
    }
}

/// <summary>
/// 单个平台的同步目标 — platform record ID + session ID + display info。
/// </summary>
public sealed record SyncTarget(
    Guid PlatformId,           // CollectionPlatform 记录 ID（用于回报）
    string PlatformKey,        // 平台标识
    string DisplayName,        // 平台中文名（用于 UI 显示）
    string AccountName,        // 账号名（用户备注或平台昵称）
    Guid SessionId,            // 平台会话 ID（用于获取凭证）
    bool IsArticle = false     // 头条：文章合集走 /mp/fe_api，视频合集走 pSeries
);

/// <summary>
/// 单个平台的删除目标 — platformCollectionId + session ID + display info。
/// </summary>
public sealed record DeleteTarget(
    string PlatformKey,                // 平台标识
    string DisplayName,                // 平台中文名
    string AccountName,                // 账号名
    string PlatformCollectionId,       // 平台合集 ID（mix_id）
    Guid SessionId,                    // 平台会话 ID（用于获取凭证）
    bool IsArticle = false             // 头条：文章合集走 /mp/fe_api，视频合集走 pSeries
);

/// <summary>
/// 一条删除目标的结果：进度对象（喂给 chip 面板）+ 归一化结果（喂给「全删光才真删」的判定）。
/// </summary>
public sealed record PlatformDeleteReport(
    PlatformSyncProgress Progress,
    CollectionDeleteOutcome Outcome
);

/// <summary>
/// 单个平台的更新目标 — PlatformRecord ID + platformCollectionId + session ID + display info。
/// </summary>
public sealed record UpdateTarget(
    Guid PlatformId,                   // CollectionPlatform 记录 ID（用于回报）
    string PlatformKey,                // 平台标识
    string DisplayName,                // 平台中文名
    string AccountName,                // 账号名
    string PlatformCollectionId,       // 平台合集 ID（mix_id）— 必填
    Guid SessionId,                    // 平台会话 ID（用于获取凭证）
    bool IsArticle = false             // 头条：文章合集走 /mp/fe_api，视频合集走 pSeries
);
