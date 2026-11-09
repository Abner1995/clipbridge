namespace CreatFlow.Application.Identity.Services;

/// <summary>
/// Manages collections — create, update, delete, and sync to platforms.
/// Called by desktop app via the CollectionController.
/// </summary>
public class CollectionService
{
    private readonly ICollectionRepository _collectionRepo;
    private readonly ICollectionPlatformRepository _platformRepo;
    private readonly IMemberRepository _memberRepo;

    public CollectionService(
        ICollectionRepository collectionRepo,
        ICollectionPlatformRepository platformRepo,
        IMemberRepository memberRepo)
    {
        _collectionRepo = collectionRepo;
        _platformRepo = platformRepo;
        _memberRepo = memberRepo;
    }

    // ═══ List ═══

    /// <summary>
    /// Returns all collections for a member with platform counts.
    /// </summary>
    public async Task<List<CollectionDto>> GetListAsync(
        Guid memberId, CancellationToken ct = default)
    {
        var collections = await _collectionRepo.GetByMemberAsync(memberId, ct);
        var result = new List<CollectionDto>();

        foreach (var c in collections)
        {
            var platforms = await _platformRepo.GetByCollectionAsync(c.Id, ct);
            var platformSummaries = platforms.Select(cp => new PlatformSummaryDto(
                cp.PlatformKey,
                cp.PlatformCollectionId,
                cp.PlatformSessionId,
                null  // v1: session label 后续补充
            )).ToList();

            result.Add(new CollectionDto(
                c.Id.Value,
                c.Title,
                c.Description,
                c.CoverImagePath,
                c.CoverImageUrl,
                platforms.Count,
                platforms.Count(p => p.Status == CollectionPlatformStatus.Created),
                c.CreatedAt ?? Clock.Now,
                c.LastModified,
                platformSummaries,
                (int)c.ContentType
            ));
        }

        return result;
    }

    // ═══ Detail ═══

    /// <summary>
    /// Returns a single collection with all its platform records.
    /// </summary>
    public async Task<CollectionDetailDto> GetDetailAsync(
        Guid memberId, Guid collectionId, CancellationToken ct = default)
    {
        var collection = await _collectionRepo.GetByIdAndMemberAsync(
            CollectionId.Of(collectionId), memberId, ct)
            ?? throw new NotFoundException($"合集 {collectionId} 不存在");

        var platforms = await _platformRepo.GetByCollectionAsync(collection.Id, ct);

        return new CollectionDetailDto(
            collection.Id.Value,
            collection.Title,
            collection.Description,
            collection.CoverImagePath,
            collection.CoverImageUrl,
            platforms.Select(ToPlatformDto).ToList(),
            collection.CreatedAt ?? Clock.Now,
            collection.LastModified,
            (int)collection.ContentType
        );
    }

    // ═══ Admin List (all members) ═══

    /// <summary>
    /// Returns paginated list of all collections (admin view).
    /// Includes member username and per-collection platform sync counts.
    /// </summary>
    public async Task<AdminCollectionListResponse> GetAdminListAsync(
        int page = 1, int pageSize = 20,
        string? titleSearch = null, Guid? memberId = null,
        DateTime? dateFrom = null, DateTime? dateTo = null,
        CancellationToken ct = default)
    {
        var (items, total) = await _collectionRepo.GetAllPagedAsync(
            page, pageSize, titleSearch, memberId, dateFrom, dateTo, ct);

        // Batch-fetch member usernames
        var memberIds = items.Select(c => c.MemberId).Distinct().ToList();
        var memberMap = new Dictionary<Guid, string>();
        foreach (var mid in memberIds)
        {
            var member = await _memberRepo.GetByIdAsync(MemberId.Of(mid), ct);
            memberMap[mid] = member?.Username ?? "已删除";
        }

        var result = new List<AdminCollectionItem>();
        foreach (var c in items)
        {
            var platforms = await _platformRepo.GetByCollectionAsync(c.Id, ct);
            result.Add(new AdminCollectionItem(
                c.Id.Value,
                c.Title,
                c.Description,
                c.CoverImagePath,
                c.CoverImageUrl,
                memberMap.GetValueOrDefault(c.MemberId, ""),
                platforms.Count,
                platforms.Count(p => p.Status == CollectionPlatformStatus.Created),
                c.CreatedAt ?? Clock.Now,
                c.LastModified
            ));
        }

        return new AdminCollectionListResponse(result, total, page, pageSize);
    }

    // ═══ Create ═══

    /// <summary>
    /// Returns platform records for a collection (admin view).
    /// </summary>
    public async Task<List<CollectionPlatformDto>> GetPlatformsAsync(
        Guid collectionId, CancellationToken ct = default)
    {
        var platforms = await _platformRepo.GetByCollectionAsync(
            CollectionId.Of(collectionId), ct);
        return platforms.Select(ToPlatformDto).ToList();
    }

