using System.Globalization;
using System.Text.Json;
using CreatFlow.Platforms.Bilibili;
using CreatFlow.Platforms.Core;
using CreatFlow.Platforms.Douyin;
using CreatFlow.Platforms.Douyin.Handlers;
using CreatFlow.Platforms.Kuaishou;
using CreatFlow.Platforms.Toutiao;
using CreatFlow.Platforms.WechatVideo;
using CreatFlow.Platforms.Xiaohongshu;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 桌面端发布管线 —— submit → upload+publish（本地 SDK） → report。
/// 对标 <see cref="CollectionSyncService"/> 的编排模式：获取凭证 → 调本地 SDK → 回报服务器。
///
/// 流程：
///   1. POST /api/publish/submit → 创建 DB 记录
///   2. 逐平台取会话凭证 → STS/authKey → 上传视频 → 发布
///   3. 每平台完成后 POST /api/publish/report → 更新服务端状态
///
/// 所有平台 SDK 调用在桌面端本地执行（**视频文件不过服务器**）。
///
/// 自 CreatFlow.App/Services/PublishPipeline.cs 移植（2026-09-17）。移植时改掉的四件事：
///   ① 去掉 <c>using CreatFlow.App.Views</c> 与 <c>WebViewLoginWindow.HiddenInstance</c>，
///      改依赖注入 <see cref="DouyinWebExecutor"/>（按会话取宿主，不再全局单例串号）；
///   ② 平台参数从 <c>Dictionary&lt;string, object?&gt;</c> 换成有类型的 <see cref="PublishPayloadBase"/>；
///   ③ 可见性/声明映射集中到 <see cref="PublishMapping"/>（修掉小红书隐私事故，缺陷 L11）；
///   ④ 视频号 <c>shootInfo</c> 用真实协议结构（老版字段名与结构双错且从未被读取，缺陷 L1）。
/// </summary>
public sealed class PublishPipeline
{
    /// <summary>单平台进度回调：(平台键, 阶段, 百分比, 文案)</summary>
    public Action<string, string, int, string?>? OnPlatformProgress;

    /// <summary>单平台完成回调：(平台键, 成功, 视频 ID, 错误)</summary>
    public Action<string, bool, string?, string?>? OnPlatformComplete;

    /// <summary>全部完成回调：(成功数, 失败数)</summary>
    public Action<int, int>? OnAllComplete;

    /// <summary>声音/通知回调（true = 成功音，false = 失败音）。P3-5 托盘通知接这里。</summary>
    public Action<bool>? OnNotify;

    /// <summary>元数据探测（视频号无自定义封面时抽首帧当封面用）。可为 null。</summary>
    public VideoMetaProbe? MetaProbe { get; init; }

    // ═══════════════════════════════════════════════════════════════
    //  提交（只写 DB，不上传）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 提交发布任务到服务端（只建记录）。UI 用这个拿到快速响应，
    /// 再由队列消费（<see cref="RunAsync"/> 或队列页驱动的重试）。
    /// </summary>
    public async Task<PublishSubmitResult?> SubmitAsync(
        SubmitPublishRequest request, CancellationToken ct = default)
    {
        OnPlatformProgress?.Invoke("", "submitting", 0, "提交发布任务...");
        var resp = await ApiClient.PostAsync<PublishSubmitResult>("/api/publish/submit", request);
        if (resp is null)
        {
            Log.Error("[Publish] 提交失败（API 返回 null）：{File}", request.VideoFileName);
            OnPlatformProgress?.Invoke("", "submit_failed", 0, "提交失败");
            return null;
        }
        Log.Information("[Publish] 已提交 taskId={TaskId}, file={File}, platforms={Count}",
            resp.TaskId, request.VideoFileName, resp.Platforms.Count);
        return resp;
    }

    /// <summary>提交并立即执行全部平台（单条走完，不排队）。</summary>
    public async Task<PublishSubmitResult?> RunAsync(
        SubmitPublishRequest request, CancellationToken ct = default)
    {
        var resp = await SubmitAsync(request, ct);
        if (resp is null) return null;

        var success = 0;
        var failed = 0;

        // 不同平台之间独立，无需错开（限速是「同账号前序任务」的事，由队列层管）
        foreach (var platform in resp.Platforms)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = await ExecutePlatformAsync(request.VideoFilePath, platform, ct);
                await ReportAsync(platform.Id, result.Success, result.VideoId,
                    result.Error, result.ExtraDataJson);

                if (result.Success) success++; else failed++;
                OnPlatformComplete?.Invoke(platform.PlatformKey,
                    result.Success, result.VideoId, result.Error);
                // 失败立即响 —— 用户要行动（重试/重新登录），不等其它平台
                if (!result.Success) Notify(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Error(ex, "[Publish] 平台 {Platform} 执行异常：{Error}",
                    platform.PlatformKey, ex.Message);
                failed++;
                await TryReportAsync(platform.Id, false, null, ex.Message);
                OnPlatformComplete?.Invoke(platform.PlatformKey, false, null, ex.Message);
                Notify(false);
            }
        }

