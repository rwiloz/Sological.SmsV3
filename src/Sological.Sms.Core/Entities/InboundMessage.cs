namespace Sological.Sms.Core.Entities;

/// <summary>A received (MO) SMS, reassembled if multipart (design §3/§5.2).</summary>
public class InboundMessage
{
    public Guid Id { get; set; }

    public long ChannelId { get; set; }

    public Channel? Channel { get; set; }

    public required string FromNumber { get; set; }

    public string? ToNumber { get; set; }

    public required string Body { get; set; }

    public string? UpstreamId { get; set; }

    /// <summary>Set when the upstream round-tripped the original send's REFERENCE — exact
    /// reply→send correlation.</summary>
    public Guid? ReplyToMessageId { get; set; }

    public Message? ReplyToMessage { get; set; }

    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>False when the 60s multipart sweep delivered what arrived (design §3).</summary>
    public bool Complete { get; set; } = true;

    /// <summary>The push's raw query parameters, verbatim (jsonb).</summary>
    public required Dictionary<string, string> Payload { get; set; }

    /// <summary>Set by the webhook egress; NULL = still owed to the customer.</summary>
    public DateTimeOffset? DeliveredToCustomerAt { get; set; }
}
