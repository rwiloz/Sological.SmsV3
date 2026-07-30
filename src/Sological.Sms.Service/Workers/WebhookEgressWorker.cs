using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sological.Sms.Core.Entities;
using Sological.Sms.Service.Data;

namespace Sological.Sms.Service.Workers;

public sealed class EgressOptions
{
    public const string SectionName = "SologicalSms:Egress";
    public const string HttpClientName = "webhook-egress";

    public int PollSeconds { get; set; } = 2;
    public int BatchSize { get; set; } = 10;
    public int ClaimLeaseSeconds { get; set; } = 120;
    public int RequestTimeoutSeconds { get; set; } = 10;

    /// <summary>Backoff per retry; exhausting the list = `dead` (visible, never silent) —
    /// design §3: 1m→5m→30m→2h then dead.</summary>
    public string RetryDelays { get; set; } = "60,300,1800,7200";

    public int[] ParseRetryDelays()
        => RetryDelays.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(int.Parse).ToArray();
}

/// <summary>The customer egress worker (design §6.2): claims pending webhook_outbox rows,
/// signs the exact bytes being sent (HMAC-SHA256, per-channel secret resolved by NAME from
/// configuration — the vault feeds it), POSTs, and applies the honest delivery contract:
/// at-least-once, 2xx acks, backoff then dead. sms.inbound success also stamps
/// inbound_messages.delivered_to_customer_at (NULL = still owed).</summary>
public sealed class WebhookEgressWorker(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    IOptions<EgressOptions> options,
    ILogger<WebhookEgressWorker> logger) : BackgroundService
{
    private readonly string _workerId = $"{Environment.MachineName}:{Guid.NewGuid():N}"[..32];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Webhook egress worker {WorkerId} started", _workerId);
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
                logger.LogError(ex, "Egress sweep failed — retrying next poll");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(options.Value.PollSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var o = options.Value;
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmsDbContext>();

        var ids = await db.Database.SqlQuery<long>($"""
            UPDATE webhook_outbox SET claimed_by = {_workerId}, claimed_at = now()
            WHERE id IN (
                SELECT id FROM webhook_outbox
                WHERE state = 'pending'
                  AND next_attempt_at <= now()
                  AND (claimed_at IS NULL OR claimed_at < now() - make_interval(secs => {o.ClaimLeaseSeconds}))
                ORDER BY id
                LIMIT {o.BatchSize}
                FOR UPDATE SKIP LOCKED)
            RETURNING id AS "Value"
            """).ToListAsync(ct);
        if (ids.Count == 0) return;

        var entries = await db.WebhookOutbox.Include(e => e.Channel)
            .Where(e => ids.Contains(e.Id))
            .OrderBy(e => e.Id)
            .ToListAsync(ct);

        foreach (var entry in entries)
        {
            try
            {
                await DeliverAsync(db, entry, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Delivering outbox row {OutboxId} failed — claim will lease-expire", entry.Id);
            }
        }
    }

    private async Task DeliverAsync(SmsDbContext db, WebhookOutboxEntry entry, CancellationToken ct)
    {
        var o = options.Value;
        var channel = entry.Channel!;

        if (string.IsNullOrEmpty(channel.WebhookUrl))
        {
            // Config was removed after enqueue — dead, loudly (never a silent drop).
            await MarkDeadAsync(db, entry, "channel has no webhook_url", ct);
            return;
        }

        var secret = channel.WebhookSecretName is { Length: > 0 } name ? configuration[name] : null;
        if (string.IsNullOrEmpty(secret))
        {
            // Without the secret we cannot sign; deliverable later once config appears — defer.
            logger.LogError("Webhook secret '{SecretName}' for channel {ChannelKey} is not resolvable — deferring outbox row {OutboxId}",
                channel.WebhookSecretName, channel.Key, entry.Id);
            await DeferAsync(db, entry, ct);
            return;
        }

        // Sign the EXACT bytes being sent (jsonb normalizes formatting, so the canonical
        // bytes are produced here, at send time — design §3 note).
        var body = Encoding.UTF8.GetBytes(entry.Payload.RootElement.GetRawText());
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));

        using var request = new HttpRequestMessage(HttpMethod.Post, channel.WebhookUrl)
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        request.Headers.Add("X-Sms-Signature", $"hmac-sha256={signature}");

        HttpResponseMessage? response = null;
        try
        {
            var client = httpClientFactory.CreateClient(EgressOptions.HttpClientName);
            client.Timeout = TimeSpan.FromSeconds(o.RequestTimeoutSeconds);
            response = await client.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning("Webhook POST to channel {ChannelKey} failed ({Reason}) — outbox row {OutboxId}",
                channel.Key, ex.GetType().Name, entry.Id);
        }

        using (response)
        {
            if (response is { IsSuccessStatusCode: true })
            {
                entry.State = WebhookOutboxState.Delivered;
                entry.Attempts += 1;
                entry.ClaimedBy = null;
                entry.ClaimedAt = null;
                await StampInboundDeliveredAsync(db, entry, ct);
                await db.SaveChangesAsync(ct);
                logger.LogInformation("Webhook {EventType} delivered to channel {ChannelKey} (outbox {OutboxId})",
                    entry.EventType, channel.Key, entry.Id);
                return;
            }

            if (response is not null)
                logger.LogWarning("Webhook POST to channel {ChannelKey} answered {Status} — outbox row {OutboxId}",
                    channel.Key, (int)response.StatusCode, entry.Id);
        }

        await DeferAsync(db, entry, ct);
    }

    /// <summary>Retry with backoff on the DATABASE clock (the claim gate reads it);
    /// exhausted = dead, loudly — dead rows are visible in ops queries and the S7 report.</summary>
    private async Task DeferAsync(SmsDbContext db, WebhookOutboxEntry entry, CancellationToken ct)
    {
        var delays = options.Value.ParseRetryDelays();
        var attempts = entry.Attempts + 1;
        if (attempts > delays.Length)
        {
            await MarkDeadAsync(db, entry, $"retries exhausted after {attempts} attempts", ct);
            return;
        }

        var delay = delays[attempts - 1];
        await db.Database.ExecuteSqlAsync($"""
            UPDATE webhook_outbox
            SET attempts = {attempts}, next_attempt_at = now() + make_interval(secs => {delay}),
                claimed_by = NULL, claimed_at = NULL
            WHERE id = {entry.Id}
            """, ct);
        db.Entry(entry).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
        logger.LogWarning("Outbox row {OutboxId} deferred (attempt {Attempt}) — next try in {Delay}s",
            entry.Id, attempts, delay);
    }

    private async Task MarkDeadAsync(SmsDbContext db, WebhookOutboxEntry entry, string reason, CancellationToken ct)
    {
        entry.State = WebhookOutboxState.Dead;
        entry.Attempts += 1;
        entry.ClaimedBy = null;
        entry.ClaimedAt = null;
        await db.SaveChangesAsync(ct);
        logger.LogError("Outbox row {OutboxId} ({EventType}, channel {ChannelId}) is DEAD: {Reason}",
            entry.Id, entry.EventType, entry.ChannelId, reason);
    }

    /// <summary>delivered_to_customer_at: NULL = owed (design §3). The inbound id rides in
    /// the payload itself — the outbox row is self-describing.</summary>
    private static async Task StampInboundDeliveredAsync(SmsDbContext db, WebhookOutboxEntry entry, CancellationToken ct)
    {
        if (entry.EventType != WebhookEventType.SmsInbound)
            return;
        if (!entry.Payload.RootElement.TryGetProperty("inboundId", out var idProp) || !idProp.TryGetGuid(out var inboundId))
            return;
        var inbound = await db.InboundMessages.SingleOrDefaultAsync(m => m.Id == inboundId, ct);
        if (inbound is not null && inbound.DeliveredToCustomerAt is null)
            inbound.DeliveredToCustomerAt = DateTimeOffset.UtcNow;
    }
}
