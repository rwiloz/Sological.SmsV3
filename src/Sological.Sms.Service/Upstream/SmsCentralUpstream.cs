using Microsoft.Extensions.Options;
using Sological.Sms.Core.Upstream;

namespace Sological.Sms.Service.Upstream;

public sealed class SmsCentralOptions
{
    /// <summary>Config section (KV secrets land here as SologicalSms--SmsCentral--*).</summary>
    public const string SectionName = "SologicalSms:SmsCentral";

    public string BaseUrl { get; set; } = "https://my.smscentral.com.au/wrapper/sms";
    public string? User { get; set; }
    public string? Password { get; set; }
}

/// <summary>SMS Central driver (design §4.1): form POST, REFERENCE = our message uuid
/// ("N" format — unique by construction, keeps the 513 duplicate rule quiet). Batch
/// (RECIPIENTMESSAGES) deliberately not used — all-or-nothing rejection fights
/// per-message honesty.</summary>
public sealed class SmsCentralUpstream(
    HttpClient http,
    IOptions<SmsCentralOptions> options,
    ILogger<SmsCentralUpstream> logger) : ISmsUpstream
{
    private static readonly HashSet<string> HardRejects = ["511", "514", "519", "531", "534", "535"];

    public async Task<UpstreamSubmitResult> SubmitAsync(OutboundSms sms, CancellationToken ct)
    {
        var o = options.Value;
        if (string.IsNullOrEmpty(o.User) || string.IsNullOrEmpty(o.Password))
        {
            logger.LogError("SMS Central credentials not configured ({Section}:User|Password) — message {MessageId} deferred",
                SmsCentralOptions.SectionName, sms.MessageId);
            return new(false, null, "not_configured", "SMS Central credentials are not configured", Retryable: true);
        }

        var form = new Dictionary<string, string>
        {
            ["USERNAME"] = o.User,
            ["PASSWORD"] = o.Password,
            ["ACTION"] = "send",
            ["ORIGINATOR"] = sms.Originator,
            ["RECIPIENT"] = ToRecipient(sms.To),
            ["REFERENCE"] = sms.MessageId.ToString("N"),
            ["MESSAGE_TEXT"] = sms.Body,
        };

        using var response = await http.PostAsync(o.BaseUrl, new FormUrlEncodedContent(form), ct);
        var body = (await response.Content.ReadAsStringAsync(ct)).Trim();

        if (!response.IsSuccessStatusCode)
            return new(false, null, ((int)response.StatusCode).ToString(),
                $"HTTP {(int)response.StatusCode} from SMS Central", Retryable: true);

        if (body == "0")
            return new(true, null, null, null);

        var split = body.Split(' ', 2);
        var code = split[0];
        var text = split.Length > 1 ? split[1] : "";

        if (code == "513")
        {
            // Our uuid collided ⇒ the prior submit succeeded — reconcile, don't resend.
            logger.LogWarning("SMS Central 513 duplicate REFERENCE for message {MessageId} — treating as previously accepted", sms.MessageId);
            return new(true, null, "513", "duplicate REFERENCE upstream — prior submit accepted");
        }

        if (HardRejects.Contains(code))
            return new(false, null, code, text, Retryable: false);

        if (code is "500" or "536")
            return new(false, null, code, text, Retryable: true);

        logger.LogWarning("Unmapped SMS Central response code {Code} for message {MessageId} — treating as retryable", code, sms.MessageId);
        return new(false, null, code, text, Retryable: true);
    }

    /// <summary>RECIPIENT is international format WITHOUT the plus (design §4.1).</summary>
    internal static string ToRecipient(string to) => to.StartsWith('+') ? to[1..] : to;
}
