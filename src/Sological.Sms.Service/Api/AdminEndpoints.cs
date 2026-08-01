using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sological.Sms.Core.Entities;
using Sological.Sms.Core.Sms;
using Sological.Sms.Service.Data;
using Sological.Sms.Service.Data.Configuration;

namespace Sological.Sms.Service.Api;

public sealed class AdminOptions
{
    public const string SectionName = "SologicalSms:Admin";

    /// <summary>Vault-held operator key (SologicalSms--Admin--ApiKey). Unset = every admin
    /// route answers 503 admin_disabled (fail closed).</summary>
    public string? ApiKey { get; set; }

    /// <summary>Hosts the admin surface answers on — the ACA-internal name and localhost.
    /// Any other Host (i.e. the public hostname) gets a bare 404: the admin API does not
    /// exist on the internet (spec §1). CSV string (config binder concatenates arrays).</summary>
    public string AllowedHosts { get; set; } = "ca-sologicalsms,localhost";

    public string[] ParseAllowedHosts()
        => AllowedHosts.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>The S7 management API (spec §2): operator plane over customers, channels,
/// whitelist, messages, outbox, breaker, tuning and usage. v3 stays the authority — the
/// AIW admin UI talks to this through a server-side proxy, never directly.</summary>
public static class AdminEndpoints
{
    public static void MapAdminApi(this WebApplication app)
    {
        var group = app.MapGroup("/api/admin");
        group.AddEndpointFilter(AuthFilterAsync);

        group.MapGet("/overview", OverviewAsync);

        group.MapGet("/customers", ListCustomersAsync);
        group.MapPost("/customers", CreateCustomerAsync);
        group.MapPatch("/customers/{id:long}", PatchCustomerAsync);

        group.MapGet("/channels", ListChannelsAsync);
        group.MapPost("/channels", CreateChannelAsync);
        group.MapPatch("/channels/{key}", PatchChannelAsync);
        group.MapPost("/channels/{key}/rotate-key", RotateChannelKeyAsync);

        group.MapGet("/originators", ListOriginatorsAsync);
        group.MapPost("/originators", AddOriginatorAsync);
        group.MapDelete("/originators/{originator}", DeleteOriginatorAsync);

        group.MapGet("/messages", ListMessagesAsync);
        group.MapGet("/messages/{id:guid}", GetMessageAsync);
        group.MapGet("/inbound", ListInboundAsync);

        group.MapGet("/outbox", ListOutboxAsync);
        group.MapPost("/outbox/{id:long}/retry", RetryOutboxAsync);

        group.MapGet("/breakers", ListBreakersAsync);
        group.MapPost("/breakers/{id:long}/re-arm", ReArmBreakerAsync);

        group.MapGet("/settings", ListSettingsAsync);
        group.MapPut("/settings/{**key}", PutSettingAsync);
        group.MapDelete("/settings/{**key}", DeleteSettingAsync);

        group.MapGet("/usage", UsageAsync);
        group.MapPost("/service/restart", RestartAsync);
    }

    // ── Auth (spec §1): internal host only → vault key, constant time, fail closed ──

    private static async ValueTask<object?> AuthFilterAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var options = ctx.HttpContext.RequestServices.GetRequiredService<IOptionsMonitor<AdminOptions>>().CurrentValue;

        // Wrong host = the surface does not exist (bare 404, indistinguishable from no route).
        var host = ctx.HttpContext.Request.Host.Host;
        if (!options.ParseAllowedHosts().Any(h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase)))
            return Results.NotFound();

        if (string.IsNullOrEmpty(options.ApiKey))
            return Results.Json(new ApiError("admin_disabled", "No admin key is configured — the management API is disabled."),
                statusCode: StatusCodes.Status503ServiceUnavailable);

        var presented = ctx.HttpContext.Request.Headers["X-Admin-Key"].FirstOrDefault() ?? "";
        if (!FixedTimeEquals(presented, options.ApiKey))
            return Results.Json(new ApiError("invalid_admin_key", "The X-Admin-Key header is missing or wrong."),
                statusCode: StatusCodes.Status401Unauthorized);

