using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sological.Sms.Core.Upstream;
using Sological.Sms.Service.Data;

namespace Sological.Sms.Service.Ingress;

public sealed class BreakerOptions
{
    public const string SectionName = "SologicalSms:Breaker";

    public bool Enabled { get; set; } = true;

    /// <summary>Unmatched DRs inside the window that trip the breaker. Sized so a couple
    /// of operator portal test sends never trip it, real volume trips it in seconds.</summary>
    public int UnmatchedThreshold { get; set; } = 10;

    public int WindowMinutes { get; set; } = 5;

    /// <summary>E.164 operator number for the one-shot trip alert; empty = no SMS.</summary>
    public string AlertNumber { get; set; } = "";

    /// <summary>Must be a sender ID the sub-account is REGISTERED for (the 333 lesson,
    /// 2026-07-31) or the alert dies at the upstream compliance gate.</summary>
    public string AlertOriginator { get; set; } = "SoLogical";
}

/// <summary>The unknown-DR circuit breaker (public-surface slice, 2026-07-31). Called by
/// the delivery ingress AFTER an uncorrelated DR's audit row is committed. Over
/// threshold-in-window: latch (partial unique index arbitrates races), pause every channel
/// on the upstream, fire ONE best-effort operator alert SMS — direct through the upstream
/// seam, NOT the message pipeline (operator machinery, unbilled, and the pipeline it would
/// ride was just paused). The latch makes the alert storm-proof by construction.</summary>
public sealed class UpstreamBreakerService(
    SmsDbContext db,
    ISmsUpstream upstream,
    IOptionsMonitor<BreakerOptions> options, // monitor: config_entries overrides hot-apply
    ILogger<UpstreamBreakerService> logger)
{
    public async Task RecordUnmatchedAsync(CancellationToken ct)
    {
        var o = options.CurrentValue;
        if (!o.Enabled) return;

        var count = await db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM delivery_events
            WHERE provider = 'smscentral' AND message_id IS NULL
              AND received_at > now() - make_interval(mins => {o.WindowMinutes})
            """).SingleAsync(ct);
        if (count < o.UnmatchedThreshold) return;

        // The latch: only the INSERT winner runs the trip actions. WHERE NOT EXISTS is the
        // fast path; the partial unique index settles the true race (23505 = lost, done).
        List<long> latch;
        try
        {
            latch = await db.Database.SqlQuery<long>($"""
                INSERT INTO upstream_breakers (upstream, tripped_at, unmatched_count, reason)
                SELECT 'smscentral', now(), {count}, 'unmatched delivery receipts over threshold'
                WHERE NOT EXISTS (
                    SELECT 1 FROM upstream_breakers WHERE upstream = 'smscentral' AND re_armed_at IS NULL)
                RETURNING id AS "Value"
                """).ToListAsync(ct);
        }
        catch (Exception ex) when ((ex.InnerException ?? ex) is Npgsql.PostgresException { SqlState: "23505" })
        {
            return;
        }
        if (latch.Count == 0) return; // already latched

        var paused = await db.Database.SqlQuery<string>($"""
            UPDATE channels SET status = 'paused'
            WHERE upstream = 'smscentral' AND status = 'active'
            RETURNING key AS "Value"
            """).ToListAsync(ct);
        logger.LogCritical(
            "UPSTREAM BREAKER TRIPPED for smscentral: {Count} unmatched DRs in {Window}m — paused channels: [{Channels}]. Re-arm manually (upstream_breakers.re_armed_at).",
            count, o.WindowMinutes, string.Join(", ", paused));

        if (string.IsNullOrEmpty(o.AlertNumber)) return;

        string? alertError = null;
        try
        {
            var result = await upstream.SubmitAsync(new OutboundSms(
                Guid.CreateVersion7(), o.AlertOriginator, o.AlertNumber,
                $"SOLOGICAL SMS BREAKER TRIPPED: {count} unknown delivery receipts in {o.WindowMinutes} min on smscentral. All channels paused. Re-arm manually."), ct);
            if (!result.Accepted)
                alertError = $"{result.ErrorCode} {result.ErrorDetail}";
        }
        catch (Exception ex)
        {
            alertError = ex.Message;
        }

        await db.Database.ExecuteSqlAsync($"""
            UPDATE upstream_breakers SET alert_sent_at = now(), alert_error = {alertError}
            WHERE id = {latch[0]}
            """, ct);
        if (alertError is null)
            logger.LogWarning("Breaker alert SMS submitted to {Number}", o.AlertNumber);
        else
            logger.LogError("Breaker alert SMS FAILED (the latch still holds): {Error}", alertError);
    }
}
