namespace CreatFlow.NewApp.Services;

/// <summary>
/// 账号 WebView2 档案目录清理 —— 自老版 WebViewLoginWindow 清理三件套移植,改造为会员域粒度:
///
///   标签坞常驻后「解绑时账号标签可能还开着」,渲染进程退出是异步的,
///   关闭后直接删目录必然被锁(半删 → 下次登录用损坏 profile → 白屏,历史白屏根因)。
///   因此删除前先等进程退出(锁探测 ≤8s),再重试删除(5×1s);仍失败则写会员域 marker
///   (wv2/{sessionId:N}.pending-delete),下次启动/开任何标签前补删(此时无 WebView2 实例,
///   删除必然成功)。
///
/// 与老版差异:老版是会员无关的全局单文件 marker + 全清语义;新版档案已按
/// accounts/{memberId} 隔离,marker 必须落在会员域内,只清自己的档案,防误删他人数据。
///
/// ⚠️ 不能用 Process.GetProcessesByName("msedgewebview2") 全等:系统里其他宿主的
/// WebView2 实例(SearchHost 等)永远在线,按进程名等待会无谓等满超时;
/// 锁探针只探测我们自己的档案目录(精确匹配)。
/// </summary>
public static class AccountProfileCleanup
{
    private const int UnlockWaitMs = 8000;   // 渲染进程退出等待上限
    private const int PollIntervalMs = 200;
    private const int DeleteAttempts = 5;
    private const int DeleteRetryDelayMs = 1000;

    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>某账号档案目录是否仍被 WebView2 进程独占(单例锁 lockfile 为锁探针)</summary>
    public static bool IsProfileDirLocked(string profileDir)
    {
        // WebView2(Chromium)进程活着时**持续持有**的是单例锁 `lockfile`(删除报错实锤:
        // "cannot access the file 'lockfile'"),实测位于 UDF 顶层 = {同目录}\EBWebView\lockfile,
        // 且被持有时独占打开必然失败。
        // ⚠️ 别再只探 `Local State`:它只是退出/设置变更时写一次的文件,**不常驻持有** ——
        // 进程活着也会探到"未锁",删除提前开跑(半删根因,2026-10-10 实测)。
        // FileNotFoundException 是 IOException 子类,若按 IOException 处理会把"文件不存在"误判成"被锁"。
        var candidates = new[]
        {
            Path.Combine(profileDir, "EBWebView", "lockfile"),    // 现行结构(实测)
            Path.Combine(profileDir, "lockfile"),                 // 老结构兜底
            Path.Combine(profileDir, "EBWebView", "Local State"), // 最老探针口径,保留兜底
        };

        foreach (var probeFile in candidates)
        {
            if (!File.Exists(probeFile))
                continue;

            try
            {
                using var fs = File.Open(probeFile,
                    FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                // 能独占打开 = 没被锁;继续探下一个
            }
            catch (FileNotFoundException)
            {
                // 探测瞬间被删 — 未锁
            }
            catch (IOException)
            {
                return true; // 文件被独占持有 — 渲染进程还活着
            }
            catch (Exception)
            {
                // 权限等其他异常 — 按未锁处理,删除阶段的重试兜底
            }
        }

        return false; // 探针文件都不在(或都未被锁)— 视为未锁
    }

    /// <summary>等待档案目录解锁(渲染进程退出);超时返回 false — 调用方不得强删</summary>
    public static bool WaitForUnlock(string profileDir, int timeoutMs = UnlockWaitMs)
    {
        if (!Directory.Exists(profileDir))
            return true; // 目录已不在 = 无需等待

        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (!IsProfileDirLocked(profileDir))
                return true;
            Thread.Sleep(PollIntervalMs);
        }

        Log.Warning("Profile dir still locked after {Ms}ms — skip delete, deferred: {Dir}",
            timeoutMs, profileDir);
        return false;
    }

