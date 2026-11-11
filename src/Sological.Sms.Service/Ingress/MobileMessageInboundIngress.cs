using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sological.Sms.Core.Entities;
using Sological.Sms.Service.Data;
using Sological.Sms.Service.Upstream;

namespace Sological.Sms.Service.Ingress;

/// <summary>POST /ingress/mobilemessage/inbound (design §5.3): replies forwarded by the legacy app's router.
/// MM reassembles multipart replies, so each webhook is one whole message. Routing precedence matches §5.2:
/// the original send (original_custom_ref with our tag, then original_message_id) → its channel; else the
/// dedicated number replied to (`to`) → its channel; else quarantine on the operator channel — never dropped.
/// An opt-out (type `unsubscribe`) is stored and forwarded like any reply: opt-out policy is the customer's
/// plane (§5.2); MM itself now blocks further sends to that number, which surface as `unsubscribed`.</summary>
public sealed class MobileMessageInboundIngress(
    SmsDbContext db,
    IOptions<MobileMessageOptions> options,
    ILogger<MobileMessageInboundIngress> logger)
{
    public async Task<IResult> HandleAsync(HttpContext ctx, CancellationToken ct)
    {
        var o = options.Value;
        var (p, refusal) = await MobileMessageWebhook.ReadAsync(ctx.Request, o, "inbound", logger, ct);
        if (refusal is not null) return refusal;

        var from = p.GetValueOrDefault("sender", "");
        var to = p.GetValueOrDefault("to", "");
        var text = p.GetValueOrDefault("message", "");
        var type = p.GetValueOrDefault("type", "");
        var originalRef = p.GetValueOrDefault("original_custom_ref", "");
        var originalId = p.GetValueOrDefault("original_message_id", "");

        if (from.Length == 0)
        {
            logger.LogWarning("Mobile Message inbound without a sender — audit-quarantining");
            from = "unknown";
        }

        // The inbound webhook carries no id of its own, and MM retries; the same reply arrives with the
        // same fields, so their digest is the dedupe handle.
        var upstreamId = DedupeId(p);
        if (await db.InboundMessages.AnyAsync(m => m.UpstreamId == upstreamId, ct))
            return Results.Ok();

        Message? replyTo = null;
        if (MobileMessageWebhook.TryReadOwnRef(originalRef, o, out var messageId, out var foreignTag))
            replyTo = await db.Messages.SingleOrDefaultAsync(m => m.Id == messageId, ct);
        else if (foreignTag is not null)
            logger.LogInformation("Mobile Message reply answers a '{Tag}' send — routing it by the number replied to", foreignTag);
        if (replyTo is null && originalId.Length > 0)
            replyTo = await db.Messages.Where(m => m.UpstreamId == originalId)
                .OrderByDescending(m => m.RequestedAt).FirstOrDefaultAsync(ct);

        long channelId;
        if (replyTo is not null)
        {
            channelId = replyTo.ChannelId;
        }
        else if (await InboundStore.ResolveByRecipientAsync(db, to, ct) is long byRecipient)
        {
            channelId = byRecipient;
        }
        else
        {
            channelId = await InboundStore.OperatorChannelIdAsync(db, ct);
            logger.LogWarning("Mobile Message inbound from ***{Last4} to '{Recipient}' unrouteable — quarantined on the operator channel",
                InboundStore.Last4(from), to);
        }

        if (string.Equals(type, "unsubscribe", StringComparison.OrdinalIgnoreCase))
            logger.LogInformation("Mobile Message opt-out from ***{Last4}: further sends to that number will be blocked upstream",
                InboundStore.Last4(from));

        await InboundStore.StoreAsync(db, channelId, from, to, text, upstreamId, replyTo, complete: true, p, logger, ct);
        return Results.Ok();
    }

    /// <summary>`mm:` + the first 40 hex of SHA-256 over the fields that identify one reply.</summary>
    public static string DedupeId(IReadOnlyDictionary<string, string> p)
    {
        var key = string.Join('\u001f',
            p.GetValueOrDefault("sender", ""), p.GetValueOrDefault("to", ""), p.GetValueOrDefault("received_at", ""),
            p.GetValueOrDefault("type", ""), p.GetValueOrDefault("message", ""));
        return "mm:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..40];
    }
}
