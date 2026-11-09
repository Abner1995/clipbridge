namespace CreatFlow.Shared;

/// <summary>
/// 统一时间源——北京时间 (UTC+8)。
/// 所有需要获取当前时间的代码都应通过 Clock.Now，不直接调 DateTime.UtcNow。
/// </summary>
public static class Clock
{
    private static readonly TimeZoneInfo BeijingTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");

    /// <summary>北京时间现在</summary>
    public static DateTime Now => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, BeijingTimeZone);
}
