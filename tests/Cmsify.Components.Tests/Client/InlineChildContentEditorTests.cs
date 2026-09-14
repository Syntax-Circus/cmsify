using Bunit;
using Microsoft.AspNetCore.Components;
using Shouldly;
using Xunit;

namespace SyntaxCircus.Cmsify.Components.Tests.Client;

using SyntaxCircus.Cmsify.Components.Client;
using SyntaxCircus.Cmsify.Components.Tests;

public sealed class InlineChildContentEditorTests : BunitContext
{
    private static HttpResponseMessage TemplateJson(Guid templateId, Guid workspaceId, string fieldsJson = "[]", string name = "Child") =>
        FakeHttpMessageHandler.Json($$"""
            { "id": "{{templateId}}", "workspaceId": "{{workspaceId}}", "name": "{{name}}", "slug": "child",
              "description": null, "isSystem": false,
              "currentVersion": { "id": "{{Guid.NewGuid()}}", "templateId": "{{templateId}}", "versionNumber": 1,
                "status": "Published", "publishedAt": null, "notes": null, "sections": [], "fields": {{fieldsJson}} } }
            """);

    private static HttpResponseMessage TemplateListJson(Guid workspaceId, params (Guid Id, string Name)[] templates)
    {
        var items = string.Join(",", templates.Select(t => $$"""
            { "id": "{{t.Id}}", "workspaceId": "{{workspaceId}}", "name": "{{t.Name}}", "slug": "{{t.Name.ToLowerInvariant()}}", "description": null, "currentVersionId": "{{Guid.NewGuid()}}" }
            """));
        return FakeHttpMessageHandler.Json($$"""{ "items": [{{items}}], "totalCount": {{templates.Length}}, "page": 1, "pageSize": 100 }""");
    }

    [Fact]
    public void FixedTemplateIdWithNoAllowedTypesSkipsPickerAndCreatesDirectlyOnAdd()
    {
        var workspaceId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        var field = TestFieldFactory.Create(templateId: templateId, compositionMode: CompositionMode.Inline);

        // No request should ever be issued: candidate resolution is skipped entirely for a fixed
        // TemplateId with no AllowedTypes, and there are no existing instances to resolve state for.
        var client = TestCmsifyClientFactory.Create(request =>
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}"));

        IList<InlineChildInstance>? changed = null;
        var cut = Render<InlineChildContentEditor>(parameters => parameters
            .Add(p => p.Client, client)
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.Field, field)
            .Add(p => p.Instances, new List<InlineChildInstance>())
            .Add(p => p.InstancesChanged, EventCallback.Factory.Create<IList<InlineChildInstance>>(this, v => changed = v)));

        cut.FindAll(".cmsify-inline-template-picker").ShouldBeEmpty();

        cut.Find(".cmsify-field-add-button").Click();

