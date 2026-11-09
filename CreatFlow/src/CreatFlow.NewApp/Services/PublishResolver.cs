using CreatFlow.Platforms.Core;
using CreatFlow.Platforms.Douyin;
using CreatFlow.Platforms.Kuaishou;
using CreatFlow.Platforms.WechatVideo;
using CreatFlow.Platforms.Xiaohongshu;

namespace CreatFlow.NewApp.Services;

// ═══════════════════════════════════════════════════════════════════════════
//  联邦解析器（设计文档 §4.4）—— 一个概念，问 N 个平台。
//
//  话题扇出 3 个平台（视频号纯文本，不查），位置扇出 4 个。
//
//  **四条设计铁律**（照抄文档，改代码前先读）：
//   1. 不做实体对齐。四个平台是四个独立 POI 库，ID 之间无对应关系；
//      一次搜索、多路扇出、每路各自显示 —— 用户一眼能确认每个平台实际命中了什么。
//   2. 未匹配不清空、不删除。话题未匹配 → 保留纯文本（老版是从正文里 Regex.Replace
//      抹掉，用户输入被静默篡改，缺陷 L2）。
//   3. 视频号要有一行，标「纯文本，无需匹配」—— 老版话题下拉只有三家，
//      视频号的话题在 UI 里根本选不到（缺陷 L3）。
//   4. 解析结果按（平台 + 关键词）缓存，二次输入直接命中。
//
//  扇出粒度 = **每平台一行**（2026-09-19 改）。原先是每 (平台 × 账号) 一行，理由是
//  「万一 ID 随账号变，宁可多查几次也不能发错 ID」（设计文档 T3）。T3 已实测关闭：
//  同平台两个账号拿到的 cid / poiId **完全相同**（抖音 1568360053346369、快手 228572829、
//  小红书 B02001A1VA、视频号 qqmap_…），所以按账号分行只是把同一份结果印 N 遍。
//  多账号的容错改为「同平台换着试」：第一个账号查不到就换下一个，坏凭证不至于让整行未命中。
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>一个待解析的目标（平台 × 账号）—— 自带展示信息，免去 UI 再查一次。</summary>
public sealed record ResolutionTarget(
    Guid SessionId,
    string PlatformKey,
    string AccountLabel,
    string BrandHex,
    string DisplayName);

/// <summary>一个候选（行内可换）—— 主/副文案 + 平台侧载荷。候选列表里要标出「当前用的那个」。</summary>
public abstract partial class ResolutionCandidate : ObservableObject
{
    protected ResolutionCandidate(string id, string primary, string secondary)
    {
        Id = id;
        Primary = primary;
        Secondary = secondary;
    }

    public string Id { get; }
    public string Primary { get; }
    public string Secondary { get; }

    /// <summary>是不是当前采用的那个（候选列表里显示「当前」而不是「用这个」）</summary>
    [ObservableProperty] private bool _isCurrent;
}

/// <summary>话题候选（Id = 平台侧 cid / topicId）</summary>
public sealed class TopicCandidate(string id, string primary, string secondary, string topicName)
    : ResolutionCandidate(id, primary, secondary)
{
    public string TopicName { get; } = topicName;
}

/// <summary>位置候选（Id = 平台侧 poiId）</summary>
public sealed class PoiCandidate(string id, string primary, string secondary, PoiRef value)
    : ResolutionCandidate(id, primary, secondary)
{
    public PoiRef Value { get; } = value;
}

