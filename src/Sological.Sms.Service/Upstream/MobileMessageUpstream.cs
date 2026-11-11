using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Sological.Sms.Core.Entities;
using Sological.Sms.Core.Upstream;

namespace Sological.Sms.Service.Upstream;

public sealed class MobileMessageOptions
{
    /// <summary>Config section (KV secrets land here as SologicalSms--MobileMessage--*).</summary>
    public const string SectionName = "SologicalSms:MobileMessage";

    public string BaseUrl { get; set; } = "https://api.mobilemessage.com.au";

    /// <summary>API key pair (Basic auth). One key per environment, so each can be revoked alone.</summary>
    public string? ApiUsername { get; set; }

    public string? ApiPassword { get; set; }

    /// <summary>This environment's routing tag (`dev` on Azure, `local` on Ray's machine). Every send's
    /// custom_ref is `{tag}:{message id}`, and the legacy app's router forwards the receipts and replies
    /// carrying it here. Unset = sends are held, never sent with a ref the router cannot route.</summary>
    public string? EnvironmentTag { get; set; }

    /// <summary>The account's webhook signing secret (account-wide, the same value the legacy app holds).</summary>
    public string? SigningSecret { get; set; }

    /// <summary>Webhooks without a valid signature are refused. Only a hermetic test turns this off.</summary>
    public bool RequireSignature { get; set; } = true;

    /// <summary>MM's guidance: reject a timestamp more than 5 minutes from our clock (replay window).</summary>
    public int MaxClockSkewSeconds { get; set; } = 300;
}

/// <summary>The custom_ref contract with the legacy router (Docs/MobileMessage-Router-Brief.md in the legacy
/// repo): `{tag}:{message id, N format}`. The legacy app's own refs are bare integers.</summary>
public static partial class MobileMessageRef
{
    /// <summary>The router's tag rule, kept identical so a tag it accepts is one we can emit.</summary>
    [GeneratedRegex("^[A-Za-z0-9_-]{1,32}$")]
    private static partial Regex TagPattern();

    public static bool IsValidTag(string? tag) => tag is not null && TagPattern().IsMatch(tag);

    public static string Format(string tag, Guid messageId) => $"{tag}:{messageId:N}";

    /// <summary>Splits a ref into its tag and message id; false when it is not of our shape.</summary>
    public static bool TryParse(string? customRef, out string tag, out Guid messageId)
    {
        tag = "";
        messageId = Guid.Empty;
        if (string.IsNullOrEmpty(customRef)) return false;
        var colon = customRef.IndexOf(':');
        if (colon <= 0) return false;
        tag = customRef[..colon];
        return IsValidTag(tag) && Guid.TryParseExact(customRef[(colon + 1)..], "N", out messageId);
    }
}

