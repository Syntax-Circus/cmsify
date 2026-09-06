using System.Text.Json;
using Shouldly;
using Xunit;

namespace SyntaxCircus.Cmsify.Components.Tests;

public sealed class PickListFieldBindingTests
{
    [Fact]
    public void ReturnsEmptyBindingWhenFieldConfigIsNull()
    {
        var binding = PickListFieldBinding.FromFieldConfig(null);

        binding.PickListId.ShouldBeNull();
        binding.RevisionId.ShouldBeNull();
        binding.Multiple.ShouldBeFalse();
    }

    [Fact]
    public void ParsesPickListIdRevisionIdAndMultipleFromFieldConfig()
    {
        var pickListId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var json = JsonDocument.Parse($$"""
            { "picklistId": "{{pickListId}}", "picklistRevisionId": "{{revisionId}}", "multiple": true }
            """).RootElement;

        var binding = PickListFieldBinding.FromFieldConfig(json);

        binding.PickListId.ShouldBe(pickListId);
        binding.RevisionId.ShouldBe(revisionId);
        binding.Multiple.ShouldBeTrue();
    }

    [Fact]
    public void DefaultsMultipleToFalseWhenAbsent()
    {
        var json = JsonDocument.Parse("""{ "picklistId": "11111111-1111-1111-1111-111111111111" }""").RootElement;

        var binding = PickListFieldBinding.FromFieldConfig(json);

        binding.Multiple.ShouldBeFalse();
    }
}
