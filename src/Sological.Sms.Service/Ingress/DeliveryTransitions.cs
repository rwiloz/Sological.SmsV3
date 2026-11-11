using Sological.Sms.Core.Entities;
using Sological.Sms.Core.Upstream;
using Sological.Sms.Service.Data;

namespace Sological.Sms.Service.Ingress;

/// <summary>The message-row transitions every delivery receiver applies (design §5.1), whatever the
/// provider: first terminal verdict wins, delivered waits for every part, and each transition enqueues the
/// customer's sms.delivery event in the same unit of work. The caller saves.</summary>
public static class DeliveryTransitions
{
    /// <summary>Delivered once <paramref name="allPartsDelivered"/> says every part has confirmed.</summary>
    public static async Task ApplyDeliveredAsync(
        SmsDbContext db, Message message, Func<Task<bool>> allPartsDelivered, ILogger logger, CancellationToken ct)
    {
        if (message.Status == MessageStatus.Delivered)
            return;
        if (message.Status is MessageStatus.Failed or MessageStatus.Expired or MessageStatus.Rejected)
        {
            // First terminal verdict wins; partial delivery stays visible in delivery_events.
            logger.LogWarning("Delivered receipt for message {MessageId} already {Status} — audit only",
                message.Id, message.Status);
            return;
        }
        if (!await allPartsDelivered())
            return; // this part confirmed; waiting on the rest

        message.Status = MessageStatus.Delivered;
        message.DeliveredAt = DateTimeOffset.UtcNow;
        await EnqueueAsync(db, message, logger, ct);
        logger.LogInformation("Message {MessageId} delivered", message.Id);
    }

    /// <summary>Failed, expired or rejected, unless a terminal verdict already landed.</summary>
    public static async Task ApplyUndeliveredAsync(
        SmsDbContext db, Message message, DeliveryVerdict verdict, string? errorCode, string? errorDetail,
        ILogger logger, CancellationToken ct)
    {
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
        message.ErrorCode = errorCode;
        message.ErrorDetail = errorDetail;
        message.FailedAt = DateTimeOffset.UtcNow;
        await EnqueueAsync(db, message, logger, ct);
        logger.LogWarning("Message {MessageId} {Status}: {Code} {Detail}",
            message.Id, message.Status, message.ErrorCode, message.ErrorDetail);
    }

    private static async Task EnqueueAsync(SmsDbContext db, Message message, ILogger logger, CancellationToken ct)
    {
        var channel = await db.Channels.FindAsync(new object?[] { message.ChannelId }, ct);
        Egress.WebhookOutbox.EnqueueDelivery(db, channel!, message, logger);
    }
}
