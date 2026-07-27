namespace Sological.Sms.Core.Entities;

/// <summary>Multipart reassembly buffer (design §3): keyed (channel, from, group_ref, part_no),
/// swept into inbound_messages on completion or 60s timeout (deliver what arrived, ordered,
/// complete=false).</summary>
public class InboundPart
{
    public long ChannelId { get; set; }

    public required string FromNumber { get; set; }

    /// <summary>UDH concatenation reference for the group.</summary>
    public required string GroupRef { get; set; }

    public short PartNo { get; set; }

    public short TotalParts { get; set; }

    public required string BodyFragment { get; set; }

    /// <summary>Data coding scheme; 8 = UCS-2.</summary>
    public short? Dcs { get; set; }

    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
}
