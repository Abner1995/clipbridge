using CreatFlow.Application.Common.Interfaces;
using CreatFlow.Domain.Entities;

namespace CreatFlow.Application.Common.Probes;

/// <summary>
/// 复合探活器 — 持有一组平台探活器，按 session.PlatformKey 分发到匹配的实现。
///
/// 探活器自己通过 PlatformKey 判断是否处理（返回 "skipped" 表示不处理），
/// Composite 依次尝试，取第一个非 "skipped" 的结果。
/// 所有探活器都返回 "skipped" → 原样返回 session.Status。
///
/// ⚠️ 本文件为什么在 Application（2026-10-28，单机版接入时从 Infrastructure 搬来）：
/// 探活实现必须被**联网版 Infrastructure 与单机版 LocalStore 共用**（规则只有一份）。
/// 放在 Platforms 会把 Application 拖进老版 App 的引用闭包（Platforms → Application →
/// `CreatFlow.Application` 命名空间与老版 `App : Application` 撞名，CS0118）；
/// 放在 Infrastructure 则单机版够不着。Application 已是两版共同依赖，且探活只用 BCL
/// HttpClient + PlatformNames，不碰任何持久化 / 服务端细节，放这里是代价最小的共享点。
/// 真正要护的协议资产仍在 Platforms。
/// </summary>
public class CompositePlatformProber : IPlatformProber
{
    private readonly IReadOnlyList<IPlatformProber> _probers;

    public CompositePlatformProber(IEnumerable<IPlatformProber> probers)
    {
        _probers = probers.ToList();
    }

    public async Task<(PlatformSessionStatus status, string result)> ProbeAsync(
        PlatformSession session, CancellationToken ct = default)
    {
        foreach (var prober in _probers)
        {
            var (status, result) = await prober.ProbeAsync(session, ct);
            if (result != "skipped")
                return (status, result);
        }

        // No prober handled this platform — keep current status
        return (session.Status, "no_prober");
    }
}
