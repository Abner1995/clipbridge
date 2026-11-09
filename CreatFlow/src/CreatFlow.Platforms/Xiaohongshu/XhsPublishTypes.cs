namespace CreatFlow.Platforms.Xiaohongshu;

/// <summary>
/// 小红书视频标注（内容声明）选项。
///
/// 对标 examples/xiaohongshu/media/视频标注.md 和 Web 页面下拉菜单。
///
/// 五个选项：
///   1=虚构演绎仅供娱乐  → optionRelationList (ORIGINAL_STATEMENT)
///   2=笔记含AI合成内容   → optionRelationList (ORIGINAL_STATEMENT)
///   3=内容包含营销广告    → optionRelationList (ORIGINAL_STATEMENT)
///   4=内容来源声明/自主拍摄 → userDeclarationBind {origin:4, photoInfo:{...}}
///   5=内容来源声明/来源转载 → userDeclarationBind {origin:5, repostInfo:{source:...}}
///
/// value 1-3 和 4-5 的提交路径不同：
///   - 1-3 → business_binds.optionRelationList
///   - 4-5 → business_binds.userDeclarationBind
/// </summary>
public sealed class XhsContentLabelOption
{
    /// <summary>值（1-5），0=不选</summary>
    public int Value { get; init; }

    /// <summary>一级显示文本</summary>
    public string Label { get; init; } = "";

    /// <summary>是否有二级展开（value=4/5 时展开）</summary>
    public bool HasChildren { get; init; }

    /// <summary>二级选项（仅 value=4 时有效）</summary>
    public IReadOnlyList<XhsContentLabelOption>? Children { get; init; }

    /// <summary>提示文本（鼠标悬停时展示）</summary>
    public string? Tooltip { get; init; }

    /// <summary>是否 disabled（如已原创声明则不可选"来源转载"）</summary>
    public bool Disabled { get; init; }
}

/// <summary>
/// 小红书视频标注（内容声明）静态选项映射。
/// 对标 DouyinDeclarationOptions，供 UI 和 SDK 共同使用。
/// </summary>
public static class XhsContentLabelOptions
{
    /// <summary>所有一级选项</summary>
    public static IReadOnlyList<XhsContentLabelOption> All { get; } = new[]
    {
        new XhsContentLabelOption { Value = 1, Label = "虚构演绎，仅供娱乐" },
        new XhsContentLabelOption { Value = 2, Label = "笔记含AI合成内容" },
        new XhsContentLabelOption { Value = 3, Label = "内容包含营销广告" },
        new XhsContentLabelOption
        {
            Value = 4,
            Label = "内容来源声明",
            HasChildren = true,
            Children = new[]
            {
                new XhsContentLabelOption { Value = 4, Label = "自主拍摄" },
                new XhsContentLabelOption { Value = 5, Label = "来源转载", Tooltip = "已声明原创不可选择" },
            },
        },
    };

    /// <summary>
    /// 根据用户选择构建 business_binds 中的声明相关字段。
    /// 返回 (optionRelationList, userDeclarationBind)，调用方需合并到 business_binds。
    /// </summary>
    /// <param name="selectedValue">1-5 的选中值，0 或 null = 无声明</param>
    /// <param name="repostSource">转载来源（value=5 时填写，如 "youtube"）</param>
    /// <param name="photoPoiId">拍摄地点 POI ID（value=4 时填写）</param>
    /// <param name="photoPoiName">拍摄地点名称</param>
    /// <param name="photoPoiSubname">拍摄地点子名称（地址/区域）</param>
    /// <param name="photoTime">拍摄时间 yyyy-MM-dd（value=4 时填写）</param>
    /// <param name="isOriginal">原创声明 — 开启后 optionRelationList 追加 ORIGINAL_STATEMENT 条目</param>
    public static (object? optionRelationList, object? userDeclarationBind) BuildDeclBind(
        int? selectedValue,
        string? repostSource = null,
        string? photoPoiId = null,
        string? photoPoiName = null,
        string? photoPoiSubname = null,
        string? photoTime = null,
        bool isOriginal = false,
        string? creatorUserId = null)
    {
        var optionRelationEntries = new List<object>();
        var userDeclarationBind = (object?)null;

        // 原创声明 — 追加 ORIGINAL_STATEMENT 条目
        if (isOriginal)
        {
            optionRelationEntries.Add(new Dictionary<string, object?>
            {
                ["type"] = "ORIGINAL_STATEMENT",
                ["relationList"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["bizType"] = "ORIGINAL_STATEMENT",
                        ["bizId"] = creatorUserId ?? "",
                        ["extraInfo"] = "{}",
                    },
                },
            });
        }