/// <summary>
/// 联邦解析的一行（话题 / 位置同构）—— **每个平台一行**，行内带候选列表。
///
/// 为什么不是每账号一行：话题/位置的 ID 是**平台级**的。2026-09-19 用两个账号实测，
/// 同一关键词下拿到的 cid / poiId 完全相同（抖音 1568360053346369、快手 228572829、
/// 小红书 B02001A1VA、视频号 qqmap_…）——原注释担心的 T3 悬案就此关闭。
/// 按账号分行只是把同一份结果印 N 遍，还把「换一个」的语义搞糊（换哪个账号的？）。
/// </summary>
public abstract class ResolutionRow : ObservableObject
{
    public required string PlatformKey { get; init; }
    public required string BrandHex { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>本行实际用来搜索的账号（同平台多账号时取第一个查得到的）</summary>
    public required Guid SessionId { get; init; }
    public string AccountLabel { get; init; } = "";

    /// <summary>候选列表 —— 行尾 [换] 展开它，点一个就用它</summary>
    public ObservableCollection<ResolutionCandidate> Candidates { get; } = [];

    /// <summary>
    /// 候选已从平台拉过吗。回填行（导入后 / 重开编辑器）**是 false** —— 候选不落盘，
    /// 那种行也要给「换」：点一下现拉（<see cref="PublishResolver.LoadCandidatesAsync"/>）。
    /// </summary>
    public bool CandidatesLoaded { get; private set; }

    private ResolutionCandidate? _selected;
    /// <summary>当前采用的候选（解析完自动采用第一个；用户换过就是换的那个）</summary>
    public ResolutionCandidate? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            ApplySelection(value);
            OnPropertyChanged(nameof(ShowSwap));
        }
    }

    private bool _isExpanded;
    /// <summary>候选面板是否展开</summary>
    public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }

    /// <summary>
    /// 结果区按关键词分组时，本行是不是该组的第一行（组头画在它上面 —— 与账号排序页同一手法）。
    /// 由 VM 装配集合时写，不是解析器的事。
    /// </summary>
    public bool IsGroupFirst { get; set; }

    /// <summary>用户输入的关键词（话题名 / 位置名）—— 翻页时还要拿它去搜，所以放基类</summary>
    public string Keyword { get; init; } = "";

    /// <summary>候选已经加载到第几页（1 = 首页）。翻页时 +1</summary>
    public int LoadedPage { get; set; }

    /// <summary>游标型分页（快手位置是 pcursor，不是页码）的下一页游标</summary>
    public string? NextCursor { get; set; }

    private bool _canLoadMore;
    /// <summary>还能不能翻（平台不支持分页 / 已经到底 → false，UI 就不给「加载更多」）</summary>
    public bool CanLoadMore { get => _canLoadMore; set => SetProperty(ref _canLoadMore, value); }

    private bool _isLoadingMore;
    public bool IsLoadingMore { get => _isLoadingMore; set => SetProperty(ref _isLoadingMore, value); }

    private bool _found;
    public bool Found { get => _found; set { if (SetProperty(ref _found, value)) RaiseDerived(); } }

    private string _primaryText = "";
    /// <summary>命中项的主文案（如「#大自然」「外滩 · 上海市黄浦区中山东一路」）</summary>
    public string PrimaryText { get => _primaryText; set => SetProperty(ref _primaryText, value); }

    private string _secondaryText = "";
    /// <summary>副文案（如热度「12.3 亿」）</summary>
    public string SecondaryText { get => _secondaryText; set => SetProperty(ref _secondaryText, value); }

    private string _notFoundText = "";
    /// <summary>未命中时的说明（**必须写清「将不带位置/保留纯文本」，不是空白**）</summary>
    public string NotFoundText { get => _notFoundText; set => SetProperty(ref _notFoundText, value); }

    /// <summary>该平台要不要走搜索 —— 视频号话题为 false（纯文本，铁律三）</summary>
    public bool NeedsSearch { get; init; } = true;

    /// <summary>正在解析（行内显示进度感）</summary>
    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set => SetProperty(ref _isBusy, value); }

    public string Initial => DisplayName.Length > 0 ? DisplayName[..1] : "?";
    public string DotHex => Found ? "#00B42A" : "#C9CDD4";
    public bool IsNotFound => !Found;
    /// <summary>未命中且该平台确实需要搜索 → 显示「重搜」</summary>
    public bool ShowRetry => !Found && NeedsSearch;
    /// <summary>
    /// 给不给「换」：命中且该平台需要搜索，且「候选还没拉过」或「候选不止一个」。
    ///
    /// 回填行（2026-10-22 用户实测：导入文件夹后小红书把「狗狗冷知识」匹配成「#亲近大自然」，
    /// 行上没有「换」，改不了）没有候选 —— 但它们同样要给入口，点一下按需现拉。
    /// 候选拉过且只有一个时不给（没什么可换的）。
    /// </summary>
    public bool ShowSwap => Found && NeedsSearch && !(CandidatesLoaded && Candidates.Count <= 1);
    /// <summary>行尾状态：命中 = 已采用（视频号那行正文已写明「无需匹配」，不再重复）</summary>
    public string StatusText => Found ? (NeedsSearch ? "已采用" : "") : NeedsSearch ? "— 未命中" : "— 无需匹配";
    public string StatusColorHex => Found ? "#0A5C28" : "#86909C";

    /// <summary>
    /// 装上候选并**默认采用第一个**（保持「命中即自动采用」的老行为）。
    ///
    /// <paramref name="keepId"/> 非空时优先采用它：回填行现拉候选是「展开看看」，
    /// 不该顺手把用户已经采用的匹配改掉（找不到同 ID 才退回第一个）。
    /// </summary>
    public void SetCandidates(IReadOnlyList<ResolutionCandidate> candidates, string? keepId = null)
    {
        Candidates.Clear();
        foreach (var c in candidates) Candidates.Add(c);
        CandidatesLoaded = true;

        Selected = keepId is { Length: > 0 }
            ? Candidates.FirstOrDefault(c =>
                  string.Equals(c.Id, keepId, StringComparison.OrdinalIgnoreCase))
              ?? Candidates.FirstOrDefault()
            : Candidates.FirstOrDefault();
        OnPropertyChanged(nameof(ShowSwap));
    }

    /// <summary>把选中项落进本行的载荷字段（子类各自实现）</summary>
    protected abstract void ApplySelection(ResolutionCandidate? candidate);

    /// <summary>候选列表里标出「当前」—— 换过之后旧的那个要变回「用这个」</summary>
    protected void MarkCurrent(ResolutionCandidate? candidate)
    {
        foreach (var c in Candidates) c.IsCurrent = ReferenceEquals(c, candidate);
    }

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(DotHex));
        OnPropertyChanged(nameof(IsNotFound));
        OnPropertyChanged(nameof(ShowRetry));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusColorHex));
        OnPropertyChanged(nameof(ShowSwap));
    }
}

/// <summary>话题解析行 —— 选中项的 Id/Name 就是写进 Params.Topics 的东西（null = 未命中，正文保留纯文本）</summary>
public sealed class TopicResolutionRow : ResolutionRow
{
    /// <summary>平台侧话题 ID（抖音 cid / 快手 id / 小红书 id）；视频号恒 null</summary>
    public string? TopicId { get; private set; }
    /// <summary>平台侧话题名（可能与用户输入不同 —— 铁律一：不做对齐，原样显示）</summary>
    public string? TopicName { get; private set; }