    /// <summary>
    /// 等锁后尝试删除档案目录(5×1s 重试)。
    /// 返回 true = 已删除或本来就不存在;false = 仍被锁/删除失败,调用方应写 marker。
    /// </summary>
    public static bool TryDeleteProfileDir(string profileDir)
    {
        if (!Directory.Exists(profileDir))
            return true; // 目录不存在 = 已删干净,不算失败(否则误写 marker)

        if (!WaitForUnlock(profileDir))
            return false;

        for (int attempt = 0; attempt < DeleteAttempts; attempt++)
        {
            try
            {
                Directory.Delete(profileDir, recursive: true);
                return true;
            }
            catch (DirectoryNotFoundException)
            {
                // 递归删除中途子路径已缺失(结构损坏)→ 视为已删大部分,不再重试
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Profile dir delete attempt {N} failed: {Dir}", attempt + 1, profileDir);
                Thread.Sleep(DeleteRetryDelayMs);
            }
        }
        return false;
    }

    /// <summary>延迟删除标记路径(会员域:wv2/{sessionId:N}.pending-delete;由档案目录推导)</summary>
    public static string MarkerPath(string profileDir) =>
        Path.Combine(Path.GetDirectoryName(profileDir) ?? profileDir,
            Path.GetFileName(profileDir) + ".pending-delete");

    /// <summary>写延迟删除标记(解绑/回滚时目录仍被锁)。写后自动安排同运行内延迟重试串 ——
    /// 渲染进程退出/宿主 GC 释放实测可滞后数十秒~分钟,等完即删,不用拖到下次启动;仍失败保留 marker 兜底。
    /// memberId 用于重试前核对本地镜像:期间档案变成"在册"(登录完成/同 ID 重连)则摘掉误标、不删。</summary>
    public static void WritePendingMarker(string profileDir, Guid memberId)
    {
        try
        {
            var marker = MarkerPath(profileDir);
            Directory.CreateDirectory(Path.GetDirectoryName(marker) ?? ".");
            File.WriteAllText(marker, DateTime.Now.ToString("O"));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Write pending-delete marker failed for {Dir}", profileDir);
        }
        _ = RetryDeleteLaterAsync(profileDir, memberId);
    }

    /// <summary>标记后延迟重试删除:15s 起步,之后每 30s 一轮,共约 5 分钟。
    /// 渲染进程/宿主 GC 释放实测可滞后数十秒~分钟(2026-10-10:15s 时仍被锁)——一轮不够,
    /// 持续重试到释放为止;成即摘 marker,仍失败保留 marker 由启动/开标签前补删兜底。
    /// 每轮删除前核对镜像:期间档案变成"在册"(登录完成/同 ID 重连补建)→ 当初是误标,摘 marker 不删。
    /// (会话 ID 不复用,无删错新档案风险)</summary>
    private static async Task RetryDeleteLaterAsync(string profileDir, Guid memberId)
    {
        try
        {
            var id = Path.GetFileName(profileDir).ToLowerInvariant();
            for (var round = 0; round < 10; round++)
            {
                await Task.Delay(round == 0 ? DeleteRetryDelayMs * 15 : 30_000);

                if (LoadMirror(memberId) is { } mirror && mirror.Contains(id))
                {
                    // 等待期间该档案已入册 —— marker 是误标,摘掉不再删
                    try { File.Delete(MarkerPath(profileDir)); } catch { /* 已由他处清理 */ }
                    return;
                }

                if (!TryDeleteProfileDir(profileDir)) continue;
                try { File.Delete(MarkerPath(profileDir)); } catch { /* 已由他处清理 */ }
                return;
            }
        }
        catch
        {
            // 静默:兜底链条仍在(marker → 下次启动补删)
        }
    }

