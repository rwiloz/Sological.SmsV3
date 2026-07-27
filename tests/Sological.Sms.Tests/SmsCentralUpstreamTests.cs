using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sological.Sms.Core.Upstream;
using Sological.Sms.Service.Upstream;

namespace Sological.Sms.Tests;

/// <summary>Design §4.1's response-code mapping, against a stub HTTP handler.</summary>
public class SmsCentralUpstreamTests
{
    private static readonly Guid MessageId = Guid.Parse("0198d2c8-5a5a-7000-8000-000000000001");
    private static readonly OutboundSms Sms = new(MessageId, "TestSender", "+61412345678", "hello world");

    private sealed class StubHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string? LastFormBody { get; private set; }
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastFormBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    private static (SmsCentralUpstream Upstream, StubHandler Handler) Create(
        string responseBody, HttpStatusCode status = HttpStatusCode.OK, bool configured = true)
    {
        var handler = new StubHandler(responseBody, status);
        var options = Options.Create(new SmsCentralOptions
        {
            User = configured ? "sological2" : null,
            Password = configured ? "pw" : null,
        });
        return (new SmsCentralUpstream(new HttpClient(handler), options, NullLogger<SmsCentralUpstream>.Instance), handler);
    }

    [Fact]
    public async Task Accepted_OnZeroBody_AndSendsTheDesignedForm()
    {
        var (upstream, handler) = Create("0");
        var result = await upstream.SubmitAsync(Sms, CancellationToken.None);

        result.Accepted.Should().BeTrue();
        handler.LastFormBody.Should().NotBeNull();
        var form = handler.LastFormBody!;
        form.Should().Contain("ACTION=send");
        form.Should().Contain("USERNAME=sological2");
        form.Should().Contain("ORIGINATOR=TestSender");
        form.Should().Contain($"REFERENCE={MessageId:N}", "REFERENCE is our uuid in N format");
        form.Should().Contain("RECIPIENT=61412345678", "RECIPIENT is international WITHOUT the plus");
    }

    [Fact]
    public async Task Duplicate513_TreatedAsPreviouslyAccepted()
    {
        var (upstream, _) = Create("513 Duplicate Reference");
        var result = await upstream.SubmitAsync(Sms, CancellationToken.None);
        result.Accepted.Should().BeTrue("our uuid collided ⇒ the prior submit succeeded — reconcile, don't resend");
        result.ErrorCode.Should().Be("513");
    }

    [Theory]
    [InlineData("511 Authentication failure")]
    [InlineData("514 No recipient")]
    [InlineData("519 Blacklisted")]
    [InlineData("531 No message content")]
    [InlineData("534 Insufficient credit")]
    [InlineData("535 Invalid originator")]
    public async Task HardRejects_AreNotRetryable(string body)
    {
        var (upstream, _) = Create(body);
        var result = await upstream.SubmitAsync(Sms, CancellationToken.None);
        result.Accepted.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        result.ErrorCode.Should().Be(body.Split(' ')[0]);
    }

    [Theory]
    [InlineData("500 Internal error")]
    [InlineData("536 Temporarily delayed")]
    [InlineData("999 Something unmapped")]
    public async Task TransientAndUnknownCodes_AreRetryable(string body)
    {
        var (upstream, _) = Create(body);
        var result = await upstream.SubmitAsync(Sms, CancellationToken.None);
        result.Accepted.Should().BeFalse();
        result.Retryable.Should().BeTrue();
    }

    [Fact]
    public async Task HttpFailure_IsRetryable()
    {
        var (upstream, _) = Create("irrelevant", HttpStatusCode.BadGateway);
        var result = await upstream.SubmitAsync(Sms, CancellationToken.None);
        result.Accepted.Should().BeFalse();
        result.Retryable.Should().BeTrue();
    }

    [Fact]
    public async Task MissingCredentials_DefersWithoutCallingUpstream()
    {
        var (upstream, handler) = Create("0", configured: false);
        var result = await upstream.SubmitAsync(Sms, CancellationToken.None);
        result.Accepted.Should().BeFalse();
        result.Retryable.Should().BeTrue("degrade to delayed, never to lost (design §9)");
        result.ErrorCode.Should().Be("not_configured");
        handler.Calls.Should().Be(0);
    }
}
