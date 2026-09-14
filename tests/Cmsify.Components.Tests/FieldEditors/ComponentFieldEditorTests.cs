using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Shouldly;
using Xunit;

namespace SyntaxCircus.Cmsify.Components.Tests.FieldEditors;

public sealed class ComponentFieldEditorTests : BunitContext
{
    [Fact]
    public void RendersOneComponentInstanceBlockPerValue()
    {
        var (component, schemas, _) = CreateSingleTextFieldComponent();
        var values = new[] { new ComponentInstanceValue(), new ComponentInstanceValue() };

        var cut = Render<ComponentFieldEditor>(parameters => parameters
            .Add(p => p.ComponentId, component.Id)
            .Add(p => p.ComponentSchemas, schemas)
            .Add(p => p.Values, values));

        cut.FindAll(".cmsify-component-instance").Count.ShouldBe(2);
    }

    [Fact]
    public void RendersFieldsOrderedByOrder()
    {
        var first = TestComponentFactory.CreateField(key: "first", primitiveType: PrimitiveType.Text) with { Order = 0 };
        var second = TestComponentFactory.CreateField(key: "second", primitiveType: PrimitiveType.Text) with { Order = 1 };
        var component = TestComponentFactory.Create(currentVersion: TestComponentFactory.CreateVersion(fields: [second, first]));
        var schemas = new Dictionary<Guid, ComponentResponse> { [component.Id] = component };

        var instance = new ComponentInstanceValue();
        instance.GetOrCreate(first.Id).TextValue = "First value";
        instance.GetOrCreate(second.Id).TextValue = "Second value";

        var cut = Render<ComponentFieldEditor>(parameters => parameters
            .Add(p => p.ComponentId, component.Id)
            .Add(p => p.ComponentSchemas, schemas)
            .Add(p => p.Values, new[] { instance }));

        var inputValues = cut.FindAll("input").Select(i => i.GetAttribute("value")).ToList();
        inputValues.ShouldBe(["First value", "Second value"]);
    }

