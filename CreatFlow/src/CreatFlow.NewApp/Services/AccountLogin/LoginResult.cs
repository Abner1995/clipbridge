namespace CreatFlow.NewApp.Services.AccountLogin;

/// <summary>登录提取结果(供调用方 sync 服务端;自老版 LoginResult 移植,Phase 1 抽出入 Services)</summary>
public sealed class LoginResult
{
    public bool Success { get; init; }
    public string? Cookie { get; init; }
    public string? LocalStorageJson { get; init; }
    public string? PlatformUid { get; init; }
    public string? PlatformName { get; init; }
    public string? PlatformAvatar { get; init; }
    public string? ExtraDataJson { get; init; }
    public string? Error { get; init; }
}
