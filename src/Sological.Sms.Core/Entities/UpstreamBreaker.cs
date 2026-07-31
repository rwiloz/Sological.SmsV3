namespace Sological.Sms.Core.Entities;

/// <summary>The unknown-DR circuit breaker's latch (public-surface slice, 2026-07-31).
/// A delivery receipt that correlates to NOTHING means the upstream sub-account processed
/// a message this service never sent — someone else is using the credentials (or the
/// operator is portal-testing; the threshold absorbs that). Over threshold-in-window the
/// breaker pauses every channel on the upstream and fires one operator alert SMS.
/// LATCHING: one active row per upstream (partial unique index); a human re-arms by
/// setting re_armed_at — never automatic. Re-armed rows stay as history.</summary>
public class UpstreamBreaker
{
    public long Id { get; set; }

    public UpstreamProvider Upstream { get; set; }

    public DateTimeOffset TrippedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Unmatched DRs observed in the window at trip time.</summary>
    public int UnmatchedCount { get; set; }

    public string? Reason { get; set; }

    /// <summary>When the one-shot operator alert SMS was attempted (null = not attempted;
    /// the alert is best-effort THROUGH the suspect upstream — the latch is the protection,
    /// the SMS is only notification).</summary>
    public DateTimeOffset? AlertSentAt { get; set; }

    public string? AlertError { get; set; }

    public DateTimeOffset? ReArmedAt { get; set; }
}
