namespace CreatFlow.Media.Tts;

/// <summary>
/// 字幕行组装 — 供应商无关：把「句级文本 + 词级时间戳」切成 ≤16 字/行的逐行字幕。
/// Edge（metadata）与 Azure（SDK WordBoundary 事件）等 Provider 共用同一套组装规则。
/// </summary>
public static class SubtitleLineBuilder
{
    /// <summary>
    /// 组装字幕行：
    ///   1) 每个词在句文本中定位（词不含标点，IndexOf 对齐到含标点的句子原文，词按序出现）
    ///   2) 行 = 句文本 [行首..词尾)，标点自然随行
    ///   3) 行字符数 ≥14 断行；即将超 16 字先断（词不拆开）
    ///   4) 行尾吸收一个标点（逗号/冒号/括号等随上一行末尾，避免下一行行首孤标点、数字被拆）
    ///   5) 行时间 = 首词 StartMs ～ 末词 EndMs
    /// 词与句文本对不齐时逐词跳过，不中断出结果。
    /// </summary>
    /// <param name="sentences">句级条目（含标点的权威文本 + 粗时间）</param>
    /// <param name="words">词级条目（不含标点/空白 + 精确时间）</param>
    public static List<SubtitleItem> Build(
        IReadOnlyList<SubtitleItem> sentences, IReadOnlyList<SubtitleItem> words)
    {
        if (words.Count > 0)
        {
            var lines = BuildLinesFromWords(sentences, words);
            if (lines.Count > 0) return lines;
        }
        return BuildFallbackLines(sentences);
    }

    private static List<SubtitleItem> BuildLinesFromWords(
        IReadOnlyList<SubtitleItem> sentences, IReadOnlyList<SubtitleItem> words)
    {
        const int lineTarget = 14;  // 满 14 字断行
        const int lineMax = 16;     // 硬上限（PlayResX 1080 / 字号 58 ≈ 16.5 字）
        var lines = new List<SubtitleItem>();
        foreach (var sent in sentences)
        {
            var t = sent.Text;
            var lineStartIdx = 0;  // 行在句文本中的起始字符
            var lineStartMs = -1;  // 行首词 StartMs
            var lineEndMs = 0;     // 行末词 EndMs
            var cur = 0;           // 句内已对齐游标
            var anyMatched = false;

            void CloseLine()
            {
                if (lineStartMs < 0) return;
                var end = cur;
                // 行尾标点随上一行（如 ",楼盘地址" → "…岚翠府,"）；17 字 ≈ 986px 仍在 1080 画幅内
                if (end < t.Length && IsTrailingPunct(t[end]) && end - lineStartIdx <= lineMax)
                    end++;
                lines.Add(new SubtitleItem(t[lineStartIdx..end], lineStartMs, lineEndMs));
                lineStartIdx = end;
                lineStartMs = -1;
            }

            foreach (var w in words)
            {
                if (w.EndMs < sent.StartMs - 100) continue;  // 词在本句之前
                if (w.StartMs > sent.EndMs + 100) break;     // 词在本句之后

                var nw = w.Text.Trim();
                if (nw.Length == 0) continue;
                var pos = t.IndexOf(nw, cur, StringComparison.Ordinal);
                if (pos < 0) continue;                       // 词对不齐 → 跳过

                anyMatched = true;
                var newEnd = pos + nw.Length;
                if (lineStartMs >= 0 && newEnd - lineStartIdx > lineMax)
                {
                    // 加上这个词就超上限 → 先断行，词留给下一行
                    CloseLine();
                    lineStartIdx = pos;
                }
                if (lineStartMs < 0) lineStartMs = w.StartMs;
                lineEndMs = w.EndMs;
                cur = newEnd;

                if (cur - lineStartIdx >= lineTarget) CloseLine();
            }
            // 句末收尾行；整句一个词都没对上 → 整句兜底（保持原行为）
            if (lineStartMs >= 0) CloseLine();
            else if (!anyMatched) lines.Add(sent);
        }
        return lines;
    }

    /// <summary>断行处紧跟的标点：随上一行尾（避免下一行行首孤标点）</summary>
    private static bool IsTrailingPunct(char c) =>
        "，,、：。！？；;.?!:·()…".IndexOf(c) >= 0;

    /// <summary>
    /// 兜底：无词级数据时的行切分。≤16 字整句用；超长句按字符比例均分时间
    /// （语音大致匀速，比例时间偏差通常在几百 ms 内，仅兜底用）。
    /// </summary>
    private static List<SubtitleItem> BuildFallbackLines(IReadOnlyList<SubtitleItem> sentences)
    {
        const int lineChars = 16;
        var lines = new List<SubtitleItem>();
        foreach (var s in sentences)
        {
            if (s.Text.Length <= lineChars) { lines.Add(s); continue; }
            var n = (int)Math.Ceiling(s.Text.Length / (double)lineChars);
            var dur = s.EndMs - s.StartMs;
            for (var i = 0; i < n; i++)
            {
                var cs = s.Text.Length * i / n;
                var ce = s.Text.Length * (i + 1) / n;
                lines.Add(new SubtitleItem(
                    s.Text[cs..ce],
                    s.StartMs + dur * i / n,
                    s.StartMs + dur * (i + 1) / n));
            }
        }
        return lines;
    }
}
