namespace CreatFlow.Shared;

/// <summary>
/// 订单相关展示文案常量。
/// 渠道/支付方式枚举定义在 Domain（Domain 引用 Shared，反方向不可引用），
/// 展示文案放 Shared，供管理端页面与桌面端共用，避免各处硬编码中文。
/// </summary>
public static class OrderDisplayNames
{
    // ---- 收款渠道 ----
    public const string ChannelManual = "手动";
    public const string ChannelAuto = "自动";

    // ---- 支付方式 ----
    public const string PayMethodWechat = "微信";
    public const string PayMethodAlipay = "支付宝";
    public const string PayMethodYunshanfu = "云闪付";
}
