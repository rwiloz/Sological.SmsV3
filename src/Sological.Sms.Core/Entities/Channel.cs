namespace Sological.Sms.Core.Entities;

/// <summary>A customer's sending identity + credentials (design §2). Auth is API keys ONLY
/// (two live for rotation, stored hashed) — no IP allowlisting in v3 (ruled 2026-07-27).</summary>
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

    /// <summary>Secret-store NAME of the webhook HMAC secret (SmsV3:Webhook:{channelKey}) — never the value.</summary>
    public string? WebhookSecretName { get; set; }

    public UpstreamProvider Upstream { get; set; } = UpstreamProvider.SmsCentral;

    public ChannelStatus Status { get; set; } = ChannelStatus.Active;

    /// <summary>Duplicate-detection window for the §6.1a guard (default 1h, per-channel).</summary>
    public int DuplicateWindowSeconds { get; set; } = 3600;

    /// <summary>Hard ceiling on parts submitted per UTC day (public-surface slice,
    /// 2026-07-31): beyond it the dispatch guard rejects `quota_exceeded`. Null = unlimited.
    /// Caps total damage from a leaked key that stays politely under the rate limit.</summary>
    public int? DailyPartLimit { get; set; }

    /// <summary>Normalized E.164 recipients this channel may text; null/empty = unrestricted.
    /// Dev channels pin this to the operator's test number so a leaked key is a nuisance,
    /// not a smishing kit.</summary>
    public string[]? AllowedRecipients { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
