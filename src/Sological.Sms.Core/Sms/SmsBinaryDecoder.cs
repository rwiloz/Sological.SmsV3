using System.Text;

namespace Sological.Sms.Core.Sms;

/// <summary>Decodes SMS Central's BINARY parameter (hex octets). Per their docs (captured
/// in the legacy gateway source): DCS 8 = UCS-2 (UTF-16 big-endian); any other value is
/// ASCII/Latin-1 — NOT packed GSM7.</summary>
public static class SmsBinaryDecoder
{
    public static string? Decode(string? binaryHex, int? dcs)
    {
        if (string.IsNullOrWhiteSpace(binaryHex) || binaryHex.Length % 2 != 0)
            return null;

        byte[] bytes;
        try { bytes = Convert.FromHexString(binaryHex); }
        catch (FormatException) { return null; }

        return dcs == 8
            ? Encoding.BigEndianUnicode.GetString(bytes)
            : Encoding.Latin1.GetString(bytes);
    }
}
