using System.Diagnostics;
using System.Globalization;

namespace CreatFlow.Media;

/// <summary>
/// FFmpeg 进程帮助 — 定位绿色版 ffmpeg/ffprobe 并执行命令（TTS 转码等轻量场景用）。
/// 路径优先 runtime/ffmpeg/（scripts/download_ffmpeg.ps1 获取，不进 git），PATH 兜底。
/// </summary>
public static class FfmpegCli
{
    private static readonly string? BundledDir = FindBundledDir();

    public static string FfmpegExe => BundledDir != null
        ? Path.Combine(BundledDir, "ffmpeg.exe")
        : "ffmpeg";

    /// <summary>ffprobe 可执行文件（探测元数据用 —— 输出 JSON 比解析 ffmpeg stderr 可靠）。</summary>
    public static string FfprobeExe => BundledDir != null
        ? Path.Combine(BundledDir, "ffprobe.exe")
        : "ffprobe";

    /// <summary>绿色版 ffmpeg 目录是否就位 —— 缺失时上面两个 Exe 会退化成 PATH 查找。</summary>
    public static bool IsBundled => BundledDir != null;

    private static string? FindBundledDir()
    {
        var candidate = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runtime", "ffmpeg");
        return File.Exists(Path.Combine(candidate, "ffmpeg.exe")) ? candidate : null;
    }

    /// <summary>跑 ffmpeg 普通命令（非编码长任务），返回 (stdout, stderr, exitCode)。</summary>
    public static Task<(string Stdout, string Stderr, int ExitCode)> RunAsync(
        IEnumerable<string> args, CancellationToken ct = default) =>
        RunExeAsync(FfmpegExe, args, ct);

    /// <summary>跑 ffprobe 普通命令（<c>-print_format json</c> 之类），返回 (stdout, stderr, exitCode)。</summary>
    public static Task<(string Stdout, string Stderr, int ExitCode)> RunProbeAsync(
        IEnumerable<string> args, CancellationToken ct = default) =>
        RunExeAsync(FfprobeExe, args, ct);

    /// <summary>以指定可执行文件跑一条命令。</summary>
    private static async Task<(string Stdout, string Stderr, int ExitCode)> RunExeAsync(
        string exe, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        if (!proc.Start()) throw new IOException($"无法启动进程: {exe}");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        using var killReg = ct.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } });
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        return (await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false), proc.ExitCode);
    }

    /// <summary>WAV → MP3 转码（本地 TTS 输出 wav 后压缩用）。返回 mp3 路径。</summary>
    public static async Task<string> WavToMp3Async(string wavPath, string mp3Path, CancellationToken ct = default)
    {
        var (_, stderr, code) = await RunAsync(new[]
        {
            "-hide_banner", "-y", "-i", wavPath,
            "-c:a", "libmp3lame", "-b:a", "128k", "-ar", "44100",
            mp3Path,
        }, ct).ConfigureAwait(false);
        if (code != 0 || !File.Exists(mp3Path))
            throw new IOException("MP3 转码失败: " + stderr[..Math.Min(stderr.Length, 300)]);
        return mp3Path;
    }

    /// <summary>InvariantCulture 数字格式化（滤镜/滤镜参数拼接用）</summary>
    public static string Fmt(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
