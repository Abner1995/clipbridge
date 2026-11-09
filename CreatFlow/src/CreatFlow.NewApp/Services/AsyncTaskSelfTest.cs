using CreatFlow.NewApp.Models;
using CreatFlow.NewApp.ViewModels;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 异步任务（批量删除）自检（`--selftest` 会一并跑）—— 不启动 UI、不连服务端。
///
/// 这一份守的是**跨端契约**：任务状态是 wire 数字，客户端把平台删除结果翻译成这些数字
/// 发给服务端，服务端据此决定「清不清本地记录」。翻译错了不会编译报错、也不会抛异常，
/// 只会**静默地**把「已解绑」报成「失败」（于是合集永远删不掉）——
/// 这种错只能靠把映射表钉死来防。
///
/// 集合删除的语义判定（“全部账号删光才删本地记录”）在 P2 之后搬到了服务端
/// （`AsyncTaskService` + `AsyncTaskItemStatuses`），客户端这边保留一份镜像
/// （<see cref="AsyncTaskFormat.IsCleared"/>）并在这里断言，两边一起看才不会漂。
///
/// 结果写 %TEMP%/newapp_asynctask_selftest.txt。
/// </summary>
public static class AsyncTaskSelfTest
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

        sb.AppendLine("═══ 异步任务契约自检 ═══");

        // ── 1. wire 数字钉死 ──
        //
        // ⚠️ 这些数字是客户端与服务端之间的协议。改动必须两端同时改，
        // 而且错了是**静默**错（服务端只会按错的状态落库），所以逐值断言，别图省事用循环。
        sb.AppendLine("\n[1] 子项状态的 wire 数字（必须与服务端 AsyncTaskItemStatus 逐值一致）");
        var wire = AsyncTaskFormat.WireValues.ToDictionary(v => v.Name, v => v.Value);
        Check("Pending = 0", wire["Pending"] == 0, wire["Pending"].ToString());
        Check("Deleted = 1", wire["Deleted"] == 1, wire["Deleted"].ToString());
        Check("AlreadyGone = 2", wire["AlreadyGone"] == 2, wire["AlreadyGone"].ToString());
        Check("UnboundCleaned = 3", wire["UnboundCleaned"] == 3, wire["UnboundCleaned"].ToString());
        Check("NeedLogin = 4", wire["NeedLogin"] == 4, wire["NeedLogin"].ToString());
        Check("PlatformFailed = 5", wire["PlatformFailed"] == 5, wire["PlatformFailed"].ToString());
        Check("RecordCleanupFailed = 6", wire["RecordCleanupFailed"] == 6,
            wire["RecordCleanupFailed"].ToString());
        Check("七个值互不相同", wire.Values.Distinct().Count() == 7);

        Check("目标类型：发布记录 = 1", AsyncTaskFormat.TargetPublishPlatform == 1);
        Check("目标类型：合集记录 = 2", AsyncTaskFormat.TargetCollectionPlatform == 2);
        Check("任务类型：视频删除 = 1", AsyncTaskFormat.KindVideoDelete == 1);
        Check("任务类型：合集删除 = 2", AsyncTaskFormat.KindCollectionDelete == 2);

        // ── 2. 平台删除结果 → 回报状态（生产代码，消费者在跑）──
        sb.AppendLine("\n[2] 平台删除结果 → 回报给服务端的状态");
        Check("视频：Success → Deleted",
            AsyncTaskFormat.StatusFor(PlatformDeleteResult.Success) == TaskItemStatus.Deleted);
        Check("视频：NotFound → AlreadyGone（脏记录可以清）",
            AsyncTaskFormat.StatusFor(PlatformDeleteResult.NotFound) == TaskItemStatus.AlreadyGone);
        Check("**视频：SessionGone → UnboundCleaned（不是 Failed！报成失败就永远删不掉）**",
            AsyncTaskFormat.StatusFor(PlatformDeleteResult.SessionGone) == TaskItemStatus.UnboundCleaned,
            AsyncTaskFormat.StatusFor(PlatformDeleteResult.SessionGone).ToString());
        Check("视频：Unauthorized → NeedLogin（登录后可重试，别当通用失败）",
            AsyncTaskFormat.StatusFor(PlatformDeleteResult.Unauthorized) == TaskItemStatus.NeedLogin);
        Check("视频：Failed → PlatformFailed",
            AsyncTaskFormat.StatusFor(PlatformDeleteResult.Failed) == TaskItemStatus.PlatformFailed);
        Check("视频：五种结果映射互不相同", new[]
        {
            PlatformDeleteResult.Success, PlatformDeleteResult.NotFound,
            PlatformDeleteResult.SessionGone, PlatformDeleteResult.Unauthorized,
            PlatformDeleteResult.Failed,
        }.Select(AsyncTaskFormat.StatusFor).Distinct().Count() == 5);

        Check("合集：Deleted → Deleted",
            AsyncTaskFormat.StatusFor(CollectionDeleteOutcome.Deleted) == TaskItemStatus.Deleted);
        Check("**合集：UnboundCleaned → UnboundCleaned（8 个账号里 1 个解绑，不能卡死整张卡）**",
            AsyncTaskFormat.StatusFor(CollectionDeleteOutcome.UnboundCleaned)
            == TaskItemStatus.UnboundCleaned);
        Check("合集：Failed → PlatformFailed",
            AsyncTaskFormat.StatusFor(CollectionDeleteOutcome.Failed) == TaskItemStatus.PlatformFailed);

        // ── 3. 「哪些算已清」的镜像 ──
        sb.AppendLine("\n[3] 「哪些算已清」（服务端 AsyncTaskItemStatuses.IsCleared 的客户端镜像）");
        Check("Deleted 算已清", AsyncTaskFormat.IsCleared((int)TaskItemStatus.Deleted));
        Check("AlreadyGone 算已清", AsyncTaskFormat.IsCleared((int)TaskItemStatus.AlreadyGone));
        Check("**UnboundCleaned 算已清**（算失败的话合集永远删不掉）",
            AsyncTaskFormat.IsCleared((int)TaskItemStatus.UnboundCleaned));
        Check("NotNeedLogin 不算已清", !AsyncTaskFormat.IsCleared((int)TaskItemStatus.NeedLogin));
        Check("PlatformFailed 不算已清", !AsyncTaskFormat.IsCleared((int)TaskItemStatus.PlatformFailed));
        Check("RecordCleanupFailed 不算已清",
            !AsyncTaskFormat.IsCleared((int)TaskItemStatus.RecordCleanupFailed));
        Check("Pending 不算已清", !AsyncTaskFormat.IsCleared((int)TaskItemStatus.Pending));

        Check("Pending 不是失败（还没跑，别在界面上标红）",
            !AsyncTaskFormat.IsFailed((int)TaskItemStatus.Pending));
        Check("三个成功态都不是失败", new[]
        {
            TaskItemStatus.Deleted, TaskItemStatus.AlreadyGone, TaskItemStatus.UnboundCleaned,
        }.All(s => !AsyncTaskFormat.IsFailed((int)s)));
        Check("三个失败态都是失败", new[]
        {
            TaskItemStatus.NeedLogin, TaskItemStatus.PlatformFailed, TaskItemStatus.RecordCleanupFailed,
        }.All(s => AsyncTaskFormat.IsFailed((int)s)));

        // ── 4. 任务页文案 ──
        sb.AppendLine("\n[4] 任务页文案");
        Check("进度 0/0 不算除零", AsyncTaskFormat.ProgressValue(0, 0) == 0);
        Check("进度 5/12 ≈ 41.7", Math.Abs(AsyncTaskFormat.ProgressValue(5, 12) - 41.666) < 0.1,
            AsyncTaskFormat.ProgressValue(5, 12).ToString("0.###"));
        Check("进度不越过 100（时钟错位时别画到框外）",
            AsyncTaskFormat.ProgressValue(13, 12) == 100);
        Check("计数文案：8 / 12", AsyncTaskFormat.CountText(8, 12, 0) == "8 / 12",
            AsyncTaskFormat.CountText(8, 12, 0));
        Check("**有失败时必须写出来**（不写用户不知道有东西没删掉）",
            AsyncTaskFormat.CountText(8, 12, 3).Contains("3 个失败"),
            AsyncTaskFormat.CountText(8, 12, 3));
        Check("0 条不显示计数", AsyncTaskFormat.CountText(0, 0, 0) == "");

        Check("徽章：没活儿不显示", AsyncTaskFormat.BadgeText(0, 0) is null);
        Check("徽章：有在跑就显示", AsyncTaskFormat.BadgeText(2, 0) == "2");
        Check("徽章：失败也算（否则「没在跑」＝用户以为没事，其实有东西没删掉）",
            AsyncTaskFormat.BadgeText(0, 3) == "3", AsyncTaskFormat.BadgeText(0, 3));
        Check("徽章：在跑 + 失败一起算", AsyncTaskFormat.BadgeText(2, 3) == "5");

        Check("标题带条数", AsyncTaskFormat.SubmitTitle("删除", 32).Contains("32"),
            AsyncTaskFormat.SubmitTitle("删除", 32));
        Check("标题带主题（合集名）", AsyncTaskFormat.SubmitTitle("删除合集", 8, "亲子日常")
            .Contains("亲子日常"),
            AsyncTaskFormat.SubmitTitle("删除合集", 8, "亲子日常"));

        // ── 5. 任务页配色与文案（纯函数，页面只绑）──
        sb.AppendLine("\n[5] 任务页配色与行文案");
        Check("任务：等进行中是品牌紫", AsyncTaskFormat.TaskDotHex(0) == AsyncTaskFormat.TaskDotHex(1));
        Check("任务：完成是绿", AsyncTaskFormat.TaskDotHex(2) == "#00B42A");
        Check("**部分失败是橙不是红**（有东西没删掉，但不是全盘失败 —— 标红会让用户以为全挂了）",
            AsyncTaskFormat.TaskDotHex(3) == "#FF7D00", AsyncTaskFormat.TaskDotHex(3));
        Check("任务：全失败是红", AsyncTaskFormat.TaskDotHex(4) == "#F53F3F");
        Check("任务：Pending 与 Running 同色（都是「还没跑完」，界面上不该有两种说法）",
            AsyncTaskFormat.TaskDotHex(0) == AsyncTaskFormat.TaskDotHex(1));
        Check("任务：六个状态共五色（只有 0/1 共用紫，其余各一色）",
            new[] { 0, 1, 2, 3, 4, 5 }.Select(AsyncTaskFormat.TaskDotHex).Distinct().Count() == 5);
        Check("**用户要做的事不同的那五档颜色互不相同**（等 / 完成 / 部分失败 / 全失败 / 取消）",
            new[] { 1, 2, 3, 4, 5 }.Select(AsyncTaskFormat.TaskDotHex).Distinct().Count() == 5);
        Check("状态胶囊：部分失败底色与文字都区别于全失败",
            AsyncTaskFormat.TaskPillBgHex(3) != AsyncTaskFormat.TaskPillBgHex(4)
            && AsyncTaskFormat.TaskPillFgHex(3) != AsyncTaskFormat.TaskPillFgHex(4));

        Check("子项：已删除是绿", AsyncTaskFormat.ItemDotHex((int)TaskItemStatus.Deleted) == "#00B42A");
        Check("子项：平台上已不存在也是绿（本地记录清了就是干净）",
            AsyncTaskFormat.ItemDotHex((int)TaskItemStatus.AlreadyGone) == "#00B42A");
        Check("**子项：账号已解绑用灰不用绿**（平台上的那份可能还在，别画成「删干净了」）",
            AsyncTaskFormat.ItemDotHex((int)TaskItemStatus.UnboundCleaned) == "#C9CDD4",
            AsyncTaskFormat.ItemDotHex((int)TaskItemStatus.UnboundCleaned));
        Check("子项：三种失败态都是红",
            new[] { 4, 5, 6 }.All(s => AsyncTaskFormat.ItemDotHex(s) == "#F53F3F"));

        var now2 = new DateTime(2026, 9, 21, 15, 30, 0);
        Check("时间：今天只给时分", AsyncTaskFormat.DateText(now2, now2) == "今天 15:30",
            AsyncTaskFormat.DateText(now2, now2));
        Check("时间：昨天", AsyncTaskFormat.DateText(new DateTime(2026, 9, 20, 9, 5, 0), now2)
            == "昨天 09:05", AsyncTaskFormat.DateText(new DateTime(2026, 9, 20, 9, 5, 0), now2));
        Check("时间：再往前给完整日期",
            AsyncTaskFormat.DateText(new DateTime(2026, 9, 18, 9, 5, 0), now2) == "2026-09-18 09:05");

        Check("子项副行：平台 · 账号", AsyncTaskFormat.ItemSubText("抖音", "主号", 0) == "抖音 · 主号",
            AsyncTaskFormat.ItemSubText("抖音", "主号", 0));
        Check("子项副行：账号名为空 → 「账号已解绑」",
            AsyncTaskFormat.ItemSubText("抖音", null, 0) == "抖音 · 账号已解绑");
        Check("子项副行：重试过要写出来（用户才知道自动重试过）",
            AsyncTaskFormat.ItemSubText("抖音", "主号", 1).Contains("第 2 次尝试"),
            AsyncTaskFormat.ItemSubText("抖音", "主号", 1));

        // ── 6. 合集确认条文案（语义反转的回归断言）──
        sb.AppendLine("\n[6] 合集删除确认条");
        var strip = CollectionDeleteFormat.StripText(8);
        Check("写清「全部成功后才删除本地记录」", strip.Contains("全部成功后才删除本地记录"), strip);
        Check("写清「删不掉时记录会保留」", strip.Contains("记录会保留"));
        Check("写清「已加入合集的作品不受影响」", strip.Contains("已加入合集的作品不受影响"));
        Check("带上账号数", strip.Contains('8'));
        Check("**不再出现「失败仍删本地」那句**（语义反转的回归断言）",
            !strip.Contains("仍会删除本地记录") && !strip.Contains("仍将删除本地记录"), strip);
        Check("没有已同步账号时不说「将删除」",
            CollectionDeleteFormat.StripText(0).Contains("平台侧没有已同步的账号"),
            CollectionDeleteFormat.StripText(0));

        // ── 7. 「平台侧可删」判定（消费者两个分支共用；2026-10-14 合集全选删除事故的回归断言）──
        //
        // 会话为 null（老数据行）或空 Guid 还去凭证接口，服务端 PlatformSessionId.Of
        // 当场抛 DomainException —— 合集分支此前漏了这道守卫，整批任务卡在失败。
        sb.AppendLine("\n[7] 「平台侧可删」判定（CanDeleteOnPlatform）");
        var sid7 = Guid.NewGuid();
        Check("会话与平台侧 ID 齐全 → 可删",
            AsyncTaskFormat.CanDeleteOnPlatform(sid7, "7412abcd"));
        Check("**会话为 null → 不可删**（老数据行；不可删 = 报 UnboundCleaned 算已清，不是失败）",
            !AsyncTaskFormat.CanDeleteOnPlatform(null, "7412abcd"));
        Check("**会话为空 Guid → 不可删**（2026-10-14 合集全选删除实测的触发形态）",
            !AsyncTaskFormat.CanDeleteOnPlatform(Guid.Empty, "7412abcd"));
        Check("平台侧 ID 为 null → 不可删（从没在平台上建过）",
            !AsyncTaskFormat.CanDeleteOnPlatform(sid7, null));
        Check("平台侧 ID 为空串 → 不可删", !AsyncTaskFormat.CanDeleteOnPlatform(sid7, ""));
        Check("平台侧 ID 为空白串 → 不可删", !AsyncTaskFormat.CanDeleteOnPlatform(sid7, "   "));

        // ── 8. 手动兜底「确认已删除」与卡上警示（2026-10-14：误判失败把合集卡死事故的防线）──
        sb.AppendLine("\n[8] 手动兜底与「删除未完成」文案");
        Check("**只有「平台删除失败」可以确认已删除**（误判场景的口子，别扩到别的失败态）",
            AsyncTaskFormat.CanConfirmGone((int)TaskItemStatus.PlatformFailed));
        Check("登录过期不给口子（重登后重试是正路）",
            !AsyncTaskFormat.CanConfirmGone((int)TaskItemStatus.NeedLogin));
        Check("本地清理失败不给口子（重试会自愈）",
            !AsyncTaskFormat.CanConfirmGone((int)TaskItemStatus.RecordCleanupFailed));
        Check("已成功的子项不给（重复确认没有意义）",
            !AsyncTaskFormat.CanConfirmGone((int)TaskItemStatus.Deleted)
            && !AsyncTaskFormat.CanConfirmGone((int)TaskItemStatus.AlreadyGone)
            && !AsyncTaskFormat.CanConfirmGone((int)TaskItemStatus.UnboundCleaned));
        Check("等待中不给（还没跑，谈不上确认删除）",
            !AsyncTaskFormat.CanConfirmGone((int)TaskItemStatus.Pending));

        var cfText = AsyncTaskFormat.CollectionDeleteFailureText(2);
        Check("卡上警示：带失败账号数", cfText.Contains("2 个账号"), cfText);
        Check("卡上警示：写明记录已保留", cfText.Contains("记录已保留"), cfText);

        var dcfText = AsyncTaskFormat.DeleteRecordConfirmText(1);
        Check("删记录确认文案：带条数", dcfText.Contains("1 条"), dcfText);
        Check("删记录确认文案：写明会失去重试入口", dcfText.Contains("重试入口"), dcfText);

        // 行级派生（页面直接绑的那几个）：喂一条真 DTO 进 From()，断言到行对象
        static DeleteTaskItemRow MakeItem(int status) => DeleteTaskItemRow.From(
            new ServerAsyncTaskItem(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                AsyncTaskFormat.TargetCollectionPlatform, Guid.NewGuid(),
                "kuaishou", "快手", Guid.NewGuid(), "主号", "264379443",
                status, "状态", null, 0, null, new DateTime(2026, 10, 14, 16, 4, 0)),
            new Dictionary<string, string>());

        var failItem = MakeItem((int)TaskItemStatus.PlatformFailed);
        Check("失败子项行：CanConfirmGone=true（按钮可见）", failItem.CanConfirmGone);
        Check("失败子项行：CanRetry=true（两个入口并存）", failItem.CanRetry);
        Check("登录过期子项行：没有「确认已删除」入口",
            !MakeItem((int)TaskItemStatus.NeedLogin).CanConfirmGone);
        Check("成功子项行：两个入口都没有",
            !MakeItem((int)TaskItemStatus.Deleted).CanRetry
            && !MakeItem((int)TaskItemStatus.Deleted).CanConfirmGone);

        var path = Path.Combine(Path.GetTempPath(), "newapp_asynctask_selftest.txt");
        sb.AppendLine(failures == 0 ? "\n═══ 全部通过 ═══" : $"\n═══ {failures} 项失败 ═══");
        try { File.WriteAllText(path, sb.ToString()); } catch { /* 写不了盘不影响退出码 */ }

        Console.WriteLine($"[AsyncTaskSelfTest] failures={failures} -> {path}");
        return failures;
    }
}
