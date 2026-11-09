namespace CreatFlow.Application.Common.Interfaces;

/// <summary>
/// Password hashing abstraction. Domain layer only stores the hash;
/// actual BCrypt/Argon2 hashing is implemented in Infrastructure.
/// </summary>
public interface IPasswordHasher
{
    /// <summary>Hash a plain-text password.</summary>
    string Hash(string plainPassword);

    /// <summary>Verify a plain-text password against a stored hash.</summary>
    bool Verify(string plainPassword, string hashedPassword);
}
