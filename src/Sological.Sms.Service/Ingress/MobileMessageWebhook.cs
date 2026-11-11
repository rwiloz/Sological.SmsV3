using System.Security.Cryptography;
using System.Text;
using Sological.Sms.Service.Upstream;

namespace Sological.Sms.Service.Ingress;

/// <summary>What both Mobile Message receivers do first (design §5.3): read the raw body bytes, verify MM's
/// signature over exactly those bytes, then parse the JSON. Events reach us through the legacy app's router,
/// which forwards the body and the X-MM-* headers untouched, so MM's own signature still verifies here.</summary>
public static class MobileMessageWebhook
{
    public const string TimestampHeader = "X-MM-Timestamp";
    public const string SignatureHeader = "X-MM-Signature";

    /// <summary>Lowercase hex HMAC-SHA256 of `{timestamp}.{raw body}`, keyed with the signing secret.</summary>
    public static string ComputeSignature(string secret, string timestamp, ReadOnlySpan<byte> body)
    {
        var prefix = Encoding.UTF8.GetBytes(timestamp + ".");
        var data = new byte[prefix.Length + body.Length];
        prefix.CopyTo(data, 0);
        body.CopyTo(data.AsSpan(prefix.Length));
        return Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), data));
    }

    /// <summary>Null when the request verifies; otherwise why not (for the log, never the response).</summary>
    public static string? Verify(string secret, string? timestamp, string? signature, ReadOnlySpan<byte> body,
        int maxClockSkewSeconds, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(timestamp) || !long.TryParse(timestamp, out var seconds))
            return "missing or malformed timestamp";
        if (Math.Abs(now.ToUnixTimeSeconds() - seconds) > maxClockSkewSeconds)
            return $"timestamp {timestamp} is more than {maxClockSkewSeconds}s from our clock";
        if (string.IsNullOrEmpty(signature))
            return "missing signature";
        var expected = Encoding.ASCII.GetBytes(ComputeSignature(secret, timestamp, body));
        var provided = Encoding.ASCII.GetBytes(signature.Trim().ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(expected, provided) ? null : "invalid signature";
    }

    /// <summary>Reads, verifies and parses one webhook. A refusal comes back as the result to return:
    /// 401 for a request that does not verify, 503 when we cannot verify at all (no secret configured) —
    /// a non-2xx the router turns into MM's retry, so events wait out a config fix instead of being lost.</summary>
    public static async Task<(Dictionary<string, string> Fields, IResult? Refusal)> ReadAsync(
        HttpRequest request, MobileMessageOptions options, string kind, ILogger logger, CancellationToken ct)
    {
        byte[] body;
        using (var buffer = new MemoryStream())
        {
            await request.Body.CopyToAsync(buffer, ct);
            body = buffer.ToArray();
        }

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(options.SigningSecret))
        {
            var error = Verify(options.SigningSecret,
                request.Headers[TimestampHeader].ToString(), request.Headers[SignatureHeader].ToString(),
                body, options.MaxClockSkewSeconds, DateTimeOffset.UtcNow);
            if (error is not null)
            {
                logger.LogWarning("Mobile Message {Kind} webhook refused: {Reason}", kind, error);
                return (fields, Results.Unauthorized());
            }
        }
        else if (options.RequireSignature)
        {
            logger.LogError("Mobile Message {Kind} webhook cannot be verified: {Section}:SigningSecret is not configured — answering 503 so it is retried",
                kind, MobileMessageOptions.SectionName);
            return (fields, Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
        }

        IngressShared.AddJsonBody(Encoding.UTF8.GetString(body), fields, request.ContentType, logger);
        return (fields, null);
    }

    /// <summary>True when <paramref name="customRef"/> is ours: our tag and a message id.</summary>
    public static bool TryReadOwnRef(string? customRef, MobileMessageOptions options, out Guid messageId, out string? foreignTag)
    {
        foreignTag = null;
        if (!MobileMessageRef.TryParse(customRef, out var tag, out messageId))
            return false;
        if (string.Equals(tag, options.EnvironmentTag, StringComparison.OrdinalIgnoreCase))
            return true;
        foreignTag = tag;
        return false;
    }
}
