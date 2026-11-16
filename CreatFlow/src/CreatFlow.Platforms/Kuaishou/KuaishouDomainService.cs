using System.Text.Json.Serialization;

namespace CreatFlow.Platforms.Kuaishou;

/// <summary>
/// 快手「专属领域 / 二级领域」列表（2026-11-10）：发布页「领域与关联」从手填字符串
/// 改成平台列表选择。
///
/// 端点（cp 域 POST，Cookie + api_ph + <c>__NS_sig3</c> 签名，与其它 cp 接口同一条 Handler 链）：
/// <code>
///   POST /rest/cp/works/v2/video/pc/upload/domain/list
///   body: {"kuaishou.web.cp.api_ph":"…"}（api_ph 由 KuaishouApiPhHandler 自动注入）
///   → {"result":1, "data":[{"firstDomain":"舞蹈", "secondDomain":["舞蹈民族舞", …]}, …]}
/// </code>
///
/// 证据：2026-11-05 抓包（<c>docs/kuaishou/20261105/capture-2026-11-05T03-09-09.jsonl</c>）
/// 页面加载即 POST 这份列表 6 次（同一网络请求被采集器记了两条）；发布页 wrapper
/// <c>fetchDomainList</c> 也是 <c>super.post</c>（<c>docs/kuaishou/js/20260616/upload1.js</c>）。
/// 提交体落点是 <c>submit.domain</c> / <c>submit.secondDomain</c>，**两个都是字符串**
/// （抓包样本两字段均为空串 —— 采集时未选领域）。
///
/// ⚠️ 抓包文件里的中文被采集端按错误编码写成了替换字符（U+FFFD），领域名以真账号探针
/// （<c>--probe-kuaishou-domains</c>）为准，不照抄抓包文本。
/// </summary>
public sealed class KuaishouDomainService
{
    private const string Path = "/rest/cp/works/v2/video/pc/upload/domain/list";

    private readonly PlatformOptions _options;

    public KuaishouDomainService(PlatformOptions options) => _options = options;

    /// <summary>拉两级领域列表（专属领域 → 二级领域）。平台顺序原样保留。</summary>
    public async Task<IReadOnlyList<KuaishouDomain>> GetDomainsAsync(
        string cookie, CancellationToken ct = default)
    {
        using var client = new KuaishouApiClient(cookie, _options);
        var items = await client.PostAsync<List<KuaishouDomainDto>>(Path, new { }, ct);
        return Normalize(items);
    }

    // ═══ 解析（公开 static 供探针 / 排障钉字段映射）═══

    /// <summary>解析 <c>domain/list</c> 原始响应（含 envelope）。</summary>
    public static IReadOnlyList<KuaishouDomain> ParseDomains(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (Int(root, "result") != 1) return [];
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        var items = new List<KuaishouDomainDto>();
        foreach (var d in data.EnumerateArray())
        {
            if (d.ValueKind != JsonValueKind.Object) continue;
            var dto = new KuaishouDomainDto { FirstDomain = Str(d, "firstDomain") };
            if (d.TryGetProperty("secondDomain", out var second)
                && second.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in second.EnumerateArray())
                    if (s.ValueKind == JsonValueKind.String && s.GetString() is { } name)
                        (dto.SecondDomain ??= []).Add(name);
            }
            items.Add(dto);
        }
        return Normalize(items);
    }

    /// <summary>过滤空名、trim；二级领域去空去重（保序）。</summary>
    private static IReadOnlyList<KuaishouDomain> Normalize(IEnumerable<KuaishouDomainDto> items)
    {
        var list = new List<KuaishouDomain>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dto in items)
        {
            var name = dto.FirstDomain?.Trim();
            if (string.IsNullOrEmpty(name) || !seen.Add(name)) continue;

            var seconds = new List<string>();
            var seenSecond = new HashSet<string>(StringComparer.Ordinal);
            foreach (var raw in dto.SecondDomain ?? [])
            {
                var s = raw?.Trim();
                if (string.IsNullOrEmpty(s) || !seenSecond.Add(s)) continue;
                seconds.Add(s);
            }
            list.Add(new KuaishouDomain(name, seconds));
        }
        return list;
    }

    private static int Int(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt32(out var n) ? n : 0;

    private static string? Str(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    /// <summary>wire DTO（<see cref="KuaishouApiClient.PostAsync{T}"/> 的 envelope.data 元素）。</summary>
    internal sealed class KuaishouDomainDto
    {
        [JsonPropertyName("firstDomain")]
        public string? FirstDomain { get; set; }

        [JsonPropertyName("secondDomain")]
        public List<string>? SecondDomain { get; set; }
    }
}

/// <summary>
/// 一个专属领域 + 其下二级领域。来源 <c>upload/domain/list</c>；
/// 选中后写 <c>submit.domain</c> / <c>submit.secondDomain</c>（<see cref="KuaishouPublishService"/>）。
/// </summary>
/// <param name="Name">专属领域名（wire 值就是名字本身）</param>
/// <param name="SecondDomains">二级领域名列表（平台顺序；可能为空）</param>
public sealed record KuaishouDomain(string Name, IReadOnlyList<string> SecondDomains)
{
    public int SecondCount => SecondDomains.Count;
}
