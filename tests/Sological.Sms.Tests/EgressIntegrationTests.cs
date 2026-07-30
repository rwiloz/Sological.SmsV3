using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
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

public sealed class EgressPostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public string ConnectionString => _postgres.GetConnectionString();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();
}

/// <summary>Records every webhook POST the egress worker makes; scripted status code.</summary>
internal sealed class FakeWebhookSink(HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
{
    public sealed record Delivery(Uri Url, string Signature, string Body);

    private readonly object _sync = new();
    private readonly List<Delivery> _deliveries = [];

    public IReadOnlyList<Delivery> Deliveries { get { lock (_sync) return _deliveries.ToArray(); } }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var signature = request.Headers.TryGetValues("X-Sms-Signature", out var values) ? values.First() : "";
        lock (_sync) _deliveries.Add(new Delivery(request.RequestUri!, signature, body));
        return new HttpResponseMessage(status);
    }
}

/// <summary>S4's egress lane end-to-end: state transition → outbox row (same transaction)
/// → claim → HMAC-signed POST → delivered/dead. Local-only (Integration category).</summary>
[Trait("Category", "Integration")]
public sealed class EgressIntegrationTests(EgressPostgresFixture fixture)
    : IClassFixture<EgressPostgresFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private sealed record TestChannel(string ApiKey, string Originator, long ChannelId);

    private static async Task<TestChannel> SeedAsync(SendLaneFactory factory, bool withWebhook = true)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var apiKey = $"key-{suffix}";
        var originator = $"TS{suffix[..6]}";

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmsDbContext>();
        var channel = new Channel
        {
            Customer = new Customer { Code = $"c{suffix}", Name = $"Test {suffix}" },
            Key = $"ch-{suffix}",
            Originator = originator,
            ApiKey1Hash = ApiKeyHasher.Sha256Hex(apiKey),
            WebhookUrl = withWebhook ? $"https://customer.example/hooks/{suffix}" : null,
            WebhookSecretName = withWebhook ? "SologicalSms:Webhook:Test" : null,
        };
        db.Channels.Add(channel);
        db.AllowedOriginators.Add(new AllowedOriginator { Originator = originator });
        await db.SaveChangesAsync();
        return new TestChannel(apiKey, originator, channel.Id);
    }

    private static async Task<Guid> SendAsync(HttpClient client, string apiKey, string to, string body, string? reference = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/messages")
        {
            Content = JsonContent.Create(new { to, body, reference }),
        };
        request.Headers.Add("X-Api-Key", apiKey);
        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        return (await response.Content.ReadFromJsonAsync<SendMessageResponse>(Json))!.MessageId;
    }

    private static async Task<IReadOnlyList<FakeWebhookSink.Delivery>> WaitForDeliveriesAsync(
        FakeWebhookSink sink, int count, int ceilingSeconds = 25)
    {
        var deadline = DateTime.UtcNow.AddSeconds(ceilingSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var seen = sink.Deliveries;
            if (seen.Count >= count) return seen;
            await Task.Delay(250);
        }
        throw new Xunit.Sdk.XunitException(
            $"Expected {count} webhook deliveries within {ceilingSeconds}s; observed {sink.Deliveries.Count}: " +
            string.Join(" | ", sink.Deliveries.Select(d => d.Body.Substring(0, Math.Min(80, d.Body.Length)))));
    }

    private static string Sign(string body)
        => "hmac-sha256=" + Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes("whsec-test"), Encoding.UTF8.GetBytes(body)));

    [Fact]
    public async Task SentTransition_EmitsSignedDeliveryEvent()
    {
        var fake = new FakeUpstream();
        var sink = new FakeWebhookSink();
        await using var factory = new SendLaneFactory(fixture.ConnectionString, fake, egressHandler: sink);
        var client = factory.CreateClient();
        var ch = await SeedAsync(factory);

        var id = await SendAsync(client, ch.ApiKey, "0413000001", "egress hello", "eg-1");

        var deliveries = await WaitForDeliveriesAsync(sink, 1);
        var d = deliveries[0];
        d.Url.ToString().Should().StartWith("https://customer.example/hooks/");
        d.Signature.Should().Be(Sign(d.Body), "the signature covers the exact bytes sent");

        using var doc = JsonDocument.Parse(d.Body);
        doc.RootElement.GetProperty("event").GetString().Should().Be("sms.delivery");
        doc.RootElement.GetProperty("messageId").GetGuid().Should().Be(id);
        doc.RootElement.GetProperty("status").GetString().Should().Be("sent");
        doc.RootElement.GetProperty("reference").GetString().Should().Be("eg-1");
        doc.RootElement.GetProperty("to").GetString().Should().Be("+61413000001");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmsDbContext>();
        (await db.WebhookOutbox.SingleAsync(e => e.ChannelId == ch.ChannelId)).State
            .Should().Be(WebhookOutboxState.Delivered);
    }

    [Fact]
    public async Task DlrDelivered_EmitsSecondEvent()
    {
        var fake = new FakeUpstream();
        var sink = new FakeWebhookSink();
        await using var factory = new SendLaneFactory(fixture.ConnectionString, fake, egressHandler: sink);
        var client = factory.CreateClient();
        var ch = await SeedAsync(factory);

        var id = await SendAsync(client, ch.ApiKey, "0413000002", "egress dlr", "eg-2");
        await WaitForDeliveriesAsync(sink, 1);

        var dlr = new HttpRequestMessage(HttpMethod.Get,
            $"/ingress/smscentral/delivery?REFERENCE={id:N}&ID=eg-dlr-1&RESULT=1&STATUS=DELIVRD");
        (await client.SendAsync(dlr)).StatusCode.Should().Be(HttpStatusCode.OK);

        var deliveries = await WaitForDeliveriesAsync(sink, 2);
        using var doc = JsonDocument.Parse(deliveries[1].Body);
        doc.RootElement.GetProperty("status").GetString().Should().Be("delivered");
        doc.RootElement.GetProperty("messageId").GetGuid().Should().Be(id);
    }

    [Fact]
    public async Task Inbound_EmitsEventWithReplyTo_AndStampsDeliveredToCustomer()
    {
        var fake = new FakeUpstream();
        var sink = new FakeWebhookSink();
        await using var factory = new SendLaneFactory(fixture.ConnectionString, fake, egressHandler: sink);
        var client = factory.CreateClient();
        var ch = await SeedAsync(factory);

        var id = await SendAsync(client, ch.ApiKey, "0413000003", "expect reply", "eg-3");
        await WaitForDeliveriesAsync(sink, 1); // the sent event

        var push = new HttpRequestMessage(HttpMethod.Get,
            $"/ingress/smscentral/inbound?REFERENCE={id:N}&ORIGINATOR=61413000003&RECIPIENT=61499111444&MESSAGE_TEXT=roger&ID=eg-in-1");
        (await client.SendAsync(push)).StatusCode.Should().Be(HttpStatusCode.OK);

        var deliveries = await WaitForDeliveriesAsync(sink, 2);
        using var doc = JsonDocument.Parse(deliveries[1].Body);
        doc.RootElement.GetProperty("event").GetString().Should().Be("sms.inbound");
        doc.RootElement.GetProperty("body").GetString().Should().Be("roger");
        doc.RootElement.GetProperty("replyTo").GetProperty("messageId").GetGuid().Should().Be(id);
        doc.RootElement.GetProperty("replyTo").GetProperty("reference").GetString().Should().Be("eg-3");
        var inboundId = doc.RootElement.GetProperty("inboundId").GetGuid();

        var deadline = DateTime.UtcNow.AddSeconds(15);
        DateTimeOffset? stamped = null;
        while (DateTime.UtcNow < deadline && stamped is null)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SmsDbContext>();
            stamped = (await db.InboundMessages.SingleAsync(m => m.Id == inboundId)).DeliveredToCustomerAt;
            if (stamped is null) await Task.Delay(250);
        }
        stamped.Should().NotBeNull("delivered_to_customer_at is set by egress; NULL = owed");
    }

    [Fact]
    public async Task FailingSink_RetriesWithBackoff_ThenDead_Loudly()
    {
        var fake = new FakeUpstream();
        var sink = new FakeWebhookSink(HttpStatusCode.InternalServerError);
        await using var factory = new SendLaneFactory(fixture.ConnectionString, fake,
            new Dictionary<string, string?> { ["SologicalSms:Egress:RetryDelays"] = "1,1" },
            egressHandler: sink);
        var client = factory.CreateClient();
        var ch = await SeedAsync(factory);

        await SendAsync(client, ch.ApiKey, "0413000004", "doomed egress", "eg-4");

        var deadline = DateTime.UtcNow.AddSeconds(25);
        WebhookOutboxEntry? entry = null;
        while (DateTime.UtcNow < deadline)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SmsDbContext>();
            entry = await db.WebhookOutbox.SingleOrDefaultAsync(e => e.ChannelId == ch.ChannelId);
            if (entry?.State == WebhookOutboxState.Dead) break;
            await Task.Delay(250);
        }

        entry.Should().NotBeNull();
        entry!.State.Should().Be(WebhookOutboxState.Dead, "exhausted retries go dead — visible, never silent");
        entry.Attempts.Should().Be(3, "initial attempt + two retries");
        sink.Deliveries.Count.Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task ChannelWithoutWebhook_EnqueuesNothing()
    {
        var fake = new FakeUpstream();
        var sink = new FakeWebhookSink();
        await using var factory = new SendLaneFactory(fixture.ConnectionString, fake, egressHandler: sink);
        var client = factory.CreateClient();
        var ch = await SeedAsync(factory, withWebhook: false);

        var id = await SendAsync(client, ch.ApiKey, "0413000005", "unsubscribed", "eg-5");

        // Poll the message to sent, then confirm no outbox row ever appeared.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/messages/{id}");
            req.Headers.Add("X-Api-Key", ch.ApiKey);
            var status = (await (await client.SendAsync(req)).Content.ReadFromJsonAsync<MessageStatusResponse>(Json))!;
            if (status.Status == MessageStatus.Sent) break;
            await Task.Delay(250);
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmsDbContext>();
        (await db.WebhookOutbox.AnyAsync(e => e.ChannelId == ch.ChannelId)).Should().BeFalse();
    }

    [Fact]
    public async Task InboundPollEndpoint_ReturnsChannelScopedRows()
    {
        var fake = new FakeUpstream();
        var sink = new FakeWebhookSink();
        await using var factory = new SendLaneFactory(fixture.ConnectionString, fake, egressHandler: sink);
        var client = factory.CreateClient();
        var ch = await SeedAsync(factory);

        var id = await SendAsync(client, ch.ApiKey, "0413000006", "poll parity", "eg-6");
        await WaitForDeliveriesAsync(sink, 1);
        var push = new HttpRequestMessage(HttpMethod.Get,
            $"/ingress/smscentral/inbound?REFERENCE={id:N}&ORIGINATOR=61413000006&MESSAGE_TEXT=polled&ID=eg-in-2");
        await client.SendAsync(push);

        var list = new HttpRequestMessage(HttpMethod.Get, "/api/v1/inbound?since=" + Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O")));
        list.Headers.Add("X-Api-Key", ch.ApiKey);
        var response = await client.SendAsync(list);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = (await response.Content.ReadFromJsonAsync<List<InboundListItem>>(Json))!;
        items.Should().ContainSingle(i => i.Body == "polled" && i.ReplyToMessageId == id);
    }
}