    /// <summary>
    /// 启动/开标签前补删:扫描会员域 *.pending-delete → 等锁 → 删档案 → 删 marker。
    /// 失败保留 marker 下次再试(下次启动无 WebView2 实例,删除必然成功)。
    /// 幂等(内部互斥闸门),调用方放在后台线程。
    /// </summary>
    public static void RunPendingCleanup(Guid memberId)
    {
        var wv2Dir = PlatformPaths.Wv2Dir(memberId);
        if (!Directory.Exists(wv2Dir))
            return;

        string[] markers;
        try
        {
            markers = Directory.GetFiles(wv2Dir, "*.pending-delete");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Scan pending-delete markers failed: {Dir}", wv2Dir);
            return;
        }
        if (markers.Length == 0) return;

        Gate.Wait();
        try
        {
            var mirror = LoadMirror(memberId);   // null = 镜像读坏,本轮不按镜像豁免、也不做孤儿清扫(保守)

            foreach (var marker in markers)
            {
                var name = Path.GetFileNameWithoutExtension(marker); // {sessionId:N}
                var profileDir = Path.Combine(wv2Dir, name);

                // ⚠️ 在册(镜像里有)的档案绝不按 marker 删:marker 的合法来源(解绑[先摘镜像再写]/
                // 取消登录[从未登记]/孤儿清扫[非镜像])写 marker 时都不在镜像;若现在在册,只可能是
                // 误标(如"登录中途又添加账号"触发的清扫把在途档案标了)或同 ID 重连补建 ——
                // 删档案 = 丢该账号登录态,摘掉 marker 保留档案才是对的。
                if (mirror is not null && mirror.Contains(name))
                {
                    try { File.Delete(marker); } catch { /* 已由他处清理 */ }
                    continue;
                }

                if (!Directory.Exists(profileDir))
                {
                    File.Delete(marker); // 档案已不在,marker 无意义
                    continue;
                }
                if (TryDeleteProfileDir(profileDir))
                    File.Delete(marker);
                // 失败:保留 marker 下次再试
            }

            if (mirror is not null)
                SweepOrphanProfiles(memberId, wv2Dir, mirror);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "RunPendingCleanup failed");
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>读会员域本地镜像(sessions.json)→ 档案名集合({sessionId:N} 小写)。
    /// 返回 null = 文件存在但读坏 —— 调用方保守处理(不做孤儿清扫、marker 不按镜像豁免)。</summary>
    private static HashSet<string>? LoadMirror(Guid memberId)
    {
        var jsonPath = PlatformPaths.SessionsJson(memberId);
        if (!File.Exists(jsonPath))
        {
            // 联网版：无镜像 = 本端没有已绑定档案，可清扫孤儿。
            // 单机版：SQLite 才是权威 —— json 可能只是丢了/没生成，
            // 此时把 wv2 当孤儿清掉就是删用户登录态。返回 null = 本轮不清扫。
            return AppConfig.IsStandalone ? null : [];
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    set.Add(id.GetString()!.Replace("-", "").ToLowerInvariant());
            }
            return set;
        }
        catch
        {
            return null;   // 读坏 → 保守:本轮不做孤儿清扫、marker 也不按镜像豁免
        }
    }

    /// <summary>
    /// 孤儿档案清扫:取消登录/崩溃残留的 wv2 目录(不在本地镜像记录中)→ 同法删除,
    /// 删不掉写 marker。只在启动(无任何标签/登录窗在途)或开新标签前执行,不误伤活动档案。
    /// 镜像由调用方传入(读坏时调用方直接跳过清扫,保守)。
    /// </summary>
    private static void SweepOrphanProfiles(Guid memberId, string wv2Dir, HashSet<string> mirror)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(wv2Dir))
            {
                var name = Path.GetFileName(dir);
                if (name.EndsWith(".pending-delete", StringComparison.OrdinalIgnoreCase)) continue;
                if (mirror.Contains(name.ToLowerInvariant())) continue; // 已绑定档案,保留
                if (TryDeleteProfileDir(dir))
                    continue;
                WritePendingMarker(dir, memberId); // 占用中 → marker 下次补删
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Sweep orphan profiles failed");
        }
    }
}
