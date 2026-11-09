namespace CreatFlow.NewApp.Services;

/// <summary>
/// 自动发布的一个目标账号（平台 × 会话）—— 合成入队时从账号芯片快照，
/// 随任务持久化（ComposeJob / ComposeTaskData）。
/// </summary>
/// <param name="PlatformKey">平台键（douyin / kuaishou / xiaohongshu / wechatvideo）</param>
/// <param name="SessionId">平台会话 ID</param>
/// <param name="AccountLabel">账号名（快照，入队后解绑也认得出）</param>
/// <param name="IsLoggedIn">入队时的登录态（执行时以预检为准）</param>
public sealed record AutoPublishTarget(
    string PlatformKey, Guid SessionId, string AccountLabel, bool IsLoggedIn = true);

/// <summary>自动发布结果。<see cref="Ok"/> = 已进服务端上传队列。</summary>
public sealed record AutoPublishResult(bool Ok, string Message, int PlatformCount = 0);

/// <summary>
/// 合成完成后自动进上传队列的**无头提交器** —— 把一份发布单元目录
/// （<c>&lt;输出目录&gt;/&lt;标题&gt;/</c>：mp4 + params.txt + cover.jpg）走一遍发布页
/// 「导入文件夹 → 提交」的等价路径，但不经任何 UI：
///
///   解析 params.txt → 建草稿（账号预设播种 + params 覆盖，共享 <see cref="PublishDraftFactory"/>）
///   → 探测元数据（ffprobe）→ params 带话题/位置/合集时做联邦解析（否则会被静默忽略）
///   → 预检（Error 拦截，与手动导入同口径）→ SHA256 → 每目标 payload → POST /api/publish/submit。
///
/// 提交后由服务端建记录、<c>UploadQueueViewModel</c> 照常认领执行 —— 本类与上传链路零耦合。
/// 调用方：<c>ComposeQueueViewModel</c> 的消费循环（池线程；冒烟模式下消费者不跑）。
/// </summary>
public sealed class PublishUnitSubmitter
{
    // 预设是**会员级**存档：DI 注入与发布页同一实例（会员由 store 的动态 provider 现取；
    // 队列任务只会在属主会员的进程状态下提交 —— 换号挂起期间自动发布会被抑制）
    private readonly PublishDraftStore _store;
    private readonly VideoMetaProbe _probe = new();
    private readonly PublishResolver _resolver = new();
    private readonly PublishPipeline _pipeline;

    public PublishUnitSubmitter(PublishDraftStore draftStore)
    {
        _store = draftStore;
        _pipeline = new PublishPipeline { MetaProbe = _probe };
    }

