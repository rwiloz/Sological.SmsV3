using Microsoft.Extensions.Options;
using Sological.Sms.Service.Upstream;

namespace Sological.Sms.Service.Ingress;

public sealed class IngressOptions
{
    public const string SectionName = "SologicalSms:Ingress";

    /// <summary>The 2018 live captures show SMS Central pushes WITHOUT credentials, so by
    /// default creds are validated only when present. Flip to true if the sub-account's
    /// pushes turn out to carry them (verify at the S3 live gate).</summary>
    public bool RequireCredentials { get; set; }

    /// <summary>Multipart inbound groups older than this are flushed partial (design §3).</summary>
    public int PartTimeoutSeconds { get; set; } = 60;

    public int SweepIntervalSeconds { get; set; } = 15;
}

public static class IngressShared
{
    /// <summary>Both receivers must answer 200 body "0" — anything else triggers SMS
    /// Central's retry engine (their duplication contract, design §5).</summary>
    public static IResult Ack() => Results.Text("0");

    /// <summary>Creds arrive in the query string on their model (USERNAME or USER_NAME —
    /// the legacy gateway read the underscore spelling). Validate when present; reject
    /// mismatches; absence is tolerated unless RequireCredentials.</summary>
    public static IResult? CheckCredentials(IQueryCollection query, SmsCentralOptions creds, IngressOptions options)
    {
        var user = query["USERNAME"].ToString();
        if (user.Length == 0) user = query["USER_NAME"].ToString();
        var password = query["PASSWORD"].ToString();

        if (user.Length == 0 && password.Length == 0)
            return options.RequireCredentials ? Results.Unauthorized() : null;

        return user == creds.User && password == creds.Password ? null : Results.Unauthorized();
    }

    /// <summary>E.164-ish comparison form: digits only, AU 04xx → 614xx.</summary>
    public static string NormalizeNumber(string number)
    {
        var digits = new string(number.Where(char.IsAsciiDigit).ToArray());
        return digits.StartsWith("04") ? "61" + digits[1..] : digits;
    }

    public static string? Truncate(string value, int max)
        => value.Length == 0 ? null : value.Length <= max ? value : value[..max];
}
