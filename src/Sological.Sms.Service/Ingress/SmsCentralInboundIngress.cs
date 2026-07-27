using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sological.Sms.Core.Entities;
using Sological.Sms.Core.Sms;
using Sological.Sms.Service.Data;
using Sological.Sms.Service.Upstream;

namespace Sological.Sms.Service.Ingress;

/// <summary>GET /ingress/smscentral/inbound (design §5.2). Routing precedence:
/// REFERENCE → message → channel (authoritative for replies) · RECIPIENT = a channel's
/// dedicated number · else quarantine on the operator channel — never dropped, never 500.
/// Multipart arrives as UDH+BINARY parts; reassembly buffers in inbound_parts.</summary>
public sealed class SmsCentralInboundIngress(
    SmsDbContext db,
    IOptions<SmsCentralOptions> smsCentral,
    IOptions<IngressOptions> ingressOptions,
    ILogger<SmsCentralInboundIngress> logger)
{
    public async Task<IResult> HandleAsync(HttpContext ctx, CancellationToken ct)
    {
        var payload = await IngressShared.ReadParamsAsync(ctx.Request);
        var failure = IngressShared.CheckVerification(ctx.Request, payload, smsCentral.Value, ingressOptions.Value);
        if (failure is not null) return failure;
        IngressShared.RedactCredentials(payload);
        payload["_slverify"] = ctx.Request.Headers[IngressShared.VerifyHeaderName].Count > 0 ? "present" : "absent";

        var from = payload.GetValueOrDefault("ORIGINATOR", "");
        var to = payload.GetValueOrDefault("RECIPIENT", "");
        var reference = payload.GetValueOrDefault("REFERENCE", "");
        var text = payload.GetValueOrDefault("MESSAGE_TEXT", "");
        var udh = payload.GetValueOrDefault("UDH", "");
        var binary = payload.GetValueOrDefault("BINARY", "");
        var upstreamId = payload.GetValueOrDefault("ID", "");
        int? dcs = int.TryParse(payload.GetValueOrDefault("DCS", ""), out var d) ? d : null;

        if (from.Length == 0)
        {
            logger.LogWarning("Inbound push without ORIGINATOR — audit-quarantining");
            from = "unknown";
        }

        // Idempotency before ack: their ID is documented as exactly this dedupe handle.
        if (upstreamId.Length > 0 && await db.InboundMessages.AnyAsync(m => m.UpstreamId == upstreamId, ct))
            return IngressShared.Ack();

        // Routing (design §5.2): REFERENCE wins — our uuid round-trips on replies and is
        // authoritative; captures show REFERENCE can also carry foreign junk, so parse
        // defensively and fall through.
        Message? replyTo = null;
        if (Guid.TryParseExact(reference, "N", out var messageId))
            replyTo = await db.Messages.SingleOrDefaultAsync(m => m.Id == messageId, ct);

        long channelId;
        if (replyTo is not null)
        {
            channelId = replyTo.ChannelId;
        }
        else
        {
            var byRecipient = await ResolveByRecipientAsync(to, ct);
            if (byRecipient is not null)
            {
                channelId = byRecipient.Value;
            }
            else
            {
                channelId = await OperatorChannelIdAsync(ct);
                logger.LogWarning("Inbound from ***{Last4} to '{Recipient}' unrouteable — quarantined on the operator channel",
                    Last4(from), to);
            }
        }

        // Body: MESSAGE_TEXT when present; else UDH+BINARY (multipart / non-GSM).
        if (text.Length > 0)
        {
            await StoreInboundAsync(channelId, from, to, text, upstreamId, replyTo?.Id, complete: true, payload, ct);
            return IngressShared.Ack();
        }

        var fragment = SmsBinaryDecoder.Decode(binary, dcs);
        if (fragment is null)
        {
            logger.LogWarning("Inbound push with undecodable BINARY (DCS={Dcs}) — storing raw hex", dcs);
            fragment = binary;
        }

        var concat = UdhParser.ParseConcat(udh);
        if (concat is null)
        {
            // No usable concat header — treat the decoded fragment as the whole message.
            await StoreInboundAsync(channelId, from, to, fragment, upstreamId, replyTo?.Id, complete: true, payload, ct);
            return IngressShared.Ack();
        }

        await StorePartAsync(channelId, from, to, fragment, concat, dcs, upstreamId, replyTo?.Id, payload, ct);
        return IngressShared.Ack();
    }

    private async Task<long?> ResolveByRecipientAsync(string recipient, CancellationToken ct)
    {
        if (recipient.Length == 0) return null;
        var target = IngressShared.NormalizeNumber(recipient);
        if (target.Length == 0) return null;

        // Channel counts stay tiny — normalize in memory rather than teaching SQL our rules.
        var channels = await db.Channels
            .Where(c => c.Status != ChannelStatus.Retired)
            .Select(c => new { c.Id, c.Originator })
            .ToListAsync(ct);
        return channels.FirstOrDefault(c => IngressShared.NormalizeNumber(c.Originator) == target)?.Id;
    }

    private long? _operatorChannelId;

    private async Task<long> OperatorChannelIdAsync(CancellationToken ct)
    {
        _operatorChannelId ??= (await db.Channels.SingleAsync(c => c.Key == SystemChannels.OperatorKey, ct)).Id;
        return _operatorChannelId.Value;
    }

    private async Task StoreInboundAsync(
        long channelId, string from, string to, string body, string upstreamId,
        Guid? replyToMessageId, bool complete, Dictionary<string, string> payload, CancellationToken ct)
    {
        db.InboundMessages.Add(new InboundMessage
        {
            Id = Guid.CreateVersion7(),
            ChannelId = channelId,
            FromNumber = from,
            ToNumber = IngressShared.Truncate(to, 20),
            Body = body,
            UpstreamId = IngressShared.Truncate(upstreamId, 64),
            ReplyToMessageId = replyToMessageId,
            Complete = complete,
            Payload = payload,
        });
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Inbound stored for channel {ChannelId} from ***{Last4} (complete={Complete}, replyTo={ReplyTo})",
            channelId, Last4(from), complete, replyToMessageId);
    }

    /// <summary>Buffers one part; assembles the group when the last part lands. The
    /// pg advisory xact lock serializes concurrent finishers of the same group — plain
    /// row locks can't (the racing rows don't exist yet).</summary>
    private async Task StorePartAsync(
        long channelId, string from, string to, string fragment, UdhParser.ConcatInfo concat,
        int? dcs, string upstreamId, Guid? replyToMessageId, Dictionary<string, string> payload, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({channelId + "|" + from + "|" + concat.GroupRef}))", ct);

        db.InboundParts.Add(new InboundPart
        {
            ChannelId = channelId,
            FromNumber = from,
            GroupRef = concat.GroupRef,
            PartNo = (short)concat.PartNo,
            TotalParts = (short)concat.TotalParts,
            BodyFragment = fragment,
            Dcs = (short?)dcs,
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Same part re-pushed (PK collision) — duplicate, ack and move on.
            await tx.RollbackAsync(ct);
            logger.LogInformation("Duplicate inbound part {Part}/{Total} group {Group} — ignored",
                concat.PartNo, concat.TotalParts, concat.GroupRef);
            return;
        }

        var parts = await db.InboundParts
            .Where(p => p.ChannelId == channelId && p.FromNumber == from && p.GroupRef == concat.GroupRef)
            .OrderBy(p => p.PartNo)
            .ToListAsync(ct);

        if (parts.Count >= concat.TotalParts)
        {
            var body = string.Concat(parts.Select(p => p.BodyFragment));
            await StoreInboundAsync(channelId, from, to, body, upstreamId, replyToMessageId, complete: true, payload, ct);
            db.InboundParts.RemoveRange(parts);
            await db.SaveChangesAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    private static string Last4(string number) => number.Length >= 4 ? number[^4..] : number;
}
