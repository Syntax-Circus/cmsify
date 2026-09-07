# Admin/SDK Restoration + Field-Validation Fix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close a template-field validation gap found during live testing, and restore `sdk/dotnet` + `src/Cmsify.Admin` to a building, functional state against the version-centric Content API (default/unbounded-version CRUD + full workflow only — dated-version creation UI stays deferred).

**Architecture:** Part 1 adds one missing FluentValidation/API-layer rule. Part 2 works bottom-up through the dependency chain: `sdk/dotnet`'s `ContentClient` (the only thing that talks to the wire) first, then `Cmsify.Components` (shared widgets Admin wraps), then `Cmsify.Admin` itself (pages that use those widgets). Each layer only consumes contracts/methods the layer below it already exposes.

**Tech Stack:** ASP.NET Core / C#, FluentValidation, Blazor Server (InteractiveServer render mode), xUnit v3 + Shouldly + NSubstitute, bUnit for Razor component tests.

**Spec:** `C:\Users\jon\.claude\plans\stay-on-this-branch-wondrous-floyd.md` (design doc this plan implements — read both).

## Global Constraints

- **No new worktree or branch.** All work happens directly on the current branch (`fix/dark-mode-contrast-and-version-overflow`) in the main checkout `D:\dev\SyntaxCircus\cmsify` — explicit user instruction.
- **Scope boundary:** this pass restores CRUD + full Draft→Review→Approved→Published→Archived workflow for each content item's *default* (unbounded, `EffectiveStartAt`/`EffectiveEndAt` both null) version only. Do not add UI for creating/editing additional dated versions — that is a separate, already-deferred follow-up.
- Only `src/Cmsify.Core`, `src/Cmsify.Api`, `sdk/dotnet`, `src/Cmsify.Components`, `src/Cmsify.Admin`, and their test projects are in scope. Do not touch `ContentController.cs` or any other already-shipped backend code — it is correct and was verified live via Chrome in the prior session.
- Every new/changed SDK method or component behavior must be exercised by the existing test suite (fixed in place, not skipped) or new tests — `dotnet build` succeeding is necessary but not sufficient.

---

## Task 1: Field-validation gap fix

**Files:**
- Modify: `src/Cmsify.Core/Validation/CommandValidators.cs:61-79` (`TemplateFieldInputValidator`)
- Modify: `src/Cmsify.Api/Controllers/TemplatesController.cs:573-619` (`ValidateFieldRequestAsync`)
- Test: `tests/Cmsify.Core.Tests/TemplateFieldInputValidatorTests.cs` (new file)
- Test: `tests/Cmsify.Api.Integration.Tests/TemplateApiTests.cs` (add a test)

**Interfaces:**
- Consumes: `TemplateFieldInput`/`TemplateFieldAllowedTypeInput` (`src/Cmsify.Core/Interfaces/Repositories/RepositoryContracts.cs:62-79`); `TemplateFieldRequest`/`TemplateFieldAllowedTypeRequest` (`src/Cmsify.Contracts/WireContracts.cs:75,77`).
- Produces: nothing new consumed by later tasks — this is a standalone fix.

### Background

`IsOpen: true` on a template field means "polymorphic ChildContent field, any template allowed" (`docs/project plan/02_core_domain.md:154,161,197`; `08_content_api.md:136`) — `AllowedTypes` is meant to be ignored in that case. Today, nothing rejects an `IsOpen: true` field whose `AllowedTypes` contains an entry with `PrimitiveType` set (only the top-level `PrimitiveType`/`TemplateId`/`ComponentId` are checked against `IsOpen`, never the contents of `AllowedTypes`). That field gets created successfully, then every content value submitted against it is rejected downstream by `ContentValidator.ValidateValueKind` with a confusing "expects a child content value / requires ChildContentItemId" error — reproduced live via the Chrome API validation pass.

- [ ] **Step 1: Write the failing Core validator test**

Create `tests/Cmsify.Core.Tests/TemplateFieldInputValidatorTests.cs`:

```csharp
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Repositories;
using Cmsify.Core.Validation;
using Shouldly;
using Xunit;

namespace Cmsify.Core.Tests;

public sealed class TemplateFieldInputValidatorTests
{
    private static TemplateFieldInput ValidClosedField() => new(
        SectionId: null,
        Key: "title",
        Label: "Title",
        HelpText: null,
        Order: 0,
        IsRequired: false,
        MinOccurrences: 0,
        MaxOccurrences: 1,
        IsOpen: false,
        CompositionMode: CompositionMode.Inline,
        PrimitiveType: PrimitiveType.Text,
        TemplateId: null,
        FieldConfig: null,
        AllowedTypes: []);

    [Fact]
    public void Validate_OpenFieldWithPrimitiveTypeAllowedTypeEntry_IsInvalid()
    {
        var field = ValidClosedField() with
        {
            IsOpen = true,
            PrimitiveType = null,
            AllowedTypes = [new TemplateFieldAllowedTypeInput(PrimitiveType.Text, null)]
        };

        var result = new TemplateFieldInputValidator().Validate(field);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.ErrorMessage.Contains("AllowedTypes"));
    }

    [Fact]
    public void Validate_OpenFieldWithOnlyAllowedTemplateIdEntries_IsValid()
    {
        var field = ValidClosedField() with
        {
            IsOpen = true,
            PrimitiveType = null,
            AllowedTypes = [new TemplateFieldAllowedTypeInput(null, Guid.NewGuid())]
        };

        var result = new TemplateFieldInputValidator().Validate(field);

        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validate_OpenFieldWithNoAllowedTypes_IsValid()
    {
        var field = ValidClosedField() with { IsOpen = true, PrimitiveType = null };

        var result = new TemplateFieldInputValidator().Validate(field);

        result.IsValid.ShouldBeTrue();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Cmsify.Core.Tests/Cmsify.Core.Tests.csproj --filter TemplateFieldInputValidatorTests`
Expected: `Validate_OpenFieldWithPrimitiveTypeAllowedTypeEntry_IsInvalid` FAILS (`result.IsValid` is `true` today); the other two pass already.

- [ ] **Step 3: Add the rule to `TemplateFieldInputValidator`**

In `src/Cmsify.Core/Validation/CommandValidators.cs`, inside `TemplateFieldInputValidator`'s constructor, after the existing two `IsOpen`-related `RuleFor` blocks (after line 77's closing `;`), add:

```csharp
        RuleFor(field => field)
            .Must(field => !field.IsOpen || field.AllowedTypes.All(allowedType => !allowedType.PrimitiveType.HasValue))
            .WithMessage("Open fields' AllowedTypes entries cannot define a PrimitiveType.");
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/Cmsify.Core.Tests/Cmsify.Core.Tests.csproj --filter TemplateFieldInputValidatorTests`
Expected: all 3 tests PASS.

- [ ] **Step 5: Write the failing API integration test**

In `tests/Cmsify.Api.Integration.Tests/TemplateApiTests.cs`, add a new test after `CreateTemplate_AddingDuplicateFieldKey_ReturnsConflict` (after line 118):

```csharp
    [Fact]
    public async Task CreateField_OpenFieldWithPrimitiveTypeAllowedType_ReturnsValidationError()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var workspaceId = await GetWorkspaceIdAsync(factory);
        var login = await LoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);

        var createResponse = await client.PostAsJsonAsync($"/api/v1/workspaces/{workspaceId}/templates", new CreateTemplateRequest("Open Field Template", $"open-field-template-{Guid.NewGuid():N}", null), cancellationToken: TestContext.Current.CancellationToken);
        createResponse.EnsureSuccessStatusCode();
        var template = await createResponse.Content.ReadFromJsonAsync<TemplateResponse>(ApiJsonOptions, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(template);
        Assert.NotNull(template.CurrentVersion);

        var request = new TemplateFieldRequest(null, "openField", "Open Field", null, 0, false, 0, 1, true, CompositionMode.Inline, null, null,
            [new TemplateFieldAllowedTypeRequest(PrimitiveType.Text, null)], null);

        var response = await client.PostAsJsonAsync($"/api/v1/workspaces/{workspaceId}/templates/{template.Id}/versions/{template.CurrentVersion!.VersionNumber}/fields", request, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }
```

(This file already has `GetWorkspaceIdAsync`/`LoginAsync` helpers and the `PrimitiveType`/`CompositionMode` contract aliases at the top — no new usings needed.)

- [ ] **Step 6: Run it to verify it fails**

Run: `dotnet test tests/Cmsify.Api.Integration.Tests/Cmsify.Api.Integration.Tests.csproj --filter CreateField_OpenFieldWithPrimitiveTypeAllowedType_ReturnsValidationError`
Expected: FAILS (currently returns 201, not 422) — requires Docker (Testcontainers Postgres).

- [ ] **Step 7: Add the mirrored rule to `ValidateFieldRequestAsync`**

In `src/Cmsify.Api/Controllers/TemplatesController.cs`, in `ValidateFieldRequestAsync`, right after the existing block at lines 591-594 (`if (request.IsOpen && (request.PrimitiveType.HasValue ...`), add:

```csharp
        if (request.IsOpen && request.AllowedTypes.Any(allowedType => allowedType.PrimitiveType.HasValue))
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Invalid field type", "Open fields' allowedTypes entries cannot define a primitiveType.");
        }
```

- [ ] **Step 8: Run both tests to verify they pass**

