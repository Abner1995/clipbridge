using CreatFlow.Domain.Entities;

namespace CreatFlow.Application.Common.Interfaces;

/// <summary>
/// Probes a platform session to check if stored credentials (cookie) are still valid.
/// Each platform has a different probing strategy.
/// </summary>
public interface IPlatformProber
{
    /// <summary>
    /// Probes the given session against the actual platform API/page.
    /// Returns the updated status and a human-readable probe result string.
    /// </summary>
    Task<(PlatformSessionStatus status, string result)> ProbeAsync(
        PlatformSession session, CancellationToken ct = default);
}
