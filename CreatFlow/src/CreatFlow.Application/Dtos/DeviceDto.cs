namespace CreatFlow.Application.DTOs;

/// <summary>
/// Read model for device list.
/// </summary>
public sealed record DeviceDto(
    Guid Id,
    Guid MemberId,
    string MemberUsername,
    string DeviceName,
    string MachineId,
    string DeviceType,
    string? OsVersion,
    string? AppVersion,
    string? LastIp,
    DateTime? LastLoginAt,
    DateTime FirstLoginAt,
    bool IsActive,
    DateTime? KickedAt,
    string? KickedBy,
    DateTime CreatedAt);

/// <summary>
/// Request from desktop app to register/update a device.
/// </summary>
public sealed record DeviceRegisterRequest(
    string MachineId,
    string DeviceName,
    string DeviceType,
    string? OsVersion,
    string? AppVersion);

/// <summary>
/// Request to rename a device.
/// </summary>
public sealed record RenameDeviceRequest(string DeviceName);

/// <summary>
/// Request to kick out a device.
/// </summary>
public sealed record KickDeviceRequest(Guid DeviceId);