    /// <summary>
    /// 回填行的「当前采用值」（候选不落盘，只有 ID/名字）。现拉候选时用它保住已选项
    /// —— 否则展开一下「换」，当前结果就被第一个候选顶掉了。
    /// </summary>
    internal void SeedCurrent(string? id, string? name)
    {
        TopicId = id;
        TopicName = name;
    }

    protected override void ApplySelection(ResolutionCandidate? candidate)
    {
        var c = candidate as TopicCandidate;
        TopicId = c?.Id;
        TopicName = c?.TopicName;
        Found = c is not null;
        PrimaryText = c?.Primary ?? "";
        SecondaryText = c?.Secondary ?? "";
        MarkCurrent(candidate);
    }
}

/// <summary>位置解析行 —— 选中项的 PoiRef 就是写进 Params.Location 的东西</summary>
public sealed class PoiResolutionRow : ResolutionRow
{
    public PoiRef? Value { get; private set; }

    /// <summary>回填行的「当前采用值」—— 同 <see cref="TopicResolutionRow.SeedCurrent"/></summary>
    internal void SeedCurrent(PoiRef? value)
    {
        Value = value;
        Found = value is not null;
    }

    protected override void ApplySelection(ResolutionCandidate? candidate)
    {
        var c = candidate as PoiCandidate;
        Value = c?.Value;
        Found = c is not null;
        PrimaryText = c?.Primary ?? "";
        SecondaryText = c?.Secondary ?? "";
        MarkCurrent(candidate);
    }
}

/// <summary>
/// 联邦解析器 —— 一次输入，多平台并行扇出。
///
/// 失败策略：**单平台失败不影响其它平台**（一个平台查不到就那一行标红，
/// 其余照常）。整体异常一律吞掉转成「未命中」，因为这个页面是辅助输入，
/// 让搜索异常把编辑流程打断是本末倒置。
/// </summary>
public sealed class PublishResolver
{
    /// <summary>解析结果缓存 key = 会话 + 关键词（铁律四）</summary>
    private readonly ConcurrentDictionary<string, object> _cache = new();

    /// <summary>单平台搜索超时 —— 并行扇出时最慢的那个决定整体等待</summary>
    private static readonly TimeSpan PerPlatformTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// 小红书签名模块没热起来时的未命中说明。
    ///
    /// 为什么要有这条：签名宿主是 WebView2 页面里的 SDK，冷启动要几十秒；而单平台预算是 20s，
    /// 于是「环境还没热」会被 20s 掐断、请求裸发 → 平台返空 → 界面显示「未找到匹配」，
    /// 用户以为是平台没这个话题（2026-09-19 实测：日志里 PollMnsv2Async 抛 TaskCanceled，
    /// 紧接着就是「Signer returned null … sent without x-s header」）。
    /// 把原因说准，用户点一下「重搜」就好了。
    /// </summary>
    private const string XhsSignerCold =
        "小红书签名模块未就绪（已在后台预热）→ 稍后点「重搜」";

    // ═══════════════════════════════════════════════════════════════
    //  话题
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 候选上限 —— **平台给多少就放多少**（抖音 POI 12 / 快手标签 15 / 快手 POI 20…）。
    /// 原来卡 5 条等于把白拿的结果丢掉；要更多得靠平台翻页（见 SupportsPaging）。
    /// </summary>
    /// <summary>翻页时一页取多少条（首页也按它请求，页边界才可预期）</summary>
    private const int PageSize = 12;

    private const int MaxCandidates = 200;

    /// <summary>
    /// 按关键词并行解析各平台话题。**每个平台一行**，行内带候选（默认采用第一个）。
    /// </summary>
    public async Task<List<TopicResolutionRow>> ResolveTopicsAsync(
        string keyword, IReadOnlyList<ResolutionTarget> targets, CancellationToken ct = default)
    {
        var byPlatform = GroupByPlatform(targets);
        var rows = new List<TopicResolutionRow>();

        foreach (var (platformKey, members) in byPlatform)
        {
            var first = members[0];
            var isWechat = platformKey == PlatformNames.WechatVideo;
            rows.Add(new TopicResolutionRow
            {
                PlatformKey = platformKey,
                SessionId = first.SessionId,
                AccountLabel = first.AccountLabel,
                BrandHex = first.BrandHex,
                DisplayName = first.DisplayName,
                // 铁律三：视频号纯文本，不经 API 匹配 —— 但**必须有一行**
                Keyword = keyword,
                NeedsSearch = !isWechat,
                PrimaryText = isWechat ? "纯文本，无需匹配" : "",
                NotFoundText = isWechat
                    ? "视频号的话题靠正文里的 # 直接生效，不做 ID 匹配"
                    : "未找到匹配 → 正文里保留为纯文本",
            });
        }

        await Task.WhenAll(rows.Select(row =>
            ResolveOneTopicAsync(row, keyword, byPlatform[row.PlatformKey], ct)));
        return rows;
    }

