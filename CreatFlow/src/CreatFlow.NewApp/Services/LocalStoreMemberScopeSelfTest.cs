using CreatFlow.NewApp.Models;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 本机六档存档「会员级化」自检（2026-10-15 批次 7，`--selftest` 会一并跑）——
/// 全部在 TEMP 夹具上做、零网络，不碰任何真实存档。
///
/// 守的是「换会员不串库」这条线（六档 = 勾选存档 / 账号排序 / 发布预设 / 发布草稿 / 两个队列）：
///  · **会员路径**：档都落在 accounts/{memberId:N}/ 下（GetConfigPath 钉住）；
///  · **A/B 隔离**：A 写的档 B 读不到、B 写的档不覆盖 A（跨会员误删/覆盖是本次修的故障）；
///  · **旧机器级档一次性采纳**：会员档不存在 + 旧档非空 → 首读采纳 + 删旧档 + 只采纳一次；
///  · **空旧档只删不采纳**；**Guid.Empty（no-member）不采纳**且旧档保留；
///  · **坏会员档读回默认、绝不被旧档覆盖**（防「坏档被旧档洗掉」）；
///  · **显式 memberId 锁定**：当前是 B 也能把一笔写进 A 的档（队列 VM 换号挂起收尾依赖它）；
///  · **AccountChipsService 换号整份重载** + 在途写落老会员的档。
///
/// 结果写 %TEMP%/newapp_memberscope_selftest.txt。
///
/// ⚠️ 角色库（compose_roles.json）**不并入本矩阵**：其空旧档必须被采纳（显式空语义，
/// 不复活内置）、旧档还是裸数组形状（要升级成 {Roles,Dirty} 包装）—— 与本矩阵的
/// 「空旧档只删不采纳」正相反，见 <see cref="ComposeRoleCloudSelfTest"/> [2]②。
/// </summary>
public static class LocalStoreMemberScopeSelfTest
{
    /// <summary>同步入口（保持与其他自检一致的调用形状）</summary>
    public static int Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task<int> RunAsync()
    {
        var sb = new StringBuilder();
        var failures = 0;

        void Check(string name, bool ok, string detail = "")
        {
            if (!ok) failures++;
            sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
        }

        sb.AppendLine("═══ 本机存档会员级化自检 ═══");

        var root = Path.Combine(Path.GetTempPath(), "creatflow-sel-member-" + Guid.NewGuid().ToString("N")[..8]);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        try
        {
            // ── [1] 六档同一矩阵（路径 / 隔离 / 采纳 / 空旧档 / no-member / 坏档 / 显式锁定）──
            sb.AppendLine("\n[1] 六档会员级矩阵");
            await MatrixAsync<AccountSelectionStore>("勾选存档", "account-selection.json",
                "{\"autoPublish\":true}", "{}",
                (r, p) => new AccountSelectionStore(r, p),
                (s, m) => s.SaveAsync(new AccountSelectionData { AutoPublish = true }, m),
                async s => (await s.LoadAsync()).AutoPublish,
                s => s.GetConfigPath(), s => s.GetLegacyPath());

            await MatrixAsync<AccountOrderStore>("账号排序", "account-order.json",
                "[\"douyin:k1\"]", "[]",
                (r, p) => new AccountOrderStore(r, p),
                (s, m) => s.SaveAsync(["douyin:k1"], m),
                async s => (await s.LoadAsync()).Count > 0,
                s => s.GetConfigPath(), s => s.GetLegacyPath());

            await MatrixAsync<PublishDraftStore>("发布预设", "publish-defaults.json",
                "{\"douyin:k1\":{}}", "{}",
                (r, p) => new PublishDraftStore(r, p),
                (s, m) => s.SaveDefaultsAsync(new Dictionary<string, PlatformParams> { ["douyin:k1"] = new() }, m),
                async s => (await s.LoadDefaultsAsync()).Count > 0,
                s => s.GetConfigPath(), s => s.GetLegacyPath());

            await MatrixAsync<ComposeQueueStore>("合成队列", "compose_queue.json",
                "[{\"id\":\"t1\",\"title\":\"x\"}]", "[]",
                (r, p) => new ComposeQueueStore(r, p),
                (s, m) => s.SaveAsync([new ComposeTaskData { Id = "t1", Title = "x" }], m),
                async s => (await s.LoadAsync()).Count > 0,
                s => s.GetConfigPath(), s => s.GetLegacyPath());

            // 空旧档用「无任务但 IsPaused=true」—— 钉住「暂停态不算内容、不采纳」的决策
            await MatrixAsync<TtsQueueStore>("配音队列", "tts_queue.json",
                "{\"tasks\":[{\"id\":\"t1\",\"title\":\"x\"}]}", "{\"isPaused\":true}",
                (r, p) => new TtsQueueStore(r, p),
                (s, m) => s.SaveAsync(new TtsQueueState { Tasks = [new TtsTaskData { Id = "t1", Title = "x" }] }, m),
                async s => (await s.LoadAsync()).Tasks.Count > 0,
                s => s.GetConfigPath(), s => s.GetLegacyPath());

            // ── [2] 发布草稿（PublishDraftStore 的第二个文件，同规矩 + 广告位缺失过滤不动）──
            sb.AppendLine("\n[2] 发布草稿（publish-drafts.json）");
            var dCur = a;
            var ds = new PublishDraftStore(Path.Combine(root, "drafts"), () => dCur);
            await ds.SaveDraftsAsync([new PublishDraft(@"C:\不存在\v.mp4", null) { Title = "甲" }]);
            Check("草稿落会员后缀路径",
                ds.GetDraftsPath().Contains(@"\accounts\" + a.ToString("N"), StringComparison.OrdinalIgnoreCase),
                ds.GetDraftsPath());
            Check("草稿别名无关：GetConfigPath 仍是预设", ds.GetConfigPath().EndsWith("publish-defaults.json"));

            dCur = b;
            var dsB = new PublishDraftStore(Path.Combine(root, "drafts"), () => b);
            Check("草稿 B 读不到 A 的", (await dsB.LoadDraftsAsync()).Count == 0);

            var dsub2 = Path.Combine(root, "drafts-adopt");
            Directory.CreateDirectory(dsub2);
            var realVideo = Path.Combine(dsub2, "v.mp4");
            File.WriteAllText(realVideo, "x");   // 草稿加载会丢弃「视频已不存在」的条目 —— 夹具用真路径
            File.WriteAllText(Path.Combine(dsub2, "publish-drafts.json"),
                System.Text.Json.JsonSerializer.Serialize(new[] { new { videoPath = realVideo, title = "乙" } }));
            var dsAdopt = new PublishDraftStore(dsub2, () => a);
            var adopted = await dsAdopt.LoadDraftsAsync();
            Check("草稿旧档被采纳（内容可读）", adopted.Count == 1 && adopted[0].Title == "乙",
                $"count={adopted.Count}");
            Check("草稿旧档采纳后删除", !File.Exists(Path.Combine(dsub2, "publish-drafts.json")));

            // ── [3] 队列的显式 member 锁定（换号挂起期间任务收尾落老会员档的底层保证）──
            sb.AppendLine("\n[3] 队列显式 member 锁定（属主写）");
            var qroot = Path.Combine(root, "q-lock");
            var qStore = new ComposeQueueStore(qroot, () => b);   // 当前是 B
            await qStore.SaveAsync([new ComposeTaskData { Id = "ta", Title = "A 的活" }], a);   // 显式写 A
            Check("显式写 A：A 档有内容",
                (await new ComposeQueueStore(qroot, () => a).LoadAsync()).Count == 1);
            Check("显式写 A：B 档没被碰",
                (await new ComposeQueueStore(qroot, () => b).LoadAsync()).Count == 0);

            // ── [4] AccountChipsService 换号重载 + 在途写锁定 ──
            sb.AppendLine("\n[4] AccountChipsService 换号重载");
            var croot = Path.Combine(root, "chips");
            var cur = a;
            var selStore = new AccountSelectionStore(croot, () => cur);
            var orderStore = new AccountOrderStore(croot, () => cur);
            var svc = new AccountChipsService(selStore, orderStore);
            await svc.EnsureLoadedAsync();

            var x = Guid.NewGuid();
            var y = Guid.NewGuid();
            svc.RecordToggle(x, false);
            await svc.SetAutoPublishAsync(true);   // 链尾等落定 → A 档 {AutoPublish:true, Off:[x]}

            cur = b;
            await svc.EnsureLoadedAsync();         // 换号 → 整份重载
            Check("换号后开关不继承 A", !svc.AutoPublish);
            var aFile = await new AccountSelectionStore(croot, () => a).LoadAsync();
            var bFile = await new AccountSelectionStore(croot, () => b).LoadAsync();
            Check("A 档保住了自己的勾选与开关",
                aFile.AutoPublish && aFile.OffSessions.Count == 1 && aFile.OffSessions[0] == x,
                $"off={aFile.OffSessions.Count}");
            Check("B 档是干净的默认", !bFile.AutoPublish && bFile.OffSessions.Count == 0);

            // 在途写锁定：B 记账 → 立刻切回 A → B 那一笔必须落 B 的档（不清写、不串写）
            svc.RecordToggle(y, false);
            cur = a;
            await svc.SetAutoPublishAsync(true);   // EnsureLoaded 先排空 B 的在途写，再按 A 装载
            bFile = await new AccountSelectionStore(croot, () => b).LoadAsync();
            aFile = await new AccountSelectionStore(croot, () => a).LoadAsync();
            Check("B 的在途勾选落 B 档", bFile.OffSessions.Count == 1 && bFile.OffSessions[0] == y,
                $"off={bFile.OffSessions.Count}");
            Check("A 档没被 B 的那笔串改",
                aFile.AutoPublish && aFile.OffSessions.Count == 1 && aFile.OffSessions[0] == x);
            Check("切回 A 后内存开关是 A 的", svc.AutoPublish);

            // 胶水说明：矩阵/换号组的共享夹具
            async Task MatrixAsync<T>(
                string label, string file,
                string payloadJson, string emptyJson,
                Func<string, Func<Guid>, T> create,
                Func<T, Guid?, Task> save,
                Func<T, Task<bool>> hasContent,
                Func<T, string> memberPath,
                Func<T, string> legacyPath)
            {
                var sub = Path.Combine(root, "m-" + label);

                // ① 会员路径 + 写读（当前会员 = A；save 传 null = 跟随当前）
                var current = a;
                var store = create(sub, () => current);
                await save(store, null);
                Check($"{label}：会员路径含 \\accounts\\{{A}}",
                    memberPath(store).Contains(@"\accounts\" + a.ToString("N"), StringComparison.OrdinalIgnoreCase),
                    memberPath(store));
                Check($"{label}：写读往返（A）", await hasContent(store));

                // ② A/B 隔离：B 读不到 A 的档
                current = b;
                var storeB = create(sub, () => current);
                Check($"{label}：B 读不到 A 的档", !await hasContent(storeB));

                // ③ 显式 memberId 锁定：当前是 B，显式写 A → 落 A 档
                await save(storeB, a);
                current = a;
                Check($"{label}：显式 memberId 落指定会员档",
                    await hasContent(create(sub, () => current)));
                current = b;
                Check($"{label}：显式锁定不影响当前会员档",
                    !await hasContent(create(sub, () => current)));

                // ④ 旧档采纳：非空旧档 → 首读采纳 + 删旧档 + 只一次
                var sub2 = Path.Combine(root, "adopt-" + label);
                Directory.CreateDirectory(sub2);
                var legacy2 = Path.Combine(sub2, file);
                File.WriteAllText(legacy2, payloadJson);
                var adoptedStore = create(sub2, () => a);
                Check($"{label}：旧档被首读采纳", await hasContent(adoptedStore));
                Check($"{label}：采纳后旧档被删", !File.Exists(legacy2));
                Check($"{label}：采纳写出会员档", File.Exists(memberPath(adoptedStore)));
                Check($"{label}：采纳只一次（B 读不到）",
                    !await hasContent(create(sub2, () => b)));

                // ⑤ 空旧档：只删不采纳、不生成会员档
                var sub3 = Path.Combine(root, "empty-" + label);
                Directory.CreateDirectory(sub3);
                var legacy3 = Path.Combine(sub3, file);
                File.WriteAllText(legacy3, emptyJson);
                var emptyStore = create(sub3, () => a);
                Check($"{label}：空旧档不采纳（读回空）", !await hasContent(emptyStore));
                Check($"{label}：空旧档被清掉", !File.Exists(legacy3));
                Check($"{label}：空旧档不生成会员档",
                    !File.Exists(Path.Combine(sub3, "accounts", a.ToString("N"), file)));

                // ⑥ no-member（Guid.Empty）不采纳、旧档保留
                var sub4 = Path.Combine(root, "nomember-" + label);
                Directory.CreateDirectory(sub4);
                var legacy4 = Path.Combine(sub4, file);
                File.WriteAllText(legacy4, payloadJson);
                var noMember = create(sub4, () => Guid.Empty);
                Check($"{label}：no-member 路径",
                    memberPath(noMember).Contains("no-member", StringComparison.OrdinalIgnoreCase),
                    memberPath(noMember));
                Check($"{label}：no-member 不采纳", !await hasContent(noMember));
                Check($"{label}：no-member 旧档保留", File.Exists(legacy4));

                // ⑦ 坏会员档：读回默认、旧档不被吞（防「坏档被旧档洗掉」）
                var sub5 = Path.Combine(root, "corrupt-" + label);
                var corruptCurrent = a;
                var corruptStore = create(sub5, () => corruptCurrent);
                await save(corruptStore, null);
                var legacy5 = Path.Combine(sub5, file);
                File.WriteAllText(legacy5, payloadJson);
                File.WriteAllText(memberPath(corruptStore), "{ 半截 json");
                Check($"{label}：坏会员档读回空", !await hasContent(corruptStore));
                Check($"{label}：坏会员档不吞旧档", File.Exists(legacy5));
            }
        }
        catch (Exception ex)
        {
            failures++;
            sb.AppendLine($"  [FAIL] 自检抛异常：{ex}");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }

        var path = Path.Combine(Path.GetTempPath(), "newapp_memberscope_selftest.txt");
        sb.AppendLine(failures == 0 ? "\n═══ 全部通过 ═══" : $"\n═══ {failures} 项失败 ═══");
        try { File.WriteAllText(path, sb.ToString()); } catch { /* 写不了盘不影响退出码 */ }

        Console.WriteLine($"[LocalStoreMemberScopeSelfTest] failures={failures} -> {path}");
        return failures;
    }
}
