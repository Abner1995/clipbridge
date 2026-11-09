namespace CreatFlow.NewApp.Services;

/// <summary>
/// 视频水印偏好（账号设置页「视频水印」卡）—— 开关 / 文字 / 位置。
/// 默认开 + 「房淘惠」+ 右上；老档缺失或坏档都落回默认（默认开启是用户拍板的行为）。
/// </summary>
public sealed record WatermarkSettings
{
    public bool Enabled { get; init; } = true;
    public string Text { get; init; } = "房淘惠";
    public WatermarkPosition Position { get; init; } = WatermarkPosition.TopRight;

    public static WatermarkSettings Default { get; } = new();

    /// <summary>转成执行快照（ComposeJob / ComposeTaskData 用同一份规格）</summary>
    public WatermarkSpec ToSpec() => new(Enabled, Text, Position);
}

/// <summary>
/// 水印偏好的本地持久化 —— **会员级** <c>accounts/{memberId:N}/watermark-settings.json</c>
/// （2026-10-21；与角色库/账号分组同「换号不串品牌」模型）。文字是用户的品牌，机器级共享
/// 会让 A 会员的水印出现在 B 会员的视频里（批次 7 修过的同一类问题）。
/// 文件不存在/读坏 → 默认（开 / 房淘惠 / 右上）；不做旧档采纳（新功能没有历史档）。
/// 冒烟换 TEMP 根（App.axaml.cs DI 注册）。
/// </summary>
public sealed class WatermarkSettingsStore
{
    private const string FileName = "watermark-settings.json";

    private readonly LocalJsonFile _file;
    private readonly Func<Guid> _memberId;

    /// <param name="root">状态根（测试/冒烟注入临时目录；生产 null = %LocalAppData%\CreatFlow）</param>
    /// <param name="memberId">当前会员提供者（DI 传 SessionContext；默认 Guid.Empty = no-member）</param>
    public WatermarkSettingsStore(string? root = null, Func<Guid>? memberId = null)
    {
        _file = new LocalJsonFile(root ?? LocalJsonFile.DefaultRoot, "Watermark");
        _memberId = memberId ?? (() => Guid.Empty);
    }

    public Guid CurrentMemberId => _memberId();

    /// <summary>会员档路径（自检断言用）</summary>
    public string GetConfigPath(Guid memberId)
        => _file.PathOf(LocalJsonFile.MemberRelative(memberId, FileName));

    public async Task<WatermarkSettings> LoadAsync(CancellationToken ct = default)
    {
        var data = await _file.ReadAsync<WatermarkSettings>(GetConfigPath(_memberId()), ct)
            .ConfigureAwait(false);
        return data is null ? WatermarkSettings.Default : Normalize(data);
    }

    /// <summary>按**载入时的会员**写盘（换号挂起/登出期间不会写到新会员档）。
    /// 返回 false = 写盘失败（账号页水印卡据此显示「保存失败」）。</summary>
    public async Task<bool> SaveAsync(Guid memberId, WatermarkSettings settings, CancellationToken ct = default)
        => await _file.TryWriteAsync(GetConfigPath(memberId), Normalize(settings), ct).ConfigureAwait(false);

    public Task<bool> SaveAsync(WatermarkSettings settings, CancellationToken ct = default)
        => SaveAsync(_memberId(), settings, ct);

    /// <summary>文字空白折成单个空格、最多 20 字；空文字保留为空（= 不烧，由 UI 提示）。
    /// 与 <see cref="WatermarkSpec.EffectiveText"/> 同口径 —— 界面显示、存档与烧录内容一致。</summary>
    private static WatermarkSettings Normalize(WatermarkSettings s)
    {
        var text = string.Join(" ", (s.Text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return s with { Text = text.Length <= 20 ? text : text[..20] };
    }
}
