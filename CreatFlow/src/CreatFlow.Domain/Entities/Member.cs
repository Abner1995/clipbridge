namespace CreatFlow.Domain.Entities;

using CreatFlow.Domain.Events;

public class Member : Aggregate<MemberId>
{
    public Username Username { get; private set; } = default!;
    public PasswordHash PasswordHash { get; private set; } = default!;
    public PhoneNumber PhoneNumber { get; private set; } = default!;
    public Email Email { get; private set; } = default!;
    public DateTime ExpireDate { get; private set; }
    public bool IsActive { get; private set; } = true;

    /// <summary>最近登录IP</summary>
    public string? LastLoginIp { get; private set; }

    /// <summary>最近登录时间</summary>
    public DateTime? LastLoginAt { get; private set; }

    private Member() { }

    /// <summary>
    /// Creates a new member.
    /// When <paramref name="durationDays"/> is 0, the member starts in an
    /// unactivated (expired) state and must use an activation code to gain access.
    /// When &gt; 0, the member is created with active membership (e.g. self-registration
    /// with a valid activation code).
    /// </summary>
    public static Member Create(MemberId id, Username username, PasswordHash passwordHash, PhoneNumber? phoneNumber, Email? email, int durationDays)
    {
        ArgumentNullException.ThrowIfNull(username);
        ArgumentNullException.ThrowIfNull(passwordHash);
        ArgumentOutOfRangeException.ThrowIfNegative(durationDays);

        var now = Clock.Now;
        var expireDate = durationDays > 0 ? now.AddDays(durationDays) : now;
        var member = new Member
        {
            Id = id,
            Username = username,
            PasswordHash = passwordHash,
            Email = email,
            PhoneNumber = phoneNumber,
            ExpireDate = expireDate,
            IsActive = true
        };

        member.CreatedAt = now;
        member.LastModified = now;

        if (durationDays > 0)
            member.AddDomainEvent(new MemberActivatedEvent(member.Id, member.ExpireDate));

        return member;
    }

    /// <summary>
    /// Returns true if the membership has expired.
    /// </summary>
    public bool IsExpired() => Clock.Now > ExpireDate;

    /// <summary>
    /// Admin disables this member remotely (does not delete data).
    /// </summary>
    public void Disable()
    {
        if (!IsActive) return;

        IsActive = false;
        LastModified = Clock.Now;
        AddDomainEvent(new MemberDisabledEvent(Id));
    }

    /// <summary>
    /// Admin re-enables a previously disabled member.
    /// </summary>
    public void Enable()
    {
        if (IsActive) return;

        IsActive = true;
        LastModified = Clock.Now;
        AddDomainEvent(new MemberEnabledEvent(Id));
    }

    /// <summary>
    /// Extends membership by the given number of days.
    /// If the membership is currently expired, days are added from now;
    /// otherwise they are added to the current ExpireDate.
    /// </summary>
    public void Extend(int days)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(days);

        var now = Clock.Now;
        ExpireDate = (ExpireDate < now ? now : ExpireDate).AddDays(days);
        LastModified = now;
        AddDomainEvent(new MemberExtendedEvent(Id, ExpireDate));
    }

    /// <summary>
    /// Changes the login password.
    /// </summary>
    public void ChangePassword(PasswordHash newPasswordHash)
    {
        ArgumentNullException.ThrowIfNull(newPasswordHash);
        PasswordHash = newPasswordHash;
        LastModified = Clock.Now;
    }

    /// <summary>
    /// Admin manually sets a new expire date (for corrections or test data).
    /// </summary>
    public void UpdateExpireDate(DateTime newExpireDate)
    {
        ExpireDate = newExpireDate;
        LastModified = Clock.Now;
    }

    /// <summary>
    /// Updates profile contact information (phone / email).
    /// Pass null to keep the current value.
    /// </summary>
    public void UpdateProfile(PhoneNumber? phoneNumber, Email? email)
    {
        if (phoneNumber is not null) PhoneNumber = phoneNumber;
        if (email is not null) Email = email;
        LastModified = Clock.Now;
        AddDomainEvent(new MemberUpdatedEvent(Id, Username.Value));
    }

    /// <summary>
    /// Records login IP and time.
    /// </summary>
    public void RecordLogin(string? ip)
    {
        LastLoginIp = ip;
        LastLoginAt = Clock.Now;
        LastModified = Clock.Now;
        AddDomainEvent(new MemberLoggedInEvent(Id, Username.Value, ip, LastLoginAt.Value));
    }
}
