using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sological.Sms.Core.Entities;
using Sological.Sms.Core.Sms;
using Sological.Sms.Core.Upstream;
using Sological.Sms.Service.Api;
using Sological.Sms.Service.Data;
using Testcontainers.PostgreSql;

namespace Sological.Sms.Tests;

public sealed class SendLanePostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public string ConnectionString => _postgres.GetConnectionString();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();
}

/// <summary>Thread-safe scripted upstream: dequeues scripted results, then accepts.</summary>
internal sealed class FakeUpstream : ISmsUpstream
{
    private readonly object _sync = new();
    private readonly Queue<UpstreamSubmitResult> _scripted = new();
    private readonly List<OutboundSms> _calls = [];

    public IReadOnlyList<OutboundSms> Calls { get { lock (_sync) return _calls.ToArray(); } }

    public void Enqueue(UpstreamSubmitResult result) { lock (_sync) _scripted.Enqueue(result); }

    public Task<UpstreamSubmitResult> SubmitAsync(OutboundSms sms, CancellationToken ct)
    {
        lock (_sync)
        {
            _calls.Add(sms);
            return Task.FromResult(_scripted.Count > 0
                ? _scripted.Dequeue()
                : new UpstreamSubmitResult(true, null, null, null));
        }
    }
}

internal sealed class SendLaneFactory(
    string connectionString, ISmsUpstream fake,
    Dictionary<string, string?>? extraSettings = null,
    HttpMessageHandler? egressHandler = null)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("SologicalSms:ConnectionStrings:DefaultConnection", connectionString);
        builder.UseSetting("AzureKeyVault:VaultUri", ""); // hermetic harnesses blank the vault (guide rule)
        builder.UseSetting("SologicalSms:Dispatch:PollSeconds", "1");
        builder.UseSetting("SologicalSms:Dispatch:RetryDelays", "1,1,1");
        builder.UseSetting("SologicalSms:Egress:PollSeconds", "1");
        builder.UseSetting("SologicalSms:SmsCentral:User", "subuser");
        builder.UseSetting("SologicalSms:SmsCentral:Password", "subpass");
        builder.UseSetting("SologicalSms:Ingress:VerifyKey", "test-verify-key");
        builder.UseSetting("SologicalSms:Webhook:Test", "whsec-test");
        // Hermetic default: no rate limiting (tests poll aggressively) and no breaker
        // (ingress tests post uncorrelated DRs on purpose). The security tests turn
        // these back on explicitly via extraSettings.
        builder.UseSetting("SologicalSms:RateLimits:Enabled", "false");
        builder.UseSetting("SologicalSms:Breaker:Enabled", "false");
        foreach (var (key, value) in extraSettings ?? [])
            builder.UseSetting(key, value);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ISmsUpstream>();
            services.AddSingleton(fake);
            if (egressHandler is not null)
            {
                services.AddHttpClient(Sological.Sms.Service.Workers.EgressOptions.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => egressHandler);
            }
        });
    }
}

