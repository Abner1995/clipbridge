namespace CreatFlow.NewApp.Services;

/// <summary>
/// 上传提示音音量自检（`--selftest` 会一并跑）—— 全部在 TEMP 夹具上做，不碰真实会员档。
///
/// 守的是「音量百分比持久化 + 立即生效」这条线：值存错/读坏会让用户的静音选择悄悄消失，
/// 增益映射错会让滑块拖到头也听不出区别 —— 都只能靠断言钉住。
///
/// 结果写 %TEMP%/newapp_soundsettings_selftest.txt。
/// </summary>
public static class SoundSettingsSelfTest
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

        sb.AppendLine("═══ 上传提示音音量自检 ═══");

        var root = Path.Combine(Path.GetTempPath(), "creatflow-sel-sound-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            var memberA = Guid.NewGuid();
            var memberB = Guid.NewGuid();
            var storeA = new SoundSettingsStore(root, () => memberA);
            var storeB = new SoundSettingsStore(root, () => memberB);

            // ── 1. 会员级路径 ──
            sb.AppendLine("\n[1] 会员级存档路径");
            var pathA = storeA.GetConfigPath(memberA);
            Check("文件名钉死 sound-settings.json", pathA.EndsWith("sound-settings.json"), pathA);
            Check("路径在 accounts/{memberId:N}/ 下且按会员隔离",
                pathA.Contains(Path.Combine("accounts", memberA.ToString("N")), StringComparison.OrdinalIgnoreCase)
                && !pathA.Contains(memberB.ToString("N"), StringComparison.OrdinalIgnoreCase),
                pathA);

            // ── 2. 缺文件 → 默认 70% ──
            sb.AppendLine("\n[2] 缺文件与默认值");
            Check($"缺文件 → 默认 {SoundSettingsStore.DefaultVolumePercent}%",
                storeA.LoadAsync().GetAwaiter().GetResult().VolumePercent
                == SoundSettingsStore.DefaultVolumePercent);
            Check("新档默认值也是 70%",
                new SoundSettings().VolumePercent == SoundSettingsStore.DefaultVolumePercent);

            // ── 3. 百分比往返（新实例 = 模拟重启）──
            sb.AppendLine("\n[3] 百分比落盘往返");
            foreach (var pct in new[] { 0, 35, 70, 100 })
            {
                var ok = storeA.SaveAsync(memberA, new SoundSettings { VolumePercent = pct })
                    .GetAwaiter().GetResult();
                var back = new SoundSettingsStore(root, () => memberA)
                    .LoadAsync().GetAwaiter().GetResult();
                Check($"{pct}% 写盘 → 新实例读回", ok && back.VolumePercent == pct,
                    $"ok={ok} back={back.VolumePercent}");
            }

            // ── 4. 会员隔离 ──
            sb.AppendLine("\n[4] 换会员不串档");
            storeA.SaveAsync(memberA, new SoundSettings { VolumePercent = 0 })
                .GetAwaiter().GetResult();
            Check("A 设 0%（静音）后 B 仍是默认 70%",
                storeB.LoadAsync().GetAwaiter().GetResult().VolumePercent
                == SoundSettingsStore.DefaultVolumePercent);

            // ── 5. 坏档 / 非法百分比退默认 ──
            sb.AppendLine("\n[5] 坏档与非法值");
            File.WriteAllText(pathA, "{ 半截 json");
            Check("坏 JSON → 默认 70%",
                storeA.LoadAsync().GetAwaiter().GetResult().VolumePercent
                == SoundSettingsStore.DefaultVolumePercent);

            foreach (var bad in new[] { -5, 101, 999 })
            {
                File.WriteAllText(pathA, $"{{\"volumePercent\": {bad}}}");
                Check($"非法值 {bad}% → 默认 70%（不钳制，退默认）",
                    storeA.LoadAsync().GetAwaiter().GetResult().VolumePercent
                    == SoundSettingsStore.DefaultVolumePercent);
            }

            // 旧版 4 档字符串档位（本次迭代前的档形）：认不出 → 默认，不抛
            File.WriteAllText(pathA, "{\"volume\": \"Low\"}");
            Check("旧档 volume=\"Low\" → 默认 70%（未知字段忽略，不抛）",
                storeA.LoadAsync().GetAwaiter().GetResult().VolumePercent
                == SoundSettingsStore.DefaultVolumePercent);

            // ── 6. 增益映射 ──
            sb.AppendLine("\n[6] 百分比 → 线性增益");
            Check("0% = 0（不出声）", PublishSoundNotifier.GainOf(0) == 0f);
            Check("35% → 0.35", Math.Abs(PublishSoundNotifier.GainOf(35) - 0.35f) < 0.0001f);
            Check("70% → 0.70", Math.Abs(PublishSoundNotifier.GainOf(70) - 0.70f) < 0.0001f);
            Check("100% = 1（满音量，与老版一致）", PublishSoundNotifier.GainOf(100) == 1f);
            Check("单调递增", PublishSoundNotifier.GainOf(0) < PublishSoundNotifier.GainOf(35)
                && PublishSoundNotifier.GainOf(35) < PublishSoundNotifier.GainOf(70)
                && PublishSoundNotifier.GainOf(70) < PublishSoundNotifier.GainOf(100));

            // ── 7. 静态音量（上传消费者靠它）──
            sb.AppendLine("\n[7] 静态音量立即生效");
            var savedPercent = PublishSoundNotifier.VolumePercent;
            try
            {
                PublishSoundNotifier.VolumePercent = 0;
                Check("设 0% → Enabled=false", !PublishSoundNotifier.Enabled);
                PublishSoundNotifier.VolumePercent = 35;
                Check("设 35% → Enabled=true", PublishSoundNotifier.Enabled);
            }
            finally
            {
                PublishSoundNotifier.VolumePercent = savedPercent;
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

        var path = Path.Combine(Path.GetTempPath(), "newapp_soundsettings_selftest.txt");
        sb.AppendLine(failures == 0 ? "\n═══ 全部通过 ═══" : $"\n═══ {failures} 项失败 ═══");
        try { File.WriteAllText(path, sb.ToString()); } catch { /* 写不了盘不影响退出码 */ }

        Console.WriteLine($"[SoundSettingsSelfTest] failures={failures} -> {path}");
        return failures;
    }
}
