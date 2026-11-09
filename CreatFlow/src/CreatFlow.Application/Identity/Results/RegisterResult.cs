namespace CreatFlow.Application.Identity.Results;

public record RegisterResult(
    bool Success,
    string? Error,
    MemberDto? Member,
    string? Token = null,
    string? ClientSecret = null);
