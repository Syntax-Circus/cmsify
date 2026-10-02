using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Shouldly;
using SyntaxCircus.Cmsify.Components.Client;
using Xunit;

namespace SyntaxCircus.Cmsify.Components.Tests;

public sealed class DirectContentEditorTests : BunitContext
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecursivelyEditsChildrenAndResolvesNestedBindingsWithoutHttp(bool supplyThrowingClient)
    {
        var source = new DirectEditorDataSource();
        var text = TestFieldFactory.Create(primitiveType: PrimitiveType.Text);
        var media = TestFieldFactory.Create(primitiveType: PrimitiveType.Media);
        var file = TestFieldFactory.Create(primitiveType: PrimitiveType.File);
        var grandchildTemplate = source.AddTemplate(text, media, file);
        var nested = TestFieldFactory.Create(templateId: grandchildTemplate.Id, compositionMode: CompositionMode.Inline);
        var revisionId = Guid.NewGuid();
        var pickListId = Guid.NewGuid();
        var referenceTemplateId = Guid.NewGuid();
        var pickConfig = JsonSerializer.SerializeToElement(new { picklistId = pickListId, picklistRevisionId = revisionId });
        var pickField = TestComponentFactory.CreateField(primitiveType: PrimitiveType.PickList, fieldConfig: pickConfig);
        var reference = TestFieldFactory.Create(templateId: referenceTemplateId);
        var leaf = TestComponentFactory.Create(currentVersion: TestComponentFactory.CreateVersion(fields: [pickField]));
        var branch = TestComponentFactory.Create(currentVersion: TestComponentFactory.CreateVersion(fields:
            [TestComponentFactory.CreateField(nestedComponentId: leaf.Id)]));
        source.Components[branch.Id] = branch;
        source.Components[leaf.Id] = leaf;
        source.PickLists[(pickListId, revisionId)] = new(pickListId, "Colors", "colors", null,
            [new(Guid.NewGuid(), "Blue snapshot", "blue", 0)], revisionId, 1);
        var referencedId = Guid.NewGuid();
        source.References[referenceTemplateId] = [new(referencedId, Guid.NewGuid(), "Story", "other-story", null,
            null, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1, null)];
        var childTemplate = source.AddTemplate(nested, TestFieldFactory.Create(componentId: branch.Id), reference);
        var root = TestFieldFactory.Create(templateId: childTemplate.Id, compositionMode: CompositionMode.Inline);
        var grandchild = new InlineChildInstance { TemplateId = grandchildTemplate.Id };
        var child = new InlineChildInstance { TemplateId = childTemplate.Id };
        child.FieldValues[nested.Id] = new() { ChildInstances = new List<InlineChildInstance> { grandchild } };
        var values = new Dictionary<Guid, ContentFieldEditorValue>
        {
            [root.Id] = new() { ChildInstances = new List<InlineChildInstance> { child } }
        };
        ContentFieldEditorValue? requestedMedia = null;
        ContentFieldEditorValue? requestedFile = null;
        var cut = Render<ContentEditForm>(p => p
            .Add(x => x.TemplateVersion, Version(root))
            .Add(x => x.FieldValues, values)
            .Add(x => x.WorkspaceId, source.WorkspaceId)
            .Add(x => x.DataSource, source)
            .Add(x => x.Client, supplyThrowingClient ? ThrowingClient() : null)
            .Add(x => x.OnMediaPickRequested, v => requestedMedia = v)
            .Add(x => x.OnFilePickRequested, v => requestedFile = v));

        cut.WaitForState(() => cut.FindComponents<TextFieldEditor>().Count == 1);
        cut.FindComponent<TextFieldEditor>().Find("input").Input("edited grandchild");
        grandchild.FieldValues[text.Id].TextValue.ShouldBe("edited grandchild");
        cut.FindComponent<MediaFieldEditor>().Find("button").Click();
        cut.FindComponent<FileFieldEditor>().Find("button").Click();
        requestedMedia.ShouldBeSameAs(grandchild.FieldValues[media.Id]);
        requestedFile.ShouldBeSameAs(grandchild.FieldValues[file.Id]);

        // Empty components display a stable seed instance. Editing its leaf commits the
        // real component tree through both nested ValuesChanged callbacks.
        cut.WaitForState(() => cut.FindComponents<PickListFieldEditor>().Count == 1);
        cut.FindComponent<PickListFieldEditor>().Find("select").Change("blue");
        child.FieldValues.Values.SelectMany(v => v.ComponentValues).Single().FieldValues.Values
            .SelectMany(v => v.ComponentValues).Single().FieldValues[pickField.Id].TextValue.ShouldBe("blue");
        cut.FindComponent<PickListFieldEditor>().Markup.ShouldContain("Blue snapshot");
        cut.FindComponent<ReferenceFieldEditor>().Find("select").Change(referencedId.ToString());
        child.FieldValues[reference.Id].ChildContentItemId.ShouldBe(referencedId);
    }

    [Fact]
    public void FixedTemplateAddPropagatesTheRootCallbackAndHonorsCardinality()
    {
        var source = new DirectEditorDataSource();
        var template = source.AddTemplate();
        var field = TestFieldFactory.Create(templateId: template.Id, compositionMode: CompositionMode.Inline,
            maxOccurrences: 1) with { MinOccurrences = 1 };
        (TemplateFieldResponse Field, ContentFieldEditorValue Value)? changed = null;
        var cut = Render<ContentEditForm>(p => p.Add(x => x.TemplateVersion, Version(field))
            .Add(x => x.DataSource, source).Add(x => x.WorkspaceId, source.WorkspaceId)
            .Add(x => x.FieldValueChanged, v => changed = v));

        cut.Find(".cmsify-field-add-button").Click();
        changed.ShouldNotBeNull();
        changed.Value.Field.Id.ShouldBe(field.Id);
        changed.Value.Value.ChildInstances.Single().TemplateId.ShouldBe(template.Id);
        cut.Render();
        cut.WaitForState(() => cut.FindAll(".cmsify-inline-child-card").Count == 1);
        cut.FindAll(".cmsify-field-add-button").ShouldBeEmpty();
        cut.FindAll(".cmsify-component-remove-button").ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpenAndAllowedTemplatesUseDirectCandidatesAndRejectAncestorSelection(bool allowedTypes)
    {
        var source = new DirectEditorDataSource();
        var ancestor = source.AddTemplate();
        var candidate = source.AddTemplate();
        var field = TestFieldFactory.Create(isOpen: !allowedTypes, compositionMode: CompositionMode.Inline,
            allowedTypes: allowedTypes ? [new(Guid.NewGuid(), null, ancestor.Id), new(Guid.NewGuid(), null, candidate.Id)] : []);
        IList<InlineChildInstance>? changed = null;
        var cut = Render<InlineChildContentEditor>(p => p.Add(x => x.DataSource, source)
            .Add(x => x.WorkspaceId, source.WorkspaceId).Add(x => x.Field, field)
            .Add(x => x.AncestorTemplateIds, new HashSet<Guid> { ancestor.Id })
            .Add(x => x.InstancesChanged, v => changed = v));

        cut.WaitForState(() => cut.FindAll(".cmsify-field-add-button").Count == 1);
        cut.Find(".cmsify-field-add-button").Click();
        cut.WaitForState(() => cut.FindAll("option").Count == 3);
        cut.FindAll("option").Single(o => o.GetAttribute("value") == ancestor.Id.ToString()).HasAttribute("disabled").ShouldBeTrue();
        cut.Find("select").Change(ancestor.Id.ToString());
        changed.ShouldBeNull();
        cut.Find("select").Change(candidate.Id.ToString());
        changed.ShouldNotBeNull();
        changed.Single().TemplateId.ShouldBe(candidate.Id);
    }

    [Fact]
    public void ReadOnlyAndOverridesReachTheDirectGrandchild()
    {
        var source = new DirectEditorDataSource();
        var text = TestFieldFactory.Create(primitiveType: PrimitiveType.Text);
        var template = source.AddTemplate(text);
        var field = TestFieldFactory.Create(templateId: template.Id, compositionMode: CompositionMode.Inline);
        RenderFragment<FieldEditorRenderContext> custom = context => builder =>
        {
            builder.OpenElement(0, "input");
            builder.AddAttribute(1, "class", "custom-text");
            builder.AddAttribute(2, "readonly", context.ReadOnly);
            builder.CloseElement();
        };
        var cut = Render<ContentEditForm>(p => p.Add(x => x.TemplateVersion, Version(field))
            .Add(x => x.DataSource, source).Add(x => x.WorkspaceId, source.WorkspaceId)
            .Add(x => x.FieldValues, new Dictionary<Guid, ContentFieldEditorValue>
            {
                [field.Id] = new() { ChildInstances = new List<InlineChildInstance> { new() { TemplateId = template.Id } } }
            })
            .Add(x => x.ReadOnly, true)
            .Add(x => x.FieldTemplateOverrides, new Dictionary<PrimitiveType, RenderFragment<FieldEditorRenderContext>> { [PrimitiveType.Text] = custom }));

        cut.WaitForState(() => cut.FindAll(".custom-text").Count == 1);
        cut.Find(".custom-text").HasAttribute("readonly").ShouldBeTrue();
        cut.FindAll("input").ShouldAllBe(input => input.HasAttribute("readonly"));
        cut.FindAll("button").ShouldBeEmpty();
    }

    [Fact]
    public void DirectFixedCycleAndDepthLimitPreventFurtherEditing()
    {
        var source = new DirectEditorDataSource();
        var templateId = Guid.NewGuid();
        var field = TestFieldFactory.Create(templateId: templateId, compositionMode: CompositionMode.Inline);
        var cut = Render<InlineChildContentEditor>(p => p.Add(x => x.DataSource, source)
            .Add(x => x.WorkspaceId, source.WorkspaceId).Add(x => x.Field, field)
            .Add(x => x.AncestorTemplateIds, new HashSet<Guid> { templateId }));
        cut.Find(".cmsify-field-warning").TextContent.ShouldContain("circular reference");
        cut.FindAll("button").ShouldBeEmpty();
        cut.Render(p => p.Add(x => x.Depth, 8).Add(x => x.Instances,
            new List<InlineChildInstance> { new() { TemplateId = templateId } }));
        cut.Find(".cmsify-field-warning").TextContent.ShouldContain("Maximum nesting depth reached");
        cut.FindAll(".cmsify-inline-child-card").ShouldBeEmpty();
    }

    private static TemplateVersionResponse Version(params TemplateFieldResponse[] fields) =>
        new(Guid.NewGuid(), Guid.NewGuid(), 1, TemplateVersionStatus.Published, null, null, [], fields);

    [Fact]
    public void SiblingChildrenResolveConcurrentlyThroughTheDirectSource()
    {
        var gate = new ConcurrencyGate(requiredConcurrency: 2);
        var source = new DirectEditorDataSource { Gate = gate };
        var text = TestFieldFactory.Create(primitiveType: PrimitiveType.Text);
        var template = source.AddTemplate(text);
        var field = TestFieldFactory.Create(templateId: template.Id, compositionMode: CompositionMode.Inline);
        var cut = Render<InlineChildContentEditor>(p => p.Add(x => x.DataSource, source)
            .Add(x => x.WorkspaceId, source.WorkspaceId).Add(x => x.Field, field)
            .Add(x => x.Instances, new List<InlineChildInstance>
            {
                new() { TemplateId = template.Id }, new() { TemplateId = template.Id }
            }));

        cut.WaitForState(() => cut.FindComponents<TextFieldEditor>().Count == 2, TimeSpan.FromSeconds(10));
        gate.MaxObserved.ShouldBe(2);
    }

    private static CmsifyClient ThrowingClient() => TestCmsifyClientFactory.Create(request =>
        throw new InvalidOperationException($"HTTP must not be used: {request.RequestUri}"));
}

