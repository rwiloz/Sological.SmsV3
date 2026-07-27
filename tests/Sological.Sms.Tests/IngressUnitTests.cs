using Sological.Sms.Core.Sms;
using Sological.Sms.Core.Upstream;
using Sological.Sms.Service.Ingress;

namespace Sological.Sms.Tests;

public class UdhParserTests
{
    [Theory]
    [InlineData("050003E80303", "E8", 3, 3)] // live capture
    [InlineData("050003710202", "71", 2, 2)] // live capture
    [InlineData("0500038A0302", "8A", 3, 2)] // SMS Central docs example
    public void EightBitConcat_Parses(string udh, string group, int total, int part)
    {
        var info = UdhParser.ParseConcat(udh)!;
        info.Should().NotBeNull();
        info.GroupRef.Should().Be(group);
        info.TotalParts.Should().Be(total);
        info.PartNo.Should().Be(part);
    }

    [Fact]
    public void SixteenBitConcat_Parses()
    {
        var info = UdhParser.ParseConcat("060804ABCD0201")!;
        info.GroupRef.Should().Be("ABCD");
        info.TotalParts.Should().Be(2);
        info.PartNo.Should().Be(1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("zz")]
    [InlineData("0500")]          // truncated
    [InlineData("050003AA0004")]  // part 4 of 0 — invalid
    [InlineData("050003AA0203")]  // part 3 of 2 — invalid
    public void Garbage_ReturnsNull(string? udh)
        => UdhParser.ParseConcat(udh).Should().BeNull();
}

public class SmsBinaryDecoderTests
{
    [Fact]
    public void Dcs8_DecodesUcs2BigEndian()
        => SmsBinaryDecoder.Decode("004800690021", 8).Should().Be("Hi!");

    [Fact]
    public void OtherDcs_DecodesLatin1()
        => SmsBinaryDecoder.Decode("48656C6C6FA1", 0).Should().Be("Hello¡");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("XYZ")]
    [InlineData("ABC")] // odd length
    public void InvalidHex_ReturnsNull(string? hex)
        => SmsBinaryDecoder.Decode(hex, 0).Should().BeNull();
}

/// <summary>Design §5.1: RESULT first, STATUS refines; 503 = expiry; DELIVRD/DELIVERD both
/// appear in SMS Central's own docs.</summary>
public class DeliveryVerdictMappingTests
{
    [Theory]
    [InlineData("1", null, DeliveryVerdict.Delivered)]
    [InlineData("1", "FAILED", DeliveryVerdict.Delivered)] // RESULT wins
    [InlineData("0", null, DeliveryVerdict.Sent)]
    [InlineData("536", null, DeliveryVerdict.Sent)]
    [InlineData("503", null, DeliveryVerdict.Expired)]
    [InlineData("550", null, DeliveryVerdict.Failed)]
    [InlineData("505", "", DeliveryVerdict.Failed)]
    [InlineData(null, "DELIVRD", DeliveryVerdict.Delivered)]
    [InlineData("", "DELIVERD", DeliveryVerdict.Delivered)]
    [InlineData("", "BUFFRED", DeliveryVerdict.Sent)]
    [InlineData("", "FAILED", DeliveryVerdict.Failed)]
    public void Maps(string? result, string? status, DeliveryVerdict expected)
        => SmsCentralDeliveryIngress.MapVerdict(result, status).Should().Be(expected);

    [Theory]
    [InlineData("", "")]
    [InlineData("banana", "WEIRD")]
    public void Unparseable_ReturnsNull(string result, string status)
        => SmsCentralDeliveryIngress.MapVerdict(result, status).Should().BeNull();
}

public class NumberNormalizationTests
{
    [Theory]
    [InlineData("0418726844", "61418726844")]
    [InlineData("61418726844", "61418726844")]
    [InlineData("+61418726844", "61418726844")]
    [InlineData("AIWorkforce", "")] // alpha originators normalize to nothing — never match a number
    public void Normalizes(string input, string expected)
        => IngressShared.NormalizeNumber(input).Should().Be(expected);
}
