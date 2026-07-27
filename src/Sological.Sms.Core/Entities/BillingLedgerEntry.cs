namespace Sological.Sms.Core.Entities;

/// <summary>Append-only billing ledger (design §3). No updates, ever — corrections are
/// compensating rows. Units are PARTS, not messages (~2.7 parts/send on live traffic).
/// Duplicate/rejected guard verdicts never get a row (design §6.1a).</summary>
public class BillingLedgerEntry
{
    public long Id { get; set; }

    public long CustomerId { get; set; }

    public long ChannelId { get; set; }

    public LedgerRefType RefType { get; set; }

    /// <summary>messages.id or inbound_messages.id, per RefType.</summary>
    public Guid RefId { get; set; }

    public SmsDirection Direction { get; set; }

    public short Units { get; set; }

    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
}
