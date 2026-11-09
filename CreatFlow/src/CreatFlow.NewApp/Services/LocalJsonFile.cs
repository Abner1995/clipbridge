namespace CreatFlow.NewApp.Services;

/// <summary>
/// 客户端本地 JSON 状态的读写底座（发布预设 / 未提交草稿 / 账号顺序共用）。
///
/// 抽出来的原因不是"少写几行"，而是这两条铁律**每个文件都必须遵守** ——
/// 各自抄一份的下场是修了一个忘了另一个：
///  · **读失败一律按空处理**：缓存坏了不该让页面打不开；
///  · **写必须先落临时文件再替换**：直接覆写时崩溃会留下半截 JSON，
///    下次启动读不出来，用户的预设/顺序就凭空消失了。
/// </summary>
internal sealed class LocalJsonFile(string root, string tag)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        // 值类型枚举走字符串：比裸 int 可读，且加枚举项时不会错位
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>本地状态根目录：%LocalAppData%\CreatFlow</summary>
    internal static string DefaultRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CreatFlow");

    internal string PathOf(string fileName) => Path.Combine(root, fileName);

    /// <summary>会员级相对路径：accounts/{memberId:N}|no-member/{fileName}（与 AccountGroupStore.MemberPath 同一约定）</summary>
    internal static string MemberRelative(Guid memberId, string fileName) => Path.Combine(
        "accounts", memberId == Guid.Empty ? "no-member" : memberId.ToString("N"), fileName);

    /// <summary>
    /// 读会员档；**不存在**时一次性采纳旧机器级档（2026-10-15 六档会员级化，与
    /// AccountGroupStore.AdoptLegacyAsync 语义一致 —— 改一处要同步）：
    ///  · 会员档存在（哪怕损坏）→ 只读它，读失败按空 —— 绝不用旧档覆盖（防坏档被旧档洗掉）；
    ///  · 未登录（Guid.Empty）→ 不采纳（旧档原样留，懒到下次登录）；
    ///  · 旧档不存在/损坏 → 返回 null；
    ///  · 旧档非空（hasContent）→ 写会员档，确认落盘后删旧档；
    ///  · 旧档为空 → 只删旧档（采纳无意义），不写会员档。
    /// </summary>
    internal async Task<T?> ReadMemberOrAdoptAsync<T>(
        string memberPath, string legacyPath, Guid memberId, Func<T, bool> hasContent,
        string tag, CancellationToken ct) where T : class
    {
        if (File.Exists(memberPath))
            return await ReadAsync<T>(memberPath, ct);

        if (memberId == Guid.Empty) return null;

        var legacy = await ReadAsync<T>(legacyPath, ct);
        if (legacy is null) return null;

        if (!hasContent(legacy))
        {
            TryDelete(legacyPath);   // 空旧档：采纳无意义，顺手清掉（免得每次加载都再读一遍）
            return null;
        }

        await WriteAsync(memberPath, legacy, ct);
        if (File.Exists(memberPath))
        {
            TryDelete(legacyPath);
            Log.Information("[{Tag}] 旧机器级存档已采纳到会员 {Member}", tag, memberId);
        }
        return legacy;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* 删不掉留着：会员档已存在/为空，不再走采纳路径 */ }
    }

    internal async Task<T?> ReadAsync<T>(string path, CancellationToken ct) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = await File.ReadAllTextAsync(path, ct);
            if (string.IsNullOrWhiteSpace(json)) return null;
            return JsonSerializer.Deserialize<T>(json, JsonOpts);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "[{Tag}] 读取 {File} 失败，按空处理", tag, Path.GetFileName(path));
            return null;
        }
    }

    internal async Task WriteAsync<T>(string path, T value, CancellationToken ct)
    {
        await TryWriteAsync(path, value, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 与 <see cref="WriteAsync"/> 同一条写路径，但把成败告诉调用方（账号页水印卡的
    /// 「已保存 / 保存失败」提示用）。取消照旧往外抛；其余异常记日志并返回 false ——
    /// 其他存档调用点继续用 <see cref="WriteAsync"/> 的无返回值语义。
    /// </summary>
    internal async Task<bool> TryWriteAsync<T>(string path, T value, CancellationToken ct)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (dir is not null) Directory.CreateDirectory(dir);

            var tmp = path + ".tmp";
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(value, JsonOpts), ct);
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "[{Tag}] 写入 {File} 失败", tag, Path.GetFileName(path));
            return false;
        }
    }
}