    private async Task ResolveOneTopicAsync(
        TopicResolutionRow row, string keyword,
        IReadOnlyList<ResolutionTarget> platformTargets, CancellationToken ct)
    {
        if (!row.NeedsSearch) { row.Found = true; return; }   // 纯文本 = 无需匹配即视为可用
        if (string.IsNullOrWhiteSpace(keyword)) { row.NotFoundText = "关键词为空"; return; }

        row.IsBusy = true;
        try
        {
            var (candidates, reason) = await FetchTopicCandidatesAsync(
                row.PlatformKey, keyword, platformTargets, ct, useCache: true);

            if (candidates.Count == 0)
            {
                row.NotFoundText = reason
                    ?? (row.PlatformKey == PlatformNames.Kuaishou
                        ? "未找到匹配（快手无该话题）→ 正文里保留为纯文本"
                        : "未找到匹配 → 正文里保留为纯文本");
                return;
            }
            row.SetCandidates(candidates);
            MarkFirstPage(row, candidates.Count, isTopic: true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            row.NotFoundText = "搜索超时 → 正文里保留为纯文本";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Resolver] 话题解析失败 {Platform}", row.PlatformKey);
            row.NotFoundText = "搜索出错 → 正文里保留为纯文本";
        }
        finally { row.IsBusy = false; }
    }

    /// <summary>
    /// 拉一个平台的话题候选（缓存优先）。同平台多账号**换着试**：一个账号凭证坏了
    /// 不该让整个平台未命中（原来是每账号一行各搜一次，坏账号只影响它自己那一行）。
    /// </summary>
    private async Task<(IReadOnlyList<TopicCandidate> Candidates, string? Reason)>
        FetchTopicCandidatesAsync(
            string platformKey, string keyword,
            IReadOnlyList<ResolutionTarget> platformTargets,
            CancellationToken ct, bool useCache)
    {
        // 缓存键不含账号 —— ID 是平台级的（见 ResolutionRow 的注释）
        var cacheKey = $"t|{platformKey}|{keyword}";
        if (useCache && _cache.TryGetValue(cacheKey, out var cached) && cached is TopicHit hit)
            return (hit.Candidates, hit.Reason);

        IReadOnlyList<TopicCandidate> candidates = [];
        string? reason = null;
        foreach (var t in platformTargets)
        {
            // 小红书签名宿主**先热、再起请求预算**（2026-10-22 实测：冷启动 46s，
            // 20s 预算从预热前就开始跑 → 签名拿到已取消的 token 抛 TaskCanceledException
            // → x-s 裸发 → 请求死在发送阶段。顺序反了就是「小红书永远搜不到」）
            await WarmPlatformAsync(platformKey, t, ct);

            // ⚠️ 请求预算**每次尝试单独起**（同平台多账号换着试）
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attemptCts.CancelAfter(PerPlatformTimeout);

            // 预热预算用外层 ct（不受单次请求预算约束）—— 见 SearchTopicAsync 的注释
            var (found, why) = await SearchTopicAsync(platformKey, t, keyword, attemptCts.Token, ct);
            if (found.Count > 0) { candidates = found; reason = null; break; }
            reason ??= why;
        }

        _cache[cacheKey] = new TopicHit(candidates, reason);
        return (candidates, reason);
    }

    /// <summary>
    /// 搜一个平台的话题候选（用指定账号的凭证）。返回 (候选, 失败原因)。
    ///
    /// 两个 ct 是刻意的：<paramref name="requestCt"/> 是「单平台请求预算」（20s），
    /// <paramref name="warmupCt"/> 是「预热预算」（外层，宽容得多）—— 小红书的签名宿主冷启动
    /// 要几十秒，套 20s 的预算它永远热不起来，每次搜索都在重复付一部分加载代价（见 XhsSignerCold）。
    /// </summary>
    private static async Task<(IReadOnlyList<TopicCandidate> Candidates, string? Reason)> SearchTopicAsync(
        string platformKey, ResolutionTarget target, string keyword,
        CancellationToken requestCt, CancellationToken warmupCt, int page = 1)
    {
        var creds = await PlatformSessionApi.GetCredentialsAsync(target.SessionId);
        if (creds?.Cookie is null) return ([], "账号凭证不可用，请重新登录");

        var options = new PlatformOptions
        {
            Environment = ParseEnv(creds.ExtraDataJson),
            SessionId = target.SessionId,
        };

        switch (platformKey)
        {
            case PlatformNames.Douyin:
            {
                // 抖音的 challengesug 服务内部自签 msToken/a_bogus
                var jar = new CookieJar(creds.Cookie!);
                var list = await new DouyinHashtagService(options).SearchAsync(jar, keyword, requestCt);
                return ([.. list.Take(MaxCandidates).Select(x =>
                    new TopicCandidate(x.Cid, $"#{x.Name}", FormatCount(x.ViewCount) ?? "", x.Name))], null);
            }
            case PlatformNames.Xiaohongshu:
            {
                // ⚠️ 必须走**关键词搜索**（网页版 # 下拉同款接口），不能走 recommend/suggest：
                // 后者按标题+正文**推荐**、跟关键词无关 —— 实测「狗狗冷知识」两行都被推成
                // 「#亲近大自然」，还被当成命中自动采用（2026-10-22 用户报「小红书是错误」）。
                // 搜索没命中就如实标未命中（正文留纯文本），绝不拿不相关的推荐顶上去。
                var signerReady = await XhsSignerService.PrepareAsync(options.SessionId, warmupCt);
                var list = await new XhsTopicService(options).SearchAsync(
                    creds.Cookie!, keyword, ct: requestCt);
                return ([.. list.Take(MaxCandidates).Select(x =>
                        new TopicCandidate(x.Id, $"#{x.Name}", "", x.Name))],
                    list.Count == 0 && !signerReady ? XhsSignerCold : null);
            }
            case PlatformNames.Kuaishou:
            {
                // ⚠️ 快手三坑（设计文档 §4.4）：关键词必须追加 NBSP、首条是关键词回显 id=0 要过滤、
                // viewCount 必须是 long。前两个在 SearchTagsAsync 内部处理，第三个靠类型。
                var apiPh = ExtractCookieValue(creds.Cookie!, "kuaishou.web.cp.api_ph");
                // 快手是 0-based 页码（page="0" 是首页），我们内部统一用 1-based
                var list = await new KuaishouHelperService(options).SearchTagsAsync(
                    creds.Cookie!, keyword, count: PageSize, page: Math.Max(0, page - 1),
                    apiPh: apiPh, ct: requestCt);
                // 回显项兜底再过滤一次（服务层已过滤，这里防回归）
                return ([.. list.Where(t => t.Id != 0).Take(MaxCandidates).Select(t =>
                    new TopicCandidate(t.Id.ToString(), $"#{t.Name}", FormatCount(t.ViewCount) ?? "", t.Name))], null);
            }
            default:
                return ([], null);
        }
    }

