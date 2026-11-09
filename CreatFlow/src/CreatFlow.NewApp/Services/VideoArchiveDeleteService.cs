using CreatFlow.Platforms.Core;
using CreatFlow.Platforms.Douyin;
using CreatFlow.Platforms.Kuaishou;
using CreatFlow.Platforms.Toutiao;
using CreatFlow.Platforms.WechatVideo;
using CreatFlow.Platforms.Xiaohongshu;

namespace CreatFlow.NewApp.Services;

/// <summary>平台侧删除的**归一化结果**（四个平台的返回形状都不一样，收口成这一份）。</summary>
public enum PlatformDeleteResult
{
    /// <summary>平台已删除 —— 可以安全清本地记录</summary>
    Success,

    /// <summary>视频在平台上已不存在（用户自己删过）—— 本地记录是脏数据，可以清</summary>
    NotFound,

    /// <summary>Cookie 过期/被拒 —— 记录必须保留，让用户重新登录后再删</summary>
    Unauthorized,

    /// <summary>其他失败（网络、平台限流、签名不符…）—— 记录保留，可重试</summary>
    Failed,

    /// <summary>
    /// 这条记录指向的**会话已经不在了**（解绑、或重新登录换了新 session id）——
    /// 不是失败：平台侧无从下手，只能清本地记录。
    /// 老版把这种情况归进 `Failed` 报「平台删除失败，请稍后重试」——
    /// 用户重试一万次也还是失败（`VideoManageViewModel.cs:212-217`）。
    /// </summary>
    SessionGone,
}

