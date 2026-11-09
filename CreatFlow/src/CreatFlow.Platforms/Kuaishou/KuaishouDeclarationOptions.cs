namespace CreatFlow.Platforms.Kuaishou;

/// <summary>
/// 快手自主声明选项 — 硬编码，对标 examples/kuaishou/media/作者声明.md。
///
/// 快手叫"作者声明"（不是抖音的"自主声明"），值体系完全不同：
///   1 = 内容为AI生成
///   2 = 演绎情节，仅供娱乐
///   3 = 个人观点，仅供参考
///   4 = 素材来源于网络（需额外填 platform + location + time）
///
/// 提交格式：
///   declareInfo: { source: 1 }
///   declareInfo: { source: 4, platform: 2, location: "北京市", time: 1785772800000 }
/// </summary>
public static class KuaishouDeclarationOptions
{
    public static IReadOnlyList<KuaishouDeclarationOption> All { get; } =
    [
        new(1, "内容为AI生成"),
        new(2, "演绎情节，仅供娱乐"),
        new(3, "个人观点，仅供参考"),
        new(4, "素材来源于网络", HasSourceDetail: true),
    ];

    /// <summary>
    /// 构建 submit 请求中的 declareInfo JSON 对象。
    /// </summary>
    /// <param name="source">声明类型: 1=AI生成, 2=演绎情节, 3=个人观点, 4=素材来源于网络</param>
    /// <param name="platform">source=4 时: 1=快手平台, 2=其他平台</param>
    /// <param name="location">source=4 时: 所在地区（省市区文本）</param>
    /// <param name="time">source=4 时: 发生时间（毫秒时间戳）</param>
    public static Dictionary<string, object> BuildDeclareInfo(
        int source,
        int? platform = null,
        string? location = null,
        long? time = null)
    {
        var info = new Dictionary<string, object> { ["source"] = source };

        if (source == 4)
        {
            if (platform.HasValue) info["platform"] = platform.Value;
            if (!string.IsNullOrWhiteSpace(location)) info["location"] = location;
            if (time.HasValue) info["time"] = time.Value;
        }

        return info;
    }
}

/// <summary>
/// 快手作者声明选项。
/// </summary>
/// <param name="Value">submit 请求中的 source 值</param>
/// <param name="Label">UI 展示标签</param>
/// <param name="HasSourceDetail">是否需要展开二级选项（平台/地区/时间）</param>
public sealed record KuaishouDeclarationOption(
    int Value,
    string Label,
    bool HasSourceDetail = false);
