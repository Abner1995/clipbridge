using System.Text.Json;
using CreatFlow.Platforms.Core;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 会话凭证（<see cref="PlatformSessionCredentials"/>）里那几个非标准字段的**取值口径**。
///
/// 原来是 <see cref="PublishPipeline"/> 的私有静态方法；视频管理页的删除执行器也要从同几个
/// 地方取值（`env` 快照 / `finder_username` / cookie 里的 `api_ph`），第二次使用就上提 ——
/// 各写一份的话，哪天登录侧换了存法只有一半代码跟着改。
///
/// 统一姿态：**取值失败一律返回空/默认**，绝不抛 —— 调用方拿不到就退回 SDK 的硬编码兜底，
/// 而不是让整条发布/删除流程炸在解析上。
/// </summary>
public static class CredentialFields
{
    /// <summary>extra_data_json → 松散字典（csrfToken / apiPh / deviceId / env 都在这）</summary>
    public static Dictionary<string, object?> Extra(string? extraDataJson)
    {
        if (string.IsNullOrWhiteSpace(extraDataJson)) return [];
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(extraDataJson) ?? [];
        }
        catch { return []; }
    }

    /// <summary>
    /// extra 里的 `env` 字段 → 环境快照；缺失/解析失败返回 null
    /// （届时 PlatformOptions 用硬编码指纹兜底）。
    /// </summary>
    public static PlatformEnvironmentSnapshot? EnvironmentSnapshot(string? extraDataJson)
    {
        if (string.IsNullOrWhiteSpace(extraDataJson)) return null;
        try
        {
            var extra = Extra(extraDataJson);
            if (!extra.TryGetValue("env", out var envRaw) || envRaw is null)
                return null;
            // ⚠️ Extra() 反序列化出的值是 JsonElement（不是 string）—— 曾写成 `envRaw is not string`，
            // 判断永远不成立 → 快照永远读不出 → 所有请求 fallback 硬编码 UA（2026-10-16 实测修复）。
            // JsonElement.ToString()：字符串值给内容、对象值给原始 JSON —— 两种存法通吃。
            var envJson = envRaw.ToString();
            if (string.IsNullOrWhiteSpace(envJson))
                return null;

            var snap = JsonSerializer.Deserialize<PlatformEnvironmentSnapshot>(envJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return snap is { IsValid: true } ? snap : null;
        }
        catch { return null; }
    }

    /// <summary>从 localStorage JSON 字符串中提取指定字段</summary>
    public static string? LocalStorageField(string? localStorageJson, string key)
    {
        if (string.IsNullOrWhiteSpace(localStorageJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(localStorageJson);
            if (!doc.RootElement.TryGetProperty(key, out var val)) return null;
            var s = val.GetString();
            // localStorage 值可能被 JSON 双重编码（如 "\"value\""），去掉外层引号
            return string.IsNullOrWhiteSpace(s) ? null : s.Trim('"');
        }
        catch { return null; }
    }

    /// <summary>从 Cookie 字符串中提取指定 key 的值</summary>
    public static string? CookieValue(string? cookie, string name)
    {
        if (string.IsNullOrWhiteSpace(cookie)) return null;
        foreach (var part in cookie.Split(';'))
        {
            var trimmed = part.Trim();
            var eq = trimmed.IndexOf('=');
            if (eq > 0 && trimmed[..eq] == name) return trimmed[(eq + 1)..];
        }
        return null;
    }
}