Run: `dotnet test tests/Cmsify.Core.Tests/Cmsify.Core.Tests.csproj --filter TemplateFieldInputValidatorTests` and `dotnet test tests/Cmsify.Api.Integration.Tests/Cmsify.Api.Integration.Tests.csproj --filter TemplateApiTests`
Expected: all PASS, including the full existing `TemplateApiTests` suite (no regressions).

- [ ] **Step 9: Commit**

```bash
git add src/Cmsify.Core/Validation/CommandValidators.cs src/Cmsify.Api/Controllers/TemplatesController.cs tests/Cmsify.Core.Tests/TemplateFieldInputValidatorTests.cs tests/Cmsify.Api.Integration.Tests/TemplateApiTests.cs
git commit -m "fix: reject open template fields whose AllowedTypes define a PrimitiveType"
```

---

## Task 2: Restore `sdk/dotnet` against the version-centric Content API

**Files:**
- Modify: `sdk/dotnet/src/SyntaxCircus.Cmsify.Client/CmsifyServices.cs:58-86` (`ContentClient`)
- Modify: `sdk/dotnet/tests/SyntaxCircus.Cmsify.Client.Tests/CmsifyClientTests.cs` (facade-routes test, ~lines 1040-1071)
- Modify: `sdk/dotnet/tests/SyntaxCircus.Cmsify.Client.Tests/ContentCachingTests.cs:154` (fixture helper)

**Interfaces:**
- Consumes: server routes on `src/Cmsify.Api/Controllers/ContentController.cs` (verified by reading the file directly — do not re-derive): `POST/PUT/DELETE .../content/{id}/versions[/{versionNumber}]`, `POST .../versions/{versionNumber}/{submit|approve|reject|archive|restore|publish|upgrade-template-version}`; contracts in `src/Cmsify.Contracts/WireContracts.cs:89-113`.
- Produces: `ContentClient.CreateVersionAsync`, `UpdateVersionAsync`, `DeleteVersionAsync`, `SubmitVersionAsync`, `ApproveVersionAsync`, `RejectVersionAsync`, `ArchiveVersionAsync`, `RestoreVersionAsync`, `PublishVersionAsync`, `UpgradeTemplateVersionAsync` — Tasks 3 and 4 call these by these exact names.

### Background

`ContentClient.PublishAsync` (line 73) references the deleted `PublishContentRequest`/`PublishContentResponse` types — a real compile error, confirmed by an actual `dotnet build`. The item-level workflow methods (`SubmitAsync`, `ApproveAsync`, `RejectAsync`, `ArchiveAsync`, `RestoreAsync`, `UpgradeVersionAsync`, `RollbackAsync`/`RollbackVersionAsync`, `TransitionAsync`) call routes that no longer exist server-side (confirmed by reading `ContentController.cs` directly — every item-level workflow action was replaced by a version-scoped equivalent, and there is no route for "rollback" any more; a rollback is now "create a new Draft version duplicated from an old one"). Separately — **found by reading the controller, not caught by the build-only scoping pass, since it's a silent runtime bug, not a compile error** — `BySlugAsync` (line 65) declares a return type of `ContentItemDetailResponse?`, but `GET .../content/by-slug/{slug}` (`ContentController.cs:237-253`) actually returns a `ContentVersionDetailResponse` body (confirmed live via the Chrome pass — the JSON has `versionNumber`/`fields`/etc., not `templateVersionId`/`versions`). This must be fixed to `ContentVersionDetailResponse?` or callers silently get an incorrectly-deserialized object.

- [ ] **Step 1: Replace `ContentClient` in `CmsifyServices.cs`**

Replace the entire `ContentClient` class (`sdk/dotnet/src/SyntaxCircus.Cmsify.Client/CmsifyServices.cs:58-86`) with:

```csharp
public sealed class ContentClient(CmsifyClient client)
{
    public Task<PagedResponse<ContentItemSummaryResponse>?> ListAsync(Guid workspaceId, ContentStatus? status, Guid? templateId, string? locale, string? tags, string? q, CancellationToken ct = default) =>
        ListAsync(workspaceId, new ContentListQuery(q, null, templateId, status, locale, null, null, tags, null, null, null, null, false, null, "updatedAt", true, 1, 20), ct);
    public Task<PagedResponse<ContentItemSummaryResponse>?> ListAsync(Guid workspaceId, ContentListQuery? query = null, CancellationToken ct = default) => client.GetAsync<PagedResponse<ContentItemSummaryResponse>>(CmsifyClient.WorkspacePath(workspaceId, $"/content{Query.Content(query)}"), ct);
    public IAsyncEnumerable<ContentItemSummaryResponse> ListAllAsync(Guid workspaceId, ContentListQuery? query = null, CancellationToken ct = default) => CmsifyClient.ListAll((page, cancellationToken) => ListAsync(workspaceId, query is null ? new ContentListQuery(null, null, null, null, null, null, null, null, null, null, null, null, false, null, "createdAt", true, page, 20) : query with { Page = page }, cancellationToken), ct);
    public Task<ContentItemDetailResponse?> GetAsync(Guid workspaceId, Guid id, bool resolve = false, DateTimeOffset? asOf = null, CancellationToken ct = default) => client.GetAsync<ContentItemDetailResponse>(CmsifyClient.WorkspacePath(workspaceId, $"/content/{id}{Query.ContentDetail(resolve, asOf)}"), ct);
    public Task<ContentVersionDetailResponse?> BySlugAsync(Guid workspaceId, string slug, DateTimeOffset? asOf = null, CancellationToken ct = default) => client.GetAsync<ContentVersionDetailResponse>(CmsifyClient.WorkspacePath(workspaceId, $"/content/by-slug/{Uri.EscapeDataString(slug)}{Query.OptionalQuery("asOf", asOf)}"), ct);
    public Task<ContentItemDetailResponse?> CreateAsync(Guid workspaceId, CreateContentItemRequest request, CancellationToken ct = default) => client.PostAsync<ContentItemDetailResponse>(CmsifyClient.WorkspacePath(workspaceId, "/content"), request, ct);
    public Task<ContentItemDetailResponse?> UpdateAsync(Guid workspaceId, Guid id, UpdateContentItemRequest request, CancellationToken ct = default, string? ifMatch = null) => ifMatch is null ? client.PutAsync<ContentItemDetailResponse>(CmsifyClient.WorkspacePath(workspaceId, $"/content/{id}"), request, ct) : client.PutAsync<ContentItemDetailResponse>(CmsifyClient.WorkspacePath(workspaceId, $"/content/{id}"), request, ifMatch, ct);
    public Task DeleteAsync(Guid workspaceId, Guid id, CancellationToken ct = default, string? ifMatch = null) => ifMatch is null ? client.DeleteAsync<object>(CmsifyClient.WorkspacePath(workspaceId, $"/content/{id}"), ct) : client.DeleteAsync<object>(CmsifyClient.WorkspacePath(workspaceId, $"/content/{id}"), ifMatch, ct);
    public Task<PagedResponse<ContentItemSummaryResponse>?> TranslationsAsync(Guid workspaceId, Guid id, int page = 1, int pageSize = 20, CancellationToken ct = default) => client.GetAsync<PagedResponse<ContentItemSummaryResponse>>(CmsifyClient.WorkspacePath(workspaceId, $"/content/{id}/translations?page={page}&pageSize={pageSize}"), ct);
    public Task<IReadOnlyList<ContentItemSummaryResponse>> ListAllTranslationsAsync(Guid workspaceId, Guid id, CancellationToken ct = default) => CmsifyClient.ListAllToListAsync((page, cancellationToken) => TranslationsAsync(workspaceId, id, page, 100, cancellationToken), ct);
    public Task<IReadOnlyList<ContentItemSummaryResponse>?> LinkTranslationAsync(Guid workspaceId, Guid id, LinkTranslationRequest request, CancellationToken ct = default) => client.PostAsync<IReadOnlyList<ContentItemSummaryResponse>>(CmsifyClient.WorkspacePath(workspaceId, $"/content/{id}/link-translation"), request, ct);

    public Task<ContentVersionDetailResponse?> CreateVersionAsync(Guid workspaceId, Guid id, CreateContentVersionRequest request, CancellationToken ct = default) => client.PostAsync<ContentVersionDetailResponse>(CmsifyClient.WorkspacePath(workspaceId, $"/content/{id}/versions"), request, ct);
    public Task<PagedResponse<ContentVersionSummaryResponse>?> VersionsAsync(Guid workspaceId, Guid id, int page = 1, int pageSize = 20, CancellationToken ct = default) => client.GetAsync<PagedResponse<ContentVersionSummaryResponse>>(CmsifyClient.WorkspacePath(workspaceId, $"/content/{id}/versions?page={page}&pageSize={pageSize}"), ct);
    public Task<PagedResponse<ContentVersionSummaryResponse>?> ListVersionsAsync(Guid workspaceId, Guid id, int page = 1, int pageSize = 20, CancellationToken ct = default) => VersionsAsync(workspaceId, id, page, pageSize, ct);
    public Task<IReadOnlyList<ContentVersionSummaryResponse>> ListAllVersionsAsync(Guid workspaceId, Guid id, CancellationToken ct = default) => CmsifyClient.ListAllToListAsync((page, cancellationToken) => ListVersionsAsync(workspaceId, id, page, 100, cancellationToken), ct);
    public Task<ContentVersionDetailResponse?> GetVersionAsync(Guid workspaceId, Guid id, int versionNumber, CancellationToken ct = default) => client.GetAsync<ContentVersionDetailResponse>(CmsifyClient.WorkspacePath(workspaceId, $"/content/{id}/versions/{versionNumber}"), ct);
    public Task<ContentVersionDetailResponse?> UpdateVersionAsync(Guid workspaceId, Guid id, int versionNumber, UpdateContentVersionRequest request, CancellationToken ct = default) => client.PutAsync<ContentVersionDetailResponse>(CmsifyClient.WorkspacePath(workspaceId, $"/content/{id}/versions/{versionNumber}"), request, ct);
    public Task DeleteVersionAsync(Guid workspaceId, Guid id, int versionNumber, CancellationToken ct = default) => client.DeleteAsync<object>(CmsifyClient.WorkspacePath(workspaceId, $"/content/{id}/versions/{versionNumber}"), ct);

    public Task<ContentVersionDetailResponse?> SubmitVersionAsync(Guid workspaceId, Guid id, int versionNumber, CancellationToken ct = default) => client.PostAsync<ContentVersionDetailResponse>(CmsifyClient.WorkspacePath(workspaceId, $"/content/{id}/versions/{versionNumber}/submit"), null, ct);
    public Task<ContentVersionDetailResponse?> ApproveVersionAsync(Guid workspaceId, Guid id, int versionNumber, CancellationToken ct = default) => client.PostAsync<ContentVersionDetailResponse>(CmsifyClient.WorkspacePath(workspaceId, $"/content/{id}/versions/{versionNumber}/approve"), null, ct);
    public Task<ContentVersionDetailResponse?> RejectVersionAsync(Guid workspaceId, Guid id, int versionNumber, RejectContentRequest request, CancellationToken ct = default) => client.PostAsync<ContentVersionDetailResponse>(CmsifyClient.WorkspacePath(workspaceId, $"/content/{id}/versions/{versionNumber}/reject"), request, ct);
    public Task<ContentVersionDetailResponse?> ArchiveVersionAsync(Guid workspaceId, Guid id, int versionNumber, CancellationToken ct = default) => client.PostAsync<ContentVersionDetailResponse>(CmsifyClient.WorkspacePath(workspaceId, $"/content/{id}/versions/{versionNumber}/archive"), null, ct);
    public Task<ContentVersionDetailResponse?> RestoreVersionAsync(Guid workspaceId, Guid id, int versionNumber, CancellationToken ct = default) => client.PostAsync<ContentVersionDetailResponse>(CmsifyClient.WorkspacePath(workspaceId, $"/content/{id}/versions/{versionNumber}/restore"), null, ct);
    public Task<PublishContentVersionResponse?> PublishVersionAsync(Guid workspaceId, Guid id, int versionNumber, PublishContentVersionRequest? request = null, CancellationToken ct = default) => client.PostAsync<PublishContentVersionResponse>(CmsifyClient.WorkspacePath(workspaceId, $"/content/{id}/versions/{versionNumber}/publish"), request, ct);
    public Task<ContentVersionDetailResponse?> UpgradeTemplateVersionAsync(Guid workspaceId, Guid id, int versionNumber, CancellationToken ct = default) => client.PostAsync<ContentVersionDetailResponse>(CmsifyClient.WorkspacePath(workspaceId, $"/content/{id}/versions/{versionNumber}/upgrade-template-version"), null, ct);
}
```

