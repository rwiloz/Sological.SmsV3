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

    /// <summary>The Mobile Message receivers (design §5.3). MM's account has one URL of each kind, which
    /// point at the legacy app; its router forwards our environment's events here, raw body and X-MM-*
    /// headers untouched. Any 2xx acks; a non-2xx makes the router answer MM 503, so MM retries.</summary>
    public static void MapMobileMessageIngress(this WebApplication app)
    {
        app.MapPost("/ingress/mobilemessage/status",
                (HttpContext ctx, MobileMessageStatusIngress ingress, CancellationToken ct) => ingress.HandleAsync(ctx, ct))
            .RequireRateLimiting(RateLimitOptions.IngressPolicy);
        app.MapPost("/ingress/mobilemessage/inbound",
                (HttpContext ctx, MobileMessageInboundIngress ingress, CancellationToken ct) => ingress.HandleAsync(ctx, ct))
            .RequireRateLimiting(RateLimitOptions.IngressPolicy);
    }
}
