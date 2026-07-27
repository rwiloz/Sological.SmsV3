namespace Sological.Sms.Core.Sms;

/// <summary>Part counting per GSM 03.38: GSM7 bodies fit 160 septets in one part, 153 per
/// part when concatenated; anything outside the GSM7 set goes UCS-2 (70 / 67 UTF-16 units).
/// Parts are the billing dimension (~2.7 parts/send on live traffic).</summary>
public static class SmsParts
{
    private const string Gsm7Basic =
        "@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞÆæßÉ !\"#¤%&'()*+,-./0123456789:;<=>?" +
        "¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà";

    // Extension table characters cost TWO septets (ESC + char).
    private const string Gsm7Extension = "\f^{}\\[~]|€";

    private static readonly HashSet<char> Basic = [.. Gsm7Basic];
    private static readonly HashSet<char> Extension = [.. Gsm7Extension];

    public static short Count(string body)
    {
        var septets = 0;
        var gsm7 = true;
        foreach (var c in body)
        {
            if (Basic.Contains(c)) septets += 1;
            else if (Extension.Contains(c)) septets += 2;
            else { gsm7 = false; break; }
        }

        if (gsm7)
            return (short)(septets <= 160 ? 1 : Math.Ceiling(septets / 153.0));

        // UCS-2: UTF-16 code units (a surrogate pair costs 2).
        var units = body.Length;
        return (short)(units <= 70 ? 1 : Math.Ceiling(units / 67.0));
    }
}
