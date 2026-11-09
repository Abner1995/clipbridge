using CreatFlow.NewApp.Models;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 账号分组自检（`--selftest` 会一并跑）—— 全部在 TEMP 夹具上做，不碰真实存档。
///
/// 守三件事：
///  · **集合语义**（纯函数）：「恰好相等才高亮」「死成员不算数」「空组永不命中」——
///    错一格用户看到的是「点了没反应/勾错账号」，页面上很难一眼看出；
///  · **整组勾选一次落盘**（ReplaceSelectionAsync）：OffSessions = all − on 的写定语义，
///    改错就是「点了组名，别的账号静默复活/失联」，也是静默错；
///  · **上云往返**（2026-10-15）：会员级隔离 / 旧档一次性采纳 / dirty 生命周期 /
///    云端整表替换 / 推送-拉取纯映射 —— 全部**零网络**（不调 AccountGroupCloudSync，
///    本机若留着真实 token.dat，编排器会真发请求）。
///
/// 结果写 %TEMP%/newapp_accountgroup_selftest.txt。
/// </summary>
public static class AccountGroupSelfTest
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

        sb.AppendLine("═══ 账号分组自检 ═══");

        var root = Path.Combine(Path.GetTempPath(), "creatflow-sel-" + Guid.NewGuid().ToString("N")[..8]);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var dead = Guid.NewGuid();

        try
        {
            // ── 1. 存档往返（含重启模拟 / 改名 / 删除 / 剪除 / 坏文件）──
            sb.AppendLine("\n[1] account-groups.json 往返");
            var store = new AccountGroupStore(root);
            Check("文件名钉死", store.GetConfigPath().EndsWith("account-groups.json"),
                store.GetConfigPath());

            var svc = new AccountGroupsService(store);
            svc.SaveAsync(new AccountGroup { Id = "g1", Name = "宠物", Sessions = [a, b] })
                .GetAwaiter().GetResult();
            svc.SaveAsync(new AccountGroup { Id = "g2", Name = "房产", Sessions = [b, c, dead] })
                .GetAwaiter().GetResult();

            // 模拟重启：新 store + 新 service 从盘读
            var svc2 = new AccountGroupsService(new AccountGroupStore(root));
            svc2.EnsureLoadedAsync().GetAwaiter().GetResult();
            Check("重启读回两组、组序保持", svc2.Groups.Count == 2
                && svc2.Groups[0].Name == "宠物" && svc2.Groups[1].Name == "房产",
                string.Join(",", svc2.Groups.Select(g => g.Name)));
            Check("重叠成员（同一账号可属多组）",
                svc2.Groups[0].Sessions.Contains(b) && svc2.Groups[1].Sessions.Contains(b));

            svc2.SaveAsync(new AccountGroup { Id = "g1", Name = "萌宠", Sessions = [a, b] })
                .GetAwaiter().GetResult();
            Check("改名：同 Id 换名不增行",
                svc2.Groups.Count == 2 && svc2.Groups[0].Name == "萌宠",
                string.Join(",", svc2.Groups.Select(g => g.Name)));

            // 剪除：dead 不在册 → 从 g2 剔掉；组本身保留（哪怕空）
            svc2.PruneAsync([a, b, c]).GetAwaiter().GetResult();
            Check("剪除死成员", !svc2.Groups[1].Sessions.Contains(dead),
                string.Join(",", svc2.Groups[1].Sessions));

            svc2.PruneAsync([]).GetAwaiter().GetResult();
            Check("整组死光留空组（用户起的名不替他丢）",
                svc2.Groups.Count == 2 && svc2.Groups.All(g => g.Sessions.Count == 0));

            svc2.DeleteAsync("g2").GetAwaiter().GetResult();
            Check("删除只删该组", svc2.Groups.Count == 1 && svc2.Groups[0].Id == "g1");

            File.WriteAllText(store.GetConfigPath(), "{ 半截 json");
            var svc3 = new AccountGroupsService(new AccountGroupStore(root));
            svc3.EnsureLoadedAsync().GetAwaiter().GetResult();
            Check("坏文件 → 空表不抛", svc3.Groups.Count == 0);

            // ── 2. 组名校验（纯函数）──
            sb.AppendLine("\n[2] 组名校验（重名拒绝——组条按名字点，重名无法区分）");
            var existing = new List<AccountGroup>
            {
                new() { Id = "g1", Name = "宠物" },
                new() { Id = "g2", Name = "房产" },
            };
            Check("首尾空格被忽略（'  宠物  ' 与 '宠物' 重名）",
                AccountGroups.ValidateName("  宠物  ", existing, selfId: null) is not null);
            Check("空名拒绝", AccountGroups.ValidateName("   ", existing, null) is not null);
            Check("与别的组重名拒绝",
                AccountGroups.ValidateName("房产", existing, "g1") is not null);
            Check("**与自己同名放行**（改名不改名都合法）",
                AccountGroups.ValidateName("宠物", existing, "g1") is null);
            Check("新组用没占用的名放行",
                AccountGroups.ValidateName(" 新组 ", existing, null) is null);

            // ── 3. 集合语义（纯函数）──
            sb.AppendLine("\n[3] 成员交集与「恰好相等」高亮");
            var pet = new AccountGroup { Id = "g1", Name = "宠物", Sessions = [a, b, dead] };
            var real = new AccountGroup { Id = "g2", Name = "房产", Sessions = [c] };
            var groups = new List<AccountGroup> { pet, real };

            var alive = new List<Guid> { a, b, c };
            Check("Members：死成员不计入", AccountGroups.Members(pet, alive).SetEquals([a, b]));

            Check("全勾 → 「全部」哨兵",
                AccountGroups.ActiveGroupId(groups, [a, b, c], alive) == AccountGroups.AllGroupsId);
            Check("恰等某组 → 该组（顺序无关、含死成员的组也按活成员算）",
                AccountGroups.ActiveGroupId(groups, [b, a], alive) == "g1");
            Check("部分勾选 → null", AccountGroups.ActiveGroupId(groups, [a], alive) is null);
            Check("空勾 → null（空组不因『都是空』被点亮）",
                AccountGroups.ActiveGroupId(groups, [], alive) is null);
            Check("空芯片列表 → null（页面还没加载出账号）",
                AccountGroups.ActiveGroupId(groups, [], []) is null);
            Check("单成员组恰等也命中",
                AccountGroups.ActiveGroupId(groups, [c], alive) == "g2");

            // ── 4. 整组勾选一次落盘（ReplaceSelectionAsync）──
            sb.AppendLine("\n[4] 整组勾选（OffSessions = all − on，一次写定）");
            var selRoot = Path.Combine(root, "sel");
            var selStore = new AccountSelectionStore(selRoot);
            var chipsSvc = new AccountChipsService(selStore);

            chipsSvc.ReplaceSelectionAsync([a, b], [a, b, c]).GetAwaiter().GetResult();
            var selData = new AccountSelectionStore(selRoot).LoadAsync().GetAwaiter().GetResult();
            Check("只勾 a/b → OffSessions == [c]", selData.OffSessions.Count == 1
                && selData.OffSessions[0] == c, string.Join(",", selData.OffSessions));

            chipsSvc.ReplaceSelectionAsync([a, b, c], [a, b, c]).GetAwaiter().GetResult();
            selData = new AccountSelectionStore(selRoot).LoadAsync().GetAwaiter().GetResult();
            Check("「全部」→ OffSessions 空（写后立读，确定性）", selData.OffSessions.Count == 0);

            chipsSvc.ReplaceSelectionAsync([], [a, b, c]).GetAwaiter().GetResult();
            selData = new AccountSelectionStore(selRoot).LoadAsync().GetAwaiter().GetResult();
            Check("空组 → 全不勾（OffSessions == 全部 3 个）", selData.OffSessions.Count == 3);

            // 不碰别的字段：AutoPublish 原样保留
            chipsSvc.SetAutoPublishAsync(true).GetAwaiter().GetResult();
            chipsSvc.ReplaceSelectionAsync([a], [a, b, c]).GetAwaiter().GetResult();
            selData = new AccountSelectionStore(selRoot).LoadAsync().GetAwaiter().GetResult();
            Check("整组勾选不擦「自动发布」开关", selData.AutoPublish);

            // ── 5. 会员隔离与上云往返（2026-10-15 上云）──
            sb.AppendLine("\n[5] 会员隔离与上云往返（云端权威 + 本地缓存）");
            var cloudRoot = Path.Combine(root, "cloud");
            var member = a;                                        // 可变会员（模拟登出→换号登录）
            var cStore = new AccountGroupStore(cloudRoot, () => member);
            var cSvc = new AccountGroupsService(cStore);
            var changedCount = 0;
            cSvc.Changed += () => changedCount++;

            cSvc.SaveAsync(new AccountGroup { Id = "cg1", Name = "甲组", Sessions = [a, b] })
                .GetAwaiter().GetResult();
            Check("会员档落在 accounts/{member:N} 下",
                cStore.GetConfigPath().Contains(@"\accounts\" + a.ToString("N"), StringComparison.OrdinalIgnoreCase),
                cStore.GetConfigPath());
            Check("本地改动置 dirty", cSvc.IsDirty);
            Check("改动触发 Changed（接云端推送的口）", changedCount == 1);

            member = b;                                            // 换号（单例服务复用的场景）
            cSvc.EnsureLoadedAsync().GetAwaiter().GetResult();
            Check("换会员重载：乙看不到甲的分组", cSvc.Groups.Count == 0);
            Check("换会员不继承甲的 dirty", !cSvc.IsDirty);

            // 旧档采纳（一次性）：机器级旧档存在 && 该会员无会员档 → 整份采纳（标 dirty）、删旧档
            File.WriteAllText(cStore.GetLegacyPath(),
                """{"groups":[{"id":"lg1","name":"旧档组","sessions":[]}]}""");
            member = c;
            var cSvc2 = new AccountGroupsService(cStore);
            var changedCount2 = 0;
            cSvc2.Changed += () => changedCount2++;
            cSvc2.EnsureLoadedAsync().GetAwaiter().GetResult();
            Check("旧档采纳给首个登录会员", cSvc2.Groups.Count == 1 && cSvc2.Groups[0].Name == "旧档组");
            Check("采纳标 dirty（随首次推送上云）", cSvc2.IsDirty);
            Check("采纳后删旧档", !File.Exists(cStore.GetLegacyPath()));

            Check("旧档不重复采纳（乙仍为空）",
                new AccountGroupStore(cloudRoot, () => b).LoadAsync().GetAwaiter().GetResult().Groups.Count == 0);

            // 未登录（Guid.Empty）不采纳：旧档留着，懒到下次登录
            var emptyRoot = Path.Combine(root, "cloud-empty");
            Directory.CreateDirectory(emptyRoot);
            var eStore = new AccountGroupStore(emptyRoot);
            File.WriteAllText(eStore.GetLegacyPath(),
                """{"groups":[{"id":"lg2","name":"未登录档","sessions":[]}]}""");
            var eData = eStore.LoadAsync().GetAwaiter().GetResult();
            Check("未登录不采纳旧档（旧档原样保留）", eData.Groups.Count == 0 && File.Exists(eStore.GetLegacyPath()));

            // dirty 生命周期：陈旧 rev 不清位（推送期间又改了一笔的保护）；当前 rev 才清并落盘
            cSvc2.ClearDirtyAsync(cSvc2.Revision - 1).GetAwaiter().GetResult();
            Check("陈旧 rev 不清 dirty", cSvc2.IsDirty);
            cSvc2.ClearDirtyAsync(cSvc2.Revision).GetAwaiter().GetResult();
            Check("当前 rev 清 dirty 并落盘", !cSvc2.IsDirty
                && !new AccountGroupStore(cloudRoot, () => c).LoadAsync().GetAwaiter().GetResult().Dirty);

            // 云端整表替换：不触发 Changed（防推-拉死循环）、落盘跨实例读回一致
            var changedBefore = changedCount2;
            cSvc2.ReplaceFromCloudAsync([
                new AccountGroup { Id = "c1", Name = "云组一", Sessions = [a] },
                new AccountGroup { Id = "c2", Name = "云组二", Sessions = [b, c] },
            ]).GetAwaiter().GetResult();
            Check("云端替换不触发 Changed（防推-拉死循环）", changedCount2 == changedBefore);
            var cReload = new AccountGroupStore(cloudRoot, () => c).LoadAsync().GetAwaiter().GetResult();
            Check("替换落盘：组数/组序/组名/成员一致", cReload.Groups.Count == 2
                && cReload.Groups[0].Name == "云组一" && cReload.Groups[1].Name == "云组二"
                && cReload.Groups[1].Sessions.SequenceEqual(new[] { b, c })
                && !cReload.Dirty);

            // Id 归一化：云端可能回带 "D" 形式 —— 载入统一成 "N"（不归一同一组会长两条）
            var dId = Guid.NewGuid().ToString("D");
            cSvc2.ReplaceFromCloudAsync([new AccountGroup { Id = dId, Name = "D 形式" }])
                .GetAwaiter().GetResult();
            Check("Id 归一 Guid \"N\"",
                new AccountGroupStore(cloudRoot, () => c).LoadAsync().GetAwaiter().GetResult()
                    .Groups[0].Id == Guid.Parse(dId).ToString("N"));

            // 推送/拉取纯映射（自检直接钉；不发网络）
            var pushItems = AccountGroupApi.MapForPush(
                [new AccountGroup { Id = Guid.NewGuid().ToString("N"), Name = "M", Sessions = [a] },
                 new AccountGroup { Id = "不是-Guid", Name = "坏", Sessions = [] }], out var skipped);
            Check("推送映射：N→Guid、坏 Id 跳过",
                pushItems.Count == 1 && skipped == 1 && pushItems[0].SessionIds!.SequenceEqual(new[] { a }));
            var pullList = AccountGroupApi.MapFromPull(new AccountGroupListResponse([
                new AccountGroupItem(a, "一", [b]), new AccountGroupItem(b, "二", null)]));
            Check("拉取映射：Guid→N、数组序=组序、空成员兜底", pullList.Count == 2
                && pullList[0].Id == a.ToString("N") && pullList[1].Id == b.ToString("N")
                && pullList[1].Sessions.Count == 0);
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

        var path = Path.Combine(Path.GetTempPath(), "newapp_accountgroup_selftest.txt");
        sb.AppendLine(failures == 0 ? "\n═══ 全部通过 ═══" : $"\n═══ {failures} 项失败 ═══");
        try { File.WriteAllText(path, sb.ToString()); } catch { /* 写不了盘不影响退出码 */ }

        Console.WriteLine($"[AccountGroupSelfTest] failures={failures} -> {path}");
        return failures;
    }
}
