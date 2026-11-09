namespace CreatFlow.Shared;

/// <summary>
/// 合集的内容类型。
///
/// **只有今日头条**把合集按内容形态拆成两套完全独立的系统：
///   - 视频合集 / 专辑（pSeries）：`/xigua/api/pSeries/*`，可跨平台同步；
///   - 文章合集：`/mp/fe_api/collection/*`，**仅头条**（其他平台没有文章合集）。
/// 其他平台（抖音 mix / 快手作品合集 / 小红书笔记合集 / 视频号合集 / B站 season）
/// 都只有一套合集体系，一律按 <see cref="Video"/> 处理。
///
/// 因此文章合集是「逻辑合集级」的属性：选了文章就只允许勾头条，
/// 且创建后不可改类型（平台侧视频 AlbumId 与文章 collection_id 是两个对象）。
/// </summary>
public enum CollectionContentType
{
    /// <summary>视频合集（默认；可跨平台）</summary>
    Video = 0,

    /// <summary>文章合集（仅今日头条）</summary>
    Article = 1,
}
