using Sological.Sms.Core.Entities;

namespace Sological.Sms.Core.Upstream;

/// <summary>The driver seam (design §4). Ingress is deliberately NOT here — receivers are
/// provider-specific endpoints. This is the whole S6 cutover surface.</summary>
public interface ISmsUpstream
{
    /// <summary>The provider this driver submits to — recorded on each message at submit.</summary>
    UpstreamProvider Provider { get; }

    /// <summary>Returns the upstream's accept/reject verdict for ONE message.</summary>
    Task<UpstreamSubmitResult> SubmitAsync(OutboundSms sms, CancellationToken ct);
}

public sealed record OutboundSms(Guid MessageId, string Originator, string To, string Body);

/// <summary>Retryable separates §4.1's bounded-retry outcomes (500/536, transport faults)
/// from hard rejects; meaningless when Accepted.</summary>
public sealed record UpstreamSubmitResult(
    bool Accepted,
    string? UpstreamId,
    string? ErrorCode,
    string? ErrorDetail,
    bool Retryable = false);
