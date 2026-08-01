using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sological.Sms.Core.Entities;
using Sological.Sms.Service.Data;
using Sological.Sms.Service.Ingress;

namespace Sological.Sms.Service.Workers;

/// <summary>Flushes stale multipart inbound groups (design §3): when a group stops
/// receiving parts for PartTimeoutSeconds, deliver what arrived, ordered, complete=false.
/// Loud by design — a partial flush is a correctness event, never metric-only.</summary>
public sealed class InboundPartsSweeper(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<IngressOptions> options, // monitor: config_entries overrides hot-apply
    ILogger<InboundPartsSweeper> logger) : BackgroundService
{
    private sealed record StaleGroup(long ChannelId, string FromNumber, string GroupRef);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
                logger.LogError(ex, "Inbound parts sweep failed — retrying next interval");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(options.CurrentValue.SweepIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var timeout = options.CurrentValue.PartTimeoutSeconds;
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmsDbContext>();

        var stale = await db.InboundParts
            .GroupBy(p => new { p.ChannelId, p.FromNumber, p.GroupRef })
            .Where(g => g.Max(p => p.ReceivedAt) < DateTimeOffset.UtcNow.AddSeconds(-timeout))
            .Select(g => new StaleGroup(g.Key.ChannelId, g.Key.FromNumber, g.Key.GroupRef))
            .ToListAsync(ct);

        foreach (var group in stale)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlAsync(
                $"SELECT pg_advisory_xact_lock(hashtext({group.ChannelId + "|" + group.FromNumber + "|" + group.GroupRef}))", ct);

            var parts = await db.InboundParts
                .Where(p => p.ChannelId == group.ChannelId && p.FromNumber == group.FromNumber && p.GroupRef == group.GroupRef)
                .OrderBy(p => p.PartNo)
                .ToListAsync(ct);
            if (parts.Count == 0)
            {
                await tx.CommitAsync(ct); // a racing push completed the group first
                continue;
            }

            var total = parts[0].TotalParts;
            var complete = parts.Count >= total;
            if (!complete)
                logger.LogWarning(
                    "Flushing PARTIAL inbound group {Group} on channel {ChannelId}: {Got}/{Total} parts after {Timeout}s — delivering what arrived",
                    group.GroupRef, group.ChannelId, parts.Count, total, timeout);

            var inbound = new InboundMessage
            {
                Id = Guid.CreateVersion7(),
                ChannelId = group.ChannelId,
                FromNumber = group.FromNumber,
                Body = string.Concat(parts.Select(p => p.BodyFragment)),
                Complete = complete,
                Payload = new Dictionary<string, string>
                {
                    ["source"] = "parts-sweep",
                    ["group_ref"] = group.GroupRef,
                    ["received_parts"] = parts.Count.ToString(),
                    ["total_parts"] = total.ToString(),
                },
            };
            db.InboundMessages.Add(inbound);
            var channel = await db.Channels.FindAsync(new object?[] { group.ChannelId }, ct);
            Egress.WebhookOutbox.EnqueueInbound(db, channel!, inbound, logger);
            db.InboundParts.RemoveRange(parts);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
    }
}
