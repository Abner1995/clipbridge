namespace CreatFlow.NewApp.Services;

/// <summary>
/// 账号分组云端同步编排（云端权威 + 本地缓存，2026-10-15 用户拍板「双向同步」）：
///
///   推：本地任一改动（<see cref="AccountGroupsService.Changed"/>）→ 整表 SaveAsync → 成功清 dirty
///   拉：启动/登录（App.OpenShellAfterLogin）+ 进平台页 → ListAsync → 整体替换本地
///       · 本地 dirty → 先推、跳过本次拉取（推送失败也**不拉**：云端旧表不许盖掉未推改动）
///       · 拉取期间用户又改了一笔（Revision 变了）→ 放弃本次落地
///   多设备并发 = 整份后写覆盖（小数据量够用）；离线照常可用、联网后补推。
///
/// **双闸（头号纪律）**：① App 层冒烟不接 Changed；② 本类入口 SmokeMode 直接返回 ——
/// 冒烟会造分组景并走平台页 LoadAsync（真 HTTP），漏闸会把「冒烟宠物」推到本机真实登录的
/// 云端账号 / 把真实云端分组灌进 TEMP 夹具。
/// </summary>
public static class AccountGroupCloudSync
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>本地变更后推送（fire-and-forget；失败留 dirty，下次变更/拉取自动重试）</summary>
    public static async Task PushAsync(AccountGroupsService svc)
    {
        if (!CanSync()) return;
        try
        {
            await Gate.WaitAsync();
            try { await PushCoreAsync(svc); }
            finally { Gate.Release(); }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[AccountGroups] 云端推送失败（留 dirty 待重试）");
        }
    }

    /// <summary>登录/启动/进平台页拉取（本地 dirty 时改为推送，先推不拉）</summary>
    public static async Task PullAsync(AccountGroupsService svc)
    {
        if (!CanSync()) return;
        try
        {
            await Gate.WaitAsync();
            try
            {
                await svc.EnsureLoadedAsync();
                if (svc.IsDirty)
                {
                    // 未推送的本地改动优先：先推（云端旧数据不许反向盖掉）；推送失败也不拉，
                    // 留给下次变更/拉取自动重试
                    await PushCoreAsync(svc);
                    return;
                }

                var rev = svc.Revision;
                var groups = await AccountGroupApi.ListAsync();
                if (groups is null) return;              // 失败/未登录：本地照常用
                if (svc.Revision != rev) return;         // 拉取期间本地又改了 → 放弃本次落地
                await svc.ReplaceFromCloudAsync(groups);
            }
            finally { Gate.Release(); }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[AccountGroups] 云端拉取失败（本地照常）");
        }
    }

    /// <summary>持闸内部版（SemaphoreSlim 不可重入 —— PullAsync 已持闸时禁止再调 PushAsync）。</summary>
    private static async Task PushCoreAsync(AccountGroupsService svc)
    {
        await svc.EnsureLoadedAsync();
        if (!svc.IsDirty) return;
        var rev = svc.Revision;
        if (await AccountGroupApi.SaveAsync(svc.Groups))
            await svc.ClearDirtyAsync(rev);
    }

    /// <summary>闸⓪：单机版没有云端（本地档即权威）；闸①：冒烟零网络；闸②：未登录（无 token / 空会员）不动。</summary>
    private static bool CanSync()
    {
        if (AppConfig.IsStandalone) return false;
        if (App.SmokeMode) return false;
        var s = TokenStorage.Load();
        return s is not null && s.MemberId != Guid.Empty && !string.IsNullOrWhiteSpace(s.Token);
    }
}