Do not change anything else in the file (`WorkspaceClient`, `TemplateClient`, `MediaClient`, etc. are unaffected).

- [ ] **Step 2: Build to verify the SDK itself compiles**

Run: `dotnet build sdk/dotnet/src/SyntaxCircus.Cmsify.Client/SyntaxCircus.Cmsify.Client.csproj`
Expected: 0 errors.

- [ ] **Step 3: Fix `CmsifyClientTests.cs`'s facade-routes test**

In `sdk/dotnet/tests/SyntaxCircus.Cmsify.Client.Tests/CmsifyClientTests.cs`, in `AddedFacadeOperations_UseTheirDocumentedRoutes` (~lines 1040-1071):

Replace:
```csharp
        await client.Content.UpgradeVersionAsync(workspaceId, resourceId, TestContext.Current.CancellationToken);
        await client.Content.LinkTranslationAsync(workspaceId, resourceId, new LinkTranslationRequest(Guid.NewGuid()), TestContext.Current.CancellationToken);
        await client.Content.GetVersionAsync(workspaceId, resourceId, version, TestContext.Current.CancellationToken);
        await client.Content.RollbackVersionAsync(workspaceId, resourceId, version, TestContext.Current.CancellationToken);
```
with:
```csharp
        await client.Content.UpgradeTemplateVersionAsync(workspaceId, resourceId, version, TestContext.Current.CancellationToken);
        await client.Content.LinkTranslationAsync(workspaceId, resourceId, new LinkTranslationRequest(Guid.NewGuid()), TestContext.Current.CancellationToken);
        await client.Content.GetVersionAsync(workspaceId, resourceId, version, TestContext.Current.CancellationToken);
        await client.Content.CreateVersionAsync(workspaceId, resourceId, new CreateContentVersionRequest(null, null, version, null), TestContext.Current.CancellationToken);
```

And replace the assertion:
```csharp
        routes.ShouldContain(route => route.Contains($"POST /api/v1/workspaces/{workspaceId}/content/{resourceId}/upgrade-version"));
```
with:
```csharp
        routes.ShouldContain($"POST /api/v1/workspaces/{workspaceId}/content/{resourceId}/versions/{version}/upgrade-template-version");
        routes.ShouldContain($"POST /api/v1/workspaces/{workspaceId}/content/{resourceId}/versions");
```

(Leave the `GET .../components/...` and `.../webhooks/.../deliveries/` and `.../packages/export` assertions after it untouched.)

- [ ] **Step 4: Fix `ContentCachingTests.cs`'s fixture helper**

In `sdk/dotnet/tests/SyntaxCircus.Cmsify.Client.Tests/ContentCachingTests.cs:154`, replace:
```csharp
    private static ContentItemDetailResponse Content(int call) => new(Guid.NewGuid(), Guid.NewGuid(), "post", ContentStatus.Published, $"post-{call}", null, null, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, null, []);
```
with:
```csharp
    private static ContentItemDetailResponse Content(int call) => new(Guid.NewGuid(), Guid.NewGuid(), "post", $"post-{call}", null, null, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, []);
```
(matches the new 11-arg `ContentItemDetailResponse(Id, TemplateVersionId, TemplateName, Slug, LocaleCode, TranslationGroupId, Tags, CreatedAt, UpdatedAt, CurrentlyServingVersion, Versions)` — only `.Slug` is asserted by these tests, so `CurrentlyServingVersion: null` and `Versions: []` are fine.)

- [ ] **Step 5: Run the full sdk/dotnet test suite**

Run: `dotnet test sdk/dotnet/tests/SyntaxCircus.Cmsify.Client.Tests/SyntaxCircus.Cmsify.Client.Tests.csproj`
Expected: all tests PASS, 0 build errors. Also run `dotnet build sdk/dotnet/src/SyntaxCircus.Cmsify.Client.DistributedCaching/SyntaxCircus.Cmsify.Client.DistributedCaching.csproj` to confirm that project (which only consumes `ContentClient.GetAsync`, unchanged) now builds clean now that its `ProjectReference` compiles.

- [ ] **Step 6: Commit**

```bash
git add sdk/dotnet/src/SyntaxCircus.Cmsify.Client/CmsifyServices.cs sdk/dotnet/tests/SyntaxCircus.Cmsify.Client.Tests/CmsifyClientTests.cs sdk/dotnet/tests/SyntaxCircus.Cmsify.Client.Tests/ContentCachingTests.cs
git commit -m "fix(sdk/dotnet): restore ContentClient against the version-centric Content API"
```

---

## Task 3: Fix `Cmsify.Components`

**Files:**
- Modify: `src/Cmsify.Components/ContentListView.razor:49`
- Modify: `src/Cmsify.Components/FieldEditors/ReferenceFieldEditor.razor:7`
- Modify: `src/Cmsify.Components/Client/ContentEditPanel.razor`
- Modify: `tests/Cmsify.Components.Tests/Client/ContentEditPanelTests.cs`

**Interfaces:**
- Consumes: `ContentClient.GetAsync`, `GetVersionAsync`, `UpdateVersionAsync`, `UpdateAsync`, `CreateAsync` from Task 2.
- Produces: `ContentEditPanel` gains a new `[Parameter] public int? VersionNumber { get; set; }` — Task 4's `ContentEditor.razor` sets this to the version number it has resolved as editable (creating a fresh Draft first if the current version is Published/Archived).

### Step 1: Mechanical renames

- [ ] In `src/Cmsify.Components/ContentListView.razor:49`, replace:
```razor
                            @contentItem.Status
```
with:
```razor
                            @(contentItem.CurrentlyServingVersion?.Status.ToString() ?? "—")
```

- [ ] In `src/Cmsify.Components/FieldEditors/ReferenceFieldEditor.razor:7`, replace:
```razor
        <option value="@option.Id">@(option.Slug ?? option.Id.ToString()) (@option.Status)</option>
```
with:
```razor
        <option value="@option.Id">@(option.Slug ?? option.Id.ToString()) (@(option.CurrentlyServingVersion?.Status.ToString() ?? "—"))</option>
```

