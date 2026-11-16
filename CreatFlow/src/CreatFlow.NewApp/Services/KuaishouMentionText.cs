namespace CreatFlow.NewApp.Services;

/// <summary>
/// 快手 @好友 的 caption 文本 token（**2026-11-15 页面 submit 抓包定案**，
/// <c>docs/kuaishou/wire-2026-11-11.md</c> §2.3 的更正段）。
///
/// 页面编辑器的「@ 芯片」在 autosave（<c>snapshot/save</c>）里序列化成
/// <c>&lt;span is-tag="true"&gt;&lt;span class="at-user-item" data-user-id="…"&gt;@昵称&lt;/span&gt;&lt;/span&gt;&amp;nbsp;</c>，
/// **但真实发布端点 <c>video/pc/submit</c> 的 caption 是纯文本**，每个 @ 变成：
/// <code>
/// @昵称(O&lt;userId&gt;)        // 例：@快手用户1719198701622(O4213080047) 后跟一个空格
/// </code>
/// 实测：把 HTML 直接提交，平台会当普通文字原样显示，被 @ 方收不到通知
/// （2026-11-15 私密受控发布 + 用户侧确认）；页面 submit 抓包则是上面的 token。
///
/// 本类只做「已选用户 → caption 文本」的纯变换（自检钉死）；发布前逐个 <c>at/choose</c>
/// 的会话登记在 <see cref="PublishPipeline"/> 快手分支里做（失败跳过该用户，不拦发布）。
///
/// 证据边界：@ 与 <c>#话题</c> 混排的 submit 文本未单独抓（页面行为应是「话题保留 #文本」）；
/// 这里把 @ token 放在**末尾**（用户文字与话题都保持原有顺序），不猜混排顺序。
/// </summary>
public static class KuaishouMentionText
{
    /// <summary>过滤坏条目：<c>userId ≤ 0</c> / 昵称为空丢弃；同一 userId 只留一条（保序）。</summary>
    public static IReadOnlyList<KuaishouMentionRef> Usable(IEnumerable<KuaishouMentionRef>? users)
    {
        var list = new List<KuaishouMentionRef>();
        var seen = new HashSet<long>();
        foreach (var user in users ?? [])
        {
            if (user is null || user.UserId <= 0) continue;
            var name = NormalizeName(user.UserName);
            if (name.Length == 0) continue;
            if (!seen.Add(user.UserId)) continue;
            list.Add(user with { UserName = name });
        }
        return list;
    }

    /// <summary>
    /// 把已选 @好友 落到 caption：
    /// 正文里已有对应 <c>@昵称</c>（整词边界）→ **原位替换**成 token（不重复显示昵称，且真挂上 @）；
    /// 已经带 token（<c>@昵称(O…)</c>）→ 不动；没有 → 追加到末尾（前置一个空格分隔）。
    /// token 后补一个空格 —— 与页面 submit 抓包同形。
    /// </summary>
    public static string Append(string? caption, IEnumerable<KuaishouMentionRef>? users)
    {
        var usable = Usable(users);
        if (usable.Count == 0) return caption ?? "";

        var text = caption ?? "";
        var searchFrom = 0;

        foreach (var user in usable)
        {
            var token = BuildToken(user.UserId, user.UserName);

            // 已经是完整 token（旧档 / 重复发布）→ 不重复加
            if (text.Contains(token, StringComparison.Ordinal)) continue;

            var plain = "@" + user.UserName;
            var idx = FindToken(text, plain, searchFrom);
            if (idx >= 0)
            {
                var after = idx + plain.Length;
                var needsSpace = after >= text.Length || !char.IsWhiteSpace(text[after]);
                text = string.Concat(
                    text[..idx], token, needsSpace ? " " : "", text[after..]);
                searchFrom = idx + token.Length;
                continue;
            }

            if (text.Length > 0 && !char.IsWhiteSpace(text[^1])) text += " ";
            text += token + " ";
        }

        return text;
    }

    /// <summary>
    /// 单个 @好友 的 wire token（页面 submit 抓包逐字）：
    /// <c>@昵称(O&lt;userId&gt;)</c>。公开给自检钉形状。
    /// </summary>
    public static string BuildToken(long userId, string userName) =>
        $"@{NormalizeName(userName)}(O{userId})";

    /// <summary>
    /// 在 <paramref name="text"/> 里找整词 <c>@昵称</c>：后面必须是结尾 / 空白 / 标点，
    /// 防止「@知鸭」误吞「@知鸭鸭」（与视频号 <c>FindMentionBoundary</c> 同口径）。
    /// </summary>
    private static int FindToken(string text, string token, int from)
    {
        var idx = Math.Max(0, from);
        while ((idx = text.IndexOf(token, idx, StringComparison.Ordinal)) >= 0)
        {
            var end = idx + token.Length;
            if (end >= text.Length || char.IsWhiteSpace(text[end]) || char.IsPunctuation(text[end]))
                return idx;
            idx = end;
        }
        return -1;
    }

    private static string NormalizeName(string? name) =>
        (name ?? "").Replace(' ', ' ').Trim();
}
