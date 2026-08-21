using Cmsify.Core.Domain.ValueObjects;

namespace Cmsify.Core.Tests;

public sealed class SlugRulesTests
{
    [Theory]
    [InlineData("yes-no")]
    [InlineData("yes_no")]
    [InlineData("v2")]
    [InlineData("blog-post_2")]
    public void IsValid_WithCanonicalSlug_ReturnsTrue(string slug)
    {
        SlugRules.IsValid(slug).ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("Yes-No")]
    [InlineData("yes/no")]
    [InlineData("yes no")]
    [InlineData("-yes")]
    [InlineData("yes-")]
    [InlineData("yes--no")]
    [InlineData("yes_-no")]
    [InlineData("café")]
    public void IsValid_WithNonCanonicalSlug_ReturnsFalse(string slug)
    {
        SlugRules.IsValid(slug).ShouldBeFalse();
    }

    [Fact]
    public void IsValid_AtMaximumLength_ReturnsTrue()
    {
        SlugRules.IsValid(new string('a', SlugRules.MaxLength)).ShouldBeTrue();
    }

    [Fact]
    public void IsValid_OverMaximumLength_ReturnsFalse()
    {
        SlugRules.IsValid(new string('a', SlugRules.MaxLength + 1)).ShouldBeFalse();
    }
}
