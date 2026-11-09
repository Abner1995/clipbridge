namespace CreatFlow.Media.Llm;

/// <summary>
/// 本地 LLM 模型档位（AI 设置页可选；4B 随包捆绑、8B 应用内下载）。
/// GGUF 量化统一 Q4_K_M —— 体积/质量平衡点，CPU 推理可用。
/// </summary>
public sealed record LlmModelTier(
    string Key,            // 稳定标识（配置持久化用，勿改）
    string DisplayName,    // 设置页展示
    string FileName,
    long SizeBytes,        // 下载完成校验基准（±5% 容差）
    int MinRamGb,          // 建议内存（仅提示文案用，不做硬门槛）
    string DownloadUrl,    // ModelScope 官方仓库（国内直连 ~10MB/s）
    string MirrorUrl)      // hf-mirror 兜底（HF 直连国内不通）
{
    public double SizeGb => Math.Round(SizeBytes / 1e9, 2);
}

/// <summary>
/// 本地 LLM 模型管理：档位清单、路径解析、下载（断点续传 + 多源回退）。
///
/// **解析顺序与 Kokoro 相反**（有意为之）：用户选定档位（LocalAppData）→ 捆绑默认（安装目录）。
/// Kokoro 是单一固定模型、捆绑优先；LLM 是多档位——用户在设置页下载的 8B 必须盖过捆绑的 4B，
/// 否则"换大档"永远不生效。捆绑目录（Program Files 只读）永不作下载目标。
/// </summary>
public static class LlmModelStore
{
    /// <summary>捆绑目录：安装目录 runtimes/llm/models/（随 zip/安装器分发，离线可用）；
    /// runtimes 拼写原因见 LlamaServerHost.EngineDir（预览器会崩，勿改回 runtime）</summary>
    public static string BundledDir => Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "runtimes", "llm", "models");

    /// <summary>下载目录：%LocalAppData%/CreatFlow/models/llm/（应用内下载落地，优先于捆绑）</summary>
    public static string LocalDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CreatFlow", "models", "llm");

    public static IReadOnlyList<LlmModelTier> Tiers { get; } =
    [
        new("qwen3-4b", "Qwen3-4B（推荐，随包附带）", "Qwen3-4B-Q4_K_M.gguf", 2_497_280_256, 8,
            "https://modelscope.cn/models/Qwen/Qwen3-4B-GGUF/resolve/master/Qwen3-4B-Q4_K_M.gguf",
            "https://hf-mirror.com/Qwen/Qwen3-4B-GGUF/resolve/main/Qwen3-4B-Q4_K_M.gguf"),
        new("qwen3-8b", "Qwen3-8B（质量更好，CPU 慢一倍）", "Qwen3-8B-Q4_K_M.gguf", 5_027_783_488, 16,
            "https://modelscope.cn/models/Qwen/Qwen3-8B-GGUF/resolve/master/Qwen3-8B-Q4_K_M.gguf",
            "https://hf-mirror.com/Qwen/Qwen3-8B-GGUF/resolve/main/Qwen3-8B-Q4_K_M.gguf"),
    ];

    /// <summary>默认档（捆绑的 4B；用户未选档/所选档不可用时用它）</summary>
    public static LlmModelTier Default => Tiers[0];

    public static LlmModelTier? Find(string? key) =>
        key == null ? null : Tiers.FirstOrDefault(t => t.Key == key);

    /// <summary>档位解析：LocalAppData（用户下载/手动放置）→ 捆绑目录；都没有 → null</summary>
    public static string? ResolvePath(LlmModelTier tier) => ResolvePath(tier, LocalDir, BundledDir);

    /// <summary>档位解析（目录可注入：--selftest 用临时目录验"用户档优先于捆绑档"）</summary>
    public static string? ResolvePath(LlmModelTier tier, string localDir, string bundledDir)
    {
        foreach (var dir in new[] { localDir, bundledDir })
        {
            var path = Path.Combine(dir, tier.FileName);
            if (IsComplete(path, tier)) return path;
        }
        return null;
    }

    public static bool IsReady(LlmModelTier tier) => ResolvePath(tier) != null;

    /// <summary>已就绪的档位（设置页列出/自动选择用）</summary>
    public static IReadOnlyList<LlmModelTier> ReadyTiers =>
        [.. Tiers.Where(IsReady)];

    /// <summary>
    /// 按偏好档位取可用模型路径：偏好档就绪 → 用它；否则回落到任一就绪档（默认档优先）；
    /// 全都没就绪 → null（调用方引导去设置页下载）。
    /// </summary>
    public static string? ResolveAnyPath(string? preferredKey) =>
        ResolveAnyPath(preferredKey, LocalDir, BundledDir);

    /// <summary>同上（目录可注入：--selftest 用）</summary>
    public static string? ResolveAnyPath(string? preferredKey, string localDir, string bundledDir) =>
        PickTier(Tiers, preferredKey, localDir, bundledDir) is { } tier
            ? ResolvePath(tier, localDir, bundledDir)
            : null;

    /// <summary>从档位表中挑可用档：偏好档就绪 → 用它；否则按表序回落；全不就绪 → null。纯函数，selftest 用</summary>
    public static LlmModelTier? PickTier(
        IReadOnlyList<LlmModelTier> tiers, string? preferredKey, string localDir, string bundledDir)
    {
        var preferred = preferredKey == null ? null : tiers.FirstOrDefault(t => t.Key == preferredKey);
        if (preferred != null && ResolvePath(preferred, localDir, bundledDir) != null) return preferred;
        return tiers.FirstOrDefault(t => ResolvePath(t, localDir, bundledDir) != null);
    }

    /// <summary>文件是否完整（存在且体积在预期 ±5% 内 —— 截断/半截文件不算就绪）</summary>
    private static bool IsComplete(string path, LlmModelTier tier)
    {
        if (!File.Exists(path)) return false;
        var len = new FileInfo(path).Length;
        return len >= tier.SizeBytes * 0.95 && len <= tier.SizeBytes * 1.05;
    }

    /// <summary>
    /// 下载档位到用户目录（断点续传 .part；ModelScope 优先，hf-mirror 兜底）。
    /// 进度 0-98 为下载、99 为校验、100 完成。取消时保留 .part，重跑续传。
    /// </summary>
    public static async Task DownloadAsync(LlmModelTier tier, Action<int>? onProgress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(LocalDir);
        var finalPath = Path.Combine(LocalDir, tier.FileName);
        var partPath = finalPath + ".part";

        // ── ① 下载（多源 + 断点续传） ──
        Exception? last = null;
        if (!IsComplete(partPath, tier))
        {
            foreach (var url in new[] { tier.DownloadUrl, tier.MirrorUrl })
            {
                try
                {
                    await DownloadFileAsync(url, partPath, ct,
                        written => onProgress?.Invoke((int)Math.Clamp(written * 100 / tier.SizeBytes, 0, 98)))
                        .ConfigureAwait(false);
                    last = null;
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    last = ex;
                    Log.Warning("[LlmModel] 下载失败 {Url}: {Msg}", url, ex.Message);
                }
            }
        }
        if (last != null) throw new IOException($"模型下载失败：{last.Message}");
        if (!IsComplete(partPath, tier))
            throw new IOException($"模型下载不完整（{new FileInfo(partPath).Length}/{tier.SizeBytes} 字节），重试可续传");

        // ── ② 落位 + 校验 ──
        onProgress?.Invoke(99);
        File.Move(partPath, finalPath, overwrite: true);
        if (!IsComplete(finalPath, tier))
            throw new IOException("模型校验失败（体积不符）");
        onProgress?.Invoke(100);
    }

    private static async Task DownloadFileAsync(string url, string partPath, CancellationToken ct, Action<long> onProgress)
    {
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromMinutes(30) };
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        var resumeFrom = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
        if (resumeFrom > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(resumeFrom, null);

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        // 服务器不支持续传（200 而非 206）→ 从头来过，避免拼接出坏文件
        if (resumeFrom > 0 && resp.StatusCode != System.Net.HttpStatusCode.PartialContent) resumeFrom = 0;
        if (!resp.IsSuccessStatusCode && resp.StatusCode != System.Net.HttpStatusCode.PartialContent)
            throw new IOException($"HTTP {(int)resp.StatusCode}");
        var total = resumeFrom + (resp.Content.Headers.ContentLength ?? 0);

        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = new FileStream(partPath, resumeFrom > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None);
        var buf = new byte[256 * 1024];
        long written = resumeFrom;
        int n;
        while ((n = await src.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
            written += n;
            onProgress?.Invoke(written);
        }
        if (total > 0 && written < total)
            throw new IOException($"连接中断（{written}/{total} 字节），重试可续传");
    }
}
