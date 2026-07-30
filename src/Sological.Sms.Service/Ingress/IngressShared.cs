using System.Text;
using Sological.Sms.Service.Upstream;

namespace Sological.Sms.Service.Ingress;

public sealed class IngressOptions
{
    public const string SectionName = "SologicalSms:Ingress";

    /// <summary>Shared secret for the SLVERIFY header (Ray's model, 2026-07-27): the
    /// portal attaches it to every push; headers stay out of URLs, logs, and our stored
    /// payloads. Secret name: SologicalSms--Ingress--VerifyKey.</summary>
    public string? VerifyKey { get; set; }

    /// <summary>The 2018 live captures show SMS Central pushes with NO verification at
    /// all, so by default the SLVERIFY header / creds params are validated only when
    /// present. Flip to true once the portal is confirmed sending SLVERIFY — then every
    /// push must verify.</summary>
    public bool RequireVerification { get; set; }

    /// <summary>Multipart inbound groups older than this are flushed partial (design §3).</summary>
    public int PartTimeoutSeconds { get; set; } = 60;

    public int SweepIntervalSeconds { get; set; } = 15;
}

public static class IngressShared
{
    /// <summary>Both receivers must answer 200 body "0" — anything else triggers SMS
    /// Central's retry engine (their duplication contract, design §5).</summary>
    public static IResult Ack() => Results.Text("0");

    /// <summary>SMS Central pushes GET with query params historically; the modern webhook
    /// engine POSTs templated bodies as FORM_ENCODED or JSON — accept all three, body
    /// fields merged under the query (query wins on duplicates). Flat JSON values map
    /// verbatim; one level of nesting flattens to `PARENT.child`, and a nested
    /// `METADATA.REFERENCE` (the Velocity metadata dump) is promoted to `REFERENCE` so
    /// correlation works if the platform round-trips our uuid there. The returned
    /// dictionary is also the stored payload.</summary>
    public static async Task<Dictionary<string, string>> ReadParamsAsync(HttpRequest request)
    {
        // Case-insensitive: templated pushes are hand-typed in the portal.
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in request.Query)
            parameters[kv.Key] = kv.Value.ToString();
        if (!HttpMethods.IsPost(request.Method))
            return parameters;

        if (request.HasFormContentType)
        {
            foreach (var field in await request.ReadFormAsync())
                parameters.TryAdd(field.Key, field.Value.ToString());
            return parameters;
        }

        // Anything else: read the body and TRY JSON regardless of the declared
        // content-type (the webhook engine's declaration proved untrustworthy at the
        // live gate). If nothing parses, the raw body is preserved in the payload — a
        // push is never silently reduced to {} again.
        string raw;
        using (var reader = new StreamReader(request.Body))
            raw = (await reader.ReadToEndAsync()).Trim();
        if (raw.Length == 0)
            return parameters;

        var before = parameters.Count;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (var property in doc.RootElement.EnumerateObject())
                {
                    if (property.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        foreach (var child in property.Value.EnumerateObject())
                            parameters.TryAdd($"{property.Name}.{child.Name}", JsonScalar(child.Value));
                    }
                    else
                    {
                        parameters.TryAdd(property.Name, JsonScalar(property.Value));
                    }
                }
                if (parameters.TryGetValue("METADATA.REFERENCE", out var reference))
                    parameters.TryAdd("REFERENCE", reference);
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // fall through to raw capture
        }

        if (parameters.Count == before)
        {
            parameters["_raw"] = raw.Length <= 4000 ? raw : raw[..4000];
            parameters["_contentType"] = request.ContentType ?? "";
        }
        return parameters;
    }

    private static string JsonScalar(System.Text.Json.JsonElement element)
        => element.ValueKind == System.Text.Json.JsonValueKind.String ? element.GetString()! : element.GetRawText();

    public const string VerifyHeaderName = "SLVERIFY";

    /// <summary>Verification, strongest lane first: the SLVERIFY header (mismatch → 401,
    /// match → in), then legacy creds params (USERNAME or USER_NAME — the underscore
    /// spelling is what the old gateway read). Nothing present is tolerated unless
    /// RequireVerification. State changes stay gated by the unguessable REFERENCE uuid
    /// regardless.</summary>
    public static IResult? CheckVerification(
        HttpRequest request, IReadOnlyDictionary<string, string> parameters,
        SmsCentralOptions creds, IngressOptions options)
    {
        var slVerify = request.Headers[VerifyHeaderName].ToString();
        if (slVerify.Length > 0 && !string.IsNullOrEmpty(options.VerifyKey))
        {
            return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(slVerify), Encoding.UTF8.GetBytes(options.VerifyKey))
                ? null
                : Results.Unauthorized();
        }
        // Header present but no key configured: can't verify — falls through as absent.

        var user = parameters.GetValueOrDefault("USERNAME", "");
        if (user.Length == 0) user = parameters.GetValueOrDefault("USER_NAME", "");
        var password = parameters.GetValueOrDefault("PASSWORD", "");
        if (user.Length > 0 || password.Length > 0)
            return user == creds.User && password == creds.Password ? null : Results.Unauthorized();

        return options.RequireVerification ? Results.Unauthorized() : null;
    }

    /// <summary>Stored payloads must never carry credentials — pushes CAN arrive with
    /// creds params (their model), and payload jsonb is forever.</summary>
    public static void RedactCredentials(Dictionary<string, string> parameters)
    {
        foreach (var key in new[] { "USERNAME", "USER_NAME", "PASSWORD" })
        {
            if (parameters.ContainsKey(key))
                parameters[key] = "***";
        }
    }

    /// <summary>E.164-ish comparison form: digits only, AU 04xx → 614xx.</summary>
    public static string NormalizeNumber(string number)
    {
        var digits = new string(number.Where(char.IsAsciiDigit).ToArray());
        return digits.StartsWith("04") ? "61" + digits[1..] : digits;
    }

    public static string? Truncate(string value, int max)
        => value.Length == 0 ? null : value.Length <= max ? value : value[..max];

    /// <summary>First non-empty value among alias keys — the portal's template picker
    /// names fields differently from the legacy pushes (dtId vs ID, sourceAddress vs
    /// ORIGINATOR, …); both dialects are first-class.</summary>
    public static string FirstOf(IReadOnlyDictionary<string, string> parameters, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = parameters.GetValueOrDefault(key, "");
            if (value.Length > 0) return value;
        }
        return "";
    }
}
