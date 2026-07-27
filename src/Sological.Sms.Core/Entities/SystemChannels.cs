namespace Sological.Sms.Core.Entities;

/// <summary>Well-known system rows the service guarantees at startup.</summary>
public static class SystemChannels
{
    /// <summary>The operator quarantine channel (design §5.2): unrouteable inbound lands
    /// here — never dropped, never 500. Paused: it can never send.</summary>
    public const string OperatorKey = "Operator";

    public const string OperatorCustomerCode = "sological";
}
