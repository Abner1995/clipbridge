namespace CreatFlow.Application.Identity.Results;

/// <summary>
/// Activation result. Returns updated member info, duration added, and client secret.
/// </summary>
public record ActivateResult(
    bool Success,
    string? Error,
    MemberDto? Member,
    string? ClientSecret = null,
    int DurationDays = 0);
