namespace CreatFlow.Media.Tts;

/// <summary>
/// Kokoro 本地 TTS 模型管理：路径、就绪检测、下载（断点续传 + 镜像回退 + 解压）。
/// 模型：kokoro-int8-multi-lang-v1_1（~300MB，含中文音色，tts-models release 官方打包）。
///
/// 分发策略（2026-09-01 定）：**随包捆绑优先** —— 安装包/zip 带 runtime/kokoro/（网盘分发），
/// 应用内下载仅作兜底（老包用户 / 模型误删）。下载源：GitHub 直连（国内不稳）→ gh-proxy 镜像兜底。
/// </summary>
public static class KokoroModels
{
    /// <summary>捆绑模型目录：安装目录 runtime/kokoro/（随 zip/安装器分发，离线可用）</summary>
    public static string BundledModelDir => Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "runtime", "kokoro");

    /// <summary>下载目录：%LocalAppData%/CreatFlow/models/kokoro-int8-multi-lang-v1_1/（应用内下载落地）</summary>
    public static string LocalModelDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CreatFlow", "models", "kokoro-int8-multi-lang-v1_1");

    /// <summary>捆绑模型是否完整（3 个关键文件齐全即视为可用，与 IsReady 同一标准）</summary>
    public static bool IsBundled =>
        File.Exists(Path.Combine(BundledModelDir, "model.int8.onnx")) &&
        File.Exists(Path.Combine(BundledModelDir, "voices.bin")) &&
        File.Exists(Path.Combine(BundledModelDir, "tokens.txt"));

    /// <summary>读取用模型目录：捆绑完整 → 用捆绑；否则 → 用户目录（下载/就绪检测都走它）</summary>
    public static string ModelDir => IsBundled ? BundledModelDir : LocalModelDir;

    // ⚠️ v1.1-zh 结构：模型名 model.int8.onnx；词典/拼音数据在模型目录根，无 data/ 子目录
    public static string KokoroOnnx => Path.Combine(ModelDir, "model.int8.onnx");
    public static string VoicesBin => Path.Combine(ModelDir, "voices.bin");
    public static string TokensTxt => Path.Combine(ModelDir, "tokens.txt");
    public static string LexiconZh => Path.Combine(ModelDir, "lexicon-zh.txt");
    /// <summary>DataDir：sherpa-onnx 校验要求根目录有 phontab/phonindex（v1.1 打包在 espeak-ng-data/ 下，
    /// 下载解压后需复制到根目录 —— 见 EnsureDataDir）</summary>
    public static string DataDir => ModelDir;

    /// <summary>espeak-ng 数据根：v1.1-zh 打包在 espeak-ng-data/ 子目录（官方用法 --kokoro-data-dir 指它）；
    /// 老解压布局（数据直接在模型根）回落根目录。</summary>
    public static string EspeakDataDir
    {
        get
        {
            var nested = Path.Combine(ModelDir, "espeak-ng-data");
            return Directory.Exists(nested) ? nested : ModelDir;
        }
    }

    /// <summary>中英混合词典（官方用法：lexicon-us-en.txt + lexicon-zh.txt，逗号分隔；
    /// 只有中文词典的存量模型回落单文件）</summary>
    public static string LexiconForKokoro
    {
        get
        {
            var en = Path.Combine(ModelDir, "lexicon-us-en.txt");
            return File.Exists(en)
                ? string.Join(",", en, Path.Combine(ModelDir, "lexicon-zh.txt"))
                : Path.Combine(ModelDir, "lexicon-zh.txt");
        }
    }

    /// <summary>
    /// 中文数字 / 日期归一化规则（tts-rule-fsts，官方用法 date-zh.fst + number-zh.fst，逗号分隔、按序应用）：
    /// 把「52000」「5.2」「2026年」这类按中文读出来。没有这层，阿拉伯数字会走 espeak 兜底 ——
    /// 实测 espeak-ng voice 'zh' 设置失败 → 数字片段被整个丢掉（纯数字文案甚至直接 NRE）。
    /// </summary>
    public static string? ZhRuleFsts
    {
        get
        {
            var files = new[] { "date-zh.fst", "number-zh.fst" }
                .Select(f => Path.Combine(ModelDir, f))
                .Where(File.Exists)
                .ToList();
            return files.Count == 0 ? null : string.Join(",", files);
        }
    }

    /// <summary>模型是否就绪（3 个关键文件齐全，词典目录在模型根）</summary>
    public static bool IsReady =>
        File.Exists(KokoroOnnx) && File.Exists(VoicesBin) && File.Exists(TokensTxt);

    private const string FileName = "kokoro-int8-multi-lang-v1_1.tar.bz2";
    private static readonly string[] MirrorUrls =
    [
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/" + FileName, // 直连（有系统代理时最快）
        "https://gh-proxy.com/https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/" + FileName, // 无代理兜底（慢但可用）
    ];

    /// <summary>
    /// 创建下载客户端：检测 Windows 系统代理（Clash 等，ProxyEnable=1 → ProxyServer）。
    /// 实测：GitHub 直连被重置、gh-proxy 镜像仅 14KB/s，走系统代理直连才快。
    /// </summary>
    private static HttpClient CreateDownloadClient()
    {
        var handler = new HttpClientHandler { UseProxy = false };
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (key?.GetValue("ProxyEnable") is int enabled && enabled == 1 &&
                key.GetValue("ProxyServer") is string proxy && !string.IsNullOrEmpty(proxy))
            {
                handler.Proxy = new System.Net.WebProxy("http://" + proxy);
                handler.UseProxy = true;
            }
        }
        catch (Exception ex)
        {
            Log.Warning("[Kokoro] 系统代理读取失败（走直连）: {Msg}", ex.Message);
        }
        return new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
    }

    /// <summary>
    /// 下载 + 解压模型到用户目录（LocalModelDir）。断点续传（Range + .part 文件）；解压用 Windows 自带 tar（bsdtar 支持 bz2）。
    /// 注意：捆绑目录（Program Files 只读）永不作下载目标 —— 下载只发生在无捆绑或捆绑不完整时。
    /// </summary>
    public static async Task DownloadAsync(Action<int>? onProgress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(LocalModelDir);
        var partPath = Path.Combine(LocalModelDir, FileName + ".part");

        // ── ① 下载（镜像优先，断点续传） ──
        var localSize = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
        Exception? last = null;
        foreach (var url in MirrorUrls)
        {
            try
            {
                await DownloadFileAsync(url, partPath, localSize, onProgress, ct).ConfigureAwait(false);
                last = null;
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                Log.Warning("[Kokoro] 下载失败 {Url}: {Msg}", url, ex.Message);
            }
        }
        if (last != null) throw new IOException($"模型下载失败：{last.Message}");

        // ── ② 解压（tar -xjf，Windows 自带 bsdtar 支持 bz2） ──
        var bz2Path = Path.Combine(LocalModelDir, FileName);
        File.Move(partPath, bz2Path, overwrite: true);
        onProgress?.Invoke(99);
        var (_, stderr, code) = await RunTarAsync(LocalModelDir, bz2Path, ct).ConfigureAwait(false);
        if (code != 0)
            throw new IOException("模型解压失败: " + stderr[..Math.Min(stderr.Length, 300)]);
        try { File.Delete(bz2Path); } catch { }

        // ③ 布局修复：sherpa-onnx 校验要求 DataDir 根有 phontab/phonindex，
        //    但 v1.1-zh 打包在 espeak-ng-data/ 子目录（实测缺失直接报错）→ 复制到根目录
        var esData = Path.Combine(LocalModelDir, "espeak-ng-data");
        if (Directory.Exists(esData))
            foreach (var f in Directory.GetFiles(esData))
            {
                var target = Path.Combine(LocalModelDir, Path.GetFileName(f));
                if (!File.Exists(target)) File.Copy(f, target);
            }

        if (!IsReady)
            throw new IOException("模型解压后校验失败（缺少关键文件）");
        onProgress?.Invoke(100);
    }

    private static async Task DownloadFileAsync(
        string url, string partPath, long resumeFrom, Action<int>? onProgress, CancellationToken ct)
    {
        using var http = CreateDownloadClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (resumeFrom > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(resumeFrom, null);

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode && resp.StatusCode != System.Net.HttpStatusCode.PartialContent)
            throw new IOException($"HTTP {(int)resp.StatusCode}");
        var total = resumeFrom + (resp.Content.Headers.ContentLength ?? 0);

        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = new FileStream(partPath, FileMode.Append, FileAccess.Write, FileShare.None);
        var buf = new byte[256 * 1024];
        long written = resumeFrom;
        int n;
        while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
            written += n;
            if (total > 0) onProgress?.Invoke((int)Math.Clamp(written * 100 / total, 0, 98));
        }
        if (resp.StatusCode != System.Net.HttpStatusCode.PartialContent && written == 0)
            throw new IOException("下载内容为空");
    }

    private static async Task<(string Stdout, string Stderr, int ExitCode)> RunTarAsync(
        string workDir, string bz2Path, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "tar",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workDir,
        };
        psi.ArgumentList.Add("-xjf");
        psi.ArgumentList.Add(bz2Path);
        using var proc = new System.Diagnostics.Process { StartInfo = psi };
        if (!proc.Start()) throw new IOException("无法启动 tar（Windows 10 1803+ 自带）");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        using var killReg = ct.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } });
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        return (await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false), proc.ExitCode);
    }
}
