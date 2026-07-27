using System.Text.Json;

namespace Sological.Sms.Core.Entities;

/// <summary>Customer webhook egress queue (design §3/§6.2). Claim-based worker; backoff
/// 1m→5m→30m→2h ×3 then dead — dead rows stay visible, never silent.</summary>
public class WebhookOutboxEntry
{
    public long Id { get; set; }

    public long ChannelId { get; set; }

    public Channel? Channel { get; set; }

    public WebhookEventType EventType { get; set; }

    /// <summary>The event body to deliver (jsonb). The HMAC signature is computed at send
    /// time over the exact bytes serialized then — jsonb normalizes formatting, so stored
    /// bytes are NOT the signed bytes.</summary>
    public required JsonDocument Payload { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;

    public WebhookOutboxState State { get; set; } = WebhookOutboxState.Pending;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Egress-worker claim; leases expire like the dispatch claims (design §9).</summary>
    public string? ClaimedBy { get; set; }

    public DateTimeOffset? ClaimedAt { get; set; }
}
