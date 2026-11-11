using Microsoft.EntityFrameworkCore;
using Sological.Sms.Core.Entities;
using Sological.Sms.Service.Data;

namespace Sological.Sms.Service.Ingress;

/// <summary>What every inbound receiver shares once it knows who a reply belongs to (design §5.2): the
/// channel a dedicated number belongs to, the operator quarantine channel for the unrouteable, and storing
/// the reply together with its sms.inbound customer event (outbox: both commit or neither).</summary>
public static class InboundStore
{
    /// <summary>The channel whose originator is <paramref name="recipient"/> (a dedicated number), if any.</summary>
    public static async Task<long?> ResolveByRecipientAsync(SmsDbContext db, string recipient, CancellationToken ct)
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

    public static async Task<long> OperatorChannelIdAsync(SmsDbContext db, CancellationToken ct)
        => (await db.Channels.SingleAsync(c => c.Key == SystemChannels.OperatorKey, ct)).Id;

    public static async Task StoreAsync(
        SmsDbContext db, long channelId, string from, string to, string body, string upstreamId,
        Message? replyTo, bool complete, Dictionary<string, string> payload, ILogger logger, CancellationToken ct)
    {
        var inbound = new InboundMessage
        {
            Id = Guid.CreateVersion7(),
            ChannelId = channelId,
            FromNumber = from,
            ToNumber = IngressShared.Truncate(to, 20),
            Body = body,
            UpstreamId = IngressShared.Truncate(upstreamId, 64),
            ReplyToMessageId = replyTo?.Id,
            ReplyToMessage = replyTo,
            Complete = complete,
            Payload = payload,
        };
        db.InboundMessages.Add(inbound);

        // Outbox pattern (S4): the customer event commits with the inbound row or not at all.
        var channel = await db.Channels.FindAsync(new object?[] { channelId }, ct);
        Egress.WebhookOutbox.EnqueueInbound(db, channel!, inbound, logger);

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Inbound stored for channel {ChannelId} from ***{Last4} (complete={Complete}, replyTo={ReplyTo})",
            channelId, Last4(from), complete, replyTo?.Id);
    }

    public static string Last4(string number) => number.Length >= 4 ? number[^4..] : number;
}
