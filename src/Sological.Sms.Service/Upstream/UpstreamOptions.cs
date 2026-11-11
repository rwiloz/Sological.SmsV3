using Sological.Sms.Core.Entities;

namespace Sological.Sms.Service.Upstream;

/// <summary>Which driver sends (2026-11-11): one provider for every channel, chosen at startup.
/// Mobile Message by default; SMS Central stays selectable while its account lives.</summary>
public sealed class UpstreamOptions
{
    public const string SectionName = "SologicalSms:Upstream";

    public UpstreamProvider Provider { get; set; } = UpstreamProvider.MobileMessage;
}
