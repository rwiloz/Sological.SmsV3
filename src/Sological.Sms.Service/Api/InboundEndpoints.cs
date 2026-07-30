using Microsoft.EntityFrameworkCore;
using Sological.Sms.Service.Data;

namespace Sological.Sms.Service.Api;

public sealed record InboundListItem(
    Guid InboundId,
    string From,
    string? To,
    string Body,
    bool Complete,
    DateTimeOffset ReceivedAt,
    Guid? ReplyToMessageId,
    DateTimeOffset? DeliveredToCustomerAt);

/// <summary>Poll-parity endpoint (design S4 surface): the same inbound stream the webhooks
/// push, readable for debugging/catch-up. Channel-scoped like everything else.</summary>
public static class InboundEndpoints
{
    public static void MapInboundApi(this WebApplication app)
    {
        app.MapGet("/api/v1/inbound", async (
            DateTimeOffset? since, HttpContext ctx, SmsDbContext db, CancellationToken ct) =>
        {
            var (channel, failure) = await ChannelAuth.ResolveAsync(ctx, db, forSend: false, ct);
            if (failure is not null) return failure;

            var from = since ?? DateTimeOffset.UtcNow.AddDays(-1);
            var items = await db.InboundMessages
                .Where(m => m.ChannelId == channel!.Id && m.ReceivedAt >= from)
                .OrderBy(m => m.ReceivedAt)
                .Take(100)
                .Select(m => new InboundListItem(
                    m.Id, m.FromNumber, m.ToNumber, m.Body, m.Complete,
                    m.ReceivedAt, m.ReplyToMessageId, m.DeliveredToCustomerAt))
                .ToListAsync(ct);
            return Results.Ok(items);
        });
    }
}