    /// <param name="unitDir">发布单元目录（合成产物的落点）。</param>
    /// <param name="videoPath">成片全路径（<c>ComposeOutcome.OutputPath</c> —— 目录里还有别的视频也不会选错）。</param>
    /// <param name="targets">目标账号快照（入队时定的那份）。</param>
    public async Task<AutoPublishResult> SubmitAsync(
        string unitDir, string videoPath, IReadOnlyList<AutoPublishTarget> targets,
        CancellationToken ct = default)
    {
        try
        {
            if (targets.Count == 0) return Fail("没有勾选任何账号");

            // 1) 单元目录 + 已知成片 → 候选（标题回退链、封面、params 都在这一层算好）
            var candidate = PublishImportPlanner.UnitFor(unitDir, videoPath);
            if (!File.Exists(candidate.VideoPath)) return Fail("成片不存在（可能已被移动或删除）");

            // 2) 账号级预设（与发布页同一份 publish-defaults.json）
            var defaults = await _store.LoadDefaultsAsync(ct);

            // 3) 建草稿：预设先播种、params 后覆盖（顺序不可反，见 PublishDraftFactory）
            var draft = new PublishDraft(candidate.VideoPath, candidate.SourceFolder)
            {
                Title = candidate.Title,
                Description = candidate.Description ?? "",
                CoverPath = candidate.CoverPath,
            };
            var seed = targets
                .Select(t => new PublishTarget(t.PlatformKey, t.SessionId, t.AccountLabel)
                {
                    IsLoggedIn = t.IsLoggedIn,
                })
                .ToList();
            PublishDraftFactory.SeedTargets(draft, seed, defaults);
            PublishDraftFactory.ApplyUnitParams(draft, candidate.Params);

            // 4) 元数据探测 —— 小红书 ≥10 秒预检要用时长；失败不致命（预检按无时长处理）
            try
            {
                draft.Meta = await _probe.ProbeAsync(draft.VideoPath, ct);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[AutoPublish] 视频元数据探测失败（继续）");
            }

            // 5) params 带了话题/位置/合集才做联邦解析 —— 跳过它们提交时会被静默忽略
            var trouble = new List<string>();
            if (draft.Topics.Count > 0)
                await ResolveTopicsAsync(draft, defaults, ct);
            if (candidate.Params.Location is { Length: > 0 } locationKeyword)
                await ResolveLocationAsync(draft, locationKeyword, defaults, ct);
            if (candidate.Params.Collection is { Length: > 0 } collectionName)
                trouble.AddRange(await ApplyCollectionAsync(draft, collectionName, defaults, ct));

            ct.ThrowIfCancellationRequested();

            // 6) 预检 —— Error 级拦截（与发布页「有 Error 不能提交」同口径）
            var errors = PublishPreflight.Validate(draft)
                .Where(i => i.Severity == PreflightSeverity.Error)
                .Select(i => i.Message)
                .ToList();
            if (errors.Count > 0)
            {
                var head = string.Join("；", errors.Take(3));
                return Fail(errors.Count > 3 ? $"预检未通过：{head}（共 {errors.Count} 条）" : $"预检未通过：{head}");
            }

            // 7) 组装 + 提交（服务端建记录，上传队列接管）
            var request = await PublishDraftFactory.BuildSubmitRequestAsync(draft, defaults);
            if (request is null) return Fail("提交请求组装失败（视频读不到或哈希失败）");

            var resp = await _pipeline.SubmitAsync(request, ct);
            if (resp is null) return Fail("服务端没有返回结果（网络或登录态问题，请稍后从发布页手动导入）");

            var message = $"已进上传队列 · {targets.Count} 个账号（{resp.Platforms.Count} 条平台记录）";
            if (trouble.Count > 0) message += "；" + string.Join("；", trouble.Distinct().Take(2));
            Log.Information("[AutoPublish] {Unit} → 已提交任务 {TaskId}（{Count} 条平台记录）",
                Path.GetFileName(unitDir), resp.TaskId, resp.Platforms.Count);
            return new AutoPublishResult(true, message, resp.Platforms.Count);
        }
        catch (OperationCanceledException)
        {
            throw;   // 取消交给调用方翻译成「自动发布被取消」
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[AutoPublish] 自动发布失败：{Dir}", unitDir);
            return Fail(ex.Message);
        }
    }

    private static AutoPublishResult Fail(string reason) => new(false, $"自动发布失败：{reason}");

    // ═══ 联邦解析（照搬发布页导入路径的写回口径） ═══

    private async Task ResolveTopicsAsync(
        PublishDraft draft, IReadOnlyDictionary<string, PlatformParams> defaults, CancellationToken ct)
    {
        var targets = BuildResolutionTargets(draft);
        if (targets.Count == 0) return;

        // **每个话题都出结果**，不是只留最后一个：每个关键词独立解析并写回
        foreach (var name in draft.Topics.ToList())
        {
            try
            {
                var rows = await _resolver.ResolveTopicsAsync(name, targets, ct);
                foreach (var row in rows)
                    ApplyToPlatformTargets(draft, row.PlatformKey, defaults, p =>
                    {
                        // 未命中的也写（PlatformId=null）——「正文保留纯文本」在数据上是显式的（缺陷 L2）
                        p.Topics.RemoveAll(t => t.Name == row.Keyword);
                        p.Topics.Add(new ResolvedTopic(row.Keyword, row.TopicId, row.TopicName));
                    });
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[AutoPublish] 话题解析失败：{Kw}", name);
            }
        }
    }

