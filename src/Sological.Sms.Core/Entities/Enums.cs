namespace Sological.Sms.Core.Entities;

// All enums are stored as lowercase strings (design §3 spells the values in lowercase);
// the converter lives in the Service data layer.

public enum CustomerStatus
{
    Active,
    Disabled,
}

public enum ChannelStatus
{
    Active,

    /// <summary>Safely un-sendable — replaces the AIDemo/AIDemoX kill-switch mismatch at S4.</summary>
    Paused,

    Retired,
}

public enum UpstreamProvider
{
    SmsCentral,
    Sinch,
}

public enum MessageStatus
{
    Queued,
    Submitting,
    Sent,
    Delivered,
    Failed,
    Rejected,
    Expired,

    /// <summary>Pre-dispatch guard verdict (design §6.1a): never dispatched, never billed.</summary>
    Duplicate,
}

public enum LedgerRefType
{
    Message,
    Inbound,
}

public enum SmsDirection
{
    Outbound,
    Inbound,
}

public enum WebhookEventType
{
    SmsDelivery,
    SmsInbound,
}

public enum WebhookOutboxState
{
    Pending,
    Delivered,
    Dead,
}

/// <summary>Wire names of the customer webhook events (design §6.2) — they carry a dot,
/// so they get an explicit mapping instead of the lowercase-enum convention.</summary>
public static class WebhookEventNames
{
    public const string SmsDelivery = "sms.delivery";
    public const string SmsInbound = "sms.inbound";

    public static string ToWire(WebhookEventType type) => type switch
    {
        WebhookEventType.SmsDelivery => SmsDelivery,
        WebhookEventType.SmsInbound => SmsInbound,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    public static WebhookEventType FromWire(string name) => name switch
    {
        SmsDelivery => WebhookEventType.SmsDelivery,
        SmsInbound => WebhookEventType.SmsInbound,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };
}
