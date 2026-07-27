namespace Sological.Sms.Core.Entities;

/// <summary>An outbound SMS (design §3). The id doubles as the upstream REFERENCE —
/// unique by construction, which is what keeps the upstream 513-duplicate rule quiet.</summary>
public class Message
{
    public Guid Id { get; set; }

    public long ChannelId { get; set; }

    public Channel? Channel { get; set; }

    /// <summary>The CALLER's correlation handle (≤64), echoed on every webhook. Uniqueness
    /// per channel is enforced in the API layer on the new surface only — the S5 legacy
    /// surface must accept duplicates, so there is deliberately NO unique constraint here.</summary>
    public string? CustomerRef { get; set; }

    public required string ToNumber { get; set; }

    public required string OriginatorUsed { get; set; }

    public required string Body { get; set; }

    /// <summary>GSM7/UCS-2 part count — the billing dimension.</summary>
    public short Parts { get; set; }

    public MessageStatus Status { get; set; } = MessageStatus.Queued;

    public string? ErrorCode { get; set; }

    public string? ErrorDetail { get; set; }

    public UpstreamProvider Upstream { get; set; } = UpstreamProvider.SmsCentral;

    /// <summary>The upstream's own id for the submission (SMS Central `ID`).</summary>
    public string? UpstreamId { get; set; }

    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? SubmittedAt { get; set; }

    public DateTimeOffset? DeliveredAt { get; set; }

    public DateTimeOffset? FailedAt { get; set; }

    /// <summary>Dispatch-worker claim; leases expire so a dead replica's work is reclaimed (design §9).</summary>
    public string? ClaimedBy { get; set; }

    public DateTimeOffset? ClaimedAt { get; set; }
}