        changed.ShouldNotBeNull();
        changed!.Count.ShouldBe(1);
        changed[0].TemplateId.ShouldBe(templateId);
        changed[0].ContentItemId.ShouldBeNull();
    }

    [Fact]
    public void AllowedTypesShowsPickerWithExactlyThoseTemplates()
    {
        var workspaceId = Guid.NewGuid();
        var templateAId = Guid.NewGuid();
        var templateBId = Guid.NewGuid();
        var field = TestFieldFactory.Create(compositionMode: CompositionMode.Inline, allowedTypes:
        [
            new TemplateFieldAllowedTypeResponse(Guid.NewGuid(), null, templateAId),
            new TemplateFieldAllowedTypeResponse(Guid.NewGuid(), null, templateBId),
        ]);

        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == $"/api/v1/workspaces/{workspaceId}/templates/{templateAId}")
            {
                return TemplateJson(templateAId, workspaceId, name: "Alpha");
            }
            if (path == $"/api/v1/workspaces/{workspaceId}/templates/{templateBId}")
            {
                return TemplateJson(templateBId, workspaceId, name: "Beta");
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        var cut = Render<InlineChildContentEditor>(parameters => parameters
            .Add(p => p.Client, client)
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.Field, field)
            .Add(p => p.Instances, new List<InlineChildInstance>()));

        cut.WaitForState(() => cut.FindAll(".cmsify-field-add-button").Count > 0);
        cut.Find(".cmsify-field-add-button").Click();

        // Add's own click-time candidates.Count check races the still-in-flight per-candidate GetAsync
        // calls; the picker renders immediately (possibly with 0 candidates so far) and is then
        // updated in place once those calls resolve, so wait for that rather than asserting right away.
        cut.WaitForState(() => cut.FindComponent<InlineTemplatePicker>().Instance.Candidates.Count == 2, TimeSpan.FromSeconds(5));

        var picker = cut.FindComponent<InlineTemplatePicker>();
        picker.Instance.Candidates.Select(c => c.Id).ShouldBe([templateAId, templateBId], ignoreOrder: true);
    }

    [Fact]
    public void OpenFieldWithNoAllowedTypesSourcesCandidatesFromWorkspaceTemplateList()
    {
        var workspaceId = Guid.NewGuid();
        var templateAId = Guid.NewGuid();
        var templateBId = Guid.NewGuid();
        var templateCId = Guid.NewGuid();
        var field = TestFieldFactory.Create(isOpen: true, compositionMode: CompositionMode.Inline);

        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == $"/api/v1/workspaces/{workspaceId}/templates")
            {
                return TemplateListJson(workspaceId, (templateAId, "Alpha"), (templateBId, "Beta"), (templateCId, "Gamma"));
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        var cut = Render<InlineChildContentEditor>(parameters => parameters
            .Add(p => p.Client, client)
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.Field, field)
            .Add(p => p.Instances, new List<InlineChildInstance>()));

        cut.WaitForState(() => cut.FindAll(".cmsify-field-add-button").Count > 0);
        cut.Find(".cmsify-field-add-button").Click();

        // Same race as above: the workspace-wide Templates.ListAsync call may still be in flight when
        // Add is clicked, so wait for the picker's Candidates to actually reflect the loaded list.
        cut.WaitForState(() => cut.FindComponent<InlineTemplatePicker>().Instance.Candidates.Count == 3, TimeSpan.FromSeconds(5));

        var picker = cut.FindComponent<InlineTemplatePicker>();
        picker.Instance.Candidates.Select(c => c.Id).ShouldBe([templateAId, templateBId, templateCId], ignoreOrder: true);
    }

    [Fact]
    public void CandidateTemplateAlreadyInAncestorChainIsDisabledInThePicker()
    {
        var workspaceId = Guid.NewGuid();
        var ancestorTemplateId = Guid.NewGuid();
        var otherTemplateId = Guid.NewGuid();
        var field = TestFieldFactory.Create(isOpen: true, compositionMode: CompositionMode.Inline);

        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == $"/api/v1/workspaces/{workspaceId}/templates")
            {
                return TemplateListJson(workspaceId, (ancestorTemplateId, "Ancestor"), (otherTemplateId, "Other"));
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        var cut = Render<InlineChildContentEditor>(parameters => parameters
            .Add(p => p.Client, client)
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.Field, field)
            .Add(p => p.Instances, new List<InlineChildInstance>())
            .Add(p => p.AncestorTemplateIds, new HashSet<Guid> { ancestorTemplateId }));

        cut.WaitForState(() => cut.FindAll(".cmsify-field-add-button").Count > 0);
        cut.Find(".cmsify-field-add-button").Click();
        cut.WaitForState(() => cut.FindComponent<InlineTemplatePicker>().Instance.Candidates.Count == 2, TimeSpan.FromSeconds(5));

        var options = cut.FindAll("option").Where(o => o.GetAttribute("value") != "").ToList();
        options.Single(o => o.GetAttribute("value") == ancestorTemplateId.ToString()).HasAttribute("disabled").ShouldBeTrue();
        options.Single(o => o.GetAttribute("value") == otherTemplateId.ToString()).HasAttribute("disabled").ShouldBeFalse();
    }

    [Fact]
    public void AddButtonIsHiddenWhenMaxOccurrencesReachedAndRemoveButtonIsHiddenAtMinOccurrences()
    {
        var workspaceId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        var field = TestFieldFactory.Create(templateId: templateId, compositionMode: CompositionMode.Inline, maxOccurrences: 2) with { MinOccurrences = 1 };

        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == $"/api/v1/workspaces/{workspaceId}/templates/{templateId}")
            {
                return TemplateJson(templateId, workspaceId);
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        var instances = new List<InlineChildInstance> { new() { TemplateId = templateId } };
        var cut = Render<InlineChildContentEditor>(parameters => parameters
            .Add(p => p.Client, client)
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.Field, field)
            .Add(p => p.Instances, instances));

        cut.WaitForState(() => cut.FindAll(".cmsify-inline-child-card").Count > 0);

        // 1 active instance == MinOccurrences (1) -> Remove hidden. 1 < MaxOccurrences (2) -> Add shown.
        cut.FindAll(".cmsify-component-remove-button").ShouldBeEmpty();
        cut.FindAll(".cmsify-field-add-button").Count.ShouldBe(1);

        // A new list, not a mutation of the one already bound as the live Instances parameter -
        // mirrors how the real Add/Remove flow always produces a fresh list via InstancesChanged
        // rather than mutating the caller's list in place.
        var updatedInstances = new List<InlineChildInstance>(instances) { new() { TemplateId = templateId } };
        cut.Render(parameters => parameters.Add(p => p.Instances, updatedInstances));
        cut.WaitForState(() => cut.FindAll(".cmsify-inline-child-card").Count == 2);

        // 2 active instances > MinOccurrences (1) -> Remove shown on each. 2 == MaxOccurrences (2) -> Add hidden.
        cut.FindAll(".cmsify-component-remove-button").Count.ShouldBe(2);
        cut.FindAll(".cmsify-field-add-button").ShouldBeEmpty();
    }

    [Fact]
    public void RemovingANeverSavedInstanceRemovesItOutrightWhileRemovingAPersistedInstanceMarksItForDeletion()
    {
        var workspaceId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        var field = TestFieldFactory.Create(templateId: templateId, compositionMode: CompositionMode.Inline);

        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == $"/api/v1/workspaces/{workspaceId}/templates/{templateId}")
            {
                return TemplateJson(templateId, workspaceId);
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        var neverSaved = new InlineChildInstance { TemplateId = templateId };
        IList<InlineChildInstance>? changed = null;
        var cut = Render<InlineChildContentEditor>(parameters => parameters
            .Add(p => p.Client, client)
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.Field, field)
            .Add(p => p.Instances, new List<InlineChildInstance> { neverSaved })
            .Add(p => p.InstancesChanged, EventCallback.Factory.Create<IList<InlineChildInstance>>(this, v => changed = v)));

        cut.WaitForState(() => cut.FindAll(".cmsify-component-remove-button").Count > 0);
        cut.Find(".cmsify-component-remove-button").Click();

        changed.ShouldNotBeNull();
        changed!.ShouldBeEmpty();

        var persisted = new InlineChildInstance { TemplateId = templateId, ContentItemId = Guid.NewGuid() };
        cut.Render(parameters => parameters.Add(p => p.Instances, new List<InlineChildInstance> { persisted }));
        cut.WaitForState(() => cut.FindAll(".cmsify-component-remove-button").Count > 0);
        cut.Find(".cmsify-component-remove-button").Click();

        persisted.MarkedForDeletion.ShouldBeTrue();
        changed.ShouldContain(persisted);
        cut.WaitForState(() => cut.FindAll(".cmsify-inline-child-card--pending-delete").Count > 0);
    }

    [Fact]
    public void RecursesIntoNestedInlineChildContentEditorForAChildTemplateWithItsOwnInlineField()
    {
        var workspaceId = Guid.NewGuid();
        var childTemplateId = Guid.NewGuid();
        var grandchildTemplateId = Guid.NewGuid();
        var nestedFieldId = Guid.NewGuid();

        var field = TestFieldFactory.Create(templateId: childTemplateId, compositionMode: CompositionMode.Inline);

        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == $"/api/v1/workspaces/{workspaceId}/templates/{childTemplateId}")
            {
                var nestedFieldJson = $$"""
                    [{ "id": "{{nestedFieldId}}", "sectionId": null, "key": "nested", "label": "Nested", "helpText": null,
                       "order": 0, "isRequired": false, "minOccurrences": 0, "maxOccurrences": null, "isOpen": false,
                       "compositionMode": "Inline", "primitiveType": null, "templateId": "{{grandchildTemplateId}}",
                       "allowedTypes": [], "fieldConfig": null, "componentId": null }]
                    """;
                return TemplateJson(childTemplateId, workspaceId, fieldsJson: nestedFieldJson);
            }
            if (path == $"/api/v1/workspaces/{workspaceId}/content")
            {
                // ContentEditSupport.LoadReferenceOptionsAsync treats any field with a TemplateId as
                // needing a reference-option list, regardless of CompositionMode - the same
                // pre-existing behavior ContentEditPanel.LoadTemplateVersionAsync already has for a
                // top-level Inline field with a fixed TemplateId. Harmless but real, so it needs a stub.
                return FakeHttpMessageHandler.Json("""{ "items": [], "totalCount": 0, "page": 1, "pageSize": 20 }""");
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        var instances = new List<InlineChildInstance> { new() { TemplateId = childTemplateId } };
        var cut = Render<InlineChildContentEditor>(parameters => parameters
            .Add(p => p.Client, client)
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.Field, field)
            .Add(p => p.Instances, instances));

        // bUnit's FindComponents<T>() does not descend into a matched component's own subtree
        // looking for further instances of that SAME type, so a same-type recursive nesting (this
        // component rendering another instance of itself) can't be counted that way even though the
        // markup genuinely contains two - assert via the intermediate FieldEditor instead: its
        // presence (for the "Nested" field) proves the recursive <InlineChildContentEditor> rendered,
        // and its forwarded AncestorTemplateIds proves the ancestor chain was extended correctly.
        cut.WaitForAssertion(() => cut.FindComponents<FieldEditor>().ShouldContain(c => c.Instance.Field.Id == nestedFieldId), TimeSpan.FromSeconds(5));

        var nestedFieldEditor = cut.FindComponents<FieldEditor>().Single(c => c.Instance.Field.Id == nestedFieldId);
        nestedFieldEditor.Instance.AncestorTemplateIds.ShouldContain(childTemplateId);
        cut.Markup.ShouldContain("Add Nested");
    }

    [Fact]
    public void NumericDepthCapRendersTerminalMessageInsteadOfRecursingPastEightLevels()
    {
        var workspaceId = Guid.NewGuid();
        var field = TestFieldFactory.Create(templateId: Guid.NewGuid(), compositionMode: CompositionMode.Inline);

        // No request should be issued at all once the depth cap is reached - resolution of
        // candidates and existing instances alike is skipped entirely.
        var client = TestCmsifyClientFactory.Create(request =>
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}"));

        var cut = Render<InlineChildContentEditor>(parameters => parameters
            .Add(p => p.Client, client)
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.Field, field)
            .Add(p => p.Instances, new List<InlineChildInstance> { new() { TemplateId = Guid.NewGuid() } })
            .Add(p => p.Depth, 8));

        cut.Find(".cmsify-field-warning").TextContent.ShouldContain("Maximum nesting depth reached");
        cut.FindAll(".cmsify-inline-child-card").ShouldBeEmpty();
        cut.FindAll(".cmsify-field-add-button").ShouldBeEmpty();
    }

    [Fact]
    public void DepthCapTerminatesByLevelCountEvenWhenTheAncestorSetDoesNotGrowDueToRepeatedTemplateIds()
    {
        // C1 regression: a cyclic chain (A -> B -> A -> B -> ...) means the SAME two template ids
        // repeat forever, so a set-based ancestor guard never grows past 2 members and never trips.
        // Depth must terminate recursion purely by level count regardless of repeats. AncestorTemplateIds
        // here deliberately holds only the two repeating ids (nowhere near MaxInlineDepth by count),
        // while Depth alone has already reached the cap - proving Depth, not the ancestor set size, is
        // what stops rendering.
        var workspaceId = Guid.NewGuid();
        var templateAId = Guid.NewGuid();
        var templateBId = Guid.NewGuid();
        var field = TestFieldFactory.Create(templateId: templateAId, compositionMode: CompositionMode.Inline);

        var client = TestCmsifyClientFactory.Create(request =>
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}"));

        var cut = Render<InlineChildContentEditor>(parameters => parameters
            .Add(p => p.Client, client)
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.Field, field)
            .Add(p => p.Instances, new List<InlineChildInstance> { new() { TemplateId = templateAId } })
            .Add(p => p.AncestorTemplateIds, new HashSet<Guid> { templateAId, templateBId })
            .Add(p => p.Depth, 8));

        cut.Find(".cmsify-field-warning").TextContent.ShouldContain("Maximum nesting depth reached");
        cut.FindAll(".cmsify-inline-child-card").ShouldBeEmpty();
    }

    [Fact]
    public void FixedTemplateIdMatchingAnAncestorIsBlockedFromAutoAddInsteadOfBypassingTheCycleGuard()
    {
        // C1: the auto-add path (fixed TemplateId, no picker) must not be able to route around the
        // picker's own ancestor-disable check. Below the numeric depth cap, adding an instance whose
        // template already appears in the ancestor chain would create a genuine cycle in real content
        // data with no server-side cycle validation to catch it.
        var workspaceId = Guid.NewGuid();
        var ancestorTemplateId = Guid.NewGuid();
        var field = TestFieldFactory.Create(templateId: ancestorTemplateId, compositionMode: CompositionMode.Inline);

        var client = TestCmsifyClientFactory.Create(request =>
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}"));

        var cut = Render<InlineChildContentEditor>(parameters => parameters
            .Add(p => p.Client, client)
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.Field, field)
            .Add(p => p.Instances, new List<InlineChildInstance>())
            .Add(p => p.AncestorTemplateIds, new HashSet<Guid> { ancestorTemplateId }));

        cut.FindAll(".cmsify-field-add-button").ShouldBeEmpty();
        cut.Find(".cmsify-field-warning").TextContent.ShouldContain("circular reference");
    }

    [Fact]
    public void NullClientRendersWarningInsteadOfThrowing()
    {
        // I9: an external consumer of the published component package rendering ContentEditForm
        // without a Client must degrade gracefully, not NullReferenceException and crash their whole
        // Blazor circuit.
        var workspaceId = Guid.NewGuid();
        var field = TestFieldFactory.Create(templateId: Guid.NewGuid(), compositionMode: CompositionMode.Inline);

        var cut = Render<InlineChildContentEditor>(parameters => parameters
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.Field, field)
            .Add(p => p.Instances, new List<InlineChildInstance>()));

        cut.Find(".cmsify-field-warning").TextContent.ShouldContain("requires a Client");
    }
}
