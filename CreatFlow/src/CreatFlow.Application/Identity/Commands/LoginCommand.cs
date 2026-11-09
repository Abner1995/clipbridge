namespace CreatFlow.Application.Identity.Commands;

/// <summary>
/// Login command with optional device info for automatic device registration.
/// </summary>
public record LoginCommand(
    string Username,
    string Password,
    string? MachineId = null,
    string? DeviceName = null,
    string? DeviceType = null,
    string? OsVersion = null,
    string? AppVersion = null);
