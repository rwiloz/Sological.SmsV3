using Sological.Sms.Core.Entities;
using Sological.Sms.Service.Data;

namespace Sological.Sms.Tests;

/// <summary>The stored spellings are contract: design §3 names the message statuses and
/// upstreams in lowercase, and §6.2 names the webhook events with a dot.</summary>
public class EnumStorageTests
{
    [Fact]
    public void MessageStatuses_StoreAsTheDesignSpellings()
    {
        var converter = new LowercaseEnumConverter<MessageStatus>();
        var toStore = converter.ConvertToProviderExpression.Compile();

        var stored = Enum.GetValues<MessageStatus>().Select(toStore);

        stored.Should().BeEquivalentTo(
        [
            "queued", "submitting", "sent", "delivered", "failed", "rejected", "expired", "duplicate",
        ]);
    }

    [Fact]
    public void UpstreamProviders_StoreAsTheDesignSpellings()
    {
        var converter = new LowercaseEnumConverter<UpstreamProvider>();
        var toStore = converter.ConvertToProviderExpression.Compile();

        Enum.GetValues<UpstreamProvider>().Select(toStore)
            .Should().BeEquivalentTo(["smscentral", "sinch"]);
    }

    [Fact]
    public void LowercaseEnumConverter_RoundTrips_EveryValue()
    {
        var converter = new LowercaseEnumConverter<MessageStatus>();
        var toStore = converter.ConvertToProviderExpression.Compile();
        var fromStore = converter.ConvertFromProviderExpression.Compile();

        foreach (var value in Enum.GetValues<MessageStatus>())
            fromStore(toStore(value)).Should().Be(value);
    }

    [Fact]
    public void WebhookEventNames_UseTheDottedWireNames_AndRoundTrip()
    {
        WebhookEventNames.ToWire(WebhookEventType.SmsDelivery).Should().Be("sms.delivery");
        WebhookEventNames.ToWire(WebhookEventType.SmsInbound).Should().Be("sms.inbound");

        foreach (var value in Enum.GetValues<WebhookEventType>())
            WebhookEventNames.FromWire(WebhookEventNames.ToWire(value)).Should().Be(value);
    }
}
