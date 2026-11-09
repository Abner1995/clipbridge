using Microsoft.Data.Sqlite;

namespace CreatFlow.LocalStore;

/// <summary>清除结果：<see cref="Ok"/> = 全部删掉；<see cref="Failed"/> 非空表示部分文件被占用。</summary>
public sealed record WipeResult(bool Ok, IReadOnlyList<string> Removed, IReadOnlyList<string> Failed);

/// <summary>
/// 单机版「清除本机数据」（doc §11.3）。
///
/// 范围（刻意收得很窄）：
///   · 本机会员目录 = creatflow.db + JSON 档 + backups\ + wv2 浏览器档案（都在
///     <see cref="LocalStorePaths.MemberDir"/> 下）；
///   · 机器级 <c>models / logs / crashes</c> 只在 <c>includeMachineLevel=true</c> 时删
///     （模型几个 GB、日志用于排障，默认保留）；
///   · **其它会员目录 / 安装目录 / 用户文档一律不碰**。
///
/// 调用方必须先做：导出备份（可选但默认）→ 二次确认 + 输入确认词 → 清除后立即重启
/// （内存里的旧 DbContext / 页面状态不会自动换；继续跑还会把库/档写回来）。
/// </summary>
public static class LocalDataWipe
{
    /// <summary>机器级白名单目录名（相对 <see cref="LocalStorePaths.MachineRoot"/>）</summary>
    private static readonly string[] MachineDirNames = ["models", "logs", "crashes"];

    /// <summary>会被 <c>includeMachineLevel=true</c> 删除的机器级目录（自检/UI 提示用）</summary>
    public static IReadOnlyList<string> MachineLevelDirs =>
        MachineDirNames.Select(n => Path.Combine(LocalStorePaths.MachineRoot, n)).ToList();

    /// <summary>删除本机会员数据；可选连机器级模型/日志一起删。</summary>
    public static WipeResult ClearMemberData(bool includeMachineLevel = false)
    {
        var removed = new List<string>();
        var failed = new List<string>();

        // 先释放 SQLite 连接池：Windows 上删「仍被打开」的 db/-wal 会直接失败
        // （与 ReplaceDatabase 同款处理）。
        SqliteConnection.ClearAllPools();

        DeleteDir(LocalStorePaths.MemberDir, removed, failed);

        if (includeMachineLevel)
        {
            foreach (var dir in MachineLevelDirs)
                DeleteDir(dir, removed, failed);
        }

        return new WipeResult(failed.Count == 0, removed, failed);
    }

    /// <summary>
    /// 递归删除目录。给句柄释放留时间（WebView2 退出、SQLite 池释放可能是滞后的）：
    /// 最多重试 4 次；仍失败就把路径 + 原因带回给调用方，绝不吞。
    /// </summary>
    private static void DeleteDir(string dir, List<string> removed, List<string> failed)
    {
        if (!Directory.Exists(dir)) return;

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
                removed.Add(dir);
                return;
            }
            catch (Exception ex)
            {
                if (attempt >= 4)
                {
                    failed.Add($"{dir}（{ex.Message}）");
                    return;
                }
                Thread.Sleep(150);
            }
        }
    }
}
