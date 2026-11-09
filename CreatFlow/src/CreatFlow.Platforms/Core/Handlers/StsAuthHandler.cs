namespace CreatFlow.Platforms.Core.Handlers;

/// <summary>
/// STS 临时凭证认证 Handler — 添加 X-Amz-Date 和 X-Amz-Security-Token 头。
///
/// 用于抖音 ImageX/TOS 服务的 AWS SigV4 签名流程。
/// </summary>
public sealed class StsAuthHandler : DelegatingHandler
{
    private readonly string _accessKeyId;
    private readonly string _secretAccessKey;
    private readonly string _sessionToken;

    public StsAuthHandler(
        string accessKeyId, string secretAccessKey, string sessionToken)
    {
        _accessKeyId = accessKeyId;
        _secretAccessKey = secretAccessKey;
        _sessionToken = sessionToken;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var amzDate = now.ToString("yyyyMMddTHHmmssZ");
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-security-token", _sessionToken);

        return base.SendAsync(request, ct);
    }
}
