namespace CreatFlow.Platforms.Topics;

/// <summary>
/// 话题 / 活动接口的短期缓存（2.6.7）。
///
/// 为什么需要：搜索 / 推荐是用户**随手点**的操作，一次请求在抖音要走签名、在小红书
/// 还要先热签名宿主（冷启动几十秒）；同一条目翻回来再点一次就重新打一遍平台接口，
/// 体验与风控都不划算。缓存键带**账号（会话 ID）+ 平台 + 搜索/推荐 + 关键词/上下文** ——
/// 换账号、换平台、改关键词都是新查询，绝不串结果（串了比慢更糟：会把 A 号的话题 ID 发给 B 号）。
///
/// 四条纪律：
///   1. **只缓存成功**（<see cref="TopicFetchResult.Ok"/> / <see cref="ActivityFetchResult.Ok"/>）——
///      失败也缓存 10 分钟 = 用户点重试也救不回来，和「接口失败不阻塞发布」相悖；
///   2. TTL 10 分钟（<see cref="DefaultTtl"/>）：平台推荐/活动几分钟内不会变，过期自动回网络；
///      UI 另给「同一个键再点一次 = 强制刷新」的口子，不用等缓存过期；
///   3. 容量上限 + 过期清理：常开着编辑器来回搜也不会无限涨内存；
///   4. 线程安全（UI 线程与后台刷新可能同时读写）。
///
/// 时钟可注入，自检用假时钟钉住 10 分钟边界，不依赖运行时刻。
/// </summary>
public sealed class TopicApiCache
{
    /// <summary>缓存有效期（产品口径：10 分钟）</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(10);

    /// <summary>每类（话题 / 活动）最多缓存多少条查询</summary>
    public const int DefaultCapacity = 128;

    /// <summary>
    /// 进程内共享实例 —— 发布页 VM 是 Transient（进出页面会重建），
    /// 缓存不能跟着 VM 死掉，否则「翻回来再搜」还是回网络。
    /// </summary>
    public static TopicApiCache Shared { get; } = new();

    private sealed record Entry(DateTimeOffset At, object Value);

    // 键里用 U+001F（字段分隔符）：关键词/标题里不可能出现，杜绝「a|b + c」拼成
    // 另一个键的「a + b|c」这类碰撞（碰撞 = 把别的话题结果当成自己的）。
    private const char Sep = '\u001F';

    private readonly Dictionary<string, Entry> _topics = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entry> _activities = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeSpan _ttl;
    private readonly int _capacity;

    public TopicApiCache(
        Func<DateTimeOffset>? now = null,
        TimeSpan? ttl = null,
        int capacity = DefaultCapacity)
    {
        _now = now ?? (() => DateTimeOffset.Now);
        _ttl = ttl ?? DefaultTtl;
        _capacity = Math.Max(1, capacity);
    }

    public TimeSpan Ttl => _ttl;

    // ═══════════════════════════════════════════════════════════════
    //  键
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 话题查询的缓存键：账号（会话）+ 平台 + 搜索/推荐 + 关键词 + 推荐上下文。
    /// 推荐结果依赖标题**和正文**（小红书 desc 也进请求；B站按标题推荐），两者都进键 ——
    /// 改了标题/正文再点「获取平台推荐」必须重新拉，不能拿旧内容的推荐糊弄。
    /// </summary>
    public static string TopicKey(
        Guid sessionId, string platformKey, bool recommend, string keyword, string context = "")
        => string.Join(Sep,
            "topic", sessionId.ToString("N"), platformKey,
            recommend ? "rec" : "search",
            Normalize(keyword), Normalize(context));

    /// <summary>活动查询的缓存键：账号（会话）+ 平台 + 上下文（B站按标题聚合活动）。</summary>
    public static string ActivityKey(Guid sessionId, string platformKey, string context = "")
        => string.Join(Sep,
            "activity", sessionId.ToString("N"), platformKey, Normalize(context));

    /// <summary>压空白：同一句话多打一个空格不该换一次请求。</summary>
    private static string Normalize(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? ""
            : string.Join(" ", raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // ═══════════════════════════════════════════════════════════════
    //  话题
    // ═══════════════════════════════════════════════════════════════

    /// <summary>取缓存（过期视为未命中并顺手清掉）</summary>
    public bool TryGetTopics(string key, out TopicFetchResult result) =>
        TryGet(_topics, key, out result);

    /// <summary>写缓存 —— **失败结果直接丢弃**（重试必须回到网络）</summary>
    public void SetTopics(string key, TopicFetchResult result)
    {
        if (result.Ok) Set(_topics, key, result);
    }

    // ═══════════════════════════════════════════════════════════════
    //  活动
    // ═══════════════════════════════════════════════════════════════

    /// <summary>取活动缓存（与话题分开两个字典，同账号同平台也互不串）</summary>
    public bool TryGetActivities(string key, out ActivityFetchResult result) =>
        TryGet(_activities, key, out result);

    public void SetActivities(string key, ActivityFetchResult result)
    {
        if (result.Ok) Set(_activities, key, result);
    }

    // ═══════════════════════════════════════════════════════════════
    //  存取实现
    // ═══════════════════════════════════════════════════════════════

    private bool TryGet<T>(Dictionary<string, Entry> map, string key, out T value) where T : class
    {
        value = null!;
        if (string.IsNullOrWhiteSpace(key)) return false;

        lock (_gate)
        {
            if (!map.TryGetValue(key, out var entry)) return false;
            if (_now() - entry.At < _ttl)
            {
                value = (T)entry.Value;
                return true;
            }
            // 过期即删：不删的话容量被死条目占着，新查询会把仍然有效的老条目挤掉
            map.Remove(key);
            return false;
        }
    }

    private void Set<T>(Dictionary<string, Entry> map, string key, T value) where T : class
    {
        if (string.IsNullOrWhiteSpace(key)) return;

        lock (_gate)
        {
            var now = _now();
            Prune(map, now);
            if (!map.ContainsKey(key) && map.Count >= _capacity) RemoveOldest(map);
            map[key] = new Entry(now, value);
        }
    }

    private void Prune(Dictionary<string, Entry> map, DateTimeOffset now)
    {
        if (map.Count == 0) return;
        List<string>? expired = null;
        foreach (var (key, entry) in map)
        {
            if (now - entry.At >= _ttl) (expired ??= []).Add(key);
        }
        if (expired is null) return;
        foreach (var key in expired) map.Remove(key);
    }

    /// <summary>容量满时淘汰**最旧**的一条（查询量小，线性找最小足够）</summary>
    private static void RemoveOldest(Dictionary<string, Entry> map)
    {
        string? oldestKey = null;
        var oldestAt = DateTimeOffset.MaxValue;
        foreach (var (key, entry) in map)
        {
            if (entry.At >= oldestAt) continue;
            oldestAt = entry.At;
            oldestKey = key;
        }
        if (oldestKey is not null) map.Remove(oldestKey);
    }
}
