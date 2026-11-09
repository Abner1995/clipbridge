namespace CreatFlow.Media.Tts;

/// <summary>
/// Edge TTS Provider — 逆向自微软 Edge「大声朗读」免费接口（见 EdgeTtsService）。
/// 效果最好付费替代：Azure Speech（同源音色，官方 SDK，见 memory: edge-tts-reverse-engineered-api）。
/// </summary>
public sealed class EdgeTtsProvider : ITtsProvider
{
    public string Name => "Edge 免费";

    public async Task<TtsResult> SynthesizeAsync(
        TtsRequest request,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        if (request.IncludeSubtitles)
        {
            var (audio, subs) = await EdgeTtsService.SynthesizeWithSubtitlesAsync(
                request.Text, request.Voice, request.Style, request.Speed, request.Pitch, onProgress, ct)
                .ConfigureAwait(false);
            return new TtsResult(audio, subs);
        }
        var plain = await EdgeTtsService.SynthesizeAsync(
            request.Text, request.Voice, request.Style, request.Speed, request.Pitch, onProgress, ct)
            .ConfigureAwait(false);
        return new TtsResult(plain, []);
    }

    /// <summary>应用退出时关闭 WebSocket 连接（由 App 退出清理调用）。</summary>
    public static void Shutdown() => EdgeTtsService.Shutdown();
}
