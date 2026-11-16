namespace CreatFlow.Platforms.Xiaohongshu;

/// <summary>
/// 小红书互动组件（投票）—— <c>create_interact</c>（2026-11-11 A4）。
///
/// API: <c>POST {creatorHost}/api/galaxy/v2/creator/note/widget/create_interact</c>
/// （抓包 <c>capture-2026-11-10T08-33-18.jsonl</c> 第 583/585 行）。
/// 请求体 / 响应解析收在 <see cref="XhsPollRules"/>（纯函数，自检钉住），
/// 这里只负责签名 HTTP 与错误翻译。
///
/// 注意：
///   · 发布端**每次发布现建一个 widget**，不复用草稿里的旧 widget（页面同款）。
///   · 投票是可选组件 —— 调用方（<c>PublishPipeline</c>）必须把异常降级成
///     「不投投票 + Warning」，**不拦发布**。
/// </summary>
public sealed class XhsInteractionService
{
    private const string CreateInteractPath =
        "/api/galaxy/v2/creator/note/widget/create_interact";

    private readonly PlatformOptions _options;
    private readonly ILogger<XhsInteractionService>? _logger;

    public XhsInteractionService(PlatformOptions options,
        ILogger<XhsInteractionService>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// 创建投票，返回平台侧 <c>widget_id</c>（发布时挂进 <c>common.hash_tag</c>）。
    /// 参数不完整或平台没回 ID 抛 <see cref="XhsPublishException"/>（文案直接给用户看）。
    /// </summary>
    public async Task<string> CreateVoteAsync(
        string cookie,
        string question,
        IReadOnlyList<string> options,
        CancellationToken ct = default)
    {
        var plan = XhsPollRules.Analyze(question, options);
        if (!plan.HasVote)
        {
            throw new XhsPublishException(
                plan.Error is { Length: > 0 } why ? $"投票参数不完整：{why}" : "投票参数不完整");
        }

        using var client = new XhsApiClient(cookie, _options);

        var resp = await client.PostCreatorRawAsync(
            CreateInteractPath,
            XhsPollRules.BuildCreateBody(plan.Question, plan.Options),
            ct);

        var widgetId = XhsPollRules.ReadWidgetId(resp);
        if (widgetId is null)
        {
            var msg = resp.ValueKind == JsonValueKind.Object
                      && resp.TryGetProperty("msg", out var m) ? m.ToString() : "";
            _logger?.LogWarning("XHS create_interact rejected: {Resp}",
                resp.GetRawText()[..Math.Min(500, resp.GetRawText().Length)]);
            throw new XhsPublishException(
                $"创建投票失败：{(string.IsNullOrWhiteSpace(msg) ? "平台未给原因" : msg)}");
        }

        _logger?.LogInformation("XHS create_interact OK: widgetId={WidgetId}", widgetId);
        return widgetId;
    }
}
