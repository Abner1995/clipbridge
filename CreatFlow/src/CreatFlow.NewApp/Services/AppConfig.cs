using System.Reflection;
using Microsoft.Extensions.Configuration;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 桌面端全局配置（自老 App 拷贝，2026-09-05 NewApp 首版）。
///
/// 加载优先级（后者覆盖前者）:
///   1. appsettings.json              — 共享默认值
///   2. appsettings.{Environment}.json — 按环境覆盖（Development / Staging / Production）
///   3. CREATFLOW_* 环境变量           — 最终覆盖（CI/CD 或生产部署）
/// </summary>
public static class AppConfig
{
    private static readonly IConfigurationRoot Config;

    static AppConfig()
    {
#if DEBUG
        var env = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Development";
#else
        var env = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production";
#endif

        Config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile($"appsettings.{env}.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables("CREATFLOW_")
            .Build();
    }

    /// <summary>
    /// 版本分支：Online（联网版）/ Standalone（单机版）。
    ///
    /// 单机版分支在 csproj 里编译期定义 CREATFLOW_STANDALONE —— 不依赖安装目录里
    /// 有没有配置文件（配置文件可被用户删/改，分支语义不该这么脆弱）。
    /// 运行期仍可用配置 / CREATFLOW_Edition 覆盖，便于开发时同一份二进制切换。
    /// </summary>
    public static string Edition => Config["Edition"] ?? DefaultEdition;

    /// <summary>是否单机版（无服务端、无登录、本地 SQLite）</summary>
    public static bool IsStandalone =>
        string.Equals(Edition, "Standalone", StringComparison.OrdinalIgnoreCase);

    private static string DefaultEdition =>
#if CREATFLOW_STANDALONE
        "Standalone";
#else
        "Online";
#endif

    /// <summary>服务端地址（不带尾部斜杠）</summary>
    public static string ServerBaseUrl =>
        Config["ServerUrl"] ?? "https://localhost:5079";

    /// <summary>CB-SigV1 签名密钥 — 用于 login / register 等公开端点</summary>
    public static string AppSecret =>
        Config["AppSecret"] ?? "CB-AppSecret-v1-ChangeMeInProduction-2026";

    /// <summary>管理员联系邮箱（原始串；多邮箱用 / 分隔）。公开版默认留空，由使用者自行配置。</summary>
    public static string ContactEmail =>
        Config["Contact:Email"] ?? "";

    /// <summary>管理员联系邮箱（拆分后；兼容 / ; ， 、 与换行分隔）</summary>
    public static IReadOnlyList<string> ContactEmails => SplitEmails(ContactEmail);

    /// <summary>登录窗底部展示用：多个邮箱各占一行（2026-10-22 用户反馈：原来用 / 挤成一行）</summary>
    public static string ContactEmailDisplay => string.Join("\n", ContactEmails);

    /// <summary>工作时间说明</summary>
    public static string ContactWorkHours =>
        Config["Contact:WorkHours"] ?? "周一到周五 9:00-17:00，法定假日放假";

    /// <summary>拆分配置里的多个邮箱/联系方式（保留原顺序，去掉空白项）</summary>
    internal static IReadOnlyList<string> SplitEmails(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        return raw
            .Split(['/', ';', '，', ',', '、', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .ToList();
    }

    /// <summary>钉钉群机器人创建教程标题</summary>
    public static string DingTalkGuideTitle =>
        Config["DingTalkGuide:Title"] ?? "如何创建钉钉群机器人";

    /// <summary>钉钉群机器人创建教程步骤（自老 App 补拷，钉钉页展示用）</summary>
    public static IReadOnlyList<(int Step, string Title, string Detail)> DingTalkGuideSteps
    {
        get
        {
            var steps = Config.GetSection("DingTalkGuide:Steps").GetChildren();
            return steps.Select(s =>
            {
                var step = int.TryParse(s["Step"], out var n) ? n : 0;
                var title = s["Title"] ?? "";
                var detail = s["Detail"] ?? "";
                return (step, title, detail);
            }).ToList();
        }
    }

    /// <summary>
    /// 当前客户端版本号 —— 与安装包 / 文件版本同源：
    /// 优先读 <see cref="AssemblyInformationalVersionAttribute"/>（MSBuild `-p:Version` 注入，
    /// 形如 `1.0.2`；裁剪掉可能的 `+&lt;commit&gt;` 构建元数据），退回 AssemblyVersion 的三段式。
    ///
    /// ⚠️ **不要用 `Assembly.GetName().Version`** —— .NET 永远把它补成四段（`1.0.0.0`），
    /// 与安装包名 / Inno Setup 的 `1.0.0` 对不上（2026-10-21 用户实测截图 v1.0.0.0）。
    /// </summary>
    public static string AppVersion
    {
        get
        {
            var asm = Assembly.GetEntryAssembly() ?? typeof(AppConfig).Assembly;

            var informational = asm
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
                return informational.Split('+')[0];

            var v = asm.GetName().Version;
            if (v is null) return "1.0.0";
            return v.Build < 0 ? $"{v.Major}.{v.Minor}" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    /// <summary>发布结果提示音开关（PublishSounds 配置键，默认开）</summary>
    public static bool PublishSoundsEnabled =>
        !string.Equals(Config["PublishSounds"], "false", StringComparison.OrdinalIgnoreCase);
}
