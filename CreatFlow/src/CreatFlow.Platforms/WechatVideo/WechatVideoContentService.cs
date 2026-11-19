namespace CreatFlow.Platforms.WechatVideo;

using CreatFlow.Platforms.Core;
using CreatFlow.Platforms.WechatVideo.Requests;
using Serilog;

/// <summary>
/// 微信视频号内容管理服务 — 发布、列表、删除、编辑视频。
///
/// 对标 Python fangai/wechatvideo/api.py 的 WechatAPI 类。
///
/// API 端点（全部 POST，Cookie + device_id 认证）：
///   Publish: /post/post_create
///   List:    /post/post_list
///   Delete:  /post/post_delete
///   Update:  /post/finder_update_post_info
///   Tags:    /post/finder_get_object_tag_list
/// </summary>
public sealed class WechatVideoContentService
{
    /// <summary>clip_video_result 轮询间隔（发布管线按它换算「等多久」）</summary>
    public static readonly TimeSpan ClipPollInterval = TimeSpan.FromSeconds(3);

    private readonly PlatformOptions _options;
    private readonly ILogger<WechatVideoContentService>? _logger;

    /// <summary>用户 UIN — 优先显式设置，否则从 Cookie 提取 wxuin</summary>
    public string? Uin { get; set; }

    /// <summary>finder_username（来自 localStorage），用作 _log_finder_id</summary>
    public string? FinderId { get; set; }

