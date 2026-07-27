using Sological.Sms.Core.Sms;

namespace Sological.Sms.Tests;

public class RecipientValidatorTests
{
    [Theory]
    [InlineData("0412345678", "+61412345678")]
    [InlineData(" 0412345678 ", "+61412345678")]
    [InlineData("+61412345678", "+61412345678")]
    [InlineData("+441234567890", "+441234567890")]
    public void ValidRecipients_NormalizeToE164(string input, string expected)
    {
        var result = RecipientValidator.Validate(input);
        result.IsValid.Should().BeTrue();
        result.Normalized.Should().Be(expected);
    }

    [Theory]
    [InlineData("0400000000")]   // the legacy sentinel
    [InlineData("+61400000000")] // same sentinel in E.164 clothing
    [InlineData("0512345678")]   // not an AU mobile prefix
    [InlineData("041234567")]    // too short
    [InlineData("04123456789")]  // too long
    [InlineData("+123")]         // too short for international
    [InlineData("not-a-number")]
    [InlineData("")]
    public void InvalidRecipients_AreRejected_WithAReason(string input)
    {
        var result = RecipientValidator.Validate(input);
        result.IsValid.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }
}