/// <summary>Mobile Message driver (design §4.2): one message per request (per-message honesty, as
/// §4.1), Basic auth, custom_ref = `{tag}:{id}`, Idempotency-Key = the message id so a resubmit after
/// a lost response or a lease reclaim replays the original result instead of sending twice (MM keeps
/// keys 24 h). Unicode is always enabled: the send lane has already folded every character with a
/// GSM-7 equivalent, so what remains needs UCS-2 and MM would otherwise strip it.</summary>
public sealed class MobileMessageUpstream(
    HttpClient http,
    IOptions<MobileMessageOptions> options,
    ILogger<MobileMessageUpstream> logger) : ISmsUpstream
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public UpstreamProvider Provider => UpstreamProvider.MobileMessage;

    public async Task<UpstreamSubmitResult> SubmitAsync(OutboundSms sms, CancellationToken ct)
    {
        var o = options.Value;
        if (string.IsNullOrEmpty(o.ApiUsername) || string.IsNullOrEmpty(o.ApiPassword))
        {
            logger.LogError("Mobile Message credentials not configured ({Section}:ApiUsername|ApiPassword) — message {MessageId} deferred",
                MobileMessageOptions.SectionName, sms.MessageId);
            return new(false, null, "not_configured", "Mobile Message credentials are not configured", Retryable: true);
        }
        if (!MobileMessageRef.IsValidTag(o.EnvironmentTag))
        {
            logger.LogError("Mobile Message environment tag not configured or invalid ({Section}:EnvironmentTag) — message {MessageId} deferred",
                MobileMessageOptions.SectionName, sms.MessageId);
            return new(false, null, "not_configured", "Mobile Message environment tag is not configured", Retryable: true);
        }

        var request = new HttpRequestMessage(HttpMethod.Post, $"{o.BaseUrl.TrimEnd('/')}/v1/messages")
        {
            Content = JsonContent.Create(new SendRequest(
                [new SendItem(ToRecipient(sms.To), sms.Body, sms.Originator, MobileMessageRef.Format(o.EnvironmentTag!, sms.MessageId), Unicode: true)]),
                options: Json),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{o.ApiUsername}:{o.ApiPassword}")));
        request.Headers.Add("Idempotency-Key", sms.MessageId.ToString("N"));

        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        var status = (int)response.StatusCode;

        if (!response.IsSuccessStatusCode)
        {
            var error = ReadError(body);
            // 401/403 (credentials, credit), 409 (same key still in flight), 429 (concurrency) and 5xx are
            // account-wide or transient: the message waits through the bounded retry. 400/422 mean this
            // request is wrong (or its key was reused with another body) — retrying cannot fix it.
            if (status is 400 or 422)
            {
                logger.LogError("Mobile Message refused message {MessageId}: HTTP {Status} {Error}", sms.MessageId, status, error);
                return new(false, null, status.ToString(), Truncate($"HTTP {status} from Mobile Message: {error}"), Retryable: false);
            }
            logger.LogWarning("Mobile Message HTTP {Status} for message {MessageId}: {Error} — retryable", status, sms.MessageId, error);
            return new(false, null, status.ToString(), Truncate($"HTTP {status} from Mobile Message: {error}"), Retryable: true);
        }

        SendResult? result;
        try
        {
            result = JsonSerializer.Deserialize<SendResponse>(body, Json)?.Results?.FirstOrDefault();
        }
        catch (JsonException ex)
        {
            // Accepted or not is unknown; the Idempotency-Key makes the retry return the original answer.
            logger.LogWarning("Mobile Message response for message {MessageId} was not JSON ({Error}) — retryable", sms.MessageId, ex.Message);
            return new(false, null, "unreadable_response", "Mobile Message answered 2xx with a body that is not JSON", Retryable: true);
        }
        if (result is null)
        {
            logger.LogWarning("Mobile Message response for message {MessageId} carried no result — retryable", sms.MessageId);
            return new(false, null, "unreadable_response", "Mobile Message answered 2xx with no per-message result", Retryable: true);
        }

        if (response.Headers.TryGetValues("Idempotency-Replay", out var replay) && replay.Contains("true"))
            logger.LogInformation("Mobile Message replayed the original result for message {MessageId} (Idempotency-Key)", sms.MessageId);

        return result.Status switch
        {
            "success" => new(true, Truncate(result.MessageId, 64), null, null),
            // The recipient opted out (MM's account-wide list): final, and named so the caller can tell
            // an opt-out from a delivery failure.
            "blocked" => new(false, null, "unsubscribed", Truncate(result.Error ?? "the recipient has unsubscribed"), Retryable: false),
            "error" => new(false, null, "upstream_error", Truncate(result.Error ?? "Mobile Message rejected the message"), Retryable: false),
            _ => Unmapped(sms, result.Status),
        };
    }

    private UpstreamSubmitResult Unmapped(OutboundSms sms, string? status)
    {
        logger.LogWarning("Unmapped Mobile Message result status '{Status}' for message {MessageId} — treating as retryable", status, sms.MessageId);
        return new(false, null, "unmapped_status", $"Mobile Message result status '{status}'", Retryable: true);
    }

    /// <summary>MM takes 61… (international without the plus) or 04…; the send lane stores +61….</summary>
    internal static string ToRecipient(string to) => to.StartsWith('+') ? to[1..] : to;

    private static string ReadError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                return error.GetString()!;
        }
        catch (JsonException)
        {
        }
        return body.Length <= 200 ? body : body[..200];
    }

    private static string? Truncate(string? value, int max = 500)
        => value is null ? null : value.Length <= max ? value : value[..max];

    private sealed record SendRequest(IReadOnlyList<SendItem> Messages);

    private sealed record SendItem(
        string To, string Message, string Sender,
        [property: JsonPropertyName("custom_ref")] string CustomRef,
        bool Unicode);

    private sealed record SendResponse(string? Status, IReadOnlyList<SendResult>? Results);

    private sealed record SendResult(
        string? Status,
        string? Error,
        [property: JsonPropertyName("message_id")] string? MessageId,
        [property: JsonPropertyName("custom_ref")] string? CustomRef);
}