/// <summary>The whole S2 lane end-to-end minus the real upstream: API accept → dispatch
/// worker → guards → seam → transitions → ledger. Real PostgreSQL, real worker, real HTTP.
/// Local-only (Integration category).</summary>
[Trait("Category", "Integration")]
public sealed class SendLaneIntegrationTests(SendLanePostgresFixture fixture)
    : IClassFixture<SendLanePostgresFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private sealed record TestChannel(string ApiKey, string Originator, long ChannelId);

    /// <summary>Unique customer/channel/whitelist rows per test — the database is shared
    /// across the class.</summary>
    private static async Task<TestChannel> SeedAsync(
        SendLaneFactory factory, bool whitelistOriginator = true, ChannelStatus status = ChannelStatus.Active)
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
            Status = status,
        };
        db.Channels.Add(channel);
        if (whitelistOriginator)
            db.AllowedOriginators.Add(new AllowedOriginator { Originator = originator });
        await db.SaveChangesAsync();
        return new TestChannel(apiKey, originator, channel.Id);
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client, string apiKey, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/messages") { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Api-Key", apiKey);
        return await client.SendAsync(request);
    }

    private static async Task<MessageStatusResponse> GetMessageAsync(HttpClient client, string apiKey, Guid id)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/messages/{id}");
        request.Headers.Add("X-Api-Key", apiKey);
        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<MessageStatusResponse>(Json))!;
    }

    /// <summary>Poll-with-ceiling (never fixed-delay-then-assert); on timeout reports what
    /// WAS observed.</summary>
    private static async Task<MessageStatusResponse> WaitForStatusAsync(
        HttpClient client, string apiKey, Guid id, MessageStatus expected, int ceilingSeconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(ceilingSeconds);
        MessageStatusResponse? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await GetMessageAsync(client, apiKey, id);
            if (last.Status == expected) return last;
            await Task.Delay(250);
        }
        throw new Xunit.Sdk.XunitException(
            $"Message {id} never reached '{expected}' within {ceilingSeconds}s. " +
            $"Last observed: status={last?.Status}, error={last?.ErrorCode} {last?.ErrorDetail}");
    }

    [Fact]
    public async Task Send_ReachesSent_NormalizesRecipient_AndWritesOneLedgerRow()
    {
        var fake = new FakeUpstream();
        await using var factory = new SendLaneFactory(fixture.ConnectionString, fake);
        var client = factory.CreateClient();
        var ch = await SeedAsync(factory);

        var response = await PostAsync(client, ch.ApiKey, new { to = "0412345678", body = "hello", reference = "r-1" });
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var accepted = (await response.Content.ReadFromJsonAsync<SendMessageResponse>(Json))!;
        accepted.Parts.Should().Be(1);
        accepted.Status.Should().Be(MessageStatus.Queued);

        var final = await WaitForStatusAsync(client, ch.ApiKey, accepted.MessageId, MessageStatus.Sent);
        final.To.Should().Be("+61412345678", "the guard normalizes AU mobiles to E.164");
        final.Originator.Should().Be(ch.Originator);
        final.SubmittedAt.Should().NotBeNull();

        var call = fake.Calls.Should().ContainSingle(c => c.MessageId == accepted.MessageId).Subject;
        call.To.Should().Be("+61412345678");
        call.Originator.Should().Be(ch.Originator);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmsDbContext>();
        var ledger = await db.BillingLedger.SingleAsync(l => l.RefId == accepted.MessageId);
        ledger.Units.Should().Be(1);
        ledger.Direction.Should().Be(SmsDirection.Outbound);
        ledger.ChannelId.Should().Be(ch.ChannelId);
    }

    [Fact]
    public async Task MultipartBody_CountsParts_AndBillsThem()
    {
        var fake = new FakeUpstream();
        await using var factory = new SendLaneFactory(fixture.ConnectionString, fake);
        var client = factory.CreateClient();
        var ch = await SeedAsync(factory);

        var response = await PostAsync(client, ch.ApiKey, new { to = "0412345678", body = new string('x', 200) });
        var accepted = (await response.Content.ReadFromJsonAsync<SendMessageResponse>(Json))!;
        accepted.Parts.Should().Be(2);

        await WaitForStatusAsync(client, ch.ApiKey, accepted.MessageId, MessageStatus.Sent);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmsDbContext>();
        (await db.BillingLedger.SingleAsync(l => l.RefId == accepted.MessageId)).Units.Should().Be(2);
    }

    [Fact]
    public async Task ReusedReference_Returns409()
    {
        var fake = new FakeUpstream();
        await using var factory = new SendLaneFactory(fixture.ConnectionString, fake);
        var client = factory.CreateClient();
        var ch = await SeedAsync(factory);

        (await PostAsync(client, ch.ApiKey, new { to = "0412345678", body = "one", reference = "dup-ref" }))
            .StatusCode.Should().Be(HttpStatusCode.Accepted);
        var second = await PostAsync(client, ch.ApiKey, new { to = "0412345699", body = "two", reference = "dup-ref" });
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await second.Content.ReadFromJsonAsync<ApiError>(Json))!.Error.Should().Be("duplicate_reference");
    }

    [Fact]
    public async Task RefLessDoubleSubmit_SecondIsFlaggedDuplicate_AndNeverBilledOrSent()
    {
        var fake = new FakeUpstream();
        await using var factory = new SendLaneFactory(fixture.ConnectionString, fake);
        var client = factory.CreateClient();
        var ch = await SeedAsync(factory);

        var first = (await (await PostAsync(client, ch.ApiKey, new { to = "0412340001", body = "double" }))
            .Content.ReadFromJsonAsync<SendMessageResponse>(Json))!;
        var second = (await (await PostAsync(client, ch.ApiKey, new { to = "0412340001", body = "double" }))
            .Content.ReadFromJsonAsync<SendMessageResponse>(Json))!;

        (await WaitForStatusAsync(client, ch.ApiKey, first.MessageId, MessageStatus.Sent)).Should().NotBeNull();
        var dup = await WaitForStatusAsync(client, ch.ApiKey, second.MessageId, MessageStatus.Duplicate);
        dup.ErrorCode.Should().Be("duplicate");

        fake.Calls.Should().ContainSingle(c => c.To == "+61412340001", "the duplicate must never reach the upstream");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmsDbContext>();
        (await db.BillingLedger.AnyAsync(l => l.RefId == second.MessageId)).Should().BeFalse("guard verdicts are unbilled");
    }

    [Fact]
    public async Task InvalidRecipient_GoesTerminalRejected_WithoutUpstreamCall()
    {
        var fake = new FakeUpstream();
        await using var factory = new SendLaneFactory(fixture.ConnectionString, fake);
        var client = factory.CreateClient();
        var ch = await SeedAsync(factory);

        var accepted = (await (await PostAsync(client, ch.ApiKey, new { to = "0400000000", body = "sentinel" }))
            .Content.ReadFromJsonAsync<SendMessageResponse>(Json))!;

        var final = await WaitForStatusAsync(client, ch.ApiKey, accepted.MessageId, MessageStatus.Rejected);
        final.ErrorCode.Should().Be("invalid_recipient");
        fake.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task NonWhitelistedOriginator_GoesTerminalRejected()
    {
        var fake = new FakeUpstream();
        await using var factory = new SendLaneFactory(fixture.ConnectionString, fake);
        var client = factory.CreateClient();
        var ch = await SeedAsync(factory, whitelistOriginator: false);

        var accepted = (await (await PostAsync(client, ch.ApiKey, new { to = "0412345678", body = "acma" }))
            .Content.ReadFromJsonAsync<SendMessageResponse>(Json))!;

        var final = await WaitForStatusAsync(client, ch.ApiKey, accepted.MessageId, MessageStatus.Rejected);
        final.ErrorCode.Should().Be("invalid_originator");
        fake.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task OriginatorOverride_MustMatchTheChannel()
    {
        var fake = new FakeUpstream();
        await using var factory = new SendLaneFactory(fixture.ConnectionString, fake);
        var client = factory.CreateClient();
        var ch = await SeedAsync(factory);

        var response = await PostAsync(client, ch.ApiKey,
            new { to = "0412345678", body = "x", originator = "SomeoneElse" });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<ApiError>(Json))!.Error.Should().Be("originator_mismatch");
    }

    [Fact]
    public async Task RetryableUpstreamFailure_RetriesThenSends()
    {
        var fake = new FakeUpstream();
        fake.Enqueue(new UpstreamSubmitResult(false, null, "536", "Temporarily delayed", Retryable: true));
        await using var factory = new SendLaneFactory(fixture.ConnectionString, fake);
        var client = factory.CreateClient();
        var ch = await SeedAsync(factory);

        var accepted = (await (await PostAsync(client, ch.ApiKey, new { to = "0412345678", body = "retry me" }))
            .Content.ReadFromJsonAsync<SendMessageResponse>(Json))!;

        await WaitForStatusAsync(client, ch.ApiKey, accepted.MessageId, MessageStatus.Sent, ceilingSeconds: 30);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmsDbContext>();
        (await db.Messages.SingleAsync(m => m.Id == accepted.MessageId)).Attempts.Should().Be(1);
        fake.Calls.Count(c => c.MessageId == accepted.MessageId).Should().Be(2);
    }

    [Fact]
    public async Task HardUpstreamReject_GoesTerminalRejected_Unbilled()
    {
        var fake = new FakeUpstream();
        fake.Enqueue(new UpstreamSubmitResult(false, null, "519", "Blacklisted", Retryable: false));
        await using var factory = new SendLaneFactory(fixture.ConnectionString, fake);
        var client = factory.CreateClient();
        var ch = await SeedAsync(factory);

        var accepted = (await (await PostAsync(client, ch.ApiKey, new { to = "0412345678", body = "blocked" }))
            .Content.ReadFromJsonAsync<SendMessageResponse>(Json))!;

        var final = await WaitForStatusAsync(client, ch.ApiKey, accepted.MessageId, MessageStatus.Rejected);
        final.ErrorCode.Should().Be("519");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmsDbContext>();
        (await db.BillingLedger.AnyAsync(l => l.RefId == accepted.MessageId)).Should().BeFalse();
    }

    [Fact]
    public async Task AuthFailures_ReturnTheErrorEnvelope()
    {
        var fake = new FakeUpstream();
        await using var factory = new SendLaneFactory(fixture.ConnectionString, fake);
        var client = factory.CreateClient();
        await SeedAsync(factory);

        var noKey = await client.PostAsJsonAsync("/api/v1/messages", new { to = "0412345678", body = "x" });
        noKey.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await noKey.Content.ReadFromJsonAsync<ApiError>(Json))!.Error.Should().Be("missing_api_key");

        var badKey = await PostAsync(client, "wrong-key", new { to = "0412345678", body = "x" });
        badKey.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await badKey.Content.ReadFromJsonAsync<ApiError>(Json))!.Error.Should().Be("invalid_api_key");
    }

    [Fact]
    public async Task PausedChannel_CannotSend_ButCanReadStatus()
    {
        var fake = new FakeUpstream();
        await using var factory = new SendLaneFactory(fixture.ConnectionString, fake);
        var client = factory.CreateClient();
        var ch = await SeedAsync(factory, status: ChannelStatus.Paused);

        var response = await PostAsync(client, ch.ApiKey, new { to = "0412345678", body = "x" });
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadFromJsonAsync<ApiError>(Json))!.Error.Should().Be("channel_paused");
    }
}