        switch (selectedValue)
        {
            case 1: // 虚构演绎仅供娱乐 → userDeclarationBind.origin=1
            case 2: // 笔记含AI合成内容 → userDeclarationBind.origin=2
            case 3: // 内容包含营销广告 → userDeclarationBind.origin=3
                userDeclarationBind = new Dictionary<string, object?>
                {
                    ["origin"] = selectedValue.Value,
                };
                break;

            case 4: // 自主拍摄
                userDeclarationBind = new Dictionary<string, object?>
                {
                    ["origin"] = 4,
                    ["photoInfo"] = new Dictionary<string, object?>
                    {
                        ["photoPlace"] = new Dictionary<string, object?>
                        {
                            ["poiId"] = photoPoiId ?? "",
                            ["poiType"] = 12,
                            ["name"] = photoPoiName ?? "",
                            ["subname"] = photoPoiSubname ?? "",
                        },
                        ["photoTime"] = photoTime ?? "",
                    },
                };
                break;

            case 5: // 来源转载
                userDeclarationBind = new Dictionary<string, object?>
                {
                    ["origin"] = 5,
                    ["repostInfo"] = new Dictionary<string, string?>
                    {
                        ["source"] = repostSource ?? "",
                    },
                };
                break;
        }

        var optionRelationList = optionRelationEntries.Count > 0
            ? (object)optionRelationEntries.ToArray()
            : null;

        return (optionRelationList, userDeclarationBind);
    }
}

/// <summary>
/// 小红书可见性类型。
/// 对应 Web UI 下拉菜单的实际选项值。
/// </summary>
public enum XhsPrivacyType
{
    /// <summary>公开可见 — privacy_info.type=0, op_type=1</summary>
    Public = 0,

    /// <summary>仅自己可见 — privacy_info.type=1, op_type=1</summary>
    Private = 1,

    /// <summary>不给谁看 — privacy_info.op_type=2, user_ids 必填</summary>
    ExcludeUsers = 2,

    /// <summary>只给谁看 — privacy_info.op_type=3, user_ids 必填（≤200人）</summary>
    OnlyUsers = 3,

    /// <summary>仅互关好友可见 — privacy_info.type=4?, op_type=1</summary>
    MutualFriends = 4,
}

/// <summary>
/// 小红书发布请求参数（完整版）。
/// 替换原来的 XhsPublishRequest 简化版。
/// </summary>
public sealed class XhsPublishRequest
{
    /// <summary>视频标题（必填）</summary>
    public required string Title { get; init; }

    /// <summary>视频描述/正文（支持 #[话题]# 格式）</summary>
    public string? Description { get; init; }

    /// <summary>视频文件 ID（COS spectrum fileId）</summary>
    public required string FileId { get; init; }

    /// <summary>视频 ID（从上传/转码获取）</summary>
    public string? VideoId { get; init; }

    /// <summary>封面文件 ID（独立于视频的封面图 fileId，null = 用视频首帧）</summary>
    public string? CoverFileId { get; init; }

    /// <summary>
    /// 封面帧（毫秒）—— <c>video_info.cover.frame.ts</c>（wire 是**秒**，组装时换算）。
    /// 仅在未上传自定义封面图时生效；上传了封面图 → <c>ts=0 / is_upload=true</c>
    /// （页面封面上传确认体 <c>frametime:0, isUpload:true</c>，2026-11-05 Vue bundle）。
    /// </summary>
    public long? CoverFrameMs { get; init; }

    /// <summary>话题标签列表（含 id+name，来自 suggest API）</summary>
    public List<XhsTopicItem>? Topics { get; init; }

    /// <summary>合集 ID</summary>
    public string? CollectionId { get; init; }

    /// <summary>定时发布时间（null = 立即发布）</summary>
    public DateTimeOffset? ScheduledAt { get; init; }

    /// <summary>位置/POI</summary>
    public XhsPoiItem? Location { get; init; }

    /// <summary>可见性设置</summary>
    public XhsPrivacyType PrivacyType { get; init; } = XhsPrivacyType.Public;

    /// <summary>隐私屏蔽/白名单用户 ID 列表（PrivacyType=ExcludeUsers/OnlyUsers 时必填）</summary>
    public List<string>? PrivacyUserIds { get; init; }

    /// <summary>视频标注/内容声明值（1-5），null = 不添加</summary>
    public int? ContentLabelValue { get; init; }

    /// <summary>转载来源（ContentLabelValue=5 时填写）</summary>
    public string? RepostSource { get; init; }