internal sealed class DirectEditorDataSource : IContentEditorDataSource
{
    public Guid WorkspaceId { get; } = Guid.NewGuid();
    public Dictionary<Guid, TemplateResponse> Templates { get; } = [];
    public Dictionary<Guid, ComponentResponse> Components { get; } = [];
    public Dictionary<(Guid PickListId, Guid RevisionId), PickListResponse> PickLists { get; } = [];
    public Dictionary<Guid, IReadOnlyList<ContentItemSummaryResponse>> References { get; } = [];
    public Exception? LookupFailure { get; set; }
    public ConcurrencyGate? Gate { get; set; }

    public TemplateResponse AddTemplate(params TemplateFieldResponse[] fields)
    {
        var id = Guid.NewGuid();
        var template = new TemplateResponse(id, WorkspaceId, "Child", "child", null, false,
            new(Guid.NewGuid(), id, 1, TemplateVersionStatus.Published, null, null, [], fields));
        Templates[id] = template;
        return template;
    }

    public async Task<TemplateResponse?> GetTemplateAsync(Guid workspaceId, Guid templateId, CancellationToken cancellationToken)
    {
        Validate(workspaceId, cancellationToken);
        if (Gate is not null)
        {
            await Gate.EnterAsync(cancellationToken);
        }
        return Templates.GetValueOrDefault(templateId);
    }

