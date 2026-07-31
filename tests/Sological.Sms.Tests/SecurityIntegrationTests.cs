using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sological.Sms.Core.Entities;
using Sological.Sms.Core.Sms;
using Sological.Sms.Service.Api;
using Sological.Sms.Service.Data;

namespace Sological.Sms.Tests;

/// <summary>The public-surface containment slice (2026-07-31): recipient allowlist,
/// daily part quota, per-key rate limiting, unknown-DR breaker. Real PostgreSQL, real
/// workers. Local-only (Integration category).</summary>
[Trait("Category", "Integration")]
public sealed class SecurityIntegrationTests(SendLanePostgresFixture fixture)
    : IClassFixture<SendLanePostgresFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private sealed record TestChannel(string ApiKey, string Originator, long ChannelId);

    private static async Task<TestChannel> SeedAsync(
        SendLaneFactory factory, int? dailyPartLimit = null, string[]? allowedRecipients = null)
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
            DailyPartLimit = dailyPartLimit,
            AllowedRecipients = allowedRecipients,
        };
        db.Channels.Add(channel);
        db.AllowedOriginators.Add(new AllowedOriginator { Originator = originator });
        await db.SaveChangesAsync();
        return new TestChannel(apiKey, originator, channel.Id);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string apiKey, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/messages") { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Api-Key", apiKey);
        return await client.SendAsync(request);
    }

    private static async Task<MessageStatusResponse> WaitForStatusAsync(
        HttpClient client, string apiKey, Guid id, MessageStatus expected, int ceilingSeconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(ceilingSeconds);
        MessageStatusResponse? last = null;
        while (DateTime.UtcNow < deadline)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/messages/{id}");
            request.Headers.Add("X-Api-Key", apiKey);
            var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            last = await response.Content.ReadFromJsonAsync<MessageStatusResponse>(Json);
            if (last!.Status == expected) return last;
            await Task.Delay(250);
        }
        throw new Xunit.Sdk.XunitException(
            $"Message {id} never reached '{expected}' within {ceilingSeconds}s. " +
            $"Last observed: status={last?.Status}, error={last?.ErrorCode} {last?.ErrorDetail}");
    }

    [Fact]
    public async Task RecipientOutsideAllowlist_GoesTerminalRejected_AllowedOnePasses()
    {
        var fake = new FakeUpstream();
        await using var factory = new SendLaneFactory(fixture.ConnectionString, fake);
        var client = factory.CreateClient();
        var ch = await SeedAsync(factory, allowedRecipients: ["+61408004199"]);

        var blocked = (await (await PostAsync(client, ch.ApiKey, new { to = "0499111222", body = "outside" }))
            .Content.ReadFromJsonAsync<SendMessageResponse>(Json))!;
        var allowed = (await (await PostAsync(client, ch.ApiKey, new { to = "0408004199", body = "inside" }))
            .Content.ReadFromJsonAsync<SendMessageResponse>(Json))!;

        var rejected = await WaitForStatusAsync(client, ch.ApiKey, blocked.MessageId, MessageStatus.Rejected);
        rejected.ErrorCode.Should().Be("recipient_not_allowed");

        await WaitForStatusAsync(client, ch.ApiKey, allowed.MessageId, MessageStatus.Sent);
        fake.Calls.Should().ContainSingle("only the allowlisted recipient may reach the upstream")
            .Which.To.Should().Be("+61408004199");
    }

    [Fact]
    public async Task DailyPartQuota_RejectsBeyondLimit_Unbilled()
    {
        var fake = new FakeUpstream();
        await using var factory = new SendLaneFactory(fixture.ConnectionString, fake);
        var client = factory.CreateClient();
        var ch = await SeedAsync(factory, dailyPartLimit: 1);

        var first = (await (await PostAsync(client, ch.ApiKey, new { to = "0412000001", body = "one" }))
            .Content.ReadFromJsonAsync<SendMessageResponse>(Json))!;
        await WaitForStatusAsync(client, ch.ApiKey, first.MessageId, MessageStatus.Sent);

        var second = (await (await PostAsync(client, ch.ApiKey, new { to = "0412000002", body = "two" }))
            .Content.ReadFromJsonAsync<SendMessageResponse>(Json))!;
        var rejected = await WaitForStatusAsync(client, ch.ApiKey, second.MessageId, MessageStatus.Rejected);
        rejected.ErrorCode.Should().Be("quota_exceeded");

        fake.Calls.Should().ContainSingle(c => c.MessageId == first.MessageId);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmsDbContext>();
        (await db.BillingLedger.AnyAsync(l => l.RefId == second.MessageId)).Should().BeFalse("quota verdicts are unbilled");
    }

    [Fact]
    public async Task SendFlood_HitsTokenBucket_429WithEnvelope()
    {
        var fake = new FakeUpstream();
        await using var factory = new SendLaneFactory(fixture.ConnectionString, fake, new Dictionary<string, string?>
        {
            ["SologicalSms:RateLimits:Enabled"] = "true",
            ["SologicalSms:RateLimits:SendPerSecond"] = "1",
            ["SologicalSms:RateLimits:SendBurst"] = "1",
            ["SologicalSms:RateLimits:GlobalPerMinute"] = "100000",
            ["SologicalSms:RateLimits:IngressPerMinute"] = "100000",
        });
        var client = factory.CreateClient();
        var ch = await SeedAsync(factory);

        var statuses = new List<HttpStatusCode>();
        HttpResponseMessage? limited = null;
        for (var i = 0; i < 5; i++)
        {
            var response = await PostAsync(client, ch.ApiKey, new { to = "0412345678", body = $"flood {i}" });
            statuses.Add(response.StatusCode);
            if (response.StatusCode == (HttpStatusCode)429) limited ??= response;
        }

        statuses.Should().Contain(HttpStatusCode.Accepted, "the bucket admits the burst");
        statuses.Should().Contain((HttpStatusCode)429, "the flood beyond the bucket is shed");
        limited!.Headers.RetryAfter.Should().NotBeNull();
        (await limited.Content.ReadFromJsonAsync<ApiError>(Json))!.Error.Should().Be("rate_limited");
    }

    [Fact]
    public async Task UnknownDrFlood_TripsBreaker_PausesChannels_OneAlertOnly()
    {
        var fake = new FakeUpstream();
        await using var factory = new SendLaneFactory(fixture.ConnectionString, fake, new Dictionary<string, string?>
        {
            ["SologicalSms:Breaker:Enabled"] = "true",
            ["SologicalSms:Breaker:UnmatchedThreshold"] = "3",
            ["SologicalSms:Breaker:WindowMinutes"] = "5",
            ["SologicalSms:Breaker:AlertNumber"] = "+61408004199",
            ["SologicalSms:Breaker:AlertOriginator"] = "TSALERT",
        });
        var client = factory.CreateClient();
        var ch = await SeedAsync(factory);

        for (var i = 0; i < 4; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/ingress/smscentral/delivery")
            {
                Content = JsonContent.Create(new
                {
                    dtId = Guid.NewGuid().ToString(),
                    mtId = Guid.NewGuid().ToString(),
                    status = "delivered",
                    mtContent = "a send this service never made",
                    sourceAddress = "+61400777888",
                }),
            };
            request.Headers.Add("SLVERIFY", "test-verify-key");
            (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmsDbContext>();

        var breaker = await db.UpstreamBreakers.SingleAsync(b => b.ReArmedAt == null);
        breaker.UnmatchedCount.Should().BeGreaterThanOrEqualTo(3);
        breaker.AlertSentAt.Should().NotBeNull("the one-shot alert must be attempted");
        breaker.AlertError.Should().BeNull("the fake upstream accepts");

        (await db.Channels.SingleAsync(c => c.Id == ch.ChannelId)).Status
            .Should().Be(ChannelStatus.Paused, "the breaker pauses every active channel on the upstream");

        var alerts = fake.Calls.Where(c => c.To == "+61408004199").ToList();
        alerts.Should().ContainSingle("the latch makes the alert one-shot — 4th unmatched DR must not re-alert");
        alerts[0].Originator.Should().Be("TSALERT");
        alerts[0].Body.Should().Contain("BREAKER TRIPPED");
    }
}
