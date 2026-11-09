using System.Text.Json;
using CreatFlow.Application.Exceptions;
using CreatFlow.Application.Identity.Services;
using CreatFlow.Domain.Exceptions;
using CreatFlow.LocalStore;
using Microsoft.Extensions.DependencyInjection;
using Dto = CreatFlow.Application.DTOs;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 单机版本地 API 路由 —— <see cref="ApiClient.LocalHandler"/> 的唯一实现。
///
/// 页面/管线仍照原样调 <c>/api/*</c>，由这里按路径分发到 Application 业务服务，
/// 再落到本地 SQLite。这样「先删平台后删记录」「合集全组清完才删本地」
/// 「认领不改状态」等规则只有一份实现（联网版服务端同一份服务类），不会分叉。
///
/// 契约纪律：
///   · 请求体 / 响应体**只靠 JSON 属性名**与客户端 DTO 对齐（不手写映射）；
///     客户端 DTO 在 CreatFlow.NewApp.Models，服务端 DTO 在 CreatFlow.Application.Dtos，
///     两边属性名逐字一致 —— 改名会静默丢字段，改任何一边都要同步另一边。
///   · 响应包裹形状必须与服务端 Controller 一致（{ tasks } / { sessions } / { items } …）。
///   · 未注册路径**报错而不是静默走 HTTP**：单机版没有服务器，静默会变成 15 秒超时。
///
/// 路由表是字典（不是 switch）：<see cref="HandledPaths"/> 直接来自表本身，
/// --selftest 才能诚实地断言「客户端会调的每条路径都有本地实现」。
/// </summary>
public static class LocalApiRouter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>一条本地路由：拿到 DI scope 的业务服务容器 + 请求体，返回客户端要的响应对象。</summary>
    private delegate Task<object?> Route(
        IServiceProvider services, Guid memberId, object? body, CancellationToken ct);

    /// <summary>
    /// 本地已实现的路径表。加分支 = 加一项；客户端新增 /api/* 调用时先在这里补上，
    /// 否则运行期会抛「单机版未实现接口」（自检也会拦下）。
    /// </summary>
    private static readonly Dictionary<string, Route> Routes = new(StringComparer.Ordinal)
    {
        // ═══ 平台会话 ═══
        ["/api/platform-sessions/list"] = async (sp, memberId, body, ct) =>
        {
            var req = ReqOrNull<ListSessionsRequest>(body);
            var svc = sp.GetRequiredService<PlatformSessionService>();
            return new { sessions = await svc.GetListAsync(memberId, req?.PlatformKey, ct) };
        },
        ["/api/platform-sessions/sync"] = async (sp, memberId, body, ct) =>
        {
            var svc = sp.GetRequiredService<PlatformSessionService>();
            return await svc.SyncAsync(memberId, Req<Dto.SyncPlatformSessionRequest>(body), ct);
        },
        ["/api/platform-sessions/rename"] = async (sp, memberId, body, ct) =>
        {
            var svc = sp.GetRequiredService<PlatformSessionService>();
            await svc.RenameAsync(memberId, Req<Dto.RenamePlatformSessionRequest>(body), ct);
            return Ok();
        },
        ["/api/platform-sessions/set-primary"] = async (sp, memberId, body, ct) =>
        {
            var svc = sp.GetRequiredService<PlatformSessionService>();
            await svc.SetPrimaryAsync(memberId, Req<Dto.SetPrimaryRequest>(body), ct);
            return Ok();
        },
        ["/api/platform-sessions/delete"] = async (sp, memberId, body, ct) =>
        {
            var svc = sp.GetRequiredService<PlatformSessionService>();
            return await svc.DeleteAsync(memberId, Req<Dto.DeletePlatformSessionRequest>(body), ct);
        },
        ["/api/platform-sessions/credentials"] = async (sp, memberId, body, ct) =>
        {
            var svc = sp.GetRequiredService<PlatformSessionService>();
            var req = Req<Dto.GetSessionCredentialsRequest>(body);
            return await svc.GetCredentialsAsync(memberId, req.SessionId, ct);
        },
        ["/api/platform-sessions/probe"] = async (sp, memberId, body, ct) =>
        {
            var req = ReqOrNull<Dto.ProbePlatformSessionsRequest>(body);
            var svc = sp.GetRequiredService<PlatformSessionService>();
            return new { results = await svc.ProbeAsync(memberId, req?.PlatformKey, ct) };
        },

        // ═══ 发布 / 上传队列 / 视频管理 ═══
        ["/api/publish/submit"] = async (sp, memberId, body, ct) =>
        {
            var svc = sp.GetRequiredService<PublishService>();
            return await svc.SubmitAsync(memberId, Req<Dto.SubmitPublishRequest>(body), ct);
        },
        ["/api/publish/report"] = async (sp, memberId, body, ct) =>
        {
            var svc = sp.GetRequiredService<PublishService>();
            await svc.ReportAsync(Req<Dto.ReportPublishRequest>(body), ct);
            return Ok();
        },
        ["/api/publish/retry"] = async (sp, memberId, body, ct) =>
        {
            var svc = sp.GetRequiredService<PublishService>();
            await svc.RetryAsync(memberId, Req<Dto.RetryPublishRequest>(body).PlatformId, ct);
            return Ok();
        },
        ["/api/publish/list"] = async (sp, memberId, body, ct) =>
        {
            var svc = sp.GetRequiredService<PublishService>();
            return new { tasks = await svc.GetListAsync(memberId, ct) };
        },
        ["/api/publish/detail"] = async (sp, memberId, body, ct) =>
        {
            var svc = sp.GetRequiredService<PublishService>();
            return await svc.GetDetailAsync(memberId, Req<Dto.GetPublishDetailRequest>(body).Id, ct);
        },
        ["/api/publish/delete"] = async (sp, memberId, body, ct) =>
        {
            var svc = sp.GetRequiredService<PublishService>();
            await svc.DeleteAsync(memberId, Req<Dto.GetPublishDetailRequest>(body).Id, ct);
            return Ok();
        },
        ["/api/publish/platforms"] = async (sp, memberId, body, ct) =>
        {
            var svc = sp.GetRequiredService<PublishService>();
            return new { platforms = await svc.GetPlatformsAsync(memberId, ct) };
        },
        ["/api/publish/platforms/page"] = async (sp, memberId, body, ct) =>
        {
            var svc = sp.GetRequiredService<PublishService>();
            var req = Req<Dto.PlatformPageRequest>(body);
            return await svc.GetPlatformsPagedAsync(
                memberId, req.Page, req.PageSize, req.PlatformKey, req.Keyword, ct);
        },
        ["/api/publish/platforms/by-task"] = async (sp, memberId, body, ct) =>
        {
            var svc = sp.GetRequiredService<PublishService>();
            var req = Req<Dto.PublishTaskIdRequest>(body);
            return new { items = await svc.GetPlatformsByTaskAsync(memberId, req.TaskId, ct) };
        },
        ["/api/publish/platforms/delete"] = async (sp, memberId, body, ct) =>
        {
            var svc = sp.GetRequiredService<PublishService>();
            await svc.DeletePlatformAsync(memberId, Req<Dto.DeletePlatformRequest>(body).PlatformId, ct);
            return Ok();
        },
        ["/api/publish/platforms/hide"] = async (sp, memberId, body, ct) =>
        {
            var svc = sp.GetRequiredService<PublishService>();
            await svc.HideFromQueueAsync(memberId, Req<Dto.HidePlatformRequest>(body).PlatformId, ct);
            return Ok();
        },

        // ═══ 合集 ═══
        ["/api/collections/list"] = async (sp, memberId, body, ct) =>
        {
            var svc = sp.GetRequiredService<CollectionService>();
            return new { collections = await svc.GetListAsync(memberId, ct) };
        },
        ["/api/collections/detail"] = async (sp, memberId, body, ct) =>
        {
            var svc = sp.GetRequiredService<CollectionService>();
            return await svc.GetDetailAsync(memberId, Req<Dto.GetCollectionDetailRequest>(body).Id, ct);
        },
        ["/api/collections/create"] = async (sp, memberId, body, ct) =>
        {
            var svc = sp.GetRequiredService<CollectionService>();
            return await svc.CreateAsync(memberId, Req<Dto.CreateCollectionRequest>(body), ct);
        },
        ["/api/collections/update"] = async (sp, memberId, body, ct) =>
        {
            var svc = sp.GetRequiredService<CollectionService>();
            await svc.UpdateAsync(memberId, Req<Dto.UpdateCollectionRequest>(body), ct);
            return Ok();
        },
        ["/api/collections/delete"] = async (sp, memberId, body, ct) =>
        {
            var svc = sp.GetRequiredService<CollectionService>();
            await svc.DeleteAsync(memberId, Req<Dto.DeleteCollectionRequest>(body), ct);
            return Ok();
        },
        ["/api/collections/sync"] = async (sp, memberId, body, ct) =>
        {
            var svc = sp.GetRequiredService<CollectionService>();
            return await svc.SyncAsync(memberId, Req<Dto.SyncCollectionRequest>(body), ct);
        },
        ["/api/collections/report"] = async (sp, memberId, body, ct) =>
        {
            var svc = sp.GetRequiredService<CollectionService>();
            var req = Req<Dto.ReportCollectionRequest>(body);
            await svc.ReportAsync(req.CollectionPlatformId, req, ct);
            return Ok();
        },

        // ═══ 批量删除（异步任务）═══
        ["/api/async-tasks/submit"] = async (sp, memberId, body, ct) =>
        {
            var svc = sp.GetRequiredService<AsyncTaskService>();
            return await svc.SubmitDeleteAsync(memberId, Req<Dto.SubmitAsyncDeleteRequest>(body), ct);
        },
        ["/api/async-tasks/list"] = async (sp, memberId, body, ct) =>
        {
            var svc = sp.GetRequiredService<AsyncTaskService>();
            return await svc.GetListAsync(memberId, Req<Dto.AsyncTaskListRequest>(body), ct);
        },
        ["/api/async-tasks/detail"] = async (sp, memberId, body, ct) =>
        {
            var svc = sp.GetRequiredService<AsyncTaskService>();
            return await svc.GetDetailAsync(memberId, Req<Dto.AsyncTaskDetailRequest>(body).TaskId, ct);
        },
        ["/api/async-tasks/claim"] = async (sp, memberId, body, ct) =>
        {
            var svc = sp.GetRequiredService<AsyncTaskService>();
            return await svc.ClaimAsync(memberId, ct);
        },
        ["/api/async-tasks/in-flight"] = async (sp, memberId, body, ct) =>
        {
            var svc = sp.GetRequiredService<AsyncTaskService>();
            return await svc.GetInFlightAsync(memberId, ct);
        },
        ["/api/async-tasks/report"] = async (sp, memberId, body, ct) =>
        {
            var svc = sp.GetRequiredService<AsyncTaskService>();
            return await svc.ReportAsync(memberId, Req<Dto.ReportAsyncTaskItemRequest>(body), ct);
        },
        ["/api/async-tasks/confirm-gone"] = async (sp, memberId, body, ct) =>
        {
            var svc = sp.GetRequiredService<AsyncTaskService>();
            return await svc.ConfirmGoneAsync(memberId, Req<Dto.ConfirmGoneRequest>(body).ItemId, ct);
        },
        ["/api/async-tasks/retry"] = async (sp, memberId, body, ct) =>
        {
            var svc = sp.GetRequiredService<AsyncTaskService>();
            await svc.RetryAsync(memberId, Req<Dto.RetryAsyncTaskRequest>(body), ct);
            return Ok();
        },
        ["/api/async-tasks/delete"] = async (sp, memberId, body, ct) =>
        {
            var svc = sp.GetRequiredService<AsyncTaskService>();
            await svc.DeleteAsync(memberId, Req<Dto.DeleteAsyncTaskRequest>(body).TaskId, ct);
            return Ok();
        },
        ["/api/async-tasks/clear-finished"] = async (sp, memberId, body, ct) =>
        {
            var svc = sp.GetRequiredService<AsyncTaskService>();
            return new { ok = true, removed = await svc.ClearFinishedAsync(memberId, ct) };
        },

        // ═══ 配置 ═══
        ["/api/config/platforms"] = async (sp, memberId, body, ct) =>
        {
            var svc = sp.GetRequiredService<PlatformService>();
            return await svc.GetEnabledAsync(ct);
        },
        ["/api/config/public"] = (_, _, _, _) =>
            // 单机版没有服务端富文本；联系我们页走 AppConfig 本地兜底（ContactViewModel 已分支）
            Task.FromResult<object?>(null),
    };

    /// <summary>本地已实现的路径（--selftest 断言客户端调用面全覆盖）</summary>
    public static IReadOnlyCollection<string> HandledPaths => Routes.Keys;

    /// <summary>本地分发入口。异常映射成客户端熟悉的 <see cref="ApiException"/> 状态码。</summary>
    public static async Task<object?> HandleAsync(string path, object? body, bool authenticated)
    {
        try
        {
            if (!Routes.TryGetValue(path, out var route))
            {
                // 不能静默走 HTTP：单机版没有服务器，静默 = 15 秒超时 + 默认值，问题被藏起来
                Log.Error("单机版未实现接口：{Path}", path);
                throw new ApiException(500, $"单机版未实现接口: {path}");
            }

            // 每个请求独立 scope + 独立 DbContext（WAL 下 UI / 后台消费者各自读写，不跨请求共享状态）
            using var scope = App.Services.CreateScope();
            return await route(scope.ServiceProvider, LocalIdentity.MemberId, body, CancellationToken.None);
        }
        catch (ApiException)
        {
            throw;
        }
        catch (ValidationException ex)
        {
            throw new ApiException(400, ex.Message);
        }
        catch (NotFoundException ex)
        {
            throw new ApiException(404, ex.Message);
        }
        catch (ForbiddenException ex)
        {
            throw new ApiException(403, ex.Message);
        }
        catch (UnauthorizedException ex)
        {
            throw new ApiException(401, ex.Message);
        }
        catch (DomainException ex)
        {
            // 领域校验（如"必须指定至少一个发布目标"）在服务端是 422，客户端按 400 处理
            throw new ApiException(400, ex.Message);
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "[LocalApi] 请求体解析失败：{Path}", path);
            throw new ApiException(400, "请求参数格式错误");
        }
    }

    private static object Ok() => new { ok = true };

    /// <summary>请求体 → Application 请求 DTO（属性名逐字对应，不手写映射）</summary>
    private static T Req<T>(object? body)
    {
        if (body is T typed) return typed;
        var json = body is null ? "{}" : JsonSerializer.Serialize(body, JsonOptions);
        return JsonSerializer.Deserialize<T>(json, JsonOptions)
            ?? throw new ValidationException("请求参数格式错误");
    }

    /// <summary>可空请求体（list / probe 的 platformKey 等）</summary>
    private static T? ReqOrNull<T>(object? body) where T : class
    {
        if (body is null) return null;
        if (body is T typed) return typed;
        var json = JsonSerializer.Serialize(body, JsonOptions);
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    /// <summary>POST /api/platform-sessions/list 的可选筛选（服务端 Controller 内部同形）</summary>
    private sealed record ListSessionsRequest(string? PlatformKey);
}
