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
    ILogger<SmsCentralDeliveryIngress> logger)
{
    public async Task<IResult> HandleAsync(HttpContext ctx, CancellationToken ct)
    {
        var p = await IngressShared.ReadParamsAsync(ctx.Request);
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

        Message? message = null;
        if (Guid.TryParseExact(reference, "N", out var messageId))
            message = await db.Messages.SingleOrDefaultAsync(m => m.Id == messageId, ct);

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
                logger.LogWarning("Message {MessageId} {Status}: {Code} {Detail}",
                    message.Id, message.Status, message.ErrorCode, message.ErrorDetail);
                return;
        }
    }

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
