using Microsoft.EntityFrameworkCore;
using Sological.Sms.Core.Entities;
using Sological.Sms.Core.Sms;
using Sological.Sms.Service.Data;

namespace Sological.Sms.Service.Api;

public sealed record SendMessageRequest(string? To, string? Body, string? Reference, string? Originator);

public sealed record SendMessageResponse(Guid MessageId, short Parts, MessageStatus Status);

public sealed record MessageStatusResponse(
    Guid MessageId,
    string? Reference,
    string To,
    string Originator,
    short Parts,
    MessageStatus Status,
    string? ErrorCode,
    string? ErrorDetail,
    DateTimeOffset RequestedAt,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? DeliveredAt,
    DateTimeOffset? FailedAt);

/// <summary>The customer send surface (design §6.1). Guards (duplicate/recipient/
/// originator-whitelist) run in the DISPATCH worker, not here — submissions are accepted
/// and honestly flagged, exactly like the legacy processor did.</summary>
public static class MessagesEndpoints
{
    public static void MapMessagesApi(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/messages");
        group.MapPost("", SendAsync)
            .RequireRateLimiting(RateLimitOptions.SendPolicy); // token bucket per API key
        group.MapGet("/{id:guid}", GetAsync);
    }

    private static async Task<IResult> SendAsync(
        SendMessageRequest request, HttpContext ctx, SmsDbContext db, CancellationToken ct)
    {
        var (channel, failure) = await ChannelAuth.ResolveAsync(ctx, db, forSend: true, ct);
        if (failure is not null) return failure;

        if (string.IsNullOrWhiteSpace(request.To))
            return BadRequest("invalid_request", "'to' is required.");
        if (string.IsNullOrEmpty(request.Body))
            return BadRequest("invalid_request", "'body' is required.");
        if (request.Reference is { Length: > 64 })
            return BadRequest("reference_too_long", "'reference' must be 64 characters or fewer.");

        // One channel per sender ID (Ray, 2026-07-27): 'originator' is accepted only as a
        // confirmation of the channel's configured sender — never a selector.
        if (request.Originator is not null && request.Originator != channel!.Originator)
            return BadRequest("originator_mismatch",
                $"This channel sends as '{channel!.Originator}'. One channel per sender ID — use the channel provisioned for that sender.");

        var parts = SmsParts.Count(request.Body);
        if (parts > 7)
            return BadRequest("message_too_long", "Message exceeds 7 SMS parts (the upstream limit).");

        if (request.Reference is not null &&
            await db.Messages.AnyAsync(m => m.ChannelId == channel!.Id && m.CustomerRef == request.Reference, ct))
        {
            // Per-channel ref uniqueness is a NEW-surface rule only (S5 legacy accepts
            // duplicates on the same table), so it is enforced here, not by the schema.
            // The dispatch duplicate guard is the backstop for the race window.
            return Results.Json(new ApiError("duplicate_reference",
                $"reference '{request.Reference}' was already used on this channel."), statusCode: StatusCodes.Status409Conflict);
        }

        var message = new Message
        {
            Id = Guid.CreateVersion7(),
            ChannelId = channel!.Id,
            CustomerRef = request.Reference,
            ToNumber = request.To.Trim(),
            OriginatorUsed = channel.Originator,
            Body = request.Body,
            Parts = parts,
            Upstream = channel.Upstream,
        };
        db.Messages.Add(message);
        await db.SaveChangesAsync(ct);

        return Results.Accepted($"/api/v1/messages/{message.Id}",
            new SendMessageResponse(message.Id, message.Parts, message.Status));
    }

    private static async Task<IResult> GetAsync(
        Guid id, HttpContext ctx, SmsDbContext db, CancellationToken ct)
    {
        var (channel, failure) = await ChannelAuth.ResolveAsync(ctx, db, forSend: false, ct);
        if (failure is not null) return failure;

        // Row-level segregation: a channel can only ever see its own messages.
        var message = await db.Messages
            .SingleOrDefaultAsync(m => m.Id == id && m.ChannelId == channel!.Id, ct);
        if (message is null)
            return Results.Json(new ApiError("message_not_found", "No such message on this channel."), statusCode: StatusCodes.Status404NotFound);

        return Results.Ok(new MessageStatusResponse(
            message.Id, message.CustomerRef, message.ToNumber, message.OriginatorUsed,
            message.Parts, message.Status, message.ErrorCode, message.ErrorDetail,
            message.RequestedAt, message.SubmittedAt, message.DeliveredAt, message.FailedAt));
    }

    private static IResult BadRequest(string error, string message)
        => Results.Json(new ApiError(error, message), statusCode: StatusCodes.Status400BadRequest);
}
