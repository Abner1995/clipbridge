namespace CreatFlow.NewApp.Services;

/// <summary>
/// 日志清理自检（`--selftest` 会一并跑）—— 全部在 TEMP 夹具上做，不碰真实日志目录。
///
/// 守的是「按天保留」的边界语义：严格早于 cutoff 才删（相等保留）、锁住的文件跳过、
/// 非 .log 不动、非法设置值兜默认。错了是**静默**错 —— 多删一天用户的日志就没了，
/// 少删则功能看着没生效，只能靠这些断言钉住。
///
/// 结果写 %TEMP%/newapp_logcleanup_selftest.txt。
/// </summary>
public static class LogCleanupSelfTest
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

        static string MakeFile(string dir, string name, DateTime time)
        {
            var p = Path.Combine(dir, name);
            File.WriteAllText(p, "fixture");
            File.SetLastWriteTime(p, time);
            return p;
        }

        sb.AppendLine("═══ 日志清理自检 ═══");

        var root = Path.Combine(Path.GetTempPath(), "creatflow-sel-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            var now = DateTime.Now;

            // ── 1. PurgeDir：三档 + 两个前缀 + 不动非 .log ──
            sb.AppendLine("\n[1] 按天清理（logs 目录，*.log 通配）");
            var logs = Path.Combine(root, "logs");
            Directory.CreateDirectory(logs);
            var f40New = MakeFile(logs, "log-newapp-20260901.log", now.AddDays(-40));
            var f40Old = MakeFile(logs, "log-20260901.log", now.AddDays(-40));
            var f20 = MakeFile(logs, "log-newapp-20260920.log", now.AddDays(-20));
            var f0 = MakeFile(logs, "log-newapp-today.log", now);
            var json = MakeFile(logs, "notes.json", now.AddDays(-40));

            var (d30, s30, _) = LogCleanup.PurgeDir(logs, "*.log", now.AddDays(-30));
            Check("保留 30 天：40 天档删（新旧两个前缀都覆盖）",
                d30 == 2 && !File.Exists(f40New) && !File.Exists(f40Old), $"deleted={d30}");
            Check("保留 30 天：20 天档保留", File.Exists(f20));
            Check("保留 30 天：今天的保留", File.Exists(f0));
            Check("保留 30 天：非 .log 文件不动（notes.json）", File.Exists(json));
            Check("返回的跳过数为 0（无锁场景）", s30 == 0, s30.ToString());

            var (d15, _, _) = LogCleanup.PurgeDir(logs, "*.log", now.AddDays(-15));
            Check("保留 15 天：20 天档也删", d15 == 1 && !File.Exists(f20), $"deleted={d15}");

            // 边界：mtime 恰好 == cutoff → 保留（严格早于才删）
            var cut = new DateTime(2026, 10, 1, 12, 0, 0);
            var edge = MakeFile(logs, "log-newapp-edge.log", cut);
            var (dEdge, _, _) = LogCleanup.PurgeDir(logs, "*.log", cut);
            Check("**严格早于 cutoff 才删（恰好相等保留）**",
                dEdge == 0 && File.Exists(edge), $"deleted={dEdge}");

            // ── 2. 锁住的文件：跳过、不抛；释放后能删 ──
            sb.AppendLine("\n[2] 被占用的文件（正在写的日志就是这条路径）");
            var locked = MakeFile(logs, "log-newapp-locked.log", now.AddDays(-99));
            int dLock, sLock;
            using (var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                (dLock, sLock, _) = LogCleanup.PurgeDir(logs, "*.log", now.AddDays(-30));
            }
            Check("锁定文件：跳过不抛、文件健在", sLock == 1 && File.Exists(locked),
                $"deleted={dLock} skipped={sLock}");
            var (dAfter, _, _) = LogCleanup.PurgeDir(logs, "*.log", now.AddDays(-30));
            Check("释放后重跑能删掉", dAfter >= 1 && !File.Exists(locked), $"deleted={dAfter}");

            // ── 3. crashes 目录（crash_*.log）──
            sb.AppendLine("\n[3] 崩溃记录目录");
            var crashes = Path.Combine(root, "crashes");
            Directory.CreateDirectory(crashes);
            var cOld = MakeFile(crashes, "crash_20260901_120000_abcd1234.log", now.AddDays(-40));
            var cNew = MakeFile(crashes, "crash_20261014_120000_abcd1234.log", now);
            var (dCrash, _, _) = LogCleanup.PurgeDir(crashes, "crash_*.log", now.AddDays(-30));
            Check("crashes：旧删新留", dCrash == 1 && !File.Exists(cOld) && File.Exists(cNew),
                $"deleted={dCrash}");

            // ── 4. 目录不存在 / 单文件版 ──
            sb.AppendLine("\n[4] 不存在路径与单文件");
            var missing = Path.Combine(root, "nope");
            Check("目录不存在 → no-op 不抛",
                LogCleanup.PurgeDir(missing, "*.log", now) == (0, 0, 0L));
            Check("Measure 目录不存在不抛", LogCleanup.MeasureDir(missing) == (0, 0L));

            var desk = MakeFile(root, "creatflow_debug.log", now.AddDays(-40));
            var (deskDel, _) = LogCleanup.PurgeFileIfOlder(desk, now.AddDays(-30));
            Check("桌面单文件：旧的删", deskDel && !File.Exists(desk));
            var desk2 = MakeFile(root, "creatflow_debug.log", now);
            Check("桌面单文件：新的留",
                !LogCleanup.PurgeFileIfOlder(desk2, now.AddDays(-30)).Deleted && File.Exists(desk2));
            Check("桌面单文件：不存在 → false",
                !LogCleanup.PurgeFileIfOlder(Path.Combine(root, "none.log"), now).Deleted);

            // ── 5. 设置 store ──
            sb.AppendLine("\n[5] 日志保留设置（log-settings.json）");
            var storeRoot = Path.Combine(root, "settings");
            var store = new LogSettingsStore(storeRoot);
            Check("文件名钉死", store.GetConfigPath().EndsWith("log-settings.json"),
                store.GetConfigPath());
            Check("缺文件 → 默认 30",
                store.LoadAsync().GetAwaiter().GetResult().RetentionDays == 30);

            store.SaveAsync(new LogSettingsData { RetentionDays = 15 }).GetAwaiter().GetResult();
            var store2 = new LogSettingsStore(storeRoot);   // 模拟重启
            Check("写 15 → 新实例读回 15",
                store2.LoadAsync().GetAwaiter().GetResult().RetentionDays == 15);

            File.WriteAllText(store.GetConfigPath(), "{ 半截 json");
            Check("坏文件 → 默认 30",
                store.LoadAsync().GetAwaiter().GetResult().RetentionDays == 30);

            foreach (var bad in new[] { 0, -5, 9999 })
            {
                File.WriteAllText(store.GetConfigPath(), $"{{\"retentionDays\": {bad}}}");
                Check($"非法值 {bad} → 默认 30（不钳制，退默认）",
                    store.LoadAsync().GetAwaiter().GetResult().RetentionDays == 30);
            }

            // ── 6. 服务编排（三处合计 + Summary 文案）──
            sb.AppendLine("\n[6] LogCleanupService 编排（TEMP 根）");
            var svcRoot = Path.Combine(root, "svc");
            var svcLogs = Path.Combine(svcRoot, "logs");
            var svcCrashes = Path.Combine(svcRoot, "crashes");
            Directory.CreateDirectory(svcLogs);
            Directory.CreateDirectory(svcCrashes);
            var sOld = MakeFile(svcLogs, "log-newapp-old.log", now.AddDays(-40));
            var sNew = MakeFile(svcLogs, "log-newapp-new.log", now);
            var scOld = MakeFile(svcCrashes, "crash_old.log", now.AddDays(-40));
            var scNew = MakeFile(svcCrashes, "crash_new.log", now);
            var sDesk = MakeFile(svcRoot, "creatflow_debug.log", now.AddDays(-40));

            var svcStore = new LogSettingsStore(svcRoot);
            svcStore.SaveAsync(new LogSettingsData { RetentionDays = 15 }).GetAwaiter().GetResult();
            var svc = new LogCleanupService(svcStore, svcRoot);
            var r = svc.RunAsync().GetAwaiter().GetResult();

            Check("三处合计删 3（logs 旧 + crashes 旧 + 桌面旧）", r.Deleted == 3, $"deleted={r.Deleted}");
            Check("旧的三个全删、新的两个健在",
                !File.Exists(sOld) && !File.Exists(scOld) && !File.Exists(sDesk)
                && File.Exists(sNew) && File.Exists(scNew));
            Check("Summary 带数字与单位", r.Summary.Contains("已清理 3 个文件"),
                r.Summary);
            var (mf, mb) = svc.Measure();
            Check("Measure 数得到剩余文件（2 个）", mf == 2 && mb > 0, $"files={mf} bytes={mb}");
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

        var path = Path.Combine(Path.GetTempPath(), "newapp_logcleanup_selftest.txt");
        sb.AppendLine(failures == 0 ? "\n═══ 全部通过 ═══" : $"\n═══ {failures} 项失败 ═══");
        try { File.WriteAllText(path, sb.ToString()); } catch { /* 写不了盘不影响退出码 */ }

        Console.WriteLine($"[LogCleanupSelfTest] failures={failures} -> {path}");
        return failures;
    }
}