    [Fact]
    public void MediaPickRequestedFromASyntheticSeedInstanceSurvivesARerenderBeforeTheAssetIsPicked()
    {
        // Regression for the component-field-path sibling of the C1 bug: when Values is empty,
        // DisplayValues fabricates a synthetic "seed" ComponentInstanceValue to render. Clicking
        // "Choose" on a Media field inside that seed instance raises OnMediaPickRequested with a
        // ContentFieldEditorValue nested inside it, but the actual selection lands later and
        // asynchronously (ContentEditPanel.OnAssetSelectedAsync mutates it directly - no
        // ValuesChanged round-trip). Opening the picker modal itself causes a re-render in between.
        // If DisplayValues fabricated a BRAND NEW seed instance on that re-render (the bug), the
        // object the picker captured would be orphaned and the eventual mutation silently lost.
        var mediaField = TestComponentFactory.CreateField(key: "hero", primitiveType: PrimitiveType.Media);
        var component = TestComponentFactory.Create(currentVersion: TestComponentFactory.CreateVersion(fields: [mediaField]));
        var schemas = new Dictionary<Guid, ComponentResponse> { [component.Id] = component };
        ContentFieldEditorValue? requestedValue = null;

        var cut = Render<ComponentFieldEditor>(parameters => parameters
            .Add(p => p.ComponentId, component.Id)
            .Add(p => p.ComponentSchemas, schemas)
            .Add(p => p.Values, [])
            .Add(p => p.OnMediaPickRequested, EventCallback.Factory.Create<ContentFieldEditorValue>(this, v => requestedValue = v)));

        cut.Find("button.cmsify-field-picker-button").Click();
        requestedValue.ShouldNotBeNull();

        // Simulate the re-render that opening the picker modal causes (Values is still empty at
        // this point - nothing has committed the seed instance yet).
        cut.Render(ParameterView.Empty);

        // Simulate the eventual asset pick, exactly as ContentEditPanel.OnAssetSelectedAsync does:
        // mutate the captured instance directly, with no ValuesChanged round-trip.
        var asset = new MediaAssetResponse(Guid.NewGuid(), "hero.png", "image/png", 2048, null, "/media/hero.png", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        requestedValue!.SelectedMediaAsset = asset;

        // Re-render again (as the picker closing would) and confirm the selection is actually
        // reflected in the rendered editor - not silently discarded onto an orphaned object.
        cut.Render(ParameterView.Empty);

        cut.Find("button.cmsify-field-picker-button").TextContent.ShouldBe("hero.png");
    }

    [Fact]
    public void MinOccurrencesSeedsThatManyEmptyInstancesWhenValuesIsEmpty()
    {
        var (component, schemas, _) = CreateSingleTextFieldComponent();

        var cut = Render<ComponentFieldEditor>(parameters => parameters
            .Add(p => p.ComponentId, component.Id)
            .Add(p => p.ComponentSchemas, schemas)
            .Add(p => p.MinOccurrences, 3));

        cut.FindAll(".cmsify-component-instance").Count.ShouldBe(3);
    }

    [Fact]
    public void AddButtonIsHiddenWhenMaxOccurrencesReached()
    {
        var (component, schemas, _) = CreateSingleTextFieldComponent();
        var values = new[] { new ComponentInstanceValue() };

        var cut = Render<ComponentFieldEditor>(parameters => parameters
            .Add(p => p.ComponentId, component.Id)
            .Add(p => p.ComponentSchemas, schemas)
            .Add(p => p.Values, values)
            .Add(p => p.MaxOccurrences, 1));

        cut.FindAll(".cmsify-field-add-button").ShouldBeEmpty();
    }

    [Fact]
    public void AddButtonIsPresentWhenBelowMaxOccurrences()
    {
        var (component, schemas, _) = CreateSingleTextFieldComponent();
        var values = new[] { new ComponentInstanceValue() };

        var cut = Render<ComponentFieldEditor>(parameters => parameters
            .Add(p => p.ComponentId, component.Id)
            .Add(p => p.ComponentSchemas, schemas)
            .Add(p => p.Values, values)
            .Add(p => p.MaxOccurrences, 2));

        cut.FindAll(".cmsify-field-add-button").Count.ShouldBe(1);
    }

    [Fact]
    public void RemoveButtonIsHiddenWhenAtMinOccurrences()
    {
        var (component, schemas, _) = CreateSingleTextFieldComponent();
        var values = new[] { new ComponentInstanceValue(), new ComponentInstanceValue() };

        var cut = Render<ComponentFieldEditor>(parameters => parameters
            .Add(p => p.ComponentId, component.Id)
            .Add(p => p.ComponentSchemas, schemas)
            .Add(p => p.Values, values)
            .Add(p => p.MinOccurrences, 2));

        cut.FindAll(".cmsify-component-remove-button").ShouldBeEmpty();
    }

    [Fact]
    public void RemoveButtonIsPresentAboveMinOccurrencesAndFiresValuesChangedWithoutRemovedInstance()
    {
        var (component, schemas, _) = CreateSingleTextFieldComponent();
        var first = new ComponentInstanceValue();
        var second = new ComponentInstanceValue();
        var values = new[] { first, second };
        IReadOnlyList<ComponentInstanceValue>? changed = null;

        var cut = Render<ComponentFieldEditor>(parameters => parameters
            .Add(p => p.ComponentId, component.Id)
            .Add(p => p.ComponentSchemas, schemas)
            .Add(p => p.Values, values)
            .Add(p => p.MinOccurrences, 0)
            .Add(p => p.ValuesChanged, EventCallback.Factory.Create<IReadOnlyList<ComponentInstanceValue>>(this, v => changed = v)));

        cut.FindAll(".cmsify-component-remove-button").Count.ShouldBe(2);
        cut.FindAll(".cmsify-component-remove-button")[0].Click();

        changed.ShouldBe(new[] { second });
    }

    [Fact]
    public void ReadOnlyHidesButtonsAndForwardsReadOnlyToNestedFieldEditors()
    {
        var (component, schemas, _) = CreateSingleTextFieldComponent();
        var values = new[] { new ComponentInstanceValue() };

        var cut = Render<ComponentFieldEditor>(parameters => parameters
            .Add(p => p.ComponentId, component.Id)
            .Add(p => p.ComponentSchemas, schemas)
            .Add(p => p.Values, values)
            .Add(p => p.ReadOnly, true));

        cut.FindAll(".cmsify-field-add-button").ShouldBeEmpty();
        cut.FindAll(".cmsify-component-remove-button").ShouldBeEmpty();
        cut.FindComponent<TextFieldEditor>().Instance.ReadOnly.ShouldBeTrue();
    }

    [Fact]
    public void RendersFallbackWarningWhenComponentSchemaIsUnresolved()
    {
        var cut = Render<ComponentFieldEditor>(parameters => parameters
            .Add(p => p.ComponentId, Guid.NewGuid())
            .Add(p => p.ComponentSchemas, new Dictionary<Guid, ComponentResponse>()));

        cut.Find(".cmsify-field-warning").TextContent.ShouldContain("Component schema unavailable.");
    }

    [Fact]
    public void AFieldWithNestedComponentIdRecursesIntoANestedComponentFieldEditor()
    {
        var childLabelField = TestComponentFactory.CreateField(key: "label", primitiveType: PrimitiveType.Text);
        var childComponent = TestComponentFactory.Create(currentVersion: TestComponentFactory.CreateVersion(fields: [childLabelField]));

        var nestedField = TestComponentFactory.CreateField(key: "child", nestedComponentId: childComponent.Id, maxOccurrences: 1);
        var parentComponent = TestComponentFactory.Create(currentVersion: TestComponentFactory.CreateVersion(fields: [nestedField]));

        var schemas = new Dictionary<Guid, ComponentResponse>
        {
            [parentComponent.Id] = parentComponent,
            [childComponent.Id] = childComponent,
        };

        var childInstance = new ComponentInstanceValue();
        childInstance.GetOrCreate(childLabelField.Id).TextValue = "Nested value";
        var parentInstance = new ComponentInstanceValue();
        parentInstance.GetOrCreate(nestedField.Id).ComponentValues = [childInstance];

        var cut = Render<ComponentFieldEditor>(parameters => parameters
            .Add(p => p.ComponentId, parentComponent.Id)
            .Add(p => p.ComponentSchemas, schemas)
            .Add(p => p.Values, new[] { parentInstance }));

        var nestedEditor = cut.FindComponents<ComponentFieldEditor>().Single(c => c.Instance.ComponentId == childComponent.Id);
        nestedEditor.Instance.Values.ShouldBe(new[] { childInstance });
        cut.Find("input").GetAttribute("value").ShouldBe("Nested value");
    }

    [Fact]
    public void ANestedPickListFieldResolvesAgainstPickListsByRevisionId()
    {
        var pickListId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var fieldConfig = JsonDocument.Parse($$"""
            { "picklistId": "{{pickListId}}", "picklistRevisionId": "{{revisionId}}", "multiple": false }
            """).RootElement;
        var colorField = TestComponentFactory.CreateField(key: "color", primitiveType: PrimitiveType.PickList, fieldConfig: fieldConfig);
        var component = TestComponentFactory.Create(currentVersion: TestComponentFactory.CreateVersion(fields: [colorField]));
        var schemas = new Dictionary<Guid, ComponentResponse> { [component.Id] = component };

        var pickList = new PickListResponse(pickListId, "Colors", "colors", null, [new PickListOptionResponse(Guid.NewGuid(), "Red", "red", 0)]);
        var pickListsByRevisionId = new Dictionary<Guid, PickListResponse> { [revisionId] = pickList };

        var cut = Render<ComponentFieldEditor>(parameters => parameters
            .Add(p => p.ComponentId, component.Id)
            .Add(p => p.ComponentSchemas, schemas)
            .Add(p => p.PickListsByRevisionId, pickListsByRevisionId)
            .Add(p => p.Values, new[] { new ComponentInstanceValue() }));

        cut.FindComponent<PickListFieldEditor>().Instance.PickList.ShouldBe(pickList);
    }

    private static (ComponentResponse Component, Dictionary<Guid, ComponentResponse> Schemas, ComponentFieldResponse Field) CreateSingleTextFieldComponent()
    {
        var field = TestComponentFactory.CreateField(key: "title", primitiveType: PrimitiveType.Text);
        var component = TestComponentFactory.Create(currentVersion: TestComponentFactory.CreateVersion(fields: [field]));
        var schemas = new Dictionary<Guid, ComponentResponse> { [component.Id] = component };
        return (component, schemas, field);
    }
}
