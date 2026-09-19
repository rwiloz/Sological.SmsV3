using Sological.Sms.Core.Sms;

namespace Sological.Sms.Tests;

/// <summary>The outbound fold (ruled 2026-09-19): every character with a GSM-7 equivalent folds onto it
/// before a body is counted, stored, sent, billed and matched against the receipt echo; anything without
/// one stays, so a body goes UCS-2 only when such a character remains. The folded characters are built
/// from their code points: several are invisible, and the line separator is a line break to the compiler.</summary>
public class GsmFoldTests
{
    private static string U(int codePoint) => char.ConvertFromUtf32(codePoint);

    private static readonly string CurlyApostrophe = U(0x2019);   // the one the smoke tripped on

    [Fact]
    public void Foldable_characters_fold_to_their_gsm7_equivalent()
    {
        var cases = new (string Body, string Expected)[]
        {
            ($"I{CurlyApostrophe}ve sent a code", "I've sent a code"),
            ($"{U(0x2018)}quoted{U(0x2019)} and {U(0x201C)}double{U(0x201D)} {U(0x00AB)}guillemets{U(0x00BB)}", "'quoted' and \"double\" \"guillemets\""),
            ($"10{U(0x2013)}12 weeks {U(0x2014)} or so {U(0x2212)} minus", "10-12 weeks - or so - minus"),
            ($"wait{U(0x2026)}", "wait..."),
            ($"a{U(0x00A0)}b{U(0x2009)}c{U(0x3000)}d{U(0x0009)}e", "a b c d e"),
            ($"next{U(0x0085)}line{U(0x2028)}break", "next\nline\nbreak"),
            ($"zero{U(0x200B)}width{U(0xFEFF)}{U(0x2060)}", "zerowidth"),
            ($"soft{U(0x00AD)}hyphen", "softhyphen"),
            ($"{U(0xFF28)}{U(0xFF49)}{U(0xFF01)} {U(0xFF40)}x", "Hi! 'x"),
            ($"{U(0xFB00)}ort {U(0xFB01)}nal {U(0xFB02)}ow {U(0xFB03)}x {U(0xFB04)}y", "ffort final flow ffix ffly"),
            ($"{U(0x2022)} item {U(0x00BD)}{U(0x2044)}{U(0x00BD)}", $"* item {U(0x00BD)}/{U(0x00BD)}"),
            ("a`b", "a'b"),
        };

        foreach (var (body, expected) in cases)
            GsmFold.Apply(body).Should().Be(expected, "the fold of {0}", body);
    }

    [Fact]
    public void Characters_without_an_equivalent_are_left_alone()
    {
        var bodies = new[]
        {
            "plain ascii, with 'quotes' and \"doubles\" - ok... (0412) [ok]?",
            "GSM basics: @£$¥èéùìòÇ ÄÖÑÜ àäöñü § ¿¡ Ø ß É",
            $"smile {U(0x1F600)} stays",
            $"{U(0x65E5)}{U(0x672C)}{U(0x8A9E)} stays",
            "José stays José",
            "",
        };

        foreach (var body in bodies)
            ReferenceEquals(GsmFold.Apply(body), body).Should().BeTrue("nothing folds in {0}", body);
    }

    [Fact]
    public void The_joiners_stay_because_they_bind_emoji_sequences_and_letters()
    {
        var family = U(0x1F468) + U(0x200D) + U(0x1F469) + U(0x200D) + U(0x1F467);
        var persian = U(0x0645) + U(0x06CC) + U(0x200C) + U(0x062E) + U(0x0648) + U(0x0627) + U(0x0647) + U(0x0645);

        ReferenceEquals(GsmFold.Apply(family), family).Should().BeTrue();
        ReferenceEquals(GsmFold.Apply(persian), persian).Should().BeTrue();
    }

    [Fact]
    public void Direction_marks_go_only_when_the_rest_of_the_body_is_gsm7()
    {
        // A handset wraps a pasted number in an embedding: the marks were the only thing forcing UCS-2.
        var pasted = $"Call {U(0x202A)}0412 345 678{U(0x202C)} today{U(0x200E)}";
        GsmFold.Apply(pasted).Should().Be("Call 0412 345 678 today");
        SmsParts.Count(GsmFold.Apply(pasted)).Should().Be(1);

        // A bidirectional body is UCS-2 regardless; the marks carry meaning and stay as written.
        var arabic = U(0x202B) + U(0x0645) + U(0x0631) + U(0x062D) + U(0x0628) + U(0x0627) + U(0x202C) + " ok";
        ReferenceEquals(GsmFold.Apply(arabic), arabic).Should().BeTrue();
    }

    [Fact]
    public void An_unchanged_body_is_the_same_instance_and_folding_is_idempotent()
    {
        const string plain = "nothing to fold";
        ReferenceEquals(GsmFold.Apply(plain), plain).Should().BeTrue();

        var once = GsmFold.Apply($"it{U(0x2019)}s {U(0x201C)}fine{U(0x201D)} {U(0x2013)} really{U(0x2026)}");
        once.Should().Be("it's \"fine\" - really...");
        ReferenceEquals(GsmFold.Apply(once), once).Should().BeTrue();
    }

    [Fact]
    public void A_curly_apostrophe_no_longer_sends_the_whole_message_ucs2()
    {
        // The collector's protections text from the 2026-09-19 smoke: three curly apostrophes sent it UCS-2, 5 parts.
        var body = $"In NSW, while you{CurlyApostrophe}re keeping to an agreed hardship arrangement, your energy won{CurlyApostrophe}t be " +
                   "disconnected for non-payment, and there are no late fees or debt-collection referrals; we can " +
                   "review and adjust the arrangement as things change, usually in about 12 weeks. Does that make " +
                   $"sense before we talk about what{CurlyApostrophe}s made things difficult?";

        SmsParts.Count(body).Should().Be(5, "UCS-2 carries 67 characters a part");
        SmsParts.Count(GsmFold.Apply(body)).Should().Be(3, "folded, the same text is GSM-7 at 153 a part");
    }

    [Fact]
    public void An_emoji_keeps_the_body_ucs2_after_the_rest_is_folded()
    {
        var smile = U(0x1F600);
        var folded = GsmFold.Apply($"It{CurlyApostrophe}s done {smile}");

        folded.Should().Be($"It's done {smile}");
        ReferenceEquals(GsmFold.Apply(folded), folded).Should().BeTrue();
        SmsParts.Count(folded).Should().Be(1);
        SmsParts.NonGsm7CodePoints(folded).Should().Equal(new[] { "U+D83D", "U+DE00" }, "the emoji's two UTF-16 units are what is outside the set");
    }
}
