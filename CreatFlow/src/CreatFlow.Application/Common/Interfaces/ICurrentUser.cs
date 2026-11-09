namespace CreatFlow.Application.Common.Interfaces;

/// <summary>
/// Provides the currently authenticated admin identity.
/// Resolved per-scope so the AuditLogEventHandler sees who performed the action.
/// </summary>
public interface ICurrentUser
{
    /// <summary>The current admin's ID, or null if not authenticated.</summary>
    AdminId? Id { get; }

    /// <summary>The current admin's username, or "系统" if not authenticated.</summary>
    string Username { get; }
}