        OnAllComplete?.Invoke(success, failed);
        // 全成功播一次成功音（失败已在失败瞬间播过，不重复）
        if (failed == 0 && success > 0) Notify(true);
        return resp;
    }

    /// <summary>
    /// 重试单条平台记录（队列页用）。不 submit —— 记录已存在。
    /// **必须回报服务端**，否则队列页的轮询同步会把本地 Completed/Failed 覆盖回 Waiting。
    /// </summary>
    public async Task<(bool Success, string? VideoId, string? Error, string? ExtraDataJson)>
        RetryPlatformAsync(string videoFilePath, PublishPlatformItem platform, CancellationToken ct = default)
    {
        try
        {
            var result = await ExecutePlatformAsync(videoFilePath, platform, ct);

            // 取消优先：平台 SDK 可能把取消吞成**失败结果**（返回 false/null 而不是抛异常）——
            // 这类「失败」不能上报服务端、不能播失败音、更不能让队列标红。
            // 成功结果照常上报（取消前那一刻平台真发出去了，必须留下凭据）。
            if (!result.Success && ct.IsCancellationRequested)
                throw new OperationCanceledException("用户已取消发布", ct);

            await TryReportAsync(platform.Id, result.Success, result.VideoId,
                result.Error, result.ExtraDataJson);
            Notify(result.Success);
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error(ex, "[Publish] 重试失败 {Platform}（{Id}），回报服务端",
                platform.PlatformKey, platform.Id);
            await TryReportAsync(platform.Id, false, null, ex.Message);
            Notify(false);
            throw;
        }
    }

    /// <summary>单平台执行：路由到对应 SDK</summary>
    private Task<(bool Success, string? VideoId, string? Error, string? ExtraDataJson)> ExecutePlatformAsync(
        string videoFilePath, PublishPlatformItem platform, CancellationToken ct) =>
        platform.PlatformKey switch
        {
            PlatformNames.Douyin => ExecuteDouyinAsync(videoFilePath, platform, ct),
            PlatformNames.WechatVideo => ExecuteWechatVideoAsync(videoFilePath, platform, ct),
            PlatformNames.Kuaishou => ExecuteKuaishouAsync(videoFilePath, platform, ct),
            PlatformNames.Xiaohongshu => ExecuteXhsAsync(videoFilePath, platform, ct),
            PlatformNames.Bilibili => ExecuteBilibiliAsync(videoFilePath, platform, ct),
            PlatformNames.Toutiao => ExecuteToutiaoAsync(videoFilePath, platform, ct),
            _ => Task.FromResult<(bool, string?, string?, string?)>(
                (false, null, $"平台「{platform.PlatformDisplayName}」SDK 尚未实现", null)),
        };

    // ═══════════════════════════════════════════════════════════════
    //  抖音
    // ═══════════════════════════════════════════════════════════════

    private async Task<(bool, string?, string?, string?)> ExecuteDouyinAsync(
        string videoFilePath, PublishPlatformItem platform, CancellationToken ct)
    {
        var payload = PublishPayload.Deserialize<DouyinPayload>(platform.ExtraDataJson);
        if (payload is null)
            return (false, null, "发布参数缺失或已损坏，请重新编辑该条目再发布", null);

        var creds = await FetchCredentialsAsync(platform);
        if (creds?.Cookie is null)
            return (false, null, "未找到抖音登录凭证，请重新登录", null);

        // CookieJar 自动吸收每个响应的 Set-Cookie（Bd-Ticket-Guard 更新）
        var cookieJar = new CookieJar(creds.Cookie);

        // BdTicketSigner：ECDSA 签名 + bd-ticket-guard-server-data 响应处理
        var signer = BdTicketSigner.FromLocalStorage(creds.LocalStorageJson);
        if (signer.IsValid)
        {
            signer.UpdateFromCookie(creds.Cookie);
            Log.Information("[Douyin] BdTicketSigner 已加载 —— 启用 ECDSA 签名");
        }

        var extra = CredentialFields.Extra(creds.ExtraDataJson);
        var csrfToken = extra.GetValueOrDefault("csrfToken")?.ToString();
        var envSnapshot = CredentialFields.EnvironmentSnapshot(creds.ExtraDataJson);
        var options = new PlatformOptions { Environment = envSnapshot, SessionId = platform.PlatformSessionId };
        // a_bogus 的计算输入包含 UA —— 签名 UA 必须与实际请求 UA（PlatformHttpClient 用快照）一致
        var envUa = envSnapshot?.UserAgent ?? options.UserAgent;

        OnPlatformProgress?.Invoke(PlatformNames.Douyin, "refreshing_sts", 5, "获取上传凭证...");
        var authSvc = new DouyinAuthService(options);
        var stsMsToken = MsTokenGenerator.Generate();
        var stsBaseParams = PlatformHttpClient.BuildQueryString(env: envSnapshot);
        var stsABogus = ABogusGenerator.Generate(stsBaseParams, envUa, aid: 1128);

        var stsResult = await authSvc.RefreshStsDetailedAsync(cookieJar, stsMsToken, stsABogus, ct);
        if (!stsResult.Ok)
        {
            Log.Error("[Douyin] STS 刷新失败（会话 {Session}，status_code={Code}）",
                platform.PlatformSessionId, stsResult.StatusCode);

            // status_code=8 = 账号被要求完成身份验证（与 passport authlv 同源，2026-10-28 实测）：
            // 把账号窗口亮出来 —— 只报「Cookie 过期」会逼用户反复重登。
            var opened = DouyinPublishGuidance.StsLooksLikeVerifyRequired(stsResult.StatusCode)
                         && await DouyinWebExecutor.RequestVerifyAsync(platform.PlatformSessionId, ct);
            return (false, null,
                DouyinPublishGuidance.StsFailure(stsResult.StatusCode, stsResult.StatusMsg, opened), null);
        }

        var sts = stsResult.Token!;

        var userId = ExtractUserId(sts.SessionToken);

        OnPlatformProgress?.Invoke(PlatformNames.Douyin, "uploading", 10, "上传视频中...");
        var ext = Path.GetExtension(videoFilePath).TrimStart('.');
        var uploadSvc = new DouyinUploadService(options);
        var upResult = await uploadSvc.UploadAsync(
            videoFilePath,
            sts.AccessKeyId, sts.SecretAccessKey, sts.SessionToken,
            userId ?? "",
            extension: ext,
            onChunkProgress: (cur, total, up, all) =>
            {
                var pct = 10 + (int)(up * 50.0 / all);
                OnPlatformProgress?.Invoke(PlatformNames.Douyin, "uploading", pct,
                    $"上传中 {cur}/{total} ({pct}%)");
                return Task.CompletedTask;
            },
            ct: ct);

        if (upResult is null)
            return (false, null, "视频上传失败", null);

        // 封面上传（60–70%）—— 2.5.7 统一：用户选的图 → 没选就抽一帧（封面帧有值按它，
        // 否则 0.5 秒）。抽帧失败不阻断，退回平台默认封面（帧照发、裁剪不下发）。
        string? coverUri = null;
        (int Width, int Height)? coverSize = null;
        var coverFile = await ResolveCoverAsync(videoFilePath, platform, payload.CoverFrameMs, ct);
        if (coverFile.HasFile)
        {
            var coverFilePath = coverFile.Path!;
            OnPlatformProgress?.Invoke(PlatformNames.Douyin, "cover_upload", 65,
                coverFile.Source == PublishCoverSource.Custom ? "上传封面图..." : "上传封面（视频首帧）...");
            try
            {
                var imageSvc = new DouyinImageUploadService(options);
                var coverBytes = await File.ReadAllBytesAsync(coverFilePath, ct);
                coverUri = await imageSvc.UploadAsync(coverBytes,
                    Path.GetFileName(coverFilePath),
                    sts.AccessKeyId, sts.SecretAccessKey, sts.SessionToken, ct);
                // 上传成功才读尺寸 —— 失败时封面根本没挂上，裁剪尺寸也别发
                if (!string.IsNullOrWhiteSpace(coverUri))
                    coverSize = ImageDimensionProbe.TryRead(coverBytes);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;   // 用户暂停/删除选中：取消不是「封面失败」，别继续往下发布
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Douyin] 封面上传失败（非致命）：{File}", coverFilePath);
            }
        }

        OnPlatformProgress?.Invoke(PlatformNames.Douyin, "publishing", 80, "发布中...");

        // 封面帧 / 裁剪的最终取值（2026-11-05 抓包）：上传了自定义封面图时页面
        // poster_delay 恒 0（帧设置不生效），裁剪尺寸 = 裁剪结果（1051×788，= 图片实际像素）；
        // 没有自定义封面 → 帧照发、裁剪不下发。规则收口在 CoverMapping，自检钉住。
        var cover = CoverMapping.Douyin(payload.CoverFrameMs, payload.CoverCropWidth,
            payload.CoverCropHeight, coverUri is not null, coverSize);

        var publishReq = new CreateDouyinWorkRequest(
            VideoId: upResult.VideoId,
            Title: platform.Title,
            Description: payload.Caption ?? platform.Description,
            Visibility: VisibilityMapping.Douyin(payload.Visibility),
            AllowDownload: payload.AllowDownload,
            ScheduledAt: payload.ScheduledAt,
            CollectionMixId: payload.MixId,
            // 合集内排序 —— 2026-11-05 抓包选合集时页面会发 mix_order（0 = 发布时间顺序）
            MixOrder: payload.CollectionOrder,
            DeclareInfo: DeclarationMapping.DouyinUserDeclareInfo(payload.Declaration, payload.DeclarationDetail),
            // 抖音要求 poi_id 与 poi_name 同时非空，否则整个位置不发（缺陷 L10 的兜底）
            PoiInfo: payload.Location is { IsUsable: true } poi
                ? new Dictionary<string, string> { ["poi_id"] = poi.PoiId!, ["poi_name"] = poi.Name }
                : null,
            HotSentence: payload.HotSentence,
            CoverUri: coverUri,
            TextExtra: payload.TextExtra,
            Challenges: payload.Challenges,
            // ── 2.1 全量长尾（1.3 抖音表）──
            MusicId: payload.MusicId,
            Chapters: payload.Chapters?
                .Select(c => new DouyinChapter(c.StartMs, c.Title)).ToList(),
            ActivityIds: payload.ActivityIds,
            Mentions: payload.Mentions,
            InteractionStickers: payload.InteractionStickers,
            SyncTargets: payload.SyncTargets,
            CoverFrameMs: cover.FrameMs,
            CoverCropWidth: cover.CropWidth,
            CoverCropHeight: cover.CropHeight,
            CoverToolsInfo: payload.CoverToolsInfo,
            CoverToolsExtendInfo: payload.CoverToolsExtendInfo);

        var item = DouyinVideoService.BuildCreateItem(publishReq);
        var bodyJson = JsonSerializer.Serialize(new { item },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        string? itemId = null;
        string? err = null;

        // ── 优先网页代理：本会话自己的 WebView2 档案页（原生 bd_ticket_guard）──
        if (!string.IsNullOrWhiteSpace(csrfToken) && DouyinWebExecutor.Has(platform.PlatformSessionId))
        {
            Log.Information("[Douyin] 经网页代理发布：title={Title}, vid={Vid}",
                publishReq.Title, publishReq.VideoId);
            var wvResult = await DouyinWebExecutor.ExecuteCreateWorkAsync(
                platform.PlatformSessionId, bodyJson, csrfToken, ct);
            var (wvItemId, wvError, verifyWays) = ParseCreateWorkResult(wvResult, platform);
            itemId = wvItemId;
            err = wvError;

            if (verifyWays is not null)
            {
                // passport 二次验证：**不兜底 HTTP**（同一个账号必然被同一道闸拦），
                // 把该账号的窗口亮出来、落到发布页，用户验完点重试即可。
                var opened = await DouyinWebExecutor.RequestVerifyAsync(platform.PlatformSessionId, ct);
                err = DouyinPublishGuidance.VerifyRequired(verifyWays, opened);
            }
        }

        // ── 兜底：HTTP CreateWork ──
        if (itemId is null && err is null)
        {
            var createWorkMsToken = MsTokenGenerator.Generate();
            var createWorkBaseParams = PlatformHttpClient.BuildQueryString(
                new Dictionary<string, string> { ["read_aid"] = "2906" }, env: envSnapshot);
            var createWorkABogus = ABogusGenerator.Generate(createWorkBaseParams, envUa, aid: 2906);

            var videoSvc = new DouyinVideoService(options);
            Log.Information("[Douyin] 经 HTTP 发布：title={Title}, vid={Vid}",
                publishReq.Title, publishReq.VideoId);
            (itemId, err) = await videoSvc.CreateWorkAsync(
                cookieJar, publishReq, createWorkMsToken, createWorkABogus, csrfToken, signer, ct);

            // 403 且 Bd-Ticket-Guard-Key-Sign-Result=2 是**永久性**错误 ——
            // ECDSA 密钥被服务端拒绝，重试无用。立即失败让用户重新登录刷新密钥。
        }

        if (itemId is null)
        {
            Log.Error("[Douyin] CreateWork 失败：{Error}（title={Title}, vid={Vid}）",
                err, publishReq.Title, publishReq.VideoId);
            return (false, null, err ?? "发布失败：抖音未返回作品 ID", null);
        }

        Log.Information("[Douyin] 发布成功 item_id={ItemId}", itemId);
        OnPlatformProgress?.Invoke(PlatformNames.Douyin, "published", 100,
            $"发布成功! {Short(itemId, 12)}...");
        return (true, itemId, null, null);
    }

    /// <summary>
    /// 解析网页代理返回的 <c>{ok,status,body,headers}</c>。
    ///
    /// 两条特殊路径（老版同款，都是实测踩出来的）：
    ///   · passport 验证（<c>x-tt-verify-passport-decision</c>）：设备认证等级不足，
    ///     页面内原生请求都被拦 → HTTP 兜底必然同样被拦 → **不再兜底**，直接引导验证；
    ///     第三项 <c>VerifyWays</c> 非空 = 需要验证（文案由调用方按「窗口开没开」生成）；
    ///   · 其它失败 → 返回 err=null 让调用方走 HTTP 兜底（成功则 err 也为 null）。
    /// </summary>
    private (string? ItemId, string? Error, string? VerifyWays) ParseCreateWorkResult(
        string? wvResult, PublishPlatformItem platform)
    {
        if (wvResult is null) return (null, null, null); // 宿主不可用 —— 走 HTTP 兜底

        try
        {
            // InvokeScript 返回的是 JSON 字符串（可能双重编码）
            var innerJson = wvResult;
            try { innerJson = JsonSerializer.Deserialize<string>(wvResult) ?? wvResult; }
            catch { /* 不是双重编码，原样用 */ }

            using var doc = JsonDocument.Parse(innerJson);
            var ok = doc.RootElement.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
            var status = doc.RootElement.TryGetProperty("status", out var stEl) ? stEl.GetInt32() : 0;
            var body = doc.RootElement.TryGetProperty("body", out var bdEl) ? bdEl.GetString() : "";
            var headers = doc.RootElement.TryGetProperty("headers", out var hdEl)
                ? hdEl.GetString() ?? "" : "";

            if (ok && !string.IsNullOrWhiteSpace(body))
            {
                using var bodyDoc = JsonDocument.Parse(body);
                var statusCode = bodyDoc.RootElement.TryGetProperty("status_code", out var sc)
                    && sc.TryGetInt32(out var scVal) ? scVal : -1;

                if (statusCode == 0 && bodyDoc.RootElement.TryGetProperty("item_id", out var iid))
                    return (iid.GetString(), null, null);

                // 把平台的人话说明一起报出来（只报状态码等于没报，见 DouyinCreateWorkResponse.StatusMsg）
                var statusMsg = bodyDoc.RootElement.TryGetProperty("status_msg", out var smEl)
                    ? smEl.GetString() : null;
                Log.Warning("[Douyin] 网页代理发布失败：status_code={Code}, status_msg={Msg}",
                    statusCode, statusMsg ?? "(无)");
                return (null, null, null); // 业务失败 —— 让 HTTP 兜底再试一次
            }

            // 带响应头输出 —— 风控拒绝的原因只长在头里
            Log.Warning("[Douyin] 网页代理 HTTP {Status}, headers={Headers}", status, headers);

            if (headers.Contains("x-tt-verify-passport-decision", StringComparison.OrdinalIgnoreCase))
            {
                var ways = ParsePassportVerifyWays(headers);
                var wayText = ways.Length > 0
                    ? string.Join("/", ways)
                    : DouyinPublishGuidance.DefaultVerifyWays;
                Log.Warning("[Douyin] 发布被 passport 验证拦截（authlv 检查）：{Ways}", wayText);
                OnPlatformProgress?.Invoke(PlatformNames.Douyin, "verify_required", 85,
                    $"抖音风控拦截发布（{wayText}）。建议暂停操作，养号后再试");
                return (null, null, wayText);
            }

            return (null, null, null);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Douyin] 网页代理结果解析失败 —— 走 HTTP 兜底");
            return (null, null, null);
        }
    }

    /// <summary>
    /// 从 CreateWork 响应头解析 passport 验证决策，返回中文验证方式列表。
    /// 头值形如 <c>{"account_flow":"verify",...,"verify_way_name_list":"mobile_sms_verify,..."}</c>
    /// </summary>
    private static string[] ParsePassportVerifyWays(string respHeaders)
    {
        try
        {
            // 贪婪匹配到本行最后一个 '}'（头值 JSON 含嵌套对象，. 不跨行）
            var m = Regex.Match(respHeaders,
                @"x-tt-verify-passport-decision\s*:\s*(\{.*\})", RegexOptions.IgnoreCase);
            if (!m.Success) return [];

            using var doc = JsonDocument.Parse(m.Groups[1].Value);
            if (!doc.RootElement.TryGetProperty("verify_way_name_list", out var ways)
                || ways.ValueKind != JsonValueKind.String)
                return [];

            return (ways.GetString() ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(MapVerifyWay)
                .Where(w => w is not null)
                .Select(w => w!)
                .ToArray();
        }
        catch
        {
            // 头值解析失败（编码变化等）→ 返回空 → 调用方用通用提示「短信/密码」
            return [];
        }
    }

    /// <summary>抖音 passport 验证方式代码 → 中文。</summary>
    private static string? MapVerifyWay(string way) => way switch
    {
        "mobile_sms_verify" => "短信验证码",
        "pwd_verify" => "密码验证",
        "mobile_up_sms_verify" => "上行短信验证",
        _ => null,
    };

    // ═══════════════════════════════════════════════════════════════
    //  视频号
    // ═══════════════════════════════════════════════════════════════

    private async Task<(bool, string?, string?, string?)> ExecuteWechatVideoAsync(
        string videoFilePath, PublishPlatformItem platform, CancellationToken ct)
    {
        var payload = PublishPayload.Deserialize<WechatVideoPayload>(platform.ExtraDataJson);
        if (payload is null)
            return (false, null, "发布参数缺失或已损坏，请重新编辑该条目再发布", null);

        var creds = await FetchCredentialsAsync(platform);
        if (creds?.Cookie is null)
            return (false, null, "未找到视频号登录凭证，请重新登录", null);

        var cookie = creds.Cookie;
        var credsExtra = CredentialFields.Extra(creds.ExtraDataJson);
        // 优先 deviceId（WebView 登录时存的 key），其次 device_id（兼容旧格式）
        var deviceId = credsExtra.GetValueOrDefault("deviceId")?.ToString()
            ?? credsExtra.GetValueOrDefault("device_id")?.ToString() ?? "";
        // 兜底：WebView 提取 device_id 失败时（反射 + localStorage 都可能静默失败），
        // 生成一个持久 UUID 当设备指纹
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            deviceId = Guid.NewGuid().ToString("N")[..32];
            Log.Warning("[WechatVideo] 凭证里没有 deviceId，生成兜底值 {DevId}", deviceId);
        }

        var options = new PlatformOptions
        {
            Environment = CredentialFields.EnvironmentSnapshot(creds.ExtraDataJson),
            SessionId = platform.PlatformSessionId,
        };

        OnPlatformProgress?.Invoke(PlatformNames.WechatVideo, "refreshing_auth", 5, "获取上传凭证...");
        var authSvc = new WechatVideoAuthService(options);
        var auth = await authSvc.GetUploadParamsAsync(cookie, deviceId, ct);
        if (auth is null)
            return (false, null, "上传凭证获取失败，请重新登录视频号", null);

        OnPlatformProgress?.Invoke(PlatformNames.WechatVideo, "uploading", 10, "上传视频中...");
        var uploadSvc = new WechatVideoUploadService(options);
        var upResult = await uploadSvc.UploadAsync(videoFilePath, auth,
            onChunkProgress: (cur, total, up, all) =>
            {
                var pct = 10 + (int)(up * 45.0 / all);
                OnPlatformProgress?.Invoke(PlatformNames.WechatVideo, "uploading", pct,
                    $"上传中 {cur}/{total} ({pct}%)");
                return Task.CompletedTask;
            }, ct: ct);

        if (upResult is null)
            return (false, null, "视频上传失败", null);

        // 封面：视频号**必填**（空则 300801）。没给自定义封面就抽视频首帧 ——
        // 老版这里塞的是 1×1 绿 JPEG 占位图，用户作品封面就是一块绿色（设计文档 §4.2 要求删掉）
        var (coverFilePath, coverNeedCleanup) =
            await ResolveWechatCoverAsync(videoFilePath, platform, payload.CoverFrameMs, ct);
        string? coverUrl = null;
        if (coverFilePath is not null)
        {
            OnPlatformProgress?.Invoke(PlatformNames.WechatVideo, "cover_upload", 55, "上传封面图...");
            try
            {
                var coverSvc = new WechatVideoUploadService(options) { CdnFileType = "picture" };
                var coverResult = await coverSvc.UploadAsync(coverFilePath, auth, ct: ct);
                coverUrl = coverResult?.HttpsUrl;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;   // 用户暂停/删除选中：取消不是「封面失败」
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[WechatVideo] 封面上传失败（非致命）：{File}", coverFilePath);
            }
            finally
            {
                if (coverNeedCleanup && File.Exists(coverFilePath))
                    try { File.Delete(coverFilePath); } catch { /* best effort */ }
            }
        }

        if (string.IsNullOrWhiteSpace(coverUrl))
        {
            // 封面拿不到 → 发布必然 300801，早失败比让用户等完整轮询好
            return (false, null, "封面图缺失且无法自动生成（需要 ffmpeg），请手动选择封面后重试", null);
        }

        var finderId = CredentialFields.LocalStorageField(creds.LocalStorageJson, "finder_username")
            ?? "v2_060000231003b20faec8cae08e1cc3d1c70ded37b077cfefe945fdb82dfe6641a73b44b04c16@finder";

        OnPlatformProgress?.Invoke(PlatformNames.WechatVideo, "clipping", 60, "视频处理中...");
        var contentSvc = new WechatVideoContentService(options) { Uin = auth.Uin, FinderId = finderId };
        var fileSize = new FileInfo(videoFilePath).Length;
        // 元数据：clip_video 的 clipOriginVideoInfo 发真值（浏览器抓包里是真实宽高/时长，
        // 老版一直发 0），同时用来算转码等待窗口
        var meta = await ProbeMetaAsync(videoFilePath, ct);
        var clipKey = await contentSvc.ClipVideoAsync(cookie, deviceId, upResult.HttpsUrl,
            width: meta is { Width: > 0 } ? meta.Width : 0,
            height: meta is { Height: > 0 } ? meta.Height : 0,
            duration: meta is { DurationS: > 0 } ? meta.DurationS : 0.0,
            fileSize: fileSize, ct: ct);
        if (clipKey is null)
            return (false, null, "视频裁剪提交失败", null);

        // 等待窗口按时长放大 —— 34 分钟的视频 3 分钟窗口远不够（2026-10-25 教学视频两连超时）
        var clipRetries = WechatClipPollRetries(meta?.DurationS ?? 0, fileSize);
        OnPlatformProgress?.Invoke(PlatformNames.WechatVideo, "processing", 70, "等待视频处理...");
        Log.Information("[WechatVideo] 等待转码：时长 {Dur:F1}s / {Size}MB，最多轮询 {Retries} 次（≈{Min:F0} 分钟）",
            meta?.DurationS ?? 0, fileSize / 1024 / 1024, clipRetries,
            clipRetries * WechatVideoContentService.ClipPollInterval.TotalMinutes);
        var clipResult = await contentSvc.ClipVideoResultAsync(cookie, deviceId, clipKey,
            maxRetries: clipRetries, ct: ct);
        if (clipResult?.Url is null)
            return (false, null,
                $"视频处理超时（已等待约 {clipRetries * 3 / 60} 分钟），请稍后重试", null);

        OnPlatformProgress?.Invoke(PlatformNames.WechatVideo, "publishing", 80, "发布中...");

        // 视频标注的 tagKey 是**会话级**的，发布时现取（见 ResolveWechatTagKeyAsync）
        var tagKey = await ResolveWechatTagKeyAsync(contentSvc, payload, cookie, deviceId, ct);

        var publishReq = new WechatVideoPublishRequest(
            VideoUrl: clipResult.Url,
            FileSize: clipResult.FileSize > 0 ? clipResult.FileSize : fileSize,
            ThumbUrl: coverUrl,
            CoverUrl: coverUrl,
            Description: payload.Caption ?? platform.Description,
            // 视频号话题是**纯文本**，不经 API 匹配（设计文档 §4.4 铁律三）——
            // 老版 HashtagPlatformOrder 只有三家，视频号在下拉里根本选不到（缺陷 L3）
            Topics: payload.Topics ?? ParseHashtags(payload.Caption ?? platform.Description),
            Location: BuildWechatLocation(payload.Location),
            CollectionId: payload.CollectionId,
            CollectionName: payload.CollectionName,
            Duration: clipResult.Duration > 0 ? clipResult.Duration : 0.0,
            Width: clipResult.Width > 0 ? clipResult.Width : 1920,
            Height: clipResult.Height > 0 ? clipResult.Height : 1080,
            Md5Sum: clipResult.Md5 ?? "",
            VideoClipTaskId: clipKey,
            // 短标题由 builder 算好（<6 字时已是 null，且标题已并入正文首行）；
            // 不能再从 platform.Title 兜底 —— 那会把 <6 字标题又塞回去，平台整条拒
            ShortTitle: payload.ShortTitle,
            CardShowStyle: payload.CardShowStyle ?? 2,
            ExternalLink: payload.ExternalLink,
            ExternalLinkTitle: payload.ExternalLinkTitle,
            ExternalLinkType: payload.ExternalLinkType,
            ScheduledAt: payload.ScheduledAt,
            TagInfo: BuildWechatTagInfo(payload, tagKey));

        var pubResult = await contentSvc.PublishAsync(cookie, deviceId, publishReq, ct);
        if (pubResult is not { Success: true })
        {
            var errMsg = pubResult?.ErrorMessage ?? "发布失败（未知错误）";
            Log.Error("[WechatVideo] 发布失败：{Error}", errMsg);
            return (false, null, $"发布失败: {errMsg}", null);
        }

        // post_create 不直接返回 objectId —— 回查 post_list 按 clipKey 匹配
        string? objectId = null;
        try
        {
            await Task.Delay(3000, ct); // 等 3s 让列表索引生效
            objectId = await contentSvc.FindObjectIdByClipKeyAsync(cookie, deviceId, clipKey, ct: ct);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[WechatVideo] 回查 objectId 失败（非致命）");
        }

        if (!string.IsNullOrWhiteSpace(objectId))
        {
            Log.Information("[WechatVideo] 发布成功 objectId={Id}", objectId);
            OnPlatformProgress?.Invoke(PlatformNames.WechatVideo, "published", 100,
                $"发布成功! {Short(objectId, 20)}...");
            return (true, objectId, null, null);
        }

        OnPlatformProgress?.Invoke(PlatformNames.WechatVideo, "published", 100, "发布成功（objectId 未获取）");
        return (true, null, null, null);
    }

    /// <summary>
    /// 探测视频元数据（ffprobe，命中缓存时毫秒级）。没接探测器 / 探测失败返回 null ——
    /// 调用方按「信息未知」继续，绝不因为探测不出来就拦住发布。
    /// </summary>
    private async Task<VideoMeta?> ProbeMetaAsync(string videoFilePath, CancellationToken ct)
    {
        if (MetaProbe is null) return null;
        try
        {
            return await MetaProbe.ProbeAsync(videoFilePath, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Publish] 元数据探测失败（按缺省继续）：{File}", videoFilePath);
            return null;
        }
    }

    /// <summary>
    /// 视频号「等待视频处理」的轮询次数（间隔 <see cref="WechatVideoContentService.ClipPollInterval"/> = 3s）。
    ///
    /// 实测基线（2026-10-20 ~ 10-25）：42 秒视频 ~7 秒出结果、123 秒 ~10 秒；
    /// 34 分钟 / 492 MB 的教学视频在旧的 60 次（3 分钟）里**两次都没转完**，
    /// 被误判成「视频处理超时」并逼用户重传整段。
    /// 预算 = 3 分钟基线 + 0.6×时长（转码约 5 倍速）；时长未知时按体积回落（每 MB 约 1 秒），
    /// 夹在 [60, 600] 次 = 3 ~ 30 分钟。
    /// </summary>
    internal static int WechatClipPollRetries(double durationSeconds, long fileSizeBytes)
    {
        var expectedSeconds = 180
            + (durationSeconds > 0
                ? durationSeconds * 0.6
                : Math.Max(0, fileSizeBytes / 1_000_000.0));
        var retries = (int)Math.Ceiling(expectedSeconds / WechatVideoContentService.ClipPollInterval.TotalSeconds);
        return Math.Clamp(retries, 60, 600);
    }

    /// <summary>
    /// 统一封面解析（2.5.7）—— 四个已接入平台共用：
    /// 用户封面图 → 自动抽帧（封面帧有值按它，否则 0.5 秒）→ 无。
    /// 规则与纯函数都在 <see cref="PublishCoverResolver"/>，这里只把管线自己的
    /// <see cref="MetaProbe"/> 接进去。抽帧失败不抛（用户取消除外）。
    /// </summary>
    private Task<PublishCover> ResolveCoverAsync(
        string videoFilePath, PublishPlatformItem platform, long? coverFrameMs, CancellationToken ct)
        => PublishCoverResolver.ResolveAsync(
            videoFilePath, platform.CoverFilePath, coverFrameMs, MetaProbe, ct);

    /// <summary>
    /// 视频号封面：用户给了就用，没给就抽首帧（2026-11-05 抓包：页面发布前把抽好的
    /// 20304 图片直传 CDN，再把 URL 写进 post_create 的五个封面子字段 ——
    /// capture 06-12-51 行 325-339 / 465，与管线走同一条图片直传链）。
    ///
    /// 2.5.7 起封面来源统一走 <see cref="PublishCoverResolver"/>（与抖音/快手/小红书
    /// 同一套规则，抽帧是**发布质量**而不是 320px 卡片缩略图；封面帧有值按它抽）。
    /// 返回 (路径, 用完是否删除)。首帧都抽不出来（缺 ffmpeg）时退回 1×1 占位图 ——
    /// 仅作最后兜底，因为「一块绿封面」也比整条发布失败好（封面空则 300801），但会打警告日志。
    /// </summary>
    private async Task<(string? Path, bool NeedCleanup)> ResolveWechatCoverAsync(
        string videoFilePath, PublishPlatformItem platform, long? coverFrameMs, CancellationToken ct)
    {
        var resolved = await ResolveCoverAsync(videoFilePath, platform, coverFrameMs, ct);
        var customCover = resolved.Source == PublishCoverSource.Custom ? resolved.Path : null;
        var firstFrame = resolved.Source == PublishCoverSource.FirstFrame ? resolved.Path : null;

        switch (WechatVideoContentService.ChooseCoverSource(customCover, firstFrame))
        {
            case WechatVideoCoverSource.CustomCover:
                Log.Information("[WechatVideo] 封面用自定义图：{File}", customCover);
                return (customCover, false);

            case WechatVideoCoverSource.FirstFrame:
                Log.Information("[WechatVideo] 未选封面，用视频首帧：{File}", firstFrame);
                return (firstFrame, false); // 抽帧是缓存资产，不删

            default:
                var placeholder = Path.Combine(Path.GetTempPath(),
                    $"wechat_cover_{Guid.NewGuid():N}.jpg");
                GeneratePlaceholderJpeg(placeholder);
                Log.Warning("[WechatVideo] 封面用 1×1 占位图兜底（未选封面且抽首帧失败）：{Video}",
                    videoFilePath);
                return (placeholder, true);
        }
    }

    /// <summary>
    /// 视频号位置对象。老版是从 extra data 里原样透传一个结构化对象；
    /// 新版从 <see cref="PoiRef"/> 组装（字段名对齐 <c>helper_search_location</c> 的产出）。
    /// </summary>
    private static object? BuildWechatLocation(PoiRef? poi)
    {
        if (poi is null || string.IsNullOrWhiteSpace(poi.Name)) return null;

        // 视频号 poiClassifyId 可空，但**必须给一个对象**（老版注释同款：空则发全零对象）
        return new Dictionary<string, object?>
        {
            ["poiClassifyId"] = poi.PoiId ?? "",
            ["poiName"] = poi.Name,
            ["address"] = poi.SubName ?? "",
            // city 老版一直发（`loc.City ?? ""`），抓包里也有 —— 解析器目前不填，缺了就不是原样
            ["city"] = poi.CityName ?? "",
            // 经纬度必须是 **number**：解析结果里按 "F6" 存的是字符串，
            // 原样发出去就成了 "31.296991"，而抓包里这两个字段是 JSON number（2026-09-20 修正）
            ["latitude"] = ToCoordinate(poi.Latitude),
            ["longitude"] = ToCoordinate(poi.Longitude),
        };
    }

    /// <summary>经纬度转数字 —— 解析结果里存的是字符串（各平台搜索接口给的就是字符串）</summary>
    private static double ToCoordinate(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0d;

    /// <summary>
    /// 取视频标注的 tagKey（会话级 token）——**只有真要标注时才取**：tagType=0 时老版连
    /// tagInfo 都不发，多打一次接口没意义。
    ///
    /// ⚠️ 不能用草稿/预设里存的那份：它是上一次会话签发的，早过期了
    /// （<c>PlatformParams.WechatTagKey</c> 到今天没有任何地方写过，等于一直发空串）。
    /// 抓包（<c>examples/wechatvideo/media/post_create_1.py</c>）里那份 key 是非空的
    /// <c>{tagType:3, tagKey:"2aa1a6…5F133"}</c>；空 key 配非零 tagType 平台直接回
    /// <c>errCode=300002 request failed</c>（2026-09-20 实测）。取不到就退回空串，但日志里点名。
    /// </summary>
    private static async Task<string?> ResolveWechatTagKeyAsync(
        WechatVideoContentService contentSvc, WechatVideoPayload payload,
        string cookie, string deviceId, CancellationToken ct)
    {
        if (payload.TagType is not int tagType || tagType == 0) return payload.TagKey;

        try
        {
            var (_, key) = await contentSvc.GetTagListAsync(cookie, deviceId, ct: ct);
            if (!string.IsNullOrWhiteSpace(key))
            {
                // 打在日志里：300002 是不是这个引起的，下次一眼能看出来
                Log.Information("[WechatVideo] 标注 tagKey 已取到（len={Len}）", key.Length);
                return key;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // 用户暂停/删除选中：取消不是「取不到 tagKey」
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[WechatVideo] 取标注 tagKey 失败");
        }

        Log.Warning("[WechatVideo] 标注 tagKey 没取到（tagType={TagType}）—— 平台多半整条拒收", tagType);
        return payload.TagKey;
    }

    /// <summary>
    /// 视频号视频标注（tagInfo）。
    ///
    /// **缺陷 L1 修复**：「自行拍摄」的二级数据在此按真实协议结构组装 ——
    /// <c>shootInfo{ postTimestamp: "秒"(**字符串**), countryCode/provinceCode/cityCode(int) }</c>
    /// （见 <c>examples/wechatvideo/media/finder_update_post_info.py:125-131</c>）。
    /// 老版产出的是 `shoot_time`(秒) + `shoot_region`(数组)，字段名与结构双错，
    /// 而且 <c>PublishPipeline.cs:686-694</c> 组装 tagInfo 时**根本没读它们** ——
    /// 整个功能从未上线。这里一并修正并真正接上。
    /// </summary>
    private static object? BuildWechatTagInfo(WechatVideoPayload payload, string? tagKey)
    {
        var tagType = payload.TagType;
        if (tagType is null or 0) return null;

        var dict = new Dictionary<string, object?>
        {
            ["tagType"] = tagType.Value,
            ["tagKey"] = tagKey ?? "",
        };

        switch (payload.Declaration)
        {
            case DeclarationKind.Repost:
                // 转载：repostSource 选填
                if (!string.IsNullOrWhiteSpace(payload.DeclarationDetail?.RepostSource))
                    dict["repostSource"] = payload.DeclarationDetail.RepostSource;
                break;

            case DeclarationKind.SelfShot:
                var shoot = payload.DeclarationDetail;
                dict["shootInfo"] = new Dictionary<string, object?>
                {
                    // 秒，且**必须是字符串** —— 抓包证实（老版写成秒整数，类型就不对）
                    ["postTimestamp"] = shoot?.ShootTime is { } t
                        ? new DateTimeOffset(t).ToUnixTimeSeconds().ToString()
                        : "",
                    ["countryCode"] = shoot?.ShootRegion?.CountryCode ?? 0,
                    ["provinceCode"] = shoot?.ShootRegion?.ProvinceCode ?? 0,
                    ["cityCode"] = shoot?.ShootRegion?.CityCode ?? 0,
                };
                break;
        }

        return dict;
    }

    // ═══════════════════════════════════════════════════════════════
    //  快手
    // ═══════════════════════════════════════════════════════════════

    private async Task<(bool, string?, string?, string?)> ExecuteKuaishouAsync(
        string videoFilePath, PublishPlatformItem platform, CancellationToken ct)
    {
        var payload = PublishPayload.Deserialize<KuaishouPayload>(platform.ExtraDataJson);
        if (payload is null)
            return (false, null, "发布参数缺失或已损坏，请重新编辑该条目再发布", null);

        // 章节 / 共创是原始 JSON 透传：**上传前**先验格式 —— 传完几分钟才报「JSON 写错」太伤
        try
        {
            KuaishouPublishService.ParseJsonArray(payload.ChaptersJson, "章节");
            KuaishouPublishService.ParseJsonArray(payload.CoPublishUsersJson, "共创用户");
        }
        catch (KuaishouApiException ex)
        {
            return (false, null, ex.Message, null);
        }

        var creds = await FetchCredentialsAsync(platform);
        if (creds?.Cookie is null)
            return (false, null, "未找到快手登录凭证，请重新登录", null);

        var cookie = creds.Cookie;
        // 服务端快照的 kuaishou.web.cp.api_st 会过期（作品列表回查 → 109/500002，
        // 2026-11-09 实测）；账号标签开着时用档案页的**活 Cookie** 覆盖同名项，
        // 没开标签/读取失败就原样用快照（行为与改动前一致）。
        cookie = await KuaishouWebCookies.MergeWithLiveAsync(
            platform.PlatformSessionId, cookie, ct);
        var credsExtra = CredentialFields.Extra(creds.ExtraDataJson);
        // apiPh 存在 cookie 里，不在 extra_data JSON（合并后优先取活 Cookie 里的新值）
        var apiPh = CredentialFields.CookieValue(cookie, "kuaishou.web.cp.api_ph")
            ?? credsExtra.GetValueOrDefault("apiPh")?.ToString();

        var options = new PlatformOptions
        {
            Environment = CredentialFields.EnvironmentSnapshot(creds.ExtraDataJson),
            SessionId = platform.PlatformSessionId,
        };

        // sig3/sig4 必须先预热签名宿主（模块只在页面 webpack 里，纯 C# 无法复算）。
        // 失败不阻断 —— 请求会裸发，由平台判（与小红书同口径）。
        await KuaishouSignerService.PrepareAsync(platform.PlatformSessionId, ct);

        OnPlatformProgress?.Invoke(PlatformNames.Kuaishou, "uploading", 10, "上传视频中...");
        var uploadSvc = new KuaishouUploadService(options, logger: null)
        {
            // 放慢 CDN 分片上传，规避限流 500002
            Concurrency = 2,
        };
        var upResult = await uploadSvc.UploadAsync(cookie, videoFilePath, apiPh: apiPh,
            onProgress: (cur, total, up, all) =>
            {
                var pct = 10 + (int)(up * 45.0 / all);
                OnPlatformProgress?.Invoke(PlatformNames.Kuaishou, "uploading", pct,
                    $"上传中 {cur}/{total} ({pct}%)");
                return Task.CompletedTask;
            }, ct: ct);

        if (upResult is null)
            return (false, null, "视频上传失败", null);

        // ── 自定义封面（2.5.2）──
        // upload/finish 的 coverKey 是平台给的默认首帧封面；选了封面图就传上去换新 Key
        // （页面 upload.js 的 editCoverKeyRef.current 同样在 submit 前整体替换）。
        // 上传失败**不致命**：退回默认封面 + 用户填的封面帧，视频照发。
        var coverKey = upResult.CoverKey;
        var coverFrameMs = payload.CoverFrameMs;
        // 2.5.7 统一：用户选的图 → 没选就抽一帧（封面帧有值按它，否则 0.5 秒）
        var coverFile = await ResolveCoverAsync(videoFilePath, platform, payload.CoverFrameMs, ct);
        if (coverFile.HasFile)
        {
            var coverFilePath = coverFile.Path!;
            OnPlatformProgress?.Invoke(PlatformNames.Kuaishou, "cover_upload", 65,
                coverFile.Source == PublishCoverSource.Custom ? "上传封面图..." : "上传封面（视频首帧）...");
            try
            {
                var uploadedKey = await new KuaishouCoverService(options)
                    .UploadAsync(cookie, coverFilePath, apiPh, ct);
                (coverKey, coverFrameMs) = KuaishouCoverService.ResolveCover(
                    upResult.CoverKey, uploadedKey, payload.CoverFrameMs);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;   // 用户暂停/删除选中：取消不是「封面失败」，别继续往下发布
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Kuaishou] 封面上传失败（非致命，用默认封面）：{File}",
                    coverFilePath);
            }
        }

        OnPlatformProgress?.Invoke(PlatformNames.Kuaishou, "publishing", 80, "发布中...");

        // 修复前入队的老条目（载荷已经落盘）可能还带着超过 3 个话题标签 ——
        // 快手会整条拒（2026-10-22 实测 result=1000「话题标签数量超过上限：4」）。
        // 组装端已裁，这里提交前再裁一道，让老队列条目重试也能发出去。
        var caption = PublishPayloadBuilder.ComposeKuaishouCaption(
            payload.Caption ?? platform.Description ?? "", []);
        var recTagIds = payload.RecTagIds?.Take(PlatformLimits.KuaishouMaxTopics).ToList();

        var decl = payload.DeclarationDetail;
        var pubReq = new KuaishouPublishRequest
        {
            FileId = upResult.FileId,
            CoverKey = coverKey,
            Caption = caption,
            Visibility = VisibilityMapping.Kuaishou(payload.Visibility),
            CollectionId = payload.CollectionId,
            ScheduledTime = payload.ScheduledAt is { } sa
                ? new DateTimeOffset(sa) : null,
            // 快手的经纬度与 poiId 是三个平级字段 —— 只有它能用经纬度绕过 POI ID
            Longitude = payload.Location?.Longitude,
            Latitude = payload.Location?.Latitude,
            PoiId = payload.Location?.PoiId,
            AllowDownload = payload.AllowDownload,
            ShowInNearby = payload.ShowInNearby,
            AllowSameFrame = payload.AllowSameFrame,
            // 长尾（阶段 2.2）—— 通知 / 封面 / PK 封面 / 画质 AI / 领域 / 短剧 / 章节 / 共创 / 活动
            NotifyFollowers = payload.NotifyFollowers,
            CoverFrameMs = coverFrameMs,
            CoverTitle = payload.CoverTitle,
            PkCoverKey = payload.PkCoverKey,
            PkCoverTimeStamp = payload.PkCoverTimeStamp,
            PkCoverType = payload.PkCoverType,
            PkCoverSize = payload.PkCoverSize,
            ProfileCoverKey = payload.ProfileCoverKey,
            RiseQuality = payload.RiseQuality,
            UseAiCaption = payload.UseAiCaption,
            UseAiCaptionCover = payload.UseAiCaptionCover,
            UseAiCover = payload.UseAiCover,
            UseIdealTime = payload.UseIdealTime,
            DisallowRecreation = payload.DisallowRecreation,
            Domain = payload.Domain,
            SecondDomain = payload.SecondDomain,
            MovieId = payload.MovieId,
            ProjectId = payload.ProjectId,
            ChaptersJson = payload.ChaptersJson,
            CoPublishUsersJson = payload.CoPublishUsersJson,
            CoPublishRole = payload.CoPublishRole,
            ActivityIds = payload.ActivityIds,
            Declaration = DeclarationMapping.KuaishouDeclaration(payload.Declaration),
            // 转载二级：来源平台 + 地区（**省市区文本，不是编码**）+ 发生时间（**毫秒**）
            DeclarationPlatform = payload.Declaration == DeclarationKind.Repost
                ? decl?.KuaishouRepostPlatform : null,
            DeclarationLocation = payload.Declaration == DeclarationKind.Repost
                ? decl?.KuaishouRepostLocation : null,
            DeclarationTime = payload.Declaration == DeclarationKind.Repost
                ? decl?.KuaishouRepostTimeMs : null,
            MediaId = upResult.MediaId,
            PhotoIdStr = upResult.PhotoIdStr,
            VideoDuration = upResult.VideoDuration,
            ApiPh = apiPh,
            RecTagIds = recTagIds,
        };

        KuaishouPublishResult publishResult;
        try
        {
            // SubmitAsync 内部自动查列表回填 workId/publishId（含重试）
            publishResult = await new KuaishouPublishService(options).SubmitAsync(cookie, pubReq, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // 用户暂停/删除选中：取消不是「发布失败」
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Kuaishou] 提交失败：{Error}", ex.Message);
            return (false, null, $"发布失败: {ex.Message}", null);
        }

        // 取 ID：workId（已发布）> publishId（待发布）> mediaId（兜底）
        var workId = publishResult.WorkId;
        var publishId = publishResult.PublishId;
        var videoId = !string.IsNullOrWhiteSpace(workId) ? workId
            : publishId > 0 ? publishId.ToString()
            : upResult.MediaId;

        // 回写 coverKey —— 后续删除作品时要靠它查列表
        var extraJson = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["ks_cover_key"] = upResult.CoverKey,
            ["ks_work_id"] = workId,
            ["ks_publish_id"] = publishId > 0 ? publishId : null,
        });

        Log.Information("[Kuaishou] 发布成功 workId={WorkId}, publishId={PubId}, videoId={Vid}",
            workId, publishId, videoId);
        OnPlatformProgress?.Invoke(PlatformNames.Kuaishou, "published", 100, $"发布成功! {videoId}...");
        return (true, videoId, null, extraJson);
    }

    // ═══════════════════════════════════════════════════════════════
    //  小红书
    // ═══════════════════════════════════════════════════════════════

    private async Task<(bool, string?, string?, string?)> ExecuteXhsAsync(
        string videoFilePath, PublishPlatformItem platform, CancellationToken ct)
    {
        var payload = PublishPayload.Deserialize<XhsPayload>(platform.ExtraDataJson);
        if (payload is null)
            return (false, null, "发布参数缺失或已损坏，请重新编辑该条目再发布", null);

        var creds = await FetchCredentialsAsync(platform);
        if (creds?.Cookie is null)
            return (false, null, "未找到小红书登录凭证，请重新登录", null);

        // 原始 JSON 透传字段（@/带货/订单/业务关系/PK 封面）：**上传前**先验格式 ——
        // 传完几分钟才报「JSON 写错」太伤（与快手 chapters 同一口径）
        try
        {
            XhsPublishService.ParseJsonValue(payload.Ats, "@ 用户", JsonValueKind.Array);
            XhsPublishService.ParseJsonValue(payload.GoodsInfo, "带货商品信息", JsonValueKind.Object);
            XhsPublishService.ParseJsonValue(payload.BizRelations, "业务关系", JsonValueKind.Array);
            XhsPublishService.ParseJsonValue(payload.OrderBind, "订单绑定", JsonValueKind.Object);
            XhsPublishService.ParseJsonValue(payload.PkCoverBizRelations, "PK 封面业务关联", JsonValueKind.Array);
        }
        catch (XhsPublishException ex)
        {
            return (false, null, ex.Message, null);
        }

        // x-s 必须先预热签名宿主（x3 = window.mnsv2，纯 C# 无法复算）。
        // 失败不阻断 —— 请求会裸发，由平台判（老版语义）。
        await XhsSignerService.PrepareAsync(platform.PlatformSessionId, ct);

        var cookie = creds.Cookie;
        var options = new PlatformOptions
        {
            Environment = CredentialFields.EnvironmentSnapshot(creds.ExtraDataJson),
            // 指定会话 ID：签名器据此定位「同账号的 WebView2 档案页」
            SessionId = platform.PlatformSessionId,
        };

        // 元数据先探：发布体要宽高/时长（不填会落到 1920×1080 / 10 秒），
        // 转码轮询预算也按时长放大。原来放在上传之后 —— 轮询只能吃固定 90 秒，
        // 43 秒 / 29MB 的视频都没转完（2026-11-09 实测），靠「超时非致命」兜底发布。
        var meta = await ProbeMetaAsync(videoFilePath, ct);

        OnPlatformProgress?.Invoke(PlatformNames.Xiaohongshu, "uploading", 10, "上传视频中...");
        XhsUploadResult upResult;
        try
        {
            var uploadSvc = new XhsUploadService(options)
            {
                TranscodePollMax = XhsUploadService.TranscodePollCount(
                    meta?.DurationS, new FileInfo(videoFilePath).Length),
            };
            upResult = await uploadSvc.UploadAsync(cookie, videoFilePath,
                async (current, total, _, _) =>
                {
                    var pct = 10 + (int)((double)current / total * 50);
                    OnPlatformProgress?.Invoke(PlatformNames.Xiaohongshu, "uploading", pct,
                        $"上传视频 {current}/{total}...");
                    await Task.CompletedTask;
                }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 用户暂停/应用退出 —— 取消语义继续上抛，不当作失败上报服务端
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Xhs] 上传失败：{File}", videoFilePath);
            if (ex.Message.Contains("401") || ex.Message.Contains("403"))
                return (false, null, "小红书登录已过期，请重新登录", null);
            return (false, null, $"视频上传失败: {ex.Message}", null);
        }

        if (upResult is null || string.IsNullOrWhiteSpace(upResult.FileId))
            return (false, null, "视频上传失败", null);

        // 封面（2.5.3 + 2.5.7 统一）：scene=image permit + ROS PUT → cover.file_id。
        // 用户选的图 → 没选就抽一帧（封面帧有值按它，否则 0.5 秒）。
        // 失败非致命 —— 回落平台默认封面（视频首帧），视频照发（与抖音/快手同一口径）。
        string? coverFileId = null;
        var coverFile = await ResolveCoverAsync(videoFilePath, platform, payload.CoverFrameMs, ct);
        if (coverFile.HasFile)
        {
            var coverFilePath = coverFile.Path!;
            OnPlatformProgress?.Invoke(PlatformNames.Xiaohongshu, "cover_upload", 70,
                coverFile.Source == PublishCoverSource.Custom ? "上传封面图..." : "上传封面（视频首帧）...");
            try
            {
                coverFileId = await new XhsUploadService(options)
                    .UploadImageAsync(cookie, coverFilePath, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;   // 用户暂停/删除选中：取消不是「封面失败」，别继续往下发布
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Xhs] 封面上传失败（非致命，用默认封面）：{File}",
                    coverFilePath);
            }
        }

        OnPlatformProgress?.Invoke(PlatformNames.Xiaohongshu, "publishing", 80, "发布中...");

        // 上传了自定义封面 → 新 fileId + 帧归 0；没有 → 不指定封面、帧照发
        var (resolvedCoverFileId, resolvedCoverFrameMs) =
            XhsPublishService.ResolveCover(coverFileId, payload.CoverFrameMs);

        // 可见性：**按名字映射，绝不强转 int**（老版强转导致隐私事故，缺陷 L11）
        var (privacyType, opType, needsUsers) = VisibilityMapping.Xhs(payload.Visibility);
        var privacyUserIds = needsUsers
            ? (payload.PrivacyUserIds ?? [])
            : []; // 不需要白名单的态一律清空 —— 老版把白名单丢在别的模式里

        // 发布体里的 video_info / composite_metadata 要带真实元数据 ——
        // 不填就落到默认值（1920×1080 / 10 秒），跟实际上传的视频（例如 34 分钟）差得离谱，
        // 平台侧可能据此拦发布（2026-10-25 排障发现：所有小红书发布都声明 10 秒）。
        // meta 在**上传前**已探好（上面还用来算转码轮询预算），这里直接复用。
        var publishReq = new XhsPublishRequest
        {
            Title = payload.Title ?? platform.Title ?? Path.GetFileNameWithoutExtension(videoFilePath),
            Description = payload.Caption ?? platform.Description ?? "",
            FileId = upResult.FileId,
            VideoId = upResult.VideoId,
            Width = meta is { Width: > 0 } ? meta.Width : 0,
            Height = meta is { Height: > 0 } ? meta.Height : 0,
            DurationMs = meta is { DurationS: > 0 } ? (int)Math.Round(meta.DurationS * 1000) : 0,
            // 自定义封面（2.5.3）：scene=image 上传后的 fileId；未上传（或上传失败）为 null，
            // 平台会从视频首帧自动生成。封面帧只在没有自定义封面时生效。
            CoverFileId = resolvedCoverFileId,
            CoverFrameMs = resolvedCoverFrameMs,
            CollectionId = payload.CollectionId,
            ScheduledAt = payload.ScheduledAt is { } xsa ? new DateTimeOffset(xsa) : null,
            Location = payload.Location is { IsUsable: true } poi
                ? new XhsPoiItem(poi.PoiId!, poi.PoiType ?? 12, poi.Name, poi.SubName)
                : null,
            PrivacyType = ToXhsPrivacyType(payload.Visibility),
            PrivacyUserIds = privacyUserIds,
            RepostSource = payload.Declaration == DeclarationKind.Repost
                ? payload.DeclarationDetail?.RepostSource : null,
            // 自行拍摄二级：拍摄地点 + 拍摄时间（yyyy-MM-dd）
            PhotoPoiId = payload.Declaration == DeclarationKind.SelfShot
                ? payload.DeclarationDetail?.ShootPoi?.PoiId : null,
            PhotoPoiName = payload.Declaration == DeclarationKind.SelfShot
                ? payload.DeclarationDetail?.ShootPoi?.Name : null,
            PhotoPoiSubname = payload.Declaration == DeclarationKind.SelfShot
                ? payload.DeclarationDetail?.ShootPoi?.SubName : null,
            PhotoTime = payload.Declaration == DeclarationKind.SelfShot
                && payload.DeclarationDetail?.ShootTime is { } st
                ? st.ToString("yyyy-MM-dd") : null,
            IsOriginal = payload.IsOriginal,
            ContentLabelValue = DeclarationMapping.XhsOrigin(payload.Declaration),
            CreatorUserId = CredentialFields.CookieValue(cookie, "x-user-id-creator.xiaohongshu.com"),
            Topics = payload.Topics?.Select(t => new XhsTopicItem(t.Id, t.Name, t.Type)).ToList(),
            // 长尾（阶段 2.3）
            AllowDownload = payload.AllowDownload,
            AllowComment = payload.AllowComment ?? true,
            Summary = payload.Summary,
            Chapters = payload.Chapters?
                .Select(c => new XhsChapter(c.Title, c.StartMs)).ToList(),
            SketchCollectionId = payload.SketchCollectionId,
            OrderBindJson = payload.OrderBind,
            ChapterSyncText = payload.ChapterSyncText,
            PkCoverBizRelationsJson = payload.PkCoverBizRelations,
            AtsJson = payload.Ats,
            GoodsInfoJson = payload.GoodsInfo,
            BizRelationsJson = payload.BizRelations,


        };

        XhsPublishResult pubResult;
        try
        {
            pubResult = await new XhsPublishService(options).PublishAsync(cookie, publishReq, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 用户暂停/删除选中：取消不是「发布失败」，原样上抛给队列
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Xhs] 发布失败：{Error}", ex.Message);
            if (ex.Message.Contains("401") || ex.Message.Contains("403"))
                return (false, null, "小红书登录已过期，请重新登录", null);
            return (false, null, $"发布失败: {ex.Message}", null);
        }

        if (pubResult is not { Success: true })
        {
            var err = pubResult?.ErrorMessage ?? "发布失败";
            Log.Warning("[Xhs] 发布失败：{Error}", err);
            return (false, null, err, null);
        }

        Log.Information("[Xhs] 发布成功 noteId={NoteId}", pubResult.NoteId);
        OnPlatformProgress?.Invoke(PlatformNames.Xiaohongshu, "published", 100,
            $"发布成功! {pubResult.NoteId}...");
        return (true, pubResult.NoteId, null, null);
    }

    /// <summary>统一可见性 → 小红书 SDK 枚举（**按名字，不按数值**）</summary>
    private static XhsPrivacyType ToXhsPrivacyType(PublishVisibility v) =>
        VisibilityMapping.Coerce(PlatformNames.Xiaohongshu, v) switch
        {
            PublishVisibility.Private => XhsPrivacyType.Private,
            PublishVisibility.MutualFriends => XhsPrivacyType.MutualFriends,
            PublishVisibility.OnlyUsers => XhsPrivacyType.OnlyUsers,
            PublishVisibility.ExcludeUsers => XhsPrivacyType.ExcludeUsers,
            _ => XhsPrivacyType.Public,
        };

    // ═══════════════════════════════════════════════════════════════
    //  B站（阶段 4）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// B站投稿：preupload → UPOS 分片 → cid → 封面（16:9 + 4:3）→ add/v3。
    ///
    /// 定时 = <c>add/v3.dtime</c>（Unix 秒；null = 立即，不下发该字段）。抓包时定时开关是关的，
    /// 字段名取自平台模型 <c>archive.dtime</c>，窗口 5 分钟 ~ 15 天（2026-11-07 页面实测）——
    /// 真账号探针首次定时发布时确认。
    ///
    /// 话题 <c>topic_id</c> / 活动 <c>mission_id</c> 的 wire 名同样待真账号探针，发送失败会被
    /// 平台拒或忽略（不静默丢失：预检 2.6.6 已提示无 ID 话题不会生效）。
    /// </summary>
    private async Task<(bool, string?, string?, string?)> ExecuteBilibiliAsync(
        string videoFilePath, PublishPlatformItem platform, CancellationToken ct)
    {
        var payload = PublishPayload.Deserialize<BilibiliPayload>(platform.ExtraDataJson);
        if (payload is null)
            return (false, null, "发布参数缺失或已损坏，请重新编辑该条目再发布", null);

        // 标签 1~10：builder 已裁到 10，这里只拦「一个都没有」（B站投稿至少要 1 个标签）
        var tags = payload.Tags ?? [];
        if (tags.Count == 0)
            return (false, null, "B站投稿至少需要 1 个标签（在该账号预设或 params.txt 里填写）", null);

        // AB 封面信息是原始 JSON 透传：**上传前**先验，别等传完几分钟才报 JSON 写错
        JsonElement? abCoverInfo;
        try
        {
            abCoverInfo = BilibiliPublishService.ParseOptionalJson(payload.AbCoverInfo, "AB 封面信息");
        }
        catch (BilibiliApiException ex)
        {
            return (false, null, ex.Message, null);
        }

        var creds = await FetchCredentialsAsync(platform);
        if (creds?.Cookie is null)
            return (false, null, "未找到B站登录凭证，请重新登录", null);

        var options = new PlatformOptions
        {
            Environment = CredentialFields.EnvironmentSnapshot(creds.ExtraDataJson),
            SessionId = platform.PlatformSessionId,
        };

        // ── 视频本体：preupload → UPOS 分片 → 合并 → 拿 cid ──
        var uploadSvc = new BilibiliUploadService(options);
        BilibiliUploadedVideo uploaded;
        try
        {
            OnPlatformProgress?.Invoke(PlatformNames.Bilibili, "uploading", 5, "上传视频中...");
            uploaded = await uploadSvc.UploadVideoAsync(creds.Cookie, videoFilePath,
                (done, total) =>
                {
                    var pct = 5 + (int)(done * 50.0 / Math.Max(1, total));
                    OnPlatformProgress?.Invoke(PlatformNames.Bilibili, "uploading", pct,
                        $"上传中 {pct}%");
                }, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (BilibiliApiException ex)
        {
            Log.Warning(ex, "[Bilibili] 视频上传失败：{File}", videoFilePath);
            return (false, null, ex.Message, null);
        }

        // ── 封面：用户图 → 自动抽帧 → 无（2.5.7 统一口径）；上传失败非致命 ──
        string? cover169 = null;
        string? cover43 = null;
        var coverFile = await ResolveCoverAsync(videoFilePath, platform, payload.CoverFrameMs, ct);
        if (coverFile.HasFile)
        {
            var workDir = Path.Combine(
                Path.GetTempPath(), "creatflow-bili-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                OnPlatformProgress?.Invoke(PlatformNames.Bilibili, "cover_upload", 60,
                    coverFile.Source == PublishCoverSource.Custom
                        ? "上传封面图..."
                        : "上传封面（视频首帧）...");
                var pair = await BilibiliCoverPreparer.PrepareAsync(coverFile.Path!, workDir, ct);
                var cover = await uploadSvc.UploadCoverAsync(
                    creds.Cookie, pair.Path16x9, pair.Path43, ct);
                (cover169, cover43) = cover.ToAddV3CoverFields();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;   // 用户暂停/删除选中：取消不是「封面失败」
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Bilibili] 封面上传失败（非致命，用平台默认封面）：{File}",
                    coverFile.Path);
            }
            finally
            {
                try { if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true); }
                catch { /* 临时目录清理失败不影响发布 */ }
            }
        }

        OnPlatformProgress?.Invoke(PlatformNames.Bilibili, "publishing", 85, "发布中...");

        // 话题/活动：只取第一个结构化 topic_id 与第一个 mission_id（wire 名待探针）
        long? topicId = null;
        if (payload.Topics is { Count: > 0 } topics
            && long.TryParse(topics[0].Id, out var parsedTopic) && parsedTopic > 0)
        {
            topicId = parsedTopic;
        }
        var missionId = payload.MissionIds?.FirstOrDefault(m => m > 0) is { } m && m > 0 ? m : (long?)null;

        var request = new BilibiliPublishRequest
        {
            Title = payload.Title ?? platform.Title ?? "",
            Description = payload.Caption ?? platform.Description ?? "",
            Tag = string.Join(",", tags),
            Filename = uploaded.Filename,
            Cid = uploaded.Cid,
            Cover = cover169,
            Cover43 = cover43,
            Tid = payload.Tid,
            HumanType2 = payload.HumanType2,
            Copyright = payload.Copyright,
            CreationStatementId = payload.CreationStatementId,
            Dynamic = payload.Dynamic,
            NoReprint = payload.NoReprint,
            Watermark = payload.Watermark,
            SubtitleOpen = payload.SubtitleOpen,
            SubtitleLan = payload.SubtitleLan,
            AllowComment = payload.AllowComment,
            AllowDanmaku = payload.AllowDanmaku,
            UpSelectionReply = payload.UpSelectionReply,
            ActReserve = payload.ActReserve,
            ActReserveTitle = payload.ActReserveTitle,
            AiCover = payload.AiCover,
            IsAbCover = payload.IsAbCover,
            AbCoverInfo = abCoverInfo,
            Interactive = payload.Interactive,
            SpaceHidden = payload.SpaceHidden,
            Dolby = payload.Dolby,
            LosslessMusic = payload.LosslessMusic,
            IsOnlySelf = VisibilityMapping.Bilibili(payload.Visibility) == 1,
            // 定时：dtime = Unix 秒；null = 立即（不下发该字段）
            Dtime = payload.ScheduledAt is { } dsa
                ? new DateTimeOffset(dsa).ToUnixTimeSeconds()
                : null,
            TopicId = topicId,
            MissionId = missionId,
        };

        try
        {
            var result = await new BilibiliPublishService(options)
                .PublishAsync(creds.Cookie, request, ct);
            var extraJson = JsonSerializer.Serialize(new
            {
                aid = result.Aid,
                bvid = result.Bvid,
            });
            Log.Information("[Bilibili] 发布成功 aid={Aid} bvid={Bvid}", result.Aid, result.Bvid);
            OnPlatformProgress?.Invoke(PlatformNames.Bilibili, "published", 100,
                $"发布成功! {result.Bvid}");
            return (true, result.Bvid, null, extraJson);
        }
        catch (OperationCanceledException) { throw; }
        catch (BilibiliApiException ex)
        {
            Log.Warning(ex, "[Bilibili] 投稿失败：{Error}", ex.Message);
            return (false, null, ex.Message, null);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  头条（阶段 5）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 头条视频发布：getAuthKey/auth_token → VOD 上传 → 封面（必填）→ PublishVideo。
    ///
    /// 横竖版在**执行时**按 ffprobe 结果定死：<c>VideoType</c> 3 横 / 6 竖；
    /// 竖版没有简介 / 定时 / 视频合集（抓包 `video_publish_vertical.json` 三样都不带）。
    /// 视频合集只支持横版（预检已 Warning）。
    /// </summary>
    private async Task<(bool, string?, string?, string?)> ExecuteToutiaoAsync(
        string videoFilePath, PublishPlatformItem platform, CancellationToken ct)
    {
        var payload = PublishPayload.Deserialize<ToutiaoPayload>(platform.ExtraDataJson);
        if (payload is null)
            return (false, null, "发布参数缺失或已损坏，请重新编辑该条目再发布", null);

        var title = (payload.Title ?? platform.Title ?? "").Trim();
        if (title.Length == 0)
            return (false, null, "头条标题不能为空", null);
        if (title.Length > PlatformLimits.ToutiaoTitleChars)
            title = title[..PlatformLimits.ToutiaoTitleChars];

        // 方向决定 VideoType（3 横 / 6 竖）与 VOD SpaceName；也用于 VideoInfo 宽高
        var meta = await ProbeMetaAsync(videoFilePath, ct);
        if (meta is null)
            return (false, null, "读不到视频信息（缺 ffprobe）—— 无法确定横竖版，头条发布已取消", null);
        var portrait = meta.Height > meta.Width;

        // 竖版抓包没有 TimerTime —— 定时了却静默立即发是最糟的失败模式，显式拦下
        if (portrait && payload.ScheduledAt is not null)
        {
            return (false, null,
                "头条竖版视频不支持定时发布 —— 请取消定时，或改用横版视频后再发", null);
        }

        // 原始 JSON 透传字段：**上传前**先验格式，别等传完几分钟才报 JSON 写错
        JsonElement? commodity, compass, attrs, etData;
        try
        {
            commodity = ToutiaoPublishService.ParseOptionalJson(payload.Commodity, "商品");
            compass = ToutiaoPublishService.ParseOptionalJson(payload.CompassVideo, "懂车帝视频结构");
            attrs = ToutiaoPublishService.ParseOptionalJson(payload.AttrsValueMap, "长尾属性");
            etData = ToutiaoPublishService.ParseOptionalJson(payload.EtData, "埋点扩展");
        }
        catch (ToutiaoPublishException ex)
        {
            return (false, null, ex.Message, null);
        }

        var creds = await FetchCredentialsAsync(platform);
        if (creds?.Cookie is null)
            return (false, null, "未找到头条登录凭证，请重新登录", null);

        var extra = CredentialFields.Extra(creds.ExtraDataJson);
        var csrfToken = extra.GetValueOrDefault("csrfToken")?.ToString();

        var options = new PlatformOptions
        {
            Environment = CredentialFields.EnvironmentSnapshot(creds.ExtraDataJson),
            SessionId = platform.PlatformSessionId,
        };

        var authSvc = new ToutiaoVodAuthService(options);

        OnPlatformProgress?.Invoke(PlatformNames.Toutiao, "refreshing_sts", 5, "获取上传凭证...");
        string userId;
        ToutiaoVodCredentials? sts;
        try
        {
            // user_id 是 VOD 的 user_id query / TOS 的 X-Storage-U 共用值（不是 media.id）
            userId = await authSvc.GetUserIdAsync(creds.Cookie, csrfToken, ct) ?? "";
            if (string.IsNullOrWhiteSpace(userId))
                return (false, null, "拿不到头条账号 user_id（登录态可能已过期），请重新登录", null);

            sts = await authSvc.GetAsync(creds.Cookie, csrfToken, portrait, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Toutiao] 获取上传凭证失败");
            return (false, null, "获取头条上传凭证失败：" + ex.Message, null);
        }

        if (sts is null)
        {
            return (false, null,
                "获取头条上传凭证失败（登录态或 secsdk 凭证失效）—— 打开头条账号窗口重新登录后再试", null);
        }

        // ── 视频本体（VOD 通用引擎，阶段 5.1）──
        OnPlatformProgress?.Invoke(PlatformNames.Toutiao, "uploading", 10, "上传视频中...");
        var ext = Path.GetExtension(videoFilePath).TrimStart('.');
        var upResult = await new ToutiaoUploadService(options).UploadAsync(
            videoFilePath,
            sts.AccessKeyId, sts.SecretAccessKey, sts.SessionToken,
            userId, portrait, sts.SpaceName, ext,
            onChunkProgress: (cur, total, up, all) =>
            {
                var pct = 10 + (int)(up * 45.0 / all);
                OnPlatformProgress?.Invoke(PlatformNames.Toutiao, "uploading", pct,
                    $"上传中 {cur}/{total} ({pct}%)");
                return Task.CompletedTask;
            },
            ct: ct);

        if (upResult is null)
            return (false, null, "视频上传失败", null);

        // ── 封面（平台必填）—— 2.5.7 统一：用户图 → 自动抽帧 ──
        OnPlatformProgress?.Invoke(PlatformNames.Toutiao, "cover_upload", 60,
            "上传封面...");
        ToutiaoVideoCover cover;
        try
        {
            var coverFile = await ResolveCoverAsync(
                videoFilePath, platform, payload.CoverFrameMs, ct);
            if (!coverFile.HasFile)
                return (false, null, ToutiaoVideoCoverService.CoverRequiredMessage, null);

            cover = await new ToutiaoVideoCoverService(options).UploadAsync(
                creds.Cookie, coverFile.Path, userId, csrfToken, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (ToutiaoVideoCoverException ex)
        {
            return (false, null, ex.Message, null);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Toutiao] 封面上传失败");
            return (false, null, "头条封面上传失败：" + ex.Message, null);
        }

        // ── 发布 ──
        OnPlatformProgress?.Invoke(PlatformNames.Toutiao, "publishing", 85, "发布中...");

        var request = new ToutiaoPublishRequest
        {
            Title = title,
            // 简介仅横版（竖版抓包没有 Abstract）
            Abstract = portrait ? null : payload.Caption,
            Vid = upResult.VideoId,
            VName = Path.GetFileName(videoFilePath),
            ThumbUri = cover.ThumbUri,
            ThumbUrl = cover.ThumbUrl,
            DurationSeconds = upResult.Duration > 0 ? upResult.Duration : meta.DurationS,
            Width = upResult.Width > 0 ? upResult.Width : meta.Width,
            Height = upResult.Height > 0 ? upResult.Height : meta.Height,
            VideoType = portrait ? 6 : 3,
            HideType = VisibilityMapping.Toutiao(payload.Visibility),
            // 定时仅横版；用与其它平台同口径的 Unix 秒
            TimerTime = !portrait && payload.ScheduledAt is { } at
                ? new DateTimeOffset(at).ToUnixTimeSeconds()
                : null,
            ClaimOrigin = payload.IsOriginal,
            Praise = payload.Praise,
            // 视频合集仅横版（竖版挂不上）
            PrependPSeriesId = portrait ? null : payload.CollectionId,
            ExternalLink = payload.ExternalLink,
            Label = payload.Tags,
            CoverTitles = payload.CoverTitle is { Length: > 0 } coverTitle
                ? [coverTitle]
                : null,
            TemplateName = payload.TemplateName,
            FilterName = payload.FilterName,
            Hashtags = payload.Hashtags is { Count: > 0 } hashtags
                ? hashtags.Select(h => new ToutiaoHashtag(h.Id, h.Name)).ToList()
                : null,
            Commodity = commodity,
            CompassVideo = compass,
            // SyncPlatform 线形未抓到（抓包恒 {}）—— v1 不发送结构化同步，保留默认 {}
            SyncPlatform = null,
            AttrsValueMap = attrs,
            EtData = etData,
        };

        try
        {
            var result = await new ToutiaoPublishService(options)
                .PublishAsync(creds.Cookie, csrfToken, request, ct);
            var extraJson = JsonSerializer.Serialize(new { itemId = result.ItemId });
            Log.Information("[Toutiao] 发布成功 ItemId={ItemId}", result.ItemId);
            OnPlatformProgress?.Invoke(PlatformNames.Toutiao, "published", 100,
                $"发布成功! {result.ItemId}");
            return (true, result.ItemId, null, extraJson);
        }
        catch (OperationCanceledException) { throw; }
        catch (ToutiaoPublishException ex)
        {
            Log.Warning(ex, "[Toutiao] 发布失败：{Error}", ex.Message);
            return (false, null, ex.Message, null);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Toutiao] 发布请求失败：{Error}", ex.Message);
            return (false, null, "头条发布失败：" + ex.Message, null);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  共用
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 取会话凭证。失败只警告不抛 —— 调用方按 <c>creds?.Cookie is null</c> 判定并
    /// 给出「请重新登录」的**平台相关**文案（这比抛一个通用异常有用得多）。
    /// </summary>
    private static async Task<PlatformSessionCredentials?> FetchCredentialsAsync(PublishPlatformItem platform)
    {
        if (platform.PlatformSessionId is not { } sessionId) return null;
        try
        {
            return await PlatformSessionApi.GetCredentialsAsync(sessionId);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Publish] 取会话凭证失败 session={SessionId}", sessionId);
            return null;
        }
    }




    /// <summary>从描述文本中提取 #话题 列表（不含 # 号）</summary>
    private static List<string> ParseHashtags(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return [];
        return Regex.Matches(description, @"#(\S+)")
            .Where(m => m.Groups[1].Success)
            .Select(m => m.Groups[1].Value)
            .ToList();
    }


    /// <summary>从 STS SessionToken 里挖出抖音 userId。</summary>
    private static string? ExtractUserId(string sessionToken)
    {
        try
        {
            var token = sessionToken.StartsWith("STS2") ? sessionToken[4..] : sessionToken;
            var padding = 4 - token.Length % 4;
            if (padding != 4) token += new string('=', padding);

            var outer = JsonSerializer.Deserialize<JsonElement>(Convert.FromBase64String(token));
            if (!outer.TryGetProperty("PolicyString", out var ps)) return null;
            var policy = JsonSerializer.Deserialize<JsonElement>(ps.GetString()!);
            if (!policy.TryGetProperty("Statement", out var stmts)) return null;
            var stmt = stmts.EnumerateArray().FirstOrDefault();
            if (!stmt.TryGetProperty("Condition", out var cond)) return null;
            var condStr = cond.GetString();
            if (string.IsNullOrWhiteSpace(condStr)) return null;
            var condObj = JsonSerializer.Deserialize<JsonElement>(condStr);
            if (!condObj.TryGetProperty("UserId", out var uid)) return null;
            return uid.ValueKind == JsonValueKind.Array
                ? uid.EnumerateArray().FirstOrDefault().GetString()
                : uid.GetString();
        }
        catch { return null; }
    }

    /// <summary>生成最小 JPEG 占位图（1×1 绿色）—— 仅在 ffmpeg 不可用时作最后兜底。</summary>
    private static void GeneratePlaceholderJpeg(string path)
    {
        // 对标 Python PIL: Image.new("RGB", (1,1), (7,193,96))
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0xFF, 0xDB, 0x00, 0x43, 0x00, 0x03, 0x02, 0x02, 0x03, 0x02, 0x02, 0x03, 0x03, 0x03, 0x03, 0x04, 0x03, 0x03, 0x04, 0x05, 0x08, 0x05, 0x05, 0x04, 0x04, 0x05, 0x0A, 0x07, 0x07, 0x06, 0x08, 0x0C, 0x0A, 0x0C, 0x0C, 0x0B, 0x0A, 0x0B, 0x0B, 0x0D, 0x0E, 0x12, 0x10, 0x0D, 0x0E, 0x11, 0x0E, 0x0B, 0x0B, 0x10, 0x16, 0x10, 0x11, 0x13, 0x14, 0x15, 0x15, 0x15, 0x0C, 0x0F, 0x17, 0x18, 0x16, 0x14, 0x18, 0x12, 0x14, 0x15, 0x14, 0xFF, 0xDB, 0x00, 0x43, 0x01, 0x03, 0x04, 0x04, 0x05, 0x04, 0x05, 0x09, 0x05, 0x05, 0x09, 0x14, 0x0D, 0x0B, 0x0D, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0x14, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x01, 0x00, 0x01, 0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01, 0xFF, 0xC4, 0x00, 0x1F, 0x00, 0x00, 0x01, 0x05, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0xFF, 0xC4, 0x00, 0xB5, 0x10, 0x00, 0x02, 0x01, 0x03, 0x03, 0x02, 0x04, 0x03, 0x03, 0x05, 0x05, 0x04, 0x04, 0x00, 0x00, 0x01, 0x7D, 0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07, 0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xA1, 0x08, 0x23, 0x42, 0xB1, 0xC1, 0x15, 0x52, 0xD1, 0xF0, 0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0A, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x25, 0x26, 0x27, 0x28, 0x29, 0x2A, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89, 0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3, 0xC4, 0xC5, 0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xE1, 0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8, 0xF9, 0xFA, 0xFF, 0xC4, 0x00, 0x1F, 0x01, 0x00, 0x03, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0xFF, 0xC4, 0x00, 0xB5, 0x11, 0x00, 0x02, 0x01, 0x02, 0x04, 0x04, 0x03, 0x04, 0x07, 0x05, 0x04, 0x04, 0x00, 0x01, 0x02, 0x77, 0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71, 0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91, 0xA1, 0xB1, 0xC1, 0x09, 0x23, 0x33, 0x52, 0xF0, 0x15, 0x62, 0x72, 0xD1, 0x0A, 0x16, 0x24, 0x34, 0xE1, 0x25, 0xF1, 0x17, 0x18, 0x19, 0x1A, 0x26, 0x27, 0x28, 0x29, 0x2A, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89, 0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3, 0xC4, 0xC5, 0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8, 0xF9, 0xFA, 0xFF, 0xDA, 0x00, 0x0C, 0x03, 0x01, 0x00, 0x02, 0x11, 0x03, 0x11, 0x00, 0x3F, 0x00, 0x8A, 0x8A, 0x28, 0xAF, 0x92, 0x3F, 0x87, 0x0F, 0xFF, 0xD9 };
        File.WriteAllBytes(path, jpeg);
    }

    // ═══ 回报与通知 ═══

    private async Task ReportAsync(Guid platformId, bool success,
        string? videoId, string? error, string? extraDataJson = null)
    {
        try
        {
            await ApiClient.PostAsync<object>("/api/publish/report",
                new ReportPublishRequest(platformId, success,
                    success ? videoId : null, null,
                    success ? null : error, null, extraDataJson));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Publish] 回报失败 platformId={PlatformId}", platformId);
        }
    }

    /// <summary>
    /// 回报服务端但**不抛**。存在的意义：请求已经出网、结果未知时也得把失败状态落库 ——
    /// 否则队列页轮询会把本地状态覆盖回 Waiting，用户看到任务永远卡在「等待中」。
    /// </summary>
    private async Task TryReportAsync(Guid platformId, bool success,
        string? videoId, string? error, string? extraDataJson = null)
    {
        try { await ReportAsync(platformId, success, videoId, error, extraDataJson); }
        catch (Exception ex) { Log.Warning(ex, "[Publish] TryReport 失败 platformId={Id}", platformId); }
    }

    private void Notify(bool success)
    {
        try
        {
            if (AppConfig.PublishSoundsEnabled) OnNotify?.Invoke(success);
        }
        catch (Exception ex) { Log.Debug(ex, "[Publish] 通知回调异常（忽略）"); }
    }

    private static string Short(string s, int n) => s.Length <= n ? s : s[..n];
}
