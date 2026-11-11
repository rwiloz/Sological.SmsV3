using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sological.Sms.Core.Entities;
using Sological.Sms.Core.Upstream;
using Sological.Sms.Service.Data;
using Sological.Sms.Service.Upstream;

namespace Sological.Sms.Service.Ingress;

/// <summary>POST /ingress/mobilemessage/status (design §5.3): Mobile Message delivery receipts, one per part,
/// forwarded by the legacy app's router. Every receipt gets its delivery_events audit row; the message row is
/// the distilled truth (the same transitions as SMS Central). Correlation is exact: custom_ref =
/// `{our tag}:{message id}`, then MM's message_id once learned at submit. An unknown ref is audit-only and
/// never trips the breaker: the router already filtered by tag, so an unmatched receipt is a configuration
/// fault (a reset database, two instances on one tag), not foreign traffic on our account.</summary>
public sealed class MobileMessageStatusIngress(
    SmsDbContext db,
    IOptions<MobileMessageOptions> options,
    ILogger<MobileMessageStatusIngress> logger)
{
    public const string Provider = "mobilemessage";

    public async Task<IResult> HandleAsync(HttpContext ctx, CancellationToken ct)
    {
        var o = options.Value;
        var (p, refusal) = await MobileMessageWebhook.ReadAsync(ctx.Request, o, "status", logger, ct);
        if (refusal is not null) return refusal;

        var customRef = p.GetValueOrDefault("custom_ref", "");
        var mmId = p.GetValueOrDefault("message_id", "");
        var status = p.GetValueOrDefault("status", "");
        var partNumber = p.GetValueOrDefault("part_number", "");
        var totalParts = p.GetValueOrDefault("total_parts", "");

        // Idempotency before the ack: MM retries anything slower than 5 s and out of order. A part can
        // report more than once with different statuses (sent, then delivered), so status is in the key.
        if (mmId.Length > 0)
        {
            var duplicate = await db.Database.SqlQuery<bool>($"""
                SELECT EXISTS(
                    SELECT 1 FROM delivery_events
                    WHERE provider = {Provider}
                      AND payload->>'message_id' = {mmId}
                      AND COALESCE(payload->>'part_number', '') = {partNumber}
                      AND COALESCE(payload->>'status', '') = {status}) AS "Value"
                """).SingleAsync(ct);
            if (duplicate)
                return Results.Ok();
        }

        Message? message = null;
        if (MobileMessageWebhook.TryReadOwnRef(customRef, o, out var messageId, out var foreignTag))
            message = await db.Messages.SingleOrDefaultAsync(m => m.Id == messageId, ct);
        else if (foreignTag is not null)
            logger.LogWarning("Mobile Message receipt tagged '{Tag}' reached the '{OwnTag}' environment — check the legacy router's routes; audit row only",
                foreignTag, o.EnvironmentTag);
        if (message is null && mmId.Length > 0)
            message = await db.Messages.Where(m => m.UpstreamId == mmId)
                .OrderByDescending(m => m.RequestedAt).FirstOrDefaultAsync(ct);
        if (message is not null && string.IsNullOrEmpty(message.UpstreamId) && mmId.Length > 0)
            message.UpstreamId = IngressShared.Truncate(mmId, 64);

        var verdict = MapVerdict(status);
        db.DeliveryEvents.Add(new DeliveryEvent
        {
            MessageId = message?.Id,
            RawResult = partNumber.Length > 0 ? IngressShared.Truncate($"{partNumber}/{totalParts}", 16) : null,
            RawStatus = IngressShared.Truncate(status, 32),
            Provider = Provider,
            Payload = p,
        });

        if (message is null && foreignTag is null)
            logger.LogWarning("Mobile Message receipt for unknown ref '{Ref}' (message_id {MmId}) — audit row only", customRef, mmId);
        if (verdict is null)
            logger.LogWarning("Mobile Message receipt with unmapped status '{Status}' — audit row, no state change", status);

        if (message is not null && verdict is not null)
            await ApplyAsync(message, verdict.Value, partNumber, totalParts, ct);

        await db.SaveChangesAsync(ct);
        return Results.Ok();
    }

    /// <summary>MM status receipts say delivered or failed; `sent` (in MM's help centre) is interim.</summary>
    public static DeliveryVerdict? MapVerdict(string? status) => status?.ToLowerInvariant() switch
    {
        "delivered" => DeliveryVerdict.Delivered,
        "failed" => DeliveryVerdict.Failed,
        "sent" => DeliveryVerdict.Sent,
        _ => null,
    };

    private async Task ApplyAsync(
        Message message, DeliveryVerdict verdict, string partNumber, string totalParts, CancellationToken ct)
    {
        var part = int.TryParse(partNumber, out var n) ? n : 1;
        var total = int.TryParse(totalParts, out var t) && t > 0 ? t : Math.Max((int)message.Parts, 1);

        switch (verdict)
        {
            case DeliveryVerdict.Sent:
                return; // interim — audit row only

            case DeliveryVerdict.Delivered:
                await DeliveryTransitions.ApplyDeliveredAsync(db, message,
                    async () => total <= 1 || await CountDeliveredPartsAsync(message.Id, part) >= total,
                    logger, ct);
                return;

            default:
                await DeliveryTransitions.ApplyUndeliveredAsync(db, message, verdict, "failed",
                    total > 1 ? $"part {part} of {total} failed (Mobile Message receipt)" : "failed (Mobile Message receipt)",
                    logger, ct);
                return;
        }

        // Distinct parts with a delivered receipt, counting the part in flight (its row is not saved yet).
        async Task<int> CountDeliveredPartsAsync(Guid id, int currentPart)
        {
            var payloads = await db.DeliveryEvents
                .Where(e => e.MessageId == id && e.Provider == Provider)
                .Select(e => e.Payload)
                .ToListAsync(ct);
            var delivered = new HashSet<int> { currentPart };
            foreach (var payload in payloads)
            {
                if (MapVerdict(payload.GetValueOrDefault("status")) == DeliveryVerdict.Delivered &&
                    int.TryParse(payload.GetValueOrDefault("part_number"), out var number))
                    delivered.Add(number);
            }
            return delivered.Count;
        }
    }
}