    // ═══ Create ═══
    /// Each target specifies platform + session (account) + optional collection type.
    /// </summary>
    public async Task<CollectionDetailDto> CreateAsync(
        Guid memberId, CreateCollectionRequest request, CancellationToken ct = default)
    {
        // ── 合集类型（2026-11-05）：文章合集仅头条，且必须由服务端兜底校验 ──
        if (!Enum.IsDefined(typeof(CollectionContentType), request.ContentType))
            throw new ValidationException($"未知的合集类型 {request.ContentType}");

        var contentType = (CollectionContentType)request.ContentType;
        if (contentType == CollectionContentType.Article
            && request.Targets is { Count: > 0 }
            && request.Targets.Any(t => t.PlatformKey != PlatformNames.Toutiao))
        {
            throw new ValidationException("文章合集仅支持今日头条，不能同时选择其他平台");
        }

        // 头条文章合集接口没有简介字段；文章合集的描述只会在本地留一份死数据，
        // 且表单已隐藏简介 —— 入库前直接清空。
        var description = contentType == CollectionContentType.Article
            ? null
            : request.Description;

        var collection = Collection.Create(
            CollectionId.New(),
            memberId,
            request.Title,
            description,
            request.CoverImagePath,
            contentType);

        await _collectionRepo.AddAsync(collection, ct);

        var platformRecords = new List<CollectionPlatform>();

        if (request.Targets is { Count: > 0 })
        {
            foreach (var target in request.Targets)
            {
                var cp = CollectionPlatform.Create(
                    CollectionPlatformId.New(),
                    collection.Id,
                    target.PlatformKey,
                    platformSessionId: target.PlatformSessionId,
                    collectionType: target.CollectionType,
                    title: collection.Title,
                    description: collection.Description,
                    coverImage: collection.CoverImageUrl);
                platformRecords.Add(cp);
            }

            foreach (var cp in platformRecords)
                await _platformRepo.AddAsync(cp, ct);
        }

        await _collectionRepo.SaveChangesAsync(ct);

        return new CollectionDetailDto(
            collection.Id.Value,
            collection.Title,
            collection.Description,
            collection.CoverImagePath,
            collection.CoverImageUrl,
            platformRecords.Select(ToPlatformDto).ToList(),
            collection.CreatedAt ?? Clock.Now,
            collection.LastModified,
            (int)collection.ContentType
        );
    }

    // ═══ Update ═══

    /// <summary>
    /// Updates collection metadata (title, description, cover).
    /// </summary>
    public async Task UpdateAsync(
        Guid memberId, UpdateCollectionRequest request, CancellationToken ct = default)
    {
        var collection = await _collectionRepo.GetByIdAndMemberAsync(
            CollectionId.Of(request.Id), memberId, ct)
            ?? throw new NotFoundException($"合集 {request.Id} 不存在");

        collection.Update(request.Title, request.Description, request.CoverImagePath);
        await _collectionRepo.SaveChangesAsync(ct);
    }

    // ═══ Delete ═══

