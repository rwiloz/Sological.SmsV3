using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
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
    // Modern webhook-engine word statuses (templated pushes carry no RESULT):
    [InlineData(null, "delivered", DeliveryVerdict.Delivered)]
    [InlineData(null, "enroute", DeliveryVerdict.Sent)]
    [InlineData(null, "submitted", DeliveryVerdict.Sent)]
    [InlineData(null, "rejected", DeliveryVerdict.Rejected)]
    [InlineData(null, "expired", DeliveryVerdict.Expired)]
    [InlineData(null, "failed", DeliveryVerdict.Failed)]
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

/// <summary>The push reader survives what the webhook engine actually sends: a handset's trailing newline
/// written into the JSON string verbatim (invalid JSON no parser accepts) — the 2026-09-19 smoke's two lost
/// replies, quarantined with an empty body.</summary>
public class ReadParamsTests
{
    private static HttpRequest JsonPost(string body)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "POST";
        ctx.Request.ContentType = "application/json";
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return ctx.Request;
    }

    [Fact]
    public async Task A_raw_newline_inside_a_json_string_is_escaped_and_the_push_parses()
    {
        var raw = "{\"moId\":\"mo-1\",\"mtId\":\"mt-1\",\"sourceAddress\":\"+61412000011\"," +
                  "\"moContent\":\"June Albright, 14 Kanangra Cres, Ruse NSW 2560\n\"}";

        var p = await IngressShared.ReadParamsAsync(JsonPost(raw), NullLogger.Instance);

        p["moContent"].Should().Be("June Albright, 14 Kanangra Cres, Ruse NSW 2560\n", "the reply as the handset sent it");
        p["sourceAddress"].Should().Be("+61412000011");
        p.Should().NotContainKey("_raw");
    }

    [Fact]
    public async Task Well_formed_json_reads_as_before_and_garbage_keeps_the_raw_body()
    {
        var p = await IngressShared.ReadParamsAsync(JsonPost("{\"moContent\":\"line one\\nline two\"}"), NullLogger.Instance);
        p["moContent"].Should().Be("line one\nline two");

        var garbage = await IngressShared.ReadParamsAsync(JsonPost("not json at all"), NullLogger.Instance);
        garbage["_raw"].Should().Be("not json at all");
        garbage["_contentType"].Should().Be("application/json");
    }

    [Theory]
    [InlineData("{\"a\":\"x\ny\"}", "{\"a\":\"x\\ny\"}")]
    [InlineData("{\"a\":\"tab\there\"}", "{\"a\":\"tab\\there\"}")]
    [InlineData("{\"a\":\"quote \\\" then\nnewline\"}", "{\"a\":\"quote \\\" then\\nnewline\"}")]
    [InlineData("{\"a\":\"\u0001\"}", "{\"a\":\"\\u0001\"}")]
    public void Only_control_characters_inside_strings_are_escaped(string raw, string expected)
        => IngressShared.EscapeControlCharactersInStrings(raw).Should().Be(expected);

    [Fact]
    public void Text_with_nothing_to_escape_comes_back_as_the_same_instance()
    {
        const string json = "{\"a\":1}\n";
        ReferenceEquals(IngressShared.EscapeControlCharactersInStrings(json), json).Should().BeTrue("the newline is outside the string");
    }
}
