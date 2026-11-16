namespace CreatFlow.Platforms.Bilibili;

/// <summary>
/// B站分区 / 二级分区列表（2026-11-10）：发布页「分区」从只读默认值
/// （21 / 1015 家装房产）改成平台列表选择。
///
/// 端点（member 域 GET，Cookie 认证，**无 WBI / 无 csrf** —— 抓包 2026-11-04：
/// 同域的 topic/type/v2、tag/recommend 也都只带 Cookie）：
/// <code>
///   GET /x/vupre/web/archive/pre?lang=ch&amp;t={ms}&amp;x-bili-locale-json=…&amp;x-bili-device-req-json=…
///       → data.typelist: [{id, name, parent, parent_name, show, rank, …}]
///         parent=0 是父分区，其余挂在该 parent 下；add_v3.tid 取**子分区 id**
///   GET /x/vupre/web/archive/human/type2/list?t={ms}&amp;x-bili-locale-json=…&amp;x-bili-device-req-json=…
///       → data.type_list: [{id, name}]（1001~1031）
/// </code>
///
/// ⚠️ **tid 与 human_type2 是两份独立列表，不是父子映射**：2026-11-04 抓包样本
/// <c>tid=21 / human_type2=1015</c>，按真实 typelist 21 = 生活·日常（parent=160），
/// 按 type2 列表 1015 = 家装房产 —— 旧笔记里「家装房产 = tid 21」的说法是错的，
/// 别拿一份列表去推另一份。发布体两个字段各自下发（见 <see cref="BilibiliPublishService"/>）。
/// </summary>
public sealed class BilibiliPartitionService
{
    private const string PrePath = "/x/vupre/web/archive/pre";
    private const string Type2Path = "/x/vupre/web/archive/human/type2/list";

    private readonly PlatformOptions _options;

    public BilibiliPartitionService(PlatformOptions options) => _options = options;

    /// <summary>分区树（父 → 子；<c>add_v3.tid</c> 取子分区 id）。</summary>
    public async Task<BilibiliPartitionTree> GetTreeAsync(
        string cookie, CancellationToken ct = default)
    {
        var ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var url = $"{_options.BilibiliHost}{PrePath}"
                  + "?lang=ch"
                  + $"&t={ms}"
                  + $"&x-bili-locale-json={BilibiliUploadService.AxiosEscape(BilibiliUploadService.LocaleJson)}"
                  + $"&x-bili-device-req-json={BilibiliUploadService.AxiosEscape(BilibiliUploadService.DeviceJson)}";

        using var client = new BilibiliApiClient(cookie, _options);
        var json = await client.GetStringAsync(url, BilibiliUploadService.VideoReferer, ct);
        return ParseTree(json);
    }

    /// <summary>二级分区类型列表（<c>add_v3.human_type2</c>）。</summary>
    public async Task<IReadOnlyList<BilibiliTypeOption>> GetHumanTypesAsync(
        string cookie, CancellationToken ct = default)
    {
        var ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var url = $"{_options.BilibiliHost}{Type2Path}"
                  + $"?t={ms}"
                  + $"&x-bili-locale-json={BilibiliUploadService.AxiosEscape(BilibiliUploadService.LocaleJson)}"
                  + $"&x-bili-device-req-json={BilibiliUploadService.AxiosEscape(BilibiliUploadService.DeviceJson)}";

        using var client = new BilibiliApiClient(cookie, _options);
        var json = await client.GetStringAsync(url, BilibiliUploadService.VideoReferer, ct);
        return ParseHumanTypes(json);
    }

    // ═══ 解析（公开 static 供探针 / 自检钉字段映射） ═══