    /// <summary>自主拍摄地点 POI ID（ContentLabelValue=4 时填写）</summary>
    public string? PhotoPoiId { get; init; }

    /// <summary>自主拍摄地点名称</summary>
    public string? PhotoPoiName { get; init; }

    /// <summary>自主拍摄地点子名称（地址/区域，ContentLabelValue=4 时填写）</summary>
    public string? PhotoPoiSubname { get; init; }

    /// <summary>自主拍摄时间 yyyy-MM-dd（ContentLabelValue=4 时填写）</summary>
    public string? PhotoTime { get; init; }

    /// <summary>原创声明 — 开启后 optionRelationList 追加 ORIGINAL_STATEMENT，且来源转载不可选</summary>
    public bool IsOriginal { get; init; }

    /// <summary>小红书用户 ID（来自 x-user-id-creator cookie），原创声明需填</summary>
    public string? CreatorUserId { get; init; }

    // ═══ 长尾（阶段 2.3，1.3 小红书表）═══

    /// <summary>允许下载/复制 —— <c>business_binds.noteCopyBind.copyable</c>
    /// （2026-11-05 抓包 default true；页面无开关，属推断映射）</summary>
    public bool AllowDownload { get; init; } = true;

    /// <summary>允许评论 —— <c>business_binds.interactionPermissionBind.commentPermission</c>
    /// （0 = 允许 / 1 = 不允许；抓包只见到 0）</summary>
    public bool AllowComment { get; init; } = true;

    /// <summary>视频摘要 —— <c>video_info.summary</c></summary>
    public string? Summary { get; init; }

    /// <summary>章节 —— <c>video_info.chapters</c>；wire 形状 <c>[{text, time:"&lt;毫秒&gt;"}]</c>
    /// （取自页面 Vue bundle 的构造函数，2026-11-05）</summary>
    public List<XhsChapter>? Chapters { get; init; }

    /// <summary>速览合集 ID —— <c>business_binds.noteSketchCollectionBind.id</c>（图文速览合集，与普通合集分开）</summary>
    public string? SketchCollectionId { get; init; }

    /// <summary>订单/品牌绑定（**原始 JSON 对象串**）—— <c>business_binds.noteOrderBind</c></summary>
    public string? OrderBindJson { get; init; }

    /// <summary>章节文字同步 —— <c>video_info.chapter_sync_text</c></summary>
    public bool? ChapterSyncText { get; init; }

    /// <summary>PK 封面业务关联（**原始 JSON 数组串**）—— <c>video_info.pk_cover_biz_relations</c></summary>
    public string? PkCoverBizRelationsJson { get; init; }

    /// <summary>@ 用户（**原始 JSON 数组串**）—— <c>common.ats</c></summary>
    public string? AtsJson { get; init; }

    /// <summary>带货商品信息（**原始 JSON 对象串**）—— <c>common.goods_info</c>
    /// （元素结构未抓到，只透传；简单商品 ID 见载荷上的 <c>ProductIds</c>，暂无安全落点）</summary>
    public string? GoodsInfoJson { get; init; }

    /// <summary>业务关系（**原始 JSON 数组串**）—— <c>common.biz_relations</c></summary>
    public string? BizRelationsJson { get; init; }

    // ═══ 视频元数据 ═══

    public int Width { get; init; } = 1920;
    public int Height { get; init; } = 1080;
    public int DurationMs { get; init; }
    public int VideoBitrate { get; init; }
    public int AudioBitrate { get; init; }
    public string? VideoCodec { get; init; }
    public string? AudioCodec { get; init; }
    public int FrameRate { get; init; } = 30;
    public int Rotation { get; init; }
}

/// <summary>话题标签条目（来自 suggest API，含 id）</summary>
public sealed record XhsTopicItem(string Id, string Name, string Type = "topic");

/// <summary>小红书章节 —— 模型统一用毫秒；wire 上是 <c>{text, time:"&lt;毫秒字符串&gt;"}</c>。</summary>
public sealed record XhsChapter(string Text, long StartMs);

/// <summary>小红书发布参数错误（本地校验失败；消息直接给用户看）。</summary>
public sealed class XhsPublishException : Exception
{
    public XhsPublishException(string message) : base(message) { }
}

/// <summary>小红书位置/POI 条目。</summary>
public sealed record XhsPoiItem(
    string PoiId,
    int PoiType,
    string Name,
    string? Subname);

/// <summary>小红书发布结果。</summary>
public sealed record XhsPublishResult(
    string NoteId,
    bool Success,
    string? ErrorMessage);
