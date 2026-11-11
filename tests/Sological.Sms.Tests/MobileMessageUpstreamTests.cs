using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sological.Sms.Core.Entities;
using Sological.Sms.Core.Upstream;
using Sological.Sms.Service.Ingress;
using Sological.Sms.Service.Upstream;

namespace Sological.Sms.Tests;

/// <summary>Design §4.2: the Mobile Message request shape and result mapping, against a stub HTTP handler.
/// Never the real API (standing rule).</summary>
public class MobileMessageUpstreamTests
{
    private static readonly Guid MessageId = Guid.Parse("0198d2c8-5a5a-7000-8000-000000000001");
    private static readonly OutboundSms Sms = new(MessageId, "SoLogical", "+61412345678", "hello world");

    private sealed class StubHandler(string body, HttpStatusCode status, (string, string)? header = null) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }
        public string? LastBody { get; private set; }
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Last = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (header is { } h) response.Headers.Add(h.Item1, h.Item2);
            return response;
        }
    }

    private static string Result(string status, string? error = null) => JsonSerializer.Serialize(new
    {
        status = "complete",
        total_cost = 1,
        results = new[] { new { to = "61412345678", status, error, message_id = "mm-uuid-1", custom_ref = $"dev:{MessageId:N}", cost = 1 } },
    });

    private static (MobileMessageUpstream Upstream, StubHandler Handler) Create(
        string responseBody, HttpStatusCode status = HttpStatusCode.OK,
        string? user = "api-user", string? tag = "dev", (string, string)? header = null)
    {
        var handler = new StubHandler(responseBody, status, header);
        var options = Options.Create(new MobileMessageOptions { ApiUsername = user, ApiPassword = "api-pass", EnvironmentTag = tag });
        return (new MobileMessageUpstream(new HttpClient(handler), options, NullLogger<MobileMessageUpstream>.Instance), handler);
    }

    [Fact]
    public async Task Success_IsAccepted_WithMmsMessageId_AndSendsTheDesignedRequest()
    {
        var (upstream, handler) = Create(Result("success"));
        var result = await upstream.SubmitAsync(Sms, CancellationToken.None);

        result.Accepted.Should().BeTrue();
        result.UpstreamId.Should().Be("mm-uuid-1", "learned at submit, so a receipt or reply can match on it too");
        upstream.Provider.Should().Be(UpstreamProvider.MobileMessage);

        var request = handler.Last!;
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri!.ToString().Should().Be("https://api.mobilemessage.com.au/v1/messages");
        request.Headers.Authorization!.Scheme.Should().Be("Basic");
        Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.Authorization.Parameter!)).Should().Be("api-user:api-pass");
        request.Headers.GetValues("Idempotency-Key").Single().Should().Be(MessageId.ToString("N"),
            "the message id: a resubmit replays the original result instead of sending twice");

        using var doc = JsonDocument.Parse(handler.LastBody!);
        var message = doc.RootElement.GetProperty("messages").EnumerateArray().Single();
        message.GetProperty("to").GetString().Should().Be("61412345678", "international without the plus");
        message.GetProperty("message").GetString().Should().Be("hello world");
        message.GetProperty("sender").GetString().Should().Be("SoLogical");
        message.GetProperty("custom_ref").GetString().Should().Be($"dev:{MessageId:N}", "the router routes on the tag");
        message.GetProperty("unicode").GetBoolean().Should().BeTrue("what survives the fold needs UCS-2; MM would strip it otherwise");
    }

    [Fact]
    public async Task Blocked_IsAFinalReject_NamedUnsubscribed()
    {
        var (upstream, _) = Create(Result("blocked", "The recipient has unsubscribed and cannot receive messages."));
        var result = await upstream.SubmitAsync(Sms, CancellationToken.None);
        result.Accepted.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        result.ErrorCode.Should().Be("unsubscribed");
        result.ErrorDetail.Should().Contain("unsubscribed");
    }

    [Fact]
    public async Task Error_IsAFinalReject_CarryingMmsText()
    {
        var (upstream, _) = Create(Result("error", "Invalid sender. You do not have permission to use this sender."));
        var result = await upstream.SubmitAsync(Sms, CancellationToken.None);
        result.Accepted.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        result.ErrorCode.Should().Be("upstream_error");
        result.ErrorDetail.Should().StartWith("Invalid sender");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]          // insufficient credit
    [InlineData(HttpStatusCode.Conflict)]           // same Idempotency-Key still in flight
    [InlineData(HttpStatusCode.TooManyRequests)]    // more than 5 concurrent requests
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task AccountWideAndTransientHttpFailures_AreRetryable(HttpStatusCode status)
    {
        var (upstream, _) = Create("{\"error\":\"nope\"}", status);
        var result = await upstream.SubmitAsync(Sms, CancellationToken.None);
        result.Accepted.Should().BeFalse();
        result.Retryable.Should().BeTrue();
        result.ErrorCode.Should().Be(((int)status).ToString());
        result.ErrorDetail.Should().Contain("nope");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task RequestFaults_AreNotRetryable(HttpStatusCode status)
    {
        var (upstream, _) = Create("{\"error\":\"\\\"sender\\\" is required.\"}", status);
        var result = await upstream.SubmitAsync(Sms, CancellationToken.None);
        result.Accepted.Should().BeFalse();
        result.Retryable.Should().BeFalse();
    }

    [Fact]
    public async Task A2xxThatIsNotJson_IsRetryable_TheIdempotencyKeyMakesTheRetrySafe()
    {
        var (upstream, _) = Create("<html>gateway</html>");
        var result = await upstream.SubmitAsync(Sms, CancellationToken.None);
        result.Accepted.Should().BeFalse();
        result.Retryable.Should().BeTrue();
        result.ErrorCode.Should().Be("unreadable_response");
    }

    [Fact]
    public async Task AReplayedResult_IsTheOriginalAnswer()
    {
        var (upstream, _) = Create(Result("success"), header: ("Idempotency-Replay", "true"));
        var result = await upstream.SubmitAsync(Sms, CancellationToken.None);
        result.Accepted.Should().BeTrue();
        result.UpstreamId.Should().Be("mm-uuid-1");
    }

    [Theory]
    [InlineData(null, "dev")]
    [InlineData("api-user", null)]
    [InlineData("api-user", "has space")]
    public async Task MissingCredentialsOrTag_DefersWithoutCallingMm(string? user, string? tag)
    {
        var (upstream, handler) = Create(Result("success"), user: user, tag: tag);
        var result = await upstream.SubmitAsync(Sms, CancellationToken.None);
        result.Accepted.Should().BeFalse();
        result.Retryable.Should().BeTrue("degrade to delayed, never to lost — and never sent with a ref the router cannot route");
        result.ErrorCode.Should().Be("not_configured");
        handler.Calls.Should().Be(0);
    }
}

