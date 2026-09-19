using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sological.Sms.Core.Entities;
using Sological.Sms.Core.Sms;
using Sological.Sms.Core.Upstream;
using Sological.Sms.Service.Data;
using Sological.Sms.Service.Upstream;

namespace Sological.Sms.Service.Ingress;

/// <summary>GET /ingress/smscentral/delivery (design §5.1). Every push gets an append-only
/// delivery_events audit row; the message row is the distilled truth. Unknown REFERENCE →
/// audit row only, never 500. Multipart: receipts arrive per part (UDH), delivered only
/// when all parts confirm; any part failing ⇒ failed.</summary>
public sealed class SmsCentralDeliveryIngress(
    SmsDbContext db,
    IOptions<SmsCentralOptions> smsCentral,
    IOptions<IngressOptions> ingressOptions,
    UpstreamBreakerService breaker,
    ILogger<SmsCentralDeliveryIngress> logger)
{
    public async Task<IResult> HandleAsync(HttpContext ctx, CancellationToken ct)
    {
        var p = await IngressShared.ReadParamsAsync(ctx.Request, logger);
        var failure = IngressShared.CheckVerification(ctx.Request, p, smsCentral.Value, ingressOptions.Value);
        if (failure is not null) return failure;
        IngressShared.RedactCredentials(p);
        p["_slverify"] = ctx.Request.Headers[IngressShared.VerifyHeaderName].Count > 0 ? "present" : "absent";

        var reference = p.GetValueOrDefault("REFERENCE", "");
        var upstreamId = IngressShared.FirstOf(p, "ID", "dtId", "drId", "reportId");
        var udh = p.GetValueOrDefault("UDH", "");
        var rawResult = p.GetValueOrDefault("RESULT", "");
        var rawStatus = p.GetValueOrDefault("STATUS", "");
        var rawDescription = IngressShared.FirstOf(p, "STATUSDESCRIPTION", "statusCode");

        // Idempotency BEFORE ack (design §5): their retry-on-silence is a duplication
        // engine; natural key is (ID, REFERENCE, part).
        if (upstreamId.Length > 0)
        {
            var duplicate = await db.Database.SqlQuery<bool>($"""
                SELECT EXISTS(
                    SELECT 1 FROM delivery_events
                    WHERE provider = 'smscentral'
                      AND COALESCE(payload->>'ID', payload->>'dtId', payload->>'drId', payload->>'reportId') = {upstreamId}
                      AND COALESCE(payload->>'REFERENCE', payload->>'reference', '') = {reference}
                      AND COALESCE(payload->>'UDH', '') = {udh}) AS "Value"
                """).SingleAsync(ct);
            if (duplicate)
                return IngressShared.Ack();
        }

        // Correlation chain (proven live 2026-07-30): REFERENCE uuid when present (the
        // legacy forward format) → the platform's message id (mtId, once learned) →
        // content match for a first receipt (the DR echoes mtContent + the handset;
        // wrapper sends never learn mtId up front, and $metadata does NOT round-trip our
        // REFERENCE — tested, the Velocity get() came back unresolved). On any match the
        // mtId is backfilled to upstream_id, which also unlocks exact reply correlation.
        var mtId = IngressShared.FirstOf(p, "mtId", "messageId", "MESSAGE_ID");
        Message? message = null;
        if (Guid.TryParseExact(reference, "N", out var messageId))
            message = await db.Messages.SingleOrDefaultAsync(m => m.Id == messageId, ct);
        if (message is null && mtId.Length > 0)
            message = await db.Messages.Where(m => m.UpstreamId == mtId)
                .OrderByDescending(m => m.RequestedAt).FirstOrDefaultAsync(ct);
        message ??= await MatchByContentAsync(p, ct);
        if (message is not null && string.IsNullOrEmpty(message.UpstreamId) && mtId.Length > 0)
            message.UpstreamId = IngressShared.Truncate(mtId, 64);

        var verdict = MapVerdict(rawResult, rawStatus);

        db.DeliveryEvents.Add(new DeliveryEvent
        {
            MessageId = message?.Id,
            RawResult = IngressShared.Truncate(rawResult, 16),
            RawStatus = IngressShared.Truncate(rawStatus, 32),
            RawDescription = IngressShared.Truncate(rawDescription, 500),
            Provider = "smscentral",
            Payload = p,
        });

        if (message is null && reference.Length > 0)
            logger.LogWarning("DLR for unknown REFERENCE {Reference} — audit row only", reference);
        if (verdict is null)
            logger.LogWarning("Unparseable DLR (RESULT={Result} STATUS={Status}) — audit row, no state change",
                rawResult, rawStatus);

        if (message is not null && verdict is not null)
            await ApplyVerdictAsync(message, verdict.Value, rawResult, rawStatus, rawDescription, udh, ct);

        await db.SaveChangesAsync(ct);

        // Uncorrelated DR = the sub-account processed a send that did NOT come from here.
        // Counted AFTER commit so this row is included in the breaker's window.
        if (message is null)
            await breaker.RecordUnmatchedAsync(ct);

        return IngressShared.Ack();
    }

    /// <summary>RESULT first, STATUS refines (design §5.1; precedence the legacy gateway
    /// proved out). 503 is documented as expiry → expired, the rest of 5xx/550 → failed.
    /// Their docs spell DELIVRD and DELIVERD in different places — accept both. The
    /// modern webhook engine sends word statuses instead (delivered/enroute/…), mapped
    /// here too so templated pushes need no RESULT at all.</summary>
    public static DeliveryVerdict? MapVerdict(string? rawResult, string? rawStatus)
    {
        if (rawResult == "1") return DeliveryVerdict.Delivered;
        if (rawResult is "0" or "536") return DeliveryVerdict.Sent;
        if (int.TryParse(rawResult, out var code))
        {
            if (code == 503) return DeliveryVerdict.Expired;
            if (code is (>= 500 and <= 535) or 550) return DeliveryVerdict.Failed;
        }

        return rawStatus?.ToUpperInvariant() switch
        {
            "DELIVRD" or "DELIVERD" or "DELIVERED" => DeliveryVerdict.Delivered,
            "BUFFRED" or "ENROUTE" or "SUBMITTED" => DeliveryVerdict.Sent,
            "FAILED" => DeliveryVerdict.Failed,
            "REJECTED" => DeliveryVerdict.Rejected,
            "EXPIRED" => DeliveryVerdict.Expired,
            _ => null,
        };
    }

    private async Task ApplyVerdictAsync(
        Message message, DeliveryVerdict verdict,
        string rawResult, string rawStatus, string rawDescription, string udh, CancellationToken ct)
    {
        switch (verdict)
        {
            case DeliveryVerdict.Sent:
                return; // interim signal — audit row only

            case DeliveryVerdict.Delivered:
                if (message.Status == MessageStatus.Delivered)
                    return;
                if (message.Status is MessageStatus.Failed or MessageStatus.Expired or MessageStatus.Rejected)
                {
                    // First terminal verdict wins; partial delivery stays visible in delivery_events.
                    logger.LogWarning("Delivered receipt for message {MessageId} already {Status} — audit only",
                        message.Id, message.Status);
                    return;
                }

                var thisPart = UdhParser.ParseConcat(udh);
                if (message.Parts > 1 && thisPart is not null &&
                    await CountDeliveredPartsAsync(message, thisPart.PartNo, ct) < message.Parts)
                {
                    return; // this part confirmed; waiting on the rest
                }

                message.Status = MessageStatus.Delivered;
                message.DeliveredAt = DateTimeOffset.UtcNow;
                await EnqueueDeliveryEventAsync(message, ct);
                logger.LogInformation("Message {MessageId} delivered", message.Id);
                return;

            case DeliveryVerdict.Failed or DeliveryVerdict.Expired or DeliveryVerdict.Rejected:
                if (message.Status == MessageStatus.Delivered)
                {
                    logger.LogWarning("{Verdict} receipt for message {MessageId} already delivered — audit only",
                        verdict, message.Id);
                    return;
                }
                if (message.Status is MessageStatus.Failed or MessageStatus.Expired or MessageStatus.Rejected)
                    return;

                message.Status = verdict switch
                {
                    DeliveryVerdict.Expired => MessageStatus.Expired,
                    DeliveryVerdict.Rejected => MessageStatus.Rejected,
                    _ => MessageStatus.Failed,
                };
                message.ErrorCode = IngressShared.Truncate(rawResult, 32) ?? IngressShared.Truncate(rawStatus, 32);
                message.ErrorDetail = rawDescription.Length > 0 ? rawDescription : rawStatus;
                message.FailedAt = DateTimeOffset.UtcNow;
                await EnqueueDeliveryEventAsync(message, ct);
                logger.LogWarning("Message {MessageId} {Status}: {Code} {Detail}",
                    message.Id, message.Status, message.ErrorCode, message.ErrorDetail);
                return;
        }
    }

    private async Task EnqueueDeliveryEventAsync(Message message, CancellationToken ct)
    {
        var channel = await db.Channels.FindAsync(new object?[] { message.ChannelId }, ct);
        Egress.WebhookOutbox.EnqueueDelivery(db, channel!, message, logger);
    }

    /// <summary>First-receipt fallback for wrapper-era sends: the DR echoes the original
    /// text (mtContent) and the handset (sourceAddress). Match only when exactly ONE
    /// recent message fits — ambiguity stays audit-only, loudly.</summary>
    private async Task<Message?> MatchByContentAsync(Dictionary<string, string> p, CancellationToken ct)
    {
        // Bodies are stored folded (GsmFold at the send), and the upstream echoes what it sent — with any curly
        // apostrophe it transliterated; folding the echo the same way makes the two comparable.
        var content = GsmFold.Apply(p.GetValueOrDefault("mtContent", ""));
        var handset = IngressShared.FirstOf(p, "sourceAddress", "RECIPIENT");
        if (content.Length == 0 || handset.Length == 0)
            return null;

        var to = "+" + IngressShared.NormalizeNumber(handset);
        var since = DateTimeOffset.UtcNow.AddHours(-72);
        var candidates = await db.Messages
            // Only a message still awaiting its first receipt: one that learned its mtId is served by the mtId rung, and
            // a repeated identical send inside the window would otherwise read as ambiguous once the first had matched.
            .Where(m => m.ToNumber == to && m.Body == content && m.RequestedAt >= since && m.UpstreamId == null)
            .OrderByDescending(m => m.RequestedAt)
            .Take(2)
            .ToListAsync(ct);

        if (candidates.Count == 1)
            return candidates[0];
        if (candidates.Count > 1)
            logger.LogWarning("DLR content-match ambiguous for ***{Last4} — audit row only", Last4(handset));
        else
        {
            // Name the class, never the text: a character the upstream transliterated before sending is the next
            // reason an echo will not match (the curly apostrophe was the first).
            var outside = SmsParts.NonGsm7CodePoints(content);
            if (outside.Count > 0)
                logger.LogWarning("DLR content-match found no candidate for ***{Last4}; the echo carries {CodePoints} outside GSM-7",
                    Last4(handset), string.Join(' ', outside));
        }
        return null;
    }

    private static string Last4(string number) => number.Length >= 4 ? number[^4..] : number;

    /// <summary>Distinct parts with a delivered receipt, counting the part in-flight
    /// (its event row isn't saved yet). Per-message event volume is tiny — parsing in
    /// memory beats denormalizing part columns.</summary>
    private async Task<int> CountDeliveredPartsAsync(Message message, int currentPart, CancellationToken ct)
    {
        var events = await db.DeliveryEvents
            .Where(e => e.MessageId == message.Id)
            .Select(e => e.Payload)
            .ToListAsync(ct);

        var delivered = new HashSet<int> { currentPart };
        foreach (var payload in events)
        {
            if (MapVerdict(payload.GetValueOrDefault("RESULT"), payload.GetValueOrDefault("STATUS")) != DeliveryVerdict.Delivered)
                continue;
            var part = UdhParser.ParseConcat(payload.GetValueOrDefault("UDH"));
            if (part is not null)
                delivered.Add(part.PartNo);
        }
        return delivered.Count;
    }
}
