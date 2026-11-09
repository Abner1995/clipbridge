namespace CreatFlow.Media.Tts;

/// <summary>
/// Kokoro 本地 TTS Provider — sherpa-onnx（k2-fsa，Apache-2.0）离线推理，完全免费稳定。
/// 模型 kokoro-int8-multi-lang-v1_1（~300MB，含中文音色），离线合成无服务端故障。
/// 字幕（2026-10-11 重做）：无词级时间戳 → **逐句合成**（每句一次 Generate，按真实样本数记句时长），
/// 句级再交给 <see cref="SubtitleLineBuilder"/> 切 ≤16 字/行 —— 整段模式字幕按句滚动。
/// 旧行为是「整段一条字幕（0→音频全长）」，会被 ASS 端 16 字×3 行截断，只剩前一点且整片不动。
/// 音色 sid：multi-lang 模型的音色全局编号，中文音色映射由探针遍历确认（见 Voices 注释）。
/// </summary>
public sealed class KokoroTtsProvider : ITtsProvider
{
    public string Name => "Kokoro 本地";

    /// <summary>sherpa-onnx Kokoro 无音调参数（Generate 只收 speed/sid），App 据此禁用音调控件</summary>
    public bool SupportsPitch => false;

    /// <summary>
    /// 中文音色（kokoro multi-lang v1.1 官方顺序，sherpa-onnx PR #2303 确认）：
    /// zf_ = 中文女声（45-48），zm_ = 中文男声（49-52）。请求 Voice 传 id（如 "zf_xiaobei"）。
    /// </summary>
    public IReadOnlyList<TtsVoiceInfo> Voices { get; } =
    [
        new("zf_xiaobei", "晓蓓"),
        new("zf_xiaoni", "晓妮"),
        new("zf_xiaoxiao", "晓晓"),
        new("zf_xiaoyi", "晓依"),
        new("zm_yunjian", "云健", IsFemale: false),
        new("zm_yunxi", "云希", IsFemale: false),
        new("zm_yunxia", "云夏", IsFemale: false),
        new("zm_yunyang", "云扬", IsFemale: false),
    ];

    /// <summary>音色 id → 模型 sid（v1.1 固定映射）</summary>
    private static readonly Dictionary<string, int> SidMap = new()
    {
        ["zf_xiaobei"] = 45,
        ["zf_xiaoni"] = 46,
        ["zf_xiaoxiao"] = 47,
        ["zf_xiaoyi"] = 48,
        ["zm_yunjian"] = 49,
        ["zm_yunxi"] = 50,
        ["zm_yunxia"] = 51,
        ["zm_yunyang"] = 52,
    };

    private const int SampleRate = 24000; // kokoro 输出固定 24kHz

