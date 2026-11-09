using CreatFlow.Platforms.Kuaishou;
using CreatFlow.Platforms.Kuaishou.Requests;
using CreatFlow.Platforms.Toutiao;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 视频管理页底座自检（`--selftest` 会一并跑）—— 不启动 UI、不连服务端。
///
/// 为什么单独一份：这一页的风险全在**分支**上 —— 分页边界（0 条 / 整除 / 末页）、
/// 时间跨月跨年、标题三级回退、以及删除六种结果的「摘不摘行 / 报不报错」。
/// 这些在界面上看只会看到「某一行显示得怪怪的」，不值得每次拉服务端起两个端去复现。
///
/// 结果写 %TEMP%/newapp_video_selftest.txt。
/// </summary>
public static class VideoArchiveSelfTest
{
    public static int Run()
    {
        var sb = new StringBuilder();
        var failures = 0;

        void Check(string name, bool ok, string detail = "")
        {
            if (!ok) failures++;
            sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
        }

        sb.AppendLine("═══ 视频管理页自检（P4）═══");

        // ── 1. 分页边界 ──
        sb.AppendLine("\n[1] 分页边界（0 条 / 整除 / 末页 / 越界）");
        Check("0 条仍是 1 页", VideoArchiveFormat.TotalPages(0, 20) == 1,
            $"实际 {VideoArchiveFormat.TotalPages(0, 20)}");
        Check("20 条 = 1 页", VideoArchiveFormat.TotalPages(20, 20) == 1);
        Check("21 条 = 2 页", VideoArchiveFormat.TotalPages(21, 20) == 2);
        Check("231 条 / 每页 20 = 12 页", VideoArchiveFormat.TotalPages(231, 20) == 12);
        Check("每页 50：231 条 = 5 页", VideoArchiveFormat.TotalPages(231, 50) == 5);
        Check("pageSize=0 不炸（落 1 页）", VideoArchiveFormat.TotalPages(231, 0) == 1);

        Check("第 1 页没有上一页", !VideoArchiveFormat.HasPrev(1));
        Check("第 2 页有上一页", VideoArchiveFormat.HasPrev(2));
        Check("末页没有下一页（231/20 第 12 页）",
            !VideoArchiveFormat.HasNext(12, 231, 20));
        Check("第 11 页有下一页", VideoArchiveFormat.HasNext(11, 231, 20));
        Check("0 条时没有下一页", !VideoArchiveFormat.HasNext(1, 0, 20));
        Check("页文案：第 1 / 1 页（不是 第 1 / 0 页）",
            VideoArchiveFormat.PageText(1, 0, 20) == "第 1 / 1 页",
            VideoArchiveFormat.PageText(1, 0, 20));
        Check("页文案：第 3 / 12 页", VideoArchiveFormat.PageText(3, 231, 20) == "第 3 / 12 页");
        Check("空态汇总不说「共 0 条」",
            VideoArchiveFormat.SummaryText(0) == "还没有已发布记录",
            VideoArchiveFormat.SummaryText(0));
        Check("汇总含「已从上传队列移除」的口径",
            VideoArchiveFormat.SummaryText(231).Contains("已从上传队列移除"),
            VideoArchiveFormat.SummaryText(231));
        Check("筛选时汇总带平台名（否则「共 27 条」与「全部 108」看着对不上）",
            VideoArchiveFormat.SummaryText(13, "视频号").Contains("视频号")
            && VideoArchiveFormat.SummaryText(13, "视频号").StartsWith("共 13 条"),
            VideoArchiveFormat.SummaryText(13, "视频号"));
        Check("不筛选时汇总不带「· 平台」",
            !VideoArchiveFormat.SummaryText(231).Contains(" · "),
            VideoArchiveFormat.SummaryText(231));
        Check("平台 chip：抖音 96", VideoArchiveFormat.ChipText("抖音", 96) == "抖音 96");

        // ── 2. 天头（今天/昨天/前天 + 跨月跨年）──
        sb.AppendLine("\n[2] 天头分组（同一个时钟基准，跨月跨年也要对）");
        var now = new DateTime(2026, 9, 21, 14, 30, 0);
        Check("同一天 → 今天", VideoArchiveFormat.DayLabel(now, now) == "今天");
        Check("当天凌晨也算今天",
            VideoArchiveFormat.DayLabel(new DateTime(2026, 9, 21, 0, 3, 0), now) == "今天");
        Check("昨天", VideoArchiveFormat.DayLabel(new DateTime(2026, 9, 20, 23, 59, 0), now) == "昨天");
        Check("前天", VideoArchiveFormat.DayLabel(new DateTime(2026, 9, 19, 12, 0, 0), now) == "前天");
        Check("再往前给日期",
            VideoArchiveFormat.DayLabel(new DateTime(2026, 9, 18, 12, 0, 0), now) == "2026-09-18",
            VideoArchiveFormat.DayLabel(new DateTime(2026, 9, 18, 12, 0, 0), now));
        Check("跨月：3-01 看 2-28 = 昨天",
            VideoArchiveFormat.DayLabel(new DateTime(2026, 2, 28, 9, 0, 0),
                new DateTime(2026, 3, 1, 9, 0, 0)) == "昨天");
        Check("跨年：1-01 看 12-31 = 昨天",
            VideoArchiveFormat.DayLabel(new DateTime(2025, 12, 31, 9, 0, 0),
                new DateTime(2026, 1, 1, 9, 0, 0)) == "昨天");
        Check("天头整行：今天 · 12 条",
            VideoArchiveFormat.DayHeaderText("今天", 12) == "今天 · 12 条");
        Check("行内只要时分", VideoArchiveFormat.TimeText(new DateTime(2026, 9, 21, 9, 5, 0)) == "09:05",
            VideoArchiveFormat.TimeText(new DateTime(2026, 9, 21, 9, 5, 0)));

        // ── 3. 标题回退链 ──
        sb.AppendLine("\n[3] 标题回退链：平台标题 → 通用标题 → 文件名");
        Check("平台标题优先",
            VideoArchiveFormat.DisplayTitle("改过的标题", "通用标题", "a.mp4") == "改过的标题");
        Check("平台标题空 → 通用标题",
            VideoArchiveFormat.DisplayTitle("", "通用标题", "a.mp4") == "通用标题");
        Check("平台标题空白（空格）也算空",
            VideoArchiveFormat.DisplayTitle("   ", "通用标题", "a.mp4") == "通用标题");
        Check("两个都空 → 文件名去扩展名",
            VideoArchiveFormat.DisplayTitle(null, null, "夏日海边vlog.mp4") == "夏日海边vlog");
        Check("**只去最后一级扩展名**（a.b.mp4 → a.b）",
            VideoArchiveFormat.DisplayTitle(null, null, "a.b.mp4") == "a.b",
            VideoArchiveFormat.DisplayTitle(null, null, "a.b.mp4"));
        Check("文件名带路径也只取名字",
            VideoArchiveFormat.DisplayTitle(null, null, @"D:\video\1\夏日.mp4") == "夏日");
        Check("全空 → 未命名视频",
            VideoArchiveFormat.DisplayTitle(null, null, null) == "未命名视频",
            VideoArchiveFormat.DisplayTitle(null, null, null));

        // ── 4. 大小 / 副行 / 账号 ──
        sb.AppendLine("\n[4] 大小、副行、账号名");
        Check("0 字节 = 0 B", VideoArchiveFormat.FormatSize(0) == "0 B", VideoArchiveFormat.FormatSize(0));
        Check("负数（父任务字段缺失）也不显示负号",
            VideoArchiveFormat.FormatSize(-1) == "0 B", VideoArchiveFormat.FormatSize(-1));
        Check("1023 B 不进位", VideoArchiveFormat.FormatSize(1023) == "1023 B",
            VideoArchiveFormat.FormatSize(1023));
        Check("1024 B = 1 KB", VideoArchiveFormat.FormatSize(1024) == "1 KB",
            VideoArchiveFormat.FormatSize(1024));
        Check("1 MB", VideoArchiveFormat.FormatSize(1024 * 1024) == "1 MB");
        Check("48.2 MB", VideoArchiveFormat.FormatSize((long)(48.2 * 1024 * 1024)) == "48.2 MB",
            VideoArchiveFormat.FormatSize((long)(48.2 * 1024 * 1024)));
        Check("1 GB", VideoArchiveFormat.FormatSize(1024L * 1024 * 1024) == "1 GB",
            VideoArchiveFormat.FormatSize(1024L * 1024 * 1024));
        Check("1.5 GB", VideoArchiveFormat.FormatSize(1024L * 1024 * 1024 * 3 / 2) == "1.5 GB");

        Check("账号名解析不出 → 账号已解绑",
            VideoArchiveFormat.AccountText(null) == "账号已解绑"
            && VideoArchiveFormat.AccountText("  ") == "账号已解绑");
        Check("账号名原样（备注优先由上游保证）",
            VideoArchiveFormat.AccountText(" 主号 ") == "主号");

        Check("副行四段：平台 · 账号 · 大小 · ID",
            VideoArchiveFormat.SubLine("抖音", "主号", (long)(48.2 * 1024 * 1024), "7412abcd") ==
            "抖音 · 主号 · 48.2 MB · ID 7412abcd",
            VideoArchiveFormat.SubLine("抖音", "主号", (long)(48.2 * 1024 * 1024), "7412abcd"));
        Check("没有作品 ID 就不写 ID 段",
            VideoArchiveFormat.SubLine("快手", "美食号", 1024, null) == "快手 · 美食号 · 1 KB",
            VideoArchiveFormat.SubLine("快手", "美食号", 1024, null));
        Check("长 ID 截断显示（19 位抖音 id 不撑爆行）",
            VideoArchiveFormat.ShortId("7412345678901234567").Length < 19
            && VideoArchiveFormat.ShortId("7412345678901234567").Contains("…"),
            VideoArchiveFormat.ShortId("7412345678901234567"));
        Check("短 ID 不截断", VideoArchiveFormat.ShortId("7412abcd") == "7412abcd");

        // ── 5. 删除口径（六种结果 → 摘不摘行 / 报不报错 / 说什么）──
        sb.AppendLine("\n[5] 删除：能不能删平台侧");
        var sid = Guid.NewGuid();
        Check("会话在 + 有 ID → 平台也能删",
            VideoArchiveFormat.DeleteMode(PlatformNames.Douyin, sid, "7412abcd")
            == DeleteScope.PlatformAndLocal);
        Check("会话已解绑（sessionId=null）→ 只能删本地",
            VideoArchiveFormat.DeleteMode(PlatformNames.Douyin, null, "7412abcd")
            == DeleteScope.LocalOnly);
        Check("空 Guid 也算解绑",
            VideoArchiveFormat.DeleteMode(PlatformNames.Douyin, Guid.Empty, "7412abcd")
            == DeleteScope.LocalOnly);
        Check("没有平台作品 ID → 只能删本地",
            VideoArchiveFormat.DeleteMode(PlatformNames.Douyin, sid, null)
            == DeleteScope.LocalOnly);
        Check("空白 ID 也算没有",
            VideoArchiveFormat.DeleteMode(PlatformNames.Douyin, sid, "   ")
            == DeleteScope.LocalOnly);
        // 记录上留着已失效的 sessionId（实测库里 8 月那 3 条就是这样）：不能承诺删平台侧
        Check("sessionId 非空但会话已不在服务端 → 只能删本地",
            VideoArchiveFormat.DeleteMode(PlatformNames.Douyin, sid, "7412abcd", sessionKnown: false)
            == DeleteScope.LocalOnly,
            VideoArchiveFormat.DeleteMode(PlatformNames.Douyin, sid, "7412abcd",
                sessionKnown: false).ToString());
        Check("会话在 + 有 ID → 仍然能删平台侧（默认参数不变）",
            VideoArchiveFormat.DeleteMode(PlatformNames.Douyin, sid, "7412abcd", sessionKnown: true)
            == DeleteScope.PlatformAndLocal);

        // 平台能力（2026-11-07）：B站删除要 Geetest → 只删本地；头条已接真删除
        Check("B站 → 只删本地（平台不接删除链路）",
            VideoArchiveFormat.DeleteMode(PlatformNames.Bilibili, sid, "BV1NSx96fEtS")
            == DeleteScope.LocalOnlyPlatformUnsupported);
        Check("头条 → 会话在 + 有 ID 就能真删",
            VideoArchiveFormat.DeleteMode(PlatformNames.Toutiao, sid, "7704491911486980627")
            == DeleteScope.PlatformAndLocal);

        var platformText = VideoArchiveFormat.DeleteStripText(DeleteScope.PlatformAndLocal);
        var localText = VideoArchiveFormat.DeleteStripText(DeleteScope.LocalOnly);
        var biliText = VideoArchiveFormat.DeleteStripText(DeleteScope.LocalOnlyPlatformUnsupported);
        Check("三种确认条文案**必须不同**",
            platformText != localText && biliText != platformText && biliText != localText);
        Check("平台侧文案写清「本地视频文件不受影响」",
            platformText.Contains("本地视频文件"));
        Check("本地-only 文案写清「平台上的视频不会被移除」",
            localText.Contains("平台上的视频不会被移除"));
        Check("平台侧文案提到「不可恢复」", platformText.Contains("不可恢复"));
        Check("B站文案写清「只清本地记录 / 去 B 站创作中心删」",
            biliText.Contains("只清理本地记录") && biliText.Contains("B 站创作中心"));

        // 头条删除（2026-11-07 接入）：横版 JSON POST / 竖版 GET，两套响应壳
        var ttBody = ToutiaoVideoService.BuildArticleDeleteBody("7704491911486980627");
        Check("头条横版删除体：ItemId / SourceType=2 / DelSlaveId / Reason",
            ttBody["ItemId"] as string == "7704491911486980627"
            && ttBody["SourceType"] as int? == 2
            && ttBody["DelSlaveId"] as string == ""
            && ttBody["Reason"] as string == "");
        Check("头条横版删除：status / data.Code / BaseResp 全 0 才算成功",
            ToutiaoVideoService.ParseArticleDelete(
                """{"data":{"BaseResp":{"StatusMessage":"SUCCESS","StatusCode":0},"Code":0,"Msg":"SUCCESS"},"status":0,"message":"SUCCESS"}""")
            && !ToutiaoVideoService.ParseArticleDelete(
                """{"data":{"BaseResp":{"StatusCode":0},"Code":21001,"Msg":"bad"},"status":0}""")
            // 竖版误投横版端点：status=0 但没 data.Code 的壳不能当成功（否则误删本地记录）
            && !ToutiaoVideoService.ParseArticleDelete("""{"status":0,"message":"SUCCESS"}""")
            && !ToutiaoVideoService.ParseArticleDelete("{bad"));
        Check("头条竖版删除：err_code=0 且 err_no=0",
            ToutiaoVideoService.ParseSmallDelete(
                """{"err_code":0,"err_no":0,"message":"Success"}""")
            && !ToutiaoVideoService.ParseSmallDelete(
                """{"err_code":1,"err_no":0,"message":"fail"}"""));

        sb.AppendLine("\n[6] 删除：六种结果的去留与文案");
        Check("已删除 → 摘行·不报错",
            VideoArchiveFormat.RowRemoved(DeleteOutcome.Deleted)
            && !VideoArchiveFormat.IsError(DeleteOutcome.Deleted));
        Check("平台上已不存在 → 摘行（清的正是脏记录）",
            VideoArchiveFormat.RowRemoved(DeleteOutcome.AlreadyGone)
            && !VideoArchiveFormat.IsError(DeleteOutcome.AlreadyGone));
        Check("账号已解绑 → 摘行",
            VideoArchiveFormat.RowRemoved(DeleteOutcome.UnboundCleaned)
            && !VideoArchiveFormat.IsError(DeleteOutcome.UnboundCleaned));
        Check("平台不支持删除（B站）→ 只清本地也摘行 + 文案说「视频仍在平台上」",
            VideoArchiveFormat.RowRemoved(DeleteOutcome.LocalOnlyCleaned)
            && !VideoArchiveFormat.IsError(DeleteOutcome.LocalOnlyCleaned)
            && VideoArchiveFormat.DeleteOutcomeText(DeleteOutcome.LocalOnlyCleaned)
                .Contains("仍在平台上"));
        Check("登录过期 → **保留行** + 报错（记录还在平台上，不能摘）",
            !VideoArchiveFormat.RowRemoved(DeleteOutcome.NeedLogin)
            && VideoArchiveFormat.IsError(DeleteOutcome.NeedLogin),
            VideoArchiveFormat.DeleteOutcomeText(DeleteOutcome.NeedLogin));
        Check("平台删除失败 → 保留行 + 报错 + 提「可能已不存在」",
            !VideoArchiveFormat.RowRemoved(DeleteOutcome.PlatformFailed)
            && VideoArchiveFormat.IsError(DeleteOutcome.PlatformFailed)
            && VideoArchiveFormat.DeleteOutcomeText(DeleteOutcome.PlatformFailed).Contains("可能已不存在"));
        Check("半成功（平台删了记录没删）→ 保留行 + 可重试",
            !VideoArchiveFormat.RowRemoved(DeleteOutcome.RecordCleanupFailed)
            && VideoArchiveFormat.DeleteOutcomeText(DeleteOutcome.RecordCleanupFailed).Contains("可重试"),
            VideoArchiveFormat.DeleteOutcomeText(DeleteOutcome.RecordCleanupFailed));
        Check("七种结果文案互不相同",
            new[]
            {
                DeleteOutcome.Deleted, DeleteOutcome.AlreadyGone, DeleteOutcome.UnboundCleaned,
                DeleteOutcome.LocalOnlyCleaned, DeleteOutcome.NeedLogin,
                DeleteOutcome.PlatformFailed, DeleteOutcome.RecordCleanupFailed,
            }.Select(VideoArchiveFormat.DeleteOutcomeText).Distinct().Count() == 7);

        // ── 7. 快手删除分流 + 封面 Key 兜底 ──
        // 旧规则「纯数字 = 待发布（unpublished/delete）」是错的：归档里存的纯数字多是
        // publishId（提交后回查列表时 workId 还是 null），已发布作品不在待发布任务表里，
        // 平台回 10001「请检查该发布任务是否存在」—— 2026-11-04 用户实测「总是失败」。
        // 当前分流（2026-11-09 校正）：数字 = publishId（unpublished/delete 先、旧
        // publish/delete 兜底），非数字 = workId（/pc/delete）；两条都不认的 mediaId
        // 用封面 Key / 标题回查。
        sb.AppendLine("\n[7] 快手删除分流（publishId / workId / mediaId）");
        Check("10 位 publishId → 认作 publishId",
            KuaishouDeleteRoutes.IsPublishId("3683939510"));
        Check("workId（3x…）→ 不是 publishId",
            !KuaishouDeleteRoutes.IsPublishId("3xfu3f4smpejnfy"));
        Check("空 ID / null → 不是 publishId（不抛）",
            !KuaishouDeleteRoutes.IsPublishId(null) && !KuaishouDeleteRoutes.IsPublishId("  "));
        Check("26 位 mediaId 超出 long → 不认作 publishId（走 workId 端点失败后再靠封面 Key）",
            !KuaishouDeleteRoutes.IsPublishId("12587728518398072257766114"));

        // 列表匹配（2026-11-09 内容管理页抓包修正）：**已发布作品的 unPublishCoverKey 恒为 null**，
        // 只按封面 Key 永远匹配不到 —— 标题才是稳定锚点；同名多条取最新上传。
        var ksRows7 = new List<KuaishouVideoItem>
        {
            new() { WorkId = "3xold", Title = "同一段文案 #家居", UploadTime = 100, UnPublishCoverKey = null },
            new() { WorkId = "3xnew", Title = "同一段文案 #家居", UploadTime = 200, UnPublishCoverKey = null },
            new() { WorkId = "3xcover", Title = "另一条", UploadTime = 150, UnPublishCoverKey = "key-abc" },
        };
        Check("列表匹配：封面 Key 优先（待发布作品才有 unPublishCoverKey）",
            KuaishouVideoService.MatchListItem(ksRows7, "key-abc", "不存在的标题")?.WorkId == "3xcover");
        Check("列表匹配：已发布作品按标题命中（Key 恒 null）",
            KuaishouVideoService.MatchListItem(ksRows7, "key-missing", "同一段文案 #家居")?.WorkId
                == "3xnew");
        Check("列表匹配：标题首尾空白裁掉后再比",
            KuaishouVideoService.MatchListItem(ksRows7, null, "  同一段文案 #家居  ")?.WorkId
                == "3xnew");
        Check("列表匹配：标题对不上 / 为空 → null",
            KuaishouVideoService.MatchListItem(ksRows7, null, "完全不同的标题") is null
            && KuaishouVideoService.MatchListItem(ksRows7, null, null) is null);

        // 2026-11-09 真实列表：title 是整段 caption（标题\n正文 #话题），归档里只有标题
        var ksRowsCaption = new List<KuaishouVideoItem>
        {
            new()
            {
                WorkId = "3xbody", UploadTime = 300,
                Title = "橘猫为什么大多是公的\n橘色基因在X染色体上…… #猫咪冷知识 #橘猫 #养猫",
            },
        };
        Check("列表匹配：caption 带正文/话题 → 按首行归一化命中",
            KuaishouVideoService.MatchListItem(ksRowsCaption, null, "橘猫为什么大多是公的")?.WorkId
                == "3xbody");
        Check("标题归一化：取第一行 + 去 #话题",
            KuaishouVideoService.NormalizeTitleForMatch(
                "橘猫为什么大多是公的\n正文 #话题") == "橘猫为什么大多是公的");
        Check("列表 keyword：去掉 #话题（平台检索不认标签）",
            KuaishouVideoService.TitleKeyword("正文一句 #家居 #好物") == "正文一句");
        Check("列表 keyword：无话题 / 开头就是 # / null",
            KuaishouVideoService.TitleKeyword("正文一句") == "正文一句"
            && KuaishouVideoService.TitleKeyword("#家居") == "#家居"
            && KuaishouVideoService.TitleKeyword(null) == "");
        Check("凭证提示：109 / 500002 都指向「刷新快手账号」",
            KuaishouVideoService.CredentialHint(109)?.Contains("刷新快手账号") == true
            && KuaishouVideoService.CredentialHint(500002)?.Contains("刷新快手账号") == true
            && KuaishouVideoService.CredentialHint(1) is null);

        // 活 Cookie 合并（2026-11-09）：列表回查要新鲜的 api_st ——
        // 档案页活 Cookie 覆盖同名项、快照独有项保留、非法名/空值丢弃。
        Check("活 Cookie：同名覆盖（api_st 用档案页的新值）",
            KuaishouCookieMerge.Merge(
                "a=1; kuaishou.web.cp.api_st=old",
                "kuaishou.web.cp.api_st=new; b=2")
            == "a=1; kuaishou.web.cp.api_st=new; b=2");
        Check("活 Cookie：空值不覆盖快照（值读空 = 没读到，别把好 Cookie 冲掉）",
            KuaishouCookieMerge.Merge("a=1", "a=; b=2") == "a=1; b=2");
        Check("活 Cookie：非法名字丢弃",
            KuaishouCookieMerge.Merge("a=1", "bad name=x; ok=2") == "a=1; ok=2");
        Check("活 Cookie：活串为空 → 原样快照；快照为空 → 用活串",
            KuaishouCookieMerge.Merge("a=1", null) == "a=1"
            && KuaishouCookieMerge.Merge(null, "b=2") == "b=2"
            && KuaishouCookieMerge.Merge(null, null) == "");
        Check("活 Cookie：只认快手的域（别的域不注进请求）",
            KuaishouCookieMerge.IsKuaishouDomain(".kuaishou.com")
            && KuaishouCookieMerge.IsKuaishouDomain("cp.kuaishou.com")
            && !KuaishouCookieMerge.IsKuaishouDomain("douyin.com")
            && !KuaishouCookieMerge.IsKuaishouDomain(null));

        Check("封面 Key：正常取值",
            VideoArchiveFormat.KuaishouCoverKey(
                """{"ks_cover_key":"cp_video_upload_cover_abc.jpg","ks_work_id":null}""")
            == "cp_video_upload_cover_abc.jpg");
        Check("封面 Key：前后空格裁掉",
            VideoArchiveFormat.KuaishouCoverKey("""{"ks_cover_key":"  key.jpg  "}""") == "key.jpg");
        Check("封面 Key：键不存在 → null",
            VideoArchiveFormat.KuaishouCoverKey("""{"ks_work_id":"3xabc"}""") is null);
        Check("封面 Key：空串 / 空白 → null",
            VideoArchiveFormat.KuaishouCoverKey("""{"ks_cover_key":"   "}""") is null);
        Check("封面 Key：值不是字符串 → null（别的载荷不能当 Key 用）",
            VideoArchiveFormat.KuaishouCoverKey("""{"ks_cover_key":123}""") is null);
        Check("封面 Key：坏 JSON / null / 非对象都返回 null（不抛）",
            VideoArchiveFormat.KuaishouCoverKey("{not json") is null
            && VideoArchiveFormat.KuaishouCoverKey(null) is null
            && VideoArchiveFormat.KuaishouCoverKey("""["ks_cover_key"]""") is null);

        // ── 7b. 「全部」chip 的计数：各平台 facet 之和，不是平台筛选后的 Total ──
        sb.AppendLine("\n[7b] 「全部」筛选芯片计数");
        var facets = new List<PlatformCountItem>
        {
            new("douyin", 13), new("kuaishou", 14),
            new("xiaohongshu", 13), new("wechatvideo", 13),
        };
        Check("四平台 facet 之和 = 53（选中视频号时 Total=13 也不能拿它当全部）",
            VideoArchiveFormat.AllChipCount(facets, fallback: 13) == 53,
            $"{VideoArchiveFormat.AllChipCount(facets, fallback: 13)}");
        Check("没有 facet 数据时退回 Total（服务端老版本/异常）",
            VideoArchiveFormat.AllChipCount([], fallback: 27) == 27);
        Check("facet 里的负数/0 不会把「全部」算成负",
            VideoArchiveFormat.AllChipCount(
                [new("a", -5), new("b", 0), new("c", 3)], fallback: 99) == 3);

        // ── 8. 批量删除（分组键 / 汇总文案 / 失败回插判定）──
        sb.AppendLine("\n[8] 批量删除");
        var s1 = Guid.NewGuid();
        var s2 = Guid.NewGuid();

        Check("同 (平台,会话) → 同组",
            VideoArchiveFormat.GroupKey("douyin", s1) == VideoArchiveFormat.GroupKey("douyin", s1));
        Check("同账号跨平台 → 不同组（抖音 cookie ≠ 快手 cookie，不用互相排队）",
            VideoArchiveFormat.GroupKey("douyin", s1) != VideoArchiveFormat.GroupKey("kuaishou", s1));
        Check("同平台不同账号 → 不同组",
            VideoArchiveFormat.GroupKey("douyin", s1) != VideoArchiveFormat.GroupKey("douyin", s2));
        Check("会话为空 → 归到「-」一档",
            VideoArchiveFormat.GroupKey("douyin", null) == VideoArchiveFormat.GroupKey("douyin", null)
            && VideoArchiveFormat.GroupKey("douyin", null).EndsWith(":-"),
            VideoArchiveFormat.GroupKey("douyin", null));
        Check("空 Guid 与 null 同组（脏记录不能各自开一组）",
            VideoArchiveFormat.GroupKey("douyin", Guid.Empty) == VideoArchiveFormat.GroupKey("douyin", null));
        Check("平台 key 大小写不敏感（服务端/配置来源不一致）",
            VideoArchiveFormat.GroupKey("Douyin", s1) == VideoArchiveFormat.GroupKey("douyin", s1));
        Check("平台 key 前后空格不影响归组",
            VideoArchiveFormat.GroupKey(" douyin ", s1) == VideoArchiveFormat.GroupKey("douyin", s1));
        Check("平台 key 为空也不炸（落「?」）",
            VideoArchiveFormat.GroupKey("", s1).StartsWith("?:"),
            VideoArchiveFormat.GroupKey("", s1));

        Check("「全部副本」确认条要说清「含本页之外的」",
            VideoArchiveFormat.WholeVideoStripText(12).Contains("含本页之外的")
            && VideoArchiveFormat.WholeVideoStripText(12).Contains("12"),
            VideoArchiveFormat.WholeVideoStripText(12));
        Check("副本为 0 时不说「将删除」（没什么可删的）",
            VideoArchiveFormat.WholeVideoStripText(0).Contains("已经没有副本"),
            VideoArchiveFormat.WholeVideoStripText(0));
        Check("按钮文字带条数（点之前就知道删几条）",
            VideoArchiveFormat.WholeVideoButtonText(12) == "全部副本 12",
            VideoArchiveFormat.WholeVideoButtonText(12));
        Check("1 条时不写「全部副本 1」（措辞怪）",
            VideoArchiveFormat.WholeVideoButtonText(1) == "全部副本",
            VideoArchiveFormat.WholeVideoButtonText(1));

        var path = Path.Combine(Path.GetTempPath(), "newapp_video_selftest.txt");
        sb.AppendLine(failures == 0 ? "\n═══ 全部通过 ═══" : $"\n═══ {failures} 项失败 ═══");
        try { File.WriteAllText(path, sb.ToString()); } catch { /* 写不了盘不影响退出码 */ }

        Console.WriteLine($"[VideoArchiveSelfTest] failures={failures} -> {path}");
        return failures;
    }
}
