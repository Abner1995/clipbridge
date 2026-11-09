namespace CreatFlow.NewApp.Services;

/// <summary>
/// 角色库云端同步编排（云端权威 + 本地缓存，2026-10-15 批次 8）：与
/// <see cref="AccountGroupCloudSync"/> 同构，独有差异 = **exists 语义**：
///
///   推：本地任一改动（<see cref="ComposeRolesService.Changed"/>）→ 整表 SaveAsync → 成功清 dirty
///   拉：启动/登录（App.OpenShellAfterLogin）+ 进 AI 设置页 → ListAsync →
///       · 本地 dirty → 先推、跳过本次拉取（推送失败也**不拉**：云端旧表不许盖掉未推改动）
///       · **云端未初始化（exists=false / 离线 null）→ 不落地**（本地内置示例/待推改动原样留）
///       · exists=true → 整体替换本地（**空库也落地** = 「删光角色」跨设备同步，不复活内置）
///       · 拉取期间用户又改了一笔（Revision 变了）→ 放弃本次落地
///   多设备并发 = 整份后写覆盖；离线照常可用、联网后补推。
///
/// **双闸（头号纪律）**：① App 层冒烟不接 Changed；② 本类入口 SmokeMode 直接返回 ——
/// 冒烟会造角色景并进 AI 设置页（真 HTTP），漏闸会把「冒烟房淘惠」推到本机真实登录的
/// 云端账号 / 把真实云端角色灌进 TEMP 夹具。
/// </summary>
public static class ComposeRoleCloudSync
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>拉取落地判定（纯函数，自检直接钉）：null = 离线/失败 → 不落地；
    /// exists=false = 云端未初始化 → 不落地；exists=true → 落地（含空表）。</summary>
    internal static bool ShouldLand(ComposeRoleListResponse? resp) => resp is { Exists: true };

    /// <summary>本地变更后推送（fire-and-forget；失败留 dirty，下次变更/拉取自动重试）</summary>
    public static async Task PushAsync(ComposeRolesService svc)
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
            Log.Debug(ex, "[ComposeRoles] 云端推送失败（留 dirty 待重试）");
        }
    }

    /// <summary>登录/启动/进 AI 设置页拉取（本地 dirty 时改为推送，先推不拉）</summary>
    public static async Task PullAsync(ComposeRolesService svc)
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
                var resp = await ComposeRoleApi.ListAsync();
                if (!ShouldLand(resp)) return;           // 失败/未登录：本地照常用
                if (svc.Revision != rev) return;         // 拉取期间本地又改了 → 放弃本次落地
                await svc.ReplaceFromCloudAsync(ComposeRoleApi.MapFromPull(resp!));
            }
            finally { Gate.Release(); }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[ComposeRoles] 云端拉取失败（本地照常）");
        }
    }

    /// <summary>持闸内部版（SemaphoreSlim 不可重入 —— PullAsync 已持闸时禁止再调 PushAsync）。</summary>
    private static async Task PushCoreAsync(ComposeRolesService svc)
    {
        await svc.EnsureLoadedAsync();
        if (!svc.IsDirty) return;
        var rev = svc.Revision;
        if (await ComposeRoleApi.SaveAsync(svc.Roles))
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
