namespace Sological.Sms.Core.Entities;

/// <summary>A billing party (AI-Workforce, Connectnow, …) — design §2.</summary>
public class Customer
{
    public long Id { get; set; }

    /// <summary>Short unique handle, e.g. "aiworkforce".</summary>
    public required string Code { get; set; }

    public required string Name { get; set; }

    public CustomerStatus Status { get; set; } = CustomerStatus.Active;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<Channel> Channels { get; set; } = [];
}