### Step 2: Rework `ContentEditPanel.razor` to target a specific `ContentVersion`

- [ ] Add a `VersionNumber` parameter and a `version` field, and change `LoadContentAsync` to load the version's fields (not the item's). In the `@code` block, add after `[Parameter] public string? SlugHelpText { get; set; }`:

```csharp
    [Parameter] public int? VersionNumber { get; set; }
```

Add a new private field next to `private ContentItemDetailResponse? item;`:

```csharp
    private ContentVersionDetailResponse? version;
```

Replace `LoadContentAsync` in full with:

```csharp
    private async Task LoadContentAsync()
    {
        var loadedItem = await Client.Content.GetAsync(WorkspaceId, ContentId!.Value)
            ?? throw new InvalidOperationException("Cmsify API returned no payload while loading content.");
        item = loadedItem;
        await ItemChanged.InvokeAsync(item);
        slug = loadedItem.Slug;
        locale = loadedItem.LocaleCode;
        tags = string.Join(",", loadedItem.Tags);

        var versionNumber = VersionNumber ?? loadedItem.CurrentlyServingVersion?.VersionNumber ?? 1;
        var loadedVersion = await Client.Content.GetVersionAsync(WorkspaceId, ContentId!.Value, versionNumber)
            ?? throw new InvalidOperationException("Cmsify API returned no payload while loading a content version.");
        version = loadedVersion;

        var templates = (await Client.Templates.ListAsync(WorkspaceId))?.Items ?? [];
        var template = templates.FirstOrDefault(t => t.CurrentVersionId == loadedVersion.TemplateVersionId);
        if (template is not null)
        {
            await LoadTemplateVersionAsync(template.Id);
        }

        fieldValues.Clear();
        foreach (var value in loadedVersion.Fields)
        {
            var editorValue = GetOrAddValue(value.FieldId);
            editorValue.TextValue = value.TextValue;
            editorValue.BoolValue = value.BoolValue ?? false;
            editorValue.ChildContentItemId = value.ChildContentItemId;
            if (!string.IsNullOrWhiteSpace(value.TextValue))
            {
                editorValue.MultiValues = [.. editorValue.MultiValues, value.TextValue];
            }
            if (value.ValueKind == ValueKind.Component && value.JsonValue.HasValue)
            {
                editorValue.ComponentValues = [.. editorValue.ComponentValues, value.JsonValue.Value.GetRawText()];
            }
            if (value.MediaAssetId.HasValue)
            {
                try
                {
                    editorValue.SelectedMediaAsset = await Client.Media.GetAsync(WorkspaceId, value.MediaAssetId.Value);
                }
                catch (CmsifyApiException)
                {
                    editorValue.FallbackMediaAssetId = value.MediaAssetId.Value;
                }
            }
            if (value.FileAssetId.HasValue)
            {
                try
                {
                    editorValue.SelectedFileAsset = await Client.Media.GetAsync(WorkspaceId, value.FileAssetId.Value);
                }
                catch (CmsifyApiException)
                {
                    editorValue.FallbackFileAssetId = value.FileAssetId.Value;
                }
            }
        }
    }
```

(Only the source of the load — `loadedVersion.Fields` instead of `loadedItem.Fields` — and the two new lines fetching the version changed; the per-field body is byte-for-byte the same as before, since `ContentVersionFieldValueResponse` has the same `FieldId`/`TextValue`/`BoolValue`/`ChildContentItemId`/`ValueKind`/`JsonValue`/`MediaAssetId`/`FileAssetId` members as the old item-level `ContentFieldValueResponse` did.)

- [ ] Replace `SaveAsync`'s save branch (lines 313-344 of the original file) with:

```csharp
        var tagList = (tags ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        try
        {
            error = null;
            if (ContentId.HasValue)
            {
                var versionNumber = VersionNumber ?? version?.VersionNumber ?? 1;
                var updatedVersion = await Client.Content.UpdateVersionAsync(WorkspaceId, ContentId.Value, versionNumber,
                    new UpdateContentVersionRequest(version?.EffectiveStartAt, version?.EffectiveEndAt, values))
                    ?? throw new InvalidOperationException("Cmsify API returned no payload while updating the content version.");
                version = updatedVersion;

                var updatedItem = await Client.Content.UpdateAsync(WorkspaceId, ContentId.Value,
                    new UpdateContentItemRequest(string.IsNullOrWhiteSpace(slug) ? null : slug, locale, item?.TranslationGroupId, tagList))
                    ?? throw new InvalidOperationException("Cmsify API returned no payload while updating content.");
                item = updatedItem;

                await ItemChanged.InvokeAsync(item);
                await Saved.InvokeAsync(item);
            }
            else
            {
                var createdItem = await Client.Content.CreateAsync(WorkspaceId,
                    new CreateContentItemRequest(templateVersion.Id, string.IsNullOrWhiteSpace(slug) ? null : slug, locale, null, tagList, values))
                    ?? throw new InvalidOperationException("Cmsify API returned no payload while creating content.");
                item = createdItem;
                await ItemChanged.InvokeAsync(item);
                await Created.InvokeAsync(createdItem);
            }
        }
        catch (CmsifyApiException ex)
        {
            error = ex.Message;
            if (ex.StatusCode == System.Net.HttpStatusCode.PreconditionFailed && ContentId.HasValue)
            {
                await LoadContentAsync();
                error = "Content changed while saving. The latest version has been reloaded; please review and save again.";
            }
        }
```

(Everything above this in `SaveAsync` — building the `values` list from `templateVersion.Fields` — is unchanged; only the final save block changed. Identity/tags now save via `UpdateAsync`; field values now save via `UpdateVersionAsync`.)

- [ ] **Step 3: Build to verify the component compiles**

Run: `dotnet build src/Cmsify.Components/Cmsify.Components.csproj`
Expected: 0 errors.

- [ ] **Step 4: Update `ContentEditPanelTests.cs` for the new load/save sequence**

The component now issues one extra `GET .../content/{id}/versions/{n}` request when `ContentId` is set, and saves via `PUT .../content/{id}/versions/{n}` (fields) + `PUT .../content/{id}` (identity/tags) instead of a single `PUT .../content/{id}`. Rewrite each of the 5 tests in `tests/Cmsify.Components.Tests/Client/ContentEditPanelTests.cs` to mock the new request sequence, keeping each test's original intent. Worked example — `EditingExistingContentLoadsCurrentValuesAndSavesUpdates` becomes:

```csharp
    [Fact]
    public void EditingExistingContentLoadsCurrentValuesAndSavesUpdates()
    {
        var workspaceId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        var templateVersionId = Guid.NewGuid();
        var contentId = Guid.NewGuid();
        var fieldId = Guid.NewGuid();
        string? capturedVersionUpdateBody = null;
        string? capturedItemUpdateBody = null;

        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{contentId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{contentId}}", "templateVersionId": "{{templateVersionId}}", "templateName": "Article",
                      "slug": "existing-post", "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z",
                      "currentlyServingVersion": null, "versions": [] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{contentId}/versions/1")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{Guid.NewGuid()}}", "contentItemId": "{{contentId}}", "versionNumber": 1, "status": "Draft",
                      "templateVersionId": "{{templateVersionId}}", "templateName": "Article", "slug": "existing-post",
                      "localeCode": null, "translationGroupId": null, "effectiveStartAt": null, "effectiveEndAt": null,
                      "publishAt": null, "publishedAt": null, "archivedAt": null, "publishedByUserId": null,
                      "rolledBackFromVersionNumber": null, "tags": [], "createdAt": "2026-01-01T00:00:00Z",
                      "updatedAt": "2026-01-01T00:00:00Z",
                      "fields": [
                        { "fieldId": "{{fieldId}}", "key": "title", "label": "Title", "order": 0, "valueKind": "Text",
                          "textValue": "Existing", "boolValue": null, "mediaAssetId": null, "fileAssetId": null,
                          "childContentItemId": null, "child": null, "jsonValue": null, "displayLabel": null }
                      ] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "items": [{ "id": "{{templateId}}", "workspaceId": "{{workspaceId}}", "name": "Article", "slug": "article", "description": null, "currentVersionId": "{{templateVersionId}}" }], "totalCount": 1, "page": 1, "pageSize": 20 }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates/{templateId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    {
                      "id": "{{templateId}}", "workspaceId": "{{workspaceId}}", "name": "Article", "slug": "article",
                      "description": null, "isSystem": false,
                      "currentVersion": {
                        "id": "{{templateVersionId}}", "templateId": "{{templateId}}", "versionNumber": 1,
                        "status": "Published", "publishedAt": null, "notes": null, "sections": [],
                        "fields": [
                          { "id": "{{fieldId}}", "sectionId": null, "key": "title", "label": "Title", "helpText": null,
                            "order": 0, "isRequired": false, "minOccurrences": 0, "maxOccurrences": null, "isOpen": false,
                            "compositionMode": "Reference", "primitiveType": "Text", "templateId": null,
                            "allowedTypes": [], "fieldConfig": null, "componentId": null }
                        ]
                      }
                    }
                    """);
            }
            if (request.Method == HttpMethod.Put && path == $"/api/v1/workspaces/{workspaceId}/content/{contentId}/versions/1")
            {
                capturedVersionUpdateBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{Guid.NewGuid()}}", "contentItemId": "{{contentId}}", "versionNumber": 1, "status": "Draft",
                      "templateVersionId": "{{templateVersionId}}", "templateName": "Article", "slug": "existing-post",
                      "localeCode": null, "translationGroupId": null, "effectiveStartAt": null, "effectiveEndAt": null,
                      "publishAt": null, "publishedAt": null, "archivedAt": null, "publishedByUserId": null,
                      "rolledBackFromVersionNumber": null, "tags": [], "createdAt": "2026-01-01T00:00:00Z",
                      "updatedAt": "2026-01-02T00:00:00Z", "fields": [] }
                    """);
            }
            if (request.Method == HttpMethod.Put && path == $"/api/v1/workspaces/{workspaceId}/content/{contentId}")
            {
                capturedItemUpdateBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{contentId}}", "templateVersionId": "{{templateVersionId}}", "templateName": "Article",
                      "slug": "existing-post", "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-02T00:00:00Z",
                      "currentlyServingVersion": null, "versions": [] }
                    """);
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        ContentItemDetailResponse? saved = null;
        var cut = Render<SyntaxCircus.Cmsify.Components.Client.ContentEditPanel>(parameters => parameters
            .Add(p => p.Client, client)
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.ContentId, contentId)
            .Add(p => p.Saved, EventCallback.Factory.Create<ContentItemDetailResponse>(this, c => saved = c)));

        cut.WaitForState(() => cut.FindAll("input").Any(input => input.GetAttribute("value") == "Existing"));

        cut.Find("input").GetAttribute("value").ShouldBe("Existing");

        cut.Find("input").Input("Updated");
        cut.Find(".cmsify-form-save-button").Click();

        cut.WaitForState(() => capturedVersionUpdateBody is not null);
        cut.WaitForState(() => capturedItemUpdateBody is not null);
        cut.WaitForState(() => saved is not null);

        capturedVersionUpdateBody.ShouldNotBeNull();
        capturedVersionUpdateBody.ShouldContain("Updated");
        saved.ShouldNotBeNull();
        saved!.Id.ShouldBe(contentId);
    }
```

