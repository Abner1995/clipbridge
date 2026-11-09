using System.Security.Cryptography;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 视频文件摘要 —— 整文件 SHA-256（小写 hex）。
///
/// **提交时必须有值**：服务端 <c>PublishTask.Create</c> 对 <c>videoHash</c> 是
/// <c>ThrowIfNullOrWhiteSpace</c> 硬校验。2026-09-19 之前 NewApp 一直送空串（当时的注释
/// 写着「服务端未做强校验」，是错的），于是点「提交到队列」整批在服务端抛
/// <c>ArgumentException</c>，页面上只看到「失败 N 条」，一条也进不了队列。
///
/// 算法与老版桌面端 <c>HomeViewModel.ComputeSha256</c> 一致（同一份数据契约）；
/// 区别只是这里走异步 + 1MB 缓冲，不给 UI 线程添堵。
/// </summary>
internal static class VideoHasher
{
    /// <summary>整文件 SHA-256 → 小写 hex（64 字符）。文件读不了会抛，由调用方决定怎么处理。</summary>
    internal static async Task<string> Sha256Async(string path)
    {
        await using var fs = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1024 * 1024, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(fs));
    }
}
