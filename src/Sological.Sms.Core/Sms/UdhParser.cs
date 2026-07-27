namespace Sological.Sms.Core.Sms;

/// <summary>Concatenation info from a GSM User Data Header (hex octets). SMS Central
/// sends these on multipart DLRs and inbound parts — e.g. 050003E80303 = group E8,
/// part 3 of 3 (last two octets are total/part, per their docs and the live captures).</summary>
public static class UdhParser
{
    public sealed record ConcatInfo(string GroupRef, int TotalParts, int PartNo);

    public static ConcatInfo? ParseConcat(string? udhHex)
    {
        if (string.IsNullOrWhiteSpace(udhHex) || udhHex.Length % 2 != 0)
            return null;

        byte[] bytes;
        try { bytes = Convert.FromHexString(udhHex); }
        catch (FormatException) { return null; }

        // bytes[0] = UDH length; then information elements: [IEI, len, data…].
        var i = 1;
        while (i + 1 < bytes.Length)
        {
            var iei = bytes[i];
            var len = bytes[i + 1];
            if (i + 2 + len > bytes.Length) return null;

            switch (iei)
            {
                case 0x00 when len == 3: // 8-bit concat reference
                    return Valid(bytes[i + 2].ToString("X2"), bytes[i + 3], bytes[i + 4]);
                case 0x08 when len == 4: // 16-bit concat reference
                    return Valid($"{bytes[i + 2]:X2}{bytes[i + 3]:X2}", bytes[i + 4], bytes[i + 5]);
            }
            i += 2 + len;
        }
        return null;
    }

    private static ConcatInfo? Valid(string group, int total, int part)
        => total >= 1 && part >= 1 && part <= total ? new(group, total, part) : null;
}
