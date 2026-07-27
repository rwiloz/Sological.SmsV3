namespace Sological.Sms.Core.Upstream;

/// <summary>The normalized delivery-event verdict every ingress translates into
/// (design §4) — everything downstream of this is driver-agnostic.</summary>
public enum DeliveryVerdict
{
    /// <summary>Interim: carrier-accepted, receipt pending (RESULT=0/536, BUFFRED, enroute/submitted).</summary>
    Sent,
    Delivered,
    Failed,
    /// <summary>Permanent refusal (modern status `rejected` — blocked, filtered, credit).</summary>
    Rejected,
    Expired,
}
