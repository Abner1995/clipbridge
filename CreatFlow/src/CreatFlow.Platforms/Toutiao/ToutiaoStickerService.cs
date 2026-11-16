using System.Globalization;
using System.Text.Json;

namespace CreatFlow.Platforms.Toutiao;

/// <summary>
/// 头条视频互动贴纸（A3，v1 **只做「关注引导」**）。
///
/// wire（2026-11-10 交互式抓包 <c>capture-2026-11-10T08-45-23.jsonl</c>）：
///   · 创建：<c>POST /ixigua/api/int/sticker/create</c>
///     ```
///     {"StickerInfos":[{"StickerID":"","BaseStickerInfo":{"URI":"","StickerType":1,
///       "StartTime":"0","Duration":"5","MarginLeft":"81","MarginTop":"78","HeightRatio":"36",
///       "StickerCreateType":0,"sticker_effect_id":"1126344"},
///       "FollowExtraInfo":{"ToUserID":"<当前账号 user_id>","FollowAvatar":"<头像 URL>"}}]}
///     ```
///     → <c>data.StickerIDs = ["7706462780370590755_1"]</c>
///   · 发布体三个字段（见 <see cref="ToutiaoPublishRequest.PlayerSticker"/> /
///     <c>AttrsValueMap.InteractStickers</c> / <c>etData.stickerETData</c>）：
///     ```json
///     "PlayerSticker": "{\"list\":[]}",
///     "AttrsValueMap": {"InteractStickers": "[\"7706462780370590755_1\"]"},
///     "etData": {"stickerETData":[{"interaction_sticker_id":"","interaction_sticker_name":"关注引导",
///       "interaction_begin_time":0,"interaction_begin_percent":"0.0","interaction_scale":"1.0",
///       "interaction_duration":5,"interaction_sticker_time_type":"custom"}]}
///     ```
///
/// 贴纸目录 <c>GET /ixigua/api/int/sticker/options</c> 返回六种类型（弹幕引导 / 关注引导 /
/// 点赞引导 / PK投票 / 二选一 / 三选一），但只有「关注引导」的 create 体被抓到 —— 其余类型
/// 的 create 结构不同，**等 P2 抓到再做**。页面本身对「视频 &lt;10 秒或竖版」置灰该入口，
/// 预检只 Warning 不拦发布；创建失败也不拦发布（贴纸是可选项）。
/// </summary>
public sealed class ToutiaoStickerService
{
    /// <summary>创建贴纸（抓包 <c>POST /ixigua/api/int/sticker/create</c>）</summary>
    public const string CreatePath = "/ixigua/api/int/sticker/create";

    /// <summary>贴纸目录（抓包 <c>GET /ixigua/api/int/sticker/options</c>）—— v1 只用它确认能力，不解析</summary>
    public const string OptionsPath = "/ixigua/api/int/sticker/options";

    /// <summary>「关注引导」的 effect id（options 响应 <c>data.effects[].EffectId</c>，2026-11-10 抓包）</summary>
    public const string FollowStickerEffectId = "1126344";

    /// <summary>展示名 —— 发布体 <c>etData.stickerETData[].interaction_sticker_name</c></summary>
    public const string FollowStickerName = "关注引导";

    /// <summary>页面默认贴纸时长（秒）</summary>
    public const int DefaultDurationSec = 5;

    private readonly PlatformOptions _options;

    public ToutiaoStickerService(PlatformOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// 创建「关注引导」贴纸，返回平台分配的 <c>StickerIDs</c>（通常一个）。
    /// 平台业务错误 → <see cref="ToutiaoStickerException"/>（调用方按「贴纸未添加、继续发布」处理）。
    /// </summary>
    public async Task<IReadOnlyList<string>> CreateFollowStickerAsync(
        string cookie, string? csrfToken, string userId, string? followAvatar,
        int startSec, int durationSec, CancellationToken ct = default)
    {
        using var client = new ToutiaoApiClient(
            cookie, _options, csrfToken, ToutiaoPublishService.PublishReferer);

        var url = _options.ToutiaoHost.TrimEnd('/') + CreatePath;
        var json = BuildCreateFollowStickerBody(userId, followAvatar, startSec, durationSec);
        using var doc = JsonDocument.Parse(json);
        var body = await client.PostJsonRawAsync(url, doc.RootElement.Clone(), ct);
        return ParseCreateResponse(body);
    }

    // ═══ 纯函数（自检直接验 wire 形体） ═══

    /// <summary>
    /// 组装 create 请求体 —— 与 2026-11-10 抓包逐字段一致（值类型也要一致：
    /// <c>StartTime</c> / <c>Duration</c> 是**字符串**，<c>MarginLeft/MarginTop/HeightRatio</c>
    /// 是字符串，<c>StickerCreateType</c> 是数字 0）。位置/尺寸是页面默认值（v1 不开放拖动）。
    /// </summary>
    public static string BuildCreateFollowStickerBody(
        string userId, string? followAvatar, int startSec, int durationSec)
    {
        var body = new Dictionary<string, object?>
        {
            ["StickerInfos"] = new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["StickerID"] = "",
                    ["BaseStickerInfo"] = new Dictionary<string, object?>
                    {
                        ["URI"] = "",
                        ["StickerType"] = 1,
                        ["StartTime"] = startSec.ToString(CultureInfo.InvariantCulture),
                        ["Duration"] = durationSec.ToString(CultureInfo.InvariantCulture),
                        ["MarginLeft"] = "81",
                        ["MarginTop"] = "78",
                        ["HeightRatio"] = "36",
                        ["StickerCreateType"] = 0,
                        ["sticker_effect_id"] = FollowStickerEffectId,
                    },
                    ["FollowExtraInfo"] = new Dictionary<string, object?>
                    {
                        ["ToUserID"] = userId,
                        ["FollowAvatar"] = followAvatar ?? "",
                    },
                },
            },
        };

