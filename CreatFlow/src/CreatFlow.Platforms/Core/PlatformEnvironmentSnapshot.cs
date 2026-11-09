using System.Text.Json.Serialization;

namespace CreatFlow.Platforms.Core;

/// <summary>
/// 平台环境快照 — 登录 WebView2 时从真实页面采集（navigator / screen / userAgentData），
/// 随 platform-sessions 的 extraDataJson 持久化（key: env）。
///
/// 用途：替代各处硬编码的假 Chrome/150 指纹，让 .NET HTTP 请求与页面内真实环境对齐
/// （消除「请求宣称 vs 页面指纹」矛盾扣分项）。本机 WebView2 实为 Edge 内核，
/// 硬编码 Chrome 只会让 sec-ch-ua 与页面 SDK 上报不一致。
///
/// 注意：属性名经 JsonPropertyName 固定 — 该类序列化进 extraDataJson 落库，
/// Obfuscar 混淆后跨版本属性名须稳定（见 obfuscar 配置）。
/// </summary>
public sealed class PlatformEnvironmentSnapshot
{
    [JsonPropertyName("userAgent")]
    public string? UserAgent { get; init; }

    /// <summary>
    /// 完整 sec-ch-ua 头值（含引号），如
    /// "Not A(Brand";v="8", "Chromium";v="151", "Microsoft Edge";v="151"
    /// </summary>
    [JsonPropertyName("secChUa")]
    public string? SecChUa { get; init; }

    /// <summary>sec-ch-ua-mobile 头值："?0" / "?1"</summary>
    [JsonPropertyName("secChUaMobile")]
    public string? SecChUaMobile { get; init; }

    /// <summary>sec-ch-ua-platform 头值（含引号），如 "\"Windows\""</summary>
    [JsonPropertyName("secChUaPlatform")]
    public string? SecChUaPlatform { get; init; }

    [JsonPropertyName("screenWidth")]
    public string? ScreenWidth { get; init; }

    [JsonPropertyName("screenHeight")]
    public string? ScreenHeight { get; init; }

    [JsonPropertyName("browserLanguage")]
    public string? BrowserLanguage { get; init; }

    [JsonPropertyName("browserPlatform")]
    public string? BrowserPlatform { get; init; }

    [JsonPropertyName("timezoneName")]
    public string? TimezoneName { get; init; }

    /// <summary>快照是否可用（UA + sec-ch-ua 是核心指纹）</summary>
    [JsonIgnore]
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(UserAgent) && !string.IsNullOrWhiteSpace(SecChUa);
}
