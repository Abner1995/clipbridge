namespace CreatFlow.Domain.Entities;

public class Admin : Aggregate<AdminId>
{
    public Username Username { get; private set; } = default!;
    public PasswordHash PasswordHash { get; private set; } = default!;
    public PhoneNumber? PhoneNumber { get; private set; }
    public Email Email { get; private set; } = default!;
    public bool IsSuperAdmin { get; private set; }

    private Admin() { }

    /// <summary>
    /// Creates a new admin. Prefers an already-hashed password.
    /// </summary>
    public static Admin Create(AdminId id, Username username, PasswordHash passwordHash, Email email, PhoneNumber? phoneNumber = null, bool isSuperAdmin = false)
    {
        ArgumentNullException.ThrowIfNull(username);
        ArgumentNullException.ThrowIfNull(passwordHash);
        ArgumentNullException.ThrowIfNull(email);

        var now = Clock.Now;
        var admin = new Admin
        {
            Id = id,
            Username = username,
            PasswordHash = passwordHash,
            Email = email,
            PhoneNumber = phoneNumber,
            IsSuperAdmin = isSuperAdmin,
            CreatedAt = now,
            LastModified = now,
        };
        admin.AddDomainEvent(new AdminCreatedEvent(id, username.Value));
        return admin;
    }

    /// <summary>
    /// Toggles the super-admin flag. Only another super admin should call this.
    /// </summary>
    public void SetSuperAdmin(bool isSuperAdmin)
    {
        IsSuperAdmin = isSuperAdmin;
        LastModified = Clock.Now;
    }

    /// <summary>
    /// Changes the admin password.
    /// </summary>
    public void ChangePassword(PasswordHash newPasswordHash)
    {
        ArgumentNullException.ThrowIfNull(newPasswordHash);
        PasswordHash = newPasswordHash;
        LastModified = Clock.Now;
        AddDomainEvent(new AdminPasswordChangedEvent(Id, Username.Value));
    }

    /// <summary>
    /// Updates profile contact info (email / phone).
    /// Pass null to keep the current value.
    /// </summary>
    public void UpdateProfile(Email? email = null, PhoneNumber? phoneNumber = null)
    {
        if (email is not null) Email = email;
        if (phoneNumber is not null) PhoneNumber = phoneNumber;
        LastModified = Clock.Now;
        AddDomainEvent(new AdminUpdatedEvent(Id, Username.Value));
    }
}
