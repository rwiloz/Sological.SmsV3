using Sological.Sms.Service.Ingress;

namespace Sological.Sms.Service.Api;

/// <summary>The SMS Central push receivers (design §5) — provider ingress routes, the only
/// non-customer public surface. Historically GET with query params; their portal now also
/// offers POST (form-encoded) — both are accepted. Both ack 200 "0" fast.</summary>
public static class IngressEndpoints
{
    private static readonly string[] GetOrPost = ["GET", "POST"];

    public static void MapSmsCentralIngress(this WebApplication app)
    {
        app.MapMethods("/ingress/smscentral/delivery", GetOrPost,
                (HttpContext ctx, SmsCentralDeliveryIngress ingress, CancellationToken ct) => ingress.HandleAsync(ctx, ct))
            .RequireRateLimiting(RateLimitOptions.IngressPolicy);
        app.MapMethods("/ingress/smscentral/inbound", GetOrPost,
                (HttpContext ctx, SmsCentralInboundIngress ingress, CancellationToken ct) => ingress.HandleAsync(ctx, ct))
            .RequireRateLimiting(RateLimitOptions.IngressPolicy);
    }
}
