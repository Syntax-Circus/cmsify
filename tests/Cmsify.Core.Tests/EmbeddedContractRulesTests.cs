using System.Text.Json;
using Cmsify.Core.EmbeddedContent;
using Cmsify.Core.Domain.Enums;
using Shouldly;

namespace Cmsify.Core.Tests;

public sealed class EmbeddedContractRulesTests
{
    [Fact]
    public void CanonicalDefinitionIgnoresJsonPropertyOrder()
    {
        var first = Contract("{\"maxLength\":200,\"formatHint\":\"plaintext\"}");
        var second = Contract("{\"formatHint\":\"plaintext\",\"maxLength\":200}");
        EmbeddedContractRules.ValidateAndCopy(first).IsSuccess.ShouldBeTrue();
        EmbeddedContractRules.Fingerprint(first).Length.ShouldBe(64);
        EmbeddedContractRules.Fingerprint(first).ShouldBe(EmbeddedContractRules.Fingerprint(second));
    }

    [Theory]
    [InlineData("json", false)]
    [InlineData("plaintext", true)]
    public void EmbeddedTextHintsHaveExplicitSearchSemantics(string hint, bool searchable)
    {
        var contract = Contract($$"""{"maxLength":262144,"formatHint":"{{hint}}"}""");
        EmbeddedContractRules.ValidateAndCopy(contract).IsSuccess.ShouldBeTrue();
        Cmsify.Core.Services.TextFormatHints.IsSearchIndexable(
            Cmsify.Core.Services.TextFormatHints.GetEffectiveHint(contract.Fields[0].FieldConfig)).ShouldBe(searchable);
    }

    [Fact]
    public void JsonAndPlaintextContractsHaveDifferentFingerprints()
    {
        var json = Contract("{\"maxLength\":262144,\"formatHint\":\"json\"}");
        var text = Contract("{\"maxLength\":262144,\"formatHint\":\"plaintext\"}");
        EmbeddedContractRules.Fingerprint(json).ShouldNotBe(EmbeddedContractRules.Fingerprint(text));
    }

    [Theory]
    [InlineData("{\"maxLength\":200,\"formatHint\":\"JSON\"}")]
    [InlineData("{\"maxLength\":200,\"formatHint\":\"json\",\"validateFormat\":true}")]
    [InlineData("{\"maxLength\":200,\"formatHint\":\"json\",\"unknown\":true}")]
    [InlineData("{\"maxLength\":200,\"maxLength\":300}")]
    [InlineData("{\"maxLength\":200,\"unknown\":true}")]
    [InlineData("{\"maxLength\":0}")]
    [InlineData("{\"maxLength\":200,\"formatHint\":\"html\"}")]
    public void DuplicateKeysAndUnknownConfigDeny(string config)
        => EmbeddedContractRules.ValidateAndCopy(Contract(config)).IsFailure.ShouldBeTrue();

    [Fact]
    public void DefinitionOwnsFieldsAndJson()
    {
        using var document = JsonDocument.Parse("{\"maxLength\":200}");
        var contract = Contract("{\"maxLength\":200}");
        var fields = new List<EmbeddedTemplateField> { contract.Fields[0] with { FieldConfig = document.RootElement } };
        var result = EmbeddedContractRules.ValidateAndCopy(contract with { Fields = fields });
        fields.Clear();
        document.Dispose();
        result.Value!.Fields[0].FieldConfig.GetProperty("maxLength").GetInt32().ShouldBe(200);
    }

    private static EmbeddedTemplateContract Contract(string config)
        => new("sample.v1", "Sample", "sample-v1", "name", new[] {
            new EmbeddedTemplateField("name", "Name", 0, true, 1, 1, PrimitiveType.Text, ValueKind.Text,
                CompositionMode.Inline, false, JsonSerializer.Deserialize<JsonElement>(config)) });
}
