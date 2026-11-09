using CreatFlow.Platforms.Abstractions.Collections;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 合集内容类型自检（`--selftest` 会一并跑）—— 纯函数，无网络无磁盘。
///
/// 守的是 2026-11-05 头条「文章合集 / 视频合集」两套体系的约束：
///   - 文章合集仅头条，不能和其他平台混选；
///   - 视频合集可跨平台；
///   - 线上 DTO 的 ContentType 默认 0（老数据 = 视频），文章 = 1。
///
/// 结果写 %TEMP%/newapp_collection_content_type_selftest.txt。
/// </summary>
public static class CollectionContentTypeSelfTest
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

        sb.AppendLine("═══ 合集内容类型自检 ═══");

        sb.AppendLine("\n[1] 枚举与标签");
        Check("Video = 0", (int)CollectionContentType.Video == 0);
        Check("Article = 1", (int)CollectionContentType.Article == 1);
        Check("视频标签", CollectionContentTypeRules.Label(0) == "视频合集");
        Check("文章标签", CollectionContentTypeRules.Label(1) == "文章合集（仅头条）");
        Check("IsArticle(1)", CollectionContentTypeRules.IsArticle(1));
        Check("IsArticle(0) = false", !CollectionContentTypeRules.IsArticle(0));

        sb.AppendLine("\n[2] 平台可选范围");
        Check("视频 → 头条可选",
            CollectionContentTypeRules.CanSelectPlatform(0, PlatformNames.Toutiao));
        Check("视频 → 抖音可选",
            CollectionContentTypeRules.CanSelectPlatform(0, PlatformNames.Douyin));
        Check("文章 → 头条可选",
            CollectionContentTypeRules.CanSelectPlatform(1, PlatformNames.Toutiao));
        Check("文章 → 抖音不可选",
            !CollectionContentTypeRules.CanSelectPlatform(1, PlatformNames.Douyin));
        Check("文章 → 快手不可选",
            !CollectionContentTypeRules.CanSelectPlatform(1, PlatformNames.Kuaishou));

        sb.AppendLine("\n[3] 目标组合校验");
        Check("视频 + 任意平台 = 合法",
            CollectionContentTypeRules.ValidateTargets(0,
                [PlatformNames.Douyin, PlatformNames.Kuaishou, PlatformNames.Toutiao]) is null);
        Check("文章 + 仅头条 = 合法",
            CollectionContentTypeRules.ValidateTargets(1, [PlatformNames.Toutiao]) is null);
        Check("文章 + 空目标 = 合法（空壳由服务端决定）",
            CollectionContentTypeRules.ValidateTargets(1, []) is null);
        var mix = CollectionContentTypeRules.ValidateTargets(1,
            [PlatformNames.Toutiao, PlatformNames.Douyin]);
        Check("文章 + 头条/抖音 = 拦截",
            mix is not null && mix.Contains("仅支持今日头条"), mix ?? "");

        sb.AppendLine("\n[4] 线上 DTO 的默认值与往返");
        var videoItem = new CollectionItem(
            Guid.NewGuid(), "视频合集", null, null, null, 0, 0, DateTime.UtcNow, null, null);
        Check("CollectionItem 缺省 ContentType = 0（老数据按视频）",
            videoItem.ContentType == 0);

        var articleItem = new CollectionItem(
            Guid.NewGuid(), "文章合集", null, null, null, 1, 0, DateTime.UtcNow, null, null, 1);
        var json = JsonSerializer.Serialize(articleItem, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        var back = JsonSerializer.Deserialize<CollectionItem>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });
        Check("CollectionItem 往返 ContentType = 1",
            back?.ContentType == 1, back?.ContentType.ToString() ?? "(null)");

        var articleDetail = new CollectionDetail(
            Guid.NewGuid(), "文章合集", null, null, null, [], DateTime.UtcNow, null, 1);
        var detailJson = JsonSerializer.Serialize(articleDetail, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        var detailBack = JsonSerializer.Deserialize<CollectionDetail>(detailJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Check("CollectionDetail 往返 ContentType = 1",
            detailBack?.ContentType == 1, detailBack?.ContentType.ToString() ?? "(null)");

        var target = new SyncTarget(Guid.NewGuid(), PlatformNames.Toutiao, "头条", "测试账号",
            Guid.NewGuid(), IsArticle: true);
        Check("SyncTarget 默认视频 / 显式文章", !new SyncTarget(
            Guid.NewGuid(), PlatformNames.Toutiao, "头条", "", Guid.NewGuid()).IsArticle
            && target.IsArticle);

        sb.AppendLine("\n[5] 文章模式的标题规则（仅头条 → 5~30）");
        Check("文章 4 字 → 至少 5",
            CollectionTitleRules.Validate("萌宠日常", [PlatformNames.Toutiao]) is { } e1
            && e1.Contains("至少 5"));
        Check("文章 30 字合法",
            CollectionTitleRules.Validate(new string('萌', 30), [PlatformNames.Toutiao]) is null);

        sb.AppendLine("\n[6] 视频发布页合集类型（阶段 3）");
        // 平台层 CollectionInfo：默认 0=视频；头条文章合集服务填 1
        var videoInfo = new CollectionInfo(
            "mix-1", "视频合集", null, null, 0, "", 3, null, null);
        Check("CollectionInfo 缺省 ContentType = 0（视频）",
            videoInfo.ContentType == 0);
        var articleInfo = new CollectionInfo(
            "c-1", "文章合集", null, null, 0, "", 3, null, null, ContentType: 1);
        Check("CollectionInfo 文章合集 ContentType = 1", articleInfo.ContentType == 1);

        // 发布页下拉项：文章合集被过滤（ContentType=1 → IsArticle）
        var videoOption = new CollectionOptionViewModel
        {
            CollectionId = Guid.NewGuid(), Name = "视频合集", SyncedCount = 1, TotalCount = 5,
        };
        var articleOption = new CollectionOptionViewModel
        {
            CollectionId = Guid.NewGuid(), Name = "文章合集", SyncedCount = 1, TotalCount = 1,
            ContentType = 1,
        };
        Check("下拉项：默认视频 / 文章标记",
            !videoOption.IsArticle && articleOption.IsArticle);

        sb.AppendLine();
        sb.AppendLine(failures == 0 ? "═══ 全部通过 ═══" : $"═══ {failures} 项失败 ═══");

        var report = sb.ToString();
        var path = Path.Combine(Path.GetTempPath(), "newapp_collection_content_type_selftest.txt");
        Console.WriteLine(report);
        try { File.WriteAllText(path, report); } catch { /* 写不了盘不影响退出码 */ }
        return failures;
    }
}
