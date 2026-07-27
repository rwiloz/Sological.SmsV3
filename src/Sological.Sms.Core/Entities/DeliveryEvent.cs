namespace Sological.Sms.Core.Entities;

/// <summary>Append-only audit of every DLR push received (design §3) — the message row is
/// the distilled truth. MessageId is nullable because an unknown REFERENCE still gets its
/// audit row and must never 500 (design §5.1).</summary>
public class DeliveryEvent
{
    public long Id { get; set; }

    public Guid? MessageId { get; set; }

    public Message? Message { get; set; }

    public string? RawResult { get; set; }

    public string? RawStatus { get; set; }

    public string? RawDescription { get; set; }

    public required string Provider { get; set; }

    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The push's raw query parameters, verbatim (jsonb).</summary>
    public required Dictionary<string, string> Payload { get; set; }
}