/// <summary>
/// 「把这件作品从平台上删掉」—— 视频管理页唯一的写操作里最重的一段。
///
/// 为什么独立成服务：它要按平台分四路、每路要的凭证字段都不一样（抖音只要 cookie；
/// 视频号还要 deviceId + finderId；快手还要 cookie 里的 api_ph），
/// 这些细节塞进 VM 就是把老版那个 4000 行 HomeViewModel 的老路再走一遍。
///
/// **精度限制（实测，别再往上加断言）**：四个平台里只有抖音能区分
/// 「已不存在」和「没权限」—— 其余三个 SDK 只回 bool，快手失败还会抛异常。
/// 所以文案写成「平台删除失败（可能已不存在）」，不把不确定说成确定。
/// </summary>
public static class VideoArchiveDeleteService
{
    /// <summary>
    /// 在平台上删除这条作品。**调用方必须先确认会话在、作品 ID 有**（见
    /// <see cref="VideoArchiveFormat.DeleteMode"/>），否则直接返回 <c>Failed</c>。
    ///
    /// <paramref name="coverKey"/> / <paramref name="title"/> 只有快手用得上：归档里存的
    /// 是早期兜底的 mediaId 时，两条删除端点都不认，要用封面 Key 回查作品列表拿真实 ID
    /// （标题只用来缩小列表 keyword）。其他平台忽略。
    /// </summary>
    public static async Task<PlatformDeleteResult> DeleteOnPlatformAsync(
        string platformKey, Guid sessionId, string platformVideoId,
        string? coverKey = null, string? title = null, CancellationToken ct = default)
    {
        if (sessionId == Guid.Empty || string.IsNullOrWhiteSpace(platformVideoId))
            return PlatformDeleteResult.Failed;

        PlatformSessionCredentials? creds;
        try
        {
            creds = await PlatformSessionApi.GetCredentialsAsync(sessionId);
        }
        catch (ApiException ex) when (ex.StatusCode == 404)
        {
            // 会话行没了（解绑 / 重登换了新 id）→ 只能清本地记录
            Log.Information("[VideoArchive] 会话已不存在 session={SessionId} → 只清本地记录", sessionId);
            return PlatformDeleteResult.SessionGone;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[VideoArchive] 取会话凭证失败 session={SessionId}", sessionId);
            return PlatformDeleteResult.Failed;
        }

        // 拿到 200 但没有 cookie：会话在、凭证不可用 —— 按失败处理（保守：记录保留可重试），
        // 不能当成「已解绑」去清本地记录，否则平台上的作品会变成没人管的孤儿
        if (creds is null || string.IsNullOrWhiteSpace(creds.Cookie))
        {
            Log.Warning("[VideoArchive] 会话凭证为空 session={SessionId}", sessionId);
            return PlatformDeleteResult.Failed;
        }

        var cookie = creds.Cookie;
        // 环境快照随会话透传（2026-10-16）：删除请求的 UA/client hints 与登录 WebView2 一致
        var options = new PlatformOptions
        {
            Environment = CredentialFields.EnvironmentSnapshot(creds.ExtraDataJson),
            SessionId = sessionId,
        };
        try
        {
            switch (platformKey)
            {
                case PlatformNames.Douyin:
                {
                    var svc = new DouyinVideoService(options);
                    var result = await svc.DeleteWorkAsync(cookie, platformVideoId, ct);
                    return result switch
                    {
                        DeleteWorkResult.Success => PlatformDeleteResult.Success,
                        DeleteWorkResult.NotFound => PlatformDeleteResult.NotFound,
                        DeleteWorkResult.Unauthorized => PlatformDeleteResult.Unauthorized,
                        _ => PlatformDeleteResult.Failed,
                    };
                }

                case PlatformNames.WechatVideo:
                {
                    // deviceId 兜底生成（与发布同款）：WebView 提取失败时拿不到，
                    // 而删除接口不带 deviceId 会被判成陌生设备
                    var extra = CredentialFields.Extra(creds.ExtraDataJson);
                    var deviceId = extra.GetValueOrDefault("deviceId")?.ToString()
                        ?? extra.GetValueOrDefault("device_id")?.ToString() ?? "";
                    if (string.IsNullOrWhiteSpace(deviceId))
                        deviceId = Guid.NewGuid().ToString("N")[..32];

                    var finderId = CredentialFields.LocalStorageField(
                        creds.LocalStorageJson, "finder_username") ?? "";
                    var svc = new WechatVideoContentService(options)
                    {
                        FinderId = finderId,
                    };
                    var ok = await svc.DeleteAsync(cookie, deviceId, platformVideoId, ct);
                    return ok ? PlatformDeleteResult.Success : PlatformDeleteResult.Failed;
                }

                case PlatformNames.Kuaishou:
                {
                    // sig3/sig4 必须先预热签名宿主（模块只在页面 webpack 里，纯 C# 无法复算）。
                    await KuaishouSignerService.PrepareAsync(sessionId, ct);
                    // api_ph 在 cookie 里（不在 extra JSON）；老版 `ExtractCookieValue` 同款
                    var apiPh = CredentialFields.Extra(creds.ExtraDataJson)
                                    .GetValueOrDefault("apiPh")?.ToString()
                                ?? CredentialFields.CookieValue(cookie, "kuaishou.web.cp.api_ph");
                    var svc = new KuaishouVideoService(options);
                    // 端点分流 + 待发布/封面 Key 兜底都收在 SDK 里（DeleteSmartAsync）：
                    // 归档里存的 ID 可能是 publishId / workId / 早期兜底的 mediaId 三种，
                    // 页面层和任务层都不该各写一份「纯数字 = 待发布」的猜测
                    var ok = await svc.DeleteSmartAsync(cookie, platformVideoId, coverKey, title,
                        apiPh: apiPh, ct: ct);
                    return ok ? PlatformDeleteResult.Success : PlatformDeleteResult.Failed;
                }

                case PlatformNames.Xiaohongshu:
                {
                    var svc = new XhsVideoService(options);
                    var ok = await svc.DeleteAsync(cookie, platformVideoId, ct);
                    return ok ? PlatformDeleteResult.Success : PlatformDeleteResult.Failed;
                }

                case PlatformNames.Toutiao:
                {
                    // 横版 / 竖版两条端点（归档里没存视频类型）—— SDK 里先横后竖
                    var csrfToken = CredentialFields.Extra(creds.ExtraDataJson)
                        .GetValueOrDefault("csrfToken")?.ToString();
                    var svc = new ToutiaoVideoService(options);
                    var ok = await svc.DeleteAsync(cookie, platformVideoId, csrfToken, ct);
                    return ok ? PlatformDeleteResult.Success : PlatformDeleteResult.Failed;
                }

                case PlatformNames.Bilibili:
                    // B站视频删除要过 Geetest（抓包 340022）——不做验证码规避。
                    // 正常路径走不到这里：DeleteMode 对 B站 直接返回 LocalOnlyPlatformUnsupported。
                    Log.Warning("[VideoArchive] B站删除需要人机验证，按「不支持」处理");
                    return PlatformDeleteResult.Failed;

                default:
                    // 老版没有这条分支（未知平台静默走 Failed）—— 记一条日志，
                    // 否则将来加平台时「点了删除没反应」查不出来
                    Log.Warning("[VideoArchive] 未支持的平台 {Key}，无法删除平台侧作品", platformKey);
                    return PlatformDeleteResult.Failed;
            }
        }
        catch (KuaishouApiException ex)
        {
            // 快手失败是**抛异常**而不是返回 false（`KuaishouApiClient.cs:274`），
            // 与「返回 false」是同一件事：平台拒绝了这次删除
            Log.Warning("[VideoArchive] 快手删除被拒 {Id}：{Msg}", platformVideoId, ex.Message);
            return PlatformDeleteResult.Failed;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[VideoArchive] 平台删除异常 {Platform}/{Id}", platformKey, platformVideoId);
            return PlatformDeleteResult.Failed;
        }
    }
}