/// <summary>Design §5.3: the custom_ref contract with the legacy router, MM's signature, the receipt
/// mapping and the reply dedupe handle.</summary>
public class MobileMessageWebhookTests
{
    [Fact]
    public void Signature_MatchesMmsPublishedTestVector()
    {
        MobileMessageWebhook.ComputeSignature("abc123", "1754640000", Encoding.UTF8.GetBytes("{\"test\":1}"))
            .Should().Be("52344b9592722e0241d82036e0920f4286bc0d47ba4624c5a1588193490a1efb");
    }

    [Fact]
    public void Verify_AcceptsAFreshSignature_AndRefusesStaleMissingOrWrong()
    {
        var body = Encoding.UTF8.GetBytes("{\"test\":1}");
        var now = DateTimeOffset.FromUnixTimeSeconds(1754640000);
        const string good = "52344b9592722e0241d82036e0920f4286bc0d47ba4624c5a1588193490a1efb";

        MobileMessageWebhook.Verify("abc123", "1754640000", good, body, 300, now).Should().BeNull();
        MobileMessageWebhook.Verify("abc123", "1754640000", good.ToUpperInvariant(), body, 300, now).Should().BeNull("hex case is not a forgery");
        MobileMessageWebhook.Verify("abc123", "1754640000", good, body, 300, now.AddSeconds(301)).Should().Contain("from our clock");
        MobileMessageWebhook.Verify("abc123", "", good, body, 300, now).Should().Contain("timestamp");
        MobileMessageWebhook.Verify("abc123", "1754640000", "", body, 300, now).Should().Be("missing signature");
        MobileMessageWebhook.Verify("wrong", "1754640000", good, body, 300, now).Should().Be("invalid signature");
        MobileMessageWebhook.Verify("abc123", "1754640000", good, Encoding.UTF8.GetBytes("{\"test\":2}"), 300, now)
            .Should().Be("invalid signature", "the body is signed byte for byte");
    }