    public WechatVideoContentService(
        PlatformOptions options,
        ILogger<WechatVideoContentService>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    private void LogInfo(string message, params object[] args)
    {
        if (_logger is not null) _logger.LogInformation(message, args);
        else Log.Information(message, args);
    }

    private void LogDebug(string message, params object[] args)
    {
        if (_logger is not null) _logger.LogDebug(message, args);
        else Log.Debug(message, args);
    }

    private void LogWarn(string message, params object[] args)
    {
        if (_logger is not null) _logger.LogWarning(message, args);
        else Log.Warning(message, args);
    }

    private void LogWarn(Exception ex, string message, params object[] args)
    {
        if (_logger is not null) _logger.LogWarning(ex, message, args);
        else Log.Warning(ex, message, args);
    }

    // ═══ Publish — 发布视频 ═══

    /// <summary>
    /// 发布视频到微信视频号。
    ///
    /// 对标 Python: WechatAPI.create_post()
    ///
    /// 端点: POST /post/post_create
    /// </summary>
    /// <param name="cookie">登录 Cookie</param>
    /// <param name="deviceId">设备指纹</param>
    /// <param name="request">发布参数</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>发布结果（objectId），失败返回 null</returns>
    public async Task<WechatVideoPublishResult?> PublishAsync(
        string cookie,
        string deviceId,
        WechatVideoPublishRequest request,
        CancellationToken ct = default)
    {
        using var client = new WechatVideoApiClient(_options, cookie, deviceId, uin: Uin, finderId: FinderId);

        // 生成任务 ID
        var taskId = request.VideoClipTaskId
            ?? (Guid.NewGuid().GetHashCode() & 0x7FFFFFFF).ToString();

        var body = BuildPostBody(request, taskId, FinderId, DateTimeOffset.UtcNow);

        LogDebug("[WechatVideo] Publish → videoUrl={Url}", request.VideoUrl);

        var resp = await client.PostAsync("/post/post_create", body,
            "https://channels.weixin.qq.com/micro/content/post/create", ct);

        if (resp.ErrCode != 0)
            return new WechatVideoPublishResult(null, false, resp.ErrMsg ?? $"errCode={resp.ErrCode}");

        // 微信 post_create 返回 data.baseResp.errcode（无 objectId）
        // errCode=0 即发布成功
        LogInfo("[WechatVideo] Publish OK: errCode=0");
        return new WechatVideoPublishResult("ok", true, null);
    }

    /// <summary>短标题下限 —— 页面实测 5 字报「标题至少6个字」，整条会被拒。</summary>
    public const int ShortTitleMinChars = 6;

    /// <summary>短标题上限 —— 页面输入框 20 字。</summary>
    public const int ShortTitleMaxChars = 20;

    /// <summary>
    /// 构建 <c>post_create</c> 请求体（**纯函数**，自检可直接断言 wire 字段）。
    ///
    /// 抓包依据（docs/wechatvideo/20261105 + wire-2026-11-11）：
    ///   · <c>postFlag</c> 是**声明位域**（2026-11-11 B2 A/B 实测：同一草稿只切换「声明原创」，
    ///     走完确认弹窗后 bit0 0→1、取消回落 0，定时开关全程没动）——
    ///     立即 / 定时**只由 <c>effectiveTime</c> 表达**；05 文档「立即 1 / 定时 0」是相关性误读
    ///     （wire-2026-11-11 §2.3 已更正）。本项目只置 bit0（原创），
    ///     bit1/bit2（Original_Auth / Member）不下发；
    ///   · 定时发表 + Unix 秒 <c>effectiveTime</c>；立即**不下发**该键；
    ///   · 未选标注时 <c>tagInfo</c> 整个键不出现（定时/图文两次抓包都没有）；
    ///   · 未参加活动时 <c>objectDesc.event</c> 是 <c>{}</c>；参加时是
    ///     <c>{eventTopicId, eventName, eventCreatorNickname}</c>（wire-2026-11-11 §1）；
    ///   · @好友：<c>objectDesc.mentionedUser = [{nickname}]</c>，正文末尾补 <c>@昵称 </c>
    ///     （结尾空格；wire-2026-11-11 §5）；
    ///   · 无位置时 <c>objectDesc.location</c> 整个键不出现；
    ///   · 无链接时 <c>extReading</c> 是 <c>{}</c>；有链接时 <c>{link, title, urlType}</c>
    ///     （urlType: 1 公众号文章 / 2 红包封面 / 0 未选，wire-2026-11-11 §3）；
    ///   · 短标题 <c>[{shortTitle}]</c>，不足 6 字页面直接报错 → 本项目**不下发**该字段。
    /// </summary>
    public static Dictionary<string, object?> BuildPostBody(
        WechatVideoPublishRequest request, string taskId, string? finderId, DateTimeOffset now)
    {
        var topics = request.Topics ?? [];
        // 去掉描述中已有的 #话题（从 Pipeline 的 ParseHashtags 提取），避免重复拼接
        var descBase = (request.Description ?? "").Trim();
        foreach (var t in topics)
            descBase = descBase.Replace($"#{t}", "").Replace("  ", " ").Trim();
        var descWithTopics = descBase;
        if (topics.Count > 0)
            descWithTopics = descBase + " " + string.Join(" ", topics.Select(t => $"#{t}")) + " ";

        // @好友（2026-11-11 B2 §5）：正文末尾补「@昵称 」（**结尾带空格**，与页面内联节点
        // 序列化同形）。正文里已有的 @昵称 不重复补 —— 手写与选人两条路都保留；
        // 手写在句尾的（后面没空格）只补一个分隔空格，不重复昵称。
        var descWithMentions = descWithTopics;
        if (request.MentionUsers is { Count: > 0 } mentionUsers)
        {
            foreach (var nickname in mentionUsers)
            {
                var nick = (nickname ?? "").Trim();
                if (nick.Length == 0) continue;
                var token = "@" + nick;

                var boundary = FindMentionBoundary(descWithMentions, token);
                if (boundary >= 0)
                {
                    if (boundary == descWithMentions.Length) descWithMentions += " ";
                    continue;
                }

                if (descWithMentions.Length > 0 && !descWithMentions.EndsWith(' '))
                    descWithMentions += " ";
                descWithMentions += token + " ";
            }
        }

        // 构建 topic XML（finderTopicInfo）
        var topicXmlParts = new List<string>
        {
            $"<value0><![CDATA[{descWithMentions}]]></value0>",
        };
        foreach (var t in topics)
            topicXmlParts.Add($"<value1><topic><![CDATA[#{t}#]]></topic></value1>");
        topicXmlParts.Add("<value2><![CDATA[ ]]></value2>");

        var topicXml = "<finder>"
            + "<version>1</version>"
            + $"<valuecount>{topicXmlParts.Count}</valuecount>"
            + "<style><at></at></style>"
            + string.Join("", topicXmlParts)
            + "</finder>";

        var uploadCdnStart = (int)(now.ToUnixTimeSeconds() - 60);
        var uploadCdnEnd = (int)now.ToUnixTimeSeconds();

        var objectDesc = new Dictionary<string, object?>
        {
            ["mpTitle"] = "",
            ["description"] = descWithMentions,
            ["extReading"] = BuildExtReading(request),
            ["mediaType"] = 4, // video
            ["topic"] = new Dictionary<string, object?>
            {
                ["finderTopicInfo"] = topicXml,
                ["collectionId"] = request.CollectionId ?? "",
                ["collectionName"] = request.CollectionName ?? "",
            },
            // 官方活动：未参加 = {}；参加 = 三件套原样（2026-11-11 B2 §1.2）
            ["event"] = request.Event is { } ev
                ? new Dictionary<string, object?>
                {
                    ["eventTopicId"] = ev.EventTopicId,
                    ["eventName"] = ev.EventName ?? "",
                    ["eventCreatorNickname"] = ev.EventCreatorNickname ?? "",
                }
                : new Dictionary<string, object?>(),
            // @好友：只带昵称（2026-11-11 B2 §5.3；username 不进提交体）
            ["mentionedUser"] = request.MentionUsers is { Count: > 0 } muted
                ? muted.Where(n => !string.IsNullOrWhiteSpace(n))
                    .Select(n => new Dictionary<string, object?> { ["nickname"] = n.Trim() })
                    .ToArray()
                : Array.Empty<object>(),
            ["media"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["url"] = request.VideoUrl,
                    ["fileSize"] = request.FileSize.ToString(),
                    ["thumbUrl"] = request.CoverUrl ?? request.ThumbUrl ?? "",
                    ["fullThumbUrl"] = request.CoverUrl ?? request.ThumbUrl ?? "",
                    ["mediaType"] = 4,
                    ["videoPlayLen"] = request.Duration,
                    ["width"] = request.Width,
                    ["height"] = request.Height,
                    ["md5sum"] = request.Md5Sum ?? "",
                    ["cardShowStyle"] = request.CardShowStyle,
                    ["coverUrl"] = request.CoverUrl ?? request.ThumbUrl ?? "",
                    ["fullCoverUrl"] = request.CoverUrl ?? request.ThumbUrl ?? "",
                    ["shareCoverUrl"] = request.CoverUrl ?? request.ThumbUrl ?? "",
                    ["urlCdnTaskId"] = taskId,
                },
            },
            ["member"] = new { postWithMemberZoneLink = 0 },
        };

        // 无位置 → **整个 location 字段不出现**（接口说明书 §5.2；不是空对象）
        if (request.Location is not null)
            objectDesc["location"] = request.Location;

        // 短标题：不足 6 字不下发；超过 20 字截断
        if (NormalizeShortTitle(request.ShortTitle) is { } shortTitle)
        {
            objectDesc["shortTitle"] = new[] { new { shortTitle } };
        }

        var body = new Dictionary<string, object?>
        {
            ["objectType"] = request.ObjectType,
            ["longitude"] = 0,
            ["latitude"] = 0,
            ["feedLongitude"] = 0,
            ["feedLatitude"] = 0,
            ["originalFlag"] = request.OriginalFlag,
            ["topics"] = topics,
            ["isFullPost"] = 1,
            ["handleFlag"] = 2,
            ["videoClipTaskId"] = taskId,
            ["traceInfo"] = new
            {
                traceKey = $"FPT_{uploadCdnEnd}_{Random.Shared.Next(0, 999999999)}",
                uploadCdnStart,
                uploadCdnEnd,
            },
            ["objectDesc"] = objectDesc,
            ["report"] = new Dictionary<string, object?>
            {
                ["clipKey"] = taskId,
                ["draftId"] = taskId,
                ["timestamp"] = now.ToUnixTimeMilliseconds().ToString(),
                ["_log_finder_uin"] = "",
                ["_log_finder_id"] = finderId ?? "v2_060000231003b20faec8cae08e1cc3d1c70ded37b077cfefe945fdb82dfe6641a73b44b04c16@finder",
                // 对标 Python post_create: report 内也需要注入上报参数
                ["rawKeyBuff"] = "",
                ["pluginSessionId"] = null!,
                ["scene"] = 7,
                ["reqScene"] = 7,
                ["height"] = request.Height,
                ["width"] = request.Width,
                ["duration"] = request.Duration,
                ["fileSize"] = request.FileSize,
                ["uploadCost"] = (uploadCdnEnd - uploadCdnStart) * 100,
            },
            // 声明位域：bit0 = 声明原创（2026-11-11 B2 A/B 实测，wire-2026-11-11 §2）。
            // ⚠️ 立即 / 定时**不在这里**表达 —— 定时只靠 effectiveTime（见下）。
            // 旧实现「立即 1 / 定时 0」会把每条立即发布的视频都当成声明原创，已修。
            ["postFlag"] = request.IsOriginal ? 1 : 0,
            ["mode"] = 1,
            ["clientid"] = Guid.NewGuid().ToString(),
        };

        // 标注未选 → 不下发 tagInfo（2026-11-05 定时/图文抓包无该键）
        if (request.TagInfo is not null)
            body["tagInfo"] = request.TagInfo;

        // 定时才下发 effectiveTime（Unix 秒）；立即发表不下发（2026-11-05 抓包）
        if (request.ScheduledAt.HasValue)
        {
            body["effectiveTime"] = new DateTimeOffset(
                DateTime.SpecifyKind(request.ScheduledAt.Value, DateTimeKind.Utc))
                .ToUnixTimeSeconds();
        }

        return body;
    }

