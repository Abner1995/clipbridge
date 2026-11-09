namespace CreatFlow.NewApp.Services;

/// <summary>
/// 角色库「会员级 + 上云」自检（2026-10-15 批次 8，`--selftest` 会一并跑）——
/// 全部 TEMP 夹具、**零网络**（不调 ComposeRoleCloudSync 的推拉 —— 本机真实 token.dat
/// 会让 CanSync 为真；只钉纯函数与本地服务生命周期）。
///
/// 为什么角色档不并入六档矩阵（LocalStoreMemberScopeSelfTest）：**空旧档语义正好相反** ——
/// 六档「空旧档只删不采纳」，角色档 `[]` 必须被采纳（= 用户删光全部角色，显式空不复活内置），
/// 且旧档是**裸数组**形状（要升级成 {Roles,Dirty} 包装）。
///
/// 结果写 %TEMP%/newapp_composerole_selftest.txt。
/// </summary>
public static class ComposeRoleCloudSelfTest
{
    /// <summary>同步入口（与其他自检同形）</summary>
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

        sb.AppendLine("═══ 角色库上云自检 ═══");

        var root = Path.Combine(Path.GetTempPath(), "creatflow-sel-roles-" + Guid.NewGuid().ToString("N")[..8]);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var sample = new ComposeRole { Id = "u1", Name = "测试角色", Template = "前缀{内容}", Persona = "人设", Knowledge = "资料" };

