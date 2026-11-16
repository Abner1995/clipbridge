namespace CreatFlow.Platforms.WechatVideo;

using CreatFlow.Platforms.Core;
using CreatFlow.Platforms.WechatVideo.Requests;

/// <summary>
/// 视频号拍摄地区树节点 —— 国家 → 省 → 市（平台最多给三级）。
///
/// 数据源 = <c>finder_get_object_tag_list</c> 响应里 <c>tagType=5</c>（内容为自行拍摄）的
/// <c>regionInfos</c>。页面「拍摄地点」选择器用的就是它；发布体要的是节点上的整数码
/// （<c>tagInfo.shootInfo.countryCode / provinceCode / cityCode</c>）。
/// </summary>
public sealed record WechatVideoRegion(int Code, string Name, IReadOnlyList<WechatVideoRegion> Sub);

/// <summary>
/// 视频号「自行拍摄」地区列表服务。
///
/// 发布页原来让人手填三个整数码（国家 / 省 / 市）—— 没人记得住 1017 / 10160 / 100032。
/// 平台接口本来就带整棵树（页面「拍摄地点」与发布体同一数据源），改为级联选择。
///
/// 抓包证据：<c>docs/wechatvideo/20261105/requests/object_tag_list.json</c>（2026-11-05，
/// tagType=5 的 regionInfos：中国=1017 → 安徽=10160 → 合肥=100032，与
/// <c>examples/wechatvideo/media/finder_update_post_info.py</c> 的 shootInfo 样本逐位一致）。
/// </summary>
public sealed class WechatVideoRegionService(PlatformOptions options)
{
    /// <summary>用户 UIN —— 缺省从 Cookie 的 wxuin 取（与内容服务同口径）</summary>
    public string? Uin { get; set; }

    /// <summary>finder_username（localStorage 那份；只作上报字段）</summary>
    public string? FinderId { get; set; }

    /// <summary>
    /// 拉取地区树。空列表 + 非空 <c>Error</c> = 失败原因；空列表 + null = 成功但没有地区数据。
    /// 网络异常照常抛给调用方（命令层统一转状态行）。
    /// </summary>
    public async Task<(IReadOnlyList<WechatVideoRegion> Countries, string? Error)> GetRegionsAsync(
        string cookie, string deviceId, CancellationToken ct = default)
    {
        var content = new WechatVideoContentService(options) { Uin = Uin, FinderId = FinderId };
        var (tags, _) = await content.GetTagListAsync(cookie, deviceId, ct: ct);
        if (tags.Count == 0)
            return ([], "平台没有返回「视频标注」列表 —— 多半是登录凭证过期（也可能接口形状变了），"
                + "到「平台账号」重新登录后再试");

        var countries = ParseRegionTree(tags);
        return countries.Count == 0
            ? ([], "标注列表里没有「内容为自行拍摄」的地区树 —— 接口形状可能变了（可重试）")
            : (countries, null);
    }

    /// <summary>
    /// 从标注列表里取 <c>tagType=5</c> 的 regionInfos 并转成树。纯函数（自检 [46] 钉住）：
    /// 不认其它 tagType；<c>sub</c> 缺失按空列表（不是 null，界面不用再做判空）。
    /// </summary>
    public static IReadOnlyList<WechatVideoRegion> ParseRegionTree(IEnumerable<ObjectTagItem> tags) =>
        Map(tags.FirstOrDefault(t => t.TagType == 5)?.RegionInfos);

    private static IReadOnlyList<WechatVideoRegion> Map(IEnumerable<RegionInfoItem>? nodes) =>
        nodes is null
            ? []
            : [.. nodes.Select(n => new WechatVideoRegion(n.Code, n.Name ?? "", Map(n.Sub)))];
}
