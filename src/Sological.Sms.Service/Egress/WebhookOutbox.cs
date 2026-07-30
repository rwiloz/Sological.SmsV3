using System.Text.Json;
using Sological.Sms.Core.Entities;
using Sological.Sms.Core.Webhooks;

namespace Sological.Sms.Service.Egress;

/// <summary>Enqueues customer webhook events (design §1: state changes and inbound flow
/// through webhook_outbox, never point-to-point). Rows are added to the SAME unit of work
/// as the state change — the outbox pattern; SaveChanges commits both or neither.
/// Channels without a webhook_url are simply not subscribed (no row, debug log only).
/// `duplicate` is deliberately NOT a webhook status — §6.2's vocabulary is
/// sent|delivered|failed|rejected|expired; duplicates stay visible on the status API.</summary>
public static class WebhookOutbox
{
    public static void EnqueueDelivery(Data.SmsDbContext db, Channel channel, Message message, ILogger logger)
    {
        if (string.IsNullOrEmpty(channel.WebhookUrl))
        {
            logger.LogDebug("Channel {ChannelKey} has no webhook_url — sms.delivery for {MessageId} not enqueued",
                channel.Key, message.Id);
            return;
        }

        var payload = new SmsDeliveryEvent(
            WebhookEventNames.SmsDelivery,
            message.Id,
            message.CustomerRef,
            message.ToNumber,
            message.Status.ToString().ToLowerInvariant(),
            message.ErrorCode,
            message.ErrorDetail,
            DateTimeOffset.UtcNow);

        db.WebhookOutbox.Add(new WebhookOutboxEntry
        {
            ChannelId = channel.Id,
            EventType = WebhookEventType.SmsDelivery,
            Payload = JsonSerializer.SerializeToDocument(payload, WebhookJson.Options),
        });
    }

    public static void EnqueueInbound(Data.SmsDbContext db, Channel channel, InboundMessage inbound, ILogger logger)
    {
        if (string.IsNullOrEmpty(channel.WebhookUrl))
        {
            logger.LogDebug("Channel {ChannelKey} has no webhook_url — sms.inbound {InboundId} not enqueued",
                channel.Key, inbound.Id);
            return;
        }

        var payload = new SmsInboundEvent(
            WebhookEventNames.SmsInbound,
            inbound.Id,
            inbound.FromNumber,
            inbound.ToNumber,
            inbound.Body,
            inbound.Complete,
            inbound.ReceivedAt,
            inbound.ReplyToMessage is { } m
                ? new SmsInboundReplyTo(m.Id, m.CustomerRef)
                : null);

        db.WebhookOutbox.Add(new WebhookOutboxEntry
        {
            ChannelId = channel.Id,
            EventType = WebhookEventType.SmsInbound,
            Payload = JsonSerializer.SerializeToDocument(payload, WebhookJson.Options),
        });
    }
}
