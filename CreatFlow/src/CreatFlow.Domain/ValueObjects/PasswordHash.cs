namespace CreatFlow.Domain.ValueObjects;

/// <summary>
/// Represents a hashed password. Domain layer only stores the hash;
/// actual hashing is done by a service in Infrastructure layer.
/// </summary>
public sealed record PasswordHash
{
    public string Hash { get; }

    /// <summary>
    /// Create from an already-hashed string (used by Infrastructure when loading from DB).
    /// </summary>
    public PasswordHash(string hash)
    {
        ArgumentNullException.ThrowIfNull(hash, nameof(hash));

        if (string.IsNullOrWhiteSpace(hash))
        {
            throw new DomainException("Password hash cannot be empty.");
        }

        Hash = hash;
    }

    public static implicit operator string(PasswordHash passwordHash) => passwordHash.Hash;
    public override string ToString() => Hash;
}
