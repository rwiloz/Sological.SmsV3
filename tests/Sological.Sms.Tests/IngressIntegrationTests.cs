using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sological.Sms.Core.Entities;
using Sological.Sms.Core.Sms;
using Sological.Sms.Service.Api;
using Sological.Sms.Service.Data;
using Testcontainers.PostgreSql;

namespace Sological.Sms.Tests;

public sealed class IngressPostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public string ConnectionString => _postgres.GetConnectionString();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();
}

/// <summary>The S3 receivers end-to-end: real PostgreSQL, real endpoints, the exact query
/// shapes from the live legacy captures. Local-only (Integration category).</summary>
[Trait("Category", "Integration")]
public sealed class IngressIntegrationTests(IngressPostgresFixture fixture)
    : IClassFixture<IngressPostgresFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private sealed record TestChannel(string ApiKey, string Originator, long ChannelId);

    private static async Task<TestChannel> SeedAsync(SendLaneFactory factory, string? numericOriginator = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var apiKey = $"key-{suffix}";
        var originator = numericOriginator ?? $"TS{suffix[..6]}";

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmsDbContext>();
        var channel = new Channel
        {
            Customer = new Customer { Code = $"c{suffix}", Name = $"Test {suffix}" },
            Key = $"ch-{suffix}",
            Originator = originator,
            ApiKey1Hash = ApiKeyHasher.Sha256Hex(apiKey),
        };
        db.Channels.Add(channel);
        db.AllowedOriginators.Add(new AllowedOriginator { Originator = originator });
        await db.SaveChangesAsync();
        return new TestChannel(apiKey, originator, channel.Id);
    }

    private static async Task<Guid> SendToSentAsync(SendLaneFactory factory, HttpClient client, TestChannel ch, string to, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/messages")
        {
            Content = JsonContent.Create(new { to, body }),
        };
        request.Headers.Add("X-Api-Key", ch.ApiKey);
        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var accepted = (await response.Content.ReadFromJsonAsync<SendMessageResponse>(Json))!;

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var status = await GetStatusAsync(client, ch.ApiKey, accepted.MessageId);
            if (status.Status == MessageStatus.Sent) return accepted.MessageId;
            await Task.Delay(250);
        }
        throw new Xunit.Sdk.XunitException($"Message {accepted.MessageId} never reached sent");
    }

    private static async Task<MessageStatusResponse> GetStatusAsync(HttpClient client, string apiKey, Guid id)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/messages/{id}");
        request.Headers.Add("X-Api-Key", apiKey);
        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<MessageStatusResponse>(Json))!;
    }

    private static string Query(params (string Key, string Value)[] pairs)
        => string.Join('&', pairs.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));

    private static async Task<string> PushAsync(HttpClient client, string path, params (string, string)[] pairs)
    {
        var response = await client.GetAsync($"{path}?{Query(pairs)}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadAsStringAsync();
    }

    // ── Delivery receiver ────────────────────────────────────────────────────

    [Fact]
    public async Task Dlr_Delivered_TransitionsMessage_AndAudits()
    {
        var fake = new FakeUpstream();
        await using var factory = new SendLaneFactory(fixture.ConnectionString, fake);
        var client = factory.CreateClient();
        var ch = await SeedAsync(factory);
        var id = await SendToSentAsync(factory, client, ch, "0412000001", "dlr happy");

        var ack = await PushAsync(client, "/ingress/smscentral/delivery",
            ("USERNAME", "subuser"), ("PASSWORD", "subpass"),
            ("REFERENCE", id.ToString("N")), ("ID", $"evt-{id:N}"),
            ("RESULT", "1"), ("STATUS", "DELIVRD"));
        ack.Should().Be("0");

        var status = await GetStatusAsync(client, ch.ApiKey, id);
        status.Status.Should().Be(MessageStatus.Delivered);
        status.DeliveredAt.Should().NotBeNull();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmsDbContext>();
        (await db.DeliveryEvents.CountAsync(e => e.MessageId == id)).Should().Be(1);
    }

    [Fact]
    public async Task Dlr_DuplicatePush_IsIdempotent()
    {
        var fake = new FakeUpstream();
        await using var factory = new SendLaneFactory(fixture.ConnectionString, fake);
        var client = factory.CreateClient();
        var ch = await SeedAsync(factory);
        var id = await SendToSentAsync(factory, client, ch, "0412000002", "dlr dedupe");

        (string, string)[] push =
        [
            ("REFERENCE", id.ToString("N")), ("ID", $"evt-{id:N}"), ("RESULT", "1"),
        ];
        (await PushAsync(client, "/ingress/smscentral/delivery", push)).Should().Be("0");
        (await PushAsync(client, "/ingress/smscentral/delivery", push)).Should().Be("0");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmsDbContext>();
        (await db.DeliveryEvents.CountAsync(e => e.MessageId == id)).Should().Be(1, "the retry engine is a duplication engine — dedupe before ack");
    }

    [Fact]
    public async Task Dlr_UnknownReference_KeepsAuditRow_AndNever500s()
    {
        var fake = new FakeUpstream();
        await using var factory = new SendLaneFactory(fixture.ConnectionString, fake);
        var client = factory.CreateClient();

        var unknownRef = Guid.NewGuid().ToString("N");
        var ack = await PushAsync(client, "/ingress/smscentral/delivery",
            ("REFERENCE", unknownRef), ("ID", $"evt-{unknownRef}"), ("RESULT", "1"));
        ack.Should().Be("0", "some other account's traffic must never 500");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmsDbContext>();
        // jsonb dictionary indexers don't translate to SQL — filter client-side.
        var orphans = await db.DeliveryEvents.Where(e => e.MessageId == null).ToListAsync();
        orphans.Should().ContainSingle(e => e.Payload.GetValueOrDefault("REFERENCE") == unknownRef);
    }

    [Fact]
    public async Task Dlr_Multipart_DeliversOnlyWhenEveryPartConfirms()
    {
        var fake = new FakeUpstream();
        await using var factory = new SendLaneFactory(fixture.ConnectionString, fake);
        var client = factory.CreateClient();
        var ch = await SeedAsync(factory);
        var id = await SendToSentAsync(factory, client, ch, "0412000003", new string('x', 200)); // 2 parts

        await PushAsync(client, "/ingress/smscentral/delivery",
            ("REFERENCE", id.ToString("N")), ("ID", "mp-1"), ("RESULT", "1"), ("UDH", "050003AA0201"));
        (await GetStatusAsync(client, ch.ApiKey, id)).Status.Should().Be(MessageStatus.Sent, "only 1 of 2 parts confirmed");

        await PushAsync(client, "/ingress/smscentral/delivery",
            ("REFERENCE", id.ToString("N")), ("ID", "mp-2"), ("RESULT", "1"), ("UDH", "050003AA0202"));
        (await GetStatusAsync(client, ch.ApiKey, id)).Status.Should().Be(MessageStatus.Delivered);
    }

    [Fact]
    public async Task Dlr_AnyPartFailing_FailsTheMessage()
    {
        var fake = new FakeUpstream();
        await using var factory = new SendLaneFactory(fixture.ConnectionString, fake);
        var client = factory.CreateClient();
        var ch = await SeedAsync(factory);
        var id = await SendToSentAsync(factory, client, ch, "0412000004", new string('y', 200));

        await PushAsync(client, "/ingress/smscentral/delivery",
            ("REFERENCE", id.ToString("N")), ("ID", "pf-1"), ("RESULT", "1"), ("UDH", "050003AB0201"));
        await PushAsync(client, "/ingress/smscentral/delivery",
            ("REFERENCE", id.ToString("N")), ("ID", "pf-2"), ("RESULT", "550"),
            ("STATUSDESCRIPTION", "0001: Phone related"), ("UDH", "050003AB0202"));

        var status = await GetStatusAsync(client, ch.ApiKey, id);
        status.Status.Should().Be(MessageStatus.Failed);
        status.ErrorCode.Should().Be("550");
        status.ErrorDetail.Should().Be("0001: Phone related");
    }

    [Fact]
    public async Task Dlr_WrongCredentials_Rejected401_NoAudit()
    {
        var fake = new FakeUpstream();
        await using var factory = new SendLaneFactory(fixture.ConnectionString, fake);
        var client = factory.CreateClient();

        var marker = Guid.NewGuid().ToString("N");
        var response = await client.GetAsync(
            $"/ingress/smscentral/delivery?{Query(("USERNAME", "intruder"), ("PASSWORD", "nope"), ("REFERENCE", marker), ("ID", "x"), ("RESULT", "1"))}");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmsDbContext>();
        var payloads = await db.DeliveryEvents.Select(e => e.Payload).ToListAsync();
        payloads.Should().NotContain(p => p.GetValueOrDefault("REFERENCE") == marker);
    }

    [Fact]
    public async Task Dlr_PostFormEncoded_WorksLikeGet()
    {
        var fake = new FakeUpstream();
        await using var factory = new SendLaneFactory(fixture.ConnectionString, fake);
        var client = factory.CreateClient();
        var ch = await SeedAsync(factory);
        var id = await SendToSentAsync(factory, client, ch, "0412000006", "posted dlr");

        var response = await client.PostAsync("/ingress/smscentral/delivery", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["USERNAME"] = "subuser",
                ["PASSWORD"] = "subpass",
                ["REFERENCE"] = id.ToString("N"),
                ["ID"] = $"post-{id:N}",
                ["RESULT"] = "1",
                ["STATUS"] = "DELIVRD",
            }));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("0");

        (await GetStatusAsync(client, ch.ApiKey, id)).Status.Should().Be(MessageStatus.Delivered);
    }

    [Fact]
    public async Task SlVerifyHeader_RightKeyAccepted_WrongKey401_CredsParamsRedacted()
    {
        var fake = new FakeUpstream();
        await using var factory = new SendLaneFactory(fixture.ConnectionString, fake);
        var client = factory.CreateClient();
        var ch = await SeedAsync(factory);
        var id = await SendToSentAsync(factory, client, ch, "0412000008", "slverify");

        // Wrong key → 401, nothing written.
        var bad = new HttpRequestMessage(HttpMethod.Get,
            $"/ingress/smscentral/delivery?{Query(("REFERENCE", id.ToString("N")), ("ID", "slv-bad"), ("RESULT", "1"))}");
        bad.Headers.Add("SLVERIFY", "not-the-key");
        (await client.SendAsync(bad)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Right key + creds params → accepted, and the stored payload is redacted.
        var good = new HttpRequestMessage(HttpMethod.Get,
            $"/ingress/smscentral/delivery?{Query(("USERNAME", "subuser"), ("PASSWORD", "subpass"), ("REFERENCE", id.ToString("N")), ("ID", "slv-good"), ("RESULT", "1"))}");
        good.Headers.Add("SLVERIFY", "test-verify-key");
        var response = await client.SendAsync(good);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("0");

        (await GetStatusAsync(client, ch.ApiKey, id)).Status.Should().Be(MessageStatus.Delivered);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmsDbContext>();
        var stored = (await db.DeliveryEvents.Where(e => e.MessageId == id).ToListAsync())
            .Single(e => e.Payload.GetValueOrDefault("ID") == "slv-good");
        stored.Payload["PASSWORD"].Should().Be("***", "payload jsonb is forever — creds never persist");
        stored.Payload["USERNAME"].Should().Be("***");
    }

    [Fact]
    public async Task Dlr_JsonTemplatedPush_ModernStatus_MetadataReferencePromoted()
    {
        var fake = new FakeUpstream();
        await using var factory = new SendLaneFactory(fixture.ConnectionString, fake);
        var client = factory.CreateClient();
        var ch = await SeedAsync(factory);
        var id = await SendToSentAsync(factory, client, ch, "0412000009", "json dlr");

        // The modern webhook engine's templated JSON, byte-for-byte as the portal
        // template emits it: word status, no RESULT, REFERENCE inside the METADATA dump.
        var refN = id.ToString("N");
        var json = "{\"ID\":\"jdr-" + refN + "\",\"STATUS\":\"delivered\",\"STATUSDESCRIPTION\":\"221\"," +
                   "\"METADATA\":{\"REFERENCE\":\"" + refN + "\"}}";
        var response = await client.PostAsync("/ingress/smscentral/delivery",
            new StringContent(json, Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("0");

        (await GetStatusAsync(client, ch.ApiKey, id)).Status.Should().Be(MessageStatus.Delivered);
    }

    [Fact]
    public async Task Dlr_PortalPickerShape_RaysFieldNames_Delivers()
    {
        var fake = new FakeUpstream();
        await using var factory = new SendLaneFactory(fixture.ConnectionString, fake);
        var client = factory.CreateClient();
        var ch = await SeedAsync(factory);
        var id = await SendToSentAsync(factory, client, ch, "0412000010", "portal picker dlr");

        // Ray's configured delivery mapping (2026-07-30): dtId/mtId/status/statusCode/
        // sourceAddress/timestamps/reference/mtContent.
        var json = "{\"dtId\":\"pp-dr-1\",\"mtId\":\"877c19ef\",\"status\":\"delivered\",\"statusCode\":\"221\"," +
                   "\"sourceAddress\":\"+61438887301\",\"submittedTimestamp\":\"2026-07-30T08:43:00.850Z\"," +
                   "\"receivedTimestamp\":\"2026-07-30T08:44:00.850Z\",\"reference\":\"" + id.ToString("N") + "\"," +
                   "\"mtContent\":\"portal picker dlr\"}";
        var response = await client.PostAsync("/ingress/smscentral/delivery",
            new StringContent(json, Encoding.UTF8, "application/json"));
        (await response.Content.ReadAsStringAsync()).Should().Be("0");

        (await GetStatusAsync(client, ch.ApiKey, id)).Status.Should().Be(MessageStatus.Delivered);

        // Same push again (their retries) — dtId is the dedupe key.
        await client.PostAsync("/ingress/smscentral/delivery",
            new StringContent(json, Encoding.UTF8, "application/json"));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmsDbContext>();
        (await db.DeliveryEvents.CountAsync(e => e.MessageId == id)).Should().Be(1);
    }

    [Fact]
    public async Task MtIdChain_ContentMatch_Backfill_Delivery_AndExactReplyCorrelation()
    {
        var fake = new FakeUpstream();
        await using var factory = new SendLaneFactory(fixture.ConnectionString, fake);
        var client = factory.CreateClient();
        var ch = await SeedAsync(factory); // alpha originator — replies can ONLY correlate via mtId
        var id = await SendToSentAsync(factory, client, ch, "0412000011", "mtid chain body");
        const string mtId = "0b0db661-bdae-4b4c-b187-c34e5bd398c7";
        // The live pushes carry the UNRESOLVED Velocity literal when metadata has no such key.
        const string badRef = "$metadata.get('REFERENCE')";

        // DR 1 (enroute): no usable reference, unknown mtId — content match backfills upstream_id.
        var dr1 = "{\"dtId\":\"mt-dr-1\",\"mtId\":\"" + mtId + "\",\"status\":\"enroute\",\"statusCode\":\"101\"," +
                  "\"sourceAddress\":\"+61412000011\",\"reference\":\"" + badRef + "\",\"mtContent\":\"mtid chain body\"}";
        (await (await client.PostAsync("/ingress/smscentral/delivery",
            new StringContent(dr1, Encoding.UTF8, "application/json"))).Content.ReadAsStringAsync()).Should().Be("0");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SmsDbContext>();
            (await db.Messages.SingleAsync(m => m.Id == id)).UpstreamId.Should().Be(mtId, "the first receipt backfills mtId");
        }
        (await GetStatusAsync(client, ch.ApiKey, id)).Status.Should().Be(MessageStatus.Sent, "enroute is interim");

        // DR 2 (delivered): mtId only — no content, no reference. Must match via upstream_id.
        var dr2 = "{\"dtId\":\"mt-dr-2\",\"mtId\":\"" + mtId + "\",\"status\":\"delivered\",\"statusCode\":\"220\"," +
                  "\"sourceAddress\":\"+61412000011\",\"reference\":\"" + badRef + "\"}";
        await client.PostAsync("/ingress/smscentral/delivery", new StringContent(dr2, Encoding.UTF8, "application/json"));
        (await GetStatusAsync(client, ch.ApiKey, id)).Status.Should().Be(MessageStatus.Delivered);

        // Reply: carries the same mtId — exact reply_to correlation even though the
        // channel's originator is an alpha ID (no recipient routing possible).
        var mo = "{\"moId\":\"mt-mo-1\",\"mtId\":\"" + mtId + "\",\"sourceAddress\":\"+61412000011\"," +
                 "\"destinationAddress\":\"+61499999999\",\"moContent\":\"exact reply\",\"reference\":\"" + badRef + "\"}";
        (await (await client.PostAsync("/ingress/smscentral/inbound",
            new StringContent(mo, Encoding.UTF8, "application/json"))).Content.ReadAsStringAsync()).Should().Be("0");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SmsDbContext>();
            var inbound = await db.InboundMessages.SingleAsync(m => m.UpstreamId == "mt-mo-1");
            inbound.ReplyToMessageId.Should().Be(id, "mtId gives exact reply correlation");
            inbound.ChannelId.Should().Be(ch.ChannelId, "channel comes from the correlated message");
            inbound.Body.Should().Be("exact reply");
        }
    }

    // ── Inbound receiver ─────────────────────────────────────────────────────

    [Fact]
    public async Task Inbound_PortalPickerShape_RaysFieldNames_RoutesAndStoresReply()
    {
        var fake = new FakeUpstream();
        await using var factory = new SendLaneFactory(fixture.ConnectionString, fake);
        var client = factory.CreateClient();
        var ch = await SeedAsync(factory, numericOriginator: "0499000444");

        // Ray's configured inbound mapping (2026-07-30): moId/mtId/sourceAddress/
        // destinationAddress/mtContent/moContent/submittedTimestamp.
        var json = "{\"moId\":\"pp-mo-1\",\"mtId\":\"877c19ef\",\"sourceAddress\":\"+61408004199\"," +
                   "\"destinationAddress\":\"+61499000444\",\"mtContent\":\"original text\"," +
                   "\"moContent\":\"the actual reply\",\"submittedTimestamp\":\"2026-07-30T08:45:00.850Z\"}";
        var response = await client.PostAsync("/ingress/smscentral/inbound",
            new StringContent(json, Encoding.UTF8, "application/json"));
        (await response.Content.ReadAsStringAsync()).Should().Be("0");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmsDbContext>();
        var inbound = await db.InboundMessages.SingleAsync(m => m.UpstreamId == "pp-mo-1");
        inbound.ChannelId.Should().Be(ch.ChannelId, "destinationAddress routes to the dedicated-number channel");
        inbound.FromNumber.Should().Be("+61408004199");
        inbound.Body.Should().Be("the actual reply", "moContent, never mtContent");
        inbound.Complete.Should().BeTrue();
    }

    [Fact]
    public async Task Inbound_PostFormEncoded_WorksLikeGet()
    {
        var fake = new FakeUpstream();
        await using var factory = new SendLaneFactory(fixture.ConnectionString, fake);
        var client = factory.CreateClient();
        var ch = await SeedAsync(factory);
        var id = await SendToSentAsync(factory, client, ch, "0412000007", "posted reply expected");

        var response = await client.PostAsync("/ingress/smscentral/inbound", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["ORIGINATOR"] = "61412000007",
                ["RECIPIENT"] = "61499111333",
                ["REFERENCE"] = id.ToString("N"),
                ["MESSAGE_TEXT"] = "posted back",
                ["ID"] = $"post-in-{id:N}",
            }));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("0");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmsDbContext>();
        var inbound = await db.InboundMessages.SingleAsync(m => m.UpstreamId == $"post-in-{id:N}");
        inbound.ChannelId.Should().Be(ch.ChannelId);
        inbound.ReplyToMessageId.Should().Be(id);
        inbound.Body.Should().Be("posted back");
    }

    [Fact]
    public async Task Inbound_Reply_CorrelatesByReference_AndDedupesById()
    {
        var fake = new FakeUpstream();
        await using var factory = new SendLaneFactory(fixture.ConnectionString, fake);
        var client = factory.CreateClient();
        var ch = await SeedAsync(factory);
        var id = await SendToSentAsync(factory, client, ch, "0412000005", "expect a reply");

        (string, string)[] push =
        [
            ("ORIGINATOR", "61412000005"), ("RECIPIENT", "61499111222"),
            ("REFERENCE", id.ToString("N")), ("MESSAGE_TEXT", "yes please"), ("ID", $"in-{id:N}"),
        ];
        (await PushAsync(client, "/ingress/smscentral/inbound", push)).Should().Be("0");
        (await PushAsync(client, "/ingress/smscentral/inbound", push)).Should().Be("0"); // retry duplicate

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmsDbContext>();
        var inbound = await db.InboundMessages.SingleAsync(m => m.UpstreamId == $"in-{id:N}");
        inbound.ChannelId.Should().Be(ch.ChannelId, "REFERENCE → message → channel is authoritative");
        inbound.ReplyToMessageId.Should().Be(id);
        inbound.Body.Should().Be("yes please");
        inbound.Complete.Should().BeTrue();
        inbound.DeliveredToCustomerAt.Should().BeNull("NULL = still owed to the customer (egress is S4)");
    }

    [Fact]
    public async Task Inbound_DedicatedNumber_RoutesByRecipient()
    {
        var fake = new FakeUpstream();
        await using var factory = new SendLaneFactory(fixture.ConnectionString, fake);
        var client = factory.CreateClient();
        var ch = await SeedAsync(factory, numericOriginator: "0499000111");

        await PushAsync(client, "/ingress/smscentral/inbound",
            ("ORIGINATOR", "61451729121"), ("RECIPIENT", "61499000111"),
            ("MESSAGE_TEXT", "no reference here"), ("ID", "ded-1"));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmsDbContext>();
        var inbound = await db.InboundMessages.SingleAsync(m => m.UpstreamId == "ded-1");
        inbound.ChannelId.Should().Be(ch.ChannelId);
        inbound.ReplyToMessageId.Should().BeNull();
    }

    [Fact]
    public async Task Inbound_Unrouteable_QuarantinesOnOperatorChannel()
    {
        var fake = new FakeUpstream();
        await using var factory = new SendLaneFactory(fixture.ConnectionString, fake);
        var client = factory.CreateClient();

        await PushAsync(client, "/ingress/smscentral/inbound",
            ("ORIGINATOR", "61400999888"), ("RECIPIENT", "61400000042"),
            ("REFERENCE", "10399858"), // legacy-style foreign ref — unparseable as our uuid
            ("MESSAGE_TEXT", "lost soul"), ("ID", "qr-1"));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmsDbContext>();
        var operatorChannel = await db.Channels.SingleAsync(c => c.Key == SystemChannels.OperatorKey);
        var inbound = await db.InboundMessages.SingleAsync(m => m.UpstreamId == "qr-1");
        inbound.ChannelId.Should().Be(operatorChannel.Id, "never dropped, never 500");
    }

    [Fact]
    public async Task Inbound_MultipartUcs2_Reassembles_InOrder_EvenOutOfOrder()
    {
        var fake = new FakeUpstream();
        await using var factory = new SendLaneFactory(fixture.ConnectionString, fake);
        var client = factory.CreateClient();
        var ch = await SeedAsync(factory, numericOriginator: "0499000222");

        string Hex(string s) => Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(s));

        // Part 2 arrives first — reassembly must order by part number.
        await PushAsync(client, "/ingress/smscentral/inbound",
            ("ORIGINATOR", "61413503503"), ("RECIPIENT", "61499000222"),
            ("UDH", "050003BB0202"), ("BINARY", Hex("second ★ half")), ("DCS", "8"), ("ID", "mpi-2"));

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SmsDbContext>();
            (await db.InboundMessages.AnyAsync(m => m.ChannelId == ch.ChannelId)).Should().BeFalse("group incomplete");
            (await db.InboundParts.CountAsync(p => p.ChannelId == ch.ChannelId)).Should().Be(1);
        }

        await PushAsync(client, "/ingress/smscentral/inbound",
            ("ORIGINATOR", "61413503503"), ("RECIPIENT", "61499000222"),
            ("UDH", "050003BB0201"), ("BINARY", Hex("first 日 half, ")), ("DCS", "8"), ("ID", "mpi-1"));

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SmsDbContext>();
            var inbound = await db.InboundMessages.SingleAsync(m => m.ChannelId == ch.ChannelId);
            inbound.Body.Should().Be("first 日 half, second ★ half");
            inbound.Complete.Should().BeTrue();
            (await db.InboundParts.CountAsync(p => p.ChannelId == ch.ChannelId)).Should().Be(0, "the buffer clears on assembly");
        }
    }

    [Fact]
    public async Task Inbound_StalePartialGroup_IsSweptIncomplete()
    {
        var fake = new FakeUpstream();
        await using var factory = new SendLaneFactory(fixture.ConnectionString, fake, new Dictionary<string, string?>
        {
            ["SologicalSms:Ingress:PartTimeoutSeconds"] = "1",
            ["SologicalSms:Ingress:SweepIntervalSeconds"] = "1",
        });
        var client = factory.CreateClient();
        var ch = await SeedAsync(factory, numericOriginator: "0499000333");

        await PushAsync(client, "/ingress/smscentral/inbound",
            ("ORIGINATOR", "61481739207"), ("RECIPIENT", "61499000333"),
            ("UDH", "050003CC0301"), ("BINARY", Convert.ToHexString(Encoding.Latin1.GetBytes("only part one of three"))),
            ("DCS", "0"), ("ID", "sw-1"));

        // Poll with a ceiling for the sweeper's partial flush (never fixed-delay-then-assert).
        var deadline = DateTime.UtcNow.AddSeconds(20);
        InboundMessage? swept = null;
        while (DateTime.UtcNow < deadline && swept is null)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SmsDbContext>();
            swept = await db.InboundMessages.SingleOrDefaultAsync(m => m.ChannelId == ch.ChannelId);
            if (swept is null) await Task.Delay(250);
        }

        swept.Should().NotBeNull("the 60s (here 1s) timeout delivers what arrived");
        swept!.Complete.Should().BeFalse();
        swept.Body.Should().Be("only part one of three");
        swept.Payload["source"].Should().Be("parts-sweep");
    }
}
