using CreatFlow.NewApp.Models;
using CreatFlow.NewApp.Services.AccountLogin;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 平台会话服务端调用集中点 —— 平台账号页与账号标签坞共用:
///
///   sync(Id 不存在)= 登录完成首次建档 —— 会话行只在真实登录同步后才落库,
///                     ID 由客户端先行生成(档案目录名 = 会话 ID,WebView2 早于登录完成创建)
///   sync(Id 已存在)= 重登写凭据更新(含服务端记录被管理端删除后的同 ID 补建)
///   delete         = 解绑删除 / 未登录取消的兜底清理
/// </summary>
public static class PlatformSessionApi
{
    /// <summary>登录成功后写凭据(首次建档 / 重登更新共用),返回服务端最新记录;失败返回 null</summary>
    public static Task<PlatformSessionSyncResult?> SyncCredentialsAsync(
        Guid id, string platformKey, LoginResult result) =>
        ApiClient.PostAsync<PlatformSessionSyncResult>("/api/platform-sessions/sync",
            new SyncSessionRequest(
                Id: id,
                PlatformKey: platformKey,
                PlatformUid: result.PlatformUid,
                PlatformName: result.PlatformName,
                PlatformAvatar: result.PlatformAvatar,
                Cookie: result.Cookie,
                LocalStorageJson: result.LocalStorageJson,
                ExtraDataJson: result.ExtraDataJson));

    /// <summary>解绑：服务端硬删该会话的本端记录 + 会话行，返回清理条数（平台侧内容不动）。
    /// 网络/空响应返回 null；4xx/5xx 抛 ApiException；未登录取消的兜底清理也走这里（忽略返回）</summary>
    public static Task<PlatformSessionDeleteResult?> DeleteSessionAsync(Guid id) =>
        ApiClient.PostAsync<PlatformSessionDeleteResult>(
            "/api/platform-sessions/delete", new SessionIdRequest(id));

    /// <summary>取会话凭证(Cookie 等) —— SDK 调用与「档案页会话恢复」共用;无记录返回 null</summary>
    public static Task<PlatformSessionCredentials?> GetCredentialsAsync(Guid id) =>
        ApiClient.PostAsync<PlatformSessionCredentials>("/api/platform-sessions/credentials",
            new GetCredentialsRequest(id));
}
