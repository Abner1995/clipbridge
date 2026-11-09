using CreatFlow.Media.Video;

namespace CreatFlow.Media.Llm;

/// <summary>
/// AI 文案生成 — 素材清单 prompt + JSON 解析 + 句数=素材数校验 + 自动重试。
/// 分镜对齐的核心：LLM 生成的句数必须等于素材数，一句 = 一镜。
/// </summary>
public static class LlmCopywriter
{
    /// <summary>
    /// 构建文案生成 prompt（含素材清单与字数约束，只输出 JSON）。
    /// 通用化设计：不绑定行业（房产/电商/门店自适应话术）；
    /// 硬信息保全：名称/地址/价格/证件号等关键信息必须保留（房产广告合规要求，历史教训：LLM 自由发挥会丢预售证号）。
    ///
    /// ⚠️ 第 2/3/5 条是一套（逐字保全 + 拆句 + 拆到哪儿的规则），**改动前先重跑对照实验**：
    /// 2026-10-10 实测（4B/8B × 5 镜房产资料 8 项硬信息，逐镜字数上限内）：
    ///  · 原版措辞：保全 2-3/8，且图片镜普遍超字数（14-18 字 vs 目标 12）；
    ///  · 现版（A+）：保全 5/8 三次零波动、零超限 —— 4B 上的净改善；
    ///  · 只改第 5 条不给拆句去处规则 → 产出过 76 字/句（视频镜定格 20 秒，照片崩坏）；
    ///  · 8B 不吃字数约束（照出 28-38 字句）——"又全又短"单靠措辞治不了，硬信息要更全需换大档（8B 宽松措辞下证件号 3/3）或走"必念清单按镜分配"机制。
    /// </summary>
    public static string BuildPrompt(string keywords, IReadOnlyList<ComposeMediaItem> items)
    {
        var sb = new StringBuilder();
        sb.AppendLine("你是一位短视频解说文案专家。请根据用户提供的资料，为一条短视频生成解说文案。");
        sb.AppendLine();
        sb.AppendLine($"素材清单（共 {items.Count} 个，按播放顺序）：");
        for (var i = 0; i < items.Count; i++)
            sb.AppendLine($"第{i + 1}镜: {(items[i].Kind == MediaKind.Image ? "图片" : "视频素材")}");
        sb.AppendLine();
        sb.AppendLine("要求：");
        sb.AppendLine($"1. 恰好生成 {items.Count} 句，每句对应一个素材（一一对应，顺序一致）");
        sb.AppendLine("2. 图片镜：每句 8~12 个字为好，**为保住关键信息最多可放宽到 20 字**（图片会随配音延长停留，不影响画面）");
        sb.AppendLine("3. 视频镜：每句 **12~20 个字是硬上限**（超出会画面定格，很难看）；信息放不下就挪到相邻的图片镜里");
        sb.AppendLine("4. 口语化、有吸引力、突出卖点，符合短视频节奏");
        sb.AppendLine("5. 【硬信息保全，最高优先级】资料中的名称、地址、价格/费用、证件/编号、开发商、联系方式等关键信息必须完整保留、逐字出现，不得省略或改写丢失；一句里放不下就拆到相邻句子里分别念出（遵守上面的镜头字数规则）。写之前先逐一核对资料里每条信息都已落句");
        sb.AppendLine("6. 文案风格根据资料所属行业自适应（房产=置业推荐，电商=种草安利，门店=到店福利），不限定行业");
        sb.AppendLine($"7. 只输出一个 JSON 对象，格式：{{\"narrations\": [\"第1句\", \"第2句\", ...]}}，不要任何多余文字");
        sb.AppendLine();
        sb.AppendLine("用户资料：");
        sb.AppendLine(keywords);
        return sb.ToString();
    }

    /// <summary>解析 LLM 回复为句列表，校验句数 = 素材数（不满足抛 FormatException，由外层重试）</summary>
    public static List<string> ParseNarrations(string response, int expectedCount)
    {
        var root = LlmJson.Parse(response);
        if (!root.TryGetProperty("narrations", out var arr) || arr.ValueKind != JsonValueKind.Array)
            throw new FormatException("LLM 响应缺少 narrations 数组");
        var lines = arr.EnumerateArray()
            .Select(e => e.GetString()?.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .Cast<string>()
            .ToList();
        if (lines.Count != expectedCount)
            throw new FormatException($"LLM 返回 {lines.Count} 句，需要 {expectedCount} 句（素材数）");
        return lines;
    }

    /// <summary>生成 + 解析 + 校验，失败自动重试（最多 attempts 次，取消不重试）</summary>
    public static async Task<List<string>> GenerateAsync(
        LlmOptions options, string keywords, IReadOnlyList<ComposeMediaItem> items,
        int attempts = 3, CancellationToken ct = default)
    {
        var prompt = BuildPrompt(keywords, items);
        Exception? last = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                // 默认关思考模式（thinking disabled）：模板化文案秒回；思考模式慢数倍且按输出价计费
                var resp = await OpenAiCompatibleLlmClient.GenerateAsync(options, prompt,
                    temperature: 0.8, maxTokens: 2000, ct: ct).ConfigureAwait(false);
                return ParseNarrations(resp, items.Count);
            }
            catch (Exception ex) when (attempt < attempts && ex is not OperationCanceledException)
            {
                last = ex;
                Log.Warning("[Llm] 文案生成第 {Attempt}/{Total} 次失败: {Msg}，重试", attempt, attempts, ex.Message);
            }
        }
        throw last ?? new IOException("文案生成失败");
    }
}
