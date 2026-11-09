using System.Diagnostics;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 用系统外壳打开东西 —— 全项目唯一一处 <c>Process.Start</c>。
///
/// 单独成文件是因为「打开资源管理器」这种调用有两个固定陷阱（路径带空格、进程不存在），
/// 散在各处迟早有一处漏掉。
/// </summary>
internal static class ShellLauncher
{
    /// <summary>
    /// 在资源管理器中打开一个目录。返回 false = 没打开（调用方降级为「把路径显示给用户」，
    /// 不能让「新建模板」整个失败）。目录已经打开时 explorer 会激活已有窗口，不会开第二个。
    /// </summary>
    internal static bool RevealFolder(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return false;

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                // 必须带引号：C:\Program Files\… 这种带空格的路径会在第一个空格处断掉，
                // 而 explorer 收到残缺路径只会弹一个「找不到」的对话框。
                Arguments = $"\"{path}\"",
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Shell] 打开资源管理器失败 {Path}", path);
            return false;
        }
    }
}
