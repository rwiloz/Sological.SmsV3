using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sological.Sms.Core.Entities;
using Sological.Sms.Core.Sms;
using Sological.Sms.Core.Upstream;
using Sological.Sms.Service.Data;

namespace Sological.Sms.Service.Workers;

public sealed class DispatchOptions
{
    public const string SectionName = "SologicalSms:Dispatch";

    public int PollSeconds { get; set; } = 2;
    public int BatchSize { get; set; } = 10;

    /// <summary>Claims older than this are reclaimable — a dead replica's work is picked up (design §9).</summary>
    public int ClaimLeaseSeconds { get; set; } = 120;

    /// <summary>Comma-separated backoff seconds per retry attempt; exhausting the list =
    /// terminal failed (design §4.1). A string, not an array, because the config binder
    /// CONCATENATES array overrides onto defaults instead of replacing them.</summary>
    public string RetryDelays { get; set; } = "60,300,900";

    public int[] ParseRetryDelays()
        => RetryDelays.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(int.Parse).ToArray();
}

/// <summary>The send lane's worker (design §1/§6.1a): claims queued messages, runs the
/// pre-dispatch guards (recipient → duplicate → originator whitelist; all terminal +
/// unbilled; recipient normalization MUST precede the duplicate compare so raw and
/// normalized spellings of the same number match), then submits through the upstream seam.
/// One billable event = one ledger row, written in the SAME transaction as the sent
/// transition.</summary>
public sealed class DispatchWorker(
    IServiceScopeFactory scopeFactory,
    ISmsUpstream upstream,
    IOptionsMonitor<DispatchOptions> options, // monitor: config_entries overrides hot-apply
    ILogger<DispatchWorker> logger) : BackgroundService
{
    private readonly string _workerId = $"{Environment.MachineName}:{Guid.NewGuid():N}"[..32];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Dispatch worker {WorkerId} started", _workerId);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Dispatch sweep failed — retrying next poll");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(options.CurrentValue.PollSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        logger.LogInformation("Dispatch worker {WorkerId} stopping", _workerId);
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var o = options.CurrentValue;
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmsDbContext>();

        // Claim a batch: fresh queued rows, due retries, and lease-expired claims from dead
        // replicas ('submitting' rows whose claim went stale — resubmission is safe because
        // REFERENCE = uuid makes the upstream 513-dedupe our idempotency net).
        var ids = await db.Database.SqlQuery<Guid>($"""
            UPDATE messages SET claimed_by = {_workerId}, claimed_at = now()
            WHERE id IN (
                SELECT id FROM messages
                WHERE status IN ('queued', 'submitting')
                  AND (next_attempt_at IS NULL OR next_attempt_at <= now())
                  AND (claimed_at IS NULL OR claimed_at < now() - make_interval(secs => {o.ClaimLeaseSeconds}))
                ORDER BY requested_at
                LIMIT {o.BatchSize}
                FOR UPDATE SKIP LOCKED)
            RETURNING id AS "Value"
            """).ToListAsync(ct);

        if (ids.Count == 0) return;

        var messages = await db.Messages.Include(m => m.Channel)
            .Where(m => ids.Contains(m.Id))
            .OrderBy(m => m.RequestedAt)
            .ToListAsync(ct);

        foreach (var message in messages)
        {
            try
            {
                await ProcessAsync(db, message, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Log-never-swallow; the claim lease expires and the row is retried.
                logger.LogError(ex, "Processing message {MessageId} failed — claim will lease-expire", message.Id);
            }
        }
    }

    private async Task ProcessAsync(SmsDbContext db, Message message, CancellationToken ct)
    {
        var o = options.CurrentValue;
        var channel = message.Channel!;

        // A paused channel (or disabled customer) holds its queue — honest wait, not a drop.
        if (channel.Status != ChannelStatus.Active)
        {
            logger.LogInformation("Channel {ChannelKey} is {Status} — message {MessageId} stays queued",
                channel.Key, channel.Status, message.Id);
            ReleaseClaim(message);
            await db.SaveChangesAsync(ct);
            return;
        }

        // Guard 1 — local recipient validation + normalization. Runs FIRST so the
        // duplicate compare below sees one canonical spelling per number.
        var recipient = RecipientValidator.Validate(message.ToNumber);
        if (!recipient.IsValid)
        {
            message.Status = MessageStatus.Rejected;
            message.ErrorCode = "invalid_recipient";
            message.ErrorDetail = recipient.Error;
            message.FailedAt = DateTimeOffset.UtcNow;
            ReleaseClaim(message);
            Egress.WebhookOutbox.EnqueueDelivery(db, channel, message, logger);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Message {MessageId} rejected: invalid recipient ***{Last4}",
                message.Id, Last4(message.ToNumber));
            return;
        }
        message.ToNumber = recipient.Normalized!;

        // Guard 1b — per-channel recipient allowlist (public-surface slice, 2026-07-31):
        // dev channels pin their audience so a leaked key is contained. Compares the
        // NORMALIZED number — allowlists store E.164.
        if (channel.AllowedRecipients is { Length: > 0 } && !channel.AllowedRecipients.Contains(message.ToNumber))
        {
            message.Status = MessageStatus.Rejected;
            message.ErrorCode = "recipient_not_allowed";
            message.ErrorDetail = $"recipient {message.ToNumber} is not on this channel's allowlist";
            message.FailedAt = DateTimeOffset.UtcNow;
            ReleaseClaim(message);
            Egress.WebhookOutbox.EnqueueDelivery(db, channel, message, logger);
            await db.SaveChangesAsync(ct);
            logger.LogWarning("Message {MessageId} rejected: recipient ***{Last4} not on channel {ChannelKey} allowlist",
                message.Id, Last4(message.ToNumber), channel.Key);
            return;
        }

        // Guard 2 — duplicate detection (design §6.1a; a PRODUCT feature): same
        // (reference, recipient, body) as an earlier message inside the channel window.
        // Null references compare as equal, so ref-less double-submits are caught too.
        var windowStart = message.RequestedAt - TimeSpan.FromSeconds(channel.DuplicateWindowSeconds);
        var isDuplicate = await db.Messages.AnyAsync(m =>
            m.ChannelId == message.ChannelId &&
            m.Id != message.Id &&
            m.CustomerRef == message.CustomerRef &&
            m.ToNumber == message.ToNumber &&
            m.Body == message.Body &&
            m.RequestedAt < message.RequestedAt &&
            m.RequestedAt >= windowStart, ct);
        if (isDuplicate)
        {
            message.Status = MessageStatus.Duplicate;
            message.ErrorCode = "duplicate";
            message.ErrorDetail = $"same reference/recipient/body as an earlier message within {channel.DuplicateWindowSeconds}s";
            ReleaseClaim(message);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Message {MessageId} flagged duplicate (never dispatched, never billed)", message.Id);
            return;
        }

        // Guard 3 — originator whitelist (ACMA sender-ID ruling, 2026-07-27).
        var whitelisted = await db.AllowedOriginators.AnyAsync(a => a.Originator == message.OriginatorUsed, ct);
        if (!whitelisted)
        {
            message.Status = MessageStatus.Rejected;
            message.ErrorCode = "invalid_originator";
            message.ErrorDetail = $"originator '{message.OriginatorUsed}' is not on the sender-ID whitelist";
            message.FailedAt = DateTimeOffset.UtcNow;
            ReleaseClaim(message);
            Egress.WebhookOutbox.EnqueueDelivery(db, channel, message, logger);
            await db.SaveChangesAsync(ct);
            logger.LogWarning("Message {MessageId} rejected: originator {Originator} not whitelisted",
                message.Id, message.OriginatorUsed);
            return;
        }

        // Guard 4 — daily part quota (public-surface slice, 2026-07-31): a hard ceiling on
        // what a channel can spend per UTC day, no matter how politely a leaked key stays
        // under the rate limit. Counted on SUBMITTED parts (DB clock) so guard verdicts
        // never consume quota.
        if (channel.DailyPartLimit is int partLimit)
        {
            var usedToday = await db.Database.SqlQuery<int>($"""
                SELECT COALESCE(SUM(parts), 0)::int AS "Value" FROM messages
                WHERE channel_id = {channel.Id} AND submitted_at >= date_trunc('day', now())
                """).SingleAsync(ct);
            if (usedToday + message.Parts > partLimit)
            {
                message.Status = MessageStatus.Rejected;
                message.ErrorCode = "quota_exceeded";
                message.ErrorDetail = $"daily part quota ({partLimit}) would be exceeded: {usedToday} part(s) already submitted today (UTC)";
                message.FailedAt = DateTimeOffset.UtcNow;
                ReleaseClaim(message);
                Egress.WebhookOutbox.EnqueueDelivery(db, channel, message, logger);
                await db.SaveChangesAsync(ct);
                logger.LogWarning("Message {MessageId} rejected: channel {ChannelKey} daily part quota {Limit} reached ({Used} used)",
                    message.Id, channel.Key, partLimit, usedToday);
                return;
            }
        }

        message.Status = MessageStatus.Submitting;
        await db.SaveChangesAsync(ct);

        UpstreamSubmitResult result;
        try
        {
            result = await upstream.SubmitAsync(
                new OutboundSms(message.Id, message.OriginatorUsed, message.ToNumber, message.Body), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Upstream submit threw for message {MessageId} — treating as retryable", message.Id);
            result = new UpstreamSubmitResult(false, null, "transport_error", ex.GetType().Name, Retryable: true);
        }

        if (result.Accepted)
        {
            message.Status = MessageStatus.Sent;
            message.SubmittedAt = DateTimeOffset.UtcNow;
            message.UpstreamId = result.UpstreamId;
            message.ErrorCode = null;
            message.ErrorDetail = result.ErrorCode == "513" ? result.ErrorDetail : null;
            ReleaseClaim(message);
            db.BillingLedger.Add(new BillingLedgerEntry
            {
                CustomerId = channel.CustomerId,
                ChannelId = channel.Id,
                RefType = LedgerRefType.Message,
                RefId = message.Id,
                Direction = SmsDirection.Outbound,
                Units = message.Parts,
            });
            Egress.WebhookOutbox.EnqueueDelivery(db, channel, message, logger);
            await db.SaveChangesAsync(ct); // sent + ledger + customer event commit together
            logger.LogInformation("Message {MessageId} sent to ***{Last4} ({Parts} part(s))",
                message.Id, Last4(message.ToNumber), message.Parts);
            return;
        }

        if (!result.Retryable)
        {
            message.Status = MessageStatus.Rejected;
            message.ErrorCode = result.ErrorCode;
            message.ErrorDetail = result.ErrorDetail;
            message.FailedAt = DateTimeOffset.UtcNow;
            ReleaseClaim(message);
            Egress.WebhookOutbox.EnqueueDelivery(db, channel, message, logger);
            await db.SaveChangesAsync(ct);
            logger.LogWarning("Message {MessageId} rejected by upstream: {Code} {Detail}",
                message.Id, result.ErrorCode, result.ErrorDetail);
            return;
        }

        var delays = o.ParseRetryDelays();
        message.Attempts += 1;
        if (message.Attempts > delays.Length)
        {
            message.Status = MessageStatus.Failed;
            message.ErrorCode = result.ErrorCode;
            message.ErrorDetail = $"retries exhausted after {message.Attempts} attempts: {result.ErrorDetail}";
            message.FailedAt = DateTimeOffset.UtcNow;
            ReleaseClaim(message);
            Egress.WebhookOutbox.EnqueueDelivery(db, channel, message, logger);
            await db.SaveChangesAsync(ct);
            logger.LogError("Message {MessageId} failed after {Attempts} attempts: {Code} {Detail}",
                message.Id, message.Attempts, result.ErrorCode, result.ErrorDetail);
            return;
        }

        // next_attempt_at is compared against the DATABASE clock in the claim query, so it
        // is written from the database clock too — one raw statement, then detach (the
        // tracked entity is stale on purpose; this sweep is done with it).
        var delay = delays[message.Attempts - 1];
        await db.Database.ExecuteSqlAsync($"""
            UPDATE messages
            SET status = 'queued', attempts = {message.Attempts},
                next_attempt_at = now() + make_interval(secs => {delay}),
                claimed_by = NULL, claimed_at = NULL
            WHERE id = {message.Id}
            """, ct);
        db.Entry(message).State = EntityState.Detached;
        logger.LogWarning("Message {MessageId} deferred (attempt {Attempt}, upstream {Code}) — next try in {Delay}s",
            message.Id, message.Attempts, result.ErrorCode, delay);
    }

    private static void ReleaseClaim(Message message)
    {
        message.ClaimedBy = null;
        message.ClaimedAt = null;
    }

    private static string Last4(string number) => number.Length >= 4 ? number[^4..] : number;
}
