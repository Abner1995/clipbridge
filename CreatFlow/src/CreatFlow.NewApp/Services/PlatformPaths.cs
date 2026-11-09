namespace CreatFlow.NewApp.Services;

/// <summary>
/// 平台账号本地路径单一来源(微信式会员隔离模型 v1):
///
///   %LocalAppData%/CreatFlow/accounts/{会员Id:N}/
///     creatflow.db                       单机版业务库(SQLite，权威)
///     sessions.json                      本地注册表镜像(联网版服务端主存 / 单机版仅镜像)
///     wv2/{sessionId:N}/                 每账号独立 WebView2 档案(cookie/指纹隔离)
///     wv2/{sessionId:N}.pending-delete   解绑清理失败的延迟删除标记(会员域)
///
/// PlatformAccountStore / AccountProfileCleanup / 账号标签坞共用,禁止散写路径。
/// </summary>
public static class PlatformPaths
{
    public static readonly string AccountsRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CreatFlow", "accounts");

    /// <summary>会员数据目录(未登录空 MemberId 极少发生:占位目录,避免写坏他人数据)</summary>
    public static string MemberDir(Guid memberId) => Path.Combine(
        AccountsRoot, memberId == Guid.Empty ? "no-member" : memberId.ToString("N"));

    /// <summary>某账号的浏览器档案目录(登录窗 EnvironmentRequested 用它隔离每账号 cookie/指纹)</summary>
    public static string ProfileDir(Guid memberId, Guid sessionId) =>
        Path.Combine(MemberDir(memberId), "wv2", sessionId.ToString("N"));

    /// <summary>会员级 sessions.json 注册表路径</summary>
    public static string SessionsJson(Guid memberId) => Path.Combine(MemberDir(memberId), "sessions.json");

    /// <summary>wv2 档案目录集(清理扫描用)</summary>
    public static string Wv2Dir(Guid memberId) => Path.Combine(MemberDir(memberId), "wv2");
}
