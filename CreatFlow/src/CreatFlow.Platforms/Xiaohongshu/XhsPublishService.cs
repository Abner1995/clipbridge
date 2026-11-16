using System.Globalization;

namespace CreatFlow.Platforms.Xiaohongshu;

/// <summary>
/// 小红书发布服务 — 创建/更新笔记。
///
/// 对标 Python examples/xiaohongshu/media/add.py + update.py。
///
/// API:
///   Publish: POST /web_api/sns/v2/note
///   Update:  PUT  /web_api/sns/capa/postgw/note/update
/// </summary>
public sealed class XhsPublishService
{
    private readonly PlatformOptions _options;
    private readonly ILogger<XhsPublishService>? _logger;

    public XhsPublishService(PlatformOptions options, ILogger<XhsPublishService>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// 发布视频笔记到小红书。
    /// </summary>
    public async Task<XhsPublishResult> PublishAsync(
        string cookie,
        XhsPublishRequest request,
        CancellationToken ct = default)
    {
        using var client = new XhsApiClient(cookie, _options,
            _logger as ILogger<PlatformHttpClient>);

        var noteData = BuildNotePayload(request);
        _logger?.LogInformation("XHS publish: title={Title}, fileId={FileId}",
            request.Title, request.FileId);

        LogDeclarationDiagnostic(request, noteData);

        try
        {
            var resp = await client.PostEdithRawAsync(
                "/web_api/sns/v2/note", noteData, ct);

            var (noteId, errorMsg) = ExtractNoteIdOrError(resp);

            if (!string.IsNullOrWhiteSpace(noteId))
            {
                _logger?.LogInformation("XHS publish OK: noteId={NoteId}", noteId);
                return new XhsPublishResult(noteId, true, null);
            }

            // Publish failed — extract XHS error message
            var raw = resp.GetRawText();
            _logger?.LogWarning("XHS publish failed: {Resp}", raw[..Math.Min(500, raw.Length)]);
            Serilog.Log.Warning("[XHS Publish] Publish rejected: {Resp}", raw[..Math.Min(800, raw.Length)]);

            var finalError = !string.IsNullOrWhiteSpace(errorMsg) ? errorMsg : "发布失败";
            return new XhsPublishResult("", false, finalError);
        }
        // 用户暂停/撤回「上传队列」时不能把取消吞成「发布失败」——
        // 否则队列会把这条标成失败（2026-10-22 实测栈：XhsSignHandler → CookieAuthHandler
        // → LoggingHandler → HttpClient）。取消必须原样上抛，由队列按「用户主动停止」处理。
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "XHS publish failed");
            return new XhsPublishResult("", false, ex.Message);
        }
    }

    /// <summary>
    /// 更新已发布的笔记。
    /// 对标 Python examples/xiaohongshu/media/update.py。
    /// </summary>
    /// <param name="cookie">Cookie</param>
    /// <param name="noteId">要更新的笔记 ID</param>
    /// <param name="request">更新参数</param>
    /// <param name="ct">取消令牌</param>
    public async Task<XhsPublishResult> UpdateAsync(
        string cookie,
        string noteId,
        XhsPublishRequest request,
        CancellationToken ct = default)
    {
        using var client = new XhsApiClient(cookie, _options,
            _logger as ILogger<PlatformHttpClient>);

        var noteData = BuildNotePayload(request, noteId);
        _logger?.LogInformation("XHS update: noteId={NoteId}, title={Title}",
            noteId, request.Title);

        LogDeclarationDiagnostic(request, noteData);

        try
        {
            var resp = await client.PutEdithRawAsync(
                "/web_api/sns/capa/postgw/note/update", noteData, ct);

            var (resultNoteId, _) = ExtractNoteIdOrError(resp);
            resultNoteId ??= noteId;

            _logger?.LogInformation("XHS update OK: noteId={NoteId}", resultNoteId);
            return new XhsPublishResult(resultNoteId, true, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // 取消不吞（同 PublishAsync 的注释）
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "XHS update failed: noteId={NoteId}", noteId);
            return new XhsPublishResult(noteId, false, ex.Message);
        }
    }

    // ═══ 封面取值（2.5.3）═══

    /// <summary>
    /// 封面的最终取值：自定义封面图上传成功 → 用新 fileId、帧归 0
    /// （页面 cover.frame = <c>{ts:0, is_upload:true}</c>）；
    /// 未上传（或上传失败）→ 不指定 <c>cover.file_id</c>（平台用视频首帧），帧照发。
    /// 与快手 <c>KuaishouCoverService.ResolveCover</c> 同口径，自检钉住。
    /// </summary>
    public static (string? CoverFileId, long? CoverFrameMs) ResolveCover(
        string? uploadedFileId, long? frameMs)
        => string.IsNullOrWhiteSpace(uploadedFileId)
            ? (null, frameMs)
            : (uploadedFileId, 0);

    // ═══ Diagnostics ═══

    /// <summary>
    /// 诊断日志 — 有声明（原创/视频标注）时打印 business_binds 全文。
    /// HTTP 日志只保留头尾各 300/200 字符，business_binds 在中间看不到，
    /// 这里单独打出来，用于排查「发布成功但声明无效」。
    /// </summary>
    private static void LogDeclarationDiagnostic(XhsPublishRequest req,
        Dictionary<string, object?> noteData)
    {
        if (!req.IsOriginal && req.ContentLabelValue is null) return;

        try
        {
            var binds = (string?)null;
            if (noteData.TryGetValue("common", out var c)
                && c is Dictionary<string, object?> common
                && common.TryGetValue("business_binds", out var bb)
                && bb is string s)
                binds = s;

            Serilog.Log.Debug(
                "[XHS Publish] declaration diagnostic: isOriginal={IsOriginal}, " +
                "contentLabel={Label}, creatorUserId=[{Uid}], business_binds={Binds}",
                req.IsOriginal, req.ContentLabelValue,
                req.CreatorUserId ?? "(null)", binds ?? "(missing)");
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning("[XHS Publish] declaration diagnostic failed: {Err}", ex.Message);
        }
    }

    // ═══ Note Payload Builder ═══

    /// <summary>
    /// 构建笔记发布/更新 JSON payload。
    /// 对标 Python add.py 的 json_data 结构。
    ///
    /// public（原 internal）是为了让 <c>--selftest</c> 能直接断言纯函数产物。
    /// 2026-10-25 排障发现这里两处错：定时 postTime 被贴成 UTC（发布被推后 8 小时）、
    /// 元数据没传导致所有视频都声明 10 秒 —— 自检把这两条钉住。
    /// </summary>
    public static Dictionary<string, object?> BuildNotePayload(
        XhsPublishRequest req, string? noteId = null)
    {
        var w = req.Width > 0 ? req.Width : 1920;
        var h = req.Height > 0 ? req.Height : 1080;
        var durationMs = req.DurationMs > 0 ? req.DurationMs : 10000;
        var durationSec = durationMs / 1000.0;

        // ── 话题标签 ──
        var hashTag = new List<object>();
        if (req.Topics is { Count: > 0 })
        {
            foreach (var t in req.Topics)
                hashTag.Add(new Dictionary<string, object?>
                {
                    ["id"] = t.Id,
                    ["name"] = t.Name,
                    ["type"] = t.Type,
                });
        }

        // ── 互动投票（A4）──
        // 发布体把 widget 挂进 common.hash_tag：末尾追加
        // {"id":<widget_id>,"name":"问题","type":"INTERACT_VOTE"}。
        // 页面 hashTag 合并函数是「先过滤同类型、再 concat」—— 同类型条目只能有一个，
        // 否则平台可能重复挂/拒掉（bundle 逐字，2026-11-10 抓包）。
        if (!string.IsNullOrWhiteSpace(req.PollWidgetId)
            && !string.IsNullOrWhiteSpace(req.PollQuestion))
        {
            hashTag.RemoveAll(t => t is Dictionary<string, object?> d
                && d.TryGetValue("type", out var type)
                && type is string typeText
                && typeText == XhsPollRules.HashTagType);

            hashTag.Add(new Dictionary<string, object?>
            {
                ["id"] = req.PollWidgetId,
                ["name"] = req.PollQuestion,
                ["type"] = XhsPollRules.HashTagType,
            });
        }

        // ── 添加路线（2026-11-15）──
        // 页面 travelSaved → hash_tag 追加 {id:travelId, name:routeName, type:"map_traver"}。
        // 没有路线时不带该条目；同类型条目先滤再加（页面 merge 同款）。
        if (req.RouteBind is { TravelId.Length: > 0, RouteName.Length: > 0 } routeTag)
        {
            hashTag.RemoveAll(t => t is Dictionary<string, object?> d
                && d.TryGetValue("type", out var type)
                && type is string typeText
                && typeText == "map_traver");
            hashTag.Add(new Dictionary<string, object?>
            {
                ["id"] = routeTag.TravelId,
                ["name"] = routeTag.RouteName,
                ["type"] = "map_traver",
            });
        }

        // ── desc 话题标记 ──
        // 对标 add.py: desc 里的话题必须写成 "#话题名[话题]#" 格式才会被小红书标记，
        // 纯 "#话题名" 会被当成普通文本。只转换 hash_tag 里有 ID 的话题。
        var desc = req.Description ?? "";
        if (req.Topics is { Count: > 0 } && !string.IsNullOrWhiteSpace(desc))
        {
            // 按名字长度降序转换，防止 "#旅行" 破坏 "#旅行日记"。
            // (?<!\w) / (?!\w) 保证话题名前后是词边界（中文也算 \w），
            // (?!\[话题\]) 防止已转换过的 "##name[话题]#" 被重复转换。
            foreach (var t in req.Topics.OrderByDescending(t => t.Name.Length))
            {
                if (string.IsNullOrWhiteSpace(t.Name)) continue;
                var pattern = @"(?<!\w)#" + System.Text.RegularExpressions.Regex.Escape(t.Name)
                    + @"(?!\w|\[话题\])";
                desc = System.Text.RegularExpressions.Regex.Replace(desc, pattern,
                    "#" + t.Name + "[话题]#");
            }
        }

        // ── desc 路线标记 ──
        // 页面把路线做成编辑器节点，正文里是 "#路线名[map_traver]#"（隐藏 span 带后缀）。
        // 正文没有就补在末尾 —— 保证正文与 hash_tag 对得上（与页面插入节点同义）。
        if (req.RouteBind is { TravelId.Length: > 0, RouteName.Length: > 0 } routeDesc)
        {
            var routeToken = $"#{routeDesc.RouteName}[map_traver]#";
            if (!desc.Contains(routeToken, StringComparison.Ordinal))
            {
                var routePattern = @"(?<!\w)#"
                    + System.Text.RegularExpressions.Regex.Escape(routeDesc.RouteName)
                    + @"(?!\w|\[map_traver\])";
                desc = System.Text.RegularExpressions.Regex.IsMatch(desc, routePattern)
                    ? System.Text.RegularExpressions.Regex.Replace(desc, routePattern, routeToken)
                    : string.IsNullOrWhiteSpace(desc)
                        ? routeToken
                        : desc.TrimEnd() + "\n" + routeToken;
            }
        }

        // ── 内容声明（视频标注） ──
        var (optionRelationList, userDeclarationBind) =
            XhsContentLabelOptions.BuildDeclBind(
                req.ContentLabelValue,
                req.RepostSource,
                req.PhotoPoiId,
                req.PhotoPoiName,
                req.PhotoPoiSubname,
                req.PhotoTime,
                req.IsOriginal,
                req.CreatorUserId);

        // ── 定时发布 ──
        // postTime 是绝对时刻的 Unix 毫秒 —— 直接取 ScheduledAt 的绝对时间。
        // 旧写法 new DateTimeOffset(dt.DateTime, TimeSpan.Zero) 把「本地墙上时间」重新贴成 UTC，
        // 结果定时被推后 8 小时（2026-10-25 实测：界面选明天 20:00 → postTime = 次日 04:00）。
        // 立即发布 = postTime 0（2026-11-05 抓包；bizType 随之为 0）。
        var notePostTiming = new Dictionary<string, object?>
        {
            ["postTime"] = req.ScheduledAt?.ToUnixTimeMilliseconds() ?? 0,
        };

        // ── 合集绑定 ──
        var noteCollectionBind = new Dictionary<string, object?>
        {
            ["id"] = req.CollectionId ?? "",
        };

        // ── 订单/品牌绑定（原始 JSON 对象透传） ──
        var noteOrderBind = ParseJsonValue(req.OrderBindJson, "订单绑定", JsonValueKind.Object);

        // ── 用户 @ / 带货 / 业务关系（原始 JSON 透传） ──
        var ats = ParseJsonValue(req.AtsJson, "@ 用户", JsonValueKind.Array);
        var goodsInfo = ParseJsonValue(req.GoodsInfoJson, "带货商品信息", JsonValueKind.Object);
        var bizRelations = ParseJsonValue(req.BizRelationsJson, "业务关系", JsonValueKind.Array);

        // ── 章节（wire: [{text, time:"<毫秒>"}]，取自页面 Vue bundle） ──
        // 按时间升序 + 同一时间点只留第一条（页面编辑器不允许重复；与抖音 BuildChapterJson 同口径）
        var chapters = new List<object>();
        var seenChapterMs = new HashSet<long>();
        foreach (var chapter in (req.Chapters ?? []).OrderBy(c => c.StartMs))
        {
            var ms = Math.Max(0, chapter.StartMs);
            if (!seenChapterMs.Add(ms)) continue;
            chapters.Add(new Dictionary<string, object?>
            {
                ["text"] = chapter.Text ?? "",
                ["time"] = ms.ToString(CultureInfo.InvariantCulture),
            });
        }

        // ── PK 封面业务关联（原始 JSON 数组透传） ──
        var pkCoverBizRelations = ParseJsonValue(
            req.PkCoverBizRelationsJson, "PK 封面业务关联", JsonValueKind.Array);

        // ── privacy_info ──
        var privacyInfo = BuildPrivacyInfo(req);

        // ── business_binds ──
        var businessBinds = new Dictionary<string, object?>
        {
            ["version"] = 1,
            ["noteId"] = 0,
            // 0 = 立即发布 / 13 = 定时发布（2026-11-05 实测；旧实现恒 13）
            ["bizType"] = req.ScheduledAt.HasValue ? 13 : 0,
            ["noteOrderBind"] = noteOrderBind,
            ["notePostTiming"] = notePostTiming,
            ["noteCollectionBind"] = noteCollectionBind,
            ["noteSketchCollectionBind"] = new Dictionary<string, object?>
            {
                ["id"] = req.SketchCollectionId ?? "",
            },
            // 页面发布体**没有** coProduceBind（2026-11-05 抓包；合作共创未开时不下发）
            ["noteCopyBind"] = new Dictionary<string, object?> { ["copyable"] = req.AllowDownload },
            ["interactionPermissionBind"] = new Dictionary<string, object?>
            {
                // 0 = 允许评论 / 1 = 不允许（抓包只见到 0）
                ["commentPermission"] = req.AllowComment ? 0 : 1,
            },
        };

        // 合并声明 + 引用笔记 + 关联活动（A5/A6）——
        // 三者共用同一个 optionRelationList 数组；同一类型先过滤再追加（页面同款 merge）。
        businessBinds["optionRelationList"] = XhsRelationBinds.Merge(
            optionRelationList as IEnumerable<object>,
            req.ReferencedNoteId,
            req.Activities);

        if (userDeclarationBind != null)
            businessBinds["userDeclarationBind"] = userDeclarationBind;

        // 选择群聊（2026-11-15）：页面 bundle 的 wire 是 {groupId,groupName,desc,avatar}；
        // 未选 = 不带该键（与旧发布体逐字一致，平台已验证可接受）。
        if (req.GroupBind is { GroupId.Length: > 0 } groupBind)
        {
            businessBinds["groupBind"] = new Dictionary<string, object?>
            {
                ["groupId"] = groupBind.GroupId,
                ["groupName"] = groupBind.GroupName,
                ["desc"] = groupBind.Desc ?? "",
                ["avatar"] = groupBind.Avatar ?? "",
            };
        }

        // ── post_loc ──
        object? postLoc = null;
        if (req.Location != null)
        {
            postLoc = new Dictionary<string, object?>
            {
                ["poi_id"] = req.Location.PoiId,
                ["poi_type"] = req.Location.PoiType,
                ["subname"] = req.Location.Subname ?? "",
                ["name"] = req.Location.Name,
            };
        }

        // ── video metadata ──
        var videoMeta = new Dictionary<string, object>
        {
            ["bitrate"] = req.VideoBitrate > 0 ? req.VideoBitrate : 0,
            ["colour_primaries"] = "BT.709",
            ["duration"] = durationMs,
            ["format"] = !string.IsNullOrWhiteSpace(req.VideoCodec) ? req.VideoCodec : "AVC",
            ["frame_rate"] = req.FrameRate > 0 ? req.FrameRate : 30,
            ["height"] = h,
            ["matrix_coefficients"] = "BT.709",
            ["rotation"] = req.Rotation,
            ["transfer_characteristics"] = "BT.709",
            ["width"] = w,
        };

        var fileId = req.FileId ?? "";
        var coverFileId = req.CoverFileId ?? fileId;

        // ── 封面帧（2.5.3）──
        // wire 是**秒**（接口说明书 §4.2：cover.frame.ts 抽帧时间戳，默认 0）。
        // 上传了自定义封面图时帧不生效：页面封面上传确认体是 frametime:0 / isUpload:true /
        // isUserSelect:false（2026-11-05 page Vue bundle 的 confirm 参数），
        // 与「选视频帧」的 frametime≠0 / isUpload:false / isUserSelect:true 互斥。
        var hasUploadedCover = !string.IsNullOrWhiteSpace(req.CoverFileId);
        var coverFrameSec = hasUploadedCover ? 0d : Math.Max(0, req.CoverFrameMs ?? 0) / 1000.0;

        // ── common ──
        var common = new Dictionary<string, object?>
        {
            ["type"] = "video",
            ["note_id"] = noteId ?? "",
            ["source"] = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["type"] = "web",
                ["ids"] = "",
                ["extraInfo"] = JsonSerializer.Serialize(new { subType = "official", systemId = "web" }),
            }),
            ["title"] = req.Title ?? "",
            ["desc"] = desc,
            ["ats"] = ats,
            ["hash_tag"] = hashTag,
            ["business_binds"] = JsonSerializer.Serialize(businessBinds),
            ["privacy_info"] = privacyInfo,
            ["goods_info"] = goodsInfo,
            // PK 封面条目并入 common.biz_relations（线上 bundle 2026-11-15：
            // `bizRelations: (bizRelation||[]).filter(!AI_VIDEO_COVER).concat(pkCoverBizRelations)`）；
            // 添加路线追加 POI_MAP_TRAVEL（页面 travelSaved 同款）。video_info.pk_cover_biz_relations
            // 同时保留（页面两处都发）。
            ["biz_relations"] = MergeBizRelations(
                bizRelations, pkCoverBizRelations, BuildRouteBizRelation(req.RouteBind)),
            ["capa_trace_info"] = new Dictionary<string, object?>
            {
                ["contextJson"] = JsonSerializer.Serialize(new
                {
                    recommend_title = new { recommend_title_id = "", is_use = 3, used_index = -1 },
                    recommendTitle = new List<object>(),
                    recommend_topics = new { used = new List<object>() },
                }),
            },
        };

        // post_loc (null 时不发字段)
        if (postLoc != null)
            common["post_loc"] = postLoc;

        // metadata（更新接口用）
        if (!string.IsNullOrWhiteSpace(noteId))
        {
            common["metadata"] = "{}";
        }

        return new Dictionary<string, object?>
        {
            ["common"] = common,
            ["image_info"] = null,
            ["video_info"] = new Dictionary<string, object?>
            {
                ["fileid"] = fileId,
                ["file_id"] = fileId,
                ["format_width"] = w,
                ["format_height"] = h,
                ["video_preview_type"] = "",
                ["composite_metadata"] = new Dictionary<string, object>
                {
                    ["video"] = videoMeta,
                    ["audio"] = new Dictionary<string, object>
                    {
                        ["bitrate"] = req.AudioBitrate,
                        ["channels"] = 2,
                        ["duration"] = durationMs,
                        ["format"] = !string.IsNullOrWhiteSpace(req.AudioCodec) ? req.AudioCodec : "AAC",
                        ["sampling_rate"] = 48000,
                    },
                },
                ["cover"] = new Dictionary<string, object?>
                {
                    ["fileid"] = coverFileId,
                    ["file_id"] = coverFileId,
                    ["height"] = h,
                    ["width"] = w,
                    ["frame"] = new Dictionary<string, object?>
                    {
                        ["ts"] = coverFrameSec,
                        // 用户选了帧（ts≠0）→ true；上传封面图 → false（两者互斥，页面同款）
                        ["is_user_select"] = !hasUploadedCover && coverFrameSec > 0,
                        ["is_upload"] = hasUploadedCover,
                    },
                    ["stickers"] = new { version = 2, neptune = new List<object>() },
                    ["fonts"] = new List<object>(),
                },
                ["chapters"] = chapters,
                ["summary"] = req.Summary ?? "",
                ["chapter_sync_text"] = req.ChapterSyncText ?? false,
                // 视频标记的 wire 字段是 **timelines**（线上 bundle `timelines: U()`，
                // 2026-11-15 实测 `video_info.markers` 被平台静默忽略）
                ["timelines"] = BuildMarkers(req.Markers, w, h),
                ["segments"] = new Dictionary<string, object?>
                {
                    ["count"] = 1,
                    ["need_slice"] = false,
                    ["items"] = new List<object>
                    {
                        new Dictionary<string, object?>
                        {
                            ["mute"] = 0,
                            ["speed"] = 1,
                            ["start"] = 0,
                            ["duration"] = durationSec,
                            ["transcoded"] = 0,
                            ["media_source"] = 1,
                            ["original_metadata"] = new Dictionary<string, object>
                            {
                                ["video"] = videoMeta,
                                ["audio"] = new Dictionary<string, object>(),
                            },
                        },
                    },
                },
                ["entrance"] = "web",
                ["pk_cover_biz_relations"] = pkCoverBizRelations,
            },
        };
    }

    /// <summary>
    /// 组装视频标记 —— <c>video_info.markers</c>（2026-11-15）。
    ///
    /// 线上 bundle（<c>project-publish-vue.d001adc9.js</c>）逐字：
    /// <code>
    ///   新建标记（选朋友）：{visibility:true, type:"USER", startTime:0, endTime:0,
    ///     config:{style:1, type:"user", event:{value:{id:userId, name:nick}}},
    ///     markId:userId, key:Date.now().toString(), active:true}
    ///   地点：type:"LOCATION" / config.type:"location"，markId/event.value.id = poiId
    ///   定位补全：config.container_size = `{${img.height}, ${img.width}}`
    ///             config.unit_center = "{0.5, 0.5}" / anchor_center=null / markY=0.5
    ///   发布：{type:"video_marks_v2", start:1000*startTime, end:1000*endTime,
    ///          detail:{id:markId, ...config}}
    /// </code>
    /// 只认 <c>user</c> / <c>location</c>（页面其它类型是商品标记，本产品不做）；
    /// 缺 ID / 名称的条目跳过。空表返回空数组（旧发布体同款）。
    /// </summary>
    public static List<object> BuildMarkers(
        IReadOnlyList<XhsMarkerInput>? markers, int videoWidth, int videoHeight)
    {
        var list = new List<object>();
        if (markers is not { Count: > 0 }) return list;

        foreach (var m in markers)
        {
            var id = (m.Id ?? "").Trim();
            var name = (m.Name ?? "").Trim();
            var type = (m.Type ?? "").Trim().ToLowerInvariant();
            if (id.Length == 0 || name.Length == 0) continue;
            if (type is not ("user" or "location")) continue;

            var startSec = Math.Max(0, m.StartSeconds);
            var endSec = Math.Max(startSec, m.EndSeconds);

            list.Add(new Dictionary<string, object?>
            {
                ["type"] = "video_marks_v2",
                ["start"] = startSec * 1000,
                ["end"] = endSec * 1000,
                ["detail"] = new Dictionary<string, object?>
                {
                    ["id"] = id,
                    ["style"] = 1,
                    ["type"] = type,
                    ["event"] = new Dictionary<string, object?>
                    {
                        ["value"] = new Dictionary<string, object?>
                        {
                            ["id"] = id,
                            ["name"] = name,
                        },
                    },
                    // 页面口径：**高在前、宽在后**，逗号后带空格
                    ["container_size"] = $"{{{videoHeight}, {videoWidth}}}",
                    ["unit_center"] = "{0.5, 0.5}",
                    ["anchor_center"] = null,
                    ["markY"] = 0.5,
                },
            });
        }

        return list;
    }

    /// <summary>
    /// 组装 PK 封面业务关联 —— <c>video_info.pk_cover_biz_relations</c>（2026-11-15）。
    ///
    /// 页面 bundle 的构造函数（2026-11-15 线上版 <c>project-publish-vue.d001adc9.js</c> 逐字，
    /// 仓内旧版 <c>1428a0fb</c> 形状相同）：
    /// <code>
    ///   if (!pkCoverEnabled || pkCoverList.length === 0) return [];
    ///   主封面：{bizId: cover.fileid, bizType:"PK_COVER",
    ///            extraInfo: JSON.stringify({audit_status:0, final_cover:false,
    ///                width, height, type:"system", url:"", index:0})}
    ///   附加图：{bizId: e.fileId, bizType:"PK_COVER",
    ///            extraInfo: JSON.stringify({audit_status:0, final_cover:false,
    ///                width, height, type:"system"|"user", url:"", index:i+1})}
    /// </code>
    /// **没有附加图就返回 <c>[]</c>**（主封面单独一条不构成 PK 封面，与页面同口径）。
    /// <para>
    /// ⚠️ 状态（2026-11-15 收口）：**已接线、落位待验**。真账号受控发布
    /// （note <c>6af92d000000000023013ebb</c>，仅自己）用 snake_case 发布体**被平台接受**，
    /// 但 <c>note/detail</c> 回读 <c>extra_info.have_pk_cover=false</c>；候选原因（账号
    /// <c>features.isPkCover</c> 权限 / 主封面来源类型 / 审核后才回写）均未定论，
    /// **不得据此写「平台不支持」**。证据见 docs/xiaohongshu/wire-2026-11-15.md §2 / §4。
    /// </para>
    /// </summary>
    public static string BuildPkCoverRelationsJson(
        string? mainCoverFileId,
        int mainWidth,
        int mainHeight,
        IReadOnlyList<XhsPkCoverEntry>? extras,
        string mainType = "user")
    {
        if (extras is not { Count: > 0 }) return "[]";

        var list = new List<object>();
        if (!string.IsNullOrWhiteSpace(mainCoverFileId))
            list.Add(PkCoverEntry(mainCoverFileId, mainWidth, mainHeight, mainType, 0));

        for (var i = 0; i < extras.Count; i++)
        {
            var e = extras[i];
            if (string.IsNullOrWhiteSpace(e.FileId)) continue;
            list.Add(PkCoverEntry(e.FileId, e.Width, e.Height,
                e.IsSystem ? "system" : "user", i + 1));
        }

        return JsonSerializer.Serialize(list);
    }

    private static Dictionary<string, object?> PkCoverEntry(
        string fileId, int width, int height, string type, int index) => new()
    {
        // ⚠️ 发布体是**递归 camelCase → snake_case** 转换后的结果（线上 bundle 的
        // constructData 写的是 bizId/bizType/extraInfo，抓包落到 wire 是
        // biz_id/biz_type/extra_info）。这里直接产出 snake_case，避免 2026-11-15
        // 第一轮用 camelCase 被平台 `result:-1` 拒收。
        ["biz_id"] = fileId,
        ["biz_type"] = "PK_COVER",
        ["extra_info"] = JsonSerializer.Serialize(new
        {
            audit_status = 0,
            final_cover = false,
            width,
            height,
            type,
            url = "",
            index,
        }),
    };

    /// <summary>
    /// Publish 体的 <c>common.biz_relations</c>：现有业务关系（滤掉 <c>AI_VIDEO_COVER</c>）
    /// 后**追加 PK 封面条目** —— 线上 bundle 2026-11-15 逐字：
    /// <code>
    /// bizRelations: (bizRelation||[])
    ///   .filter(e =&gt; "AI_VIDEO_COVER" !== e.bizType)
    ///   .concat(s.pkCoverBizRelations)
    /// </code>
    /// </summary>
    private static List<object> MergeBizRelations(
        object bizRelations, object pkCoverRelations, object? routeRelation = null)
    {
        var list = new List<object>();
        if (bizRelations is JsonElement biz && biz.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in biz.EnumerateArray())
            {
                // 结构化字段会覆盖同名类型的原始 JSON（页面 insertBizRelation 先删后插同款）。
                // POI_MAP_TRAVEL 只在本次真带路线时覆盖；没带路线时保留用户原始 JSON。
                if (e.ValueKind == JsonValueKind.Object
                    && (TryString(e, "bizType") ?? TryString(e, "biz_type")) is { } bizType
                    && (bizType is "AI_VIDEO_COVER" or "PK_COVER" or "RED_CIRCLE"
                        || (routeRelation is not null && bizType == "POI_MAP_TRAVEL")))
                    continue;
                list.Add(e.Clone());
            }
        }

        if (pkCoverRelations is JsonElement pk && pk.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in pk.EnumerateArray()) list.Add(e.Clone());
        }

        if (routeRelation is Dictionary<string, object?> route) list.Add(route);

        return list;
    }

    /// <summary>
    /// 添加路线 → <c>common.biz_relations</c> 的 <c>POI_MAP_TRAVEL</c> 条目（2026-11-15）。
    /// 页面 <c>travelSaved</c> 逐字：外层 wire 是 snake_case，<c>extra_info</c> 字符串里层
    /// 保持 camelCase（字符串不参与 HTTP 层转换）。
    /// </summary>
    public static Dictionary<string, object?>? BuildRouteBizRelation(XhsRouteBind? route)
    {
        if (route is not { TravelId.Length: > 0 }) return null;

        return new Dictionary<string, object?>
        {
            ["biz_type"] = "POI_MAP_TRAVEL",
            ["biz_id"] = route.TravelId,
            ["extra_info"] = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["travelId"] = route.TravelId,
                ["travelType"] = route.TravelType,
                ["title"] = $"{route.RouteName} 包含{route.PlanAmount}条路线",
                ["icon"] = "",
                ["link"] = "xhsdiscover://rn/poi/map?pageMode=travel_publish",
                ["mapTravels"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["travelId"] = route.TravelId,
                        ["travelType"] = route.TravelType,
                    },
                },
                ["capaPoiRich"] = 0,
            }),
        };
    }

    private static string? TryString(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    /// <summary>
    /// 原始 JSON 透传字段 → 可序列化节点。空/未填按 <paramref name="expected"/> 返回空数组或空对象。
    /// **格式坏就报错**（不静默丢成空值）—— 用户填了内容却没发出去，比直接失败更难排查。
    /// </summary>
    public static object ParseJsonValue(string? json, string fieldName, JsonValueKind expected)
    {
        if (string.IsNullOrWhiteSpace(json))
            return expected == JsonValueKind.Array
                ? Array.Empty<object>()
                : new Dictionary<string, object?>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != expected)
                throw new XhsPublishException(expected == JsonValueKind.Array
                    ? $"小红书{fieldName}必须是 JSON 数组"
                    : $"小红书{fieldName}必须是 JSON 对象");
            return doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new XhsPublishException($"小红书{fieldName} JSON 格式不正确：{ex.Message}");
        }
    }

    // ═══ Privacy Builder ═══

    private static Dictionary<string, object?> BuildPrivacyInfo(XhsPublishRequest req)
    {
        var info = new Dictionary<string, object?>
        {
            ["type"] = (int)req.PrivacyType,
            ["user_ids"] = req.PrivacyUserIds ?? new List<string>(),
        };

        info["op_type"] = req.PrivacyType switch
        {
            XhsPrivacyType.ExcludeUsers => 2, // 不给谁看
            XhsPrivacyType.OnlyUsers => 3,     // 只给谁看
            _ => 1,                             // 公开/私密/互关好友
        };

        return info;
    }

    // ═══ Helpers ═══

    /// <summary>Extract noteId (success) or error message (failure) from XHS response.</summary>
    private static (string? NoteId, string? ErrorMsg) ExtractNoteIdOrError(JsonElement resp)
    {
        // Try data.id first (from add.json: {data: {id: "..."}})
        if (resp.TryGetProperty("data", out var data))
        {
            if (TryGetString(data, "id") is { } id) return (id, null);
            if (TryGetString(data, "noteId") is { } nid) return (nid, null);
            if (TryGetString(data, "note_id") is { } nid2) return (nid2, null);
        }

        // Top-level noteId fields
        if (TryGetString(resp, "noteId") is { } s1) return (s1, null);
        if (TryGetString(resp, "note_id") is { } s2) return (s2, null);
        if (TryGetString(resp, "id") is { } s3) return (s3, null);

        // Failure: extract error message
        var errorMsg = TryGetString(resp, "msg") ?? TryGetString(resp, "message");
        return (null, errorMsg);
    }

    private static string? TryGetString(JsonElement element, string prop)
    {
        if (element.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.String)
            return val.GetString();
        return null;
    }
}
