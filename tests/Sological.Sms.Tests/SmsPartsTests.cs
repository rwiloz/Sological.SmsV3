using Sological.Sms.Core.Sms;

namespace Sological.Sms.Tests;

/// <summary>Parts are billing — boundaries must be exact (160/153 GSM7, 70/67 UCS-2).</summary>
public class SmsPartsTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(160, 1)]
    [InlineData(161, 2)]
    [InlineData(306, 2)]
    [InlineData(307, 3)]
    [InlineData(459, 3)]
    [InlineData(460, 4)]
    public void Gsm7_Boundaries(int chars, short expectedParts)
        => SmsParts.Count(new string('a', chars)).Should().Be(expectedParts);

    [Fact]
    public void Gsm7_ExtensionChars_CostTwoSeptets()
    {
        SmsParts.Count(new string('€', 80)).Should().Be(1, "80 € = 160 septets, exactly one part");
        SmsParts.Count(new string('€', 81)).Should().Be(2, "81 € = 162 septets");
        SmsParts.Count("[]{}^~\\|€").Should().Be(1);
    }

    [Theory]
    [InlineData(70, 1)]
    [InlineData(71, 2)]
    [InlineData(134, 2)]
    [InlineData(135, 3)]
    public void Ucs2_Boundaries(int chars, short expectedParts)
        => SmsParts.Count(new string('日', chars)).Should().Be(expectedParts);

    [Fact]
    public void Ucs2_SurrogatePairs_CostTwoUnits()
    {
        SmsParts.Count(string.Concat(Enumerable.Repeat("😀", 35))).Should().Be(1, "35 emoji = 70 UTF-16 units");
        SmsParts.Count(string.Concat(Enumerable.Repeat("😀", 36))).Should().Be(2);
    }

    [Fact]
    public void Gsm7_BasicSetCharacters_StayGsm7()
        => SmsParts.Count("Hello @£$¥ (0412) [ok]?\nLine2 ÄÖÑÜ àèéùìò").Should().Be(1);
}
