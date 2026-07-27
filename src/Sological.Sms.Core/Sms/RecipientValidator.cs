using System.Text.RegularExpressions;

namespace Sological.Sms.Core.Sms;

public sealed record RecipientValidation(bool IsValid, string? Normalized, string? Error);

/// <summary>Local recipient validation (design §6.1a): AU-mobile shape (04 + 8 digits) or
/// +international; the 0400000000 sentinel is rejected. Runs pre-dispatch — saves the
/// upstream round-trip (the legacy processor's `I` before upstream 525 ever fired).
/// Valid AU numbers normalize to E.164 (+614…).</summary>
public static partial class RecipientValidator
{
    [GeneratedRegex(@"^04\d{8}$")]
    private static partial Regex AuMobile();

    [GeneratedRegex(@"^\+\d{8,15}$")]
    private static partial Regex International();

    public static RecipientValidation Validate(string to)
    {
        var t = to.Trim();

        if (AuMobile().IsMatch(t))
        {
            return t == "0400000000"
                ? new(false, null, "recipient is the 0400000000 sentinel")
                : new(true, "+61" + t[1..], null);
        }

        if (International().IsMatch(t))
        {
            return t == "+61400000000"
                ? new(false, null, "recipient is the 0400000000 sentinel")
                : new(true, t, null);
        }

        return new(false, null, "recipient must be an AU mobile (04xxxxxxxx) or +international");
    }
}
