using System.Text;

namespace Sological.Sms.Core.Sms;

/// <summary>Folds every character that has a GSM 03.38 equivalent onto that equivalent before a
/// body is counted, stored, sent, billed and matched against the upstream's receipt echo: the
/// "smart" punctuation and invisible characters that word processors, handsets and language
/// models produce (curly quotes and apostrophes, dashes, the ellipsis, non-breaking and thin
/// spaces, the tab, zero-width marks, the soft hyphen, fullwidth ASCII, the Latin ligatures). One
/// such character sends the whole message UCS-2 (70 characters a part instead of 160) and comes
/// back transliterated in the delivery receipt, so the content match fails. A character with no
/// GSM-7 equivalent (an emoji, another script, an accented letter outside the set) is left as it
/// is, so a body goes UCS-2 only when one remains; the joiners (U+200C, U+200D) stay too — they
/// bind emoji sequences and Persian and Indic letters — and the direction marks a handset wraps a
/// pasted number in go only when the rest of the body is GSM-7, since in a bidirectional body they
/// carry meaning and the body is UCS-2 regardless. Idempotent: folding a folded body changes
/// nothing. Code points are written as numbers: several of these characters are invisible or are
/// line breaks to the compiler, so they never appear literally in this source.</summary>
public static class GsmFold
{
    private static readonly Dictionary<char, string> Map = Build();

    private static readonly HashSet<char> DirectionMarks =
        [.. new[] { 0x200E, 0x200F, 0x202A, 0x202B, 0x202C, 0x202D, 0x202E, 0x2066, 0x2067, 0x2068, 0x2069 }.Select(cp => (char)cp)];

    private static Dictionary<char, string> Build()
    {
        var map = new Dictionary<char, string>();

        // Apostrophes, single quotes and primes: the GSM-7 apostrophe (the backtick is the one plain-ASCII
        // character outside the set, so it folds too).
        foreach (var cp in new[] { 0x2018, 0x2019, 0x201A, 0x201B, 0x2032, 0x2035, 0x02BC, 0x00B4, 0x0060 })
            map[(char)cp] = "'";

        // Double quotes and guillemets: the GSM-7 double quote.
        foreach (var cp in new[] { 0x201C, 0x201D, 0x201E, 0x201F, 0x2033, 0x00AB, 0x00BB })
            map[(char)cp] = "\"";

        // Hyphens, dashes and the minus sign: the hyphen-minus; the soft hyphen disappears.
        foreach (var cp in new[] { 0x2010, 0x2011, 0x2012, 0x2013, 0x2014, 0x2015, 0x2212 })
            map[(char)cp] = "-";
        map[(char)0x00AD] = "";

        // The ellipsis: three full stops.
        map[(char)0x2026] = "...";

        // Every Unicode space and the tab: the plain space. The next-line, line and paragraph separators: a newline.
        foreach (var cp in new[] { 0x0009, 0x00A0, 0x2002, 0x2003, 0x2004, 0x2005, 0x2006, 0x2007, 0x2008, 0x2009, 0x200A, 0x202F, 0x205F, 0x3000 })
            map[(char)cp] = " ";
        foreach (var cp in new[] { 0x0085, 0x2028, 0x2029 })
            map[(char)cp] = "\n";

        // Invisible marks that carry nothing a handset shows: removed. The joiners are not among them.
        foreach (var cp in new[] { 0x200B, 0x2060, 0xFEFF })
            map[(char)cp] = "";

        // Punctuation with a plain equivalent.
        map[(char)0x2022] = "*";
        map[(char)0x2044] = "/";
        map[(char)0x2215] = "/";

        // The Latin ligatures.
        map[(char)0xFB00] = "ff";
        map[(char)0xFB01] = "fi";
        map[(char)0xFB02] = "fl";
        map[(char)0xFB03] = "ffi";
        map[(char)0xFB04] = "ffl";

        return map;
    }

    /// <summary>The body with every foldable character folded; the same instance when nothing folds.</summary>
    public static string Apply(string body)
    {
        var folded = FoldMapped(body);
        if (!folded.Any(DirectionMarks.Contains))
            return folded;

        // A handset wraps a pasted number in direction marks; in an otherwise GSM-7 body they are the only
        // thing forcing UCS-2 and mean nothing, so they go. In a bidirectional body they carry meaning and the
        // body is UCS-2 regardless, so they stay as written.
        var stripped = new string(folded.Where(c => !DirectionMarks.Contains(c)).ToArray());
        return SmsParts.IsGsm7(stripped) ? stripped : folded;
    }

    private static string FoldMapped(string body)
    {
        StringBuilder? folded = null;
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            var replacement = ReplacementFor(c);
            if (replacement is null)
            {
                folded?.Append(c);
                continue;
            }
            folded ??= new StringBuilder(body.Length).Append(body, 0, i);
            folded.Append(replacement);
        }
        return folded?.ToString() ?? body;
    }

    private static string? ReplacementFor(char c)
    {
        if (Map.TryGetValue(c, out var mapped))
            return mapped;
        // Fullwidth ASCII (U+FF01 to U+FF5E): the ASCII it mirrors, itself folded when that is foldable (the backtick).
        if (c >= (char)0xFF01 && c <= (char)0xFF5E)
        {
            var ascii = (char)(c - 0xFF01 + 0x21);
            return Map.TryGetValue(ascii, out var asciiMapped) ? asciiMapped : ascii.ToString();
        }
        return null;
    }
}
