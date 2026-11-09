using System.IO.Compression;
using CreatFlow.Application.Common.Interfaces;
using CreatFlow.Domain.ValueObjects;
using CreatFlow.LocalStore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Dto = CreatFlow.Application.DTOs;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 单机版本地库自检（`--selftest` 会一并跑）—— 全部在 TEMP 夹具上做，不碰用户 creatflow.db。
///
/// 守的是「删不掉的记录」等不可逆语义：
///  · 建库 / 迁移 / 种子幂等；固定本机会员 ID；WAL 打开成功；
///  · 发布 submit → report → retry → hide → page / by-task → delete 全链路；
///  · 合集 create → sync → report → update → delete（平台记录级联删）；
///  · 批量删除 submit → claim（不改状态）→ report / confirm-gone → 清本地；
///    合集「全组清完才删本地」、失败保留、清空已完成跳过失败任务；
///  · 会话 sync → list → credentials（Cookie 往返）→ rename / set-primary → delete 硬删级联；
///  · VACUUM INTO 备份可打开；两个 scope 并发写不锁死；
///  · 客户端会调的 /api/* 全部有本地路由，且不包含 C 类（登录/设备/公告/钉钉/云同步）；
///  · 故障注入：坏库初始化报错且原文件保留、坏数据库备份拒绝导入（当前库不被覆盖）；
///  · 平台探活接线：DI 注册了 IPlatformProber，无探针平台零网络返回 no_prober；
///  · 清除本机数据：只删本会员目录、机器级默认保留、可重建、勾选后连模型/日志一起删、幂等。
///
/// 结果写 %TEMP%/newapp_localstore_selftest.txt。
/// </summary>
public static class LocalStoreSelfTest
{
    public static int Run()
    {
        var sb = new StringBuilder();
        var failures = 0;

        void Check(string name, bool ok, string detail = "")
        {
            if (!ok) failures++;
            sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
        }

        sb.AppendLine("═══ 单机版本地库自检 ═══");

        var root = Path.Combine(Path.GetTempPath(), "creatflow-localstore-sel-" + Guid.NewGuid().ToString("N")[..8]);

        // 初始化里的「每日自动备份」按 LocalStorePaths.BackupsDir 落盘 ——
        // 自检期间把根指到 TEMP，绝不写用户的 %LocalAppData%
        var priorRoot = LocalStorePaths.AccountsRootOverride;
        LocalStorePaths.AccountsRootOverride = root;

        // 与真实运行同构：库就在会员目录下（导出/导入按 LocalStorePaths 找库与 JSON）
        var dbPath = LocalStorePaths.DatabasePath;
        var backupDir = LocalStorePaths.BackupsDir;
        var memberId = LocalIdentity.MemberId;

        try
        {
            // ═══ [1] 建库 / 迁移 / 种子 ═══
            sb.AppendLine("\n[1] 建库 / 迁移 / 种子");
            LocalStoreInitializer.EnsureInitialized(dbPath);
            Check("数据库文件已创建", File.Exists(dbPath), dbPath);

            using (var db = LocalStoreInitializer.CreateContext(dbPath))
            {
                Check("平台种子 4 条", db.Platform.Count() == 4, db.Platform.Count().ToString());
                // 值对象比较留在内存里做（EF 表达式里对转换列比较容易翻不成 SQL）
                Check("固定本机会员存在",
                    db.Member.ToList().Any(m => m.Id == MemberId.Of(memberId)));
                var journal = db.Database
                    .SqlQueryRaw<string>("PRAGMA journal_mode;")
                    .AsEnumerable().FirstOrDefault();
                Check("WAL 已打开", string.Equals(journal, "wal", StringComparison.OrdinalIgnoreCase),
                    journal ?? "(null)");
            }

            // 幂等：再跑一次不应重复种子
            LocalStoreInitializer.EnsureInitialized(dbPath);
            using (var db = LocalStoreInitializer.CreateContext(dbPath))
                Check("重复初始化幂等（平台仍 4 条）", db.Platform.Count() == 4);

            var provider = BuildProvider(dbPath);

            // ═══ [2] 发布任务全链路 ═══
            sb.AppendLine("\n[2] 发布：submit → report → retry → hide → page / by-task → delete");
            var sessionId = Guid.NewGuid();
            var (taskId, platformIds) = Run(provider, async sp =>
            {
                var sessions = sp.GetRequiredService<CreatFlow.Application.Identity.Services.PlatformSessionService>();
                var s = await sessions.SyncAsync(memberId, new Dto.SyncPlatformSessionRequest(
                    sessionId, "douyin", "uid-1", "测试号", null,
                    "cookie=douyin=1", null, "{\"csrfToken\":\"t\"}"));

                var publish = sp.GetRequiredService<CreatFlow.Application.Identity.Services.PublishService>();
                var submit = await publish.SubmitAsync(memberId, new Dto.SubmitPublishRequest(
                    "/tmp/video.mp4", "video.mp4", 1024, "hash-1", 12, 1080, 1920,
                    null, "标题", "简介", null,
                    [
                        new Dto.PublishTarget("douyin", s.Id, null, null, null, null, null),
                        new Dto.PublishTarget("kuaishou", null, "快手标题", null, null, null, null),
                    ]));
                return (submit.TaskId, submit.Platforms.Select(p => p.Id).ToList());
            });

            Check("submit 返回任务 + 2 条平台记录", taskId != Guid.Empty && platformIds.Count == 2);

            Run(provider, async sp =>
            {
                var publish = sp.GetRequiredService<CreatFlow.Application.Identity.Services.PublishService>();

                await publish.ReportAsync(new Dto.ReportPublishRequest(
                    platformIds[0], true, "dy-vid-1", "https://dy/1", null, null));
                await publish.ReportAsync(new Dto.ReportPublishRequest(
                    platformIds[1], false, null, null, "平台拒绝", null));

                var platforms = await publish.GetPlatformsAsync(memberId);
                Check("两行状态：已发布 / 失败",
                    platforms.Count == 2
                    && platforms.Any(p => p.PlatformId == platformIds[0] && p.Status == 4)
                    && platforms.Any(p => p.PlatformId == platformIds[1] && p.Status == 5));

                // 已发布不能 retry（只放行 Failed / Pending）
                var rejected = false;
                try { await publish.RetryAsync(memberId, platformIds[0]); }
                catch (CreatFlow.Application.Exceptions.ValidationException) { rejected = true; }
                Check("已发布记录 retry 被拒", rejected);

                await publish.RetryAsync(memberId, platformIds[1]);
                var afterRetry = await publish.GetPlatformsAsync(memberId);
                Check("失败记录 retry 回到待发布",
                    afterRetry.First(p => p.PlatformId == platformIds[1]).Status == 0);

                await publish.HideFromQueueAsync(memberId, platformIds[0]);
                var queueRows = await publish.GetPlatformsAsync(memberId);
                Check("hide 后队列只剩 1 行", queueRows.Count == 1 && queueRows[0].PlatformId == platformIds[1]);

                var page = await publish.GetPlatformsPagedAsync(memberId, 1, 20);
                Check("视频管理只列已发布（含已隐藏）", page.Total == 1
                    && page.Items[0].PlatformId == platformIds[0]
                    && page.Items[0].HiddenInQueue);

                var byTask = await publish.GetPlatformsByTaskAsync(memberId, taskId);
                Check("by-task 返回该视频全部已发布副本", byTask.Count == 1);

                // 最后一条平台记录被删 → 父任务一起删（与联网版同语义）
                await publish.DeletePlatformAsync(memberId, platformIds[0]);
                await publish.DeletePlatformAsync(memberId, platformIds[1]);
                var tasks = await publish.GetListAsync(memberId);
                Check("最后一条记录删除后父任务也消失", tasks.Count == 0);
                return true;
            });

            // ═══ [3] 合集全链路 ═══
            sb.AppendLine("\n[3] 合集：create → sync → report → update → delete");
            Run(provider, async sp =>
            {
                var collections = sp.GetRequiredService<CreatFlow.Application.Identity.Services.CollectionService>();
                var created = await collections.CreateAsync(memberId, new Dto.CreateCollectionRequest(
                    "测试合集", "简介", null,
                    [
                        new Dto.CreateTarget("douyin", sessionId, null),
                        new Dto.CreateTarget("kuaishou", null, null),
                    ]));
                Check("建合集带 2 条平台记录", created.Platforms.Count == 2);

                var synced = await collections.SyncAsync(memberId, new Dto.SyncCollectionRequest(
                    created.Id,
                    [
                        new Dto.SyncTarget("douyin", sessionId, "新标题", null, null, null),
                        new Dto.SyncTarget("kuaishou", null, null, null, null, null),
                    ]));
                Check("重复 sync 按 平台+会话 命中、不新增行", synced.Results.Count == 2);

                var douyin = synced.Results.First(r => r.PlatformKey == "douyin");
                await collections.ReportAsync(douyin.Id, new Dto.ReportCollectionRequest(
                    douyin.Id, true, "mix-1", null, "平台标题", null, "https://cover/1"));

                var list = await collections.GetListAsync(memberId);
                Check("列表：2 条记录 / 1 条已同步",
                    list.Count == 1 && list[0].PlatformCount == 2 && list[0].CreatedCount == 1);

                await collections.UpdateAsync(memberId, new Dto.UpdateCollectionRequest(
                    created.Id, "改名合集", "新简介", null));
                var detail = await collections.GetDetailAsync(memberId, created.Id);
                Check("改名生效且平台数量不变",
                    detail.Title == "改名合集" && detail.Platforms.Count == 2);

                await collections.DeleteAsync(memberId, new Dto.DeleteCollectionRequest(created.Id));
                var gone = false;
                try { await collections.GetDetailAsync(memberId, created.Id); }
                catch (CreatFlow.Application.Exceptions.NotFoundException) { gone = true; }
                Check("删除合集级联删平台记录", gone);
                return true;
            });

            // ═══ [4] 批量删除 ═══
            sb.AppendLine("\n[4] 批量删除：认领不改状态 / 合集全组清完才删 / 失败保留 / confirm-gone");
            Run(provider, async sp =>
            {
                var publish = sp.GetRequiredService<CreatFlow.Application.Identity.Services.PublishService>();
                var tasks = sp.GetRequiredService<CreatFlow.Application.Identity.Services.AsyncTaskService>();

                // 造两条发布记录
                var submit = await publish.SubmitAsync(memberId, new Dto.SubmitPublishRequest(
                    "/tmp/del.mp4", "del.mp4", 10, "hash-del", null, null, null,
                    null, "删除测试", null, null,
                    [
                        new Dto.PublishTarget("douyin", sessionId, null, null, null, null, null),
                        new Dto.PublishTarget("kuaishou", null, null, null, null, null, null),
                    ]));
                var pids = submit.Platforms.Select(p => p.Id).ToList();

                var del = await tasks.SubmitDeleteAsync(memberId, new Dto.SubmitAsyncDeleteRequest(
                    1, "删除 2 条视频", pids.Select(pid => new Dto.AsyncDeleteTargetRequest(
                        pid, 1, submit.TaskId, "douyin", sessionId, "测试号", null)).ToList()));
                Check("提交删除任务", del.Total == 2);

                var claim1 = await tasks.ClaimAsync(memberId);
                var claim2 = await tasks.ClaimAsync(memberId);
                Check("认领不改状态（连认两次都拿得到）",
                    claim1.Items.Count == 2 && claim2.Items.Count == 2);

                var inFlight = await tasks.GetInFlightAsync(memberId);
                Check("在飞目标含两条记录", inFlight.TargetIds.Count == 2);

                // 第一条成功 → 本地记录删除
                await tasks.ReportAsync(memberId, new Dto.ReportAsyncTaskItemRequest(
                    claim1.Items[0].Id, 1));
                var left = await publish.GetPlatformsAsync(memberId);
                Check("回报已删除 → 本地记录少一条", left.Count == 1);

                // 第二条平台失败 → 记录保留（失败保留行）
                await tasks.ReportAsync(memberId, new Dto.ReportAsyncTaskItemRequest(
                    claim1.Items[1].Id, 5, "平台拒绝"));
                left = await publish.GetPlatformsAsync(memberId);
                Check("平台失败 → 记录保留可重试", left.Count == 1);

                // 手动兜底：确认平台侧已删 → 清记录
                var confirm = await tasks.ConfirmGoneAsync(memberId, claim1.Items[1].Id);
                left = await publish.GetPlatformsAsync(memberId);
                Check("confirm-gone 清掉保留记录", confirm.LocalCleared && left.Count == 0);
                return true;
            });

            Run(provider, async sp =>
            {
                var collections = sp.GetRequiredService<CreatFlow.Application.Identity.Services.CollectionService>();
                var tasks = sp.GetRequiredService<CreatFlow.Application.Identity.Services.AsyncTaskService>();

                var created = await collections.CreateAsync(memberId, new Dto.CreateCollectionRequest(
                    "跨账号合集", null, null,
                    [
                        new Dto.CreateTarget("douyin", sessionId, null),
                        new Dto.CreateTarget("kuaishou", Guid.NewGuid(), null),
                    ]));
                var cps = created.Platforms;

                var del = await tasks.SubmitDeleteAsync(memberId, new Dto.SubmitAsyncDeleteRequest(
                    2, "删除合集 2 账号", cps.Select(cp => new Dto.AsyncDeleteTargetRequest(
                        cp.Id, 2, created.Id, cp.PlatformKey, cp.PlatformSessionId, "账号", "obj-1")).ToList()));
                var claim = await tasks.ClaimAsync(memberId);
                var items = claim.Items.Where(i => i.TaskId == del.TaskId).ToList();

                // 第一条成功 → 合集记录**保留**（还有账号没删完）
                var r1 = await tasks.ReportAsync(memberId, new Dto.ReportAsyncTaskItemRequest(items[0].Id, 1));
                Check("合集第一条删完：本地记录保留", !r1.LocalCleared && r1.GroupHasFailure);
                var still = await collections.GetListAsync(memberId);
                Check("合集卡仍在", still.Any(c => c.Id == created.Id));

                // 第二条「账号已解绑」算已清 → 全组清完 → 删本地
                var r2 = await tasks.ReportAsync(memberId, new Dto.ReportAsyncTaskItemRequest(items[1].Id, 3));
                Check("账号已解绑算已清，全组清完删本地", r2.LocalCleared && !r2.GroupHasFailure);
                var gone = await collections.GetListAsync(memberId);
                Check("合集卡消失", !gone.Any(c => c.Id == created.Id));
                return true;
            });

            // 清空已完成：跳过还有失败子项的任务
            Run(provider, async sp =>
            {
                var publish = sp.GetRequiredService<CreatFlow.Application.Identity.Services.PublishService>();
                var tasks = sp.GetRequiredService<CreatFlow.Application.Identity.Services.AsyncTaskService>();

                async Task<Guid> MakeTaskWithItemAsync()
                {
                    var submit = await publish.SubmitAsync(memberId, new Dto.SubmitPublishRequest(
                        "/tmp/c.mp4", "c.mp4", 10, "hash-c", null, null, null,
                        null, "清理测试", null, null,
                        [new Dto.PublishTarget("douyin", sessionId, null, null, null, null, null)]));
                    var t = await tasks.SubmitDeleteAsync(memberId, new Dto.SubmitAsyncDeleteRequest(
                        1, "清理测试", [new Dto.AsyncDeleteTargetRequest(
                            submit.Platforms[0].Id, 1, submit.TaskId, "douyin", sessionId, "号", null)]));
                    return t.TaskId;
                }

                var failedTask = await MakeTaskWithItemAsync();
                var okTask = await MakeTaskWithItemAsync();
                var claimed = (await tasks.ClaimAsync(memberId)).Items;

                var failedItem = claimed.First(i => i.TaskId == failedTask);
                var okItem = claimed.First(i => i.TaskId == okTask);
                await tasks.ReportAsync(memberId, new Dto.ReportAsyncTaskItemRequest(failedItem.Id, 5, "失败"));
                await tasks.ReportAsync(memberId, new Dto.ReportAsyncTaskItemRequest(okItem.Id, 1));

                // 前面几步留下的任务也都已停稳 —— 清空会一并收掉；关键断言是
                // 「干净任务没了、失败任务还在」（失败任务是唯一的重试/兜底入口）
                var removed = await tasks.ClearFinishedAsync(memberId);
                var list = await tasks.GetListAsync(memberId, new Dto.AsyncTaskListRequest(PageSize: 100));
                Check("清空已完成：删掉干净任务、留下失败任务",
                    removed >= 1
                    && list.Items.Any(t => t.Id == failedTask)
                    && !list.Items.Any(t => t.Id == okTask),
                    $"removed={removed} 剩余={list.Total}");
                return true;
            });

            // ═══ [5] 会话 ═══
            sb.AppendLine("\n[5] 会话：sync / list / credentials / rename / set-primary / delete 级联");
            var secondSession = Guid.NewGuid();
            var secondSession2 = Guid.NewGuid();
            Run(provider, async sp =>
            {
                var sessions = sp.GetRequiredService<CreatFlow.Application.Identity.Services.PlatformSessionService>();

                var s1 = await sessions.SyncAsync(memberId, new Dto.SyncPlatformSessionRequest(
                    secondSession, "xiaohongshu", "x-1", "小红书号", null, "cookie=abc", null, "{}"));
                Check("首个会话自动设为主账号", s1.IsPrimary);

                var s2 = await sessions.SyncAsync(memberId, new Dto.SyncPlatformSessionRequest(
                    secondSession2, "xiaohongshu", "x-2", "小红书小号", null, "cookie=def", null, "{}"));
                Check("同平台第二个会话不抢主账号", !s2.IsPrimary);

                var list = await sessions.GetListAsync(memberId, "xiaohongshu");
                Check("列表按平台过滤", list.Count == 2 && list.All(x => x.PlatformKey == "xiaohongshu"));

                var cred = await sessions.GetCredentialsAsync(memberId, secondSession);
                Check("凭据往返（Cookie 原样）", cred.Cookie == "cookie=abc" && cred.PlatformUid == "x-1");

                await sessions.RenameAsync(memberId, new Dto.RenamePlatformSessionRequest(secondSession2, "大号"));
                await sessions.SetPrimaryAsync(memberId, new Dto.SetPrimaryRequest(secondSession2));
                var after = await sessions.GetListAsync(memberId, "xiaohongshu");
                Check("改名 + 主账号切换",
                    after.First(x => x.Id == secondSession2).AccountLabel == "大号"
                    && after.First(x => x.Id == secondSession2).IsPrimary
                    && !after.First(x => x.Id == secondSession).IsPrimary);

                // 解绑硬删：会话所属发布 / 合集记录一起清
                var publish = sp.GetRequiredService<CreatFlow.Application.Identity.Services.PublishService>();
                var collections = sp.GetRequiredService<CreatFlow.Application.Identity.Services.CollectionService>();
                await publish.SubmitAsync(memberId, new Dto.SubmitPublishRequest(
                    "/tmp/unbind.mp4", "unbind.mp4", 10, "hash-u", null, null, null,
                    null, "解绑测试", null, null,
                    [new Dto.PublishTarget("xiaohongshu", secondSession2, null, null, null, null, null)]));
                await collections.CreateAsync(memberId, new Dto.CreateCollectionRequest(
                    "解绑合集", null, null,
                    [new Dto.CreateTarget("xiaohongshu", secondSession2, null)]));

                var del = await sessions.DeleteAsync(memberId,
                    new Dto.DeletePlatformSessionRequest(secondSession2));
                Check("解绑硬删该会话的发布 / 合集记录",
                    del.PublishRecordsDeleted == 1 && del.CollectionRecordsDeleted == 1);

                var gone = false;
                try { await sessions.GetCredentialsAsync(memberId, secondSession2); }
                catch (CreatFlow.Application.Exceptions.NotFoundException) { gone = true; }
                Check("会话行已删（凭据 404）", gone);

                var remain = await publish.GetListAsync(memberId);
                Check("解绑把该账号的发布任务一并清掉",
                    !remain.Any(t => t.CommonTitle == "解绑测试"), remain.Count.ToString());
                return true;
            });

            // ═══ [6] 备份 ═══
            sb.AppendLine("\n[6] 备份");
            using (var db = LocalStoreInitializer.CreateContext(dbPath))
            {
                var file = LocalStoreInitializer.Backup(db, "selftest", backupDir);
                Check("VACUUM INTO 生成快照", File.Exists(file), Path.GetFileName(file));
                using var snap = LocalStoreInitializer.CreateContext(file);
                Check("快照可打开且平台数据完整", snap.Platform.Count() == 4);
            }

            // ═══ [6b] 导出 / 导入（zip）═══
            sb.AppendLine("\n[6b] 导出 / 导入");
            var zipPath = Path.Combine(root, "backup.zip");
            var (_, fileCount) = LocalBackupService.ExportAsync(zipPath).GetAwaiter().GetResult();
            Check("导出 zip 成功", File.Exists(zipPath) && fileCount >= 1, $"files={fileCount}");
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                Check("zip 含 manifest + 数据库",
                    zip.GetEntry("manifest.json") != null && zip.GetEntry("creatflow.db") != null);
            }

            // 导出后加一条数据 → 导入应回到导出时状态
            var importProbe = Guid.NewGuid();
            Run(provider, async sp =>
            {
                var sessions = sp.GetRequiredService<CreatFlow.Application.Identity.Services.PlatformSessionService>();
                await sessions.SyncAsync(memberId, new Dto.SyncPlatformSessionRequest(
                    importProbe, "douyin", "probe", "导入探针", null, "c=probe", null, "{}"));
                return true;
            });
            var (importOk, importMsg) = LocalBackupService.ImportAsync(zipPath).GetAwaiter().GetResult();
            Check("导入成功", importOk, importMsg);
            Run(provider, async sp =>
            {
                var sessions = sp.GetRequiredService<CreatFlow.Application.Identity.Services.PlatformSessionService>();
                var list = await sessions.GetListAsync(memberId);
                Check("导入后数据回到导出时状态", !list.Any(s => s.Id == importProbe));
                return true;
            });

            // 坏备份必须被拒绝，且不覆盖当前数据
            var badZip = Path.Combine(root, "bad.zip");
            using (var z = ZipFile.Open(badZip, ZipArchiveMode.Create))
            {
                var entry = z.CreateEntry("readme.txt");
                using var w = entry.Open();
                var bytes = "not a backup"u8.ToArray();
                w.Write(bytes, 0, bytes.Length);
            }
            var (badOk, badMsg) = LocalBackupService.ImportAsync(badZip).GetAwaiter().GetResult();
            Check("坏备份被拒绝", !badOk, badMsg);
            Run(provider, async sp =>
            {
                var sessions = sp.GetRequiredService<CreatFlow.Application.Identity.Services.PlatformSessionService>();
                var list = await sessions.GetListAsync(memberId);
                Check("拒绝导入后数据未被覆盖", list.Count > 0);
                return true;
            });

            // ═══ [7] 路由覆盖 ═══
            sb.AppendLine("\n[7] 路由覆盖");
            string[] clientPaths =
            [
                "/api/config/platforms",
                "/api/platform-sessions/list", "/api/platform-sessions/sync",
                "/api/platform-sessions/rename", "/api/platform-sessions/set-primary",
                "/api/platform-sessions/delete", "/api/platform-sessions/credentials",
                "/api/platform-sessions/probe",
                "/api/publish/submit", "/api/publish/report", "/api/publish/retry",
                "/api/publish/list", "/api/publish/detail", "/api/publish/delete",
                "/api/publish/platforms", "/api/publish/platforms/page",
                "/api/publish/platforms/by-task", "/api/publish/platforms/delete",
                "/api/publish/platforms/hide",
                "/api/collections/list", "/api/collections/detail", "/api/collections/create",
                "/api/collections/update", "/api/collections/delete", "/api/collections/sync",
                "/api/collections/report",
                "/api/async-tasks/submit", "/api/async-tasks/list", "/api/async-tasks/detail",
                "/api/async-tasks/claim", "/api/async-tasks/in-flight", "/api/async-tasks/report",
                "/api/async-tasks/confirm-gone", "/api/async-tasks/retry",
                "/api/async-tasks/delete", "/api/async-tasks/clear-finished",
            ];
            var missing = clientPaths.Where(p => !LocalApiRouter.HandledPaths.Contains(p)).ToList();
            Check("客户端保留路径全部有本地路由", missing.Count == 0, string.Join(",", missing));

            string[] removedPaths =
            [
                "/api/auth/login", "/api/auth/register", "/api/auth/activate",
                "/api/auth/change-password", "/api/heartbeat", "/api/devices/list",
                "/api/devices/register", "/api/devices/rename", "/api/devices/kick",
                "/api/announcements", "/api/dingtalk/config", "/api/exceptions/report",
                "/api/account-groups/list", "/api/account-groups/save",
                "/api/compose-roles/list", "/api/compose-roles/save",
            ];
            var leaked = removedPaths.Where(p => LocalApiRouter.HandledPaths.Contains(p)).ToList();
            Check("C 类（登录/设备/公告/钉钉/云同步）没有本地路由", leaked.Count == 0, string.Join(",", leaked));

            // ═══ [8] 并发（两个 scope 同时写，WAL 不锁死） ═══
            sb.AppendLine("\n[8] 并发写");
            var concurrent = Run(provider, async _ =>
            {
                var a = Guid.NewGuid();
                var b = Guid.NewGuid();
                var t1 = Task.Run(async () =>
                {
                    using var scope = provider.CreateScope();
                    var svc = scope.ServiceProvider.GetRequiredService<CreatFlow.Application.Identity.Services.PlatformSessionService>();
                    return await svc.SyncAsync(memberId, new Dto.SyncPlatformSessionRequest(
                        a, "wechatvideo", "w-1", "视频号 A", null, "c=1", null, "{}"));
                });
                var t2 = Task.Run(async () =>
                {
                    using var scope = provider.CreateScope();
                    var svc = scope.ServiceProvider.GetRequiredService<CreatFlow.Application.Identity.Services.PlatformSessionService>();
                    return await svc.SyncAsync(memberId, new Dto.SyncPlatformSessionRequest(
                        b, "wechatvideo", "w-2", "视频号 B", null, "c=2", null, "{}"));
                });
                await Task.WhenAll(t1, t2);
                return t1.Result.Id != Guid.Empty && t2.Result.Id != Guid.Empty;
            });
            Check("两个 scope 并发写入成功", concurrent);

            // ═══ [9] 故障注入（坏文件绝不删、坏备份绝不覆盖） ═══
            // 守在「初始化失败 → 错误窗 → 用户重装」这条路径上：库坏了要明确报错，
            // 但原文件必须原样保留（用户还可能拿去修/换机导入），不能删掉重建。
            sb.AppendLine("\n[9] 故障注入");
            var faultDir = Path.Combine(root, "fault");
            Directory.CreateDirectory(faultDir);

            // 9a. 数据库文件头是垃圾 → 初始化必须抛错，原文件原样保留
            var faultDb = Path.Combine(faultDir, "creatflow.db");
            var garbage = "this is not a sqlite database"u8.ToArray();
            File.WriteAllBytes(faultDb, garbage);
            var initThrew = false;
            var initMessage = "";
            try { LocalStoreInitializer.EnsureInitialized(faultDb); }
            catch (Exception ex)
            {
                initThrew = true;
                initMessage = ex.Message.Replace('\r', ' ').Replace('\n', ' ');
            }
            Check("坏库初始化抛错（不静默进壳）", initThrew,
                initMessage.Length > 90 ? initMessage[..90] : initMessage);
            var preserved = false;
            try
            {
                // 失败后连接池可能还攥着文件句柄 → 用共享读打开（不依赖池何时释放）
                using var fs = new FileStream(faultDb, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var actual = new byte[fs.Length];
                fs.ReadExactly(actual);
                preserved = actual.SequenceEqual(garbage);
            }
            catch (Exception ex) { initMessage = ex.Message; }
            Check("坏库原文件保留且未被改写", preserved, preserved ? "" : initMessage);

            // 9b. 备份包里的数据库损坏 → 导入拒绝，当前库不被覆盖
            var badDbZip = Path.Combine(faultDir, "bad-db.zip");
            using (var z = ZipFile.Open(badDbZip, ZipArchiveMode.Create))
            {
                var manifestEntry = z.CreateEntry("manifest.json");
                using (var w = manifestEntry.Open())
                {
                    var json = """
                        {"format":"creatflow-backup","version":1,"appVersion":"selftest",
                         "edition":"Standalone","createdAt":"2026-10-28T00:00:00",
                         "memberId":"c0de0000-0000-4000-8000-000000000001",
                         "files":["creatflow.db"]}
                        """;
                    var bytes = Encoding.UTF8.GetBytes(json);
                    w.Write(bytes, 0, bytes.Length);
                }
                var dbEntry = z.CreateEntry("creatflow.db");
                using var dw = dbEntry.Open();
                dw.Write(garbage, 0, garbage.Length);
            }
            var (badDbOk, badDbMsg) = LocalBackupService.ImportAsync(badDbZip).GetAwaiter().GetResult();
            Check("坏数据库备份被拒绝导入", !badDbOk, badDbMsg);
            using (var db = LocalStoreInitializer.CreateContext(dbPath))
            {
                Check("拒绝导入后当前库仍可读", db.Platform.Count() == 4);
            }

            // ═══ [10] 平台探活接线（零网络路径） ═══
            // 探活器已从 Infrastructure 搬到 Application/Common/Probes，两版共用同一份实现。
            // 这里只验「DI 装上了 + 无探针的平台原样返回」，不触碰真实抖音/视频号页面。
            sb.AppendLine("\n[10] 平台探活接线");
            var probeOk = Run(provider, async sp =>
            {
                var sessions = sp.GetRequiredService<CreatFlow.Application.Identity.Services.PlatformSessionService>();
                var created = await sessions.SyncAsync(memberId, new Dto.SyncPlatformSessionRequest(
                    Guid.NewGuid(), "kuaishou", "probe-ks", "探针账号", null, "c=probe", null, "{}"));

                var prober = sp.GetRequiredService<IPlatformProber>();
                var repo = sp.GetRequiredService<IPlatformSessionRepository>();
                var entity = await repo.GetByIdAndMemberAsync(PlatformSessionId.Of(created.Id), memberId);
                if (entity is null) return false;

                // 快手没有 HTTP 探针（与联网版一致）→ Composite 返回 no_prober，不发网络请求
                var (status, result) = await prober.ProbeAsync(entity);
                return result == "no_prober" && status == entity.Status;
            });
            Check("IPlatformProber 已注册；无探针平台零网络返回 no_prober", probeOk);

            // ═══ [11] 清除本机数据（doc 11.3） ═══
            sb.AppendLine("\n[11] 清除本机数据");
            var memberDir = LocalStorePaths.MemberDir;
            var wv2Probe = Path.Combine(memberDir, "wv2", "probe-session", "Cookies");
            Directory.CreateDirectory(Path.GetDirectoryName(wv2Probe)!);
            File.WriteAllText(wv2Probe, "cookie");
            File.WriteAllText(Path.Combine(memberDir, "sessions.json"), "{}");

            var modelsDir = Path.Combine(LocalStorePaths.MachineRoot, "models");
            var logsDir = Path.Combine(LocalStorePaths.MachineRoot, "logs");
            var crashesDir = Path.Combine(LocalStorePaths.MachineRoot, "crashes");
            Directory.CreateDirectory(modelsDir);
            Directory.CreateDirectory(logsDir);
            Directory.CreateDirectory(crashesDir);
            var modelProbe = Path.Combine(modelsDir, "probe.gguf");
            var logProbe = Path.Combine(logsDir, "probe.log");
            var crashProbe = Path.Combine(crashesDir, "probe.log");
            File.WriteAllText(modelProbe, "m");
            File.WriteAllText(logProbe, "l");
            File.WriteAllText(crashProbe, "c");

            var otherMemberDir = Path.Combine(LocalStorePaths.AccountsRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(otherMemberDir);
            var otherProbe = Path.Combine(otherMemberDir, "keep.txt");
            File.WriteAllText(otherProbe, "other");

            // 1) 默认只删会员目录（DB + JSON + backups + wv2）
            var wipe1 = LocalDataWipe.ClearMemberData();
            Check("会员目录被清除（DB + JSON + backups + wv2）",
                wipe1.Ok && !Directory.Exists(memberDir), string.Join("；", wipe1.Failed));
            Check("机器级模型/日志默认保留",
                File.Exists(modelProbe) && File.Exists(logProbe) && File.Exists(crashProbe));
            Check("其它会员目录不受影响", File.Exists(otherProbe));

            // 2) 清除后可重新建库：种子/迁移照常（下次启动同一条路径）
            LocalStoreInitializer.EnsureInitialized(dbPath);
            using (var db = LocalStoreInitializer.CreateContext(dbPath))
            {
                Check("清除后可重建新库（平台种子 4 条）", db.Platform.Count() == 4);
            }

            // 3) 勾选机器级再清一次（幂等：目录已不在也不报错）
            var wipe2 = LocalDataWipe.ClearMemberData(includeMachineLevel: true);
            Check("再次清除幂等且机器级目录被删",
                wipe2.Ok && !Directory.Exists(memberDir)
                && !Directory.Exists(modelsDir) && !Directory.Exists(logsDir)
                && !Directory.Exists(crashesDir),
                string.Join("；", wipe2.Failed));

            (provider as IDisposable)?.Dispose();
        }
        catch (Exception ex)
        {
            failures++;
            sb.AppendLine($"  [FAIL] 自检异常：{ex}");
        }
        finally
        {
            LocalStorePaths.AccountsRootOverride = priorRoot;
            try { Directory.Delete(root, recursive: true); } catch { /* 锁住的临时目录留给系统清理 */ }
        }

        sb.AppendLine(failures == 0 ? "\n全部通过" : $"\n失败 {failures} 项");
        var report = Path.Combine(Path.GetTempPath(), "newapp_localstore_selftest.txt");
        try { File.WriteAllText(report, sb.ToString()); } catch { }
        Console.WriteLine(sb.ToString());
        return failures;
    }

    private static ServiceProvider BuildProvider(string dbPath)
    {
        var services = new ServiceCollection();
        services.AddLocalStore(dbPath);
        return services.BuildServiceProvider();
    }

    /// <summary>在一个独立 scope 里跑一段（与本地路由每请求一个 scope 同构）</summary>
    private static T Run<T>(IServiceProvider provider, Func<IServiceProvider, Task<T>> body)
    {
        using var scope = provider.CreateScope();
        return body(scope.ServiceProvider).GetAwaiter().GetResult();
    }
}