Apply the same pattern (item GET drops `status`/`fields`, gains `currentlyServingVersion`/`versions`; a new `GET .../versions/1` mock returns the field data; a save mocks `PUT .../versions/1` for field changes and, where the test triggers a save, `PUT .../content/{id}` for identity) to the other 4 tests: `CreatingNewContentLoadsTemplateAndSavesFieldValues` (no `ContentId` set — no version fetch needed, `CreateAsync`/`POST /content` response just drops `status`/`fields`, gains `currentlyServingVersion: null, versions: []`), `RaisesItemChangedAfterLoadingExistingContent` (add the `GET .../versions/1` mock; assert `changed!.Id` only — `changed.Status` no longer exists on `ContentItemDetailResponse`, remove that assertion), `SavingInvalidComponentFieldJsonSetsErrorInsteadOfThrowing` (no `ContentId` — unaffected by the version fetch, no change needed beyond confirming it still compiles), `LoadingContentWithDanglingMediaAssetDoesNotCrashAndRoundTripsIdOnSave` (add the `GET .../versions/1` mock carrying the media field, and both `PUT` mocks).

- [ ] **Step 5: Run the full Components test suite**

Run: `dotnet test tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj`
Expected: all tests PASS, 0 build errors.

- [ ] **Step 6: Commit**

```bash
git add src/Cmsify.Components/ContentListView.razor src/Cmsify.Components/FieldEditors/ReferenceFieldEditor.razor src/Cmsify.Components/Client/ContentEditPanel.razor tests/Cmsify.Components.Tests/Client/ContentEditPanelTests.cs
git commit -m "fix(components): target a specific ContentVersion for load/save"
```

---

## Task 4: Fix `Cmsify.Admin`

**Files:**
- Modify: `src/Cmsify.Admin/Components/Pages/Content/ContentEditor.razor`
- Modify: `src/Cmsify.Admin/Components/Pages/Content/ContentList.razor`
- Modify: `src/Cmsify.Admin/Components/Pages/Content/ContentVersions.razor`
- Modify: `src/Cmsify.Admin/Components/Shared/PublishContentDialog.razor`
- Modify: `src/Cmsify.Admin/Components/Pages/Workspaces/WorkspaceDetail.razor:51`

**Interfaces:**
- Consumes: `ContentClient` methods from Task 2; `ContentEditPanel.VersionNumber` from Task 3; `ContentWorkflowActions.*State(ContentStatus, ...)` (`src/Cmsify.Admin/Services/ContentWorkflowActions.cs` — unchanged, confirmed by reading it directly).

### Step 1: `PublishContentDialog.razor` — drop bounded-window fields, target a version

- [ ] Replace the entire file with:

```razor
@if (ContentId.HasValue && VersionNumber.HasValue)
{
    <div class="modal d-block" tabindex="-1" role="dialog" aria-modal="true">
        <div class="modal-dialog">
            <div class="modal-content">
                <div class="modal-header">
                    <h5 class="modal-title">Publish content</h5>
                    <button type="button" class="btn-close" @onclick="Close" aria-label="Close"></button>
                </div>
                <div class="modal-body">
                    @if (!string.IsNullOrWhiteSpace(publishError))
                    {
                        <div class="alert alert-danger">@publishError</div>
                    }
                    @if (publishWarnings.Count > 0)
                    {
                        <div class="alert alert-warning">
                            @foreach (var warning in publishWarnings)
                            {
                                <div>@warning</div>
                            }
                        </div>
                    }
                    @if (NeedsOverrideConfirmation)
                    {
                        <div class="alert alert-warning">
                            <div>This will publish directly from <strong>@VersionStatus</strong>, skipping Submit/Approve.</div>
                            <div class="form-check mt-2">
                                <input id="overrideConfirmed" class="form-check-input" type="checkbox" @bind="overrideConfirmed" />
                                <label class="form-check-label" for="overrideConfirmed">I understand, publish anyway</label>
                            </div>
                        </div>
                    }
                    else
                    {
                        <div class="mb-3">
                            <label class="form-label">Schedule publish at (UTC, optional)</label>
                            <input class="form-control" placeholder="2026-12-01T00:00:00Z" @bind="publishAtInput" />
                        </div>
                    }
                </div>
                <div class="modal-footer">
                    <button class="btn btn-outline-secondary" type="button" @onclick="Close">Cancel</button>
                    <button class="btn btn-primary" type="button" disabled="@(NeedsOverrideConfirmation && !overrideConfirmed)" @onclick="PublishAsync">Publish</button>
                </div>
            </div>
        </div>
    </div>
    <div class="modal-backdrop show"></div>
}

@code {
    [Parameter] public Guid WorkspaceId { get; set; }
    [Parameter] public Guid? ContentId { get; set; }
    [Parameter] public int? VersionNumber { get; set; }
    [Parameter] public ContentStatus VersionStatus { get; set; }
    [Parameter] public bool CanOverrideWorkflow { get; set; }
    [Parameter, EditorRequired] public CmsifyClient Cmsify { get; set; } = default!;
    [Parameter] public EventCallback OnClosed { get; set; }
    [Parameter] public EventCallback<PublishContentVersionResponse> OnPublished { get; set; }

    private Guid? lastContentId;
    private string? publishAtInput;
    private string? publishError;
    private IReadOnlyList<string> publishWarnings = [];
    private bool overrideConfirmed;

    private bool NeedsOverrideConfirmation => CanOverrideWorkflow && VersionStatus is ContentStatus.Draft or ContentStatus.Review;

    protected override void OnParametersSet()
    {
        if (ContentId == lastContentId)
        {
            return;
        }

        lastContentId = ContentId;
        publishAtInput = null;
        publishError = null;
        publishWarnings = [];
        overrideConfirmed = false;
    }

    private Task Close() => OnClosed.InvokeAsync();

    private async Task PublishAsync()
    {
        if (!ContentId.HasValue || !VersionNumber.HasValue)
        {
            return;
        }

        try
        {
            var request = new PublishContentVersionRequest(
                NeedsOverrideConfirmation ? null : ParseUtcDateTimeOffset(publishAtInput),
                NeedsOverrideConfirmation && overrideConfirmed);
            var response = ApiResponse.Required(
                await Cmsify.Content.PublishVersionAsync(WorkspaceId, ContentId.Value, VersionNumber.Value, request),
                "publishing content");
            publishWarnings = response.Warnings;
            await OnPublished.InvokeAsync(response);
            if (publishWarnings.Count == 0)
            {
                await Close();
            }
        }
        catch (Exception ex)
        {
            publishError = ex.Message;
        }
    }

    private static DateTimeOffset? ParseUtcDateTimeOffset(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTime.SpecifyKind(DateTime.Parse(value), DateTimeKind.Utc);
    }
}
```

### Step 2: `WorkspaceDetail.razor` — mechanical

- [ ] In `src/Cmsify.Admin/Components/Pages/Workspaces/WorkspaceDetail.razor:51`, replace:
```csharp
        publishedCount = ApiResponse.ItemsOrEmpty(content).Count(item => item.Status == ContentStatus.Published);
```
with:
```csharp
        publishedCount = ApiResponse.ItemsOrEmpty(content).Count(item => item.CurrentlyServingVersion?.Status == ContentStatus.Published);
```

### Step 3: `ContentList.razor` — status source + version-scoped actions

