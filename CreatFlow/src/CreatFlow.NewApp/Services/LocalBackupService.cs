using System.IO.Compression;
using CreatFlow.LocalStore;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 单机版数据备份 / 导出 / 导入（doc 11.2）。
///
/// 导出 = SQLite 一致性快照（VACUUM INTO）+ 会员级 JSON 档 + manifest → zip。
/// **不含** wv2 浏览器档案、模型、日志、缓存 —— 档案体积大且与机器绑定，换机后本来就要
/// 重新登录各平台账号（Cookie 在库里，但小红书/抖音签名依赖档案页，缺档案就签不了）。
///
/// 导入 = 校验 manifest → 先备份当前库（pre-import）→ 解到临时目录 → 原子替换。
/// 解压失败/版本不符一律不覆盖当前数据；导入完成后必须重启（内存里还有旧连接/页面状态）。
/// </summary>
public static class LocalBackupService
{
    public const string Format = "creatflow-backup";
    public const int FormatVersion = 1;
    private const string ManifestEntry = "manifest.json";
    private const string DbEntry = "creatflow.db";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    /// <summary>manifest 内容（zip 自描述：版本 / 会员 / 文件清单）</summary>
    public sealed class BackupManifest
    {
        public string Format { get; set; } = "";
        public int Version { get; set; }
        public string AppVersion { get; set; } = "";
        public string Edition { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public Guid MemberId { get; set; }
        public List<string> Files { get; set; } = [];
    }

    /// <summary>导出到 zip；返回实际写出的路径与条目数。</summary>
    public static async Task<(string Path, int Files)> ExportAsync(
        string zipPath, CancellationToken ct = default)
    {
        var dbPath = LocalStorePaths.DatabasePath;
        if (!File.Exists(dbPath))
            throw new FileNotFoundException("本地数据库不存在，无法导出", dbPath);

        var tempDir = Path.Combine(Path.GetTempPath(), "cf-export-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        var snapshot = Path.Combine(tempDir, DbEntry);

        try
        {
            // 一致性快照（不能直接拷 .db：WAL 里可能还有未落盘的事务）
            using (var db = LocalStoreInitializer.CreateContext(dbPath))
            {
                var made = LocalStoreInitializer.Backup(db, "export", tempDir);
                File.Move(made, snapshot, overwrite: true);
            }

            var memberDir = LocalStorePaths.MemberDir;
            var jsonFiles = Directory.Exists(memberDir)
                ? Directory.GetFiles(memberDir, "*.json")
                : [];

            if (File.Exists(zipPath))
                File.Delete(zipPath);

            var entries = new List<string>();
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(snapshot, DbEntry, CompressionLevel.Optimal);
                entries.Add(DbEntry);

                foreach (var json in jsonFiles)
                {
                    var name = Path.GetFileName(json);
                    zip.CreateEntryFromFile(json, $"accounts/{LocalIdentity.MemberId:N}/{name}",
                        CompressionLevel.Optimal);
                    entries.Add($"accounts/{LocalIdentity.MemberId:N}/{name}");
                }

                var manifest = new BackupManifest
                {
                    Format = Format,
                    Version = FormatVersion,
                    AppVersion = AppConfig.AppVersion,
                    Edition = AppConfig.Edition,
                    CreatedAt = DateTime.Now,
                    MemberId = LocalIdentity.MemberId,
                    Files = entries,
                };
                var entry = zip.CreateEntry(ManifestEntry, CompressionLevel.Optimal);
                await using var stream = entry.Open();
                await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, ct);
            }

            return (zipPath, entries.Count);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// 导入 zip。**调用方必须提示用户重启**（内存中的旧 DbContext / 页面数据不会自动换）。
    /// 失败返回 (false, 原因)，当前数据保持不变。
    /// </summary>
    public static async Task<(bool Ok, string Message)> ImportAsync(
        string zipPath, CancellationToken ct = default)
    {
        if (!File.Exists(zipPath))
            return (false, "备份文件不存在");

        try
        {
            using var zip = ZipFile.OpenRead(zipPath);

            var manifestEntry = zip.GetEntry(ManifestEntry);
            if (manifestEntry is null)
                return (false, "不是 CreatFlow 备份文件（缺少 manifest）");

            BackupManifest? manifest;
            await using (var ms = manifestEntry.Open())
                manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(ms, JsonOptions, ct);

            if (manifest is null || manifest.Format != Format)
                return (false, "不是 CreatFlow 备份文件（格式不符）");
            if (manifest.Version > FormatVersion)
                return (false, $"备份来自更高版本（v{manifest.Version}），请升级软件后再导入");

            var dbEntry = zip.GetEntry(DbEntry);
            if (dbEntry is null)
                return (false, "备份文件缺少数据库（creatflow.db）");

            var tempDir = Path.Combine(Path.GetTempPath(), "cf-import-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(tempDir);
            try
            {
                var tempDb = Path.Combine(tempDir, DbEntry);
                dbEntry.ExtractToFile(tempDb, overwrite: true);

                // 先验证快照能打开（损坏的库绝不允许覆盖当前数据）
                using (var probe = LocalStoreInitializer.CreateContext(tempDb))
                {
                    if (probe.Platform.Count() == 0)
                        return (false, "备份数据库无法读取或为空，已取消导入");
                }

                var dbPath = LocalStorePaths.DatabasePath;
                Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

                // 当前库先留一份（导入后想反悔还能找回）
                if (File.Exists(dbPath))
                {
                    try
                    {
                        using var current = LocalStoreInitializer.CreateContext(dbPath);
                        LocalStoreInitializer.Backup(current, "pre-import");
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "[Backup] 导入前备份当前库失败（继续导入）");
                    }
                }

                LocalStoreInitializer.ReplaceDatabase(dbPath, tempDb);

                // 会员级 JSON 只恢复顶层 *.json（zip 里的路径前缀固定；防目录穿越）
                var prefix = $"accounts/{LocalIdentity.MemberId:N}/";
                var restored = 0;
                foreach (var entry in zip.Entries)
                {
                    if (!entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var name = entry.Name;
                    if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!string.Equals(entry.FullName, prefix + name, StringComparison.OrdinalIgnoreCase))
                        continue; // 子目录不收（manifest 之外的捷径一律忽略）

                    var target = Path.Combine(LocalStorePaths.MemberDir, name);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                    restored++;
                }

                return (true, $"已导入 {restored} 个本地档与数据库，请立即重启软件生效");
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Backup] 导入失败：{Zip}", zipPath);
            return (false, "导入失败：" + ex.Message);
        }
    }
}