    /// <summary>
    /// 解析 <c>archive/pre</c> 的 <c>data.typelist</c>。
    /// 列表是**平铺的**：<c>parent = 0</c> 的项是父分区，其余项按 <c>parent</c> 挂到父下；
    /// <c>show = false</c> 的项平台自己也不显示，这里同样过滤。
    /// </summary>
    public static BilibiliPartitionTree ParseTree(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var code = Int(root, "code");
        var message = Str(root, "message") ?? "";
        var raw = json.Length > 4000 ? json[..4000] : json;
        if (code != 0)
            return new BilibiliPartitionTree { Code = code, Message = message, RawJson = raw };

        if (!root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("typelist", out var arr)
            || arr.ValueKind != JsonValueKind.Array)
            return new BilibiliPartitionTree
            {
                Code = code, Message = "typelist 缺失", RawJson = raw,
            };

        var rawCount = arr.GetArrayLength();
        var zones = new List<(int Id, string Name, int Parent, string ParentName)>();
        foreach (var z in arr.EnumerateArray())
        {
            if (z.ValueKind != JsonValueKind.Object) continue;
            var id = Int(z, "id");
            var name = Str(z, "name");
            if (id <= 0 || string.IsNullOrWhiteSpace(name)) continue;
            if (z.TryGetProperty("show", out var show) && show.ValueKind == JsonValueKind.False)
                continue;
            zones.Add((id, name, Int(z, "parent"), Str(z, "parent_name") ?? ""));
        }

        var childrenByParent = zones
            .Where(z => z.Parent != 0)
            .GroupBy(z => z.Parent)
            .ToDictionary(g => g.Key, g => g.ToList());

        var groups = new List<BilibiliZoneGroup>();
        foreach (var parent in zones.Where(z => z.Parent == 0))
        {
            var group = new BilibiliZoneGroup(new BilibiliZone(parent.Id, parent.Name));
            if (childrenByParent.Remove(parent.Id, out var children))
            {
                foreach (var c in children)
                    group.Children.Add(new BilibiliZone(c.Id, c.Name));
            }
            groups.Add(group);
        }

        // 兜底：父项缺失（响应截断 / 平台调整）的子分区按 parent_name 自建一组，
        // 名称缺失时显示 id —— 宁可难看也别把分区藏起来
        foreach (var (parentId, children) in childrenByParent)
        {
            var parentName = children.Select(c => c.ParentName)
                .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
                ?? $"分区 {parentId}";
            var group = new BilibiliZoneGroup(new BilibiliZone(parentId, parentName));
            foreach (var c in children)
                group.Children.Add(new BilibiliZone(c.Id, c.Name));
            groups.Add(group);
        }

        return new BilibiliPartitionTree
        {
            Groups = groups,
            Code = code,
            Message = message,
            EntryCount = zones.Count,
            RawEntryCount = rawCount,
            ChildEntryCount = zones.Count(z => z.Parent != 0),
            RawJson = raw,
        };
    }

    /// <summary>解析 <c>human/type2/list</c> 的 <c>data.type_list</c>。</summary>
    public static IReadOnlyList<BilibiliTypeOption> ParseHumanTypes(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (Int(root, "code") != 0) return [];
        if (!root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("type_list", out var arr)
            || arr.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<BilibiliTypeOption>();
        foreach (var t in arr.EnumerateArray())
        {
            var id = Int(t, "id");
            var name = Str(t, "name");
            if (id <= 0 || string.IsNullOrWhiteSpace(name)) continue;
            list.Add(new BilibiliTypeOption(id, name));
        }
        return list;
    }

    private static int Int(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt32(out var n) ? n : 0;

    private static string? Str(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}

// ═══ 公共返回类型 ═══

/// <summary>一个分区（父或子）。</summary>
public sealed record BilibiliZone(int Id, string Name);

/// <summary>父分区 + 其子分区（发布页「分区」面板左列 = Parent，右列 = Children）。</summary>
public sealed class BilibiliZoneGroup
{
    public BilibiliZoneGroup(BilibiliZone parent) => Parent = parent;

    public BilibiliZone Parent { get; }
    public List<BilibiliZone> Children { get; } = [];
}

/// <summary>分区树（平台返回的 <c>typelist</c>）。</summary>
public sealed class BilibiliPartitionTree
{
    public List<BilibiliZoneGroup> Groups { get; init; } = [];
    public int Code { get; init; }
    public string Message { get; init; } = "";

    /// <summary>typelist 过滤后的条目数（排障：父分区以外是否带回了子分区）</summary>
    public int EntryCount { get; init; }

    /// <summary>typelist 数组原始长度（过滤前）</summary>
    public int RawEntryCount { get; init; }

    /// <summary>其中 <c>parent != 0</c> 的子分区条目数</summary>
    public int ChildEntryCount { get; init; }

    /// <summary>响应前缀（排障 / 探针用，最多 4000 字）</summary>
    public string RawJson { get; init; } = "";
}

/// <summary>二级分区类型（<c>human_type2</c>）。</summary>
public sealed record BilibiliTypeOption(int Id, string Name);
