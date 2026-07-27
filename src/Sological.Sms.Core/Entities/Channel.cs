namespace Sological.Sms.Core.Entities;

/// <summary>A customer's sending identity + credentials (design §2). Auth is API keys ONLY
/// (two live for rotation, stored hashed) — no IP allowlisting in v2 (ruled 2026-07-27).</summary>
public class Channel
{
    public long Id { get; set; }

    public long CustomerId { get; set; }

    public Customer? Customer { get; set; }

    /// <summary>Unique auth handle — the legacy ExternalID equivalent.</summary>
    public required string Key { get; set; }

    public string? Description { get; set; }

    /// <summary>≤11 alphanumerics, "shared", or a dedicated number.</summary>
    public required string Originator { get; set; }

    public string? ApiKey1Hash { get; set; }

    public string? ApiKey2Hash { get; set; }

    public string? WebhookUrl { get; set; }

    /// <summary>Secret-store NAME of the webhook HMAC secret (SmsV2:Webhook:{channelKey}) — never the value.</summary>
    public string? WebhookSecretName { get; set; }

    public UpstreamProvider Upstream { get; set; } = UpstreamProvider.SmsCentral;

    public ChannelStatus Status { get; set; } = ChannelStatus.Active;

    /// <summary>Duplicate-detection window for the §6.1a guard (default 1h, per-channel).</summary>
    public int DuplicateWindowSeconds { get; set; } = 3600;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
