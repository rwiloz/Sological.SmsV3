using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sological.Sms.Core.Webhooks;

/// <summary>The customer webhook contract (design §6.2) — these shapes ARE the API;
/// field names never drift from the design doc.</summary>
public sealed record SmsDeliveryEvent(
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("messageId")] Guid MessageId,
    [property: JsonPropertyName("reference")] string? Reference,
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("errorCode")] string? ErrorCode,
    [property: JsonPropertyName("errorDetail")] string? ErrorDetail,
    [property: JsonPropertyName("timestamp")] DateTimeOffset Timestamp);

public sealed record SmsInboundReplyTo(
    [property: JsonPropertyName("messageId")] Guid MessageId,
    [property: JsonPropertyName("reference")] string? Reference);

public sealed record SmsInboundEvent(
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("inboundId")] Guid InboundId,
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("to")] string? To,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("complete")] bool Complete,
    [property: JsonPropertyName("receivedAt")] DateTimeOffset ReceivedAt,
    // Contractually explicit `null` when uncorrelated (§6.2) — never omitted.
    [property: JsonPropertyName("replyTo"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] SmsInboundReplyTo? ReplyTo);

public static class WebhookJson
{
    /// <summary>One serializer config for the whole contract: camelCase is explicit via
    /// attributes, nulls omitted except replyTo (which is contractually `null` when
    /// uncorrelated — handled by writing the property explicitly).</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
