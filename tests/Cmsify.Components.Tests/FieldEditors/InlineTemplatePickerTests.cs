using Bunit;
using Microsoft.AspNetCore.Components;
using Shouldly;
using Xunit;

namespace SyntaxCircus.Cmsify.Components.Tests.FieldEditors;

public sealed class InlineTemplatePickerTests : BunitContext
{
    private static TemplateSummaryResponse CreateTemplate(string name) => new(
        Guid.NewGuid(), Guid.NewGuid(), name, name.ToLowerInvariant().Replace(' ', '-'), null, null);

    [Fact]
    public void RendersAllCandidates()
    {
        var candidates = new[] { CreateTemplate("Article"), CreateTemplate("Landing Page"), CreateTemplate("Author") };

        var cut = Render<InlineTemplatePicker>(parameters => parameters
            .Add(p => p.Candidates, candidates));

        var options = cut.FindAll("option");
        // +1 for the "Select a template" placeholder option.
        options.Count.ShouldBe(candidates.Length + 1);
        foreach (var candidate in candidates)
        {
            options.ShouldContain(o => o.GetAttribute("value") == candidate.Id.ToString());
        }
    }

    [Fact]
    public void SelectingCandidateRaisesTemplateSelectedWithCorrectId()
    {
        var candidates = new[] { CreateTemplate("Article"), CreateTemplate("Author") };
        Guid? selected = null;
        var cut = Render<InlineTemplatePicker>(parameters => parameters
            .Add(p => p.Candidates, candidates)
            .Add(p => p.TemplateSelected, EventCallback.Factory.Create<Guid>(this, id => selected = id)));

        cut.Find("select").Change(candidates[1].Id.ToString());

        selected.ShouldBe(candidates[1].Id);
    }

    [Fact]
    public void DisabledCandidateRendersDisabledAndDoesNotRaiseTemplateSelected()
    {
        var candidates = new[] { CreateTemplate("Article"), CreateTemplate("Author") };
        var disabledId = candidates[1].Id;
        var wasCalled = false;
        var cut = Render<InlineTemplatePicker>(parameters => parameters
            .Add(p => p.Candidates, candidates)
            .Add(p => p.DisabledTemplateIds, new HashSet<Guid> { disabledId })
            .Add(p => p.TemplateSelected, EventCallback.Factory.Create<Guid>(this, _ => wasCalled = true)));

        var disabledOption = cut.FindAll("option").Single(o => o.GetAttribute("value") == disabledId.ToString());
        disabledOption.HasAttribute("disabled").ShouldBeTrue();

        cut.Find("select").Change(disabledId.ToString());

        wasCalled.ShouldBeFalse();
        cut.Markup.ShouldContain("circular reference chain");
    }

    [Fact]
    public void ReadOnlyDisablesTheWholeControl()
    {
        var candidates = Enumerable.Range(0, 10).Select(i => CreateTemplate($"Template {i}")).ToArray();

        var cut = Render<InlineTemplatePicker>(parameters => parameters
            .Add(p => p.Candidates, candidates)
            .Add(p => p.ReadOnly, true));

        cut.Find("select").HasAttribute("disabled").ShouldBeTrue();
        // Long candidate list also renders the filter input, which must be disabled too.
        cut.Find("input").HasAttribute("disabled").ShouldBeTrue();
    }

    [Fact]
    public void LongCandidateListRendersFilterInput()
    {
        var candidates = Enumerable.Range(0, 10).Select(i => CreateTemplate($"Template {i}")).ToArray();

        var cut = Render<InlineTemplatePicker>(parameters => parameters
            .Add(p => p.Candidates, candidates));

        cut.FindAll("input").Count.ShouldBe(1);
    }

    [Fact]
    public void ShortCandidateListDoesNotRenderFilterInput()
    {
        var candidates = new[] { CreateTemplate("Article"), CreateTemplate("Author") };

        var cut = Render<InlineTemplatePicker>(parameters => parameters
            .Add(p => p.Candidates, candidates));

        cut.FindAll("input").Count.ShouldBe(0);
    }
}
