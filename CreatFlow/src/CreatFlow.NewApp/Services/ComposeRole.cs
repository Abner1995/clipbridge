namespace CreatFlow.NewApp.Services;

/// <summary>
/// 合成「角色」（角色库；2026-10-11 批次 4）—— 用户自建的角色卡：
/// 名称 + 台词模板（<c>{内容}</c> 占位）+ 人设提示词（AI 优化的口吻依据；空 = 该角色禁用 AI 优化）
/// + 知识库（可选，喂给优化的常用真实资料）。
///
/// 顺序语义（定案）：**先 AI 优化裸文案、再套模板** —— 模板是逐字保真的本地概念，
/// 绝不进 LLM（见 <c>LlmTextOptimizer</c> 头注释）。
/// 存档走 <see cref="ComposeRoleStore"/>（compose_roles.json）—— **绝不能塞 llm.json**（老版会整文件覆写）。
/// </summary>
public sealed record ComposeRole
{
    /// <summary>模板占位符：套用时被「最终文案」替换（只在本地替换，模型看不到它）</summary>
    public const string Placeholder = "{内容}";

    /// <summary>内置固定 Id（builtin-*）；用户新建用 Guid。空 Id = 下拉首位哨兵「不使用角色」</summary>
    public string Id { get; init; } = "";

    public string Name { get; init; } = "";

    /// <summary>台词模板（可含 {内容}；空白 = 不套模板，直接用文案）</summary>
    public string Template { get; init; } = "";

    /// <summary>人设提示词（给 AI 优化的角色定位；空白 → 该角色不能做 AI 优化）</summary>
    public string Persona { get; init; } = "";

    /// <summary>知识库 / 常用资料（可选；会被拼进优化 prompt，别写太长）</summary>
    public string Knowledge { get; init; } = "";

    /// <summary>下拉首位哨兵（「（不使用角色）」）</summary>
    public bool IsNone => Id.Length == 0;

    public bool HasPersona => !string.IsNullOrWhiteSpace(Persona);

    /// <summary>哨兵实例（每个下拉列表放第一位；判定一律用 <see cref="IsNone"/> / 引用，不用名称）</summary>
    public static ComposeRole None { get; } = new() { Name = "（不使用角色）" };

    /// <summary>内置示例（存档缺失/读坏时的兜底；用户一保存就以存档为准，删光不复活）。
    /// 模板刻意只用纯中文 + 中文标点（Kokoro 对拉丁字母/特殊符号会读音出错或静默丢弃）；
    /// 知识库刻意留空（示例资料会被模型当真资料引用，宁可空着让用户自己填）。</summary>
    public static IReadOnlyList<ComposeRole> Defaults { get; } =
    [
        new()
        {
            Id = "builtin-fangtaohui",
            Name = "房淘惠",
            Template = "您好，我是房淘惠。{内容}。欢迎大家私信获取详细信息。",
            Persona = "你是房产置业顾问「房淘惠」：热情、专业、接地气，像跟朋友聊天一样介绍房源，突出地段、户型、价格、配套等卖点，引导观众私信咨询。",
        },
        new()
        {
            Id = "builtin-pet",
            Name = "毛豆妈妈",
            Template = "嗨，我是毛豆妈妈。{内容}。喜欢毛孩子的话，记得点个关注哦。",
            Persona = "你是一位宠物博主（柯基「毛豆」的家长），用第一人称、亲切轻松的语气说话，句子短、有活力，不硬广。",
        },
    ];
}

/// <summary>
/// 角色台词模板套用（纯函数，--selftest 直接断言）——规则（2026-10-11 定案）：
/// <list type="bullet">
/// <item>模板空白 → 原文返回（不套）；</item>
/// <item>含 {内容} → 全部替换；**连接处标点去重**：内容已以句末标点结尾时，去掉模板里紧跟在
///   {内容} 后的那个句末标点（防「。。」进字幕与 TTS 多一次停顿），只动连接处；</item>
/// <item>不含 {内容} → 内容接在模板后面：整段模式加换行分隔；分镜模式直接拼接（换行会让句数 +1）；</item>
/// <item>分镜模式（<paramref name="singleLineTemplate"/> = UseShots）→ 先把模板自身换行去掉再套
///   （一行 = 一镜，句数守恒；否则 runner 的 Materials[i] 会越界），内容自身换行原样保留。</item>
/// </list>
/// 输入先做 \r\n / \r → \n 归一。
/// </summary>
public static class ComposeRoleTemplate
{
    private static readonly char[] SentenceEnds = ['。', '．', '.', '！', '!', '？', '?'];

    public static string Apply(string? template, string content, bool singleLineTemplate = false)
    {
        var text = Normalize(content);
        if (string.IsNullOrWhiteSpace(template)) return text;

        var tpl = Normalize(template);
        if (singleLineTemplate) tpl = tpl.Replace("\n", "");

        return tpl.Contains(ComposeRole.Placeholder)
            ? ReplaceWithPunctuationDedup(tpl, text)
            : singleLineTemplate ? tpl.TrimEnd() + text : tpl.TrimEnd() + "\n" + text;
    }

    /// <summary>文件夹批量单元套模板（Text=null 的自备配音单元原样返回）；role 为 null/哨兵 = 不套</summary>
    public static ComposeFolderUnit ApplyToUnit(ComposeFolderUnit unit, ComposeRole? role)
        => unit.Text is null || role is null || role.IsNone
            ? unit
            : unit with { Text = Apply(role.Template, unit.Text) };

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>{内容} 替换 + 连接处标点去重（逐占位符处理，多占位都替换）。</summary>
    private static string ReplaceWithPunctuationDedup(string template, string content)
    {
        var contentEnds = content.Length > 0 && SentenceEnds.Contains(content[^1]);
        var sb = new StringBuilder(template.Length + Math.Max(0, content.Length - ComposeRole.Placeholder.Length));
        var i = 0;
        while (i < template.Length)
        {
            if (i + ComposeRole.Placeholder.Length <= template.Length &&
                string.CompareOrdinal(template, i, ComposeRole.Placeholder, 0, ComposeRole.Placeholder.Length) == 0)
            {
                sb.Append(content);
                i += ComposeRole.Placeholder.Length;
                // 内容已自带句末标点 → 吞掉模板紧跟在 {内容} 后的那个句末标点
                if (contentEnds && i < template.Length && SentenceEnds.Contains(template[i])) i++;
            }
            else
            {
                sb.Append(template[i]);
                i++;
            }
        }
        return sb.ToString();
    }
}
