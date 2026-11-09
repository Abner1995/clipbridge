namespace CreatFlow.Media.Tts;

/// <summary>
/// TTS 缓存装饰器 — 对真实 Provider 透明包装：同一（引擎+文本+音色+语速+音调）命中缓存直接返回。
/// 降低 Edge 免费端点调用频率（限流风险的核心诱因）；Kokoro 本地也享受（省推理时间）。
/// </summary>
public sealed class CachingTtsProvider : ITtsProvider
{
    private readonly ITtsProvider _inner;

    public CachingTtsProvider(ITtsProvider inner) => _inner = inner;

    public string Name => _inner.Name;
    public IReadOnlyList<TtsVoiceInfo> Voices => _inner.Voices;

    public async Task<TtsResult> SynthesizeAsync(
        TtsRequest request, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        var cached = TtsCache.Get(_inner, request);
        if (cached != null)
        {
            Log.Debug("[TtsCache] 命中 {Engine}: {Text}", _inner.Name, Truncate(request.Text, 40));
            onProgress?.Invoke("命中缓存，直接返回");
            return TtsCache.Load(cached);
        }
        var result = await _inner.SynthesizeAsync(request, onProgress, ct).ConfigureAwait(false);
        TtsCache.Put(_inner, request, result);
        return result;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
