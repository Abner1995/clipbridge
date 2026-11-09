using NAudio.Wave;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 试听播放器（配音页 / 合成页共用）：MemoryStream + Mp3FileReader + WaveOutEvent 的薄封装。
/// 实例字段持有引用防 GC 中途回收（老配音页教训）；播放是异步的 —— Play 返回后音频继续放，
/// 播完（或被打断）经 <c>onFinished</c> 回调，回调已切回 UI 线程。
/// </summary>
public sealed class Mp3PreviewPlayer
{
    private WaveOutEvent? _waveOut;
    private Mp3FileReader? _reader;
    private MemoryStream? _stream;

    /// <summary>是否正在播（试听状态收尾判断用）</summary>
    public bool IsPlaying => _waveOut is not null;

    /// <summary>播放一段 mp3；先停掉上一段。播完自动释放并回调（停止/换段时旧实例的停止事件不回调）</summary>
    public void Play(byte[] mp3, Action onFinished)
    {
        Stop();
        _stream = new MemoryStream(mp3);
        _reader = new Mp3FileReader(_stream);
        var waveOut = new WaveOutEvent();
        _waveOut = waveOut;
        waveOut.Init(_reader);
        waveOut.PlaybackStopped += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            // 播完 / 被打断：只有还是当前实例时才收状态（旧的停止事件不能踩新的播放）
            if (ReferenceEquals(_waveOut, waveOut))
            {
                Stop();
                onFinished();
            }
            else
            {
                try { waveOut.Dispose(); } catch { /* 已释放 */ }
            }
        });
        waveOut.Play();
    }

    /// <summary>停掉当前播放并释放实例（不回调 onFinished —— 状态由调用方收）</summary>
    public void Stop()
    {
        var waveOut = _waveOut;
        _waveOut = null;
        if (waveOut != null)
        {
            try { waveOut.Stop(); } catch { /* 设备可能已失效 */ }
            try { waveOut.Dispose(); } catch { }
        }
        try { _reader?.Dispose(); } catch { }
        _reader = null;
        try { _stream?.Dispose(); } catch { }
        _stream = null;
    }
}
