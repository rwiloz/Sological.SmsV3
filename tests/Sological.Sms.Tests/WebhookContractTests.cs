using System.Text.Json;
using Sological.Sms.Core.Webhooks;

namespace Sological.Sms.Tests;

/// <summary>The §6.2 payload shapes are the CUSTOMER contract — field names and null
/// semantics are pinned here so they can never drift silently.</summary>
public class WebhookContractTests
{
    [Fact]
    public void DeliveryEvent_SerializesTheContractFields()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var json = JsonSerializer.Serialize(new SmsDeliveryEvent(
            "sms.delivery", id, "ref-1", "+61412345678", "delivered", null, null,
            DateTimeOffset.Parse("2026-07-30T01:02:03Z")), WebhookJson.Options);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("event").GetString().Should().Be("sms.delivery");
        doc.RootElement.GetProperty("messageId").GetGuid().Should().Be(id);
        doc.RootElement.GetProperty("reference").GetString().Should().Be("ref-1");
        doc.RootElement.GetProperty("to").GetString().Should().Be("+61412345678");
        doc.RootElement.GetProperty("status").GetString().Should().Be("delivered");
        doc.RootElement.TryGetProperty("errorCode", out _).Should().BeFalse("nulls are omitted");
    }

    [Fact]
    public void DeliveryEvent_CarriesErrorFields_WhenPresent()
    {
        var json = JsonSerializer.Serialize(new SmsDeliveryEvent(
            "sms.delivery", Guid.NewGuid(), null, "+61412345678", "failed", "550", "0001: Phone related",
            DateTimeOffset.UtcNow), WebhookJson.Options);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("errorCode").GetString().Should().Be("550");
        doc.RootElement.GetProperty("errorDetail").GetString().Should().Be("0001: Phone related");
    }

    [Fact]
    public void InboundEvent_ReplyTo_IsExplicitNull_WhenUncorrelated()
    {
        var json = JsonSerializer.Serialize(new SmsInboundEvent(
            "sms.inbound", Guid.NewGuid(), "+61408004199", "+61438887301", "hi", true,
            DateTimeOffset.UtcNow, null), WebhookJson.Options);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("replyTo").ValueKind.Should().Be(JsonValueKind.Null,
            "the contract says replyTo is null when uncorrelated — present, not omitted");
    }

    [Fact]
    public void InboundEvent_ReplyTo_CarriesMessageIdAndReference()
    {
        var mid = Guid.NewGuid();
        var json = JsonSerializer.Serialize(new SmsInboundEvent(
            "sms.inbound", Guid.NewGuid(), "+61408004199", null, "hi", true,
            DateTimeOffset.UtcNow, new SmsInboundReplyTo(mid, "r-9")), WebhookJson.Options);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("replyTo").GetProperty("messageId").GetGuid().Should().Be(mid);
        doc.RootElement.GetProperty("replyTo").GetProperty("reference").GetString().Should().Be("r-9");
    }
}