    /// <summary>
    /// 找正文里「@昵称」的命中位置（返回命中的**下一个**字符下标；没命中返回 -1）。
    /// 只认后面是空白 / 标点 / 行尾的命中 —— 防止「@知鸭」误吞「@知鸭鸭」。
    /// </summary>
    private static int FindMentionBoundary(string text, string token)
    {
        var idx = 0;
        while ((idx = text.IndexOf(token, idx, StringComparison.Ordinal)) >= 0)
        {
            var end = idx + token.Length;
            if (end == text.Length || char.IsWhiteSpace(text[end]) || char.IsPunctuation(text[end]))
                return end;
            idx = end;
        }
        return -1;
    }

    /// <summary>
    /// 短标题规范化：trim → 不足 6 字返回 null（不下发）、超过 20 字截断。
    /// 页面实测 5 字报「标题至少6个字」，会把整条发布拦下。
    /// </summary>
    public static string? NormalizeShortTitle(string? shortTitle)
    {
        var s = shortTitle?.Trim();
        if (string.IsNullOrEmpty(s) || s.Length < ShortTitleMinChars) return null;
        return s.Length <= ShortTitleMaxChars ? s : s[..ShortTitleMaxChars];
    }

    /// <summary>
    /// 扩展链接卡片（<c>objectDesc.extReading</c>）。
    /// 无链接 = <c>{}</c>（2026-11-05 抓包三次全是 <c>{}</c>）；
    /// 有链接 = <c>{link, title, urlType}</c> —— **键名是 urlType 不是 type**，
    /// 枚举 1 = 公众号文章 / 2 = 红包封面 / 0 = 未选（wire-2026-11-11 §3 草稿提交体裁剪）。
    /// </summary>
    private static object BuildExtReading(WechatVideoPublishRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ExternalLink))
            return new Dictionary<string, object?>();

        return new Dictionary<string, object?>
        {
            ["link"] = request.ExternalLink,
            ["title"] = request.ExternalLinkTitle ?? "",
            ["urlType"] = request.ExternalLinkType,
        };
    }

    // ═══ Clip Video — 视频裁剪（post_create 前置步骤，必须调用） ═══

    /// <summary>
    /// 提交视频裁剪任务，获取 clipKey（即 videoClipTaskId）。
    ///
    /// 对标 Python: WechatAPI.clip_video()
    /// 端点: POST /post/post_clip_video
    ///
    /// post_create 之前必须先调用此接口。传入上传后的原始 URL，
    /// 返回 clipKey 用于轮询处理和最终发布。
    /// </summary>
    public async Task<string?> ClipVideoAsync(
        string cookie,
        string deviceId,
        string videoUrl,
        int width = 0,
        int height = 0,
        double duration = 0.0,
        long fileSize = 0,
        int useAstraThumb = 1,
        CancellationToken ct = default)
    {
        using var client = new WechatVideoApiClient(_options, cookie, deviceId, uin: Uin, finderId: FinderId);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var body = new Dictionary<string, object?>
        {
            ["url"] = videoUrl,
            ["timeStart"] = 0,
            ["cropDuration"] = 0,
            ["height"] = height,
            ["width"] = width,
            ["x"] = 0,
            ["y"] = 0,
            ["clipOriginVideoInfo"] = new
            {
                width,
                height,
                duration,
                fileSize,
            },
            ["traceInfo"] = new
            {
                traceKey = $"FPT_{now}_{Random.Shared.Next(0, 999999999)}",
                uploadCdnStart = now - 60,
                uploadCdnEnd = now,
            },
            ["targetWidth"] = width,
            ["targetHeight"] = height,
            ["type"] = 4,
            ["useAstraThumbCover"] = useAstraThumb,
        };

        LogDebug("[WechatVideo] clip_video → url={Url}", videoUrl[..Math.Min(60, videoUrl.Length)]);

        var resp = await client.PostAsync("/post/post_clip_video", body, ct: ct);

        if (resp.ErrCode != 0)
        {
            LogWarn("[WechatVideo] clip_video failed: errCode={Code}, errMsg={Msg}",
                resp.ErrCode, resp.ErrMsg);
            return null;
        }

        var clipData = JsonSerializer.Deserialize<ClipVideoData>(
            resp.Data.GetRawText(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var clipKey = clipData?.ClipKey;

        LogInfo("[WechatVideo] clip_video OK: clipKey={Key}", clipKey);
        return clipKey;
    }

    /// <summary>
    /// 查询视频裁剪结果。poll=false 只查一次，poll=true 轮询直到 flag=1。
    ///
    /// 对标 Python: WechatAPI.clip_video_result()
    /// 端点: POST /post/post_clip_video_result
    ///
    /// 返回的 data.url 是 post_create 应该使用的最终视频 URL（含 basedata + sign）。
    /// </summary>
    /// <param name="cookie">登录 Cookie</param>
    /// <param name="deviceId">设备指纹</param>
    /// <param name="clipKey">clip_video 返回的 clipKey</param>
    /// <param name="draftId">草稿 ID（默认等于 clipKey）</param>
    /// <param name="maxRetries">最大轮询次数（默认 30，每次间隔 <see cref="ClipPollInterval"/>）</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>裁剪结果（url, width, height, duration, md5, fileSize），未完成返回 null</returns>
    /// <remarks>
    /// 窗口要按视频时长给够：实测 42s/123s 视频 7~10 秒就 flag=1，而 34 分钟 1080p
    /// 在旧的 60 次（3 分钟）里两次都没转完（2026-10-25 教学视频，被误判「视频处理超时」）。
    /// 调用方（PublishPipeline）按元数据算次数，见 WechatClipPollRetries。
    /// </remarks>
    public async Task<ClipVideoResultData?> ClipVideoResultAsync(
        string cookie,
        string deviceId,
        string clipKey,
        string? draftId = null,
        int maxRetries = 30,
        CancellationToken ct = default)
    {
        using var client = new WechatVideoApiClient(_options, cookie, deviceId, uin: Uin, finderId: FinderId);
        var startedAt = DateTime.UtcNow;

        for (int i = 0; i < maxRetries; i++)
        {
            var body = new Dictionary<string, object?>
            {
                ["clipKey"] = clipKey,
                ["draftId"] = draftId ?? clipKey,
            };

            var resp = await client.PostAsync("/post/post_clip_video_result", body, ct: ct);

            if (resp.ErrCode != 0)
            {
                LogWarn("[WechatVideo] clip_video_result failed: errCode={Code}, errMsg={Msg}",
                    resp.ErrCode, resp.ErrMsg);
                return null;
            }

            var result = JsonSerializer.Deserialize<ClipVideoResultData>(
                resp.Data.GetRawText(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (result?.Flag == 1)
            {
                LogInfo("[WechatVideo] clip_video_result done: {W}x{H}, {Dur}s",
                    result.Width, result.Height, result.Duration);
                return result;
            }

            if (i < maxRetries - 1)
            {
                // flag 一并打出来：排障时要能区分「一直在处理(0)」和「返回了别的值」
                LogDebug("[WechatVideo] clip_video_result polling... retry={Retry}/{Max} flag={Flag}",
                    i + 1, maxRetries, result?.Flag ?? -1);
                await Task.Delay(ClipPollInterval, ct);
            }
        }

        LogWarn("[WechatVideo] clip_video_result timeout after {Max} retries（已等 {Elapsed:F0}s；" +
            "视频可能仍在转码，可稍后重试或加长窗口）", maxRetries,
            (DateTime.UtcNow - startedAt).TotalSeconds);
        return null;
    }

    // ═══ List — 视频列表 ═══

    /// <summary>
    /// 获取视频列表。
    ///
    /// 端点: POST /post/post_list
    /// </summary>
    public async Task<WechatVideoListResult?> ListAsync(
        string cookie,
        string deviceId,
        int page = 1,
        int pageSize = 20,
        int userpageType = 11,
        CancellationToken ct = default)
    {
        using var client = new WechatVideoApiClient(_options, cookie, deviceId, uin: Uin, finderId: FinderId);

        var body = new Dictionary<string, object?>
        {
            ["pageSize"] = pageSize,
            ["currentPage"] = page,
            ["userpageType"] = userpageType,
            ["stickyOrder"] = true,
        };

        var resp = await client.PostAsync("/post/post_list", body,
            "https://channels.weixin.qq.com/micro/content/post/list", ct);

        if (resp.ErrCode != 0) return null;

        var listData = JsonSerializer.Deserialize<PostListData>(
            resp.Data.GetRawText(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        return listData is null ? null : new WechatVideoListResult(
            listData.List ?? [],
            listData.TotalCount,
            listData.ContinueFlag);
    }

    /// <summary>
    /// 诊断：原样返回 <c>post_list</c> 的 <c>data</c> JSON —— <c>--probe-wechat-list</c> 用。
    /// 业务路径不要用；它存在的意义是回答「新发的作品进没进列表 / urlCdnTaskId 还对不对得上
    /// clipKey / 官方活动挂上没有」这类**字段核对**问题（与 <see cref="PostListItem"/> 的
    /// 半成品映射无关，直接看平台原样返回）。
    /// </summary>
    public async Task<string?> ListRawAsync(
        string cookie,
        string deviceId,
        int page = 1,
        int pageSize = 20,
        int userpageType = 11,
        CancellationToken ct = default)
    {
        using var client = new WechatVideoApiClient(_options, cookie, deviceId, uin: Uin, finderId: FinderId);
        var body = new Dictionary<string, object?>
        {
            ["pageSize"] = pageSize,
            ["currentPage"] = page,
            ["userpageType"] = userpageType,
            ["stickyOrder"] = true,
        };
        var resp = await client.PostAsync("/post/post_list", body,
            "https://channels.weixin.qq.com/micro/content/post/list", ct);
        return resp.ErrCode == 0 ? resp.Data.GetRawText() : null;
    }

    /// <summary>
    /// 发布成功后回查 post_list 拿到 objectId。微信 post_create 不直接返回 objectId，需从列表回查。
    ///
    /// 两种匹配锚点（2026-11-14 实测修正）：
    ///   · <c>desc.media[0].urlCdnTaskId == clipKey</c> —— 2026-11-05 抓包里的形状；
    ///   · <c>desc.media[0].md5sum == md5</c> —— **当前平台（2026-11-14）列表项里已没有
    ///     <c>urlCdnTaskId</c> 这个键**，只按 clipKey 匹配永远查不到（objectId 恒 null →
    ///     report 500 卡住队列行）。列表按时间倒序，第一个 md5 命中就是刚发的那条。
    /// </summary>
    /// <param name="md5">clip_video_result 返回的成片 MD5（与列表项 media[0].md5sum 同源）</param>
    public async Task<string?> FindObjectIdByClipKeyAsync(
        string cookie, string deviceId, string clipKey, string? md5 = null,
        int maxPages = 3, CancellationToken ct = default)
    {
        for (int page = 1; page <= maxPages; page++)
        {
            using var client = new WechatVideoApiClient(_options, cookie, deviceId, uin: Uin, finderId: FinderId);
            var body = new Dictionary<string, object?>
            {
                ["pageSize"] = 20,
                ["currentPage"] = page,
                ["userpageType"] = 11,
                ["stickyOrder"] = true,
            };
            var resp = await client.PostAsync("/post/post_list", body,
                "https://channels.weixin.qq.com/micro/content/post/list", ct);
            if (resp.ErrCode != 0) return null;

            var rawList = resp.Data.GetRawText();
            using var doc = JsonDocument.Parse(rawList);
            if (!doc.RootElement.TryGetProperty("list", out var list)) return null;

            foreach (var item in list.EnumerateArray())
            {
                var oid = item.TryGetProperty("objectId", out var idEl) ? idEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(oid)) continue;

                string? taskId = null, itemMd5 = null;
                if (item.TryGetProperty("desc", out var desc)
                    && desc.TryGetProperty("media", out var media)
                    && media.ValueKind == JsonValueKind.Array
                    && media.GetArrayLength() > 0)
                {
                    taskId = media[0].TryGetProperty("urlCdnTaskId", out var tId) ? tId.GetString() : null;
                    itemMd5 = media[0].TryGetProperty("md5sum", out var md5El) ? md5El.GetString() : null;
                }

                var byClipKey = !string.IsNullOrEmpty(taskId) && taskId == clipKey;
                var byMd5 = !string.IsNullOrWhiteSpace(md5) && !string.IsNullOrWhiteSpace(itemMd5)
                            && string.Equals(itemMd5, md5, StringComparison.OrdinalIgnoreCase);
                if (byClipKey || byMd5)
                {
                    LogInfo("[WechatVideo] Found objectId by {Anchor}={Key}: {ObjectId}",
                        byClipKey ? "clipKey" : "md5",
                        byClipKey ? clipKey : md5, oid);
                    return oid;
                }
            }

            // 检查是否还有下一页
            if (!doc.RootElement.TryGetProperty("continueFlag", out var cf)
                || cf.ValueKind != JsonValueKind.True)
                break;
        }
        LogWarn("[WechatVideo] ObjectId not found for clipKey={ClipKey} md5={Md5} after {Pages} pages",
            clipKey, md5 ?? "-", maxPages);
        return null;
    }

    // ═══ Delete — 删除视频 ═══

    /// <summary>
    /// 删除指定视频。
    ///
    /// 端点: POST /post/post_delete
    /// </summary>
    /// <param name="cookie">登录 Cookie</param>
    /// <param name="deviceId">设备指纹</param>
    /// <param name="objectId">视频 objectId（如 "export/UzFf..."）</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>删除成功返回 true</returns>
    public async Task<bool> DeleteAsync(
        string cookie,
        string deviceId,
        string objectId,
        CancellationToken ct = default)
    {
        using var client = new WechatVideoApiClient(_options, cookie, deviceId, uin: Uin, finderId: FinderId);

        var body = new Dictionary<string, object?> { ["objectId"] = objectId };

        LogDebug("[WechatVideo] Delete → objectId={Id}", objectId);

        var resp = await client.PostAsync("/post/post_delete", body,
            "https://channels.weixin.qq.com/micro/content/post/list", ct);

        var ok = resp.ErrCode == 0;
        if (!ok) LogWarn("[WechatVideo] Delete failed: errCode={Code}, errMsg={Msg}", resp.ErrCode, resp.ErrMsg);
        return ok;
    }

    // ═══ Visibility — 可见性（发布后独立操作） ═══

    /// <summary>可见性 wire 值：公开</summary>
    public const int VisibleTypePublic = 1;
    /// <summary>可见性 wire 值：仅自己</summary>
    public const int VisibleTypePrivate = 3;

    /// <summary>
    /// 设置作品可见性。
    ///
    /// 端点: POST /post/post_update_visible
    /// 抓包: <c>docs/wechatvideo/20261105/requests/post_update_visible_private.json</c>
    ///
    /// ⚠️ 视频号的可见性**不是发布参数**：<c>post_create</c> 体里没有这个字段（默认公开），
    /// 私密必须发布成功后单独调这里（字段映射 §2.6）。平台只有两态：公开 / 仅自己。
    /// </summary>
    /// <param name="visibleType"><see cref="VisibleTypePublic"/> / <see cref="VisibleTypePrivate"/></param>
    /// <returns>成功返回 null；失败返回平台错误文案（合集视频会回 -11220）</returns>
    public async Task<string?> UpdateVisibleAsync(
        string cookie,
        string deviceId,
        string objectId,
        int visibleType,
        CancellationToken ct = default)
    {
        using var client = new WechatVideoApiClient(_options, cookie, deviceId, uin: Uin, finderId: FinderId);

        var body = new Dictionary<string, object?>
        {
            ["objectId"] = objectId,
            ["visibleType"] = visibleType,
        };

        LogDebug("[WechatVideo] UpdateVisible → objectId={Id}, visibleType={Type}",
            objectId, visibleType);

        // pageUrl 取抓包的 referer：这个操作在「作品列表」页，不在发布页
        var resp = await client.PostAsync("/post/post_update_visible", body,
            "https://channels.weixin.qq.com/micro/content/post/finderNewLifePostList", ct);

        if (resp.ErrCode != 0)
        {
            var msg = $"errCode={resp.ErrCode}, errMsg={resp.ErrMsg}";
            LogWarn("[WechatVideo] UpdateVisible failed: {Error}", msg);
            return msg;
        }

        // 成功体是 data.errorCode / data.msg；平台拒绝（如合集视频 -11220）
        // 走 data.baseResp.errcode / errmsg —— 此时顶层 errCode 仍是 0
        var nested = ReadVisibleError(resp.Data);
        if (nested is not null)
        {
            LogWarn("[WechatVideo] UpdateVisible rejected: {Error}", nested);
            return nested;
        }

        LogInfo("[WechatVideo] UpdateVisible ok → objectId={Id}, visibleType={Type}",
            objectId, visibleType);
        return null;
    }

    /// <summary>
    /// 从 <c>post_update_visible</c> 的 <c>data</c> 里取错误文案（成功返回 null）。
    /// 纯函数，自检可直接断言两种错误形态。
    /// </summary>
    public static string? ReadVisibleError(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;

        if (data.TryGetProperty("errorCode", out var code)
            && code.ValueKind == JsonValueKind.Number
            && code.GetInt32() != 0)
        {
            var msg = data.TryGetProperty("msg", out var m) ? m.GetString() : null;
            return $"errorCode={code.GetInt32()}, msg={msg ?? ""}";
        }

        if (data.TryGetProperty("baseResp", out var baseResp)
            && baseResp.ValueKind == JsonValueKind.Object
            && baseResp.TryGetProperty("errcode", out var errEl)
            && errEl.ValueKind == JsonValueKind.Number
            && errEl.GetInt32() != 0)
        {
            var msg = baseResp.TryGetProperty("errmsg", out var em) ? em.GetString() : null;
            return $"errcode={errEl.GetInt32()}, errmsg={msg ?? ""}";
        }

        return null;
    }

    // ═══ Update — 编辑视频信息 ═══

    /// <summary>
    /// 编辑/更新视频信息。
    ///
    /// 端点: POST /post/finder_update_post_info
    /// </summary>
    public async Task<bool> UpdateAsync(
        string cookie,
        string deviceId,
        string objectId,
        Dictionary<string, object?> postReq,
        CancellationToken ct = default)
    {
        using var client = new WechatVideoApiClient(_options, cookie, deviceId, uin: Uin, finderId: FinderId);

        postReq["objectId"] = objectId;
        var body = new Dictionary<string, object?>
        {
            ["displayDraftId"] = objectId,
            ["postReq"] = postReq,
        };

        LogDebug("[WechatVideo] Update → objectId={Id}", objectId);

        var resp = await client.PostAsync("/post/finder_update_post_info", body,
            "https://channels.weixin.qq.com/micro/content/post/create", ct);

        var ok = resp.ErrCode == 0;
        if (!ok) LogWarn("[WechatVideo] Update failed: errCode={Code}, errMsg={Msg}", resp.ErrCode, resp.ErrMsg);
        return ok;
    }

    // ═══ Cover — 封面（发布前直传 / 发布后 coverEdit 入口） ═══

    /// <summary>
    /// 发布后「修改封面」页面路径（接口说明书 §9，2026-11-05 抓包）：
    /// <c>https://channels.weixin.qq.com/platform/post/coverEdit?objectId=…</c>。
    /// </summary>
    public const string CoverEditPagePath = "/platform/post/coverEdit";

    /// <summary>创作中心 Origin（coverEdit 页面所在域）。</summary>
    public const string CoverEditPageOrigin = "https://channels.weixin.qq.com";

    /// <summary>
    /// 发布后自动改封面 **是否可用** —— 恒 false：coverEdit 页的保存接口从未抓包。
    ///
    /// 2026-11-05 抓包只覆盖了页面入口与三个封面位；同页的 <c>finderModFeed</c>
    /// 只带 description/shortTitle（<c>requests/finder_mod_feed.json</c>），没有
    /// 任何 cover 字段。按「不猜 wire 结构」纪律，这里不拼未知请求，只提供页面地址，
    /// 由 UI 文案引导用户手动改。
    /// </summary>
    public static bool PublishedCoverEditSupported => false;

    /// <summary>不支持自动改封面时给用户看的说明（含手动入口）。</summary>
    public const string PublishedCoverEditUnavailableReason =
        "视频号「发布后修改封面」的保存接口尚未抓包，请到创作中心 → 作品 → 修改封面 手动操作";

    /// <summary>
    /// 生成 coverEdit 页地址。<paramref name="objectId"/> 为空 = 发布响应里没拿到
    /// objectId（回查失败），此时返回 null —— 该页面必须带 objectId 才可用。
    /// </summary>
    public static string? BuildCoverEditPageUrl(string? objectId)
        => string.IsNullOrWhiteSpace(objectId)
            ? null
            : $"{CoverEditPageOrigin}{CoverEditPagePath}"
              + $"?objectId={Uri.EscapeDataString(objectId.Trim())}";

    /// <summary>
    /// coverEdit 页可编辑的三个封面位（接口说明书 §9.1：视频封面 /
    /// 个人主页卡片 3:4 / 分享卡片 4:3）。视频封面文档没给比例 → AspectRatio = null（不编造）。
    /// </summary>
    public static IReadOnlyList<WechatVideoCoverSlot> CoverEditSlots { get; } =
    [
        new("video", "视频封面", null),
        new("profile", "个人主页卡片", 3.0 / 4.0),
        new("share", "分享卡片", 4.0 / 3.0),
    ];

    /// <summary>
    /// 发布前封面来源的选择规则（纯函数）：用户选的封面图 → 视频首帧 → 1×1 占位图
    /// （最后兜底，因为视频号封面**必填**，空则 300801）。
    ///
    /// 页面行为（2026-11-05 capture 06-12-51 行 325-339）：发布前会上传一张
    /// 20304 图片（本地抽帧或用户选图），completepartuploaddfs 返回的 DownloadURL
    /// 就是 post_create 里五个封面子字段用的地址（host 由 <c>wxapp.tc.qq.com</c>
    /// 换成 <c>finder.video.qq.com</c>，与 <see cref="WechatVideoUploadService"/>
    /// 的替换一致）。
    /// </summary>
    public static WechatVideoCoverSource ChooseCoverSource(
        string? customCoverPath, string? firstFramePath)
    {
        if (!string.IsNullOrWhiteSpace(customCoverPath) && File.Exists(customCoverPath))
            return WechatVideoCoverSource.CustomCover;
        if (!string.IsNullOrWhiteSpace(firstFramePath) && File.Exists(firstFramePath))
            return WechatVideoCoverSource.FirstFrame;
        return WechatVideoCoverSource.Placeholder;
    }

    // ═══ Tags — 获取标签列表 ═══

    /// <summary>
    /// 获取视频标注标签列表（用于 tagInfo）。
    ///
    /// 端点: POST /post/finder_get_object_tag_list
    /// </summary>
    /// <param name="cookie">登录 Cookie</param>
    /// <param name="deviceId">设备指纹</param>
    /// <param name="source">来源（默认 1）</param>
    /// <param name="ct">取消令牌</param>
    public async Task<(List<ObjectTagItem> Tags, string? TagKey)> GetTagListAsync(
        string cookie,
        string deviceId,
        int source = 1,
        CancellationToken ct = default)
    {
        using var client = new WechatVideoApiClient(_options, cookie, deviceId, uin: Uin, finderId: FinderId);

        var body = new Dictionary<string, object?> { ["source"] = source };

        var resp = await client.PostAsync("/post/finder_get_object_tag_list", body,
            "https://channels.weixin.qq.com/micro/content/post/create", ct);

        if (resp.ErrCode != 0) return ([], null);

        var tagData = JsonSerializer.Deserialize<ObjectTagListData>(
            resp.Data.GetRawText(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        return (tagData?.TagList ?? [], tagData?.TagKey);
    }
}

// ═══ Public Request/Response Types ═══

/// <summary>
/// 微信视频号发布请求参数。
/// </summary>
public sealed record WechatVideoPublishRequest(
    /// <summary>上传完成的视频 HTTPS URL（必填）</summary>
    string VideoUrl,

    /// <summary>视频文件大小（字节）</summary>
    long FileSize,

    /// <summary>缩略图 URL</summary>
    string? ThumbUrl = null,

    /// <summary>封面图 URL</summary>
    string? CoverUrl = null,

    /// <summary>视频描述</summary>
    string? Description = null,

    /// <summary>话题列表（不含 # 号）</summary>
    List<string>? Topics = null,

    /// <summary>位置信息 JSON（latitude, longitude, city, poiName, address, poiClassifyId）</summary>
    object? Location = null,

    /// <summary>合集 ID（如 "event/UzFf..."）</summary>
    string? CollectionId = null,

    /// <summary>合集名称</summary>
    string? CollectionName = null,

    /// <summary>视频时长（秒）</summary>
    double Duration = 0.0,

    /// <summary>视频宽度</summary>
    int Width = 1920,

    /// <summary>视频高度</summary>
    int Height = 1080,

    /// <summary>视频文件 MD5（UUID 格式）</summary>
    string? Md5Sum = null,

    /// <summary>上传任务 ID（clipKey）</summary>
    string? VideoClipTaskId = null,

    /// <summary>媒体卡片样式（0=默认 / 1=平铺 / 2=居中；页面横版默认 2）</summary>
    int CardShowStyle = 2,

    /// <summary>短标题（不足 6 字不下发、超过 20 字截断）</summary>
    string? ShortTitle = null,

    /// <summary>原创标记 —— 顶层 <c>originalFlag</c>，实测恒 0（2026-11-11 B2：勾没勾声明都是 0；
    /// 真正的原创位在 <see cref="IsOriginal"/> → <c>postFlag</c> bit0）</summary>
    int OriginalFlag = 0,

    /// <summary>声明原创 —— <c>postFlag</c> bit0（2026-11-11 B2 A/B 实测：
    /// 走完「声明原创」确认弹窗后 0→1，取消回落 0）</summary>
    bool IsOriginal = false,

    /// <summary>内容类型（0=默认）</summary>
    int ObjectType = 0,

    /// <summary>扩展链接 URL —— objectDesc.extReading.link（null/空 = 不挂链接卡片）</summary>
    string? ExternalLink = null,

    /// <summary>链接卡片标题 —— extReading.title（选填）</summary>
    string? ExternalLinkTitle = null,

    /// <summary>链接类型 —— extReading.urlType（1 公众号文章 / 2 红包封面；wire-2026-11-11 §3）</summary>
    int ExternalLinkType = 1,

    /// <summary>官方活动 —— <c>objectDesc.event</c>（null/空 = 不参加活动，下发 <c>{}</c>）。
    /// 形状 <c>{eventTopicId, eventName, eventCreatorNickname}</c>（2026-11-11 B2 §1）
    /// </summary>
    WechatVideoEvent? Event = null,

    /// <summary>@好友昵称 —— <c>objectDesc.mentionedUser = [{nickname}]</c>，同时正文末尾补
    /// <c>@昵称 </c>（2026-11-11 B2 §5；平台只认昵称）</summary>
    IReadOnlyList<string>? MentionUsers = null,

    /// <summary>定时发布时间（null=立即）</summary>
    DateTime? ScheduledAt = null,

    /// <summary>视频标注信息</summary>
    object? TagInfo = null
);

/// <summary>
/// 官方活动 —— 发布体 <c>objectDesc.event</c> 的三件套（2026-11-11 B2 抓包 §1）。
/// 三项都来自 <c>post/post_search_event</c> 的选中项，平台不再按 ID 二次查询。
/// </summary>
public sealed record WechatVideoEvent(
    string EventTopicId,
    string? EventName = null,
    string? EventCreatorNickname = null);

/// <summary>发布前封面来源（用户封面 / 视频首帧 / 占位图兜底）</summary>
public enum WechatVideoCoverSource
{
    /// <summary>用户选的自定义封面图（先经图片直传换 URL）</summary>
    CustomCover,

    /// <summary>自动抽取的视频首帧</summary>
    FirstFrame,

    /// <summary>1×1 占位图（抽帧失败时的最后兜底，封面必填）</summary>
    Placeholder,
}

/// <summary>coverEdit 页的封面位（视频封面 / 个人主页卡片 3:4 / 分享卡片 4:3）</summary>
public sealed record WechatVideoCoverSlot(string Key, string Label, double? AspectRatio);

/// <summary>发布结果</summary>
public sealed record WechatVideoPublishResult(
    string? ObjectId,
    bool Success,
    string? ErrorMessage
);

/// <summary>视频列表结果</summary>
public sealed record WechatVideoListResult(
    List<PostListItem> Items,
    int TotalCount,
    bool ContinueFlag
);
