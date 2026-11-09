namespace CreatFlow.Media.Llm;

/// <summary>
/// AI 文案优化 — 按角色人设把用户输入改写成口播文案（合成页「角色库」用，2026-10-11 批次 4）。
///
/// **顺序语义**（定案，别倒过来）：先优化裸文案、再由 NewApp 侧套角色台词模板 ——
/// 模板（品牌开场白/引导语）是逐字保真的本地概念，**绝不进 prompt**；
/// 占位符 <c>{内容}</c> 只在本地替换，模型永远看不到它（模板进了 LLM 会被其"顺手改写"）。
///
/// prompt 是纯静态 builder（LlmSelfTest [6] 有逐字守卫 —— 改词先想行为再跑对照）；
/// 调用骨架照 <see cref="LlmCopywriter"/>：自动重试（取消不重试）、失败记 Log.Warning。
/// </summary>
public static class LlmTextOptimizer
{
    /// <summary>
    /// 构建优化 prompt（人设 + 知识库可选 + 待优化文案；分行规则按输入行数数据驱动 ——
    /// 不换行/保持 N 行，不给模型选择余地，分镜模式（一行 = 一镜）全靠它保句数）。
    /// </summary>
    public static string BuildPrompt(string? persona, string? knowledge, string input)
    {
        var normalized = input.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        var lineCount = normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;

        var sb = new StringBuilder();
        sb.AppendLine("你是短视频口播文案编辑。把「待优化文案」（零散资料、卖点或关键词）改写成该角色在镜头前口播时说的话——像真人对着镜头聊天那样，用完整的话把信息讲出来。");
        sb.AppendLine();
        sb.AppendLine("【改写范例】（示范改写幅度；内容与下面的资料无关）");
        sb.AppendLine("资料：幸福里 120平 四居 南北通透 550万 地铁口 学区");
        sb.AppendLine("改写：幸福里这套120平的四居室，南北通透，走几步就是地铁口，还带学区，550万就能拿下。");
        sb.AppendLine();
        sb.AppendLine("【角色人设】");
        sb.AppendLine(string.IsNullOrWhiteSpace(persona) ? "一位亲切、专业的短视频主播" : persona.Trim());
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(knowledge))
        {
            sb.AppendLine("【角色常用资料】（真实资料，可作为事实依据；只在本次文案相关时使用，不要硬塞）");
            sb.AppendLine(knowledge.Trim());
            sb.AppendLine();
        }
        sb.AppendLine("【待优化文案】");
        sb.AppendLine(normalized);
        sb.AppendLine();
        sb.AppendLine("【要求】");
        sb.AppendLine("1. 只输出改写后的文案本身：不要解释、不要点评、不要加标题或编号、不要用引号或代码块包起来，不要输出任何多余文字。");
        sb.AppendLine("2. 【不许加信息】只允许使用「待优化文案」和「角色常用资料」里出现过的信息：原文没提的开发商、交付时间、优惠活动、联系方式等事实一律不许自己编；语气可以更热情，但事实不能加。");
        sb.AppendLine("3. 【信息不能丢】楼盘/小区名称、地址、面积、户型、价格/费用、电话/微信号、证件编号、时间等一个都不能少；数字不能改（不要换成汉字、不要换算或四舍五入）。");
        sb.AppendLine("4. 【必须成句】把词组罗列改写成完整顺口的句子（不写「89平 三居」，要写「这套89平的三居」）；加自然的衔接，句子短、有节奏；不要书面语、不要空喊口号。");
        sb.AppendLine("5. 长度与资料相称：资料少就写短一点（一两句也行），不要为了凑长度加废话；也不能为了变短丢掉第 3 条的信息。");
        sb.AppendLine("6. 不要自己加开场白、自我介绍、结尾的总结或号召语（包括「关注我」「私信咨询」这类——这些由角色台词模板统一负责）：把资料讲完就结束。");
        sb.AppendLine("7. 不要使用 emoji、颜文字、markdown 标记（星号/井号/列表符号）或括号里的动作说明。");
        sb.AppendLine(lineCount > 1
            ? $"8. 原文共 {lineCount} 行，请保持相同的分行：每行对应原文一行，不要合并、不要拆行，仍输出 {lineCount} 行。"
            : "8. 请输出一段连续的文字，不要换行。");
        sb.AppendLine();
        sb.AppendLine("最后核对一遍：信息没加、没丢、数字没改；每句都是完整的话。");
        return sb.ToString();
    }

    /// <summary>
    /// 清洗模型回复：trim / 去 ```` ``` ```` 围栏 / 去「优化后的文案：」类行首标签 / 去整体包裹的引号。
    /// **不做**编号或列表符号剥离 —— 「主卧 1.2 米」这类正常文本会被误伤。
    /// 清洗后为空 = 没有可用内容，调用方按失败处理。
    /// </summary>
    public static string Cleanup(string reply)
    {
        var text = (reply ?? "").Trim();

        // ``` 围栏（整体包裹）：取首行之后、最后一个围栏之前
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = text.IndexOf('\n');
            if (firstNewline >= 0)
            {
                var body = text[(firstNewline + 1)..];
                var end = body.LastIndexOf("```", StringComparison.Ordinal);
                if (end >= 0) body = body[..end];
                text = body.Trim();
            }
        }

        // 行首标签（模型爱加的「优化后的文案：」）
        foreach (var label in (string[])["优化后的文案：", "优化后的文案:", "优化文案：", "优化文案:", "文案：", "文案:"])
        {
            if (text.StartsWith(label, StringComparison.Ordinal))
            {
                text = text[label.Length..].Trim();
                break;
            }
        }

        // 整体包裹的引号（「」/ “” / ""）
        if (text.Length >= 2 &&
            ((text[0] == '「' && text[^1] == '」') ||
             (text[0] == '“' && text[^1] == '”') ||
             (text[0] == '"' && text[^1] == '"')))
        {
            text = text[1..^1].Trim();
        }

        return text;
    }

    /// <summary>生成 + 清洗 + 校验非空，失败自动重试（最多 attempts 次，取消不重试）。</summary>
    public static async Task<string> OptimizeAsync(
        LlmOptions options, string? persona, string? knowledge, string input,
        int attempts = 3, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input)) throw new ArgumentException("待优化文案为空", nameof(input));

        var prompt = BuildPrompt(persona, knowledge, input);
        // 输出上限跟着输入走：固定小值会把长资料静默截断（客户端只判空、不判截断）；
        // 下限 256 给推理模型留 reasoning 余量（content 被 reasoning 吃空的教训），上限防跑飞。
        var maxTokens = Math.Clamp(input.Length + 128, 256, 1500);
        Exception? last = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                // 默认关思考模式（thinking disabled）：改写不需要深度推理，开思考慢数倍且按输出价计费
                var reply = await OpenAiCompatibleLlmClient.GenerateAsync(options, prompt,
                    temperature: 0.7, maxTokens: maxTokens, ct: ct).ConfigureAwait(false);
                var text = Cleanup(reply);
                if (text.Length == 0) throw new IOException("模型返回为空");
                return text;
            }
            catch (Exception ex) when (attempt < attempts && ex is not OperationCanceledException)
            {
                last = ex;
                Log.Warning("[Llm] 文案优化第 {Attempt}/{Total} 次失败: {Msg}，重试", attempt, attempts, ex.Message);
            }
        }
        throw last ?? new IOException("文案优化失败");
    }
}
