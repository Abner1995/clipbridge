namespace CreatFlow.NewApp.Services;

/// <summary>
/// 合成页底座自检（<c>--selftest</c> 会一并跑）—— 不启动 UI、不碰用户的 compose_settings.json：
/// 发布单元写入器（params.txt 的格式契约 + 不重建/不覆盖规矩 + 参数样板）、偏好存储往返、
/// 文件夹扫描（含 bgm 前缀 / 资料.txt 注释行，2026-10-11 追加）、合成队列存储、
/// 配音队列（存储恢复 / 许可门槛 / 文件命名，2026-10-10 追加）、
/// Kokoro 字幕切分（逐句 / ≤16 字行）、合成动效/转场池与合成单元模板（2026-10-11 追加）、
/// 角色库（模板套用 / 存档 / 快照映射，2026-10-11 批次 4 追加）、
/// 合成完成后自动发布（勾选存档 / 快照往返 / 无头提交组装，2026-10-11 批次 5 追加）、
/// 视频水印（规格 / 位置表达式 / 会员级存档 / 快照映射，2026-10-21 追加）。
/// 结果写 %TEMP%/newapp_compose_selftest.txt。
/// </summary>
public static class ComposeSelfTest
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

        sb.AppendLine("═══ 合成页底座自检 ═══");

        // ── 1. params.txt 文本：能被既有解析器零警告读回 ──
        sb.AppendLine("\n[1] 发布单元 params.txt（ComposeUnitWriter）");
        {
            var parsed = PublishParamsFile.Parse(ComposeUnitWriter.BuildParamsText("大华岚翠 89 平三居"));
            Check("零警告解析（模板自己不能教用户写错）", parsed.Problems.Count == 0,
                string.Join(" / ", parsed.Problems.Select(p => p.Reason)));
            Check("标题读得回", parsed.Title == "大华岚翠 89 平三居", parsed.Title ?? "(空)");

            var sched = PublishParamsFile.Parse(
                ComposeUnitWriter.BuildParamsText("标题A", new DateTime(2026, 10, 20, 20, 0, 0)));
            Check("发布时间是解析器认的格式",
                sched.IsScheduled == true && sched.ScheduledAt == new DateTime(2026, 10, 20, 20, 0, 0),
                sched.ScheduledAt?.ToString("yyyy-MM-dd HH:mm") ?? "(无)");

            var weird = PublishParamsFile.Parse(ComposeUnitWriter.BuildParamsText("  多行\n标题  #带井号  "));
            Check("标题折成一行且 # 不被当注释吃掉",
                weird.Title == "多行 标题 #带井号" && weird.Problems.Count == 0, weird.Title ?? "(空)");

            // 参数样板：全部可写字段都要列出来（2026-10-11 用户反馈「不清楚有哪些参数」）
            var guide = ComposeUnitWriter.BuildParamsText("样板检查");
            var guideKeys = new[] { "发布时间", "简介", "话题", "话题ID", "官方活动", "位置", "合集",
                "可见性", "允许下载", "内容声明", "原创" };
            Check("样板列全所有可写字段",
                guideKeys.All(k => guide.Contains($"# {k}: ", StringComparison.Ordinal)),
                string.Join("/", guideKeys.Where(k => !guide.Contains($"# {k}: ", StringComparison.Ordinal))));
            var guideParsed = PublishParamsFile.Parse(guide);
            Check("可选示例一律注释着（不偷偷改变导入语义）",
                guideParsed.Description is null && guideParsed.Location is null && guideParsed.Collection is null
                && guideParsed.Visibility is null && guideParsed.AllowDownload is null && guideParsed.Declaration is null
                && guideParsed.IsOriginal is null && guideParsed.IsScheduled is null && guideParsed.Topics.Count == 0
                && guideParsed.TopicRefs.Count == 0 && guideParsed.Activities.Count == 0
                && guideParsed.Problems.Count == 0,
                $"problems={guideParsed.Problems.Count}");
        }

        // ── 2. 文件夹名清洗 ──
        sb.AppendLine("\n[2] 文件夹名清洗（ToSafeName）");
        {
            var dirty = "a" + string.Concat(Path.GetInvalidFileNameChars()) + "b";
            var cleaned = ComposeUnitWriter.ToSafeName(dirty, "回退");
            Check("所有平台非法字符都被替换",
                Path.GetInvalidFileNameChars().All(c => !cleaned.Contains(c)), cleaned);

            Check("空白标题回退", ComposeUnitWriter.ToSafeName("   ", "回退") == "回退");
            Check("结尾的点被去掉（Windows 文件夹名不允许以点结尾）",
                ComposeUnitWriter.ToSafeName("房源 1.2.", "回退") == "房源 1.2",
                ComposeUnitWriter.ToSafeName("房源 1.2.", "回退"));
            Check("超长截断到 60 字", ComposeUnitWriter.ToSafeName(new string('房', 100), "回退").Length == 60);
        }

        // ── 3. 二次写入不洗掉用户补的行（规矩 1：params 只改目标行，绝不重建）──
        sb.AppendLine("\n[3] params.txt 就地更新（不重建约定）");
        {
            var text = ComposeUnitWriter.BuildParamsText("旧标题", new DateTime(2026, 10, 20, 20, 0, 0));
            text = ComposeUnitWriter.UpsertParam(text, "简介", "用户自己补的简介");
            text = ComposeUnitWriter.UpsertParam(text, "话题", "苏州房产");
            text = ComposeUnitWriter.UpsertParam(text, "标题", "新标题");
            text = ComposeUnitWriter.UpsertParam(text, "发布时间", "2026-10-21 20:00");

            var p = PublishParamsFile.Parse(text);
            Check("标题/发布时间更新、简介/话题原样保留、零警告",
                p.Title == "新标题" && p.Description == "用户自己补的简介" && p.Topics.Contains("苏州房产")
                && p.ScheduledAt == new DateTime(2026, 10, 21, 20, 0, 0) && p.Problems.Count == 0,
                $"标题={p.Title} 简介={p.Description} 话题={string.Join("|", p.Topics)}");

            var inserted = ComposeUnitWriter.UpsertParam("# 头注释\n\n标题: x\n", "发布时间", "2026-10-20 20:00");
            Check("新键插在生效行之后（不掉到注释块下面）",
                inserted.IndexOf("发布时间", StringComparison.Ordinal) > inserted.IndexOf("标题: x", StringComparison.Ordinal),
                inserted.Replace("\n", " ⏎ "));
        }

        // ── 4. 落盘往返（%TEMP% 造景；抽帧失败必须不拦、已有封面必须不动）──
        sb.AppendLine("\n[4] 发布单元落盘（WriteAsync）");
        var root = Path.Combine(Path.GetTempPath(), "creatflow-compose-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(root);
            var unit = Path.Combine(root, "测试单元");
            var fakeVideo = Path.Combine(root, "fake.mp4");
            File.WriteAllText(fakeVideo, "not a real video");   // 抽帧注定失败 —— 正是要验的「不拦」

            var written = ComposeUnitWriter.WriteAsync(unit, fakeVideo, "测试单元").GetAwaiter().GetResult();
            Check("params.txt 写下", File.Exists(Path.Combine(unit, PublishParamsFile.FileName)));
            Check("假视频抽帧失败不往外抛、不加进返回列表", written.All(p => !p.EndsWith("cover.jpg", StringComparison.OrdinalIgnoreCase)));
            Check("读回标题正确", PublishParamsFile.TryRead(unit)?.Title == "测试单元");

            // 用户改过封面 → 二次写入不覆盖（规矩 2）
            var cover = Path.Combine(unit, "cover.jpg");
            File.WriteAllText(cover, "user cover");
            var written2 = ComposeUnitWriter.WriteAsync(unit, fakeVideo, "测试单元2").GetAwaiter().GetResult();
            Check("已有 cover.jpg 不动", File.ReadAllText(cover) == "user cover" && written2.All(p => !p.EndsWith("cover.jpg", StringComparison.OrdinalIgnoreCase)));
            Check("二次写入只更新标题", PublishParamsFile.TryRead(unit)?.Title == "测试单元2");

            // 旧格式 params（旧版生成 / 用户自己补过行）：WriteAsync 只补样板、不动任何已生效内容（规矩 3）
            var legacyUnit = Path.Combine(root, "旧格式单元");
            Directory.CreateDirectory(legacyUnit);
            File.WriteAllText(Path.Combine(legacyUnit, PublishParamsFile.FileName),
                "# 发布单元参数\n\n标题: 旧标题\n简介: 用户自己补的简介\n");
            ComposeUnitWriter.WriteAsync(legacyUnit, fakeVideo, "旧标题改").GetAwaiter().GetResult();
            var legacyParsed = PublishParamsFile.TryRead(legacyUnit);
            Check("旧文件补上参数样板、用户内容与生效行不动",
                File.ReadAllText(Path.Combine(legacyUnit, PublishParamsFile.FileName))
                    .Contains("# ── 可选参数", StringComparison.Ordinal)
                && legacyParsed is { Problems.Count: 0 } lp && lp.Title == "旧标题改"
                && lp.Description == "用户自己补的简介",
                legacyParsed is { Problems.Count: 0 } p2 ? $"标题={p2.Title}" : "解析有警告");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }

        // ── 5. 偏好存储往返 + 老版 tts_settings.json 一次性兜底 ──
        sb.AppendLine("\n[5] 偏好存储（ComposeSettingsStore，临时根目录）");
        var setRoot = Path.Combine(Path.GetTempPath(), "creatflow-composeset-" + Guid.NewGuid().ToString("N")[..8]);
        var legacyRoot = Path.Combine(Path.GetTempPath(), "creatflow-legacy-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var store = new ComposeSettingsStore(setRoot);
            store.SaveAsync(new ComposeSettings
            {
                ProviderName = "Kokoro 本地",
                VoiceId = "zf_xiaobei",
                Speed = 1.25,
                Pitch = 0.95,
                BgmVolume = 0.3,
                OutputDirectory = @"D:\视频\合成",
                ExportDirectory = @"D:\配音导出",
                IsWide = true,
                RoleId = "builtin-fangtaohui",
            }).GetAwaiter().GetResult();
            var loaded = store.LoadAsync().GetAwaiter().GetResult();
            Check("偏好往返完整（引擎/音色/语速/音调/两个目录/画幅/角色）",
                loaded.ProviderName == "Kokoro 本地" && loaded.VoiceId == "zf_xiaobei"
                && Math.Abs(loaded.Speed - 1.25) < 1e-9 && Math.Abs(loaded.Pitch - 0.95) < 1e-9
                && loaded.IsWide && loaded.OutputDirectory == @"D:\视频\合成"
                && loaded.ExportDirectory == @"D:\配音导出"
                && loaded.RoleId == "builtin-fangtaohui");

            Directory.CreateDirectory(legacyRoot);
            // 值照抄老版真实存量：Edge 引擎名 + Edge 音色 —— 存储层不校验引擎，
            // 读回来由 UI 侧「在当前引擎里找不找得到」决定回落（Edge 已下架 → 回落 Kokoro）
            File.WriteAllText(Path.Combine(legacyRoot, "tts_settings.json"),
                """{"voiceShortName":"zh-CN-XiaoyiNeural","speed":1.1,"pitch":0.9,"providerName":"Edge 免费","saveDirectory":"D:\\老导出"}""");
            var legacyStore = new ComposeSettingsStore(legacyRoot);
            var legacy = legacyStore.LoadAsync().GetAwaiter().GetResult();
            Check("新版文件不存在时沿用老版配音偏好（音色/语速/音调/引擎/导出目录）",
                legacy.VoiceId == "zh-CN-XiaoyiNeural" && Math.Abs(legacy.Speed - 1.1) < 1e-9
                && Math.Abs(legacy.Pitch - 0.9) < 1e-9 && legacy.ProviderName == "Edge 免费"
                && legacy.ExportDirectory == @"D:\老导出",
                $"{legacy.ProviderName} / {legacy.VoiceId} / {legacy.Speed}");

            legacyStore.SaveAsync(new ComposeSettings { VoiceId = "zf_xiaoxiao" }).GetAwaiter().GetResult();
            Check("写下新版文件后不再回落老文件",
                legacyStore.LoadAsync().GetAwaiter().GetResult().VoiceId == "zf_xiaoxiao");
        }
        finally
        {
            try { Directory.Delete(setRoot, recursive: true); } catch { }
            try { Directory.Delete(legacyRoot, recursive: true); } catch { }
        }

        // ── 6. 文件夹批量扫描（子文件夹 = 一条；自然序；资料.txt（# 注释）/ 音频优先 / bgm 约定；跳过原因）──
        sb.AppendLine("\n[6] 文件夹扫描（ComposeFolderScanner）");
        var scanRoot = Path.Combine(Path.GetTempPath(), "creatflow-scan-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var one = Path.Combine(scanRoot, "一号房");
            var two = Path.Combine(scanRoot, "二号房");
            var noVoice = Path.Combine(scanRoot, "没配音");
            var noMedia = Path.Combine(scanRoot, "没素材");
            var withBgm = Path.Combine(scanRoot, "带音乐");
            var musicOnly = Path.Combine(scanRoot, "只有音乐");
            var commentsOnly = Path.Combine(scanRoot, "只剩注释");
            var textAndMusic = Path.Combine(scanRoot, "文字加音乐");
            foreach (var d in new[] { one, two, noVoice, noMedia, withBgm, musicOnly, commentsOnly, textAndMusic })
                Directory.CreateDirectory(d);
            foreach (var f in new[] { "1.jpg", "2.jpg", "10.jpg" })
                File.WriteAllText(Path.Combine(one, f), "x");
            File.WriteAllText(Path.Combine(one, "资料.txt"), "# 注释不读\n 第一行。\n# 又一行注释\n第二行。 \n");
            File.WriteAllText(Path.Combine(two, "b.mp3"), "x");
            File.WriteAllText(Path.Combine(two, "a.mp3"), "x");
            File.WriteAllText(Path.Combine(two, "1.png"), "x");
            File.WriteAllText(Path.Combine(two, "资料.txt"), "二号房文案：有音乐也有文案。");
            File.WriteAllText(Path.Combine(noVoice, "1.jpg"), "x");
            File.WriteAllText(Path.Combine(noMedia, "说明.txt"), "x");
            File.WriteAllText(Path.Combine(withBgm, "1.jpg"), "x");
            File.WriteAllText(Path.Combine(withBgm, "配音.mp3"), "x");
            File.WriteAllText(Path.Combine(withBgm, "bgm-夏日.mp3"), "x");
            File.WriteAllText(Path.Combine(musicOnly, "1.jpg"), "x");
            File.WriteAllText(Path.Combine(musicOnly, "bgm.mp3"), "x");
            File.WriteAllText(Path.Combine(musicOnly, "资料.txt"), "背景乐不算配音，文案要生效。");
            File.WriteAllText(Path.Combine(commentsOnly, "1.jpg"), "x");
            File.WriteAllText(Path.Combine(commentsOnly, "资料.txt"), "# 全是注释\n# 没有一句能读\n");
            // 用户实测场景（2026-10-11）：文案 + 任意命名的歌曲文件（旧规则把歌当配音、文案被顶掉、没字幕）
            File.WriteAllText(Path.Combine(textAndMusic, "1.jpg"), "x");
            File.WriteAllText(Path.Combine(textAndMusic, "夏日.flac"), "x");
            File.WriteAllText(Path.Combine(textAndMusic, "资料.txt"), "文案靠我，音乐当背景。");

            var scan = ComposeFolderScanner.Scan([scanRoot]);
            var u1 = scan.Units.FirstOrDefault(u => u.Title == "一号房");
            var u2 = scan.Units.FirstOrDefault(u => u.Title == "二号房");
            var u3 = scan.Units.FirstOrDefault(u => u.Title == "带音乐");
            var u4 = scan.Units.FirstOrDefault(u => u.Title == "只有音乐");
            var u5 = scan.Units.FirstOrDefault(u => u.Title == "文字加音乐");
            Check("带素材的子文件夹 = 一条（没素材的不产出也不误报）",
                scan.Units.Count == 5 && scan.Skipped.Count == 2,
                $"units={scan.Units.Count} skipped={scan.Skipped.Count}");
            Check("素材按自然序（1,2,10）",
                u1 is not null && u1.MaterialPaths.Select(Path.GetFileName)
                    .SequenceEqual(["1.jpg", "2.jpg", "10.jpg"]),
                u1 is null ? "(缺)" : string.Join(",", u1.MaterialPaths.Select(Path.GetFileName)));
            Check("资料.txt 去掉 # 注释行（首尾空白修剪）",
                u1?.Text == "第一行。\n第二行。", (u1?.Text ?? "(空)").Replace("\n", "⏎"));
            Check("有 资料.txt 就按文案配音：音频收编为背景乐并标记",
                u2 is not null && u2.Text == "二号房文案：有音乐也有文案。" && u2.AudioPath is null
                && Path.GetFileName(u2.BgmPath ?? "") == "a.mp3" && u2.AudioAsBgm,
                $"text={u2?.Text ?? "(无)"} audio={u2?.AudioPath ?? "(无)"} bgm={Path.GetFileName(u2?.BgmPath ?? "(无)")}");
            Check("没 资料.txt 时才拿音频当配音：第一个非 bgm 音频当配音、bgm 前缀单独带走",
                u3 is not null && Path.GetFileName(u3.AudioPath ?? "") == "配音.mp3"
                && Path.GetFileName(u3.BgmPath ?? "") == "bgm-夏日.mp3" && !u3.AudioAsBgm,
                $"audio={Path.GetFileName(u3?.AudioPath ?? "(无)")} bgm={Path.GetFileName(u3?.BgmPath ?? "(无)")}");
            Check("bgm 前缀优先中标（有文案时也不出收编提示）",
                u4 is not null && u4.AudioPath is null && Path.GetFileName(u4.BgmPath ?? "") == "bgm.mp3"
                && u4.Text == "背景乐不算配音，文案要生效。" && !u4.AudioAsBgm,
                $"audio={u4?.AudioPath ?? "(无)"} text={u4?.Text ?? "(无)"}");
            Check("文案 + 任意名音乐（用户实测：歌曲 夏日.flac）→ 文案配音 + 音乐当背景乐",
                u5 is not null && u5.Text == "文案靠我，音乐当背景。" && u5.AudioPath is null
                && Path.GetFileName(u5.BgmPath ?? "") == "夏日.flac" && u5.AudioAsBgm,
                $"text={u5?.Text ?? "(无)"} bgm={Path.GetFileName(u5?.BgmPath ?? "(无)")}");
            Check("缺配音给出跳过原因（没音频的、只剩注释的都给）",
                scan.Skipped.Any(s => s.Folder.EndsWith("没配音") && s.Reason.Contains("缺配音"))
                && scan.Skipped.Any(s => s.Folder.EndsWith("只剩注释") && s.Reason.Contains("缺配音")),
                string.Join("；", scan.Skipped.Select(s => $"{Path.GetFileName(s.Folder)}：{s.Reason}")));

            var selfScan = ComposeFolderScanner.Scan([one]);
            Check("选中的文件夹本身带素材时 = 一条（没有子文件夹的场合）",
                selfScan.Units.Count == 1 && selfScan.Units[0].Title == "一号房",
                $"units={selfScan.Units.Count}");
        }
        finally
        {
            try { Directory.Delete(scanRoot, recursive: true); } catch { }
        }

        // ── 7. 队列持久化 + 重启恢复规则 ──
        sb.AppendLine("\n[7] 合成队列存储（ComposeQueueStore）");
        var qRoot = Path.Combine(Path.GetTempPath(), "creatflow-queue-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var store = new ComposeQueueStore(qRoot);
            store.SaveAsync(
            [
                new ComposeTaskData { Id = "a", Title = "一号房", MaterialPaths = [@"D:\x\1.jpg"], Text = "文案" },
                new ComposeTaskData { Id = "b", Title = "二号房", MaterialPaths = [@"D:\x\2.jpg"], Status = ComposeTaskStatus.Running },
                new ComposeTaskData { Id = "c", Title = "三号房", MaterialPaths = [@"D:\x\3.jpg"], Status = ComposeTaskStatus.Done, OutputPath = @"D:\out\三号房\三号房.mp4" },
            ]).GetAwaiter().GetResult();

            var loaded = ComposeQueueStore.ApplyRestoreRules(store.LoadAsync().GetAwaiter().GetResult());
            Check("往返读回（标题/素材/文案）",
                loaded.Count == 3 && loaded[0].Title == "一号房" && loaded[0].Text == "文案"
                && loaded[0].MaterialPaths.Single() == @"D:\x\1.jpg", $"count={loaded.Count}");
            Check("重启恢复：排队中原样续跑", loaded[0].Status == ComposeTaskStatus.Pending);
            Check("重启恢复：合成中标已中断（可重试）",
                loaded[1].Status == ComposeTaskStatus.Interrupted && loaded[1].StatusMessage.Contains("中断"));
            Check("已完成原样（含产物路径）",
                loaded[2].Status == ComposeTaskStatus.Done && loaded[2].OutputPath == @"D:\out\三号房\三号房.mp4");
        }
        finally
        {
            try { Directory.Delete(qRoot, recursive: true); } catch { }
        }

        // ── 8. 配音队列（存储往返 + 恢复规则 + 许可门槛 + 文件命名）──
        sb.AppendLine("\n[8] 配音队列（TtsQueueStore / FeatureGate / TtsRunner）");
        var tqRoot = Path.Combine(Path.GetTempPath(), "creatflow-ttsqueue-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var store = new TtsQueueStore(tqRoot);
            store.SaveAsync(new TtsQueueState
            {
                IsPaused = true,
                Tasks =
                [
                    new TtsTaskData { Id = "a", Title = "第一条", Text = "文案一", SuggestedName = "配音20261010_01" },
                    new TtsTaskData { Id = "b", Title = "第二条", Text = "文案二", Status = TtsTaskStatus.Running },
                    new TtsTaskData { Id = "c", Title = "第三条", Text = "文案三", Status = TtsTaskStatus.Done, OutputPath = @"D:\out\配音-1.mp3" },
                ],
            }).GetAwaiter().GetResult();

            var state = TtsQueueStore.ApplyRestoreRules(store.LoadAsync().GetAwaiter().GetResult());
            Check("往返读回（任务 + 暂停态）",
                state.Tasks.Count == 3 && state.Tasks[0].Text == "文案一"
                && state.Tasks[0].SuggestedName == "配音20261010_01" && state.IsPaused,
                $"count={state.Tasks.Count} paused={state.IsPaused}");
            Check("重启恢复：排队中原样续跑", state.Tasks[0].Status == TtsTaskStatus.Pending);
            Check("重启恢复：合成中标已中断（可重试）",
                state.Tasks[1].Status == TtsTaskStatus.Interrupted && state.Tasks[1].StatusMessage.Contains("中断"));
            Check("已完成原样（含产物路径）",
                state.Tasks[2].Status == TtsTaskStatus.Done && state.Tasks[2].OutputPath == @"D:\out\配音-1.mp3");

            // 许可门槛（用户规则：凡是功能页面，都必须登录且激活才能使用 —— 配音 / 合成共用这道闸）
            Check("未登录拦下", FeatureGate.OnlineBlockReason(false, "", "配音功能") is not null);
            Check("已过期拦下（提示去激活）", FeatureGate.OnlineBlockReason(true, "user", "配音功能")?.Contains("激活") == true);
            Check("登录且未过期放行", FeatureGate.OnlineBlockReason(false, "user", "配音功能") is null);
            Check("冒烟验证台让路", FeatureGate.BlockReason(true, "", true, "配音功能") is null);
            Check("功能名进提示语（合成功能）",
                FeatureGate.OnlineBlockReason(true, "user", "合成功能")?.Contains("合成功能") == true,
                FeatureGate.OnlineBlockReason(true, "user", "合成功能"));

            // 文件名：摘要 + 清洗 + 重名兜底
            Check("摘要取首行前 n 字（折成一行）",
                TtsRunner.Snippet("  第一行挺长的一句话啊\n第二行", 6) == "第一行挺长的…",
                TtsRunner.Snippet("  第一行挺长的一句话啊\n第二行", 6));
            var safe = TtsRunner.SuggestName("标题:含*非法?字符", "20261010120000", 3);
            Check("建议文件名含时间戳/序号且不含非法字符",
                safe.StartsWith("配音20261010120000_03") && Path.GetInvalidFileNameChars().All(c => !safe.Contains(c)),
                safe);
            // 文件名摘要：超过 12 字直接截断、不留「…」（2026-10-11 用户反馈：文件名带省略号像内容缺失，误导）
            var longName = TtsRunner.SuggestName("楼盘名称：金融街武夷·融御 在售 楼盘特点：现房", "20261011095820", 1);
            Check("文件名摘要截断不留省略号",
                longName == "配音20261011095820_01_楼盘名称：金融街武夷·融",
                longName);
            var nameDir = Path.Combine(tqRoot, "naming");
            Directory.CreateDirectory(nameDir);
            var p1 = Path.Combine(nameDir, "配音.mp3");
            File.WriteAllText(p1, "x");
            Check("重名加 -2 兜底",
                Path.GetFileName(TtsRunner.EnsureUniquePath(p1)) == "配音-2.mp3",
                Path.GetFileName(TtsRunner.EnsureUniquePath(p1)));
        }
        finally
        {
            try { Directory.Delete(tqRoot, recursive: true); } catch { }
        }

        // ── 9. Kokoro 字幕切分（2026-10-11 修复：整段模式曾整片一条字幕、被 ASS 16 字×3 行截断）──
        sb.AppendLine("\n[9] Kokoro 字幕切分（SplitSentences / SubtitleLineBuilder）");
        {
            var segs = KokoroTtsProvider.SplitSentences("第一句。第二句！第三句？");
            Check("按句末标点切句（标点随前句）", segs.SequenceEqual(["第一句。", "第二句！", "第三句？"]),
                string.Join("|", segs));
            Check("换行也是断点",
                KokoroTtsProvider.SplitSentences("第一行\n第二行").SequenceEqual(["第一行", "第二行"]));
            var punctOnly = KokoroTtsProvider.SplitSentences("好。……");
            Check("纯标点/省略号碎片不合成也不出字幕", punctOnly.SequenceEqual(["好。"]), string.Join("|", punctOnly));

            var lines = SubtitleLineBuilder.Build(
                [new SubtitleItem("第一句。", 0, 1000),
                 new SubtitleItem("这是一句超过十六个字的长句子用来验行切分", 1000, 5000)], []);
            Check("句级字幕切 ≤16 字/行、时间随句推进",
                lines.Count >= 3 && lines.All(l => l.Text.Length <= 16)
                && lines[0] == new SubtitleItem("第一句。", 0, 1000) && lines[^1].EndMs == 5000,
                string.Join("|", lines.Select(l => l.Text)));
        }

        // ── 10. 合成动效与转场（2026-10-11 用户要求：特效一定要多，固定几个会审美疲劳）──
        sb.AppendLine("\n[10] 合成动效与转场（FfmpegVideoComposer）");
        {
            Check("动效池够多（≥24 种组合）", FfmpegVideoComposer.MotionEffectCount >= 24,
                $"count={FfmpegVideoComposer.MotionEffectCount}");
            Check("转场池够多（≥20 种）", FfmpegVideoComposer.TransitionCount >= 20,
                $"count={FfmpegVideoComposer.TransitionCount}");

            var pool = FfmpegVideoComposer.MotionEffectCount;
            var motions = FfmpegVideoComposer.PickMotionIndexes(pool + 5, seed: 20261011);
            Check("一段视频内动效不重样（整轮两两不同 + 跨轮边界防撞）",
                motions.Take(pool).Distinct().Count() == pool
                && motions[pool] != motions[pool - 1]
                && motions[^1] != motions[^2],
                string.Join(",", motions.Take(6)));
            Check("动效分配对 seed 确定（同 seed 同顺序、换 seed 换顺序）",
                FfmpegVideoComposer.PickMotionIndexes(8, seed: 7)
                    .SequenceEqual(FfmpegVideoComposer.PickMotionIndexes(8, seed: 7))
                && !FfmpegVideoComposer.PickMotionIndexes(8, seed: 7)
                    .SequenceEqual(FfmpegVideoComposer.PickMotionIndexes(8, seed: 8)));

            var junctions = FfmpegVideoComposer.PickTransitions(20, seed: 20261011);
            Check("接缝数 ≤ 池子大小时转场零重复",
                junctions.Count == 20 && junctions.Distinct().Count() == 20,
                string.Join(",", junctions.Take(6)));

            var frames = new[] { 75, 75, 75 };
            var overlap = FfmpegVideoComposer.SpreadTransitionOverlap(
                frames, [MediaKind.Image, MediaKind.Image, MediaKind.Image]);
            Check("转场重叠帧摊进图片素材（总时长不缩水）",
                overlap == 10 && frames.Sum() == 75 * 3 + 20, $"overlap={overlap} sum={frames.Sum()}");

            var mixedFrames = new[] { 75, 200, 75 };
            var mixedOverlap = FfmpegVideoComposer.SpreadTransitionOverlap(
                mixedFrames, [MediaKind.Image, MediaKind.Video, MediaKind.Image]);
            Check("混剪只摊给图片（视频段原样）",
                mixedOverlap == 10 && mixedFrames[1] == 200 && mixedFrames.Sum() == 75 * 2 + 200 + 20,
                string.Join(",", mixedFrames));

            var allVideo = new[] { 75, 75 };
            var noOverlap = FfmpegVideoComposer.SpreadTransitionOverlap(allVideo, [MediaKind.Video, MediaKind.Video]);
            Check("纯视频素材不启用转场（没有可拉长的段落，保持硬切）",
                noOverlap == 0 && allVideo.Sum() == 150);

            var shortClip = new[] { 75, 15, 75 };
            var shortOverlap = FfmpegVideoComposer.SpreadTransitionOverlap(
                shortClip, [MediaKind.Image, MediaKind.Image, MediaKind.Image]);
            Check("太短的段落（<0.8s）不启用转场",
                shortOverlap == 0 && shortClip.Sum() == 165, $"overlap={shortOverlap}");

            var single = new[] { 75 };
            Check("单素材不启用转场",
                FfmpegVideoComposer.SpreadTransitionOverlap(single, [MediaKind.Image]) == 0 && single[0] == 75);
        }

        // ── 11. 合成单元模板（2026-10-11 用户要求：添加按钮先生成文件夹、用户自己替换素材）──
        sb.AppendLine("\n[11] 合成单元模板（ComposeUnitTemplate）");
        var tplRoot = Path.Combine(Path.GetTempPath(), "creatflow-tpl-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var result = ComposeUnitTemplate.WriteAsync(tplRoot).GetAwaiter().GetResult();
            var tplFolder = Path.Combine(tplRoot, ComposeUnitTemplate.FolderName);
            Check("模板落盘（说明.txt / 资料.txt / 3 张示例图）",
                result.Error is null
                && File.Exists(Path.Combine(tplFolder, ComposeUnitTemplate.ReadmeFileName))
                && File.Exists(Path.Combine(tplFolder, ComposeFolderScanner.TextFileName))
                && Enumerable.Range(1, ComposeUnitTemplate.SampleImageCount)
                    .All(i => File.Exists(Path.Combine(tplFolder, $"{i}.jpg"))),
                result.Error ?? string.Join(",", result.Written));

            var tplScan = ComposeFolderScanner.Scan([tplRoot]);
            Check("模板能被批量扫描直接吃成一条（素材自然序 + 示例文案生效）",
                tplScan.Units.Count == 1 && tplScan.Units[0].Title == ComposeUnitTemplate.FolderName
                && tplScan.Units[0].MaterialPaths.Select(Path.GetFileName).SequenceEqual(["1.jpg", "2.jpg", "3.jpg"])
                && tplScan.Units[0].Text == ComposeUnitTemplate.SampleCopy,
                tplScan.Units.Count == 1
                    ? tplScan.Units[0].Text ?? "(无文案)"
                    : $"units={tplScan.Units.Count} skipped={tplScan.Skipped.Count}");

            // 幂等 + 不覆盖用户改动（改了 资料.txt 再点第二次，内容要原样）
            var tplText = Path.Combine(tplFolder, ComposeFolderScanner.TextFileName);
            File.WriteAllText(tplText, "用户改过的文案。");
            var again = ComposeUnitTemplate.WriteAsync(tplRoot).GetAwaiter().GetResult();
            Check("重复生成幂等、不动用户改过的文件",
                again.Error is null && again.Written.Count == 0
                && File.ReadAllText(tplText) == "用户改过的文案。",
                $"written={again.Written.Count} kept={again.Kept.Count}");
        }
        finally
        {
            try { Directory.Delete(tplRoot, recursive: true); } catch { }
        }

        // ── 12. 角色库（2026-10-11 批次 4：模板套用 + 存档 + 快照映射）──
        sb.AppendLine("\n[12] 角色库（ComposeRole / 模板套用 / 存档）");
        {
            var roleRoot = Path.Combine(Path.GetTempPath(), "creatflow-roles-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                var roleStore = new ComposeRoleStore(roleRoot);
                Check("存档是独立的 compose_roles.json（绝不塞 llm.json）",
                    roleStore.GetConfigPath().EndsWith("compose_roles.json", StringComparison.OrdinalIgnoreCase),
                    roleStore.GetConfigPath());
                Check("会员级路径（no-member 兜底）",
                    roleStore.GetConfigPath().Contains(@"\accounts\no-member\", StringComparison.OrdinalIgnoreCase),
                    roleStore.GetConfigPath());

                var defaults = roleStore.LoadAsync().GetAwaiter().GetResult().Roles;
                Check("文件不存在 → 内置 2 条示例，Id 唯一非空",
                    defaults.Count == 2 && defaults.All(r => !r.IsNone)
                    && defaults.Select(r => r.Id).Distinct().Count() == 2);
                Check("房淘惠模板 = 用户原句（一字不改）+ 有人设",
                    defaults.Any(r => r.Id == "builtin-fangtaohui"
                        && r.Template == "您好，我是房淘惠。{内容}。欢迎大家私信获取详细信息。" && r.HasPersona));
                Check("内置模板无拉丁字母（Kokoro 会读音出错/丢失）",
                    defaults.All(r => !r.Template.Any(char.IsAsciiLetter)));

                roleStore.SaveAsync(new ComposeRolesData
                {
                    Roles = [.. defaults, new ComposeRole
                        { Id = "u1", Name = "测试角色", Template = "前缀{内容}后缀", Persona = "人设", Knowledge = "资料" }],
                }).GetAwaiter().GetResult();
                var round = roleStore.LoadAsync().GetAwaiter().GetResult().Roles;
                Check("存储往返完整（5 字段）",
                    round.Count == 3 && round[2] is
                        { Id: "u1", Name: "测试角色", Template: "前缀{内容}后缀", Persona: "人设", Knowledge: "资料" });

                roleStore.SaveAsync(new ComposeRolesData { Roles = [] }).GetAwaiter().GetResult();
                Check("合法空列表 → 空（删光不复活内置）", roleStore.LoadAsync().GetAwaiter().GetResult().Roles.Count == 0);

                File.WriteAllText(roleStore.GetConfigPath(), "{ 半截 json");
                Check("坏文件 → 回落内置默认", roleStore.LoadAsync().GetAwaiter().GetResult().Roles.Count == 2);
            }
            finally
            {
                try { Directory.Delete(roleRoot, recursive: true); } catch { }
            }

            // 模板套用（纯函数；分镜单行套用是防 runner Materials[i] 越界的关键）
            Check("空模板不套（原文返回）",
                ComposeRoleTemplate.Apply(null, "原文") == "原文"
                && ComposeRoleTemplate.Apply("   ", "原文") == "原文");
            Check("占位替换（多处都换）", ComposeRoleTemplate.Apply("{内容}中间{内容}", "X") == "X中间X");
            Check("连接处标点去重：内容带句号 → 吞模板句号",
                ComposeRoleTemplate.Apply("A{内容}。B", "X。") == "AX。B");
            Check("连接处标点去重：内容不带标点 → 保留模板句号",
                ComposeRoleTemplate.Apply("A{内容}。B", "X") == "AX。B");
            Check("连接处标点去重：内容带感叹号 → 吞模板句号",
                ComposeRoleTemplate.Apply("A{内容}。B", "X！") == "AX！B");
            Check("无占位：整段加换行分隔 / 分镜直接拼接",
                ComposeRoleTemplate.Apply("T", "C") == "T\nC"
                && ComposeRoleTemplate.Apply("T", "C", singleLineTemplate: true) == "TC");
            var shotWrapped = ComposeRoleTemplate.Apply("开场。\n{内容}\n结束。", "一\n二\n三", singleLineTemplate: true);
            Check("分镜模式：模板换行先去掉、句数守恒（首行含开场、末行含结束）",
                shotWrapped.Split('\n').Length == 3
                && shotWrapped.StartsWith("开场。") && shotWrapped.EndsWith("结束。"),
                shotWrapped.Replace("\n", "⏎"));
            Check("CRLF 归一", ComposeRoleTemplate.Apply("A{内容}B", "x\r\ny") == "Ax\nyB");
            Check("硬信息逐字保留（模板不动内容、连接处只吞一个句号）",
                ComposeRoleTemplate.Apply("您好。{内容}。欢迎。", "均价五万元。电话12345。")
                    == "您好。均价五万元。电话12345。欢迎。");

            var audioUnit = new ComposeFolderUnit(@"C:\x", "标题", ["a.jpg"], null, "v.mp3", null, false);
            Check("批量：无文案的自备配音单元不套模板（原样返回）",
                ReferenceEquals(ComposeRoleTemplate.ApplyToUnit(audioUnit, ComposeRole.Defaults[0]), audioUnit));
            var textUnit = audioUnit with { Text = "测试文案。", AudioPath = null };
            var wrappedUnit = ComposeRoleTemplate.ApplyToUnit(textUnit, ComposeRole.Defaults[0]);
            Check("批量：有文案的单元套模板（房淘惠示例）",
                wrappedUnit.Text == "您好，我是房淘惠。测试文案。欢迎大家私信获取详细信息。", wrappedUnit.Text);
            Check("批量：哨兵角色不套（原样返回）",
                ReferenceEquals(ComposeRoleTemplate.ApplyToUnit(textUnit, ComposeRole.None), textUnit));

            // 快照映射（RoleName 只为展示搬运；执行快照 ToJob 刻意不带）
            var roleJob = new ComposeJob
            {
                Materials = [new ComposeMediaItem("a.jpg", MediaKind.Image)],
                OutputDirectory = @"C:\out",
                Text = "最终文案。",
                RoleName = "房淘惠",
            };
            var fromJob = ComposeTask.FromJob(roleJob);
            Check("FromJob 搬运 RoleName（队列行展示用）", fromJob is { RoleName: "房淘惠", HasRole: true });
            var backTask = ComposeTask.FromData(fromJob.ToData());
            Check("ToData/FromData 往返保留 RoleName",
                backTask is { RoleName: "房淘惠", RoleText: "角色：房淘惠" });
            Check("FromFolder 可选角色参数",
                ComposeTask.FromFolder(textUnit, new ComposeSettings(), "房淘惠").RoleName == "房淘惠");
        }

        // ── 13. 合成完成后自动发布（2026-10-11：勾选存档 + 快照往返 + 无头组装，零网络）──
        sb.AppendLine("\n[13] 合成完成后自动发布（勾选存档 / 快照 / 提交组装）");
        {
            // ① 勾选存档（AccountSelectionStore + AccountChipsService）
            var selRoot = Path.Combine(Path.GetTempPath(), "creatflow-sel-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                var selStore = new AccountSelectionStore(selRoot);
                Check("存档是独立的 account-selection.json（不进 compose_settings —— 那份三写者互擦）",
                    selStore.GetConfigPath().EndsWith("account-selection.json", StringComparison.OrdinalIgnoreCase),
                    selStore.GetConfigPath());

                var a = Guid.NewGuid();
                var b = Guid.NewGuid();
                var svc = new AccountChipsService(selStore);
                svc.EnsureLoadedAsync().GetAwaiter().GetResult();
                Check("首次无存档：开关关（= 全选）", !svc.AutoPublish);

                svc.RecordToggle(a, false);
                svc.RecordToggle(b, false);
                // SetAutoPublish 写完才返回 → 前面两笔也随链落定（读回才是确定的）
                svc.SetAutoPublishAsync(true).GetAwaiter().GetResult();

                var svc2 = new AccountChipsService(selStore);   // 新实例 = 模拟重启
                svc2.EnsureLoadedAsync().GetAwaiter().GetResult();
                var raw = selStore.LoadAsync().GetAwaiter().GetResult();
                Check("跨重启保留：开关开 + 显式关掉的两个账号都在 off 集（全关是合法选择）",
                    svc2.AutoPublish && raw.OffSessions.Contains(a) && raw.OffSessions.Contains(b),
                    $"auto={svc2.AutoPublish} off={raw.OffSessions.Count}");

                svc2.RecordToggle(a, true);                 // 开回 a
                var c = Guid.NewGuid();
                // b 已从账号列表消失（解绑）→ 清遗骸；链尾再读，顺序确定
                svc2.PruneAsync([a, c]).GetAwaiter().GetResult();
                var pruned = selStore.LoadAsync().GetAwaiter().GetResult();
                Check("Prune 清掉已解绑账号的勾选遗骸，开关不受影响",
                    !pruned.OffSessions.Contains(b) && !pruned.OffSessions.Contains(a) && pruned.AutoPublish,
                    $"off={string.Join(",", pruned.OffSessions)} auto={pruned.AutoPublish}");

                File.WriteAllText(selStore.GetConfigPath(), "{ 半截 json");
                var broken = new AccountSelectionStore(selRoot);
                var brokenData = broken.LoadAsync().GetAwaiter().GetResult();
                Check("坏文件 → 回落默认（全选 + 开关关），不抛",
                    !brokenData.AutoPublish && brokenData.OffSessions.Count == 0);
            }
            finally
            {
                try { Directory.Delete(selRoot, recursive: true); } catch { }
            }

            // ② 快照往返（目标入队即冻结；恢复规则）
            var t1 = new AutoPublishTarget("douyin", Guid.NewGuid(), "主号");
            var t2 = new AutoPublishTarget("xiaohongshu", Guid.NewGuid(), "小号", IsLoggedIn: false);
            var apJob = new ComposeJob
            {
                Materials = [new ComposeMediaItem("a.jpg", MediaKind.Image)],
                OutputDirectory = @"C:\out",
                AutoPublishTargets = [t1, t2],
            };
            var apTask = ComposeTask.FromJob(apJob);
            Check("FromJob 搬运 targets（队列行入队即可见「自动发布 2 个账号」）",
                apTask is { HasAutoPublish: true, AutoPublishSummary: "自动发布 2 个账号" });

            var apBack = ComposeTask.FromData(apTask.ToData());
            Check("ToData/FromData 往返保留 targets（账号名快照）",
                apBack.AutoPublishTargets.Count == 2
                && apBack.AutoPublishTargets[0] == t1 && apBack.AutoPublishTargets[1] == t2);
            Check("ToJob 携带 targets（重试也保住同一批账号）",
                apTask.ToJob().AutoPublishTargets.Count == 2);

            var folderTask = ComposeTask.FromFolder(
                new ComposeFolderUnit(@"C:\x", "标题", ["a.jpg"], "文案。", null, null, false),
                new ComposeSettings(), null, [t1]);
            Check("FromFolder 带 targets（批量确认条那条路）",
                folderTask.AutoPublishTargets.Count == 1
                && folderTask.AutoPublishTargets[0].SessionId == t1.SessionId);
            Check("FromFolder 不带 targets = 空（关着自动发布的常规路）",
                ComposeTask.FromFolder(
                    new ComposeFolderUnit(@"C:\x", "标题", ["a.jpg"], "文案。", null, null, false),
                    new ComposeSettings()).AutoPublishTargets.Count == 0);

            // 恢复规则：提交中被打断 → 状态未知（绝不自动重发）；合成中断规则不回归
            var restored = ComposeQueueStore.ApplyRestoreRules(
            [
                new ComposeTaskData { Status = ComposeTaskStatus.Running },
                new ComposeTaskData { Status = ComposeTaskStatus.Done, AutoPublishState = AutoPublishState.Submitting },
                new ComposeTaskData { Status = ComposeTaskStatus.Done, AutoPublishState = AutoPublishState.Submitted,
                    AutoPublishText = "已进上传队列 · 2 个账号（2 条平台记录）" },
            ]);
            Check("合成中 → 已中断（原规则不回归）",
                restored[0].Status == ComposeTaskStatus.Interrupted);
            Check("提交中被中断 → 状态未知 + 提示去上传队列核对（不自动重发）",
                restored[1].AutoPublishState == AutoPublishState.Uncertain
                && restored[1].AutoPublishText.Contains("上传队列", StringComparison.Ordinal));
            Check("已提交的结果原样保留", restored[2].AutoPublishState == AutoPublishState.Submitted);
            Check("重试守卫：提交中 / 状态未知都要挡（崩溃→重启→重试不许绕过防重发）",
                ComposeQueueViewModel.ShouldSkipAutoPublish(AutoPublishState.Submitting)
                && ComposeQueueViewModel.ShouldSkipAutoPublish(AutoPublishState.Uncertain)
                && !ComposeQueueViewModel.ShouldSkipAutoPublish(AutoPublishState.Failed)
                && !ComposeQueueViewModel.ShouldSkipAutoPublish(AutoPublishState.None));

            // ③ 结果展示派生
            var doneTask = ComposeTask.FromJob(apJob);
            doneTask.AutoPublishState = AutoPublishState.Submitted;
            doneTask.AutoPublishText = "已进上传队列 · 2 个账号（2 条平台记录）";
            Check("已提交 → 绿色小字 + 给「去上传队列」入口",
                doneTask.AutoPublishTextHex == "#0A5C28" && doneTask is { HasAutoPublishText: true, ShowQueueLink: true });
            doneTask.AutoPublishState = AutoPublishState.Failed;
            Check("失败 → 红色小字且不给队列入口（只有文案与手动导入兜底）",
                doneTask.AutoPublishTextHex == "#C62828" && !doneTask.ShowQueueLink);

            // ④ 批量确认条文案（纯函数口径）
            var confirm = ComposeQueueViewModel.BuildBatchConfirmText(12, 3);
            Check("确认条文案含 条数 / 账号数 / 平台记录总数 / 立即发布",
                confirm.Contains("12 条", StringComparison.Ordinal)
                && confirm.Contains("3 个账号", StringComparison.Ordinal)
                && confirm.Contains("36 条平台记录", StringComparison.Ordinal)
                && confirm.Contains("立即发布", StringComparison.Ordinal), confirm);

            // ④b 队列页自动发布配置区 + 批次定时（2026-10-15 批次 6）
            var confirmSched = ComposeQueueViewModel.BuildBatchConfirmText(12, 3, new DateTime(2026, 10, 20, 20, 0, 0));
            Check("确认条定时变体：说「将于 …发布」且不再说「立即发布」",
                confirmSched.Contains("将于 2026-10-20 20:00 发布", StringComparison.Ordinal)
                && !confirmSched.Contains("立即发布", StringComparison.Ordinal), confirmSched);

            var now2 = DateTime.Now;
            Check("定时日期/时间不全 → 永远拦（自动发布开没开都拦）",
                ComposeQueueViewModel.BatchScheduleError(true, null, false, now2)?.Contains("日期和时间", StringComparison.Ordinal) == true
                && ComposeQueueViewModel.BatchScheduleError(true, null, true, now2) is not null);
            Check("窗口外 + 自动发布 → 拦（复用发布页口径）",
                ComposeQueueViewModel.BatchScheduleError(true, now2.AddHours(1), true, now2)?.Contains("2 小时", StringComparison.Ordinal) == true);
            Check("窗口外 + 不自动发布 → 放行（定时只写单元参数，导入时再响亮报错）",
                ComposeQueueViewModel.BatchScheduleError(true, now2.AddHours(1), false, now2) is null);
            Check("窗口内 + 自动发布 → 放行",
                ComposeQueueViewModel.BatchScheduleError(true, now2.AddHours(3), true, now2) is null);

            var schedFolderTask = ComposeTask.FromFolder(
                new ComposeFolderUnit(@"C:\x", "标题", ["a.jpg"], "文案。", null, null, false),
                new ComposeSettings(), null, null, new DateTime(2026, 10, 20, 20, 0, 0));
            Check("FromFolder 带 scheduledAt（批次定时入快照 + 行副行提示）",
                schedFolderTask.ScheduledAt == new DateTime(2026, 10, 20, 20, 0, 0)
                && schedFolderTask is { HasSchedule: true }
                && schedFolderTask.ScheduleText.Contains("定时", StringComparison.Ordinal),
                schedFolderTask.ScheduleText);
            Check("ScheduledAt 落盘往返 + ToJob（重试保住同一时间点）",
                ComposeTask.FromData(schedFolderTask.ToData()).ScheduledAt == new DateTime(2026, 10, 20, 20, 0, 0)
                && schedFolderTask.ToJob().ScheduledAt == new DateTime(2026, 10, 20, 20, 0, 0));

            // ⑤ 定时窗口（发布页预检口径单一来源，合成页提交前同用）
            var now = DateTime.Now;
            Check("窗口外拦下：距现在 1 小时 → 报「2 小时」",
                PublishPreflight.ScheduleWindowError(now.AddHours(1), now)?.Contains("2 小时", StringComparison.Ordinal) == true);
            Check("窗口内放行：距现在 3 小时 → null", PublishPreflight.ScheduleWindowError(now.AddHours(3), now) is null);
            Check("窗口外拦下：距现在 15 天 → 报「14 天」",
                PublishPreflight.ScheduleWindowError(now.AddDays(15), now)?.Contains("14 天", StringComparison.Ordinal) == true);

            // ⑥ 无头组装（真文件、假网络）：单元目录 + params.txt → 草稿 → 提交请求
            var unitRoot = Path.Combine(Path.GetTempPath(), "creatflow-unit-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                Directory.CreateDirectory(unitRoot);
                var videoPath = Path.Combine(unitRoot, "测试单元.mp4");
                File.WriteAllBytes(videoPath, [0x00, 0x01, 0x02, 0x03]);
                // 分钟精度（params 只认到分）：截断秒再 +3 小时，落在 2h~14d 窗口内
                var scheduled = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0)
                    .AddHours(3);
                File.WriteAllText(Path.Combine(unitRoot, "params.txt"), string.Join('\n',
                [
                    "标题: 测试单元",
                    "简介: 夏日记录",
                    "话题: 夏日",
                    "可见性: 仅自己",
                    $"发布时间: {scheduled:yyyy-MM-dd HH:mm}",
                ]));

                var candidate = PublishImportPlanner.UnitFor(unitRoot, videoPath);
                Check("UnitFor：标题/简介/话题/定时从 params.txt 读出（回退链同导入路径）",
                    candidate.Title == "测试单元" && candidate.Description == "夏日记录"
                    && candidate.Params.Topics.Contains("夏日") && candidate.Params.IsScheduled == true);

                var draft = new PublishDraft(candidate.VideoPath, candidate.SourceFolder)
                {
                    Title = candidate.Title,
                    Description = candidate.Description ?? "",
                    CoverPath = candidate.CoverPath,
                };
                var xhsSession = Guid.NewGuid();
                var seedTargets = new List<PublishTarget>
                {
                    new("xiaohongshu", xhsSession, "小号"),
                };
                var defaults = new Dictionary<string, PlatformParams>
                {
                    [PublishDraftStore.KeyOf("xiaohongshu", xhsSession)] = new() { IsOriginal = true, AllowDownload = false },
                };
                PublishDraftFactory.SeedTargets(draft, seedTargets, defaults);
                PublishDraftFactory.ApplyUnitParams(draft, candidate.Params);

                Check("params 覆盖预设：可见性 / 下载 / 定时",
                    draft.Visibility != PublishVisibility.Public
                    && draft.IsScheduled && draft.ScheduledAt == scheduled,
                    $"visibility={draft.Visibility} scheduled={draft.ScheduledAt:MM-dd HH:mm}");
                Check("没写的项跟随预设（原创沿用小号预设 = 开）",
                    draft.ParamsFor(seedTargets[0], defaults.GetValueOrDefault(PublishDraftStore.KeyOf("xiaohongshu", xhsSession)))
                        .IsOriginal == true);

                var request = PublishDraftFactory.BuildSubmitRequestAsync(draft, defaults).GetAwaiter().GetResult();
                Check("提交请求组装：哈希非空 64 hex / 目标数 / 标题 / 定时",
                    request is not null
                    && request.VideoHash.Length == 64 && request.VideoHash.All(Uri.IsHexDigit)
                    && request.Targets.Count == 1
                    && request.CommonTitle == "测试单元"
                    && request.ScheduledAt == scheduled,
                    request is null ? "request=null" : $"hash={request.VideoHash.Length} targets={request.Targets.Count}");

                // 预检口径：登录过期的目标 → Error（自动发布会被拦下并给原因）
                draft.Targets[0] = draft.Targets[0] with { IsLoggedIn = false };
                var errors = PublishPreflight.Validate(draft)
                    .Where(i => i.Severity == PreflightSeverity.Error).ToList();
                Check("账号登录过期 → 预检 Error（自动发布列出原因而不是静默发失败）",
                    errors.Any(e => e.Message.Contains("登录已过期", StringComparison.Ordinal)),
                    string.Join(" / ", errors.Select(e => e.Message)));
            }
            finally
            {
                try { Directory.Delete(unitRoot, recursive: true); } catch { }
            }
        }

        // ── 14. 视频水印（规格 / 位置表达式 / 随机 / 会员级存档 / 快照映射，2026-10-21）──
        sb.AppendLine("\n[14] 视频水印（WatermarkSpec / 存档 / 快照）");
        {
            var def = WatermarkSpec.Default;
            Check("默认开 + 房淘惠 + 右上",
                def is { Enabled: true, Text: "房淘惠", Position: WatermarkPosition.TopRight } && def.IsActive);

            Check("开关关 → 不烧", !new WatermarkSpec(false, "房淘惠", WatermarkPosition.TopLeft).IsActive);
            Check("文字空 → 不烧（即使开关开着）",
                !new WatermarkSpec(true, "   ", WatermarkPosition.TopRight).IsActive);
            Check("文字去空白 + 最多 20 字",
                new WatermarkSpec(true, new string('房', 30), WatermarkPosition.TopRight).EffectiveText.Length == 20);

            var tl = FfmpegVideoComposer.BuildWatermarkFilter(1080, 1920, WatermarkPosition.TopLeft);
            var tr = FfmpegVideoComposer.BuildWatermarkFilter(1080, 1920, WatermarkPosition.TopRight);
            var bl = FfmpegVideoComposer.BuildWatermarkFilter(1080, 1920, WatermarkPosition.BottomLeft);
            var br = FfmpegVideoComposer.BuildWatermarkFilter(1080, 1920, WatermarkPosition.BottomRight);
            Check("竖屏四角表达式 + 字号/边距（min 1080 → 40/48）",
                tl.Contains("fontsize=40", StringComparison.Ordinal)
                && tl.Contains("x=48:y=48", StringComparison.Ordinal)
                && tr.Contains("x=w-text_w-48:y=48", StringComparison.Ordinal)
                && bl.Contains("x=48:y=h-text_h-48", StringComparison.Ordinal)
                && br.Contains("x=w-text_w-48:y=h-text_h-48", StringComparison.Ordinal),
                tr);
            Check("横屏同字号（按 min(w,h) 算，不按宽）",
                FfmpegVideoComposer.BuildWatermarkFilter(1920, 1080, WatermarkPosition.TopRight)
                    .Contains("fontsize=40", StringComparison.Ordinal));
            Check("文字走 textfile（中文/标点不转义）",
                tr.Contains("textfile=watermark.txt", StringComparison.Ordinal));

            var rng = new Random(20261021);
            var corners = Enumerable.Range(0, 200)
                .Select(_ => FfmpegVideoComposer.ResolveWatermarkPosition(WatermarkPosition.Random, rng))
                .Distinct().ToList();
            Check("随机只落在四角（不含 Random 本身）",
                corners.All(c => c is WatermarkPosition.TopLeft or WatermarkPosition.TopRight
                    or WatermarkPosition.BottomLeft or WatermarkPosition.BottomRight)
                && corners.Count >= 3,
                string.Join(",", corners));
            Check("固定位置原样返回",
                FfmpegVideoComposer.ResolveWatermarkPosition(WatermarkPosition.BottomLeft, rng)
                    == WatermarkPosition.BottomLeft);

            // 会员级存档：默认开 / 往返 / 换号隔离 / 坏档回默认
            var wmRoot = Path.Combine(Path.GetTempPath(), "creatflow-wm-" + Guid.NewGuid().ToString("N")[..8]);
            var memberA = Guid.NewGuid();
            var memberB = Guid.NewGuid();
            var current = memberA;
            var store = new WatermarkSettingsStore(wmRoot, () => current);
            try
            {
                Check("没存过 → 默认开（老用户升级后默认开启）",
                    store.LoadAsync().GetAwaiter().GetResult()
                        is { Enabled: true, Text: "房淘惠", Position: WatermarkPosition.TopRight });

                store.SaveAsync(memberA, new WatermarkSettings
                {
                    Enabled = false,
                    Text = " 房淘惠测试 ",
                    Position = WatermarkPosition.BottomRight,
                }).GetAwaiter().GetResult();
                var wmPath = store.GetConfigPath(memberA);
                Check("会员级路径 accounts/{memberId}/watermark-settings.json",
                    wmPath.Contains(
                        Path.Combine("accounts", memberA.ToString("N"), "watermark-settings.json"),
                        StringComparison.Ordinal), wmPath);
                var wmJson = File.ReadAllText(wmPath);
                Check("JSON：位置枚举字符串化 + 不落计算属性",
                    wmJson.Contains("\"position\": \"BottomRight\"", StringComparison.Ordinal)
                    && !wmJson.Contains("effectiveText", StringComparison.Ordinal)
                    && !wmJson.Contains("isActive", StringComparison.Ordinal), wmJson);
                Check("往返：关 / 去空白 / 右下",
                    store.LoadAsync().GetAwaiter().GetResult()
                        is { Enabled: false, Text: "房淘惠测试", Position: WatermarkPosition.BottomRight });

                current = memberB;
                Check("换号 → 不继承 A 的品牌（读默认）",
                    store.LoadAsync().GetAwaiter().GetResult() is { Enabled: true, Text: "房淘惠" });

                Directory.CreateDirectory(Path.GetDirectoryName(store.GetConfigPath(memberB))!);
                File.WriteAllText(store.GetConfigPath(memberB), "{ 坏档");
                Check("坏档 → 回默认（不把页面打挂）",
                    store.LoadAsync().GetAwaiter().GetResult() is { Enabled: true, Text: "房淘惠" });
            }
            finally
            {
                try { Directory.Delete(wmRoot, recursive: true); } catch { }
            }

            // 快照映射：入队冻结 / 落盘往返 / 老存档缺字段默认开
            var wmJob = new ComposeJob
            {
                Materials = [new ComposeMediaItem(@"C:\x\a.jpg", MediaKind.Image)],
                OutputDirectory = @"C:\x",
                Watermark = new WatermarkSpec(false, "冒烟水印", WatermarkPosition.TopLeft),
            };
            var wmTask = ComposeTask.FromJob(wmJob);
            Check("FromJob 搬运水印", wmTask.Watermark == wmJob.Watermark);
            Check("ToJob 重试保住水印", wmTask.ToJob().Watermark == wmJob.Watermark);
            Check("ToData/FromData 保住水印",
                ComposeTask.FromData(wmTask.ToData()).Watermark == wmJob.Watermark);
            Check("老存档缺字段 → 默认开",
                new ComposeTaskData().Watermark is { Enabled: true, Text: "房淘惠" });
            Check("FromFolder 带水印 / 缺省默认开",
                ComposeTask.FromFolder(
                    new ComposeFolderUnit(@"C:\x", "标题", ["a.jpg"], "文案。", null, null, false),
                    new ComposeSettings(), null, null, null,
                    new WatermarkSpec(true, "批量水印", WatermarkPosition.Random)).Watermark.Text == "批量水印"
                && ComposeTask.FromFolder(
                    new ComposeFolderUnit(@"C:\x", "标题", ["a.jpg"], "文案。", null, null, false),
                    new ComposeSettings()).Watermark == WatermarkSpec.Default);

            // JSON 落盘往返（枚举字符串化）+ 手写老档缺字段
            var wmqRoot = Path.Combine(Path.GetTempPath(), "creatflow-wmq-" + Guid.NewGuid().ToString("N")[..8]);
            var wmqMember = Guid.NewGuid();
            var wmqStore = new ComposeQueueStore(wmqRoot, () => wmqMember);
            try
            {
                wmqStore.SaveAsync([wmTask.ToData()], wmqMember).GetAwaiter().GetResult();
                var wmqBack = wmqStore.LoadAsync(wmqMember).GetAwaiter().GetResult();
                Check("队列 JSON 落盘往返（位置枚举字符串化）",
                    wmqBack.Count == 1 && wmqBack[0].Watermark == wmJob.Watermark);

                File.WriteAllText(wmqStore.GetConfigPath(), """[{"title":"老任务"}]""");
                var legacyBack = wmqStore.LoadAsync(wmqMember).GetAwaiter().GetResult();
                Check("手写老档缺 watermark 字段 → 默认开（升级前排队的老任务）",
                    legacyBack.Count == 1 && legacyBack[0].Watermark is { Enabled: true, Text: "房淘惠" });
            }
            finally
            {
                try { Directory.Delete(wmqRoot, recursive: true); } catch { }
            }
        }

        sb.AppendLine();
        sb.AppendLine(failures == 0 ? "═══ 全部通过 ═══" : $"═══ {failures} 项失败 ═══");

        var report = sb.ToString();
        Console.WriteLine(report);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "newapp_compose_selftest.txt"), report);
        return failures;
    }
}