- [ ] Replace the `StatusTemplate`, `RowActions` blocks, and the whole `@code` block. In the markup, replace:
```razor
        <StatusTemplate Context="contentItem">
            <StatusBadge Status="@contentItem.Status" />
        </StatusTemplate>
        <UpdatedTemplate Context="contentItem">
            <LocalTimeDisplay Value="@contentItem.UpdatedAt" />
        </UpdatedTemplate>
        <RowActions Context="contentItem">
            @{
                var submitState = ContentWorkflowActions.SubmitState(contentItem.Status);
                var approveState = ContentWorkflowActions.ApproveState(contentItem.Status);
                var rejectState = ContentWorkflowActions.RejectState(contentItem.Status, CurrentRole);
                var publishState = ContentWorkflowActions.PublishState(contentItem.Status, CurrentRole);
                var archiveState = ContentWorkflowActions.ArchiveState(contentItem.Status);
                var restoreState = ContentWorkflowActions.RestoreState(contentItem.Status, CurrentRole);
            }
            <button class="btn btn-outline-info btn-sm" disabled="@(!submitState.Enabled)" title="@submitState.DisabledReason" @onclick="() => RunTransitionAsync(contentItem.Id, SubmitAction)">Submit</button>
            <button class="btn btn-outline-success btn-sm" disabled="@(!approveState.Enabled)" title="@approveState.DisabledReason" @onclick="() => RunTransitionAsync(contentItem.Id, ApproveAction)">Approve</button>
            <button class="btn btn-outline-warning btn-sm" disabled="@(!rejectState.Enabled)" title="@rejectState.DisabledReason" @onclick="() => RunRejectAsync(contentItem.Id)">Reject</button>
            <button class="btn btn-success btn-sm" disabled="@(!publishState.Enabled)" title="@publishState.DisabledReason" @onclick="() => OpenPublishDialog(contentItem)">Publish</button>
            <button class="btn btn-outline-danger btn-sm" disabled="@(!archiveState.Enabled)" title="@archiveState.DisabledReason" @onclick="() => RunTransitionAsync(contentItem.Id, ArchiveAction)">Archive</button>
            <button class="btn btn-outline-secondary btn-sm" disabled="@(!restoreState.Enabled)" title="@restoreState.DisabledReason" @onclick="() => RunTransitionAsync(contentItem.Id, RestoreAction)">Restore</button>
        </RowActions>
    </ContentListPanel>
</div>

<PublishContentDialog WorkspaceId="@WorkspaceId" ContentId="@publishContentId" ContentStatus="@publishContentStatus"
                      CanOverrideWorkflow="@(CurrentRole >= UserRole.Admin)" Cmsify="@Cmsify"
                      OnClosed="ClosePublishDialog" OnPublished="OnPublishedAsync" />
```
with:
```razor
        <StatusTemplate Context="contentItem">
            <StatusBadge Status="@(contentItem.CurrentlyServingVersion?.Status ?? ContentStatus.Draft)" />
        </StatusTemplate>
        <UpdatedTemplate Context="contentItem">
            <LocalTimeDisplay Value="@contentItem.UpdatedAt" />
        </UpdatedTemplate>
        <RowActions Context="contentItem">
            @{
                var rowVersionStatus = contentItem.CurrentlyServingVersion?.Status ?? ContentStatus.Draft;
                var rowVersionNumber = contentItem.CurrentlyServingVersion?.VersionNumber ?? 1;
                var submitState = ContentWorkflowActions.SubmitState(rowVersionStatus);
                var approveState = ContentWorkflowActions.ApproveState(rowVersionStatus);
                var rejectState = ContentWorkflowActions.RejectState(rowVersionStatus, CurrentRole);
                var publishState = ContentWorkflowActions.PublishState(rowVersionStatus, CurrentRole);
                var archiveState = ContentWorkflowActions.ArchiveState(rowVersionStatus);
                var restoreState = ContentWorkflowActions.RestoreState(rowVersionStatus, CurrentRole);
            }
            <button class="btn btn-outline-info btn-sm" disabled="@(!submitState.Enabled)" title="@submitState.DisabledReason" @onclick="() => RunTransitionAsync(contentItem.Id, rowVersionNumber, SubmitAction)">Submit</button>
            <button class="btn btn-outline-success btn-sm" disabled="@(!approveState.Enabled)" title="@approveState.DisabledReason" @onclick="() => RunTransitionAsync(contentItem.Id, rowVersionNumber, ApproveAction)">Approve</button>
            <button class="btn btn-outline-warning btn-sm" disabled="@(!rejectState.Enabled)" title="@rejectState.DisabledReason" @onclick="() => RunRejectAsync(contentItem.Id, rowVersionNumber)">Reject</button>
            <button class="btn btn-success btn-sm" disabled="@(!publishState.Enabled)" title="@publishState.DisabledReason" @onclick="() => OpenPublishDialog(contentItem, rowVersionNumber, rowVersionStatus)">Publish</button>
            <button class="btn btn-outline-danger btn-sm" disabled="@(!archiveState.Enabled)" title="@archiveState.DisabledReason" @onclick="() => RunTransitionAsync(contentItem.Id, rowVersionNumber, ArchiveAction)">Archive</button>
            <button class="btn btn-outline-secondary btn-sm" disabled="@(!restoreState.Enabled)" title="@restoreState.DisabledReason" @onclick="() => RunTransitionAsync(contentItem.Id, rowVersionNumber, RestoreAction)">Restore</button>
        </RowActions>
    </ContentListPanel>
</div>

<PublishContentDialog WorkspaceId="@WorkspaceId" ContentId="@publishContentId" VersionNumber="@publishVersionNumber" VersionStatus="@publishVersionStatus"
                      CanOverrideWorkflow="@(CurrentRole >= UserRole.Admin)" Cmsify="@Cmsify"
                      OnClosed="ClosePublishDialog" OnPublished="OnPublishedAsync" />
```

- [ ] Replace the `@code` block with:
```csharp
@code {
    private ContentClient Content => Cmsify.Content;
    [Parameter] public Guid WorkspaceId { get; set; }
    private ContentListPanel? listPanel;
    private const string SubmitAction = "submit";
    private const string ApproveAction = "approve";
    private const string ArchiveAction = "archive";
    private const string RestoreAction = "restore";
    private Guid? publishContentId;
    private int? publishVersionNumber;
    private ContentStatus publishVersionStatus;

    private UserRole CurrentRole => Enum.TryParse<UserRole>(Auth.User?.Role, ignoreCase: true, out var role) ? role : UserRole.Reader;

    private void NewContent() => Navigation.NavigateTo($"/workspaces/{WorkspaceId}/content/new");

    private void EditContent(ContentItemSummaryResponse item) => Navigation.NavigateTo($"/workspaces/{WorkspaceId}/content/{item.Id}");

    private async Task RunTransitionAsync(Guid id, int versionNumber, string action)
    {
        try
        {
            switch (action)
            {
                case SubmitAction:
                    await Content.SubmitVersionAsync(WorkspaceId, id, versionNumber);
                    break;
                case ApproveAction:
                    await Content.ApproveVersionAsync(WorkspaceId, id, versionNumber);
                    break;
                case ArchiveAction:
                    await Content.ArchiveVersionAsync(WorkspaceId, id, versionNumber);
                    break;
                case RestoreAction:
                    await Content.RestoreVersionAsync(WorkspaceId, id, versionNumber);
                    break;
            }
            Toasts.Success("Content updated.");
        }
        catch (Exception ex)
        {
            Toasts.Danger(ex.Message);
        }
        finally
        {
            await ReloadListAsync();
        }
    }

    private async Task RunRejectAsync(Guid id, int versionNumber)
    {
        try
        {
            await Content.RejectVersionAsync(WorkspaceId, id, versionNumber, new RejectContentRequest("Rejected from content list"));
            Toasts.Success("Content updated.");
        }
        catch (Exception ex)
        {
            Toasts.Danger(ex.Message);
        }
        finally
        {
            await ReloadListAsync();
        }
    }

    private void OpenPublishDialog(ContentItemSummaryResponse item, int versionNumber, ContentStatus versionStatus)
    {
        publishContentId = item.Id;
        publishVersionNumber = versionNumber;
        publishVersionStatus = versionStatus;
    }

    private void ClosePublishDialog() => publishContentId = null;

    private async Task OnPublishedAsync(PublishContentVersionResponse response)
    {
        Toasts.Success(response.Warnings.Count == 0 ? "Content published." : "Content published with warnings.");
        await ReloadListAsync();
    }

    private Task ReloadListAsync() => listPanel is null ? Task.CompletedTask : listPanel.ReloadAsync();
}
```

### Step 4: `ContentVersions.razor` — renames + rollback becomes duplicate-into-Draft