    /// <summary>
    /// 这个平台的搜索能不能翻页（决定候选面板给不给「加载更多」）。
    ///
    /// 能翻的：抖音位置（count/page）、快手话题（count/page）与位置（count/pcursor）、
    /// 小红书位置（page）。
    /// **不能翻的**：抖音话题（challengesug 是联想接口，不带分页参数）、小红书话题
    /// （recommend/suggest 是「推荐」，同理）、视频号位置（请求体里没有分页字段）——
    /// 这三处「加载更多」按钮不该出现，免得点了没反应。
    /// </summary>
    internal static bool SupportsPaging(string platformKey, bool isTopic) => platformKey switch
    {
        PlatformNames.Douyin => !isTopic,
        PlatformNames.Kuaishou => true,
        PlatformNames.Xiaohongshu => !isTopic,
        _ => false,
    };

    /// <summary>首页搜完标一下翻页状态：平台不支持分页、或这一页没取满 → 不给「加载更多」</summary>
    private static void MarkFirstPage(ResolutionRow row, int count, bool isTopic)
    {
        row.LoadedPage = 1;
        row.CanLoadMore = SupportsPaging(row.PlatformKey, isTopic) && count >= PageSize;
    }

    /// <summary>
    /// 翻下一页（候选面板底部的「加载更多」）。返回**新增**的候选；空 = 没有更多了。
    /// 只有 <see cref="SupportsPaging"/> 为真的平台会调到 —— UI 按它决定给不给按钮。
    /// </summary>
    public async Task<IReadOnlyList<ResolutionCandidate>> LoadMoreAsync(
        ResolutionRow row, bool isTopic, CancellationToken ct = default)
    {
        if (!row.CanLoadMore || string.IsNullOrWhiteSpace(row.Keyword)) return [];

        var target = new ResolutionTarget(
            row.SessionId, row.PlatformKey, row.AccountLabel, row.BrandHex, row.DisplayName);
        var page = row.LoadedPage + 1;

        // 翻页同样可能撞上签名宿主冷启动（休眠/页面被导航走会掉）—— 预热先于预算
        await WarmPlatformAsync(row.PlatformKey, target, ct);

        IReadOnlyList<ResolutionCandidate> found = [];
        string? cursor = null;
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(PerPlatformTimeout);
            if (isTopic)
            {
                var (items, why) = await SearchTopicAsync(
                    row.PlatformKey, target, row.Keyword, cts.Token, ct, page);
                found = items;
                if (items.Count == 0) Log.Debug("[Resolver] 加载更多（话题）没拿到东西：{Why}", why ?? "到底了");
            }
            else
            {
                var (items, why, next) = await SearchPoiAsync(
                    row.PlatformKey, target, row.Keyword, cts.Token, ct, page, row.NextCursor);
                found = items;
                cursor = next;
                if (items.Count == 0) Log.Debug("[Resolver] 加载更多（位置）没拿到东西：{Why}", why ?? "到底了");
            }
        }

        // 按 ID 去重后追加：平台偶尔会把上一页的尾巴再给一遍，重复条目比少几条更惹人烦
        var seen = row.Candidates.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fresh = found.Where(c => seen.Add(c.Id)).ToList();
        foreach (var c in fresh) row.Candidates.Add(c);

