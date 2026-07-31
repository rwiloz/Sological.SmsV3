using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Sological.Sms.Service.Api;

public sealed class RateLimitOptions
{
    public const string SectionName = "SologicalSms:RateLimits";
    public const string SendPolicy = "send";
    public const string IngressPolicy = "ingress";

    /// <summary>Master switch — hermetic test harnesses turn the limiter off.</summary>
    public bool Enabled { get; set; } = true;

    public int SendPerSecond { get; set; } = 5;
    public int SendBurst { get; set; } = 10;
    public int GlobalPerMinute { get; set; } = 120;
    public int IngressPerMinute { get; set; } = 300;
    public long MaxRequestBodyBytes { get; set; } = 65_536;
}

/// <summary>Request-layer abuse limits (public-surface slice, 2026-07-31): token bucket
/// per API key on send — a leaked key trickles instead of floods — and per-IP windows
/// everywhere else. /health and /ingress/* are exempt from the GLOBAL window (probes must
/// never rate-limit into a restart loop; DR pushes clump when bulk sends deliver) —
/// ingress has its own higher per-IP policy instead.</summary>
public static class RateLimiting
{
    public static void AddSmsRateLimiter(this IServiceCollection services, RateLimitOptions o)
    {
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = async (ctx, ct) =>
            {
                ctx.HttpContext.Response.Headers.RetryAfter = "1";
                ctx.HttpContext.Response.ContentType = "application/json";
                await ctx.HttpContext.Response.WriteAsync(
                    """{"error":"rate_limited","message":"Too many requests - slow down."}""", ct);
            };

            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
                ctx.Request.Path.StartsWithSegments("/health") || ctx.Request.Path.StartsWithSegments("/ingress")
                    ? RateLimitPartition.GetNoLimiter("exempt")
                    : RateLimitPartition.GetFixedWindowLimiter(ClientIp(ctx), _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = o.GlobalPerMinute,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    }));

            limiter.AddPolicy(RateLimitOptions.SendPolicy, ctx =>
                RateLimitPartition.GetTokenBucketLimiter(
                    ctx.Request.Headers["X-Api-Key"].FirstOrDefault() ?? ClientIp(ctx),
                    _ => new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = o.SendBurst,
                        TokensPerPeriod = o.SendPerSecond,
                        ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                        QueueLimit = 0,
                        AutoReplenishment = true,
                    }));

            limiter.AddPolicy(RateLimitOptions.IngressPolicy, ctx =>
                RateLimitPartition.GetFixedWindowLimiter(ClientIp(ctx), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = o.IngressPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
        });
    }

    /// <summary>After UseForwardedHeaders (ForwardLimit = 1) this is the proxy-attested
    /// client address — the RIGHTMOST forwarded hop, not spoofable XFF depth.</summary>
    private static string ClientIp(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