        return await next(ctx);
    }

    private static bool FixedTimeEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(a)),
            SHA256.HashData(Encoding.UTF8.GetBytes(b)));

    // ── Overview ────────────────────────────────────────────────────────────────

    private static async Task<IResult> OverviewAsync(SmsDbContext db, CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow.AddHours(-24);
        var counts = await db.Messages.Where(m => m.RequestedAt >= since)
            .GroupBy(m => m.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var inbound24h = await db.InboundMessages.CountAsync(i => i.ReceivedAt >= since, ct);
        var outbox = await db.WebhookOutbox.GroupBy(o => o.State)
            .Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var breaker = await db.UpstreamBreakers.Where(b => b.ReArmedAt == null)
            .OrderByDescending(b => b.Id).FirstOrDefaultAsync(ct);
        var channels = await db.Channels.OrderBy(c => c.Key)
            .Select(c => new { c.Key, c.Status, c.Originator }).ToListAsync(ct);

        return Results.Ok(new
        {
            healthy = true,
            counts24h = counts.ToDictionary(x => x.Key.ToString().ToLowerInvariant(), x => x.Count),
            inbound24h,
            outbox = new
            {
                pending = outbox.FirstOrDefault(o => o.Key == WebhookOutboxState.Pending)?.Count ?? 0,
                dead = outbox.FirstOrDefault(o => o.Key == WebhookOutboxState.Dead)?.Count ?? 0,
            },
            breaker = breaker is null
                ? new { active = false, trippedAt = (DateTimeOffset?)null, unmatchedCount = (int?)null }
                : new { active = true, trippedAt = (DateTimeOffset?)breaker.TrippedAt, unmatchedCount = (int?)breaker.UnmatchedCount },
            channels,
        });
    }

    // ── Customers ───────────────────────────────────────────────────────────────

    private static async Task<IResult> ListCustomersAsync(SmsDbContext db, CancellationToken ct)
        => Results.Ok(await db.Customers.OrderBy(c => c.Code)
            .Select(c => new { c.Id, c.Code, c.Name, c.Status, ChannelCount = c.Channels.Count, c.CreatedAt })
            .ToListAsync(ct));

    private sealed record CreateCustomerRequest(string? Code, string? Name);

    private static async Task<IResult> CreateCustomerAsync(CreateCustomerRequest request, SmsDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.Name))
            return Bad("invalid_request", "'code' and 'name' are required.");
        if (await db.Customers.AnyAsync(c => c.Code == request.Code, ct))
            return Conflict("customer_exists", $"customer '{request.Code}' already exists.");
        var customer = new Customer { Code = request.Code.Trim(), Name = request.Name.Trim() };
        db.Customers.Add(customer);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/admin/customers/{customer.Id}", new { customer.Id, customer.Code, customer.Name, customer.Status });
    }

    private static async Task<IResult> PatchCustomerAsync(long id, JsonDocument body, SmsDbContext db, CancellationToken ct)
    {
        var customer = await db.Customers.FindAsync([id], ct);
        if (customer is null) return NotFound("customer_not_found", "No such customer.");
        if (body.RootElement.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
            customer.Name = name.GetString()!;
        if (body.RootElement.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String)
        {
            if (!Enum.TryParse<CustomerStatus>(status.GetString(), ignoreCase: true, out var parsed))
                return Bad("invalid_status", "status must be 'active' or 'disabled'.");
            customer.Status = parsed;
        }
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { customer.Id, customer.Code, customer.Name, customer.Status });
    }

    // ── Channels ────────────────────────────────────────────────────────────────

    private static async Task<IResult> ListChannelsAsync(SmsDbContext db, CancellationToken ct)
        => Results.Ok(await db.Channels.OrderBy(c => c.Key).Select(c => new
        {
            c.Id, c.Key, c.Description, c.Originator, c.Status, c.Upstream,
            CustomerCode = c.Customer!.Code,
            c.WebhookUrl, c.WebhookSecretName,
            c.DailyPartLimit, c.AllowedRecipients, c.DuplicateWindowSeconds,
            Key1Configured = c.ApiKey1Hash != null, Key2Configured = c.ApiKey2Hash != null,
            c.CreatedAt,
        }).ToListAsync(ct));

    private sealed record CreateChannelRequest(
        string? CustomerCode, string? Key, string? Originator, string? Description,
        string? WebhookUrl, int? DailyPartLimit, string[]? AllowedRecipients);

    private static async Task<IResult> CreateChannelAsync(CreateChannelRequest request, SmsDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.CustomerCode) || string.IsNullOrWhiteSpace(request.Key)
            || string.IsNullOrWhiteSpace(request.Originator))
            return Bad("invalid_request", "'customerCode', 'key' and 'originator' are required.");

        var customer = await db.Customers.SingleOrDefaultAsync(c => c.Code == request.CustomerCode, ct);
        if (customer is null) return NotFound("customer_not_found", $"No customer '{request.CustomerCode}'.");
        if (await db.Channels.AnyAsync(c => c.Key == request.Key, ct))
            return Conflict("channel_exists", $"channel '{request.Key}' already exists.");
        // Guard parity: never create a channel that can only reject invalid_originator.
        if (!await db.AllowedOriginators.AnyAsync(a => a.Originator == request.Originator, ct))
            return Conflict("originator_not_whitelisted",
                $"originator '{request.Originator}' is not on the sender-ID whitelist — add it first.");

        var apiKey = NewKey();
        var channel = new Channel
        {
            Customer = customer,
            Key = request.Key.Trim(),
            Originator = request.Originator,
            Description = request.Description,
            WebhookUrl = request.WebhookUrl,
            DailyPartLimit = request.DailyPartLimit,
            AllowedRecipients = request.AllowedRecipients,
            ApiKey1Hash = ApiKeyHasher.Sha256Hex(apiKey),
            Status = ChannelStatus.Paused, // born safe; activation is an explicit step
        };
        db.Channels.Add(channel);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/admin/channels/{channel.Key}", new
        {
            channel.Id, channel.Key, channel.Originator, channel.Status,
            apiKey, // plaintext, exactly once — only the SHA-256 is stored
        });
    }

    private static async Task<IResult> PatchChannelAsync(string key, JsonDocument body, SmsDbContext db, CancellationToken ct)
    {
        var channel = await db.Channels.SingleOrDefaultAsync(c => c.Key == key, ct);
        if (channel is null) return NotFound("channel_not_found", "No such channel.");
        var root = body.RootElement;

        if (root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String)
        {
            if (!Enum.TryParse<ChannelStatus>(status.GetString(), ignoreCase: true, out var parsed))
                return Bad("invalid_status", "status must be 'active', 'paused' or 'retired'.");
            if (parsed == ChannelStatus.Active &&
                await db.UpstreamBreakers.AnyAsync(b => b.Upstream == channel.Upstream && b.ReArmedAt == null, ct))
                return Conflict("breaker_active",
                    "The upstream breaker is latched — re-arm it (after investigating) before activating channels.");
            channel.Status = parsed;
        }
        if (root.TryGetProperty("description", out var desc))
            channel.Description = desc.ValueKind == JsonValueKind.Null ? null : desc.GetString();
        if (root.TryGetProperty("webhookUrl", out var url))
            channel.WebhookUrl = url.ValueKind == JsonValueKind.Null ? null : url.GetString();
        if (root.TryGetProperty("webhookSecretName", out var secretName))
            channel.WebhookSecretName = secretName.ValueKind == JsonValueKind.Null ? null : secretName.GetString();
        if (root.TryGetProperty("dailyPartLimit", out var limit))
            channel.DailyPartLimit = limit.ValueKind == JsonValueKind.Null ? null : limit.GetInt32();
        if (root.TryGetProperty("allowedRecipients", out var recipients))
            channel.AllowedRecipients = recipients.ValueKind == JsonValueKind.Null
                ? null
                : [.. recipients.EnumerateArray().Select(e => e.GetString()!)];
        if (root.TryGetProperty("duplicateWindowSeconds", out var window) && window.ValueKind == JsonValueKind.Number)
            channel.DuplicateWindowSeconds = window.GetInt32();

        await db.SaveChangesAsync(ct);
        return Results.Ok(new
        {
            channel.Key, channel.Status, channel.Description, channel.Originator,
            channel.WebhookUrl, channel.WebhookSecretName,
            channel.DailyPartLimit, channel.AllowedRecipients, channel.DuplicateWindowSeconds,
        });
    }

    private sealed record RotateKeyRequest(int Slot);

    private static async Task<IResult> RotateChannelKeyAsync(string key, RotateKeyRequest request, SmsDbContext db, CancellationToken ct)
    {
        if (request.Slot is not (1 or 2))
            return Bad("invalid_slot", "slot must be 1 or 2.");
        var channel = await db.Channels.SingleOrDefaultAsync(c => c.Key == key, ct);
        if (channel is null) return NotFound("channel_not_found", "No such channel.");

        var apiKey = NewKey();
        if (request.Slot == 1) channel.ApiKey1Hash = ApiKeyHasher.Sha256Hex(apiKey);
        else channel.ApiKey2Hash = ApiKeyHasher.Sha256Hex(apiKey);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { channel.Key, slot = request.Slot, apiKey }); // plaintext, exactly once
    }

    // ── Sender-ID whitelist ─────────────────────────────────────────────────────

    private static async Task<IResult> ListOriginatorsAsync(SmsDbContext db, CancellationToken ct)
        => Results.Ok(await db.AllowedOriginators.OrderBy(a => a.Originator)
            .Select(a => new { a.Originator, a.Description, a.CreatedAt }).ToListAsync(ct));

    private sealed record AddOriginatorRequest(string? Originator, string? Description);

    private static async Task<IResult> AddOriginatorAsync(AddOriginatorRequest request, SmsDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Originator))
            return Bad("invalid_request", "'originator' is required.");
        if (await db.AllowedOriginators.AnyAsync(a => a.Originator == request.Originator, ct))
            return Conflict("originator_exists", "Already whitelisted.");
        db.AllowedOriginators.Add(new AllowedOriginator
        {
            Originator = request.Originator.Trim(),
            Description = request.Description,
        });
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/admin/originators/{request.Originator}", new { request.Originator, request.Description });
    }

    private static async Task<IResult> DeleteOriginatorAsync(string originator, SmsDbContext db, CancellationToken ct)
    {
        var row = await db.AllowedOriginators.FindAsync([originator], ct);
        if (row is null) return NotFound("originator_not_found", "Not on the whitelist.");
        var user = await db.Channels.Where(c => c.Originator == originator && c.Status != ChannelStatus.Retired)
            .Select(c => c.Key).FirstOrDefaultAsync(ct);
        if (user is not null)
            return Conflict("originator_in_use", $"channel '{user}' sends as this originator — retire it first.");
        db.AllowedOriginators.Remove(row);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    // ── Messages / inbound (read-only explorers) ────────────────────────────────

    private static async Task<IResult> ListMessagesAsync(
        SmsDbContext db, string? channel, string? status, string? to,
        DateTimeOffset? since, DateTimeOffset? until, Guid? before, CancellationToken ct, int limit = 50)
    {
        limit = limit is <= 0 or > 200 ? 50 : limit;
        var query = db.Messages.AsQueryable();
        if (channel is not null) query = query.Where(m => m.Channel!.Key == channel);
        if (status is not null && Enum.TryParse<MessageStatus>(status, ignoreCase: true, out var s))
            query = query.Where(m => m.Status == s);
        if (to is not null) query = query.Where(m => m.ToNumber == to);
        if (since is not null) query = query.Where(m => m.RequestedAt >= since);
        if (until is not null) query = query.Where(m => m.RequestedAt <= until);
        if (before is not null) query = query.Where(m => m.Id.CompareTo(before.Value) < 0);

        // uuidv7 ids are time-ordered — id-descending IS newest-first, and `before` pages it.
        var page = await query.OrderByDescending(m => m.Id).Take(limit).Select(m => new
        {
            m.Id, ChannelKey = m.Channel!.Key, m.CustomerRef, m.ToNumber, m.OriginatorUsed,
            m.Parts, m.Status, m.ErrorCode, m.RequestedAt, m.SubmittedAt, m.DeliveredAt, m.FailedAt,
        }).ToListAsync(ct);
        return Results.Ok(page);
    }

    private static async Task<IResult> GetMessageAsync(Guid id, SmsDbContext db, CancellationToken ct)
    {
        var message = await db.Messages.Include(m => m.Channel).SingleOrDefaultAsync(m => m.Id == id, ct);
        if (message is null) return NotFound("message_not_found", "No such message.");
        var events = await db.DeliveryEvents.Where(e => e.MessageId == id)
            .OrderBy(e => e.ReceivedAt)
            .Select(e => new { e.Id, e.RawResult, e.RawStatus, e.RawDescription, e.ReceivedAt, e.Payload })
            .ToListAsync(ct);
        var outbox = await db.WebhookOutbox
            .FromSql($"SELECT * FROM webhook_outbox WHERE payload->>'messageId' = {id.ToString()}")
            .OrderBy(o => o.Id)
            .Select(o => new { o.Id, o.EventType, o.State, o.Attempts, o.NextAttemptAt, o.CreatedAt })
            .ToListAsync(ct);
        return Results.Ok(new
        {
            message.Id, ChannelKey = message.Channel!.Key, message.CustomerRef, message.ToNumber,
            message.OriginatorUsed, message.Body, message.Parts, message.Status,
            message.ErrorCode, message.ErrorDetail, message.UpstreamId, message.Attempts,
            message.RequestedAt, message.SubmittedAt, message.DeliveredAt, message.FailedAt,
            deliveryEvents = events,
            outboxEntries = outbox,
        });
    }

    private static async Task<IResult> ListInboundAsync(
        SmsDbContext db, string? channel, DateTimeOffset? since, DateTimeOffset? until,
        Guid? before, CancellationToken ct, int limit = 50)
    {
        limit = limit is <= 0 or > 200 ? 50 : limit;
        var query = db.InboundMessages.AsQueryable();
        if (channel is not null) query = query.Where(i => i.Channel!.Key == channel);
        if (since is not null) query = query.Where(i => i.ReceivedAt >= since);
        if (until is not null) query = query.Where(i => i.ReceivedAt <= until);
        if (before is not null) query = query.Where(i => i.Id.CompareTo(before.Value) < 0);
        var page = await query.OrderByDescending(i => i.Id).Take(limit).Select(i => new
        {
            i.Id, ChannelKey = i.Channel!.Key, i.FromNumber, i.ToNumber, i.Body,
            i.ReplyToMessageId, i.Complete, i.ReceivedAt, i.DeliveredToCustomerAt,
            Quarantined = i.Channel!.Key == SystemChannels.OperatorKey,
        }).ToListAsync(ct);
        return Results.Ok(page);
    }

    // ── Webhook outbox ──────────────────────────────────────────────────────────

    private static async Task<IResult> ListOutboxAsync(SmsDbContext db, string? state, string? channel, CancellationToken ct)
    {
        var query = db.WebhookOutbox.AsQueryable();
        if (state is not null && Enum.TryParse<WebhookOutboxState>(state, ignoreCase: true, out var s))
            query = query.Where(o => o.State == s);
        if (channel is not null) query = query.Where(o => o.Channel!.Key == channel);
        return Results.Ok(await query.OrderByDescending(o => o.Id).Take(200).Select(o => new
        {
            o.Id, ChannelKey = o.Channel!.Key, o.EventType, o.State, o.Attempts,
            o.NextAttemptAt, o.CreatedAt,
        }).ToListAsync(ct));
    }

    private static async Task<IResult> RetryOutboxAsync(long id, SmsDbContext db, CancellationToken ct)
    {
        var rows = await db.Database.ExecuteSqlAsync($"""
            UPDATE webhook_outbox
            SET state = 'pending', next_attempt_at = now(), claimed_by = NULL, claimed_at = NULL
            WHERE id = {id}
            """, ct);
        return rows == 0 ? NotFound("outbox_not_found", "No such outbox row.") : Results.Ok(new { id, state = "pending" });
    }

    // ── Breaker ─────────────────────────────────────────────────────────────────

    private static async Task<IResult> ListBreakersAsync(SmsDbContext db, CancellationToken ct)
        => Results.Ok(await db.UpstreamBreakers.OrderByDescending(b => b.Id).Take(50).Select(b => new
        {
            b.Id, b.Upstream, b.TrippedAt, b.UnmatchedCount, b.Reason,
            b.AlertSentAt, b.AlertError, b.ReArmedAt, Active = b.ReArmedAt == null,
        }).ToListAsync(ct));

    private static async Task<IResult> ReArmBreakerAsync(long id, SmsDbContext db, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var rows = await db.Database.ExecuteSqlAsync($"""
            UPDATE upstream_breakers SET re_armed_at = now() WHERE id = {id} AND re_armed_at IS NULL
            """, ct);
        if (rows == 0) return NotFound("breaker_not_found", "No such ACTIVE breaker (already re-armed?).");
        // Deliberately does NOT unpause channels — the operator re-activates each one
        // explicitly after investigating (spec §2.8).
        loggerFactory.CreateLogger("AdminAction").LogWarning("Breaker {BreakerId} RE-ARMED — channels remain paused until explicitly activated", id);
        return Results.Ok(new { id, reArmed = true });
    }

    // ── Settings (config_entries; spec §2.9) ────────────────────────────────────

    private sealed record TunableSpec(string AppliesOn, Func<string, bool> Valid);

    private static bool IsInt(string v) => int.TryParse(v, out _);
    private static bool IsLong(string v) => long.TryParse(v, out _);
    private static bool IsBool(string v) => bool.TryParse(v, out _);
    private static bool IsCsvInts(string v) => v.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .All(x => int.TryParse(x, out _));

    /// <summary>The ONLY keys the settings door accepts — secrets and connection strings can
    /// never ride in through here. RateLimits/body cap are constructed at startup (restart);
    /// the rest hot-apply via IOptionsMonitor + in-process ForceReload.</summary>
    private static readonly Dictionary<string, TunableSpec> Tunables = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SologicalSms:RateLimits:Enabled"] = new("restart", IsBool),
        ["SologicalSms:RateLimits:SendPerSecond"] = new("restart", IsInt),
        ["SologicalSms:RateLimits:SendBurst"] = new("restart", IsInt),
        ["SologicalSms:RateLimits:GlobalPerMinute"] = new("restart", IsInt),
        ["SologicalSms:RateLimits:IngressPerMinute"] = new("restart", IsInt),
        ["SologicalSms:RateLimits:MaxRequestBodyBytes"] = new("restart", IsLong),
        ["SologicalSms:Breaker:Enabled"] = new("reload", IsBool),
        ["SologicalSms:Breaker:UnmatchedThreshold"] = new("reload", IsInt),
        ["SologicalSms:Breaker:WindowMinutes"] = new("reload", IsInt),
        ["SologicalSms:Breaker:AlertNumber"] = new("reload", _ => true),
        ["SologicalSms:Breaker:AlertOriginator"] = new("reload", _ => true),
        ["SologicalSms:Dispatch:PollSeconds"] = new("reload", IsInt),
        ["SologicalSms:Dispatch:BatchSize"] = new("reload", IsInt),
        ["SologicalSms:Dispatch:ClaimLeaseSeconds"] = new("reload", IsInt),
        ["SologicalSms:Dispatch:RetryDelays"] = new("reload", IsCsvInts),
        ["SologicalSms:Egress:PollSeconds"] = new("reload", IsInt),
        ["SologicalSms:Egress:BatchSize"] = new("reload", IsInt),
        ["SologicalSms:Egress:ClaimLeaseSeconds"] = new("reload", IsInt),
        ["SologicalSms:Egress:RequestTimeoutSeconds"] = new("reload", IsInt),
        ["SologicalSms:Egress:RetryDelays"] = new("reload", IsCsvInts),
        ["SologicalSms:Ingress:PartTimeoutSeconds"] = new("reload", IsInt),
    };

    private static async Task<IResult> ListSettingsAsync(
        IConfiguration configuration, PostgresConfigurationWriter writer, CancellationToken ct)
    {
        var overrides = await writer.ListAsync(ct);
        return Results.Ok(Tunables.OrderBy(t => t.Key).Select(t => new
        {
            key = t.Key,
            effective = configuration[t.Key],
            @override = overrides.GetValueOrDefault(t.Key),
            appliesOn = t.Value.AppliesOn,
        }));
    }

    private sealed record PutSettingRequest(string? Value);

    private static async Task<IResult> PutSettingAsync(
        string key, PutSettingRequest request, PostgresConfigurationWriter writer, CancellationToken ct)
    {
        if (!Tunables.TryGetValue(key, out var spec))
            return Bad("unknown_setting", $"'{key}' is not a managed tunable.");
        if (request.Value is null || !spec.Valid(request.Value))
            return Bad("invalid_value", $"'{request.Value}' is not a valid value for {key}.");
        await writer.SetAsync(key, request.Value, ct);
        return Results.Ok(new { key, value = request.Value, appliesOn = spec.AppliesOn });
    }

    private static async Task<IResult> DeleteSettingAsync(
        string key, PostgresConfigurationWriter writer, CancellationToken ct)
    {
        if (!Tunables.TryGetValue(key, out var spec))
            return Bad("unknown_setting", $"'{key}' is not a managed tunable.");
        var removed = await writer.DeleteAsync(key, ct);
        return removed
            ? Results.Ok(new { key, @override = (string?)null, appliesOn = spec.AppliesOn })
            : NotFound("no_override", "No override row exists for this key.");
    }

    // ── Usage (ledger rollup) ───────────────────────────────────────────────────

    private static async Task<IResult> UsageAsync(
        SmsDbContext db, string? customerCode, string? month, CancellationToken ct)
    {
        var start = month is not null && DateTimeOffset.TryParse($"{month}-01T00:00:00Z", out var parsed)
            ? parsed
            : new DateTimeOffset(DateTimeOffset.UtcNow.Year, DateTimeOffset.UtcNow.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var end = start.AddMonths(1);

        var query = db.BillingLedger.Where(l => l.OccurredAt >= start && l.OccurredAt < end);
        if (customerCode is not null)
        {
            var customerId = await db.Customers.Where(c => c.Code == customerCode).Select(c => (long?)c.Id).SingleOrDefaultAsync(ct);
            if (customerId is null) return NotFound("customer_not_found", "No such customer.");
            query = query.Where(l => l.CustomerId == customerId);
        }

        var rows = await query
            .GroupBy(l => new { l.ChannelId, Day = l.OccurredAt.Date, l.Direction })
            .Select(g => new { g.Key.ChannelId, g.Key.Day, g.Key.Direction, Units = g.Sum(l => (int)l.Units) })
            .ToListAsync(ct);
        var channelKeys = await db.Channels.ToDictionaryAsync(c => c.Id, c => c.Key, ct);
        return Results.Ok(new
        {
            month = start.ToString("yyyy-MM"),
            rows = rows.OrderBy(r => r.Day).ThenBy(r => r.ChannelId).Select(r => new
            {
                channelKey = channelKeys.GetValueOrDefault(r.ChannelId, r.ChannelId.ToString()),
                date = r.Day.ToString("yyyy-MM-dd"),
                direction = r.Direction.ToString().ToLowerInvariant(),
                units = r.Units,
            }),
        });
    }

    // ── Service ─────────────────────────────────────────────────────────────────

    private static IResult RestartAsync(IHostApplicationLifetime lifetime, ILoggerFactory loggerFactory)
    {
        loggerFactory.CreateLogger("AdminAction").LogWarning(
            "RESTART requested via management API — stopping host (ACA/docker restart policy revives it with fresh config)");
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500)); // let the 202 flush
            lifetime.StopApplication();
        });
        return Results.Accepted(value: new { restarting = true });
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static IResult Bad(string error, string message)
        => Results.Json(new ApiError(error, message), statusCode: StatusCodes.Status400BadRequest);

    private static IResult Conflict(string error, string message)
        => Results.Json(new ApiError(error, message), statusCode: StatusCodes.Status409Conflict);

    private static IResult NotFound(string error, string message)
        => Results.Json(new ApiError(error, message), statusCode: StatusCodes.Status404NotFound);
}
