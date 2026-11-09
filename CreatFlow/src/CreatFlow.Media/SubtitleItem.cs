namespace CreatFlow.Media;

/// <summary>
/// 字幕条目，与配音真实语音时间对齐（毫秒）。
/// 来自 TTS Provider 的 SentenceBoundary/WordBoundary 事件（100ns → ms），
/// 组装后每行 ≤16 字，行时间 = 首词 StartMs ～ 末词 EndMs。
/// </summary>
public sealed record SubtitleItem(string Text, int StartMs, int EndMs);
