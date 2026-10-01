using Cmsify.Core.Validation;

namespace Cmsify.Core.Tests;

public sealed class PasswordRulesTests
{
    [Theory]
    [InlineData(" leading-space-pw")]
    [InlineData("trailing-space-pw ")]
    [InlineData("trailing-newline-pw\n")]
    [InlineData("embedded\ttab-pw")]
    [InlineData("non-breaking\u00A0space")]
    [InlineData("figure\u2007space-pw")]
    [InlineData("narrow\u202Fnbsp-pw")]
    [InlineData("zero-width\u200Bspace")]
    [InlineData("zero-width\u200Djoiner")]
    [InlineData("bom\uFEFFinside")]
    [InlineData("bidi\u200Emark")]
    [InlineData("line\u2028separator")]
    [InlineData("paragraph\u2029separator")]
    [InlineData("nul\0char")]
    [InlineData("")]
    public void IsValid_RejectsInvisibleOrAmbiguousPasswords(string password)
    {
        PasswordRules.IsValid(password).ShouldBeFalse();
    }

    [Fact]
    public void IsValid_RejectsNull()
    {
        PasswordRules.IsValid(null).ShouldBeFalse();
    }

    [Theory]
    [InlineData("internal spaces are fine")]
    [InlineData("p@$$ w0rd!&+=%\"'<>")]
    [InlineData("emoji-\U0001F600\U0001F680-pw")]
    [InlineData("caf\u00e9-precomposed")]
    [InlineData("cafe\u0301-decomposed")]
    [InlineData("curly \u2018quotes\u2019 and \u201Cdouble\u201D")]
    [InlineData("pw-ends-with-emoji-\U0001F512")]
    [InlineData("\U0001F512starts-with-emoji")]
    public void IsValid_AllowsOrdinaryPasswords(string password)
    {
        PasswordRules.IsValid(password).ShouldBeTrue();
    }
}