- [ ] Replace every `ContentVersionStatus` with `ContentStatus` (lines 65, 126).
- [ ] Replace every `version.RetiredAt`/`inspected.RetiredAt` with `version.ArchivedAt`/`inspected.ArchivedAt` (lines 49-52, 104-107) — the property, structure, and null-guard are otherwise unchanged.
- [ ] Guard the two now-nullable `PublishedAt` displays (`LocalTimeDisplay.Value` requires non-nullable `DateTimeOffset`, confirmed by reading `src/Cmsify.Admin/Components/Shared/LocalTimeDisplay.razor:5`). Replace line 47:
```razor
                        <td><LocalTimeDisplay Value="@version.PublishedAt" /></td>
```
with:
```razor
                        <td>
                            @if (version.PublishedAt.HasValue)
                            {
                                <LocalTimeDisplay Value="@version.PublishedAt.Value" />
                            }
                            else
                            {
                                <span class="text-muted">—</span>
                            }
                        </td>
```
and replace line 103:
```razor
                        <dd class="col-sm-9"><LocalTimeDisplay Value="@inspected.PublishedAt" /></dd>
```
with:
```razor
                        <dd class="col-sm-9">
                            @if (inspected.PublishedAt.HasValue)
                            {
                                <LocalTimeDisplay Value="@inspected.PublishedAt.Value" />
                            }
                            else
                            {
                                <span class="text-muted">—</span>
                            }
                        </dd>
```
- [ ] Update the rollback confirmation copy. Replace:
```razor
<ConfirmDialog Visible="@confirmVersion.HasValue"
               Title="Roll back content"
               Message="@($"Replace the currently-active content with version {confirmVersion} and retire the active version? This creates a new published snapshot.")"
               ConfirmText="Roll back"
               ButtonClass="btn-primary"
               OnCancel="() => confirmVersion = null"
               OnConfirm="ConfirmRollbackAsync" />
```
with:
```razor
<ConfirmDialog Visible="@confirmVersion.HasValue"
               Title="Roll back content"
               Message="@($"Create a new Draft version cloned from version {confirmVersion}? You can review and publish it from the editor.")"
               ConfirmText="Roll back"
               ButtonClass="btn-primary"
               OnCancel="() => confirmVersion = null"
               OnConfirm="ConfirmRollbackAsync" />
```
- [ ] Replace `ConfirmRollbackAsync` in full:
```csharp
    private async Task ConfirmRollbackAsync()
    {
        if (!confirmVersion.HasValue)
        {
            return;
        }

        var target = confirmVersion.Value;
        confirmVersion = null;
        try
        {
            var created = await Content.CreateVersionAsync(WorkspaceId, ContentId, new CreateContentVersionRequest(null, null, target, null));
            if (created is not null)
            {
                Navigation.NavigateTo($"/workspaces/{WorkspaceId}/content/{ContentId}");
            }
        }
        catch (CmsifyApiException ex)
        {
            error = ex.Message;
        }
    }
```

### Step 5: `ContentEditor.razor` — resolve the editable version, auto-create a Draft when Published/Archived

- [ ] Replace the `@code` block in full:

```csharp
@code {
    private ContentClient Content => Cmsify.Content;
    private TemplateClient Templates => Cmsify.Templates;
    [Parameter] public Guid WorkspaceId { get; set; }
    [Parameter] public Guid? ContentId { get; set; }

    private const string SubmitAction = "submit";
    private const string ApproveAction = "approve";
    private const string ArchiveAction = "archive";
    private const string RestoreAction = "restore";
    private Guid? publishContentId;

    private UserRole CurrentRole => Enum.TryParse<UserRole>(Auth.User?.Role, ignoreCase: true, out var role) ? role : UserRole.Reader;

    private IReadOnlyList<TemplateSummaryResponse> templates = [];
    private ContentItemDetailResponse? item;
    private Guid? selectedTemplateId;
    private string? translationTarget;
    private int? editableVersionNumber;
    private Guid? resolvedForContentId;

    private ContentVersionSummaryResponse? EditingVersion =>
        item?.Versions.FirstOrDefault(v => v.VersionNumber == editableVersionNumber);

    protected override async Task OnParametersSetAsync()
    {
        if (!ContentId.HasValue)
        {
            templates = ApiResponse.ItemsOrEmpty(await Templates.ListAsync(WorkspaceId));
            return;
        }

        if (resolvedForContentId == ContentId)
        {
            return;
        }

        resolvedForContentId = ContentId;

        var loadedItem = await Content.GetAsync(WorkspaceId, ContentId.Value);
        if (loadedItem is null)
        {
            return;
        }

        item = loadedItem;

        var candidateVersionNumber = loadedItem.CurrentlyServingVersion?.VersionNumber
            ?? loadedItem.Versions.OrderByDescending(v => v.VersionNumber).FirstOrDefault()?.VersionNumber
            ?? 1;
        var candidateVersion = loadedItem.Versions.FirstOrDefault(v => v.VersionNumber == candidateVersionNumber);

        if (candidateVersion is { Status: ContentStatus.Published or ContentStatus.Archived })
        {
            var draft = await Content.CreateVersionAsync(WorkspaceId, ContentId.Value, new CreateContentVersionRequest(null, null, candidateVersionNumber, null));
            editableVersionNumber = draft?.VersionNumber ?? candidateVersionNumber;
            Toasts.Success($"Version {candidateVersionNumber} is {candidateVersion.Status}; opened a new Draft (version {editableVersionNumber}) to edit.");
            item = await Content.GetAsync(WorkspaceId, ContentId.Value);
        }
        else
        {
            editableVersionNumber = candidateVersionNumber;
        }
    }

    private void OnTemplateSelected(ChangeEventArgs e) =>
        selectedTemplateId = Guid.TryParse(e.Value?.ToString(), out var id) ? id : null;

    private async Task RefreshItemAsync()
    {
        if (ContentId.HasValue)
        {
            item = await Content.GetAsync(WorkspaceId, ContentId.Value);
        }
    }

    private async Task RunTransitionAsync(string action)
    {
        if (!ContentId.HasValue || editableVersionNumber is not { } versionNumber)
        {
            return;
        }

        try
        {
            var updated = action switch
            {
                SubmitAction => await Content.SubmitVersionAsync(WorkspaceId, ContentId.Value, versionNumber),
                ApproveAction => await Content.ApproveVersionAsync(WorkspaceId, ContentId.Value, versionNumber),
                ArchiveAction => await Content.ArchiveVersionAsync(WorkspaceId, ContentId.Value, versionNumber),
                RestoreAction => await Content.RestoreVersionAsync(WorkspaceId, ContentId.Value, versionNumber),
                _ => null
            };
            if (updated is not null)
            {
                await RefreshItemAsync();
                Toasts.Success("Content updated.");
            }
        }
        catch (Exception ex)
        {
            Toasts.Danger(ex.Message);
        }
    }

    private async Task RunRejectAsync()
    {
        if (!ContentId.HasValue || editableVersionNumber is not { } versionNumber)
        {
            return;
        }

        try
        {
            await Content.RejectVersionAsync(WorkspaceId, ContentId.Value, versionNumber, new RejectContentRequest("Rejected from content editor"));
            await RefreshItemAsync();
            Toasts.Success("Content updated.");
        }
        catch (Exception ex)
        {
            Toasts.Danger(ex.Message);
        }
    }

    private void OpenPublishDialog() => publishContentId = ContentId;

    private void ClosePublishDialog() => publishContentId = null;

    private async Task OnPublishedAsync(PublishContentVersionResponse response)
    {
        await RefreshItemAsync();
        Toasts.Success(response.Warnings.Count == 0 ? "Content published." : "Content published with warnings.");
    }

    private Task OnSavedAsync(ContentItemDetailResponse saved)
    {
        Toasts.Success("Draft saved");
        return Task.CompletedTask;
    }

    private Task OnCreatedAsync(ContentItemDetailResponse created)
    {
        Toasts.Success("Draft created");
        Navigation.NavigateTo($"/workspaces/{WorkspaceId}/content/{created.Id}");
        return Task.CompletedTask;
    }

    private async Task DuplicateAsync()
    {
        if (item is null || editableVersionNumber is not { } versionNumber)
        {
            return;
        }

        try
        {
            var sourceVersion = await Content.GetVersionAsync(WorkspaceId, item.Id, versionNumber);
            if (sourceVersion is null)
            {
                return;
            }

            var fields = sourceVersion.Fields
                .Select(f => new ContentFieldValueRequest(f.FieldId, f.Order, f.ValueKind, f.TextValue, f.BoolValue, f.MediaAssetId, f.FileAssetId, f.ChildContentItemId, f.JsonValue))
                .ToList();
            var created = await Content.CreateAsync(WorkspaceId, new CreateContentItemRequest(item.TemplateVersionId, null, item.LocaleCode, null, item.Tags, fields));
            if (created is not null)
            {
                Toasts.Success("Duplicated as new draft.");
                Navigation.NavigateTo($"/workspaces/{WorkspaceId}/content/{created.Id}");
            }
        }
        catch (Exception ex)
        {
            Toasts.Danger(ex.Message);
        }
    }

    private async Task LinkTranslationAsync()
    {
        if (ContentId.HasValue && Guid.TryParse(translationTarget, out var targetId))
        {
            try
            {
                await Content.LinkTranslationAsync(WorkspaceId, ContentId.Value, new LinkTranslationRequest(targetId));
            }
            catch (CmsifyApiException ex)
            {
                Toasts.Danger(ex.Message);
            }
        }
    }
}
```

