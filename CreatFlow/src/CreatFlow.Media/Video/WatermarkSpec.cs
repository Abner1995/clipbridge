using System.Text.Json.Serialization;

namespace CreatFlow.Media.Video;

/// <summary>
/// 水印位置（2026-10-21 用户拍板：四角可选 + 随机）。
/// <see cref="Random"/> = **每条成片合成时**从四角随机挑一个（任务快照存「意图」而不是具体角，
/// 批量里每条各自换角；重试会重新随机）。
/// </summary>
public enum WatermarkPosition
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
    Random,
}

/// <summary>
/// 合成水印规格（2026-10-21 用户需求：合成视频烧水印，默认开启、用户可关）。
/// 文字走 textfile（中文/空格/标点都不需要转义），默认「房淘惠」、右上、半透明白字。
/// **留空 = 不烧**（即使 <see cref="Enabled"/> 开着）—— 开关与文字是两条独立的用户意图。
/// </summary>
public sealed record WatermarkSpec(
    bool Enabled = true,
    string Text = "房淘惠",
    WatermarkPosition Position = WatermarkPosition.TopRight)
{
    /// <summary>默认：开 / 房淘惠 / 右上（老存档缺字段也落到这里）</summary>
    public static WatermarkSpec Default { get; } = new();

    /// <summary>实际烧录的文字：空白折成单个空格、最多 20 字
    /// （手改存档写换行/超长时也只会得到一行短字，不让 drawtext 撑出画面）</summary>
    [JsonIgnore]
    public string EffectiveText
    {
        get
        {
            var t = string.Join(" ", (Text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return t.Length <= 20 ? t : t[..20];
        }
    }

    /// <summary>真正会烧水印：开关开着且文字非空</summary>
    [JsonIgnore]
    public bool IsActive => Enabled && EffectiveText.Length > 0;
}