    [Fact]
    public void Ref_FormatsAndParsesOurShape_AndRefusesTheLegacyOnes()
    {
        var id = Guid.NewGuid();
        var formatted = MobileMessageRef.Format("dev", id);
        formatted.Should().Be($"dev:{id:N}");
        formatted.Length.Should().BeLessThanOrEqualTo(38);

        MobileMessageRef.TryParse(formatted, out var tag, out var parsed).Should().BeTrue();
        tag.Should().Be("dev");
        parsed.Should().Be(id);

        MobileMessageRef.TryParse("10399858", out _, out _).Should().BeFalse("a legacy SMSID");
        MobileMessageRef.TryParse("E11101530", out _, out _).Should().BeFalse("a legacy alert ref");
        MobileMessageRef.TryParse("", out _, out _).Should().BeFalse();
        MobileMessageRef.TryParse($"dev:{id:D}", out _, out _).Should().BeFalse("N format only");
        MobileMessageRef.TryParse($"bad tag:{id:N}", out _, out _).Should().BeFalse("the router's tag rule");
    }

    [Fact]
    public void OwnRef_IsOursOnlyUnderOurTag_CaseInsensitively()
    {
        var id = Guid.NewGuid();
        var options = new MobileMessageOptions { EnvironmentTag = "dev" };

        MobileMessageWebhook.TryReadOwnRef($"DEV:{id:N}", options, out var parsed, out var foreign).Should().BeTrue();
        parsed.Should().Be(id);
        foreign.Should().BeNull();

        MobileMessageWebhook.TryReadOwnRef($"local:{id:N}", options, out _, out foreign).Should().BeFalse();
        foreign.Should().Be("local");

        MobileMessageWebhook.TryReadOwnRef("10399858", options, out _, out foreign).Should().BeFalse();
        foreign.Should().BeNull();
    }

    [Theory]
    [InlineData("delivered", DeliveryVerdict.Delivered)]
    [InlineData("DELIVERED", DeliveryVerdict.Delivered)]
    [InlineData("failed", DeliveryVerdict.Failed)]
    [InlineData("sent", DeliveryVerdict.Sent)]
    public void ReceiptStatus_Maps(string status, DeliveryVerdict expected)
        => MobileMessageStatusIngress.MapVerdict(status).Should().Be(expected);

    [Fact]
    public void ReceiptStatus_UnknownIsUnmapped() => MobileMessageStatusIngress.MapVerdict("queued").Should().BeNull();

    [Fact]
    public void ReplyDedupeId_IsStableForARetry_AndDiffersForAnotherReply()
    {
        var reply = new Dictionary<string, string>
        {
            ["sender"] = "61412345678", ["to"] = "61400000001", ["received_at"] = "2026-11-11T01:02:03Z",
            ["type"] = "inbound", ["message"] = "yes",
        };
        var again = new Dictionary<string, string>(reply);
        var other = new Dictionary<string, string>(reply) { ["message"] = "no" };

        var id = MobileMessageInboundIngress.DedupeId(reply);
        id.Should().StartWith("mm:").And.HaveLength(43);
        MobileMessageInboundIngress.DedupeId(again).Should().Be(id);
        MobileMessageInboundIngress.DedupeId(other).Should().NotBe(id);
    }
}