- [ ] Replace the markup lines that reference `item.Status`/`item.PublishAt`/`item.PendingEffectiveStartAt`/`item.PendingEffectiveEndAt` and the `ContentEditPanel`/`PublishContentDialog` tags. Replace:
```razor
                <ContentEditPanel @key="@(ContentId ?? selectedTemplateId)" Client="@Cmsify" WorkspaceId="@WorkspaceId" ContentId="@ContentId"
                                  TemplateId="@(ContentId.HasValue ? null : selectedTemplateId)"
                                  SlugHelpText="@($"Optional. If supplied: {SlugRules.ValidationMessage}")"
                                  ItemChanged="@(i => item = i)" Saved="OnSavedAsync" Created="OnCreatedAsync" />
```
with:
```razor
                <ContentEditPanel @key="@(ContentId, editableVersionNumber)" Client="@Cmsify" WorkspaceId="@WorkspaceId" ContentId="@ContentId"
                                  TemplateId="@(ContentId.HasValue ? null : selectedTemplateId)"
                                  VersionNumber="@editableVersionNumber"
                                  SlugHelpText="@($"Optional. If supplied: {SlugRules.ValidationMessage}")"
                                  ItemChanged="@(i => item = i)" Saved="OnSavedAsync" Created="OnCreatedAsync" />
```
and its enclosing guard (a few lines above):
```razor
            @if (ContentId.HasValue || selectedTemplateId.HasValue)
```
with:
```razor
            @if (selectedTemplateId.HasValue || (ContentId.HasValue && editableVersionNumber.HasValue))
```

Replace the lifecycle block:
```razor
            @if (item is not null)
            {
                <StatusBadge Status="@item.Status" />
                var submitState = ContentWorkflowActions.SubmitState(item.Status);
                var approveState = ContentWorkflowActions.ApproveState(item.Status);
                var rejectState = ContentWorkflowActions.RejectState(item.Status, CurrentRole);
                var publishState = ContentWorkflowActions.PublishState(item.Status, CurrentRole);
                var archiveState = ContentWorkflowActions.ArchiveState(item.Status);
                var restoreState = ContentWorkflowActions.RestoreState(item.Status, CurrentRole);
                <div class="d-flex flex-wrap gap-1 mt-2">
                    <button class="btn btn-outline-secondary btn-sm" disabled="@(!submitState.Enabled)" title="@submitState.DisabledReason" @onclick="() => RunTransitionAsync(SubmitAction)">Submit</button>
                    <button class="btn btn-outline-secondary btn-sm" disabled="@(!approveState.Enabled)" title="@approveState.DisabledReason" @onclick="() => RunTransitionAsync(ApproveAction)">Approve</button>
                    <button class="btn btn-outline-warning btn-sm" disabled="@(!rejectState.Enabled)" title="@rejectState.DisabledReason" @onclick="RunRejectAsync">Reject</button>
                    <button class="btn btn-outline-secondary btn-sm" disabled="@(!publishState.Enabled)" title="@publishState.DisabledReason" @onclick="OpenPublishDialog">Publish</button>
                    <button class="btn btn-outline-secondary btn-sm" disabled="@(!archiveState.Enabled)" title="@archiveState.DisabledReason" @onclick="() => RunTransitionAsync(ArchiveAction)">Archive</button>
                    <button class="btn btn-outline-secondary btn-sm" disabled="@(!restoreState.Enabled)" title="@restoreState.DisabledReason" @onclick="() => RunTransitionAsync(RestoreAction)">Restore</button>
                </div>
                @if (item.PublishAt.HasValue)
                {
                    <div class="small text-muted mt-2">
                        Scheduled to publish: <LocalTimeDisplay Value="@item.PublishAt.Value" />
                        @if (item.PendingEffectiveStartAt.HasValue || item.PendingEffectiveEndAt.HasValue)
                        {
                            <div>
                                Effective window:
                                @if (item.PendingEffectiveStartAt.HasValue) { <LocalTimeDisplay Value="@item.PendingEffectiveStartAt.Value" /> } else { <text>(open start)</text> }
                                –
                                @if (item.PendingEffectiveEndAt.HasValue) { <LocalTimeDisplay Value="@item.PendingEffectiveEndAt.Value" /> } else { <text>(open end)</text> }
                            </div>
                        }
                    </div>
                }
            }
```
with:
```razor
            @if (item is not null && EditingVersion is not null)
            {
                <StatusBadge Status="@EditingVersion.Status" />
                var submitState = ContentWorkflowActions.SubmitState(EditingVersion.Status);
                var approveState = ContentWorkflowActions.ApproveState(EditingVersion.Status);
                var rejectState = ContentWorkflowActions.RejectState(EditingVersion.Status, CurrentRole);
                var publishState = ContentWorkflowActions.PublishState(EditingVersion.Status, CurrentRole);
                var archiveState = ContentWorkflowActions.ArchiveState(EditingVersion.Status);
                var restoreState = ContentWorkflowActions.RestoreState(EditingVersion.Status, CurrentRole);
                <div class="d-flex flex-wrap gap-1 mt-2">
                    <button class="btn btn-outline-secondary btn-sm" disabled="@(!submitState.Enabled)" title="@submitState.DisabledReason" @onclick="() => RunTransitionAsync(SubmitAction)">Submit</button>
                    <button class="btn btn-outline-secondary btn-sm" disabled="@(!approveState.Enabled)" title="@approveState.DisabledReason" @onclick="() => RunTransitionAsync(ApproveAction)">Approve</button>
                    <button class="btn btn-outline-warning btn-sm" disabled="@(!rejectState.Enabled)" title="@rejectState.DisabledReason" @onclick="RunRejectAsync">Reject</button>
                    <button class="btn btn-outline-secondary btn-sm" disabled="@(!publishState.Enabled)" title="@publishState.DisabledReason" @onclick="OpenPublishDialog">Publish</button>
                    <button class="btn btn-outline-secondary btn-sm" disabled="@(!archiveState.Enabled)" title="@archiveState.DisabledReason" @onclick="() => RunTransitionAsync(ArchiveAction)">Archive</button>
                    <button class="btn btn-outline-secondary btn-sm" disabled="@(!restoreState.Enabled)" title="@restoreState.DisabledReason" @onclick="() => RunTransitionAsync(RestoreAction)">Restore</button>
                </div>
                @if (EditingVersion.PublishAt.HasValue)
                {
                    <div class="small text-muted mt-2">
                        Scheduled to publish: <LocalTimeDisplay Value="@EditingVersion.PublishAt.Value" />
                    </div>
                }
            }
```

Replace the `PublishContentDialog` tag:
```razor
    <PublishContentDialog WorkspaceId="@WorkspaceId" ContentId="@publishContentId" ContentStatus="@item.Status"
                          CanOverrideWorkflow="@(CurrentRole >= UserRole.Admin)" Cmsify="@Cmsify"
                          OnClosed="ClosePublishDialog" OnPublished="OnPublishedAsync" />
```
with:
```razor
    <PublishContentDialog WorkspaceId="@WorkspaceId" ContentId="@publishContentId" VersionNumber="@editableVersionNumber" VersionStatus="@(EditingVersion?.Status ?? ContentStatus.Draft)"
                          CanOverrideWorkflow="@(CurrentRole >= UserRole.Admin)" Cmsify="@Cmsify"
                          OnClosed="ClosePublishDialog" OnPublished="OnPublishedAsync" />
```
(and its `@if (item is not null)` guard above stays as-is — `EditingVersion` may still be null the instant `item` loads before `editableVersionNumber` resolves, which the `?? ContentStatus.Draft` fallback and the `PublishContentDialog`'s own `VersionNumber.HasValue` guard both handle safely.)

- [ ] **Step 6: Build the whole Admin project**

Run: `dotnet build src/Cmsify.Admin/Cmsify.Admin.csproj`
Expected: 0 errors.

- [ ] **Step 7: Build the whole solution/repo to confirm no masked failures remain anywhere**

Run: `dotnet build` from the repo root (or build each `.csproj` under `src/`, `sdk/`, `tests/` individually if there is no top-level `.sln`, per the note in the scoping investigation that no `.sln` exists).
Expected: 0 errors across every project.

- [ ] **Step 8: Run the full test suite**

Run each test project: `Cmsify.Core.Tests`, `Cmsify.Infrastructure.Tests`, `Cmsify.Api.Integration.Tests`, `Cmsify.Components.Tests`, `SyntaxCircus.Cmsify.Client.Tests`.
Expected: all green.

- [ ] **Step 9: Commit**

```bash
git add src/Cmsify.Admin/Components/Pages/Content/ContentEditor.razor src/Cmsify.Admin/Components/Pages/Content/ContentList.razor src/Cmsify.Admin/Components/Pages/Content/ContentVersions.razor src/Cmsify.Admin/Components/Shared/PublishContentDialog.razor src/Cmsify.Admin/Components/Pages/Workspaces/WorkspaceDetail.razor
git commit -m "fix(admin): restore Content pages against version-scoped workflow API"
```

---

## Final Verification (done directly in this session, not delegated)

After Task 4 is reviewed and merged in-place:

1. Stand up a fresh throwaway Postgres + `dotnet run --project src/Cmsify.Api` + `dotnet run --project src/Cmsify.Admin` (same pattern as the earlier Chrome API-only validation pass — a separate throwaway container, not the user's real dev Postgres).
2. Use Claude-in-Chrome to: log into Admin, create a workspace/template/content item, edit the default version's fields and save, run it through Submit → Approve → Publish, confirm status badges update in both the list and editor, then click Edit again on the now-Published item and confirm a new Draft version is auto-created rather than a 409, exercise Rollback from the Version History page and confirm it opens a new Draft, and reproduce the Task 1 bug scenario (either through the Templates UI if reachable, or directly via the API as already done) to confirm it now 422s at field-creation time.
3. Tear down the throwaway environment.
4. Report results to the user for their own review pass.