    /// <summary>
    /// Deletes a collection and cascades to all its platform records.
    /// </summary>
    public async Task DeleteAsync(
        Guid memberId, DeleteCollectionRequest request, CancellationToken ct = default)
    {
        var collection = await _collectionRepo.GetByIdAndMemberAsync(
            CollectionId.Of(request.Id), memberId, ct)
            ?? throw new NotFoundException($"合集 {request.Id} 不存在");

        // Cascade: delete platform records first
        await _platformRepo.DeleteByCollectionAsync(collection.Id, ct);

        await _collectionRepo.DeleteAsync(collection, ct);
        await _collectionRepo.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Admin delete — no ownership check. Only super admins should call this.
    /// </summary>
    public async Task AdminDeleteAsync(Guid collectionId, CancellationToken ct = default)
    {
        var collection = await _collectionRepo.GetByIdAsync(
            CollectionId.Of(collectionId), ct)
            ?? throw new NotFoundException($"合集 {collectionId} 不存在");

        // Cascade: delete platform records first
        await _platformRepo.DeleteByCollectionAsync(collection.Id, ct);

        await _collectionRepo.DeleteAsync(collection, ct);
        await _collectionRepo.SaveChangesAsync(ct);
    }

    // ═══ Sync to Platforms ═══

    /// <summary>
    /// Syncs (or re-syncs) a collection to specified platforms.
    /// Creates or updates CollectionPlatform records.
    /// Actual API calls to platforms happen later in the upload pipeline.
    /// </summary>
    public async Task<CollectionSyncResultDto> SyncAsync(
        Guid memberId, SyncCollectionRequest request, CancellationToken ct = default)
    {
        var collection = await _collectionRepo.GetByIdAndMemberAsync(
            CollectionId.Of(request.CollectionId), memberId, ct)
            ?? throw new NotFoundException($"合集 {request.CollectionId} 不存在");

        // 文章合集仅头条：编辑补账号时也不能混入其他平台
        if (collection.ContentType == CollectionContentType.Article
            && request.Targets?.Any(t => t.PlatformKey != PlatformNames.Toutiao) == true)
        {
            throw new ValidationException("文章合集仅支持今日头条，不能同时添加其他平台");
        }

        var results = new List<CollectionPlatform>();
        // 多账号体系：同合集同平台可存在多条记录（每登录会话一条）——
        // existing 必须按 (平台 + 会话) 精确匹配，仅按平台匹配会把新账号的建档
        // 错并入其它账号的旧记录（UpdatePlatformInfo 只刷文案不换会话，越走越乱）。
        var records = await _platformRepo.GetByCollectionAsync(collection.Id, ct);

        foreach (var target in request.Targets)
        {
            // Check for existing record (re-sync scenario)：会话精确匹配；
            // 目标不带会话（历史空壳记录/未选号）时退化为按平台取无会话记录，再退化为任一记录
            var existing = target.PlatformSessionId != null
                ? records.FirstOrDefault(cp => cp.PlatformKey == target.PlatformKey
                    && cp.PlatformSessionId == target.PlatformSessionId)
                : records.FirstOrDefault(cp => cp.PlatformKey == target.PlatformKey
                    && cp.PlatformSessionId == null)
                    ?? records.FirstOrDefault(cp => cp.PlatformKey == target.PlatformKey);

            if (existing != null)
            {
                // Re-sync: update platform info, reset to pending
                existing.UpdatePlatformInfo(
                    target.Title, target.Description,
                    target.CoverImage, target.ExtraDataJson);
                results.Add(existing);
            }
            else
            {
                var cp = CollectionPlatform.Create(
                    CollectionPlatformId.New(),
                    collection.Id,
                    target.PlatformKey,
                    target.PlatformSessionId,
                    collectionType: null,
                    title: target.Title ?? collection.Title,
                    description: target.Description ?? collection.Description,
                    coverImage: target.CoverImage ?? collection.CoverImageUrl,
                    extraDataJson: target.ExtraDataJson);
                await _platformRepo.AddAsync(cp, ct);
                results.Add(cp);
            }
        }

        await _platformRepo.SaveChangesAsync(ct);

        return new CollectionSyncResultDto(
            collection.Id.Value,
            results.Select(ToPlatformDto).ToList()
        );
    }

    // ═══ Report Sync Result ═══

    /// <summary>
    /// Called by the desktop app after a platform SDK call completes.
    /// Updates a single CollectionPlatform record to Created or Failed.
    /// On success, also syncs the platform-side title/description/coverImage back.
    /// </summary>
    public async Task ReportAsync(
        Guid collectionPlatformId, ReportCollectionRequest request,
        CancellationToken ct = default)
    {
        var cp = await _platformRepo.GetByIdAsync(
            CollectionPlatformId.Of(collectionPlatformId), ct)
            ?? throw new NotFoundException($"平台记录 {collectionPlatformId} 不存在");

        if (request.Success)
        {
            cp.MarkCreated(request.PlatformCollectionId
                ?? throw new ArgumentNullException(nameof(request.PlatformCollectionId),
                    "成功时必须提供平台合集 ID"));

            // 同步平台侧返回的 title / description / coverImage
            if (request.Title != null || request.Description != null || request.CoverImageUrl != null)
            {
                cp.UpdatePlatformInfo(request.Title, request.Description,
                    request.CoverImageUrl, extraDataJson: null);
            }

            // 如果有封面图 TOS key，回填到父合集（替换 CDN URL）
            if (!string.IsNullOrWhiteSpace(request.CoverImageUrl))
            {
                var collection = await _collectionRepo.GetByIdAsync(cp.CollectionId, ct);
                if (collection != null)
                {
                    collection.SetCoverImageUrl(request.CoverImageUrl);
                }
            }
        }
        else
        {
            cp.MarkFailed(request.ErrorMsg ?? "未知错误");
        }

        await _platformRepo.SaveChangesAsync(ct);
    }

    // ═══ Mappers ═══

    private static CollectionPlatformDto ToPlatformDto(CollectionPlatform cp) => new(
        cp.Id.Value,
        cp.PlatformKey,
        PlatformNames.GetDisplayName(cp.PlatformKey),
        cp.PlatformSessionId,
        cp.PlatformCollectionId,
        cp.CollectionType,
        cp.Title,
        cp.Description,
        cp.CoverImage,
        (int)cp.Status,
        StatusToText(cp.Status),
        cp.ErrorMsg,
        cp.ExtraDataJson,
        cp.CreatedAt ?? Clock.Now
    );

    private static string StatusToText(CollectionPlatformStatus status) => status switch
    {
        CollectionPlatformStatus.Pending => "待同步",
        CollectionPlatformStatus.Created => "已同步",
        CollectionPlatformStatus.Failed => "同步失败",
        _ => "未知"
    };
}
