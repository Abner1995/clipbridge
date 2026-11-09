namespace CreatFlow.NewApp.Services;

/// <summary>
/// 提示音偏好的存档形状（一个音量百分比，2026-10-22 用户需求「滑块控制」）。
/// 0 = 关闭，100 = 满音量（原行为）。
/// </summary>
public sealed record SoundSettings
{
    /// <summary>音量百分比 0~100（0 = 关闭）</summary>
    public int VolumePercent { get; init; } = SoundSettingsStore.DefaultVolumePercent;
}

/// <summary>
/// 提示音音量的本地持久化 —— **会员级** <c>accounts/{memberId:N}/sound-settings.json</c>
/// （与视频水印同「换号不串偏好」模型；声音偏好跟着账号走，换人登录不沿用上一位的静音设置）。
/// 文件不存在/读坏/超出 0~100 → 默认 <see cref="DefaultVolumePercent"/>；新功能没有历史档，不做采纳。
/// 冒烟换 TEMP 根（App.axaml.cs DI 注册）。
/// </summary>
public sealed class SoundSettingsStore
{
    /// <summary>默认音量百分比 —— 70%（既不是静音也不是满音量，用户可往两边拖）</summary>
    public const int DefaultVolumePercent = 70;

    /// <summary>滑块上限（也是存档合法上界）</summary>
    public const int MaxVolumePercent = 100;

    private const string FileName = "sound-settings.json";

    private readonly LocalJsonFile _file;
    private readonly Func<Guid> _memberId;

    /// <param name="root">状态根（测试/冒烟注入临时目录；生产 null = %LocalAppData%\CreatFlow）</param>
    /// <param name="memberId">当前会员提供者（DI 传 SessionContext；默认 Guid.Empty = no-member）</param>
    public SoundSettingsStore(string? root = null, Func<Guid>? memberId = null)
    {
        _file = new LocalJsonFile(root ?? LocalJsonFile.DefaultRoot, "SoundSettings");
        _memberId = memberId ?? (() => Guid.Empty);
    }

    public Guid CurrentMemberId => _memberId();

    /// <summary>会员档路径（自检断言用）</summary>
    public string GetConfigPath(Guid memberId)
        => _file.PathOf(LocalJsonFile.MemberRelative(memberId, FileName));

    public async Task<SoundSettings> LoadAsync(CancellationToken ct = default)
    {
        var data = await _file.ReadAsync<SoundSettings>(GetConfigPath(_memberId()), ct)
            .ConfigureAwait(false);
        return Normalize(data);
    }

    /// <summary>按**载入时的会员**写盘（换号挂起/登出期间不会写到新会员档）。
    /// 返回 false = 写盘失败（账号页提示用）。</summary>
    public async Task<bool> SaveAsync(Guid memberId, SoundSettings settings, CancellationToken ct = default)
        => await _file.TryWriteAsync(GetConfigPath(memberId), Normalize(settings), ct)
            .ConfigureAwait(false);

    public Task<bool> SaveAsync(SoundSettings settings, CancellationToken ct = default)
        => SaveAsync(_memberId(), settings, ct);

    /// <summary>缺档/坏档 → 默认；手改成 0~100 之外的值也退默认，不静默钳制 ——
    /// 与日志保留设置同一取舍（钳出来的「999 → 100」会让用户以为设置生效了）。</summary>
    private static SoundSettings Normalize(SoundSettings? s)
        => s is null || s.VolumePercent is < 0 or > MaxVolumePercent
            ? new SoundSettings()
            : s;
}
