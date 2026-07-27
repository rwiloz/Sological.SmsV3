using Sological.Sms.Service.Ingress;

namespace Sological.Sms.Service.Api;

/// <summary>The SMS Central push receivers (design §5) — provider ingress routes, the only
/// non-customer public surface. Both are GET (their model), both ack 200 "0" fast.</summary>
public static class IngressEndpoints
{
    public static void MapSmsCentralIngress(this WebApplication app)
    {
        app.MapGet("/ingress/smscentral/delivery",
            (HttpContext ctx, SmsCentralDeliveryIngress ingress, CancellationToken ct) => ingress.HandleAsync(ctx, ct));
        app.MapGet("/ingress/smscentral/inbound",
            (HttpContext ctx, SmsCentralInboundIngress ingress, CancellationToken ct) => ingress.HandleAsync(ctx, ct));
    }
}
