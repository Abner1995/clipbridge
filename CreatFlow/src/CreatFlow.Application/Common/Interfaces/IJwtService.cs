namespace CreatFlow.Application.Common.Interfaces;

/// <summary>
/// JWT token generation. Implemented in Infrastructure (System.IdentityModel.Tokens.Jwt).
/// </summary>
public interface IJwtService
{
    string GenerateToken(Guid memberId, string username);

    /// <summary>
    /// Derives a per-user ClientSecret from the server-side JWT secret.
    /// Used by the desktop app to sign CB-SigV1 API requests after login.
    /// </summary>
    string GenerateClientSecret(Guid memberId);
}
