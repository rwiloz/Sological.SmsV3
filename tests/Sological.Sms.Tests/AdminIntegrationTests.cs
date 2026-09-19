using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sological.Sms.Core.Entities;
using Sological.Sms.Service.Api;
using Sological.Sms.Service.Data;

namespace Sological.Sms.Tests;

/// <summary>The S7 management API (spec §2): fail-closed auth, internal-host-only surface,
/// channel lifecycle with once-only keys, settings door with hot-apply, outbox/breaker ops.
/// Real PostgreSQL. Local-only (Integration category).</summary>
[Trait("Category", "Integration")]
public sealed class AdminIntegrationTests(SendLanePostgresFixture fixture)
    : IClassFixture<SendLanePostgresFixture>
{
    private const string AdminKey = "test-admin-key";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static SendLaneFactory Factory(string conn, Sological.Sms.Core.Upstream.ISmsUpstream fake, bool withAdminKey = true)
        => new(conn, fake, withAdminKey
            ? new Dictionary<string, string?> { ["SologicalSms:Admin:ApiKey"] = AdminKey }
            : null);

    private static HttpRequestMessage AdminRequest(HttpMethod method, string path, object? body = null, string? key = AdminKey, string? host = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (key is not null) request.Headers.Add("X-Admin-Key", key);
        if (host is not null) request.Headers.Host = host;
        return request;
    }

    [Fact]
    public async Task NoKeyConfigured_FailsClosed503()
    {
        await using var factory = Factory(fixture.ConnectionString, new FakeUpstream(), withAdminKey: false);
        var client = factory.CreateClient();

        var response = await client.SendAsync(AdminRequest(HttpMethod.Get, "/api/admin/overview", key: null));
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadFromJsonAsync<ApiError>(Json))!.Error.Should().Be("admin_disabled");
    }

    [Fact]
    public async Task WrongKey_401_And_PublicHost_Bare404()
    {
        await using var factory = Factory(fixture.ConnectionString, new FakeUpstream());
        var client = factory.CreateClient();

        var wrong = await client.SendAsync(AdminRequest(HttpMethod.Get, "/api/admin/overview", key: "nope"));
        wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await wrong.Content.ReadFromJsonAsync<ApiError>(Json))!.Error.Should().Be("invalid_admin_key");

        // On the public hostname the surface must not exist — even with the RIGHT key.
        var publicHost = await client.SendAsync(AdminRequest(HttpMethod.Get, "/api/admin/overview", host: "sms.dev.ai-workforce.au"));
        publicHost.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await publicHost.Content.ReadAsStringAsync()).Should().BeEmpty("a bare 404 is indistinguishable from no-such-route");
    }

    [Fact]
    public async Task ChannelLifecycle_CreateRotateActivate_KeyShownOnce()
    {
        var fake = new FakeUpstream();
        await using var factory = Factory(fixture.ConnectionString, fake);
        var client = factory.CreateClient();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        (await client.SendAsync(AdminRequest(HttpMethod.Post, "/api/admin/customers",
            new { code = $"cust-{suffix}", name = "Lifecycle Test" }))).StatusCode.Should().Be(HttpStatusCode.Created);

        // Whitelist guard parity: creation refuses an unlisted sender ID.
        var refused = await client.SendAsync(AdminRequest(HttpMethod.Post, "/api/admin/channels",
            new { customerCode = $"cust-{suffix}", key = $"ch-{suffix}", originator = $"TA{suffix[..6]}" }));
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await refused.Content.ReadFromJsonAsync<ApiError>(Json))!.Error.Should().Be("originator_not_whitelisted");

        (await client.SendAsync(AdminRequest(HttpMethod.Post, "/api/admin/originators",
            new { originator = $"TA{suffix[..6]}", description = "test" }))).StatusCode.Should().Be(HttpStatusCode.Created);

        var created = await client.SendAsync(AdminRequest(HttpMethod.Post, "/api/admin/channels",
            new { customerCode = $"cust-{suffix}", key = $"ch-{suffix}", originator = $"TA{suffix[..6]}", dailyPartLimit = 50 }));
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var apiKey = createdBody.RootElement.GetProperty("apiKey").GetString()!;
        apiKey.Should().NotBeNullOrEmpty();
        createdBody.RootElement.GetProperty("status").GetString().Should().Be("paused", "channels are born safe");

        // The once-only key WORKS on the public surface (paused → 403 proves auth passed).
        var send = new HttpRequestMessage(HttpMethod.Post, "/api/v1/messages")
        {
            Content = JsonContent.Create(new { to = "0412345678", body = "x" }),
        };
        send.Headers.Add("X-Api-Key", apiKey);
        var sendResponse = await client.SendAsync(send);
        sendResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await sendResponse.Content.ReadFromJsonAsync<ApiError>(Json))!.Error.Should().Be("channel_paused");

        // Rotate slot 2 — a fresh key comes back exactly once; slot flags update.
        var rotated = await client.SendAsync(AdminRequest(HttpMethod.Post, $"/api/admin/channels/ch-{suffix}/rotate-key", new { slot = 2 }));
        rotated.StatusCode.Should().Be(HttpStatusCode.OK);
        using var rotatedBody = JsonDocument.Parse(await rotated.Content.ReadAsStringAsync());
        rotatedBody.RootElement.GetProperty("apiKey").GetString().Should().NotBe(apiKey);

        // Activate via PATCH, then the allowlist/quota fields round-trip.
        var patched = await client.SendAsync(AdminRequest(HttpMethod.Patch, $"/api/admin/channels/ch-{suffix}",
            new { status = "active", allowedRecipients = new[] { "+61408004199" } }));
        patched.StatusCode.Should().Be(HttpStatusCode.OK);
        using var patchedBody = JsonDocument.Parse(await patched.Content.ReadAsStringAsync());
        patchedBody.RootElement.GetProperty("status").GetString().Should().Be("active");
        patchedBody.RootElement.GetProperty("allowedRecipients")[0].GetString().Should().Be("+61408004199");

        // Whitelist delete refuses while a live channel uses the sender.
        var inUse = await client.SendAsync(AdminRequest(HttpMethod.Delete, $"/api/admin/originators/TA{suffix[..6]}"));
        inUse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await inUse.Content.ReadFromJsonAsync<ApiError>(Json))!.Error.Should().Be("originator_in_use");
    }

    [Fact]
    public async Task Settings_Door_ValidatesAndHotApplies()
    {
        await using var factory = Factory(fixture.ConnectionString, new FakeUpstream());
        var client = factory.CreateClient();

        (await (await client.SendAsync(AdminRequest(HttpMethod.Put, "/api/admin/settings/SologicalSms:SmsCentral:Password",
            new { value = "sneaky" }))).Content.ReadFromJsonAsync<ApiError>(Json))!
            .Error.Should().Be("unknown_setting", "secrets can never ride in through the settings door");

        (await (await client.SendAsync(AdminRequest(HttpMethod.Put, "/api/admin/settings/SologicalSms:Breaker:UnmatchedThreshold",
            new { value = "not-a-number" }))).Content.ReadFromJsonAsync<ApiError>(Json))!
            .Error.Should().Be("invalid_value");

        var put = await client.SendAsync(AdminRequest(HttpMethod.Put, "/api/admin/settings/SologicalSms:Breaker:UnmatchedThreshold",
            new { value = "5" }));
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        using var putBody = JsonDocument.Parse(await put.Content.ReadAsStringAsync());
        putBody.RootElement.GetProperty("appliesOn").GetString().Should().Be("reload");

        // In-process ForceReload means the OVERRIDE is already effective — no restart, no 30s.
        var list = await client.SendAsync(AdminRequest(HttpMethod.Get, "/api/admin/settings"));
        using var listBody = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var entry = listBody.RootElement.EnumerateArray()
            .Single(e => e.GetProperty("key").GetString() == "SologicalSms:Breaker:UnmatchedThreshold");
        entry.GetProperty("override").GetString().Should().Be("5");
        entry.GetProperty("effective").GetString().Should().Be("5");

        var delete = await client.SendAsync(AdminRequest(HttpMethod.Delete, "/api/admin/settings/SologicalSms:Breaker:UnmatchedThreshold"));
        delete.StatusCode.Should().Be(HttpStatusCode.OK);
        var relisted = await client.SendAsync(AdminRequest(HttpMethod.Get, "/api/admin/settings"));
        using var relistedBody = JsonDocument.Parse(await relisted.Content.ReadAsStringAsync());
        relistedBody.RootElement.EnumerateArray()
            .Single(e => e.GetProperty("key").GetString() == "SologicalSms:Breaker:UnmatchedThreshold")
            .GetProperty("override").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task OutboxRetry_And_BreakerReArm()
    {
        await using var factory = Factory(fixture.ConnectionString, new FakeUpstream());
        var client = factory.CreateClient();

        long outboxId, breakerId, channelId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SmsDbContext>();
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var channel = new Channel
            {
                Customer = new Customer { Code = $"ob-{suffix}", Name = "Outbox Test" },
                Key = $"ob-{suffix}",
                Originator = $"OB{suffix[..6]}",
            };
            db.Channels.Add(channel);
            var entry = new WebhookOutboxEntry
            {
                Channel = channel,
                EventType = WebhookEventType.SmsDelivery,
                Payload = JsonDocument.Parse("{}"),
                State = WebhookOutboxState.Dead,
                Attempts = 4,
            };
            db.WebhookOutbox.Add(entry);
            var breaker = new UpstreamBreaker { Upstream = UpstreamProvider.SmsCentral, UnmatchedCount = 12 };
            db.UpstreamBreakers.Add(breaker);
            await db.SaveChangesAsync();
            outboxId = entry.Id;
            breakerId = breaker.Id;
            channelId = channel.Id;
        }

        // The overview names the latched breaker by id — what the admin page's Re-arm button acts on.
        var overview = await client.SendAsync(AdminRequest(HttpMethod.Get, "/api/admin/overview"));
        overview.StatusCode.Should().Be(HttpStatusCode.OK);
        var breakerJson = JsonDocument.Parse(await overview.Content.ReadAsStringAsync()).RootElement.GetProperty("breaker");
        breakerJson.GetProperty("active").GetBoolean().Should().BeTrue();
        breakerJson.GetProperty("id").GetInt64().Should().Be(breakerId);
        breakerJson.GetProperty("unmatchedCount").GetInt32().Should().Be(12);

        var retry = await client.SendAsync(AdminRequest(HttpMethod.Post, $"/api/admin/outbox/{outboxId}/retry", new { }));
        retry.StatusCode.Should().Be(HttpStatusCode.OK);

        // While latched, activation is refused.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SmsDbContext>();
            var refreshed = await db.WebhookOutbox.SingleAsync(o => o.Id == outboxId);
            refreshed.State.Should().Be(WebhookOutboxState.Pending);
            refreshed.NextAttemptAt.Should().BeOnOrBefore(DateTimeOffset.UtcNow.AddSeconds(5));
            var channelKey = (await db.Channels.SingleAsync(c => c.Id == channelId)).Key;

            var blocked = await client.SendAsync(AdminRequest(HttpMethod.Patch, $"/api/admin/channels/{channelKey}",
                new { status = "active" }));
            blocked.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await blocked.Content.ReadFromJsonAsync<ApiError>(Json))!.Error.Should().Be("breaker_active");
        }

        var reArm = await client.SendAsync(AdminRequest(HttpMethod.Post, $"/api/admin/breakers/{breakerId}/re-arm", new { }));
        reArm.StatusCode.Should().Be(HttpStatusCode.OK);
        var again = await client.SendAsync(AdminRequest(HttpMethod.Post, $"/api/admin/breakers/{breakerId}/re-arm", new { }));
        again.StatusCode.Should().Be(HttpStatusCode.NotFound, "the latch re-arms exactly once");
    }

    [Fact]
    public async Task Overview_And_MessageExplorer_Smoke()
    {
        var fake = new FakeUpstream();
        await using var factory = Factory(fixture.ConnectionString, fake);
        var client = factory.CreateClient();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        // Seed via admin API end-to-end: customer → originator → channel → activate → send.
        await client.SendAsync(AdminRequest(HttpMethod.Post, "/api/admin/customers", new { code = $"ov-{suffix}", name = "Overview" }));
        await client.SendAsync(AdminRequest(HttpMethod.Post, "/api/admin/originators", new { originator = $"OV{suffix[..6]}" }));
        var created = await client.SendAsync(AdminRequest(HttpMethod.Post, "/api/admin/channels",
            new { customerCode = $"ov-{suffix}", key = $"ov-{suffix}", originator = $"OV{suffix[..6]}" }));
        using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var apiKey = createdBody.RootElement.GetProperty("apiKey").GetString()!;
        await client.SendAsync(AdminRequest(HttpMethod.Patch, $"/api/admin/channels/ov-{suffix}", new { status = "active" }));

        var send = new HttpRequestMessage(HttpMethod.Post, "/api/v1/messages")
        {
            Content = JsonContent.Create(new { to = "0412000777", body = "explorer smoke" }),
        };
        send.Headers.Add("X-Api-Key", apiKey);
        using var sendBody = JsonDocument.Parse(await (await client.SendAsync(send)).Content.ReadAsStringAsync());
        var messageId = sendBody.RootElement.GetProperty("messageId").GetGuid();

        var overview = await client.SendAsync(AdminRequest(HttpMethod.Get, "/api/admin/overview"));
        overview.StatusCode.Should().Be(HttpStatusCode.OK);
        (await overview.Content.ReadAsStringAsync()).Should().Contain($"ov-{suffix}");

        var list = await client.SendAsync(AdminRequest(HttpMethod.Get, $"/api/admin/messages?channel=ov-{suffix}"));
        (await list.Content.ReadAsStringAsync()).Should().Contain(messageId.ToString());

        // Cursor paging: `before` the only message → empty page (also proves the Guid
        // CompareTo translation).
        var paged = await client.SendAsync(AdminRequest(HttpMethod.Get, $"/api/admin/messages?channel=ov-{suffix}&before={messageId}"));
        using var pagedBody = JsonDocument.Parse(await paged.Content.ReadAsStringAsync());
        pagedBody.RootElement.GetArrayLength().Should().Be(0);

        var detail = await client.SendAsync(AdminRequest(HttpMethod.Get, $"/api/admin/messages/{messageId}"));
        detail.StatusCode.Should().Be(HttpStatusCode.OK);
        using var detailBody = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        detailBody.RootElement.GetProperty("body").GetString().Should().Be("explorer smoke");
    }
}