    private async Task ResolveLocationAsync(
        PublishDraft draft, string keyword, IReadOnlyDictionary<string, PlatformParams> defaults,
        CancellationToken ct)
    {
        var targets = BuildResolutionTargets(draft);
        if (targets.Count == 0) return;

        try
        {
            var rows = await _resolver.ResolveLocationAsync(keyword, targets, ct);
            foreach (var row in rows)
                ApplyToPlatformTargets(draft, row.PlatformKey, defaults, p => p.Location = row.Value);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[AutoPublish] 位置解析失败（将不带位置）：{Kw}", keyword);
        }
    }

    /// <summary>按名字查合集映射并铺到各目标。返回给用户看的原因（没查到 / 哪些账号没同步）。</summary>
    private async Task<List<string>> ApplyCollectionAsync(
        PublishDraft draft, string name, IReadOnlyDictionary<string, PlatformParams> defaults,
        CancellationToken ct)
    {
        var trouble = new List<string>();
        CollectionItem? item;
        try
        {
            var resp = await ApiClient.PostAsync<CollectionListResponse>("/api/collections/list");
            item = resp?.Collections.FirstOrDefault(c =>
                string.Equals(c.Title?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (item is null)
            {
                trouble.Add($"没找到叫「{name}」的合集 → 保持账号预设里的合集");
                return trouble;
            }
            // 文章合集不能挂到视频发布（头条文章 collection_id ≠ 视频 pSeries）
            if (item.ContentType == 1)
            {
                trouble.Add($"「{name}」是文章合集，不能挂到视频发布 → 保持账号预设里的合集（文章合集仅用于图文发布）");
                return trouble;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[AutoPublish] 合集列表查询失败");
            trouble.Add($"合集列表没取到（{ex.Message}）→ 保持账号预设里的合集");
            return trouble;
        }

        // 铺映射（与编辑器点一次合集下拉同一口径：命中的写 id，缺映射的**显式清空**）
        var detail = item.Platforms ?? [];
        var missed = new List<string>();
        foreach (var t in draft.EnabledTargets)
        {
            var key = PublishDraftStore.KeyOf(t.PlatformKey, t.SessionId);
            var target = draft.Targets.FirstOrDefault(x => x.Key == key);
            if (target is null) continue;

            var mapping = detail.FirstOrDefault(d =>
                string.Equals(d.PlatformKey, t.PlatformKey, StringComparison.OrdinalIgnoreCase)
                && d.PlatformSessionId == t.SessionId);

            var p = draft.ParamsFor(target, defaults.GetValueOrDefault(key));
            if (!string.IsNullOrWhiteSpace(mapping?.PlatformCollectionId))
            {
                p.CollectionId = mapping.PlatformCollectionId;
                p.CollectionName = name;
            }
            else
            {
                p.CollectionId = null;
                p.CollectionName = null;
                missed.Add(t.AccountLabel);
            }
        }

        if (missed.Count > 0)
            trouble.Add($"合集「{item.Title}」有 {missed.Count} 个账号没同步过 → 那些账号不带合集");
        return trouble;
    }

    private static List<ResolutionTarget> BuildResolutionTargets(PublishDraft draft) =>
        draft.EnabledTargets.Select(t => new ResolutionTarget(
            t.SessionId, t.PlatformKey, t.AccountLabel,
            "#6C5CE7", PlatformNames.GetDisplayName(t.PlatformKey))).ToList();

    /// <summary>把某平台的解析结果写进该平台**所有账号**（话题/位置的 ID 是平台级的，§T3）</summary>
    private static void ApplyToPlatformTargets(
        PublishDraft draft, string platformKey,
        IReadOnlyDictionary<string, PlatformParams> defaults,
        Action<PlatformParams> apply)
    {
        foreach (var target in draft.Targets)
        {
            if (!string.Equals(target.PlatformKey, platformKey, StringComparison.OrdinalIgnoreCase))
                continue;
            var key = PublishDraftStore.KeyOf(target.PlatformKey, target.SessionId);
            apply(draft.ParamsFor(target, defaults.GetValueOrDefault(key)));
        }
    }
}
