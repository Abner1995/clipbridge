using CreatFlow.NewApp.Models;
using CreatFlow.Platforms.Douyin;
using CreatFlow.Platforms.Douyin.Requests;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 抖音 @好友 的正文 / wire 规则（2026-11-11 B1 抓包，样本 4 逐字）：
/// <list type="bullet">
/// <item><c>item.common.mentions</c> = <c>["&lt;uid&gt;"]</c>（字符串数组）</item>
/// <item><c>item.common.caption</c> / <c>item.common.text</c> 里保留 <c>@昵称</c> 文本</item>
/// <item><c>item.common.text_extra</c> 追加
///   <c>{"start","end","type":0,"hashtag_name":"","hashtag_id":0,"user_id":"…",
///   "at_source":"user_manual","caption_start","caption_end"}</c>
///   —— <c>start/end</c> 以 <c>common.text</c>（标题 + 空格 + 简介）为坐标系，
///   <c>caption_*</c> 以简介为坐标系；与 #话题 条目**合并**成同一个数组。</item>
/// </list>
/// 不补正文里的「@昵称」= <c>mentions</c> 落不下去（页面两者一起发），
/// 所以正文补写、预检与发布端共用本类的同一份口径。
/// </summary>
public static class DouyinMentionRules
{
    /// <summary>过滤掉缺 uid / 昵称的坏条目（旧档、手改存档可能带进来）。</summary>
    public static IReadOnlyList<DouyinMentionRef> Usable(IEnumerable<DouyinMentionRef>? mentions) =>
        (mentions ?? []).Where(m => !string.IsNullOrWhiteSpace(m.Uid)
                                    && !string.IsNullOrWhiteSpace(m.Nickname)).ToList();

    /// <summary>正文末尾补「@昵称」—— 每个提及之间留一个空格（页面内联节点的文本化同形）。</summary>
    public static string AppendMentionText(string caption, IReadOnlyList<DouyinMentionRef> mentions)
    {
        var usable = Usable(mentions);
        if (usable.Count == 0) return caption;

        var text = string.Join(" ", usable.Select(m => "@" + m.Nickname.Trim()));
        return string.IsNullOrEmpty(caption) ? text : caption + " " + text;
    }

    /// <summary><c>common.mentions</c> —— uid 的 JSON 字符串数组（去重保序）；空 → null（不下发）。</summary>
    public static string? BuildMentionsJson(IReadOnlyList<DouyinMentionRef> mentions)
    {
        var uids = Usable(mentions).Select(m => m.Uid.Trim()).Distinct().ToList();
        return uids.Count == 0 ? null : JsonSerializer.Serialize(uids);
    }

    /// <summary>
    /// 构造 @ 条目的 <c>text_extra</c>。调用前正文必须已经补过「@昵称」
    /// （否则找不到定位会跳过该条 —— 宁可少一条也不发错偏移量）。
    /// </summary>
    public static List<HashtagExtraEntry> BuildMentionExtras(
        string text, string caption, IReadOnlyList<DouyinMentionRef> mentions)
    {
        var result = new List<HashtagExtraEntry>();
        // text = 标题 + 空格 + 简介（BuildCreateItem 同款公式）→ 简介在 text 里的偏移
        var offset = Math.Max(0, text.Length - caption.Length);
        var searchFrom = 0;

        foreach (var mention in Usable(mentions))
        {
            var token = "@" + mention.Nickname.Trim();
            var idx = caption.IndexOf(token, searchFrom, StringComparison.Ordinal);
            if (idx < 0) continue;
            searchFrom = idx + token.Length;

            result.Add(new HashtagExtraEntry
            {
                Start = offset + idx,
                End = offset + idx + token.Length,
                Type = 0,
                HashtagName = "",
                HashtagId = 0,
                UserId = mention.Uid.Trim(),
                AtSource = "user_manual",
                CaptionStart = idx,
                CaptionEnd = idx + token.Length,
            });
        }
        return result;
    }

    /// <summary>
    /// 把 @ 条目并入话题的 <c>text_extra</c> JSON（同类型先保留、@ 追加在后）。
    /// 话题串是本地生成的；万一解析失败，以 @ 条目为准（不丢用户选择）。
    /// 序列化走属性上的 <c>JsonPropertyName</c>（snake_case）；话题条目的
    /// <c>at_source</c> 为 null → 不写出，旧 wire 形状逐字不变。
    /// </summary>
    public static string? MergeMentionExtras(
        string? hashtagTextExtraJson, IReadOnlyList<HashtagExtraEntry> mentionExtras)
    {
        if (mentionExtras.Count == 0) return hashtagTextExtraJson;

        var list = new List<HashtagExtraEntry>();
        if (!string.IsNullOrWhiteSpace(hashtagTextExtraJson))
        {
            try
            {
                list = JsonSerializer.Deserialize<List<HashtagExtraEntry>>(hashtagTextExtraJson) ?? [];
            }
            catch (JsonException)
            {
                list = [];
            }
        }

        list.AddRange(mentionExtras);
        return JsonSerializer.Serialize(list);
    }

    /// <summary>
    /// 旧档 / 开发者选项的原始 <c>mentions</c> JSON（字符串数组）→ 已选列表；
    /// 只有 uid 没有昵称，展示用 uid 顶替（像小红书活动旧档的 name=ID 做法）。
    /// </summary>
    public static IReadOnlyList<DouyinMentionRef> ParseLegacyUids(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var uids = JsonSerializer.Deserialize<List<string>>(json);
            return (uids ?? [])
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Select(u => new DouyinMentionRef { Uid = u.Trim(), Nickname = u.Trim() })
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
