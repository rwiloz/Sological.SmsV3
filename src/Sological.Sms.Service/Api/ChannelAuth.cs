using Microsoft.EntityFrameworkCore;
using Sological.Sms.Core.Entities;
using Sological.Sms.Core.Sms;
using Sological.Sms.Service.Data;

namespace Sological.Sms.Service.Api;

/// <summary>Per-channel API-key auth — the ONLY customer auth mechanism (design §6.1).
/// Both key slots are live so rotation never drops traffic.</summary>
public static class ChannelAuth
{
    public static async Task<(Channel? Channel, IResult? Failure)> ResolveAsync(
        HttpContext ctx, SmsDbContext db, bool forSend, CancellationToken ct)
    {
        if (!ctx.Request.Headers.TryGetValue("X-Api-Key", out var raw) || string.IsNullOrWhiteSpace(raw))
            return (null, Results.Json(new ApiError("missing_api_key", "The X-Api-Key header is required."), statusCode: StatusCodes.Status401Unauthorized));

        var hash = ApiKeyHasher.Sha256Hex(raw.ToString());
        var channel = await db.Channels.Include(c => c.Customer)
            .SingleOrDefaultAsync(c => c.ApiKey1Hash == hash || c.ApiKey2Hash == hash, ct);

        if (channel is null)
            return (null, Results.Json(new ApiError("invalid_api_key", "No channel matches this API key."), statusCode: StatusCodes.Status401Unauthorized));

        if (channel.Customer!.Status != CustomerStatus.Active)
            return (null, Results.Json(new ApiError("customer_disabled", "This customer account is disabled."), statusCode: StatusCodes.Status403Forbidden));

        if (channel.Status == ChannelStatus.Retired)
            return (null, Results.Json(new ApiError("channel_retired", "This channel has been retired."), statusCode: StatusCodes.Status403Forbidden));

        if (forSend && channel.Status == ChannelStatus.Paused)
            return (null, Results.Json(new ApiError("channel_paused", "This channel is paused and cannot send."), statusCode: StatusCodes.Status403Forbidden));

        return (channel, null);
    }
}
