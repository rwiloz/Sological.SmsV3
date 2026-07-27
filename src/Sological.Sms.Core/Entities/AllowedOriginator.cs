namespace Sological.Sms.Core.Entities;

/// <summary>The Ray-controlled sender-ID whitelist (ruled 2026-07-27 — ACMA enforces
/// sender-ID compliance). A send whose resolved originator has no row here is terminally
/// rejected before any upstream submit. Alphanumeric IDs enter only once registered on the
/// ACMA SMS Sender ID Register.</summary>
public class AllowedOriginator
{
    /// <summary>The originator exactly as channels/messages carry it (natural key).</summary>
    public required string Originator { get; set; }

    public string? Description { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
