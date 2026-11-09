namespace CreatFlow.Application.Identity.Services;

/// <summary>
/// Manages publish tasks — submit, list, detail, report, retry.
/// Called by desktop app via PublishController.
/// </summary>
public class PublishService
{
    private readonly IPublishTaskRepository _taskRepo;
    private readonly IPublishPlatformRepository _platformRepo;
    private readonly IMemberRepository _memberRepo;

    public PublishService(
        IPublishTaskRepository taskRepo,
        IPublishPlatformRepository platformRepo,
        IMemberRepository memberRepo)
    {
        _taskRepo = taskRepo;
        _platformRepo = platformRepo;
        _memberRepo = memberRepo;
    }

    // ═══ Submit ═══

    /// <summary>
    /// Creates a publish task with platform records.
    /// Returns taskId + platform list for desktop to start the pipeline.
    /// </summary>
    public async Task<PublishSubmitResult> SubmitAsync(
        Guid memberId, SubmitPublishRequest request, CancellationToken ct = default)
    {
        if (request.Targets is not { Count: > 0 })
            throw new ValidationException("至少需要指定一个发布目标平台");

        var task = PublishTask.Create(
            PublishTaskId.New(),
            memberId,
            request.VideoFilePath,
            request.VideoFileName,
            request.VideoFileSize,
            request.VideoHash,
            request.VideoDurationS,
            request.VideoWidth,
            request.VideoHeight,
            request.CoverFilePath,
            request.CommonTitle,
            request.CommonDesc,
            request.ScheduledAt);

        foreach (var target in request.Targets)
        {
            task.AddPlatform(
                target.PlatformKey,
                target.PlatformSessionId,
                target.Title,
                target.Description,
                target.CoverFilePath,
                target.CollectionId,
                target.ExtraDataJson);
        }

        task.Submit();

        // Save task first, then platforms (both in same transaction-safe context)
        await _taskRepo.AddAsync(task, ct);

        foreach (var p in task.Platforms)
            await _platformRepo.AddAsync(p, ct);

        await _taskRepo.SaveChangesAsync(ct);

        return new PublishSubmitResult(
            task.Id.Value,
            task.Platforms.Select(ToPlatformDto).ToList());
    }

    // ═══ List ═══

    /// <summary>
    /// Returns all publish tasks for a member, newest first.
    /// </summary>
    public async Task<List<PublishTaskDto>> GetListAsync(
        Guid memberId, CancellationToken ct = default)
    {
        var tasks = await _taskRepo.GetByMemberAsync(memberId, ct);
        var result = new List<PublishTaskDto>();

        foreach (var t in tasks)
        {
            var platforms = await _platformRepo.GetByTaskAsync(t.Id, ct);
            result.Add(new PublishTaskDto(
                t.Id.Value,
                t.VideoFileName,
                t.VideoFilePath,
                t.CommonTitle,
                t.VideoFileSize,
                t.VideoDurationS,
                platforms.Count,
                platforms.Count(p => p.Status == PublishPlatformStatus.Published),
                platforms.Count(p => p.Status == PublishPlatformStatus.Failed),
                (int)t.Status,
                TaskStatusToText(t.Status),
                t.ScheduledAt,
                t.CreatedAt ?? Clock.Now,
                t.LastModified
            ));
        }

        return result;
    }

    // ═══ Detail ═══

    /// <summary>
    /// Returns a single publish task with all platform records.
    /// </summary>
    public async Task<PublishTaskDetailDto> GetDetailAsync(
        Guid memberId, Guid taskId, CancellationToken ct = default)
    {
        var task = await _taskRepo.GetByIdAndMemberAsync(
            PublishTaskId.Of(taskId), memberId, ct)
            ?? throw new NotFoundException($"发布任务 {taskId} 不存在");

        var platforms = await _platformRepo.GetByTaskAsync(task.Id, ct);

        return new PublishTaskDetailDto(
            task.Id.Value,
            task.MemberId,
            task.VideoFileName,
            task.VideoFilePath,
            task.VideoFileSize,
            task.VideoHash,
            task.VideoDurationS,
            task.VideoWidth,
            task.VideoHeight,
            task.CoverFilePath,
            task.CommonTitle,
            task.CommonDesc,
            task.ScheduledAt,
            (int)task.Status,
            TaskStatusToText(task.Status),
            platforms.Select(ToPlatformDto).ToList(),
            task.CreatedAt ?? Clock.Now,
            task.LastModified
        );
    }

