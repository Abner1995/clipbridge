using CreatFlow.NewApp.Models;
using CreatFlow.NewApp.Services;

namespace CreatFlow.NewApp.ViewModels;

/// <summary>
/// 视频管理页列表里的一行 —— **天头行与记录行是同一个类型**，靠 <see cref="IsDayHeader"/> 分叉。
///
/// 为什么不用 DataTemplateSelector：这一页只有两种行，为它引一套模板选择机制
/// （外加 x:DataType 两套模板）不值当；IsVisible 分叉是这个项目里到处在用的老办法
/// （队列页的展开明细、发布页的四视图）。
///
/// 展示字段是**就地更新**的（<see cref="Apply"/>）—— 本页每 4 秒会重查一次
/// （有记录在后台删时），换对象就等于把勾选态、确认条、滚动位置全冲掉。
/// </summary>
public sealed partial class VideoArchiveRow : ObservableObject
{
    // ═══ 行身份 ═══

    /// <summary>
    /// 行身份：记录行 = 平台记录 id，天头行 = 天标签。刷新时按它对上号**复用实例**。
    /// </summary>
    public required string RowKey { get; init; }

    // ═══ 天头行 ═══

    public bool IsDayHeader { get; init; }

    /// <summary>天头整行文案：`今天 · 12 条`（当天条数会变，故可写）</summary>
    [ObservableProperty] private string _dayHeaderText = "";
    public bool IsRecord => !IsDayHeader;

    // ═══ 记录行 ═══

    /// <summary>服务端那一行（删除要用它的 PlatformId / SessionId / PlatformVideoId）</summary>
    [ObservableProperty] private VideoArchiveItem? _item;

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _subText = "";
    [ObservableProperty] private string _timeText = "";
    [ObservableProperty] private string _platformName = "";
    [ObservableProperty] private string _brandHex = "#6C5CE7";
    /// <summary>平台方块里的那个字（零 emoji：用字，不用图标）</summary>
    [ObservableProperty] private string _initial = "";

    /// <summary>确认条文案 —— 随「能不能连平台一起删」变（这是本页最要紧的一句话）</summary>
    [ObservableProperty] private string _confirmText = "";

    // ═══ 多选 ═══

    /// <summary>
    /// 勾选态。**只有记录行能勾**（天头行没有 Item，勾了也没东西可删）——
    /// 模板里靠 <see cref="ShowCheckbox"/> 控制显隐。
    /// </summary>
    [ObservableProperty] private bool _isSelected;

    public bool ShowCheckbox => IsRecord && !IsDeleting;

    // ═══ 删除态 ═══

    /// <summary>确认条是否展开（同一时刻只开一张卡，由页面 VM 保证）</summary>
    [ObservableProperty] private bool _isConfirmingDelete;
    /// <summary>本行正在删（按钮转圈/禁用）</summary>
    [ObservableProperty] private bool _isDeleting;

    public bool ShowConfirmStrip => IsConfirmingDelete;

    partial void OnIsConfirmingDeleteChanged(bool value) => OnPropertyChanged(nameof(ShowConfirmStrip));

    /// <summary>
    /// 「删除这条视频全部副本」的确认条。与单条确认条**互斥**（同一时刻只开一个，
    /// 由页面 VM 保证）—— 两条并存会让「确认删除」到底删哪个变得说不清。
    /// </summary>
    [ObservableProperty] private bool _isConfirmingWholeVideo;

    /// <summary>这条视频在平台上共有几个副本。点「全部副本」查完才有值，确认条文案要引用它</summary>
    [ObservableProperty] private int _wholeVideoCount;

    /// <summary>确认条正文（含副本数，「含本页之外的」这句不能省）</summary>
    [ObservableProperty] private string _wholeVideoText = "";

    /// <summary>查询中（按了「全部副本」还没拿到数）</summary>
    [ObservableProperty] private bool _isCountingWholeVideo;

    public bool ShowWholeVideoStrip => IsConfirmingWholeVideo;
    public string WholeVideoButtonText => VideoArchiveFormat.WholeVideoButtonText(WholeVideoCount);

    partial void OnIsConfirmingWholeVideoChanged(bool value) =>
        OnPropertyChanged(nameof(ShowWholeVideoStrip));

    partial void OnWholeVideoCountChanged(int value) => OnPropertyChanged(nameof(WholeVideoButtonText));

    partial void OnIsDeletingChanged(bool value) => OnPropertyChanged(nameof(ShowCheckbox));

    /// <summary>天头行（key = 天标签；条数随刷新就地更新）</summary>
    public static VideoArchiveRow Header(string dayLabel, int count)
    {
        var row = new VideoArchiveRow { RowKey = dayLabel, IsDayHeader = true };
        row.ApplyHeader(count);
        return row;
    }

