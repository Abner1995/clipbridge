using Avalonia.Platform;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 发布结果提示音 —— 自老 App 移植（<c>CreatFlow.App/Services/PublishSoundNotifier.cs</c>）。
///
/// 声音是 Avalonia 嵌入资源（<c>Assets/Sounds/*.mp3</c>，随 DLL 走，无路径依赖，混淆/打包都不受影响）。
/// 播放用 NAudio 的 Mp3FileReader + WaveOutEvent：线程安全、不需要窗口句柄，后台线程可直接调用。
///
/// 触发规则（调用方 <c>PublishPipeline</c> 负责）：
///   失败 → 立即响（用户要行动，不等其它平台）
///   成功 → 一条视频的所有平台都完成后响一次（避免 4 个平台连响 4 声）
///
/// 之前在 NewApp 里**根本没人接管线上的 <c>OnNotify</c>** —— 管线该响的时候喊了，
/// 没人听，所以「发布成功/失败都没声音」（2026-09-20 用户实测反馈）。
///
/// 2026-10-22 起音量可调（账号设置页「上传提示音」卡，滑块 0~100%）：<see cref="VolumePercent"/> 由
/// <see cref="SoundSettingsStore"/> 在登录进壳时与改档时灌入，改一次立即生效。
/// </summary>
public static class PublishSoundNotifier
{
    /// <summary>
    /// 当前音量百分比（0~100）。**静态可写**：上传是在后台消费者里跑的，拿不到页面 VM 的设置实例 ——
    /// 由 App 登录进壳时读会员档灌入（见 <c>App.LoadSoundSettingsAsync</c>），账号设置页拖滑块时同步更新。
    /// </summary>
    public static int VolumePercent { get; set; } = SoundSettingsStore.DefaultVolumePercent;

    /// <summary>是否出声（0 = 静音；appsettings 的 PublishSounds=false 仍在管线出口另有一道硬闸）</summary>
    public static bool Enabled => VolumePercent > 0;

    /// <summary>百分比 → 线性增益（0~1）。试听与实际上传共用，保证听到的就是上传时的音量。</summary>
    public static float GainOf(int volumePercent)
        => Math.Clamp(volumePercent, 0, SoundSettingsStore.MaxVolumePercent) / 100f;

    /// <summary>发布成功提示音（欢快铃声）</summary>
    public static void PlaySuccess() => Play("publish_success.mp3");

    /// <summary>发布失败提示音（失败音）</summary>
    public static void PlayFailure() => Play("publish_failure.mp3");

    /// <summary>管线通知回调的统一入口：true=成功音，false=失败音</summary>
    public static void Notify(bool success)
    {
        if (success) PlaySuccess();
        else PlayFailure();
    }

    private static void Play(string fileName)
    {
        if (!Enabled) return;

        try
        {
            // 嵌入资源流可能不支持 Mp3FileReader 要求的 Seek，先整块拷进 MemoryStream
            var raw = AssetLoader.Open(
                new Uri($"avares://CreatFlow.NewApp/Assets/Sounds/{fileName}"));
            var ms = new MemoryStream();
            raw.CopyTo(ms);
            ms.Position = 0;

            // 音量用 VolumeSampleProvider 做（不依赖声卡混音器是否支持每路音量，
            // WaveOutEvent.Volume 在部分设备上会直接抛）。满档时不套包装，行为与老版一致。
            var reader = new Mp3FileReader(ms);
            var gain = GainOf(VolumePercent);
            IWaveProvider source = reader;
            if (gain < 1f)
            {
                source = new VolumeSampleProvider(reader.ToSampleProvider()) { Volume = gain }
                    .ToWaveProvider();
            }

            // 每次播放一个独立实例，播完自毁；PlaybackStopped 里释放，
            // 防止 GC 在播放中途回收 reader/waveOut 导致崩溃
            var waveOut = new WaveOutEvent();
            waveOut.Init(source);
            waveOut.PlaybackStopped += (_, _) =>
            {
                try { waveOut.Dispose(); } catch { }
                try { reader.Dispose(); } catch { }
                try { ms.Dispose(); } catch { }
            };
            waveOut.Play();
        }
        catch (Exception ex)
        {
            // 提示音失败不影响发布流程（例如机器没有音频设备）
            Log.Warning(ex, "Publish sound play failed: {File}", fileName);
        }
    }
}