    // ═══ Report ═══

    /// <summary>
    /// Called by desktop app after a platform SDK call completes.
    /// Updates a single PublishPlatform record to Published or Failed.
    /// Also updates the parent task's overall status.
    /// </summary>
    public async Task ReportAsync(
        ReportPublishRequest request, CancellationToken ct = default)
    {
        var platform = await _platformRepo.GetByIdAsync(
            PublishPlatformId.Of(request.PlatformId), ct)
            ?? throw new NotFoundException($"平台记录 {request.PlatformId} 不存在");

        if (request.Success)
        {
            platform.MarkPublished(
                request.PlatformVideoId
                    ?? throw new ArgumentNullException(nameof(request.PlatformVideoId),
                        "成功时必须提供平台视频 ID"),
                request.PlatformVideoUrl);

            if (!string.IsNullOrWhiteSpace(request.CoverUrl))
                platform.SetCoverUrl(request.CoverUrl);

            // 合并 extra_data_json（如 ks_cover_key），不覆盖已有字段
            if (!string.IsNullOrWhiteSpace(request.ExtraDataJson))
                platform.MergeExtraData(request.ExtraDataJson);
        }
        else
        {
            platform.MarkFailed(request.ErrorMsg ?? "未知错误");
        }

        await _platformRepo.SaveChangesAsync(ct);

        // Update parent task overall status
        var task = await _taskRepo.GetByIdAsync(
            PublishTaskId.Of(platform.PublishTaskId.Value), ct);
        if (task is not null)
        {
            // Re-fetch all platforms to compute overall status
            var allPlatforms = await _platformRepo.GetByTaskAsync(task.Id, ct);
            var published = allPlatforms.Count(p => p.Status == PublishPlatformStatus.Published);
            var failed = allPlatforms.Count(p => p.Status == PublishPlatformStatus.Failed);

            task.UpdateOverallStatus();
            await _taskRepo.SaveChangesAsync(ct);
        }
    }

    // ═══ Retry ═══

    /// <summary>
    /// Resets a failed platform record to Pending for retry.
    /// </summary>
    public async Task RetryAsync(
        Guid memberId, Guid platformId, CancellationToken ct = default)
    {
        var platform = await _platformRepo.GetByIdAsync(
            PublishPlatformId.Of(platformId), ct)
            ?? throw new NotFoundException($"平台记录 {platformId} 不存在");

        // Verify ownership via parent task
        var task = await _taskRepo.GetByIdAndMemberAsync(
            PublishTaskId.Of(platform.PublishTaskId.Value), memberId, ct)
            ?? throw new NotFoundException("发布任务不存在");

        // Pending 也放行：桌面端"继续"（ResumeTask）在暂停被正确处理后服务端仍是 Pending(0)，
        // 调 retry 只为兜底重置（Failed 遗留记录）或确认待执行状态，此时不应报错。
        if (platform.Status != PublishPlatformStatus.Failed
            && platform.Status != PublishPlatformStatus.Pending)
            throw new ValidationException("只能重试失败或待发布的任务");

        platform.Retry();
        await _platformRepo.SaveChangesAsync(ct);
    }

    // ═══ Delete ═══

