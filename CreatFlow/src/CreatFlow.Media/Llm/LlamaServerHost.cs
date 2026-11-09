using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace CreatFlow.Media.Llm;

/// <summary>
/// 内置 llama-server 子进程托管 —— 零安装本地 LLM 的运行时（产品决策 2026-10-10：不再要求用户装 Ollama）。
///
/// llama-server 自带 OpenAI 兼容端点（<c>http://127.0.0.1:&lt;端口&gt;/v1</c>），
/// 与云端 DeepSeek/Qwen、用户自装 Ollama 走同一条 <see cref="OpenAiCompatibleLlmClient"/> 路径。
///
/// 生命周期（懒启动 + 热态保持）：
///  · 首次 <see cref="EnsureRunningAsync"/> 才拉起（模型加载数秒~十几秒）；
///  · 用完后热态保持 <see cref="IdleTimeout"/>（默认 10 分钟）无请求 → 自动卸载释放内存；
///  · 换模型 = 停旧起新；应用退出（Dispose / ProcessExit）杀进程；
///  · 硬崩残留的孤儿进程在下次启动时回收（仅在确认没有第二个应用实例时 —— 双开会误杀对方的 server）。
///
/// 单次生成最长耗时远小于空闲阈值（4B CPU 最慢档一条文案约 1~3 分钟），故无需"生成中"租约。
/// </summary>
public sealed class LlamaServerHost : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// 引擎目录：安装目录 runtimes/llm/（scripts/download_llama.ps1 获取；仓库源目录是 runtime/llm，勿混）。
    /// ⚠️ 输出路径刻意用 runtimes（不是 runtime）：VS 的 Avalonia 预览器会把输出目录下所有未知 .dll
    /// 当托管程序集预加载，llama 原生 dll 会 GGML abort 崩掉整个预览器进程；runtimes\ 子树被跳过。
    /// 详见 CreatFlow.NewApp.csproj 的 llm 内容块注释。
    /// </summary>
    public static string EngineDir => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runtimes", "llm");

    public static string EngineExe => Path.Combine(EngineDir, "llama-server.exe");

    /// <summary>引擎是否就位（设置页/调用方据此给"未下载"引导）</summary>
    public static bool IsEngineReady => File.Exists(EngineExe);

    /// <summary>空闲卸载阈值（热态保持时长）</summary>
    public TimeSpan IdleTimeout { get; }

    private readonly int _contextSize;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Timer _idleTimer;
    private readonly ConcurrentQueue<string> _recentOutput = new(); // 启动失败诊断用（最后若干行输出）
    private Process? _proc;
    private string? _modelPath;
    private string? _baseUrl;
    private long _lastUseTicks;
    private bool _disposed;

    public LlamaServerHost(TimeSpan? idleTimeout = null, int contextSize = 4096)
    {
        IdleTimeout = idleTimeout ?? TimeSpan.FromMinutes(10);
        _contextSize = contextSize;
        ReapOrphans();
        _idleTimer = new Timer(_ => CheckIdle(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        AppDomain.CurrentDomain.ProcessExit += (_, _) => KillProcessQuietly();
    }

    public bool IsRunning => _proc is { HasExited: false };

    /// <summary>当前已加载的模型路径（未运行为 null）</summary>
    public string? RunningModelPath => IsRunning ? _modelPath : null;

    /// <summary>OpenAI 兼容 baseUrl（如 http://127.0.0.1:52431/v1；未运行为 null）</summary>
    public string? BaseUrl => IsRunning ? _baseUrl : null;

    /// <summary>
    /// 确保 server 以指定模型运行，返回 baseUrl。已在跑同一模型 → 直接返回（并刷新热态计时）。
    /// 换模型 → 停旧起新。引擎/模型缺失 → 抛 <see cref="InvalidOperationException"/>（文案可直接给用户看）。
    /// </summary>
    public async Task<string> EnsureRunningAsync(string modelPath, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsRunning && _modelPath == modelPath && _baseUrl != null)
            {
                Touch();
                return _baseUrl;
            }

            await StopCoreAsync().ConfigureAwait(false);

            if (!File.Exists(EngineExe))
                throw new InvalidOperationException($"本地推理引擎未就绪：缺少 {EngineExe}（运行 scripts/download_llama.ps1 获取，打包版应随包分发）");
            if (!File.Exists(modelPath))
                throw new InvalidOperationException($"本地模型未就绪：缺少 {modelPath}（到「AI 设置」页下载）");

            var port = PickFreePort();
            _recentOutput.Clear();
            var psi = new ProcessStartInfo
            {
                FileName = EngineExe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = EngineDir,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            // --reasoning off：Qwen3 必须关思考（慢数倍且内容跑进 reasoning 字段）——同 DeepSeek thinking:disabled 一课
            // --jinja 此版默认开启；--no-webui 不需要内置网页控制台
            foreach (var a in new[]
            {
                "-m", modelPath, "--host", "127.0.0.1", "--port", port.ToString(),
                "-c", _contextSize.ToString(), "--reasoning", "off", "--no-webui",
            }) psi.ArgumentList.Add(a);

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) => AppendOutput(e.Data);
            proc.ErrorDataReceived += (_, e) => AppendOutput(e.Data);
            try
            {
                if (!proc.Start()) throw new IOException("llama-server 启动失败（Process.Start 返回 false）");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"本地推理引擎启动失败：{ex.Message}（引擎目录 {EngineDir}）", ex);
            }
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            _proc = proc;
            _modelPath = modelPath;

            try
            {
                var url = await WaitReadyAsync(proc, port, ct).ConfigureAwait(false);
                _baseUrl = url;
                Touch();
                Log.Information("[LlamaHost] 就绪 {Url} 模型 {Model}", url, Path.GetFileName(modelPath));
                return url;
            }
            catch
            {
                await StopCoreAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>停掉 server（下次 EnsureRunning 会重新加载）</summary>
    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _idleTimer.Dispose();
        await StopCoreAsync().ConfigureAwait(false);
    }

    /// <summary>同步释放（DI 容器同步 Dispose 时走这里；只杀进程不等优雅退出）</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _idleTimer.Dispose();
        KillProcessQuietly();
    }

    // ═══ 内部 ═══

    private void Touch() => Interlocked.Exchange(ref _lastUseTicks, DateTime.UtcNow.Ticks);

    private void CheckIdle()
    {
        if (_disposed || !IsRunning) return;
        var idle = DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastUseTicks), DateTimeKind.Utc);
        if (idle < IdleTimeout) return;
        Log.Information("[LlamaHost] 空闲 {Min:0.#} 分钟，卸载本地模型", idle.TotalMinutes);
        _ = StopAsync(); // 内部有 gate，失败只记日志
    }

    private async Task StopCoreAsync()
    {
        var proc = _proc;
        _proc = null;
        _baseUrl = null;
        _modelPath = null;
        if (proc == null) return;
        try
        {
            if (!proc.HasExited) proc.Kill(entireProcessTree: true);
            await proc.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning("[LlamaHost] 停止进程异常（忽略）: {Msg}", ex.Message);
        }
        finally
        {
            try { proc.Dispose(); } catch { }
        }
    }

    private void KillProcessQuietly()
    {
        try { if (_proc is { HasExited: false }) _proc.Kill(entireProcessTree: true); } catch { }
    }

    /// <summary>轮询 /health 直到 200（加载中返回 503）；进程提前退出/超时 → 抛带输出尾部的异常</summary>
    private async Task<string> WaitReadyAsync(Process proc, int port, CancellationToken ct)
    {
        var url = $"http://127.0.0.1:{port}";
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(180);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (proc.HasExited)
                throw new InvalidOperationException($"本地推理引擎提前退出（exit={proc.ExitCode}）：{OutputTail()}");
            try
            {
                using var resp = await http.GetAsync(url + "/health", ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode) return url + "/v1";
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // 未就绪/连接未起 —— 继续轮询
            }
            await Task.Delay(400, ct).ConfigureAwait(false);
        }
        throw new TimeoutException($"本地推理引擎 180 秒内未就绪：{OutputTail()}");
    }

    private void AppendOutput(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        _recentOutput.Enqueue(line);
        while (_recentOutput.Count > 40) _recentOutput.TryDequeue(out _);
        Log.Debug("[LlamaHost] {Line}", line);
    }

    private string OutputTail() => string.Join(" | ", _recentOutput.TakeLast(6));

    private static int PickFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// 回收上次硬崩残留的 llama-server（只杀镜像路径 = 本包引擎的进程）。
    /// ⚠️ 检测到第二个应用实例时不回收：两个实例共享同一 runtime 目录，杀"孤儿"会误杀对方的 server。
    /// </summary>
    private static void ReapOrphans()
    {
        try
        {
            var me = Process.GetCurrentProcess();
            var sameName = Process.GetProcessesByName(me.ProcessName);
            var otherInstance = sameName.Any(p => p.Id != me.Id);
            foreach (var p in sameName) p.Dispose();
            if (otherInstance)
            {
                Log.Debug("[LlamaHost] 检测到第二个应用实例，跳过孤儿回收");
                return;
            }

            foreach (var p in Process.GetProcessesByName("llama-server"))
            {
                try
                {
                    if (string.Equals(p.MainModule?.FileName, EngineExe, StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Warning("[LlamaHost] 回收残留的 llama-server 进程 pid={Pid}", p.Id);
                        p.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug("[LlamaHost] 检查进程 {Pid} 失败（忽略）: {Msg}", p.Id, ex.Message);
                }
                finally { p.Dispose(); }
            }
        }
        catch (Exception ex)
        {
            Log.Debug("[LlamaHost] 孤儿回收失败（忽略）: {Msg}", ex.Message);
        }
    }
}
