namespace SyntaxCircus.Cmsify.Client.Tests;

public sealed class TemplateRenderingTests
{
    [Fact]
    public void Render_KnownVariable_SubstitutesValue()
    {
        var result = CmsifyTemplateRenderer.Render(
            "Email ${{supportEmail}} for help.",
            new Dictionary<string, string?> { ["supportEmail"] = "support@example.com" });

        result.ShouldBe("Email support@example.com for help.");
    }

    [Fact]
    public void Render_UnknownToken_LeftAsLiteral()
    {
        var result = CmsifyTemplateRenderer.Render(
            "Contact ${{supprtEmail}} for help.",
            new Dictionary<string, string?> { ["supportEmail"] = "support@example.com" });

        result.ShouldBe("Contact ${{supprtEmail}} for help.");
    }

    [Fact]
    public void Render_ExplicitNullValue_RendersEmptyString()
    {
        var result = CmsifyTemplateRenderer.Render(
            "Prefix[${{maybeBlank}}]Suffix",
            new Dictionary<string, string?> { ["maybeBlank"] = null });

        result.ShouldBe("Prefix[]Suffix");
    }

    [Fact]
    public void Render_NoTokensPresent_ReturnsOriginalStringUnchanged()
    {
        const string template = "Nothing to render here.";

        var result = CmsifyTemplateRenderer.Render(
            template,
            new Dictionary<string, string?> { ["unused"] = "value" });

        result.ShouldBeSameAs(template);
    }

    [Fact]
    public void Render_EmptyVariableDictionary_ReturnsOriginalStringUnchanged()
    {
        const string template = "Has a ${{token}} but no variables supplied.";

        var result = CmsifyTemplateRenderer.Render(template, new Dictionary<string, string?>());

        result.ShouldBeSameAs(template);
    }

    [Fact]
    public void Render_MultipleTokens_MixedKnownAndUnknown()
    {
        var result = CmsifyTemplateRenderer.Render(
            "Hi ${{name}}, email ${{supportEmail}} or call ${{phone}}.",
            new Dictionary<string, string?>
            {
                ["name"] = "Jon",
                ["supportEmail"] = "support@example.com",
            });

        result.ShouldBe("Hi Jon, email support@example.com or call ${{phone}}.");
    }

    [Fact]
    public void Render_ToleratesWhitespaceInsideBraces()
    {
        var result = CmsifyTemplateRenderer.Render(
            "Email ${{ supportEmail }} for help.",
            new Dictionary<string, string?> { ["supportEmail"] = "support@example.com" });

        result.ShouldBe("Email support@example.com for help.");
    }

    [Fact]
    public void Render_IsCaseSensitive()
    {
        var result = CmsifyTemplateRenderer.Render(
            "Email ${{SupportEmail}} for help.",
            new Dictionary<string, string?> { ["supportEmail"] = "support@example.com" });

        result.ShouldBe("Email ${{SupportEmail}} for help.");
    }

    [Theory]
    [InlineData("${{}}")]
    [InlineData("${{123abc}}")]
    public void Render_MalformedToken_LeftAsLiteral(string malformedTemplate)
    {
        var result = CmsifyTemplateRenderer.Render(
            malformedTemplate,
            new Dictionary<string, string?> { ["123abc"] = "should not match" });

        result.ShouldBe(malformedTemplate);
    }

    [Fact]
    public void Render_NullTemplate_Throws() =>
        Should.Throw<ArgumentNullException>(() =>
            CmsifyTemplateRenderer.Render(null!, new Dictionary<string, string?>()));

    [Fact]
    public void Render_NullVariables_Throws() =>
        Should.Throw<ArgumentNullException>(() =>
            CmsifyTemplateRenderer.Render("template", null!));
}