    /// <summary>
    /// 进程级单例：模型加载 ~3-5s（140MB），逐句重建会重复加载（分镜模式 N 句 = N 次加载）。
    /// sherpa-onnx OfflineTts.Generate 每次调用独立（无共享可变状态），串行调用线程安全。
    /// </summary>
    private static readonly Lazy<SherpaOnnx.OfflineTts> TtsLazy = new(() =>
    {
        var config = new SherpaOnnx.OfflineTtsConfig
        {
            Model = new SherpaOnnx.OfflineTtsModelConfig
            {
                Kokoro = new SherpaOnnx.OfflineTtsKokoroModelConfig
                {
                    Model = KokoroModels.KokoroOnnx,
                    Voices = KokoroModels.VoicesBin,
                    Tokens = KokoroModels.TokensTxt,
                    DataDir = KokoroModels.EspeakDataDir,
                    Lexicon = KokoroModels.LexiconForKokoro,
                    Lang = "zh",
                },
                Debug = 0,
                NumThreads = 2,
                Provider = "cpu",
            },
            MaxNumSentences = 1,
            // 中文数字/日期归一化（官方用法）：阿拉伯数字先转成中文再进模型 ——
            // 不挂这层，数字走 espeak 兜底会整段丢掉（见 KokoroModels.ZhRuleFsts 注释）
            RuleFsts = KokoroModels.ZhRuleFsts,
        };
        return new SherpaOnnx.OfflineTts(config);
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    public Task<TtsResult> SynthesizeAsync(
        TtsRequest request, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        if (!KokoroModels.IsReady)
            throw new IOException("Kokoro 本地模型未下载，请到「🤖 AI 设置」页下载（约 300MB）");
        if (!SidMap.TryGetValue(request.Voice, out var sid))
        {
            var all = string.Join(" / ", Voices.Select(v => v.Id));
            throw new ArgumentException($"Kokoro 未知音色: {request.Voice}（可用：{all}）", nameof(request));
        }

        return Task.Run(async () =>
        {
            var tts = TtsLazy.Value; // 首次加载 ~3-5s，后续复用
            var speed = (float)Math.Clamp(request.Speed, 0.5, 2.0);

            // 逐句合成（2026-10-11）：句级切分后每句一次 Generate，用**累计样本数**记每句的起止毫秒 ——
            // 句时长是真实值（不是估算），整段模式的字幕得以按句滚动；
            // 此前整段一次 Generate = 整片一条字幕，被 ASS 16 字×3 行截断（用户实测「只有一点、不滚动」）。
            var segments = SplitSentences(request.Text);
            var all = new List<float>();
            var sentences = new List<SubtitleItem>();
            var sampleRate = SampleRate;
            for (var i = 0; i < segments.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                onProgress?.Invoke(segments.Count > 1 ? $"正在合成语音 {i + 1}/{segments.Count}…" : "正在合成语音...");
                var audio = tts.Generate(SanitizeText(segments[i]), speed, sid);
                sampleRate = audio.SampleRate > 0 ? audio.SampleRate : SampleRate;
                var startMs = (int)(all.Count * 1000.0 / sampleRate);
                all.AddRange(audio.Samples);
                var endMs = (int)(all.Count * 1000.0 / sampleRate);
                sentences.Add(new SubtitleItem(segments[i], startMs, endMs)); // 显示文本用原文（保留「·」等写法）
            }

            // float[] → WAV（16-bit PCM 单声道）→ ffmpeg 转 mp3
            var wavPath = Path.Combine(Path.GetTempPath(), "CreatFlow", $"kokoro_{Guid.NewGuid():N}.wav");
            var mp3Path = Path.Combine(Path.GetTempPath(), "CreatFlow", $"kokoro_{Guid.NewGuid():N}.mp3");
            Directory.CreateDirectory(Path.GetDirectoryName(wavPath)!);
            try
            {
                WriteWav(wavPath, [.. all], sampleRate);
                onProgress?.Invoke("正在转码...");
                await FfmpegCli.WavToMp3Async(wavPath, mp3Path, ct).ConfigureAwait(false);

                var bytes = await File.ReadAllBytesAsync(mp3Path, ct).ConfigureAwait(false);
                // 字幕：句级 → ≤16 字/行（无词级时间戳，行内按字数比例分时 = SubtitleLineBuilder 的兜底路径）
                IReadOnlyList<SubtitleItem> subs = request.IncludeSubtitles && sentences.Count > 0
                    ? SubtitleLineBuilder.Build(sentences, [])
                    : [];
                return new TtsResult(bytes, subs);
            }
            finally
            {
                try { File.Delete(wavPath); } catch { }
                try { File.Delete(mp3Path); } catch { }
            }
        }, ct);
    }

    /// <summary>
    /// 句级切分（合成与字幕的最小单位）：句末标点后断开（标点随前句）、换行也是断点。
    /// 清洗后没有可读字符的碎片（「……」「。」这类单标点段）直接丢弃 —— 不合成也不出字幕；
    /// 全丢光时回退「整段一条」，与旧行为一致。纯函数，--selftest 直接断言。
    /// </summary>
    public static List<string> SplitSentences(string text)
    {
        var result = new List<string>();
        var sb = new StringBuilder();

        void Flush()
        {
            var s = sb.ToString().Trim();
            if (s.Length > 0) result.Add(s);
            sb.Clear();
        }

        foreach (var ch in text)
        {
            if (ch == '\r') continue;
            if (ch == '\n') { Flush(); continue; }
            sb.Append(ch);
            if (ch is '。' or '！' or '？' or '；' or '…' or '!' or '?' or ';') Flush();
        }
        Flush();

        var speakable = result.Where(s => SanitizeText(s).Any(char.IsLetterOrDigit)).ToList();
        return speakable.Count > 0 ? speakable : [text.Trim()];
    }

    /// <summary>
    /// 清洗词表外字符：删除 emoji（代理对）与控制字符 —— Kokoro 是词典式 TTS，词表有限，
    /// emoji 会触发 sherpa-onnx 的 Unknown token 警告（实测合成仍成功，但控制台噪音 + 潜在风险）。
    /// 保留汉字/数字/常见标点。面积单位「m2/㎡」先换成中文读法：'m'/'M' 是词表外字符会被整段丢掉
    /// （实测 espeak 兜底对该模型不可用），不换的话「元/m2·月」会读成「元二」。
    /// </summary>
    private static string SanitizeText(string text)
    {
        if (text.Length == 0) return text;
        text = text.Replace("/m2", "每平方米").Replace("/M2", "每平方米")
                   .Replace("/㎡", "每平方米").Replace("/m²", "每平方米").Replace("/M²", "每平方米")
                   .Replace("m2", "平方米").Replace("M2", "平方米")
                   .Replace("㎡", "平方米").Replace("m²", "平方米").Replace("M²", "平方米");
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsSurrogate(ch)) continue;          // emoji 等代理对 → 删除
            if (ch is '\r' or '\n' or '\t') { sb.Append(' '); continue; }
            if (ch == '·') continue;                     // 隔点（楼盘名分隔）不会读出来，删掉顺带消 espeak 噪音
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>float samples → 16-bit PCM WAV（sherpa-onnx 输出 24kHz 单声道）</summary>
    private static void WriteWav(string path, float[] samples, int sampleRate)
    {
        var data = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var s = (short)Math.Clamp(samples[i] * short.MaxValue, short.MinValue, short.MaxValue);
            data[i * 2] = (byte)(s & 0xFF);
            data[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write("RIFF"u8);
        bw.Write(36 + data.Length);
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8);
        bw.Write(16);
        bw.Write((short)1);            // PCM
        bw.Write((short)1);            // mono
        bw.Write(sampleRate);
        bw.Write(sampleRate * 2);      // byte rate
        bw.Write((short)2);            // block align
        bw.Write((short)16);           // bits
        bw.Write("data"u8);
        bw.Write(data.Length);
        bw.Write(data);
    }
}
