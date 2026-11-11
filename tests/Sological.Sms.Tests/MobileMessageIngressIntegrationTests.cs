using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sological.Sms.Core.Entities;
using Sological.Sms.Core.Sms;
using Sological.Sms.Core.Upstream;
using Sological.Sms.Service.Api;
using Sological.Sms.Service.Data;
using Sological.Sms.Service.Ingress;

namespace Sological.Sms.Tests;

/// <summary>Design §5.3 end to end: Mobile Message receipts and replies as the legacy router forwards them
/// (MM's JSON body and X-MM-* headers, untouched), real PostgreSQL, real endpoints. Local-only.</summary>
[Trait("Category", "Integration")]
public sealed class MobileMessageIngressIntegrationTests(IngressPostgresFixture fixture)
    : IClassFixture<IngressPostgresFixture>
{
    private const string Secret = "mm-test-signing-secret";
    private const string StatusPath = "/ingress/mobilemessage/status";
    private const string InboundPath = "/ingress/mobilemessage/inbound";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private sealed record TestChannel(string ApiKey, string Originator, long ChannelId);

    private SendLaneFactory Factory(FakeUpstream fake, string? signingSecret = Secret) => new(fixture.ConnectionString, fake,
        new Dictionary<string, string?>
        {
            ["SologicalSms:MobileMessage:EnvironmentTag"] = "dev",
            ["SologicalSms:MobileMessage:SigningSecret"] = signingSecret ?? "",
        });

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

    private static async Task<Guid> SendToSentAsync(HttpClient client, TestChannel ch, string to, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/messages") { Content = JsonContent.Create(new { to, body }) };
        request.Headers.Add("X-Api-Key", ch.ApiKey);
        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var accepted = (await response.Content.ReadFromJsonAsync<SendMessageResponse>(Json))!;

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if ((await GetStatusAsync(client, ch.ApiKey, accepted.MessageId)).Status == MessageStatus.Sent)
                return accepted.MessageId;
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

    /// <summary>POSTs the body exactly as MM signs it: HMAC over `{timestamp}.{raw bytes}`.</summary>
    private static async Task<HttpStatusCode> PostSignedAsync(
        HttpClient client, string path, string body, string secret = Secret, DateTimeOffset? at = null)
    {
        var timestamp = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds().ToString();
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add(MobileMessageWebhook.TimestampHeader, timestamp);
        request.Headers.Add(MobileMessageWebhook.SignatureHeader,
            MobileMessageWebhook.ComputeSignature(secret, timestamp, Encoding.UTF8.GetBytes(body)));
        return (await client.SendAsync(request)).StatusCode;
    }

    private static string Receipt(string customRef, string status, string messageId, int part = 1, int total = 1) =>
        JsonSerializer.Serialize(new
        {
            to = "61412000001", message = "the text", sender = "SoLogical", custom_ref = customRef, status,
            message_id = messageId, received_at = "2026-11-11 01:02:03", part_number = part, total_parts = total,
        });

    private static string Reply(string to, string sender, string message, string originalRef = "", string originalId = "",
        string type = "inbound", string receivedAt = "2026-11-11 02:03:04") =>
        JsonSerializer.Serialize(new
        {
            to, message, sender, received_at = receivedAt, type, original_message_id = originalId, original_custom_ref = originalRef,
        });

    private static async Task<T> InScopeAsync<T>(SendLaneFactory factory, Func<SmsDbContext, Task<T>> read)
    {
        using var scope = factory.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<SmsDbContext>());
    }

    // ── Delivery receipts ────────────────────────────────────────────────────

    [Fact]
    public async Task Receipt_Delivered_TransitionsTheMessage_BackfillsMmsId_AndRecordsTheProvider()
    {
        var fake = new FakeUpstream();
        await using var factory = Factory(fake);
        var client = factory.CreateClient();
        var ch = await SeedAsync(factory);
        var id = await SendToSentAsync(client, ch, "0412000101", "mm delivered");

        (await PostSignedAsync(client, StatusPath, Receipt($"dev:{id:N}", "delivered", "mm-101"))).Should().Be(HttpStatusCode.OK);

        var status = await GetStatusAsync(client, ch.ApiKey, id);
        status.Status.Should().Be(MessageStatus.Delivered);
        var message = await InScopeAsync(factory, db => db.Messages.SingleAsync(m => m.Id == id));
        message.UpstreamId.Should().Be("mm-101");
        message.Upstream.Should().Be(UpstreamProvider.MobileMessage, "the provider actually used is recorded at submit");
        var events = await InScopeAsync(factory, db => db.DeliveryEvents.Where(e => e.MessageId == id).ToListAsync());
        events.Should().ContainSingle().Which.Provider.Should().Be("mobilemessage");
    }

    [Fact]
    public async Task Receipt_Multipart_DeliversWhenEveryPartConfirms_AndARetryIsDeduped()
    {
        var fake = new FakeUpstream();
        await using var factory = Factory(fake);
        var client = factory.CreateClient();
        var ch = await SeedAsync(factory);
        var id = await SendToSentAsync(client, ch, "0412000102", new string('m', 400)); // 3 parts
        var customRef = $"dev:{id:N}";

        (await PostSignedAsync(client, StatusPath, Receipt(customRef, "delivered", "mm-102", 2, 3))).Should().Be(HttpStatusCode.OK);
        (await PostSignedAsync(client, StatusPath, Receipt(customRef, "delivered", "mm-102", 2, 3))).Should().Be(HttpStatusCode.OK); // MM retry
        (await PostSignedAsync(client, StatusPath, Receipt(customRef, "delivered", "mm-102", 1, 3))).Should().Be(HttpStatusCode.OK);
        (await GetStatusAsync(client, ch.ApiKey, id)).Status.Should().Be(MessageStatus.Sent, "2 of 3 parts confirmed, out of order");

        (await PostSignedAsync(client, StatusPath, Receipt(customRef, "delivered", "mm-102", 3, 3))).Should().Be(HttpStatusCode.OK);
        (await GetStatusAsync(client, ch.ApiKey, id)).Status.Should().Be(MessageStatus.Delivered);
        (await InScopeAsync(factory, db => db.DeliveryEvents.CountAsync(e => e.MessageId == id)))
            .Should().Be(3, "the retried part is deduped before the ack");
    }

    [Fact]
    public async Task Receipt_AFailedPart_FailsTheMessage()
    {
        var fake = new FakeUpstream();
        await using var factory = Factory(fake);
        var client = factory.CreateClient();
        var ch = await SeedAsync(factory);
        var id = await SendToSentAsync(client, ch, "0412000103", new string('f', 200)); // 2 parts
        var customRef = $"dev:{id:N}";

        await PostSignedAsync(client, StatusPath, Receipt(customRef, "delivered", "mm-103", 1, 2));
        await PostSignedAsync(client, StatusPath, Receipt(customRef, "failed", "mm-103", 2, 2));

        var status = await GetStatusAsync(client, ch.ApiKey, id);
        status.Status.Should().Be(MessageStatus.Failed);
        status.ErrorCode.Should().Be("failed");
        status.ErrorDetail.Should().Be("part 2 of 2 failed (Mobile Message receipt)");
    }

    [Fact]
    public async Task Receipt_UnderAnotherEnvironmentsTag_IsAuditOnly_AndAcked()
    {
        var fake = new FakeUpstream();
        await using var factory = Factory(fake);
        var client = factory.CreateClient();
        var ch = await SeedAsync(factory);
        var id = await SendToSentAsync(client, ch, "0412000104", "mine, but tagged local");

        (await PostSignedAsync(client, StatusPath, Receipt($"local:{id:N}", "delivered", "mm-104"))).Should().Be(HttpStatusCode.OK);

        (await GetStatusAsync(client, ch.ApiKey, id)).Status.Should().Be(MessageStatus.Sent, "a misrouted receipt never moves a message");
        var orphans = await InScopeAsync(factory, db => db.DeliveryEvents.Where(e => e.MessageId == null && e.Provider == "mobilemessage").ToListAsync());
        orphans.Should().Contain(e => e.Payload.GetValueOrDefault("message_id") == "mm-104");
    }

    [Fact]
    public async Task Receipt_ForAnUnknownMessage_IsAuditOnly_AndNever500s()
    {
        var fake = new FakeUpstream();
        await using var factory = Factory(fake);
        var client = factory.CreateClient();

        (await PostSignedAsync(client, StatusPath, Receipt($"dev:{Guid.NewGuid():N}", "delivered", "mm-105")))
            .Should().Be(HttpStatusCode.OK, "a reset database must not make MM retry for hours");
        var orphans = await InScopeAsync(factory, db => db.DeliveryEvents.Where(e => e.MessageId == null && e.Provider == "mobilemessage").ToListAsync());
        orphans.Should().Contain(e => e.Payload.GetValueOrDefault("message_id") == "mm-105");
    }

    // ── Signature ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Webhook_WithAWrongOrStaleSignature_IsRefused_AndChangesNothing()
    {
        var fake = new FakeUpstream();
        await using var factory = Factory(fake);
        var client = factory.CreateClient();
        var ch = await SeedAsync(factory);
        var id = await SendToSentAsync(client, ch, "0412000106", "forged receipt");
        var receipt = Receipt($"dev:{id:N}", "delivered", "mm-106");

        (await PostSignedAsync(client, StatusPath, receipt, secret: "not-the-secret")).Should().Be(HttpStatusCode.Unauthorized);
        (await PostSignedAsync(client, StatusPath, receipt, at: DateTimeOffset.UtcNow.AddMinutes(-6))).Should().Be(HttpStatusCode.Unauthorized);
        var unsigned = await client.PostAsync(StatusPath, new StringContent(receipt, Encoding.UTF8, "application/json"));
        unsigned.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await GetStatusAsync(client, ch.ApiKey, id)).Status.Should().Be(MessageStatus.Sent);
        (await InScopeAsync(factory, db => db.DeliveryEvents.CountAsync(e => e.MessageId == id))).Should().Be(0);
    }

    [Fact]
    public async Task Webhook_WithNoSigningSecretConfigured_Answers503_SoMmRetriesThroughTheFix()
    {
        var fake = new FakeUpstream();
        await using var factory = Factory(fake, signingSecret: null);
        var client = factory.CreateClient();

        (await PostSignedAsync(client, StatusPath, Receipt($"dev:{Guid.NewGuid():N}", "delivered", "mm-107")))
            .Should().Be(HttpStatusCode.ServiceUnavailable);
        (await PostSignedAsync(client, InboundPath, Reply("61400000107", "61412000107", "hello")))
            .Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    // ── Replies ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reply_CorrelatesByOriginalCustomRef_AndARetryIsDeduped()
    {
        var fake = new FakeUpstream();
        await using var factory = Factory(fake);
        var client = factory.CreateClient();
        var ch = await SeedAsync(factory);
        var id = await SendToSentAsync(client, ch, "0412000108", "reply to me");
        var reply = Reply("61499000108", "61412000108", "yes please", originalRef: $"dev:{id:N}");

        (await PostSignedAsync(client, InboundPath, reply)).Should().Be(HttpStatusCode.OK);
        (await PostSignedAsync(client, InboundPath, reply)).Should().Be(HttpStatusCode.OK); // MM retry

        var inbound = await InScopeAsync(factory, db => db.InboundMessages.Where(m => m.ReplyToMessageId == id).ToListAsync());
        var stored = inbound.Should().ContainSingle("the retry is deduped on the reply's digest").Subject;
        stored.ChannelId.Should().Be(ch.ChannelId);
        stored.Body.Should().Be("yes please");
        stored.FromNumber.Should().Be("61412000108", "MM's `sender` is the handset");
        stored.ToNumber.Should().Be("61499000108");
        stored.UpstreamId.Should().StartWith("mm:");
    }

    [Fact]
    public async Task Reply_CorrelatesByOriginalMessageId_WhenTheRefIsMissing()
    {
        var fake = new FakeUpstream();
        fake.Enqueue(new UpstreamSubmitResult(true, "mm-109", null, null));
        await using var factory = Factory(fake);
        var client = factory.CreateClient();
        var ch = await SeedAsync(factory);
        var id = await SendToSentAsync(client, ch, "0412000109", "reply by id");

        await PostSignedAsync(client, InboundPath, Reply("61499000109", "61412000109", "by id", originalId: "mm-109"));

        var stored = await InScopeAsync(factory, db => db.InboundMessages.SingleAsync(m => m.ReplyToMessageId == id));
        stored.ChannelId.Should().Be(ch.ChannelId);
    }

    [Fact]
    public async Task Reply_ToADedicatedNumber_RoutesByTheNumber_EvenWhenItAnswersAnotherEnvironmentsSend()
    {
        var fake = new FakeUpstream();
        await using var factory = Factory(fake);
        var client = factory.CreateClient();
        var ch = await SeedAsync(factory, numericOriginator: "0499000110");

        await PostSignedAsync(client, InboundPath,
            Reply("61499000110", "61412000110", "texted the number", originalRef: $"local:{Guid.NewGuid():N}"));

        var stored = await InScopeAsync(factory, db => db.InboundMessages.SingleAsync(m => m.Body == "texted the number"));
        stored.ChannelId.Should().Be(ch.ChannelId);
        stored.ReplyToMessageId.Should().BeNull();
    }

    [Fact]
    public async Task Reply_ThatNothingClaims_IsQuarantinedOnTheOperatorChannel()
    {
        var fake = new FakeUpstream();
        await using var factory = Factory(fake);
        var client = factory.CreateClient();

        (await PostSignedAsync(client, InboundPath, Reply("61400000111", "61412000111", "lost soul"))).Should().Be(HttpStatusCode.OK);

        var operatorId = await InScopeAsync(factory, db => db.Channels.Where(c => c.Key == SystemChannels.OperatorKey).Select(c => c.Id).SingleAsync());
        var stored = await InScopeAsync(factory, db => db.InboundMessages.SingleAsync(m => m.Body == "lost soul"));
        stored.ChannelId.Should().Be(operatorId, "never dropped, never 500");
    }

    [Fact]
    public async Task Reply_ThatOptsOut_IsStoredAndForwardedLikeAnyReply()
    {
        var fake = new FakeUpstream();
        await using var factory = Factory(fake);
        var client = factory.CreateClient();
        var ch = await SeedAsync(factory);
        var id = await SendToSentAsync(client, ch, "0412000112", "opt me out");

        await PostSignedAsync(client, InboundPath,
            Reply("61499000112", "61412000112", "STOP", originalRef: $"dev:{id:N}", type: "unsubscribe"));

        var stored = await InScopeAsync(factory, db => db.InboundMessages.SingleAsync(m => m.ReplyToMessageId == id));
        stored.Body.Should().Be("STOP");
        stored.Payload.GetValueOrDefault("type").Should().Be("unsubscribe", "the opt-out is visible in the stored payload");
    }

    [Fact]
    public async Task Reply_WithARawNewlineInsideTheJsonString_IsRepairedAndStored()
    {
        var fake = new FakeUpstream();
        await using var factory = Factory(fake);
        var client = factory.CreateClient();
        var ch = await SeedAsync(factory, numericOriginator: "0499000113");

        var body = "{\"to\":\"61499000113\",\"message\":\"two\nlines\",\"sender\":\"61412000113\",\"received_at\":\"2026-11-11 03:04:05\",\"type\":\"inbound\",\"original_message_id\":\"\",\"original_custom_ref\":\"\"}";
        (await PostSignedAsync(client, InboundPath, body)).Should().Be(HttpStatusCode.OK, "the signature covers the raw bytes, the repair only the parse");

        var stored = await InScopeAsync(factory, db => db.InboundMessages.SingleAsync(m => m.ChannelId == ch.ChannelId));
        stored.Body.Should().Be("two\nlines");
    }
}
