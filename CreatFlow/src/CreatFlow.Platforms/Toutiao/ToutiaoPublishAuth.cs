using System.Text.Json;

namespace CreatFlow.Platforms.Toutiao;

/// <summary>
/// 头条发布权限快照（<c>GET /xigua/api/upload/GetPublishAuth/?params={}</c> 的
/// <c>data.authorPermission</c>）—— 发布页初始化时页面自己拉的接口，抓包见
/// <c>docs/toutiao/20261105/capture-2026-11-05T01-02-37.jsonl</c>（2026-11-10 真账号回读样本见
/// <c>%TEMP%\toutiao_publish_auth_probe.json</c>）。
///
/// 用途：头条卡上的「赞赏」开关是否真的会被平台接受，取决于账号权限 ——
/// <c>HasPraisePermission=false</c> 时页面根本不显示赞赏开关，<c>PublishVideo</c> 里的
/// <c>Praise=true</c> 会被静默忽略（与视频原创 <c>ClaimOrigin</c> 同一条通道）。
/// 「视频生成图文」（A2）同样有入口权限位
/// <c>TransferToArticleStatus.TransferToArticleButtonShow</c>，一并解析。
/// 字段缺失/形状变化一律回 <c>null</c>（未知），**不猜默认值**。
/// </summary>
public sealed record ToutiaoPublishAuth(
    bool? PraiseAllowed,
    bool? ClaimOriginAllowed,
    bool? PublishDisabled,
    string? NoClaimOriginReason,
    bool? TransferToArticleAllowed);

/// <summary>解析 <see cref="ToutiaoPublishAuth"/>（纯函数，自检可直接钉 JSON 形体）。</summary>
public static class ToutiaoPublishAuthParser
{
    /// <summary>
    /// 解析原始 JSON。找不到 <c>data.authorPermission</c>（或整体不是 JSON）→ <c>null</c>。
    /// 布尔值同时接受 <c>true/false</c>、<c>0/1</c> 与字符串 <c>"true"/"1"</c>（页面不同版本混用过）。
    /// </summary>
    public static ToutiaoPublishAuth? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var container = root;
            if (container.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Object)
            {
                container = data;
            }

            if (!container.TryGetProperty("authorPermission", out var perm)
                || perm.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return new ToutiaoPublishAuth(
                Bool(perm, "HasPraisePermission"),
                Bool(perm, "ClaimOriginPermission"),
                Bool(perm, "DisablePublish"),
                Str(perm, "CanNotClaimOriginReason"),
                // 视频生成图文（A2）：permission.TransferToArticleStatus.TransferToArticleButtonShow
                TransferToArticle(perm));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool? Bool(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when el.TryGetInt64(out var n) => n != 0,
            JsonValueKind.String => el.GetString()?.Trim().ToLowerInvariant() switch
            {
                "true" or "1" => true,
                "false" or "0" or "" => false,
                _ => null,
            },
            _ => null,
        };
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    /// <summary>
    /// <c>authorPermission.TransferToArticleStatus.TransferToArticleButtonShow</c> ——
    /// 「视频生成图文」入口是否显示；缺字段回 <c>null</c>（未知，不猜）。
    /// </summary>
    private static bool? TransferToArticle(JsonElement perm)
    {
        if (!perm.TryGetProperty("TransferToArticleStatus", out var status)
            || status.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        return Bool(status, "TransferToArticleButtonShow");
    }
}