        try
        {
            // ── [1] 会员路径 / 包装档往返 / A·B 隔离 / 显式锁定 / 坏包装档 ──
            sb.AppendLine("\n[1] 会员级包装档");
            var cur = a;
            var store = new ComposeRoleStore(root, () => cur);
            Check("会员路径含 \\accounts\\{A}",
                store.GetConfigPath().Contains(@"\accounts\" + a.ToString("N"), StringComparison.OrdinalIgnoreCase),
                store.GetConfigPath());

            await store.SaveAsync(new ComposeRolesData { Roles = [sample], Dirty = true });
            var round = await store.LoadAsync();
            Check("包装档往返（Roles + Dirty）", round.Roles.Count == 1 && round.Roles[0] == sample && round.Dirty);

            cur = b;
            var forB = new ComposeRoleStore(root, () => b);
            var bData = await forB.LoadAsync();
            Check("B 读不到 A 的档（回落内置 2 条、不落盘）",
                bData.Roles.Count == 2 && !bData.Dirty && !File.Exists(forB.GetConfigPath()));

            await forB.SaveAsync(new ComposeRolesData { Roles = [sample] }, memberId: a);   // 当前 B 显式写 A
            Check("显式 memberId 落指定会员档",
                (await new ComposeRoleStore(root, () => a).LoadAsync()).Roles.Count == 1);
            Check("显式锁定不影响当前会员档（B 档仍不存在）", !File.Exists(forB.GetConfigPath()));

            // 坏包装档 → 内置；且不吞旁边的旧裸数组档
            var sub2 = Path.Combine(root, "corrupt");
            Directory.CreateDirectory(sub2);
            File.WriteAllText(Path.Combine(sub2, "compose_roles.json"), "[{\"id\":\"legacy-1\",\"name\":\"旧\"}]");   // 根=旧裸数组
            var cur2 = a;
            var corruptStore = new ComposeRoleStore(sub2, () => cur2);
            Directory.CreateDirectory(Path.GetDirectoryName(corruptStore.GetConfigPath())!);
            File.WriteAllText(corruptStore.GetConfigPath(), "{ 半截 json");   // 会员档=坏包装
            var corrupt = await corruptStore.LoadAsync();
            Check("坏会员档 → 内置兜底，且不读旧档",
                corrupt.Roles.Count == 2 && !corrupt.Dirty
                && File.Exists(Path.Combine(sub2, "compose_roles.json")));

            // ── [2] 旧档采纳矩阵（与六档相反：[] 也采纳）──
            sb.AppendLine("\n[2] 旧机器级档采纳（裸数组 → 包装，[] 也采纳）");

            // ① 非空裸数组 → 采纳 + Dirty + 删旧档
            var sub3 = Path.Combine(root, "adopt-nonempty");
            Directory.CreateDirectory(sub3);
            var legacy3 = Path.Combine(sub3, "compose_roles.json");
            File.WriteAllText(legacy3, System.Text.Json.JsonSerializer.Serialize(
                new List<ComposeRole> { sample }));
            var adopted = await new ComposeRoleStore(sub3, () => a).LoadAsync();
            Check("非空旧档被采纳（内容 + 标待推）",
                adopted.Roles.Count == 1 && adopted.Roles[0].Id == "u1" && adopted.Dirty);
            Check("采纳后旧档被删 + 会员档是包装形状",
                !File.Exists(legacy3)
                && File.ReadAllText(new ComposeRoleStore(sub3, () => a).GetConfigPath()).Contains("\"dirty\""));

            // ② 旧档 [] → **也采纳**（显式空库语义，不复活内置）
            var sub4 = Path.Combine(root, "adopt-empty");
            Directory.CreateDirectory(sub4);
            var legacy4 = Path.Combine(sub4, "compose_roles.json");
            File.WriteAllText(legacy4, "[]");
            var adoptedEmpty = await new ComposeRoleStore(sub4, () => a).LoadAsync();
            Check("空旧档 [] 被采纳为显式空 + 标待推（不复活内置）",
                adoptedEmpty.Roles.Count == 0 && adoptedEmpty.Dirty);
            Check("空旧档采纳后也被删", !File.Exists(legacy4));

            // ③ 坏旧档 → 不采纳、旧档留、会员档不落盘、内置兜底
            var sub5 = Path.Combine(root, "adopt-corrupt");
            Directory.CreateDirectory(sub5);
            var legacy5 = Path.Combine(sub5, "compose_roles.json");
            File.WriteAllText(legacy5, "{ 半截 json");
            var cur5 = a;
            var brokenLegacy = new ComposeRoleStore(sub5, () => cur5);
            var fromBroken = await brokenLegacy.LoadAsync();
            Check("坏旧档不采纳（内置 2 条、旧档留、会员档未落盘）",
                fromBroken.Roles.Count == 2 && !fromBroken.Dirty
                && File.Exists(legacy5) && !File.Exists(brokenLegacy.GetConfigPath()));

            // ④ 未登录（Guid.Empty）不采纳、旧档保留
            var sub6 = Path.Combine(root, "adopt-nomember");
            Directory.CreateDirectory(sub6);
            var legacy6 = Path.Combine(sub6, "compose_roles.json");
            File.WriteAllText(legacy6, System.Text.Json.JsonSerializer.Serialize(new List<ComposeRole> { sample }));
            var noMember = await new ComposeRoleStore(sub6).LoadAsync();
            Check("no-member 不采纳（内置 2 条、旧档保留）",
                noMember.Roles.Count == 2 && !noMember.Dirty && File.Exists(legacy6));

            // ⑤ 会员档在（合法）→ 旧档不再被读/删
            var sub7 = Path.Combine(root, "member-wins");
            Directory.CreateDirectory(sub7);
            var legacy7 = Path.Combine(sub7, "compose_roles.json");
            File.WriteAllText(legacy7, System.Text.Json.JsonSerializer.Serialize(new List<ComposeRole> { sample }));
            var cur7 = a;
            var memberWins = new ComposeRoleStore(sub7, () => cur7);
            await memberWins.SaveAsync(new ComposeRolesData { Roles = [], Dirty = false });
            var fromMember = await memberWins.LoadAsync();
            Check("会员档在 → 不读旧档（旧档原样留）",
                fromMember.Roles.Count == 0 && !fromMember.Dirty && File.Exists(legacy7));

            // ⑥ 会员档是裸数组（手工拷贝）→ 识别并升级成包装（Dirty=true）
            var sub8 = Path.Combine(root, "member-bare");
            var cur8 = a;
            var memberBare = new ComposeRoleStore(sub8, () => cur8);
            Directory.CreateDirectory(Path.GetDirectoryName(memberBare.GetConfigPath())!);
            File.WriteAllText(memberBare.GetConfigPath(),
                System.Text.Json.JsonSerializer.Serialize(new List<ComposeRole> { sample }));
            var upgraded = await memberBare.LoadAsync();
            Check("会员档裸数组被识别升级（内容保留 + 标待推）",
                upgraded.Roles.Count == 1 && upgraded.Dirty);
            Check("升级后是包装形状", File.ReadAllText(memberBare.GetConfigPath()).Contains("\"dirty\""));

            // ── [3] 服务生命周期（dirty / revision / Changed）──
            sb.AppendLine("\n[3] ComposeRolesService 生命周期");
            var svcRoot = Path.Combine(root, "svc");
            var svcCur = a;
            var svcStore = new ComposeRoleStore(svcRoot, () => svcCur);
            var svc = new ComposeRolesService(svcStore);
            var changedCount = 0;
            svc.Changed += () => changedCount++;

            await svc.SaveAsync([sample]);
            Check("本地保存 → dirty + Changed 触发",
                svc.IsDirty && changedCount == 1 && (await svcStore.LoadAsync()).Dirty);

            var staleRev = svc.Revision;
            await svc.SaveAsync([sample, sample with { Id = "u2", Name = "第二条" }]);   // 又改一笔 → rev 变
            await svc.ClearDirtyAsync(staleRev);
            Check("陈旧 revision 不清 dirty（那笔改动仍待推）", svc.IsDirty);

            await svc.ClearDirtyAsync(svc.Revision);
            Check("当前 revision 清 dirty + 落盘", !svc.IsDirty && !(await svcStore.LoadAsync()).Dirty);

            var beforeReplace = changedCount;
            await svc.ReplaceFromCloudAsync([sample with { Id = "cloud-1", Name = "云端角色" }]);
            Check("云端替换：清 dirty、不触发 Changed",
                !svc.IsDirty && changedCount == beforeReplace);
            Check("云端替换落盘（跨实例读盘一致）",
                (await new ComposeRoleStore(svcRoot, () => a).LoadAsync()).Roles is [{ Id: "cloud-1" }]);

            // 换会员：B 不继承 A 的档/待推
            svcCur = b;
            await svc.EnsureLoadedAsync();
            Check("换会员整份重载（B 回落内置、不继承 dirty）",
                svc.Roles.Count == 2 && !svc.IsDirty);

            // ── [4] 纯映射与落地判定 ──
            sb.AppendLine("\n[4] 映射与 ShouldLand（纯函数）");
            var push = ComposeRoleApi.MapForPush([sample]);
            Check("推送映射：5 字段原样", push.Count == 1 && push[0] is
                { Id: "u1", Name: "测试角色", Template: "前缀{内容}", Persona: "人设", Knowledge: "资料" });

            var pull = ComposeRoleApi.MapFromPull(new Models.ComposeRoleListResponse(true,
                [new Models.ComposeRoleItem("cloud-x", null, "T", null, null),
                 new Models.ComposeRoleItem("", "空 Id 应被丢弃", null, null, null)]));
            Check("拉取映射：空字段兜底 + 空 Id 行丢弃",
                pull.Count == 1 && pull[0] is { Id: "cloud-x", Name: "", Template: "T", Persona: "", Knowledge: "" });

            Check("ShouldLand：离线 null / exists=false 不落地；exists=true（含空表）落地",
                !ComposeRoleCloudSync.ShouldLand(null)
                && !ComposeRoleCloudSync.ShouldLand(new Models.ComposeRoleListResponse(false, []))
                && ComposeRoleCloudSync.ShouldLand(new Models.ComposeRoleListResponse(true, []))
                && ComposeRoleCloudSync.ShouldLand(new Models.ComposeRoleListResponse(true, [new Models.ComposeRoleItem("x", "n", "", "", "")])));
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

        var path = Path.Combine(Path.GetTempPath(), "newapp_composerole_selftest.txt");
        sb.AppendLine(failures == 0 ? "\n═══ 全部通过 ═══" : $"\n═══ {failures} 项失败 ═══");
        try { File.WriteAllText(path, sb.ToString()); } catch { /* 写不了盘不影响退出码 */ }

        Console.WriteLine($"[ComposeRoleCloudSelfTest] failures={failures} -> {path}");
        return failures;
    }
}