        return JsonSerializer.Serialize(body, BodyJsonOptions);
    }

    /// <summary>
    /// 解析 create 响应：<c>{"data":{"StickerIDs":["…"],"BaseResp":{…}},"status":0}</c>。
    /// 失败（status 非 0 / 没有 StickerIDs）→ <see cref="ToutiaoStickerException"/>。
    /// </summary>
    public static IReadOnlyList<string> ParseCreateResponse(string bodyText)
    {
        if (string.IsNullOrWhiteSpace(bodyText))
            throw new ToutiaoStickerException("头条贴纸：创建响应为空", bodyText);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(bodyText);
        }
        catch (JsonException ex)
        {
            throw new ToutiaoStickerException(
                $"头条贴纸：创建响应不是 JSON（{ex.Message}），原文：{Snippet(bodyText)}", bodyText);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ToutiaoStickerException(
                    $"头条贴纸：创建响应结构认不出（根节点 {root.ValueKind}），原文：{Snippet(bodyText)}",
                    bodyText);

            var status = root.TryGetProperty("status", out var statusEl)
                         && statusEl.ValueKind == JsonValueKind.Number
                         && statusEl.TryGetInt32(out var s)
                ? s
                : 0;

            var ids = new List<string>();
            if (root.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("StickerIDs", out var arr)
                && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    if (el.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(el.GetString()))
                    {
                        ids.Add(el.GetString()!);
                    }
                }
            }

            if (status != 0 || ids.Count == 0)
            {
                var message = root.TryGetProperty("message", out var m)
                              && m.ValueKind == JsonValueKind.String
                    ? m.GetString()
                    : null;
                throw new ToutiaoStickerException(
                    $"头条贴纸创建失败：status={status}，message={message ?? ""}，"
                    + $"原文：{Snippet(bodyText)}", bodyText);
            }

            return ids;
        }
    }

    /// <summary>
    /// 把「关注引导」并入发布体的 <c>AttrsValueMap</c> / <c>etData</c> —— **已有键原样保留**，
    /// 只写 <c>InteractStickers</c> 与 <c>stickerETData</c>（用户手写的长尾 JSON 不能被覆盖）。
    ///
    /// 现有值不是 JSON 对象（旧档可能写坏）→ 返回 false，调用方按「贴纸未添加」处理，
    /// **不拦发布**。贴纸 ID 列表为空 → 同样返回 false（没东西可合并）。
    /// </summary>
    public static bool TryMergeFollowSticker(
        JsonElement? attrs, JsonElement? etData,
        IReadOnlyList<string> stickerIds, int startSec, int durationSec,
        double videoDurationSeconds,
        out JsonElement mergedAttrs, out JsonElement mergedEtData)
    {
        mergedAttrs = default;
        mergedEtData = default;
        if (stickerIds.Count == 0) return false;
        if (!TryObject(attrs, out var attrsMap) || !TryObject(etData, out var etMap))
            return false;

        // InteractStickers 的值是**字符串**，内容为 JSON 数组（抓包逐字）
        attrsMap["InteractStickers"] = JsonSerializer.SerializeToElement(
            JsonSerializer.Serialize(stickerIds));

        // 每个贴纸一条展示元数据；关注引导通常只有一个 id
        var etList = new List<Dictionary<string, object?>>(stickerIds.Count);
        foreach (var _ in stickerIds)
            etList.Add(BuildStickerEtData(startSec, durationSec, videoDurationSeconds));
        etMap["stickerETData"] = JsonSerializer.SerializeToElement(etList);

        mergedAttrs = JsonSerializer.SerializeToElement(attrsMap);
        mergedEtData = JsonSerializer.SerializeToElement(etMap);
        return true;
    }

    /// <summary>
    /// 展示元数据 —— 字段名/类型逐字对齐抓包。抓包只出现过 begin=0 的样本
    /// （<c>interaction_begin_percent="0.0"</c>）；非 0 开始按页面代码
    /// <c>Math.floor(begin/时长*100)</c> 计算，再按抓包格式保留一位小数。
    /// </summary>
    private static Dictionary<string, object?> BuildStickerEtData(
        int startSec, int durationSec, double videoDurationSeconds)
    {
        var percent = 0.0;
        if (videoDurationSeconds > 0)
        {
            percent = Math.Floor(startSec / videoDurationSeconds * 1000) / 10;
        }

        return new Dictionary<string, object?>
        {
            ["interaction_sticker_id"] = "",
            ["interaction_sticker_name"] = FollowStickerName,
            ["interaction_begin_time"] = startSec,
            ["interaction_begin_percent"] = percent.ToString("0.0", CultureInfo.InvariantCulture),
            ["interaction_scale"] = "1.0",
            ["interaction_duration"] = durationSec,
            ["interaction_sticker_time_type"] = "custom",
        };
    }

    private static bool TryObject(
        JsonElement? value, out Dictionary<string, JsonElement> map)
    {
        map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (value is not { } el || el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return true;
        if (el.ValueKind != JsonValueKind.Object) return false;

        foreach (var p in el.EnumerateObject())
            map[p.Name] = p.Value.Clone();
        return true;
    }

    private static readonly JsonSerializerOptions BodyJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string Snippet(string text) =>
        text.Length > 300 ? text[..300] + "…" : text;
}

/// <summary>头条贴纸创建失败（平台壳错误 / 响应认不出）。</summary>
public sealed class ToutiaoStickerException : Exception
{
    public string? ResponseBody { get; }

    public ToutiaoStickerException(string message, string? responseBody = null)
        : base(message)
    {
        ResponseBody = responseBody;
    }
}
