using CreatFlow.Platforms.Core;
using CreatFlow.Platforms.Kuaishou;
using CreatFlow.Platforms.Topics;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 话题 / 官方活动自检（`--selftest` 会一并跑）—— 纯解析 + 能力目录，**零网络**。
///
/// 夹具直接取自 2026-11-04/05 的六平台抓包（截短，字段名逐字保留）：
///   · 抖音 <c>media/activity/get</c> / <c>activity/pc/list</c> / <c>hashtag/activity</c> / challengesug
///   · 快手 <c>activity/pc/list</c> 条目、<c>tag/search</c> 形状
///   · 小红书 <c>suggest/topics</c> / <c>search/topics</c>
///   · B站 <c>topic/type/v2</c> / <c>tag/recommend</c>
///   · 头条 <c>hashtag/rec</c>（空样本 → 解析器必须安全返回空表）
///
/// 守的是「平台字段名读错 → 话题 ID 丢失/活动截止时间错」这类静默故障：
/// 解析错了 UI 不会报错，只会让结构化话题悄悄退化成纯文本、活动显示成未结束。
///
/// 结果写 %TEMP%/newapp_topic_selftest.txt。
/// </summary>
public static class TopicSelfTest
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

        // 缓存自检用小工具：写入后必须查不到（空键 / 失败结果）
        static bool SetAndMiss(TopicApiCache c, string k)
        {
            c.SetTopics(k, TopicFetchResult.Empty);
            return !c.TryGetTopics(k, out _);
        }

        static bool SetActivityAndMiss(TopicApiCache c, string k)
        {
            c.SetActivities(k, ActivityFetchResult.Fail("x"));
            return !c.TryGetActivities(k, out _);
        }

        sb.AppendLine("═══ 话题 / 官方活动自检 ═══");

        try
        {
            // ── 1. 抖音搜索（challengesug）──
            sb.AppendLine("\n[1] 抖音 challengesug");
            const string dySearch = """
                {"sug_list":[
                  {"cha_name":"接口测试","view_count":27066798,"cid":"1658571727441931","group_id":"6595532318910240013","tag":0},
                  {"cha_name":"这是一条用于","view_count":0,"cid":"","group_id":"7002370677296108800","tag":0}
                ],"status_code":0,"status_msg":""}
                """;
            var dy = DouyinTopicApi.ParseSearch(dySearch);
            Check("两条都返回（cid 空的不丢弃）", dy.Items.Count == 2, $"count={dy.Items.Count}");
            Check("有 cid → HasPlatformId", dy.Items[0].HasPlatformId && dy.Items[0].PlatformId == "1658571727441931");
            Check("cid 空串 → PlatformId=null（只能落纯文本）", !dy.Items[1].HasPlatformId);
            Check("view_count 原样", dy.Items[0].ViewCount == 27066798);
            Check("group_id 原样", dy.Items[0].GroupId == "6595532318910240013");
            Check("status_code≠0 → 显式失败",
                DouyinTopicApi.ParseSearch("""{"sug_list":[],"status_code":-1}""").Error is not null);

            // ── 2. 抖音活动列表（media/activity/get）──
            sb.AppendLine("\n[2] 抖音 media/activity/get");
            const string dyActs = """
                {"activity_list":[
                  {"activity_id":"7702034615116174390","activity_level":3,"activity_name":"全民进入购物听劝时代",
                   "activity_status":1,"challenge":["全民进入购物听劝时代"],
                   "challenge_ids":[7700827267148843000],"cover_image":"https://example.com/a.png",
                   "hot_score":520,"reward_type":3,"query_tag":100001,"jump_type":2,"jump_link":"aweme://x",
                   "show_start_time":"2026.10.29","show_end_time":"2099.12.31"},
                  {"activity_id":"7657822925256823854","activity_level":3,"activity_name":"无话题活动",
                   "challenge":[],"challenge_ids":[0],"hot_score":16969,"reward_type":0,
                   "show_start_time":"2020.07.02","show_end_time":"2020.12.12"}
                ],"status_code":0}
                """;
            var acts = DouyinTopicApi.ParseActivityGet(dyActs);
            Check("两条活动", acts.Count == 2, $"count={acts.Count}");
            Check("activity_id 字符串原样", acts[0].Id == "7702034615116174390");
            Check("activity_name 原样", acts[0].Name == "全民进入购物听劝时代");
            Check("challenge_ids + challenge 名配对", acts[0].Topics.Count == 1
                && acts[0].Topics[0].PlatformId == "7700827267148843000"
                && acts[0].Topics[0].Name == "全民进入购物听劝时代");
            Check("challenge_ids=[0] → 不带话题", acts[1].Topics.Count == 0);
            Check("reward_type>0 → 「有奖活动」", acts[0].Reward == "有奖活动");
            Check("reward_type=0 → 无奖励文案", acts[1].Reward is null);
            Check("未来截止时间 → 未结束", !acts[0].IsEnded);
            Check("过去截止时间 → 已结束", acts[1].IsEnded);
            Check("截止日按当天 23:59:59 收尾（2020.12.12 当天不算过期）",
                acts[1].EndTime is { } end && end.Date == new DateTime(2020, 12, 12));

            // ── 3. 抖音活动中心（activity/pc/list）──
            sb.AppendLine("\n[3] 抖音 activity/pc/list");
            const string dyCenter = """
                {"list":[{"activity_id":"7702034615116174390","activity_level":3,"activity_type":1,
                  "challenge_ids":[7700827267148843000],"head_image":"https://example.com/h.png",
                  "hot_score":100,"jump_link":"","jump_type":1,"query_tag":100001,
                  "show_end_time":1794758341,"show_name":"全民进入购物听劝时代","show_start_time":1793271601}]}
                """;
            var center = DouyinTopicApi.ParseActivityCenter(dyCenter);
            Check("show_name → Name", center.Count == 1 && center[0].Name == "全民进入购物听劝时代");
            Check("Unix 秒 → 本地时间", center[0].EndTime is not null && center[0].EndTime.Value.Year == 2026);
            Check("challenge_ids → 话题（名字缺省为「话题 {id}」）",
                center[0].Topics.Count == 1 && center[0].Topics[0].Name.StartsWith("话题 "));

            // ── 4. 抖音话题关联活动 ──
            sb.AppendLine("\n[4] 抖音 hashtag/activity");
            Check("空活动 \"{}\" → 空表",
                DouyinTopicApi.ParseTopicActivity("""{"activity":"{}","status_code":0}""").Count == 0);
            Check("activity 对象 → 一条",
                DouyinTopicApi.ParseTopicActivity(
                    """{"activity":{"activity_id":"1","activity_name":"活动A"},"status_code":0}""").Count == 1);
            Check("activity 缺失 → 空表",
                DouyinTopicApi.ParseTopicActivity("""{"status_code":0}""").Count == 0);

            // ── 5. 快手活动条目 ──
            sb.AppendLine("\n[5] 快手 activity/pc/list 条目");
            var ks = KuaishouTopicApi.ToActivity(new KuaishouActivityItem
            {
                Id = 71563,
                ActivityId = "ae2bf332ab91e90747aaba357e22ca2a",
                Title = "老铁心晴季—心情晴起来",
                HeadUrl = "https://example.com/ks.jpg",
                Number = 21788,
                BeginTime = 1793548800000,
                EndTime = 1794153599000,
                TopGain = "发稿得现金",
                CustomTag = "发稿得现金",
                TagConditionList = ["满分爸妈", "老铁心晴季"],
                Status = 2,
            });
            Check("activityId 优先于数字 id", ks.Id == "ae2bf332ab91e90747aaba357e22ca2a");
            Check("title/headUrl/topGain 落位", ks.Name == "老铁心晴季—心情晴起来"
                && ks.CoverUrl == "https://example.com/ks.jpg" && ks.Reward == "发稿得现金");
            Check("参加人数 number", ks.ParticipantCount == 21788);
            Check("tagConditionList → 关联话题（只有名字）",
                ks.Topics.Count == 2 && ks.Topics[0].Name == "满分爸妈" && !ks.Topics[0].HasPlatformId);
            Check("毫秒时间戳换算", ks.EndTime is not null && ks.EndTime.Value.Year == 2026);
            // 不按「当前时间未结束」断言（抓包截止日会随时间过期，2026-11-09 撞过一次时间炸弹）；
            // 改为校验解析出的绝对时刻，未来/过期语义由上面的 2099 / 下方 2020 夹具覆盖。
            Check("抓包里的活动截止时刻解析为 2026-11-08 15:59:59（UTC+8）",
                ks.EndTime == DateTimeOffset.FromUnixTimeMilliseconds(1794153599000));
            var ksExpired = KuaishouTopicApi.ToActivity(new KuaishouActivityItem
            {
                ActivityId = "expired", Title = "已结束活动",
                BeginTime = 1577836800000, EndTime = 1590000000000,   // 2020 年 → 必已结束
            });
            Check("过期活动 IsEnded", ksExpired.IsEnded);

            // ── 6. 小红书话题 ──
            sb.AppendLine("\n[6] 小红书 suggest/search topics");
            const string xhsSearch = """
                {"data":{"topics":[{"topic_info_dto":{"id":"5bffe3d402ebf80001da671f","name":"图文",
                  "topic_type_v2":0,"discuss_num":"6.6亿","discuss_num_desc":"6.6亿浏览",
                  "hot_topic":false,"view_num":664862818}}]},"success":true,"code":0}
                """;
            using (var doc = JsonDocument.Parse(xhsSearch))
            {
                var list = XhsTopicApi.ParseTopicArray(doc.RootElement, "data", "topics", "topic_info_dto");
                Check("search：topic_info_dto 解包", list.Count == 1 && list[0].Name == "图文");
                Check("id → PlatformId", list[0].PlatformId == "5bffe3d402ebf80001da671f");
                Check("discuss_num_desc 优先", list[0].DiscussText == "6.6亿浏览");
                Check("view_num → ViewCount", list[0].ViewCount == 664862818);
                Check("topic_type_v2 → TopicType", list[0].TopicType == "0");
            }
            const string xhsSuggest = """
                {"data":{"topic_dto_list":[{"id":"6135a2e20000000001009ef9","name":"单纯分享看看就好",
                  "discuss_num":"174.6M","discuss_num_desc":"1.7亿浏览","hot_topic":true,"topic_type_v2":0}]},"success":true}
                """;
            using (var doc = JsonDocument.Parse(xhsSuggest))
            {
                var list = XhsTopicApi.ParseTopicArray(doc.RootElement, "data", "topic_dto_list", null);
                Check("suggest：topic_dto_list 直接解", list.Count == 1 && list[0].Name == "单纯分享看看就好");
                Check("hot_topic → IsHot", list[0].IsHot);
            }

            // ── 7. B站话题 / 标签 / 活动聚合 ──
            sb.AppendLine("\n[7] B站 topic/type/v2 + tag/recommend");
            const string biliTopics = """
                {"code":0,"message":"OK","data":{"topics":[
                  {"topic_id":1098785,"topic_name":"我在B站搞装修","topic_description":"搞装修，上B站",
                   "mission_id":4070872,"activity_text":"","arc_play_vv":84119041,"show_activity_icon":true},
                  {"topic_id":1346007,"topic_name":"房间焕新大作战","topic_description":"焕新季",
                   "mission_id":4070872,"activity_text":"","arc_play_vv":27607751,"show_activity_icon":true},
                  {"topic_id":1349452,"topic_name":"我的家电购物车","topic_description":"双十一",
                   "mission_id":0,"activity_text":"","arc_play_vv":6592687,"show_activity_icon":false}
                ]}}
                """;
            var (bTopics, bCode, _) = BilibiliTopicApi.ParseTopics(biliTopics);
            Check("三条话题", bTopics.Count == 3 && bCode == 0);
            Check("mission_id → MissionId", bTopics[0].MissionId == 4070872);
            Check("arc_play_vv → ViewCount", bTopics[0].ViewCount == 84119041);
            var bActs = BilibiliTopicApi.GroupActivities(bTopics);
            Check("同 mission 的两条话题聚成一个活动", bActs.Count == 1 && bActs[0].Id == "4070872");
            Check("活动含两个赛道", bActs[0].Topics.Count == 2);
            Check("activity_text 空 → 活动名退回首个话题名", bActs[0].Name == "我在B站搞装修");
            Check("非活动话题不进活动表", bActs[0].Topics.All(t => t.MissionId == 4070872));

            const string biliTags = """
                {"code":0,"data":[{"tag":"家居","checked":0},{"tag":"室内设计","checked":0}],"message":"OK"}
                """;
            var (bTags, _, _) = BilibiliTopicApi.ParseTags(biliTags);
            Check("标签两条 + 名字原样", bTags.Count == 2 && bTags[0].Name == "家居");
            Check("B站 login 过期 code=-101 → 带引导",
                BilibiliTopicApi.ParseTopics("""{"code":-101,"message":"not login"}""").Code == -101);

            // ── 8. 头条 hashtag/rec ──
            sb.AppendLine("\n[8] 头条 hashtag/rec");
            Check("空 hashtag_list → 空表", ToutiaoTopicApi.ParseHashtagList([]).Count == 0);
            Check("null → 空表", ToutiaoTopicApi.ParseHashtagList(null).Count == 0);
            using (var doc = JsonDocument.Parse("""
                [{"name":"家居","id":123},{"tag_name":"装修","hashtag_id":"456"},
                 {"title":"只有标题"},{"foo":"认不出"}]
                """))
            {
                var list = ToutiaoTopicApi.ParseHashtagList(
                    [.. doc.RootElement.EnumerateArray()]);
                Check("宽容取 name/tag_name/title，认不出的跳过", list.Count == 3, $"count={list.Count}");
                Check("number id 转字符串", list[0].PlatformId == "123");
                Check("string id 原样", list[1].PlatformId == "456");
                Check("没有 id → PlatformId=null", list[2].PlatformId is null);
            }

            // ── 9. 视频号「无接口」 ──
            sb.AppendLine("\n[9] 视频号无接口标记");
            var wechat = new WechatVideoTopicApi(new PlatformOptions());
            Check("Capability 明确无接口", !wechat.Capability.SupportsSearch
                && !wechat.Capability.SupportsRecommend && !wechat.Capability.SupportsActivities);
            Check("NoInterfaceReason 文案非空", !string.IsNullOrWhiteSpace(wechat.Capability.NoInterfaceReason));
            var wxResult = wechat.SearchAsync(new PlatformCredentials("c=1"), new TopicQuery { Keyword = "x" })
                .GetAwaiter().GetResult();
            Check("SearchAsync 返回原因而不是抛", wxResult.Items.Count == 0 && wxResult.Error is not null);

            // ── 10. 能力目录（UI 装配依据）──
            sb.AppendLine("\n[10] TopicServiceCatalog 能力矩阵");
            var options = new PlatformOptions();
            Check("六平台话题服务都能构造", PlatformNames.All.All(k =>
                TopicServiceCatalog.CreateTopicService(k, options).PlatformKey == k));
            Check("六平台活动服务都能构造", PlatformNames.All.All(k =>
                TopicServiceCatalog.CreateActivityService(k, options).PlatformKey == k));
            Check("活动支持面 = 抖音/快手/小红书/B站", PlatformNames.All
                .Where(TopicServiceCatalog.SupportsActivities)
                .OrderBy(x => x)
                .SequenceEqual(new[] { PlatformNames.Bilibili, PlatformNames.Douyin,
                    PlatformNames.Kuaishou, PlatformNames.Xiaohongshu }.OrderBy(x => x)));
            Check("只有 B站 有标签推荐", TopicServiceCatalog.CreateTagService(PlatformNames.Bilibili, options) is not null
                && TopicServiceCatalog.CreateTagService(PlatformNames.Douyin, options) is null);
            Check("头条推荐依赖上传上下文；B站编辑阶段即可拉（upload_id=0 实测可用）",
                TopicServiceCatalog.CapabilityOf(PlatformNames.Toutiao).RecommendNeedsUpload
                && !TopicServiceCatalog.CapabilityOf(PlatformNames.Bilibili).RecommendNeedsUpload);
            Check("未知平台 → 默认空能力（不抛）",
                TopicServiceCatalog.CapabilityOf("nope") is { SupportsSearch: false, SupportsActivities: false });

            // ── 11. 凭证解析 ──
            sb.AppendLine("\n[11] PlatformCredentials extra_data");
            var creds = new PlatformCredentials("a=1", "{\"csrfToken\":\"tok\",\"apiPh\":123}");
            Check("字符串值取出", creds.CsrfToken == "tok");
            Check("非字符串值取 JSON 文本", creds.GetExtraString("apiPh") == "123");
            Check("缺失键 → null", creds.GetExtraString("missing") is null);
            Check("坏 JSON → null 不抛", new PlatformCredentials("a=1", "{半截").CsrfToken is null);
            Check("空 extra → null", new PlatformCredentials("a=1").CsrfToken is null);

            // ── 12. 话题 / 活动缓存（2.6.7）──
            sb.AppendLine("\n[12] TopicApiCache 缓存与降级");
            var now = new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.FromHours(8));
            var cache = new TopicApiCache(() => now);
            var sid = Guid.Parse("11111111-2222-3333-4444-555555555555");
            var sid2 = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
            var key = TopicApiCache.TopicKey(sid, PlatformNames.Douyin, false, "家居");
            var items = new TopicFetchResult(
                [new TopicSuggestion { Name = "家居", PlatformId = "1658571727441931" }]);

            Check("空缓存 → 未命中", !cache.TryGetTopics(key, out _));
            cache.SetTopics(key, items);
            Check("写入后可命中（含平台 ID）",
                cache.TryGetTopics(key, out var got)
                && got.Items.Count == 1
                && got.Items[0].Name == "家居"
                && got.Items[0].PlatformId == "1658571727441931");

            Check("不同账号 → 不同键",
                TopicApiCache.TopicKey(sid2, PlatformNames.Douyin, false, "家居") != key);
            Check("不同平台 → 不同键",
                TopicApiCache.TopicKey(sid, PlatformNames.Kuaishou, false, "家居") != key);
            Check("搜索 / 推荐 → 不同键",
                TopicApiCache.TopicKey(sid, PlatformNames.Douyin, true, "家居") != key);
            Check("不同关键词 → 不同键",
                TopicApiCache.TopicKey(sid, PlatformNames.Douyin, false, "装修") != key);
            Check("首尾/连续空白归一（同一查询不重复打接口）",
                TopicApiCache.TopicKey(sid, PlatformNames.Douyin, false, "  家居  ") == key);
            Check("推荐上下文（标题）进键",
                TopicApiCache.TopicKey(sid, PlatformNames.Bilibili, true, "", "标题A")
                != TopicApiCache.TopicKey(sid, PlatformNames.Bilibili, true, "", "标题B"));
            Check("空键不写入", SetAndMiss(cache, ""));

            // TTL 10 分钟边界：9:59 命中，10:00 整过期
            now = now.AddSeconds(599);
            Check("9分59秒仍命中", cache.TryGetTopics(key, out _));
            now = now.AddSeconds(1);
            Check("10分钟整过期（回到网络）", !cache.TryGetTopics(key, out _));

            // 失败不缓存：失败被缓存 = 用户点重试也救不回来
            var failKey = TopicApiCache.TopicKey(sid, PlatformNames.Douyin, false, "断网");
            cache.SetTopics(failKey, TopicFetchResult.Fail("网络不通"));
            Check("失败结果不缓存", !cache.TryGetTopics(failKey, out _));

            var emptyKey = TopicApiCache.TopicKey(sid, PlatformNames.Douyin, false, "无结果");
            cache.SetTopics(emptyKey, TopicFetchResult.Empty);
            Check("「这次没搜到」属于成功，照样可缓存",
                cache.TryGetTopics(emptyKey, out var empty) && empty.Items.Count == 0);

            // 容量上限：满后淘汰最旧（仍有效的条目不该被新查询无限挤占）
            var small = new TopicApiCache(() => now, capacity: 2);
            var k1 = TopicApiCache.TopicKey(sid, "p", false, "a");
            var k2 = TopicApiCache.TopicKey(sid, "p", false, "b");
            var k3 = TopicApiCache.TopicKey(sid, "p", false, "c");
            small.SetTopics(k1, items);
            now = now.AddSeconds(1);
            small.SetTopics(k2, items);
            now = now.AddSeconds(1);
            small.SetTopics(k3, items);
            Check("容量满 → 淘汰最旧",
                !small.TryGetTopics(k1, out _)
                && small.TryGetTopics(k2, out _)
                && small.TryGetTopics(k3, out _));

            // 活动与话题两个命名空间互不串
            var actKey = TopicApiCache.ActivityKey(sid, PlatformNames.Douyin, "标题");
            var cachedActs = new ActivityFetchResult([new PlatformActivity { Id = "1", Name = "活动A" }]);
            cache.SetActivities(actKey, cachedActs);
            Check("活动写入后可命中",
                cache.TryGetActivities(actKey, out var actGot)
                && actGot.Items.Count == 1 && actGot.Items[0].Name == "活动A");
            Check("活动失败不缓存",
                SetActivityAndMiss(cache, TopicApiCache.ActivityKey(sid, PlatformNames.Douyin, "断网")));
            Check("话题键查不到活动、活动键查不到话题",
                !cache.TryGetTopics(actKey, out _) && !cache.TryGetActivities(key, out _));

            // 并发读写不抛（UI 线程与后台刷新可能同时动缓存）
            var concurrent = new TopicApiCache(() => now);
            var errors = 0;
            Parallel.For(0, 200, i =>
            {
                try
                {
                    var k = TopicApiCache.TopicKey(sid, "p", false, i.ToString());
                    concurrent.SetTopics(k, items);
                    concurrent.TryGetTopics(k, out _);
                }
                catch
                {
                    Interlocked.Increment(ref errors);
                }
            });
            Check("并发读写不抛", errors == 0, $"errors={errors}");
        }
        catch (Exception ex)
        {
            failures++;
            sb.AppendLine($"  [FAIL] 自检异常：{ex}");
        }

        sb.AppendLine();
        sb.AppendLine(failures == 0 ? "═══ 全部通过 ═══" : $"═══ {failures} 项失败 ═══");
        try
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "newapp_topic_selftest.txt"),
                sb.ToString());
        }
        catch { /* 报告落盘失败不影响结果 */ }

        return failures;
    }
}
