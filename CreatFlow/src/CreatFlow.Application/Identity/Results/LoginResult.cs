namespace CreatFlow.Application.Identity.Results;

/// <summary>
/// Login / change-password result with optional JWT and ClientSecret.
/// ClientSecret is used by the desktop app to sign subsequent API requests (CB-SigV1).
/// </summary>
public record LoginResult(
    bool Success,
    string? Token,
    string? Error,
    MemberDto? Member,
    string? ClientSecret = null);