    /// <summary>天头行的条数更新（`今天 · 3 条` → `今天 · 2 条`）</summary>
    public void ApplyHeader(int count) =>
        DayHeaderText = VideoArchiveFormat.DayHeaderText(RowKey, count);

    /// <summary>
    /// 记录行。账号名由调用方解析后传进来（解析不出 = 会话已解绑，
    /// 见 <see cref="AccountLabels.LabelOf"/>），平台中文名/品牌色来自平台配置。
    /// </summary>
    public static VideoArchiveRow Record(
        VideoArchiveItem item, string? accountLabel, string platformName, string brandHex,
        bool sessionKnown = true)
    {
        var row = new VideoArchiveRow { RowKey = item.PlatformId.ToString("N"), IsDayHeader = false };
        row.Apply(item, accountLabel, platformName, brandHex, sessionKnown);
        return row;
    }

    /// <summary>按服务端那一行**就地更新**（勾选/确认条这些交互态一概不碰）</summary>
    public void Apply(VideoArchiveItem item, string? accountLabel, string platformName,
        string brandHex, bool sessionKnown = true)
    {
        var account = VideoArchiveFormat.AccountText(accountLabel);
        Item = item;
        Title = VideoArchiveFormat.DisplayTitle(item.Title, item.CommonTitle, item.VideoFileName);
        SubText = VideoArchiveFormat.SubLine(platformName, account, item.VideoFileSize, item.PlatformVideoId);
        TimeText = VideoArchiveFormat.TimeText(item.CreatedAt);
        PlatformName = platformName;
        BrandHex = brandHex;
        Initial = platformName.Length > 0 ? platformName[..1] : "?";
        // 「能不能删平台侧」要同时看会话 id 与**会话是否还在服务端**（见 DeleteMode 的注释）：
        // 记录上留着已失效的 sessionId 时，确认条不能承诺删平台上的东西
        ConfirmText = VideoArchiveFormat.DeleteStripText(VideoArchiveFormat.DeleteMode(
            item.PlatformKey, item.PlatformSessionId, item.PlatformVideoId,
            sessionKnown: sessionKnown));
    }
}

/// <summary>
/// 顶部平台筛选芯片（单选：全部 / 抖音 / 快手 / 视频号 / 小红书）。
///
/// 用 Button 而不是 ToggleButton：这是一组**单选**，用 ToggleButton 得自己防「再点一下取消选中」，
/// 而 Semi 的 ToggleButton 还会在 :checked 下把 Foreground 刷成白色（自绘芯片的老坑）。
/// 配色完全由 VM 给（选中 = 品牌紫淡底 + 深紫字），控件不自作主张。
/// </summary>
public sealed partial class PlatformFilterChip : ObservableObject
{
    /// <summary>平台 key；**空串 = 全部**</summary>
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string BrandHex { get; init; }

    [ObservableProperty] private int _count;
    [ObservableProperty] private bool _isOn;

    /// <summary>芯片文字：`全部 231` / `抖音 96`</summary>
    public string Text => VideoArchiveFormat.ChipText(Name, Count);

    partial void OnCountChanged(int value) => OnPropertyChanged(nameof(Text));

    // ── 配色（选中 = 品牌淡紫底/深紫字，未选 = 白底/次级字）──

    public string BgHex => IsOn ? "#F4F1FE" : "White";
    public string BorderHex => IsOn ? "#D8D2F8" : "#EDEFF3";
    public string FgHex => IsOn ? "#4B3BC3" : "#4E5969";

    partial void OnIsOnChanged(bool value)
    {
        OnPropertyChanged(nameof(BgHex));
        OnPropertyChanged(nameof(BorderHex));
        OnPropertyChanged(nameof(FgHex));
    }
}

/// <summary>
/// 底栏「每页 20 / 50 / 100」的一格 —— 与 <see cref="PlatformFilterChip"/> 同款
/// （同样是 Button + VM 给配色，同样避免自己拼三套 bool→颜色 的转换）。
/// </summary>
public sealed partial class PageSizeChip : ObservableObject
{
    public required int Size { get; init; }
    public string Text => Size.ToString();

    [ObservableProperty] private bool _isOn;

    public string BgHex => IsOn ? "#F4F1FE" : "White";
    public string BorderHex => IsOn ? "#D8D2F8" : "#EDEFF3";
    public string FgHex => IsOn ? "#4B3BC3" : "#4E5969";

    partial void OnIsOnChanged(bool value)
    {
        OnPropertyChanged(nameof(BgHex));
        OnPropertyChanged(nameof(BorderHex));
        OnPropertyChanged(nameof(FgHex));
    }
}