        row.LoadedPage = page;
        row.NextCursor = cursor;
        // 取满一页就还有下一页；一条新的都没有 / 没取满 → 到此为止
        row.CanLoadMore = found.Count >= PageSize && fresh.Count > 0;
        return fresh;
    }

    /// <summary>
    /// 单独给一行**现拉首页候选** —— 回填行（导入后 / 重开编辑器，候选不落盘）上的「换」入口。
    ///
    /// **不走缓存**：用户点「换」就是要看现在的候选，缓存里可能是上次的失败结果
    /// （比如小红书签名没热起来时的「未命中」）。拉到的结果照常写回缓存供翻页复用。
    ///
    /// <paramref name="platformTargets"/> 为空时退回行自己记的那个账号；调用方给了就用传的
    /// （草稿里该平台的**全部**账号，同平台换着试）。
    /// </summary>
    public async Task<IReadOnlyList<ResolutionCandidate>> LoadCandidatesAsync(
        ResolutionRow row, bool isTopic,
        IReadOnlyList<ResolutionTarget>? platformTargets = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(row.Keyword)) return [];

        IReadOnlyList<ResolutionTarget> targets = platformTargets is { Count: > 0 } t
            ? t
            : [new ResolutionTarget(
                row.SessionId, row.PlatformKey, row.AccountLabel, row.BrandHex, row.DisplayName)];

        try
        {
            if (isTopic)
            {
                var (candidates, reason) = await FetchTopicCandidatesAsync(
                    row.PlatformKey, row.Keyword, targets, ct, useCache: false);
                if (candidates.Count == 0)
                {
                    row.NotFoundText = reason ?? "未找到匹配 → 正文里保留为纯文本";
                    return [];
                }
                MarkFirstPage(row, candidates.Count, isTopic: true);
                return candidates;
            }

            var (items, why, cursor) = await FetchPoiCandidatesAsync(
                row.PlatformKey, row.Keyword, targets, ct, useCache: false);
            if (items.Count == 0)
            {
                row.NotFoundText = why ?? "未找到匹配 → 该平台将不带位置";
                return [];
            }
            row.LoadedPage = 1;
            row.NextCursor = cursor;
            row.CanLoadMore = SupportsPaging(row.PlatformKey, isTopic: false) && items.Count >= PageSize;
            return items;
        }
        catch (OperationCanceledException)
        {
            row.NotFoundText = "搜索超时 → 稍后再点一次「换」";
            return [];
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Resolver] 换候选失败 {Platform}/{Keyword}", row.PlatformKey, row.Keyword);
            row.NotFoundText = "搜索出错 → 稍后再点一次「换」";
            return [];
        }
    }

    /// <summary>
    /// 平台级预热 —— **必须在「单次请求预算」之前调**（小红书：签名宿主 WebView2 冷启动
    /// 实测 19–46 秒，见 XhsPageSigner.PrepareAsync 的注释）。
    ///
    /// 2026-10-22 实测事故：预算和预热同时在跑，46 秒预热把 20 秒预算吃光 →
    /// 签名委托拿到已取消的 token（`TaskCanceledException`）→ 返回 null →
    /// 请求不带 x-s 裸发 → 平台拒/挂起，界面表现为「小红书永远未命中」。
    /// 用**外层 ct**（不受单次请求预算约束），热好之后真正的请求才拿 20 秒。
    /// </summary>
    private static Task WarmPlatformAsync(
        string platformKey, ResolutionTarget target, CancellationToken warmupCt) =>
        platformKey switch
        {
            PlatformNames.Xiaohongshu => XhsSignerService.PrepareAsync(target.SessionId, warmupCt),
            PlatformNames.Kuaishou => KuaishouSignerService.PrepareAsync(target.SessionId, warmupCt),
            _ => Task.CompletedTask,
        };

    /// <summary>按平台分组并保持出现顺序（每平台一行，不再按账号分行）</summary>
    private static Dictionary<string, List<ResolutionTarget>> GroupByPlatform(
        IReadOnlyList<ResolutionTarget> targets)
    {
        var map = new Dictionary<string, List<ResolutionTarget>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in targets)
        {
            if (!map.TryGetValue(t.PlatformKey, out var list)) map[t.PlatformKey] = list = [];
            list.Add(t);
        }
        return map;
    }

    // ═══════════════════════════════════════════════════════════════
    //  位置
    // ═══════════════════════════════════════════════════════════════

    /// <summary>按关键词并行解析各平台位置（四路扇出）。**每个平台一行**，行内带候选。</summary>
    public async Task<List<PoiResolutionRow>> ResolveLocationAsync(
        string keyword, IReadOnlyList<ResolutionTarget> targets, CancellationToken ct = default)
    {
        var byPlatform = GroupByPlatform(targets);
        var rows = byPlatform.Select(kv => new PoiResolutionRow
        {
            PlatformKey = kv.Key,
            // 关键词要记在行上 —— 「加载更多」还得拿它去搜（漏了这行会让翻页静默失效）
            Keyword = keyword,
            SessionId = kv.Value[0].SessionId,
            AccountLabel = kv.Value[0].AccountLabel,
            BrandHex = kv.Value[0].BrandHex,
            DisplayName = kv.Value[0].DisplayName,
            NotFoundText = "未找到匹配 → 该平台将不带位置",
        }).ToList();

        await Task.WhenAll(rows.Select(row =>
            ResolveOnePoiAsync(row, keyword, byPlatform[row.PlatformKey], ct)));
        return rows;
    }

    private async Task ResolveOnePoiAsync(
        PoiResolutionRow row, string keyword,
        IReadOnlyList<ResolutionTarget> platformTargets, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(keyword)) { row.NotFoundText = "关键词为空"; return; }

        row.IsBusy = true;
        try
        {
            var (candidates, reason, cursor) = await FetchPoiCandidatesAsync(
                row.PlatformKey, keyword, platformTargets, ct, useCache: true);
            row.NextCursor = cursor;

            if (candidates.Count == 0)
            {
                row.NotFoundText = reason ?? "未找到匹配 → 该平台将不带位置";
                return;
            }
            row.SetCandidates(candidates);
            MarkFirstPage(row, candidates.Count, isTopic: false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            row.NotFoundText = "搜索超时 → 该平台将不带位置";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Resolver] 位置解析失败 {Platform}", row.PlatformKey);
            row.NotFoundText = "搜索出错 → 该平台将不带位置";
        }
        finally { row.IsBusy = false; }
    }

    /// <summary>拉一个平台的位置候选（缓存优先；多账号换着试 + 请求预算每次单独起，同话题那条）</summary>
    private async Task<(IReadOnlyList<PoiCandidate> Candidates, string? Reason, string? NextCursor)>
        FetchPoiCandidatesAsync(
            string platformKey, string keyword,
            IReadOnlyList<ResolutionTarget> platformTargets,
            CancellationToken ct, bool useCache)
    {
        var cacheKey = $"p|{platformKey}|{keyword}";
        // 游标不缓存（与原来一致）：缓存命中只复用候选，翻页从下一页按页码重新问
        if (useCache && _cache.TryGetValue(cacheKey, out var cached) && cached is PoiHit hit)
            return (hit.Candidates, hit.Reason, null);

        IReadOnlyList<PoiCandidate> candidates = [];
        string? reason = null;
        string? nextCursor = null;
        foreach (var t in platformTargets)
        {
            // 预热先于请求预算 —— 同话题那条注释（小红书签名宿主冷启动几十秒）
            await WarmPlatformAsync(platformKey, t, ct);

            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attemptCts.CancelAfter(PerPlatformTimeout);

            var (found, why, cursor) = await SearchPoiAsync(
                platformKey, t, keyword, attemptCts.Token, ct);
            if (found.Count > 0) { candidates = found; reason = null; nextCursor = cursor; break; }
            reason ??= why;
        }

        _cache[cacheKey] = new PoiHit(candidates, reason);
        return (candidates, reason, nextCursor);
    }

    /// <summary>候选主文案：名称 · 副名（选中后的行、以及编辑器回填时的行都用它，保证一致）</summary>
    internal static string PoiPrimary(PoiRef p) =>
        string.IsNullOrWhiteSpace(p.SubName) ? p.Name : $"{p.Name} · {p.SubName}";

    /// <summary>返回 (候选, 未命中原因)。失败原因要具体 —— 「快手缺定位」和「真没这个地点」对用户是两回事。</summary>
    private static async Task<(IReadOnlyList<PoiCandidate> Candidates, string? Reason, string? NextCursor)>
        SearchPoiAsync(
            string platformKey, ResolutionTarget target, string keyword,
            CancellationToken requestCt, CancellationToken warmupCt,
            int page = 1, string? cursor = null)
    {
        var creds = await PlatformSessionApi.GetCredentialsAsync(target.SessionId);
        if (creds?.Cookie is null) return ([], "账号凭证不可用，请重新登录", null);

        var cookie = creds.Cookie!;
        var options = new PlatformOptions
        {
            Environment = ParseEnv(creds.ExtraDataJson),
            SessionId = target.SessionId,
        };

        switch (platformKey)
        {
            case PlatformNames.Douyin:
            {
                // POI 搜索要调用方自签（aid=1128，与 STS 同款；来源 examples/douyin/poi/poi.py）
                var baseParams = PlatformHttpClient.BuildQueryString(env: options.Environment);
                var msToken = MsTokenGenerator.Generate();
                // a_bogus 内嵌 UA 必须与实际请求头 UA 同源（与 PublishPipeline 同口径）
                var aBogus = ABogusGenerator.Generate(baseParams,
                    options.Environment?.UserAgent ?? options.UserAgent, aid: 1128);

                var result = await new DouyinPoiService(options).SearchAsync(
                    cookie, keyword, count: PageSize, page: page,
                    msToken: msToken, aBogus: aBogus, ct: requestCt);
                return ([.. result.Items.Take(MaxCandidates).Select(x =>
                {
                    var r = new PoiRef(x.PoiId, x.PoiName, x.Address);
                    return new PoiCandidate(x.PoiId, PoiPrimary(r), "", r);
                })], null, null);
            }

            case PlatformNames.Kuaishou:
            {
                // 快手 cityName 是 body **必填**（缺了 result=10001）。城市优先取 nearby，
                // **拿不到就用关键词兜底** —— 老版正是这条退路（App/ViewModels/MainWindowViewModel.cs:762
                // `KuaishouNearbyCityName ?? keyword`）。
                //
                // ⚠️ 别再把「Cookie 里有 lat/lon」当硬门槛（2026-09-19 修）：那种写法下，
                // Cookie 没有经纬度时**请求根本不发**，用户在界面上看到的是「未找到匹配」，
                // 而真相是代码自己放弃了 —— 老版能搜到、新版搜不到，就是这个差异。
                var apiPh = ExtractCookieValue(cookie, "kuaishou.web.cp.api_ph");
                var helper = new KuaishouHelperService(options);

                var lat = ExtractCookieValue(cookie, "lat");
                var lon = ExtractCookieValue(cookie, "lon");
                var hasGeo = !string.IsNullOrWhiteSpace(lat) && !string.IsNullOrWhiteSpace(lon)
                             && lat != "0" && lon != "0";

                var cityName = keyword;
                if (hasGeo)
                {
                    var (nearbyCity, _) = await helper.GetNearbyPoisAsync(cookie, lat, lon, apiPh: apiPh, ct: requestCt);
                    if (!string.IsNullOrWhiteSpace(nearbyCity)) cityName = nearbyCity;
                    else Log.Debug("[Resolver] 快手 nearby 没给城市名 —— 用关键词「{Kw}」当城市名", keyword);
                }
                else
                {
                    Log.Debug("[Resolver] 快手 Cookie 无经纬度 —— 用关键词「{Kw}」当城市名（老版退路）", keyword);
                }

                var (list, nextCursor) = await helper.SearchPoiPageAsync(
                    cookie, cityName, keyword, count: PageSize, pcursor: cursor, apiPh: apiPh, ct: requestCt);
                if (list.Count == 0) return ([], $"未在「{cityName}」找到该地点 → 该平台将不带位置", null);
                return ([.. list.Take(MaxCandidates).Select(best =>
                {
                    var r = new PoiRef(
                        best.IdString ?? best.Id.ToString(), best.Title, best.Address,
                        Longitude: best.Longitude.ToString("F6"),
                        Latitude: best.Latitude.ToString("F6"),
                        CityName: string.IsNullOrWhiteSpace(best.City) ? cityName : best.City);
                    return new PoiCandidate(r.PoiId!, PoiPrimary(r), "", r);
                })], null, nextCursor);
            }

            case PlatformNames.WechatVideo:
            {
                var extra = ParseExtraDict(creds.ExtraDataJson);
                var deviceId = extra.GetValueOrDefault("deviceId")?.ToString()
                    ?? extra.GetValueOrDefault("device_id")?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(deviceId))
                    return ([], "视频号缺设备标识 → 该平台将不带位置", null);

                var list = await new WechatVideoCollectionService(options)
                    .SearchLocationAsync(cookie, deviceId, keyword, ct: requestCt);
                return ([.. list.Take(MaxCandidates).Select(x =>
                {
                    var r = new PoiRef(
                        x.PoiClassifyId, x.PoiName ?? keyword, x.Address,
                        Latitude: x.Latitude.ToString("F6"),
                        Longitude: x.Longitude.ToString("F6"),
                        // 城市：搜索结果里就有（老版一直发、抓包里也有），发布时要进位置对象
                        CityName: x.City);
                    return new PoiCandidate(r.PoiId!, PoiPrimary(r), "", r);
                })], null, null);   // 视频号的搜索请求体里没有分页字段 —— 翻不了页
            }

            case PlatformNames.Xiaohongshu:
            {
                var signerReady = await XhsSignerService.PrepareAsync(options.SessionId, warmupCt);
                var list = await new XhsPoiService(options).SearchAsync(cookie, keyword, page: page, ct: requestCt);
                return ([.. list.Take(MaxCandidates).Select(x =>
                {
                    // 保留平台给的真实 poi_type —— 老版搜到了却硬编码 12（缺陷 L5）
                    var r = new PoiRef(x.PoiId, x.Name, x.Subname, x.PoiType);
                    return new PoiCandidate(r.PoiId!, PoiPrimary(r), "", r);
                })],
                    list.Count == 0 && !signerReady ? XhsSignerCold : null, null);
            }

            default:
                return ([], "该平台不支持位置", null);
        }
    }

    // ═══ 工具 ═══

    /// <summary>一个平台的话题解析结果（进缓存的就是它）。Candidates 为空时 Reason 说明为什么。</summary>
    private sealed record TopicHit(IReadOnlyList<TopicCandidate> Candidates, string? Reason);

    /// <summary>一个平台的位置解析结果；同上。</summary>
    private sealed record PoiHit(IReadOnlyList<PoiCandidate> Candidates, string? Reason);

    /// <summary>热度展示：12.3 亿 / 480 万 / 3.1 亿（中文计数法）</summary>
    private static string? FormatCount(long count) => count switch
    {
        <= 0 => null,
        >= 100_000_000 => $"{count / 100_000_000.0:0.#} 亿",
        >= 10_000 => $"{count / 10_000.0:0.#} 万",
        _ => count.ToString(),
    };

    /// <summary>extra_data_json 里的 env 快照（发布页话题块 2.6.3 也用它，同口径）</summary>
    internal static PlatformEnvironmentSnapshot? ParseEnv(string? extraDataJson)
    {
        if (string.IsNullOrWhiteSpace(extraDataJson)) return null;
        try
        {
            var extra = ParseExtraDict(extraDataJson);
            if (!extra.TryGetValue("env", out var raw) || raw is null) return null;
            // ⚠️ 值是 JsonElement 不是 string（同 CredentialFields 的坑，2026-10-16 同批修复）
            var envJson = raw.ToString();
            if (string.IsNullOrWhiteSpace(envJson)) return null;
            var snap = JsonSerializer.Deserialize<PlatformEnvironmentSnapshot>(envJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return snap is { IsValid: true } ? snap : null;
        }
        catch { return null; }
    }

    private static Dictionary<string, object?> ParseExtraDict(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<Dictionary<string, object?>>(json) ?? []; }
        catch { return []; }
    }

    private static string? ExtractCookieValue(string cookie, string name)
    {
        foreach (var part in cookie.Split(';'))
        {
            var trimmed = part.Trim();
            var eq = trimmed.IndexOf('=');
            if (eq > 0 && trimmed[..eq] == name) return trimmed[(eq + 1)..];
        }
        return null;
    }
}