    /// <summary>
    /// Deletes a publish task and cascades to all its platform records.
    /// </summary>
    public async Task DeleteAsync(
        Guid memberId, Guid taskId, CancellationToken ct = default)
    {
        var task = await _taskRepo.GetByIdAndMemberAsync(
            PublishTaskId.Of(taskId), memberId, ct)
            ?? throw new NotFoundException($"发布任务 {taskId} 不存在");

        // Cascade: delete platform records first
        await _platformRepo.DeleteByTaskAsync(task.Id, ct);
        await _taskRepo.DeleteAsync(task, ct);
        await _taskRepo.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Deletes a single platform record. If it was the last platform for its task,
    /// also deletes the parent task (empty aggregate root is useless).
    /// </summary>
    public async Task DeletePlatformAsync(
        Guid memberId, Guid platformId, CancellationToken ct = default)
    {
        var platform = await _platformRepo.GetByIdAsync(
            PublishPlatformId.Of(platformId), ct)
            ?? throw new NotFoundException($"平台记录 {platformId} 不存在");

        // Verify ownership via parent task
        var task = await _taskRepo.GetByIdAndMemberAsync(
            PublishTaskId.Of(platform.PublishTaskId.Value), memberId, ct)
            ?? throw new NotFoundException("发布任务不存在");

        var allPlatforms = await _platformRepo.GetByTaskAsync(task.Id, ct);

        if (allPlatforms.Count <= 1)
        {
            // Last platform → delete entire task
            await _platformRepo.DeleteByTaskAsync(task.Id, ct);
            await _taskRepo.DeleteAsync(task, ct);
        }
        else
        {
            // Other platforms remain → just delete this one
            await _platformRepo.DeleteAsync(platform, ct);
        }

        await _taskRepo.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Admin delete for a publish task — no ownership check. Only super admins should call this.
    /// </summary>
    public async Task AdminDeleteTaskAsync(Guid taskId, CancellationToken ct = default)
    {
        var task = await _taskRepo.GetByIdAsync(PublishTaskId.Of(taskId), ct)
            ?? throw new NotFoundException($"发布任务 {taskId} 不存在");

        // Cascade: delete platform records first
        await _platformRepo.DeleteByTaskAsync(task.Id, ct);
        await _taskRepo.DeleteAsync(task, ct);
        await _taskRepo.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Admin delete for a single platform record — no ownership check.
    /// If it was the last platform, also deletes the parent task.
    /// </summary>
    public async Task AdminDeletePlatformAsync(Guid platformId, CancellationToken ct = default)
    {
        var platform = await _platformRepo.GetByIdAsync(
            PublishPlatformId.Of(platformId), ct)
            ?? throw new NotFoundException($"平台记录 {platformId} 不存在");

        var task = await _taskRepo.GetByIdAsync(
            PublishTaskId.Of(platform.PublishTaskId.Value), ct)
            ?? throw new NotFoundException("发布任务不存在");

        var allPlatforms = await _platformRepo.GetByTaskAsync(task.Id, ct);

        if (allPlatforms.Count <= 1)
        {
            // Last platform → delete entire task
            await _platformRepo.DeleteByTaskAsync(task.Id, ct);
            await _taskRepo.DeleteAsync(task, ct);
        }
        else
        {
            // Other platforms remain → just delete this one
            await _platformRepo.DeleteAsync(platform, ct);
        }

        await _taskRepo.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Marks a published platform record as hidden from the upload queue.
    /// This is a soft-delete — the record stays in DB for the video management page.
    /// Only works on Published records (other statuses should be hard-deleted instead).
    /// </summary>
    public async Task HideFromQueueAsync(
        Guid memberId, Guid platformId, CancellationToken ct = default)
    {
        var platform = await _platformRepo.GetByIdAsync(
            PublishPlatformId.Of(platformId), ct)
            ?? throw new NotFoundException($"平台记录 {platformId} 不存在");

        // Verify ownership via parent task
        var task = await _taskRepo.GetByIdAndMemberAsync(
            PublishTaskId.Of(platform.PublishTaskId.Value), memberId, ct)
            ?? throw new NotFoundException("发布任务不存在");

        platform.HideFromQueue();
        await _platformRepo.SaveChangesAsync(ct);
    }

    // ═══ Video Management (Paginated Platform List) ═══

    /// <summary>
    /// Returns paginated platform records for the video management page.
    /// Only returns Published records (status=4). Each row = one platform record.
    /// Optionally filtered by platform key and a keyword (title / task title / file name);
    /// the response also carries per-platform facet counts for the filter chips.
    /// </summary>
    public async Task<PlatformPageResponse> GetPlatformsPagedAsync(
        Guid memberId, int page = 1, int pageSize = 20,
        string? platformKey = null, string? keyword = null, CancellationToken ct = default)
    {
        // 越界防护（客户端与老版都可能传 0）：page=0 会让 Skip(-20) 直接抛 ArgumentException → 500；
        // pageSize 以前原样下传，客户端要 10000 就真查 10000 条。
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var (platforms, total) = await _platformRepo.GetPagedByMemberAsync(
            memberId, page, pageSize, PublishPlatformStatus.Published, platformKey, keyword, ct);

        // Batch-load parent tasks
        var taskIds = platforms.Select(p => p.PublishTaskId).Distinct().ToList();
        var taskMap = new Dictionary<PublishTaskId, PublishTask>();
        foreach (var tid in taskIds)
        {
            var task = await _taskRepo.GetByIdAsync(tid, ct);
            if (task is not null)
                taskMap[tid] = task;
        }

        var items = platforms.Select(p => ToPageItem(p, taskMap.GetValueOrDefault(p.PublishTaskId))).ToList();

        // Facet 计数：跟 Keyword、**不跟** PlatformKey —— 否则点一下「抖音」，
        // 其余三个 chip 的计数会一起变成抖音的数，筛选器就没法用了。
        var counts = await _platformRepo.GetPlatformCountsAsync(
            memberId, PublishPlatformStatus.Published, keyword, ct);

        return new PlatformPageResponse(
            items, total, page, pageSize,
            counts.Select(kv => new PlatformCountItem(kv.Key, kv.Value)).ToList());
    }

    /// <summary>
    /// Every **published** platform record of one publish task — backs「删除该视频全部副本」。
    ///
    /// 只取 Published：归档页只列已发布记录，用户看到几条就该删几条，
    /// 把没发成功的历史行也算进「全部副本」会让他删掉自己没见过的东西。
    /// 归属校验走父任务（平台记录本身没有 member_id）。
    /// </summary>
    public async Task<List<PlatformPageItem>> GetPlatformsByTaskAsync(
        Guid memberId, Guid taskId, CancellationToken ct = default)
    {
        var task = await _taskRepo.GetByIdAndMemberAsync(PublishTaskId.Of(taskId), memberId, ct)
            ?? throw new NotFoundException($"发布任务 {taskId} 不存在");

        var platforms = await _platformRepo.GetByTaskAsync(task.Id, ct);
        return platforms
            .Where(p => p.Status == PublishPlatformStatus.Published)
            .Select(p => ToPageItem(p, task))
            .ToList();
    }

    /// <summary>平台记录 + 父任务 → 归档页的一行（分页接口与 by-task 接口共用，语义只有一份）</summary>
    private static PlatformPageItem ToPageItem(PublishPlatform p, PublishTask? task) => new(
        TaskId: task?.Id.Value ?? Guid.Empty,
        VideoFileName: task?.VideoFileName ?? "",
        VideoFilePath: task?.VideoFilePath ?? "",
        VideoFileSize: task?.VideoFileSize ?? 0,
        CommonTitle: task?.CommonTitle,
        PlatformId: p.Id.Value,
        PlatformKey: p.PlatformKey,
        PlatformDisplayName: PlatformNames.GetDisplayName(p.PlatformKey),
        PlatformSessionId: p.PlatformSessionId,
        Title: p.Title,
        Description: p.Description,
        CoverFilePath: p.CoverFilePath,
        CoverUrl: p.CoverUrl,
        CollectionId: p.CollectionId,
        ExtraDataJson: p.ExtraDataJson,
        Status: (int)p.Status,
        StatusText: PlatformStatusToText(p.Status),
        ProgressPct: p.ProgressPct,
        ErrorMsg: p.ErrorMsg,
        RetryCount: p.RetryCount,
        PlatformVideoId: p.PlatformVideoId,
        PlatformVideoUrl: p.PlatformVideoUrl,
        PublishedAt: p.PublishedAt,
        CreatedAt: p.CreatedAt ?? Clock.Now,
        HiddenInQueue: p.HiddenInQueue
    );

    // ═══ Admin List ═══

    /// <summary>
    /// Returns paginated list of all publish tasks (admin view).
    /// </summary>
    public async Task<AdminPublishListResponse> GetAdminListAsync(
        int page = 1, int pageSize = 20,
        string? titleSearch = null, Guid? memberId = null,
        PublishTaskStatus? status = null,
        DateTime? dateFrom = null, DateTime? dateTo = null,
        CancellationToken ct = default)
    {
        var (items, total) = await _taskRepo.GetAllPagedAsync(
            page, pageSize, titleSearch, memberId, status, dateFrom, dateTo, ct);

        var memberIds = items.Select(t => t.MemberId).Distinct().ToList();
        var memberMap = new Dictionary<Guid, string>();
        foreach (var mid in memberIds)
        {
            var member = await _memberRepo.GetByIdAsync(MemberId.Of(mid), ct);
            memberMap[mid] = member?.Username ?? "已删除";
        }

        var result = new List<AdminPublishItem>();
        foreach (var t in items)
        {
            var platforms = await _platformRepo.GetByTaskAsync(t.Id, ct);
            result.Add(new AdminPublishItem(
                t.Id.Value,
                t.VideoFileName,
                t.CommonTitle,
                memberMap.GetValueOrDefault(t.MemberId, ""),
                t.VideoFileSize,
                platforms.Count,
                platforms.Count(p => p.Status == PublishPlatformStatus.Published),
                platforms.Count(p => p.Status == PublishPlatformStatus.Failed),
                (int)t.Status,
                TaskStatusToText(t.Status),
                t.CreatedAt ?? Clock.Now,
                t.LastModified
            ));
        }

        return new AdminPublishListResponse(result, total, page, pageSize);
    }

    /// <summary>
    /// Returns paginated list of all platform publish records (admin view).
    /// Each row = one platform record with parent task + member info.
    /// </summary>
    public async Task<AdminPlatformListResponse> GetAdminPlatformsListAsync(
        int page = 1, int pageSize = 20,
        string? platformKey = null, Guid? memberId = null,
        PublishPlatformStatus? status = null, string? titleSearch = null,
        DateTime? dateFrom = null, DateTime? dateTo = null,
        CancellationToken ct = default)
    {
        var (platforms, total) = await _platformRepo.GetAllPagedAsync(
            page, pageSize, platformKey, memberId, status, titleSearch, dateFrom, dateTo, ct);

        // Batch-load parent tasks
        var taskIds = platforms.Select(p => p.PublishTaskId).Distinct().ToList();
        var taskMap = new Dictionary<PublishTaskId, PublishTask>();
        foreach (var tid in taskIds)
        {
            var task = await _taskRepo.GetByIdAsync(tid, ct);
            if (task is not null) taskMap[tid] = task;
        }

        // Batch-load members
        var memberIds = taskMap.Values.Select(t => t.MemberId).Distinct().ToList();
        var memberMap = new Dictionary<Guid, string>();
        foreach (var mid in memberIds)
        {
            var member = await _memberRepo.GetByIdAsync(MemberId.Of(mid), ct);
            memberMap[mid] = member?.Username ?? "已删除";
        }

        var items = platforms.Select(p =>
        {
            var task = taskMap.GetValueOrDefault(p.PublishTaskId);
            return new AdminPlatformItem(
                TaskId: task?.Id.Value ?? Guid.Empty,
                VideoFileName: task?.VideoFileName ?? "",
                VideoFilePath: task?.VideoFilePath ?? "",
                VideoFileSize: task?.VideoFileSize ?? 0,
                CommonTitle: task?.CommonTitle,
                MemberUsername: task is not null
                    ? memberMap.GetValueOrDefault(task.MemberId, "")
                    : "",
                PlatformId: p.Id.Value,
                PlatformKey: p.PlatformKey,
                PlatformDisplayName: PlatformNames.GetDisplayName(p.PlatformKey),
                PlatformSessionId: p.PlatformSessionId,
                Title: p.Title,
                Description: p.Description,
                CoverUrl: p.CoverUrl,
                ExtraDataJson: p.ExtraDataJson,
                Status: (int)p.Status,
                StatusText: PlatformStatusToText(p.Status),
                ProgressPct: p.ProgressPct,
                ErrorMsg: p.ErrorMsg,
                RetryCount: p.RetryCount,
                PlatformVideoId: p.PlatformVideoId,
                PlatformVideoUrl: p.PlatformVideoUrl,
                PublishedAt: p.PublishedAt,
                CreatedAt: p.CreatedAt ?? Clock.Now
            );
        }).ToList();

        return new AdminPlatformListResponse(items, total, page, pageSize);
    }

    // ═══ Platforms List (Upload Queue) ═══

    /// <summary>
    /// Returns all platform records for the current member,
    /// with key task-level fields (VideoFileName, VideoFilePath) included.
    /// Used by the upload queue page to load/sync from server.
    /// Excludes records where HiddenInQueue = true (user dismissed from queue).
    /// </summary>
    public async Task<List<PublishPlatformWithTaskDto>> GetPlatformsAsync(
        Guid memberId, CancellationToken ct = default)
    {
        var tasks = await _taskRepo.GetByMemberAsync(memberId, ct);
        var result = new List<PublishPlatformWithTaskDto>();

        foreach (var t in tasks)
        {
            var platforms = await _platformRepo.GetByTaskAsync(t.Id, ct);
            foreach (var p in platforms.Where(p => !p.HiddenInQueue))
            {
                result.Add(new PublishPlatformWithTaskDto(
                    t.Id.Value,
                    t.VideoFileName,
                    t.VideoFilePath,
                    p.Id.Value,
                    p.PlatformKey,
                    PlatformNames.GetDisplayName(p.PlatformKey),
                    p.PlatformSessionId,
                    p.Title,
                    p.Description,
                    p.CoverFilePath,
                    p.CoverUrl,
                    p.CollectionId,
                    p.ExtraDataJson,
                    (int)p.Status,
                    PlatformStatusToText(p.Status),
                    p.ProgressPct,
                    p.ErrorMsg,
                    p.RetryCount,
                    p.PlatformVideoId,
                    p.PlatformVideoUrl,
                    p.PublishedAt,
                    p.CreatedAt ?? Clock.Now,
                    p.LastModified ?? p.CreatedAt ?? Clock.Now
                ));
            }
        }

        return result;
    }

    // ═══ Mappers ═══

    private static PublishPlatformDto ToPlatformDto(PublishPlatform p) => new(
        p.Id.Value,
        p.PlatformKey,
        PlatformNames.GetDisplayName(p.PlatformKey),
        p.PlatformSessionId,
        p.Title,
        p.Description,
        p.CoverFilePath,
        p.CoverUrl,
        p.CollectionId,
        p.ExtraDataJson,
        (int)p.Status,
        PlatformStatusToText(p.Status),
        p.ProgressPct,
        p.ErrorMsg,
        p.RetryCount,
        p.PlatformVideoId,
        p.PlatformVideoUrl,
        p.PublishedAt,
        p.CreatedAt ?? Clock.Now
    );

    private static string TaskStatusToText(PublishTaskStatus status) => status switch
    {
        PublishTaskStatus.Pending => "等待提交",
        PublishTaskStatus.Processing => "进行中",
        PublishTaskStatus.Completed => "全部完成",
        PublishTaskStatus.PartialFailed => "部分失败",
        PublishTaskStatus.Failed => "全部失败",
        _ => "未知"
    };

    private static string PlatformStatusToText(PublishPlatformStatus status) => status switch
    {
        PublishPlatformStatus.Pending => "等待处理",
        PublishPlatformStatus.Uploading => "上传中",
        PublishPlatformStatus.Transcoding => "等待转码",
        PublishPlatformStatus.Publishing => "发布中",
        PublishPlatformStatus.Published => "已发布",
        PublishPlatformStatus.Failed => "发布失败",
        _ => "未知"
    };
}