    public Task<IReadOnlyList<TemplateSummaryResponse>> ListTemplatesAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        Validate(workspaceId, cancellationToken);
        return Task.FromResult<IReadOnlyList<TemplateSummaryResponse>>(Templates.Values.Select(t =>
            new TemplateSummaryResponse(t.Id, t.WorkspaceId, t.Name, t.Slug, t.Description, t.CurrentVersion?.Id)).ToList());
    }

    public Task<PickListResponse?> GetPickListRevisionAsync(Guid workspaceId, Guid pickListId, Guid revisionId, CancellationToken cancellationToken)
    {
        Validate(workspaceId, cancellationToken);
        return Task.FromResult(PickLists.GetValueOrDefault((pickListId, revisionId)));
    }

    public Task<IReadOnlyList<ContentItemSummaryResponse>> ListReferenceOptionsAsync(Guid workspaceId, Guid templateId, CancellationToken cancellationToken)
    {
        Validate(workspaceId, cancellationToken);
        return Task.FromResult(References.GetValueOrDefault(templateId) ?? (IReadOnlyList<ContentItemSummaryResponse>)[]);
    }

    public Task<ComponentResponse?> GetComponentAsync(Guid workspaceId, Guid componentId, CancellationToken cancellationToken)
    {
        Validate(workspaceId, cancellationToken);
        return Task.FromResult(Components.GetValueOrDefault(componentId));
    }

    private void Validate(Guid workspaceId, CancellationToken cancellationToken)
    {
        workspaceId.ShouldBe(WorkspaceId);
        cancellationToken.ThrowIfCancellationRequested();
        if (LookupFailure is not null)
        {
            throw LookupFailure;
        }
    }
}
