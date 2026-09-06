# Reusable Blazor Content-Editing Components Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship `SyntaxCircus.Cmsify.Components` (headless Blazor field editors + composed content-edit/list screens) and a companion opt-in `SyntaxCircus.Cmsify.Components.Theme` package, then migrate Cmsify.Admin to consume them in place of its hand-rolled `RenderField` switch.

**Architecture:** Presentational field-editor components (one per `PrimitiveType`, plus Component/Reference composition editors) are composed by a `FieldEditor` dispatcher; a presentational `ContentEditForm`/`ContentListView` pair is wrapped by SDK-backed `Client.ContentEditPanel`/`Client.ContentListPanel` smart components that call `SyntaxCircus.Cmsify.Client`. Structural CSS ships via CSS isolation; all visual styling is driven by `--cmsify-*` custom properties, with defaults supplied by the separate Theme package.

**Tech Stack:** .NET 10 / Blazor Server (Interactive Server render mode), Razor Class Library, xunit.v3 + Shouldly + NSubstitute + bunit for tests.

**Spec:** `docs/superpowers/specs/2026-09-05-cmsify-components-design.md`

## Global Constraints

- Target framework: `net10.0` everywhere (matches `global.json`, all existing projects).
- New packages follow the exact `Cmsify.Contracts.csproj` packable-project template: `PackageId`, `Title`, `Description`, `Authors`, `Company`, `Copyright`, `PackageTags`, `PackageLicenseExpression=MIT`, `PackageReadmeFile=README.md`, `PublishRepositoryUrl=true`, `IncludeSymbols=true`, `SymbolPackageFormat=snupkg`. `IsPackable` stays governed by the repo-wide `Directory.Build.props` gate (`CmsifyReleaseBuild` flag) — do not override it per-project.
- `SyntaxCircus.Cmsify.Components` references `Cmsify.Contracts` and `SyntaxCircus.Cmsify.Client` via `ProjectReference` only (mirrors `SyntaxCircus.Cmsify.Client.csproj`'s own reference to `Cmsify.Contracts`) — never `Cmsify.Core` (that namespace's `PrimitiveType`/enums are server/domain-only; the wire-facing `SyntaxCircus.Cmsify.Contracts` enums are the ones `TemplateFieldResponse` actually uses).
- Every `.razor` component in the new package is presentational unless it lives under a `Client/` folder (namespace `SyntaxCircus.Cmsify.Components.Client`) — smart components take a `CmsifyClient` as an explicit `[Parameter]`, never via `@inject`, so the package has no DI-container assumptions and stays trivially testable in bUnit.
- Visual styling is CSS custom properties only (`--cmsify-*`), set in each component's `.razor.css`; never hard-code a color, border, or radius value directly in a component's CSS — always reference a `--cmsify-*` variable with a sensible fallback (e.g. `var(--cmsify-color-border, #ced4da)`).
- Standard `--cmsify-*` token vocabulary used throughout (documented in the package README, task 1): `--cmsify-color-text`, `--cmsify-color-border`, `--cmsify-color-border-focus`, `--cmsify-color-danger`, `--cmsify-color-muted`, `--cmsify-radius`, `--cmsify-spacing-sm`, `--cmsify-spacing-md`, `--cmsify-font-family`, `--cmsify-focus-ring`.
- Tests use `xunit.v3`, `Shouldly`, and `bunit` (Blazor component tests) — matches `tests/Cmsify.Core.Tests` conventions plus `bunit` for rendering. No `NSubstitute` needed for HTTP-calling smart components; use a real `HttpClient` backed by a fake `HttpMessageHandler` (task 1 provides `FakeHttpMessageHandler`), since `CmsifyClient` is a concrete sealed class, not mockable via an interface.
- Follow strict RED-GREEN-REFACTOR: every task writes a failing test first, confirms the failure, then implements.
- Commit after every task using `git add` scoped to the exact files touched (never `git add -A`).
- Scope cut (see spec "Out of Scope"): workflow/lifecycle actions (submit/approve/reject/publish/archive/restore/translation-linking, `StatusBadge`, `PublishContentDialog`, `ContentWorkflowActions`) stay in `Cmsify.Admin` unchanged — the package only extracts field editing, the edit form, and the content list/filter table.

---

## Task 1: Scaffold `Cmsify.Components` and its test project

**Files:**
- Create: `src/Cmsify.Components/Cmsify.Components.csproj`
- Create: `src/Cmsify.Components/_Imports.razor`
- Create: `src/Cmsify.Components/GlobalUsings.cs`
- Create: `src/Cmsify.Components/README.md`
- Create: `tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj`
- Create: `tests/Cmsify.Components.Tests/GlobalUsings.cs`
- Create: `tests/Cmsify.Components.Tests/SmokeTests.cs`
- Modify: `Cmsify.slnx`
- Modify: `Directory.Packages.props`

**Interfaces:**
- Produces: the `SyntaxCircus.Cmsify.Components` project (empty except smoke test) that every later task adds files to; the `bunit` central package version later tasks' test projects rely on.

- [ ] **Step 1: Add `bunit` to central package versions**

Edit `Directory.Packages.props`, adding this line alphabetically among the existing `<PackageVersion>` entries (after `AWSSDK.S3`, before `BCrypt.Net-Next`):

```xml
    <PackageVersion Include="bunit" Version="2.9.0" />
```

- [ ] **Step 2: Create the `Cmsify.Components` project file**

```xml
<Project Sdk="Microsoft.NET.Sdk.Razor">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <PackageId>SyntaxCircus.Cmsify.Components</PackageId>
    <Title>Syntax Circus LLC Cmsify Components</Title>
    <Description>Reusable, restylable Blazor components for editing and managing Cmsify content: field editors, a composed content edit form, and a content list view, with optional SDK-backed smart wrappers.</Description>
    <Authors>Syntax Circus LLC</Authors>
    <Company>Syntax Circus LLC</Company>
    <Copyright>Copyright © Syntax Circus LLC</Copyright>
    <PackageTags>cms;headless-cms;cmsify;content-management;blazor;components</PackageTags>
    <PackageRequireLicenseAcceptance>false</PackageRequireLicenseAcceptance>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
    <PackageReadmeFile>README.md</PackageReadmeFile>
    <PackageProjectUrl>https://github.com/Syntax-Circus/cmsify</PackageProjectUrl>
    <RepositoryUrl>https://github.com/Syntax-Circus/cmsify</RepositoryUrl>
    <RepositoryType>git</RepositoryType>
    <NoWarn>$(NoWarn);1591</NoWarn>
    <PublishRepositoryUrl>true</PublishRepositoryUrl>
    <IncludeSymbols>true</IncludeSymbols>
    <SymbolPackageFormat>snupkg</SymbolPackageFormat>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Cmsify.Contracts\Cmsify.Contracts.csproj" />
    <ProjectReference Include="..\..\sdk\dotnet\src\SyntaxCircus.Cmsify.Client\SyntaxCircus.Cmsify.Client.csproj" />
  </ItemGroup>
  <ItemGroup>
    <None Include="README.md" Pack="true" PackagePath="\" />
    <None Include="..\..\LICENSE-MIT.txt" Pack="true" PackagePath="\" Link="LICENSE-MIT.txt" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Create `_Imports.razor`**

`_Imports.razor` usings apply only to `.razor` file compilation, not plain `.cs` files — Step 3a below adds the equivalent for `.cs` files in this project.

```razor
@using Microsoft.AspNetCore.Components
@using Microsoft.AspNetCore.Components.Forms
@using Microsoft.AspNetCore.Components.Web
@using SyntaxCircus.Cmsify
@using SyntaxCircus.Cmsify.Contracts
```

- [ ] **Step 3a: Create `GlobalUsings.cs` for the main project's plain `.cs` files**

Task 2 adds `.cs` files (not `.razor`) that need `TemplateFieldResponse`/`MediaAssetResponse` (from `SyntaxCircus.Cmsify.Contracts`) and `EventCallback<T>` (from `Microsoft.AspNetCore.Components`) without repeating `using` in every file:

```csharp
global using Microsoft.AspNetCore.Components;
global using SyntaxCircus.Cmsify.Contracts;
```

- [ ] **Step 3b: Create `GlobalUsings.cs` for the test project**

Every test file from Task 3 onward references types from `SyntaxCircus.Cmsify.Components` (the component library under test) and `SyntaxCircus.Cmsify.Contracts` (wire DTOs) unqualified; Client-namespace tests (Tasks 11, 13, 15, 17, 19) additionally reference `CmsifyClient`/`CmsifyClientOptions`/`CmsifyApiException` from `SyntaxCircus.Cmsify`. One global-usings file up front avoids repeating these in every test file:

```csharp
global using SyntaxCircus.Cmsify;
global using SyntaxCircus.Cmsify.Components;
global using SyntaxCircus.Cmsify.Contracts;
```

Create this at `tests/Cmsify.Components.Tests/GlobalUsings.cs`.

- [ ] **Step 4: Create the package README with the token vocabulary**

```markdown
# SyntaxCircus.Cmsify.Components

Reusable Blazor Server components for editing and managing Cmsify content: field editors for every content-model primitive type, a composed content edit form, and a content list view. Presentational components take data via parameters and raise events; SDK-backed "smart" wrappers under the `SyntaxCircus.Cmsify.Components.Client` namespace additionally accept a `CmsifyClient` and handle loading/saving.

## Styling

Components ship with structural CSS only (via CSS isolation). Every visual property is a CSS custom property you can override at any scope:

| Variable | Purpose |
| --- | --- |
| `--cmsify-color-text` | Text color |
| `--cmsify-color-border` | Default border color |
| `--cmsify-color-border-focus` | Border color on focus |
| `--cmsify-color-danger` | Error/warning text and borders |
| `--cmsify-color-muted` | Secondary/help text |
| `--cmsify-radius` | Corner radius for inputs, buttons, cards |
| `--cmsify-spacing-sm` | Small padding/gap |
| `--cmsify-spacing-md` | Medium padding/gap |
| `--cmsify-font-family` | Font family |
| `--cmsify-focus-ring` | Focus box-shadow |

Install `SyntaxCircus.Cmsify.Components.Theme` for a ready-made default look, or set these variables yourself to match your own design system.
```

- [ ] **Step 5: Create the test project**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <OutputType>Exe</OutputType>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Cmsify.Components\Cmsify.Components.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="bunit" />
    <PackageReference Include="coverlet.collector">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="Shouldly" />
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
  </ItemGroup>
</Project>
```

- [ ] **Step 6: Write a failing smoke test**

```csharp
using Shouldly;
using Xunit;

namespace SyntaxCircus.Cmsify.Components.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void ProjectReferenceResolves()
    {
        typeof(ContentFieldEditorValue).Assembly.GetName().Name.ShouldBe("Cmsify.Components");
    }
}
```

- [ ] **Step 7: Run test to verify it fails**

Run: `dotnet test tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj`
Expected: FAIL to build — `ContentFieldEditorValue` does not exist yet.

- [ ] **Step 8: Add both projects to the solution**

Edit `Cmsify.slnx`, adding after the `Cmsify.Contracts` project line:

```xml
  <Project Path="src\Cmsify.Components\Cmsify.Components.csproj" />
```

and after the `tests\Cmsify.Infrastructure.Tests` project line:

```xml
  <Project Path="tests\Cmsify.Components.Tests\Cmsify.Components.Tests.csproj" />
```

- [ ] **Step 9: Add a placeholder type so the smoke test can pass structurally (temporary — replaced by Task 2's real implementation)**

Skip this step — do not add a placeholder type. Instead proceed directly to Task 2, whose Step 1 creates the real `ContentFieldEditorValue` class that makes this smoke test pass. Leave the smoke test failing at the end of Task 1; this is expected and documented here so the next task's "run to pass" step covers both.

- [ ] **Step 10: Commit**

```bash
git add src/Cmsify.Components/Cmsify.Components.csproj src/Cmsify.Components/_Imports.razor src/Cmsify.Components/GlobalUsings.cs src/Cmsify.Components/README.md tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj tests/Cmsify.Components.Tests/GlobalUsings.cs tests/Cmsify.Components.Tests/SmokeTests.cs Cmsify.slnx Directory.Packages.props
git commit -m "$(cat <<'EOF'
Scaffold SyntaxCircus.Cmsify.Components project and test project

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3Smg2MFcrDKPuq2WYQEYU
EOF
)"
```

---

## Task 2: Shared value model (`ContentFieldEditorValue`, `FieldEditorRenderContext`, `PickListFieldBinding`)

**Files:**
- Create: `src/Cmsify.Components/ContentFieldEditorValue.cs`
- Create: `src/Cmsify.Components/FieldEditorRenderContext.cs`
- Create: `src/Cmsify.Components/PickListFieldBinding.cs`
- Test: `tests/Cmsify.Components.Tests/PickListFieldBindingTests.cs`

**Interfaces:**
- Consumes: nothing (pure model types).
- Produces: `ContentFieldEditorValue` (mutable value bag: `TextValue`, `BoolValue`, `ChildContentItemId`, `SelectedMediaAsset`, `SelectedFileAsset`, `MultiValues`, `ComponentValues`) and `PickListFieldBinding.FromFieldConfig(JsonElement?) -> PickListFieldBinding { PickListId, RevisionId, Multiple }`, both used by every field editor and the dispatcher from Task 3 onward.

- [ ] **Step 1: Create `ContentFieldEditorValue`**

```csharp
namespace SyntaxCircus.Cmsify.Components;

public sealed class ContentFieldEditorValue
{
    public string? TextValue { get; set; }
    public bool BoolValue { get; set; }
    public Guid? ChildContentItemId { get; set; }
    public MediaAssetResponse? SelectedMediaAsset { get; set; }
    public MediaAssetResponse? SelectedFileAsset { get; set; }
    public IReadOnlyList<string> MultiValues { get; set; } = [];
    public IReadOnlyList<string> ComponentValues { get; set; } = [];
}
```

This makes `SmokeTests.ProjectReferenceResolves` (Task 1) compile and pass.

- [ ] **Step 2: Create `FieldEditorRenderContext`**

```csharp
namespace SyntaxCircus.Cmsify.Components;

public sealed record FieldEditorRenderContext(
    TemplateFieldResponse Field,
    ContentFieldEditorValue Value,
    EventCallback<ContentFieldEditorValue> ValueChanged);
```

- [ ] **Step 3: Write the failing test for `PickListFieldBinding`**

```csharp
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
```

- [ ] **Step 4: Run test to verify it fails**

Run: `dotnet test tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj`
Expected: FAIL to build — `PickListFieldBinding` does not exist yet.

- [ ] **Step 5: Implement `PickListFieldBinding`**

```csharp
using System.Text.Json;

namespace SyntaxCircus.Cmsify.Components;

public sealed record PickListFieldBinding(Guid? PickListId, Guid? RevisionId, bool Multiple)
{
    public static PickListFieldBinding FromFieldConfig(JsonElement? fieldConfig)
    {
        if (fieldConfig is not { ValueKind: JsonValueKind.Object } config)
        {
            return new PickListFieldBinding(null, null, false);
        }

        Guid? pickListId = null;
        Guid? revisionId = null;
        var multiple = false;

        if (config.TryGetProperty("picklistId", out var pickProp) &&
            pickProp.ValueKind == JsonValueKind.String &&
            Guid.TryParse(pickProp.GetString(), out var parsedPickList))
        {
            pickListId = parsedPickList;
        }

        if (config.TryGetProperty("picklistRevisionId", out var revisionProp) &&
            revisionProp.ValueKind == JsonValueKind.String &&
            Guid.TryParse(revisionProp.GetString(), out var parsedRevision))
        {
            revisionId = parsedRevision;
        }

        if (config.TryGetProperty("multiple", out var multiProp) &&
            multiProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            multiple = multiProp.GetBoolean();
        }

        return new PickListFieldBinding(pickListId, revisionId, multiple);
    }
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj`
Expected: PASS (4 tests: the Task 1 smoke test plus the 3 binding tests).

- [ ] **Step 7: Commit**

```bash
git add src/Cmsify.Components/ContentFieldEditorValue.cs src/Cmsify.Components/FieldEditorRenderContext.cs src/Cmsify.Components/PickListFieldBinding.cs tests/Cmsify.Components.Tests/PickListFieldBindingTests.cs
git commit -m "$(cat <<'EOF'
Add shared field-editor value model and pick-list binding parser

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3Smg2MFcrDKPuq2WYQEYU
EOF
)"
```

---

## Task 3: Simple single-value field editors (Text, Link, Quote, Separator, Boolean)

**Files:**
- Create: `src/Cmsify.Components/FieldEditors/TextFieldEditor.razor` + `.razor.css`
- Create: `src/Cmsify.Components/FieldEditors/LinkFieldEditor.razor` + `.razor.css`
- Create: `src/Cmsify.Components/FieldEditors/QuoteFieldEditor.razor` + `.razor.css`
- Create: `src/Cmsify.Components/FieldEditors/SeparatorFieldEditor.razor` + `.razor.css`
- Create: `src/Cmsify.Components/FieldEditors/BooleanFieldEditor.razor` + `.razor.css`
- Create: `tests/Cmsify.Components.Tests/FieldEditors/TextFieldEditorTests.cs`
- Create: `tests/Cmsify.Components.Tests/FieldEditors/LinkFieldEditorTests.cs`
- Create: `tests/Cmsify.Components.Tests/FieldEditors/QuoteFieldEditorTests.cs`
- Create: `tests/Cmsify.Components.Tests/FieldEditors/SeparatorFieldEditorTests.cs`
- Create: `tests/Cmsify.Components.Tests/FieldEditors/BooleanFieldEditorTests.cs`

**Interfaces:**
- Consumes: nothing beyond framework types.
- Produces: `TextFieldEditor { [Parameter] string? Value; [Parameter] EventCallback<string?> ValueChanged; }`, `LinkFieldEditor` (same shape), `QuoteFieldEditor` (same shape), `SeparatorFieldEditor` (no parameters), `BooleanFieldEditor { [Parameter] bool Value; [Parameter] EventCallback<bool> ValueChanged; }` — all consumed by the `FieldEditor` dispatcher in Task 9.

- [ ] **Step 1: Write the failing test for `TextFieldEditor`**

```csharp
using Bunit;
using Microsoft.AspNetCore.Components;
using Shouldly;
using Xunit;

namespace SyntaxCircus.Cmsify.Components.Tests.FieldEditors;

public sealed class TextFieldEditorTests : TestContext
{
    [Fact]
    public void RendersValueAndRaisesValueChangedOnInput()
    {
        string? changed = null;
        var cut = RenderComponent<TextFieldEditor>(parameters => parameters
            .Add(p => p.Value, "hello")
            .Add(p => p.ValueChanged, EventCallback.Factory.Create<string?>(this, v => changed = v)));

        cut.Find("input").GetAttribute("value").ShouldBe("hello");

        cut.Find("input").Input("updated");

        changed.ShouldBe("updated");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj --filter TextFieldEditorTests`
Expected: FAIL to build — `TextFieldEditor` does not exist yet.

- [ ] **Step 3: Implement `TextFieldEditor.razor`**

```razor
@namespace SyntaxCircus.Cmsify.Components

<input type="text" class="cmsify-field-input" value="@Value" @oninput="OnInput" />

@code {
    [Parameter] public string? Value { get; set; }
    [Parameter] public EventCallback<string?> ValueChanged { get; set; }

    private Task OnInput(ChangeEventArgs e) => ValueChanged.InvokeAsync(e.Value?.ToString());
}
```

- [ ] **Step 4: Implement `TextFieldEditor.razor.css`**

```css
.cmsify-field-input {
    display: block;
    width: 100%;
    box-sizing: border-box;
    padding: var(--cmsify-spacing-sm, 0.375rem) var(--cmsify-spacing-md, 0.75rem);
    border: 1px solid var(--cmsify-color-border, #ced4da);
    border-radius: var(--cmsify-radius, 0.375rem);
    font-family: var(--cmsify-font-family, inherit);
    color: var(--cmsify-color-text, inherit);
}

.cmsify-field-input:focus {
    outline: none;
    border-color: var(--cmsify-color-border-focus, #86b7fe);
    box-shadow: var(--cmsify-focus-ring, 0 0 0 0.2rem rgba(13, 110, 253, 0.25));
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj --filter TextFieldEditorTests`
Expected: PASS

- [ ] **Step 6: Write the failing test for `LinkFieldEditor`**

```csharp
using Bunit;
using Microsoft.AspNetCore.Components;
using Shouldly;
using Xunit;

namespace SyntaxCircus.Cmsify.Components.Tests.FieldEditors;

public sealed class LinkFieldEditorTests : TestContext
{
    [Fact]
    public void RendersAsUrlInputWithPlaceholderAndRaisesValueChanged()
    {
        string? changed = null;
        var cut = RenderComponent<LinkFieldEditor>(parameters => parameters
            .Add(p => p.Value, "https://example.com")
            .Add(p => p.ValueChanged, EventCallback.Factory.Create<string?>(this, v => changed = v)));

        var input = cut.Find("input");
        input.GetAttribute("type").ShouldBe("url");
        input.GetAttribute("placeholder").ShouldBe("https://example.com");
        input.GetAttribute("value").ShouldBe("https://example.com");

        input.Input("https://updated.example.com");

        changed.ShouldBe("https://updated.example.com");
    }
}
```

- [ ] **Step 7: Run test to verify it fails**

Run: `dotnet test tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj --filter LinkFieldEditorTests`
Expected: FAIL to build — `LinkFieldEditor` does not exist yet.

- [ ] **Step 8: Implement `LinkFieldEditor.razor`**

```razor
@namespace SyntaxCircus.Cmsify.Components

<input type="url" class="cmsify-field-input" placeholder="https://example.com" value="@Value" @oninput="OnInput" />

@code {
    [Parameter] public string? Value { get; set; }
    [Parameter] public EventCallback<string?> ValueChanged { get; set; }

    private Task OnInput(ChangeEventArgs e) => ValueChanged.InvokeAsync(e.Value?.ToString());
}
```

- [ ] **Step 9: Implement `LinkFieldEditor.razor.css`**

```css
.cmsify-field-input {
    display: block;
    width: 100%;
    box-sizing: border-box;
    padding: var(--cmsify-spacing-sm, 0.375rem) var(--cmsify-spacing-md, 0.75rem);
    border: 1px solid var(--cmsify-color-border, #ced4da);
    border-radius: var(--cmsify-radius, 0.375rem);
    font-family: var(--cmsify-font-family, inherit);
    color: var(--cmsify-color-text, inherit);
}

.cmsify-field-input:focus {
    outline: none;
    border-color: var(--cmsify-color-border-focus, #86b7fe);
    box-shadow: var(--cmsify-focus-ring, 0 0 0 0.2rem rgba(13, 110, 253, 0.25));
}
```

- [ ] **Step 10: Run test to verify it passes**

Run: `dotnet test tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj --filter LinkFieldEditorTests`
Expected: PASS

- [ ] **Step 11: Write the failing test for `QuoteFieldEditor`**

```csharp
using Bunit;
using Microsoft.AspNetCore.Components;
using Shouldly;
using Xunit;

namespace SyntaxCircus.Cmsify.Components.Tests.FieldEditors;

public sealed class QuoteFieldEditorTests : TestContext
{
    [Fact]
    public void RendersAsFourRowTextareaAndRaisesValueChanged()
    {
        string? changed = null;
        var cut = RenderComponent<QuoteFieldEditor>(parameters => parameters
            .Add(p => p.Value, "a quote")
            .Add(p => p.ValueChanged, EventCallback.Factory.Create<string?>(this, v => changed = v)));

        var textarea = cut.Find("textarea");
        textarea.GetAttribute("rows").ShouldBe("4");
        textarea.TextContent.ShouldBe("a quote");

        textarea.Input("an updated quote");

        changed.ShouldBe("an updated quote");
    }
}
```

- [ ] **Step 12: Run test to verify it fails**

Run: `dotnet test tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj --filter QuoteFieldEditorTests`
Expected: FAIL to build — `QuoteFieldEditor` does not exist yet.

- [ ] **Step 13: Implement `QuoteFieldEditor.razor`**

```razor
@namespace SyntaxCircus.Cmsify.Components

<textarea class="cmsify-field-textarea" rows="4" @oninput="OnInput">@Value</textarea>

@code {
    [Parameter] public string? Value { get; set; }
    [Parameter] public EventCallback<string?> ValueChanged { get; set; }

    private Task OnInput(ChangeEventArgs e) => ValueChanged.InvokeAsync(e.Value?.ToString());
}
```

- [ ] **Step 14: Implement `QuoteFieldEditor.razor.css`**

```css
.cmsify-field-textarea {
    display: block;
    width: 100%;
    box-sizing: border-box;
    padding: var(--cmsify-spacing-sm, 0.375rem) var(--cmsify-spacing-md, 0.75rem);
    border: 1px solid var(--cmsify-color-border, #ced4da);
    border-radius: var(--cmsify-radius, 0.375rem);
    font-family: var(--cmsify-font-family, inherit);
    color: var(--cmsify-color-text, inherit);
    resize: vertical;
}

.cmsify-field-textarea:focus {
    outline: none;
    border-color: var(--cmsify-color-border-focus, #86b7fe);
    box-shadow: var(--cmsify-focus-ring, 0 0 0 0.2rem rgba(13, 110, 253, 0.25));
}
```

- [ ] **Step 15: Run test to verify it passes**

Run: `dotnet test tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj --filter QuoteFieldEditorTests`
Expected: PASS

- [ ] **Step 16: Write the failing test for `SeparatorFieldEditor`**

```csharp
using Bunit;
using Shouldly;
using Xunit;

namespace SyntaxCircus.Cmsify.Components.Tests.FieldEditors;

public sealed class SeparatorFieldEditorTests : TestContext
{
    [Fact]
    public void RendersAHorizontalRule()
    {
        var cut = RenderComponent<SeparatorFieldEditor>();

        cut.Find("hr").ClassList.ShouldContain("cmsify-field-separator");
    }
}
```

- [ ] **Step 17: Run test to verify it fails**

Run: `dotnet test tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj --filter SeparatorFieldEditorTests`
Expected: FAIL to build — `SeparatorFieldEditor` does not exist yet.

- [ ] **Step 18: Implement `SeparatorFieldEditor.razor`**

```razor
@namespace SyntaxCircus.Cmsify.Components

<hr class="cmsify-field-separator" />
```

- [ ] **Step 19: Implement `SeparatorFieldEditor.razor.css`**

```css
.cmsify-field-separator {
    border: none;
    border-top: 1px solid var(--cmsify-color-border, #ced4da);
    margin: var(--cmsify-spacing-md, 0.75rem) 0;
}
```

- [ ] **Step 20: Run test to verify it passes**

Run: `dotnet test tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj --filter SeparatorFieldEditorTests`
Expected: PASS

- [ ] **Step 21: Write the failing test for `BooleanFieldEditor`**

```csharp
using Bunit;
using Microsoft.AspNetCore.Components;
using Shouldly;
using Xunit;

namespace SyntaxCircus.Cmsify.Components.Tests.FieldEditors;

public sealed class BooleanFieldEditorTests : TestContext
{
    [Fact]
    public void RendersCheckboxStateAndRaisesValueChanged()
    {
        bool? changed = null;
        var cut = RenderComponent<BooleanFieldEditor>(parameters => parameters
            .Add(p => p.Value, false)
            .Add(p => p.ValueChanged, EventCallback.Factory.Create<bool>(this, v => changed = v)));

        var checkbox = cut.Find("input");
        checkbox.GetAttribute("type").ShouldBe("checkbox");
        ((bool)checkbox.HasAttribute("checked")).ShouldBeFalse();

        checkbox.Change(true);

        changed.ShouldBe(true);
    }
}
```

- [ ] **Step 22: Run test to verify it fails**

Run: `dotnet test tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj --filter BooleanFieldEditorTests`
Expected: FAIL to build — `BooleanFieldEditor` does not exist yet.

- [ ] **Step 23: Implement `BooleanFieldEditor.razor`**

```razor
@namespace SyntaxCircus.Cmsify.Components

<input type="checkbox" class="cmsify-field-checkbox" checked="@Value" @onchange="OnChange" />

@code {
    [Parameter] public bool Value { get; set; }
    [Parameter] public EventCallback<bool> ValueChanged { get; set; }

    private Task OnChange(ChangeEventArgs e) => ValueChanged.InvokeAsync((bool?)e.Value ?? false);
}
```

- [ ] **Step 24: Implement `BooleanFieldEditor.razor.css`**

```css
.cmsify-field-checkbox {
    width: 1.1rem;
    height: 1.1rem;
    accent-color: var(--cmsify-color-border-focus, #0d6efd);
}
```

- [ ] **Step 25: Run all Task 3 tests to verify they pass**

Run: `dotnet test tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj --filter FullyQualifiedName~FieldEditors`
Expected: PASS (5 test classes)

- [ ] **Step 26: Commit**

```bash
git add src/Cmsify.Components/FieldEditors/TextFieldEditor.razor src/Cmsify.Components/FieldEditors/TextFieldEditor.razor.css src/Cmsify.Components/FieldEditors/LinkFieldEditor.razor src/Cmsify.Components/FieldEditors/LinkFieldEditor.razor.css src/Cmsify.Components/FieldEditors/QuoteFieldEditor.razor src/Cmsify.Components/FieldEditors/QuoteFieldEditor.razor.css src/Cmsify.Components/FieldEditors/SeparatorFieldEditor.razor src/Cmsify.Components/FieldEditors/SeparatorFieldEditor.razor.css src/Cmsify.Components/FieldEditors/BooleanFieldEditor.razor src/Cmsify.Components/FieldEditors/BooleanFieldEditor.razor.css tests/Cmsify.Components.Tests/FieldEditors/TextFieldEditorTests.cs tests/Cmsify.Components.Tests/FieldEditors/LinkFieldEditorTests.cs tests/Cmsify.Components.Tests/FieldEditors/QuoteFieldEditorTests.cs tests/Cmsify.Components.Tests/FieldEditors/SeparatorFieldEditorTests.cs tests/Cmsify.Components.Tests/FieldEditors/BooleanFieldEditorTests.cs
git commit -m "$(cat <<'EOF'
Add Text, Link, Quote, Separator, and Boolean field editors

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3Smg2MFcrDKPuq2WYQEYU
EOF
)"
```

---

## Task 4: Rich text field editors (RichText, Markdown)

**Files:**
- Create: `src/Cmsify.Components/FieldEditors/RichTextFieldEditor.razor` + `.razor.css`
- Create: `src/Cmsify.Components/FieldEditors/MarkdownFieldEditor.razor` + `.razor.css`
- Create: `tests/Cmsify.Components.Tests/FieldEditors/RichTextFieldEditorTests.cs`
- Create: `tests/Cmsify.Components.Tests/FieldEditors/MarkdownFieldEditorTests.cs`

**Interfaces:**
- Consumes: nothing beyond framework types.
- Produces: `RichTextFieldEditor { [Parameter] string? Value; [Parameter] EventCallback<string?> ValueChanged; [Parameter] int Rows = 6; }`, `MarkdownFieldEditor` (same parameter shape, composes `RichTextFieldEditor` internally and adds a live preview) — both consumed by the `FieldEditor` dispatcher in Task 9.

- [ ] **Step 1: Write the failing test for `RichTextFieldEditor`**

```csharp
using Bunit;
using Microsoft.AspNetCore.Components;
using Shouldly;
using Xunit;

namespace SyntaxCircus.Cmsify.Components.Tests.FieldEditors;

public sealed class RichTextFieldEditorTests : TestContext
{
    [Fact]
    public void RendersSixRowTextareaByDefaultAndRaisesValueChanged()
    {
        string? changed = null;
        var cut = RenderComponent<RichTextFieldEditor>(parameters => parameters
            .Add(p => p.Value, "<p>hi</p>")
            .Add(p => p.ValueChanged, EventCallback.Factory.Create<string?>(this, v => changed = v)));

        var textarea = cut.Find("textarea");
        textarea.GetAttribute("rows").ShouldBe("6");
        textarea.TextContent.ShouldBe("<p>hi</p>");

        textarea.Input("<p>updated</p>");

        changed.ShouldBe("<p>updated</p>");
    }

    [Fact]
    public void HonorsRowsParameter()
    {
        var cut = RenderComponent<RichTextFieldEditor>(parameters => parameters.Add(p => p.Rows, 3));

        cut.Find("textarea").GetAttribute("rows").ShouldBe("3");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj --filter RichTextFieldEditorTests`
Expected: FAIL to build — `RichTextFieldEditor` does not exist yet.

- [ ] **Step 3: Implement `RichTextFieldEditor.razor`**

```razor
@namespace SyntaxCircus.Cmsify.Components

<textarea class="cmsify-field-textarea" rows="@Rows" @oninput="OnInput">@Value</textarea>

@code {
    [Parameter] public string? Value { get; set; }
    [Parameter] public EventCallback<string?> ValueChanged { get; set; }
    [Parameter] public int Rows { get; set; } = 6;

    private Task OnInput(ChangeEventArgs e) => ValueChanged.InvokeAsync(e.Value?.ToString());
}
```

- [ ] **Step 4: Implement `RichTextFieldEditor.razor.css`**

```css
.cmsify-field-textarea {
    display: block;
    width: 100%;
    box-sizing: border-box;
    padding: var(--cmsify-spacing-sm, 0.375rem) var(--cmsify-spacing-md, 0.75rem);
    border: 1px solid var(--cmsify-color-border, #ced4da);
    border-radius: var(--cmsify-radius, 0.375rem);
    font-family: var(--cmsify-font-family, inherit);
    color: var(--cmsify-color-text, inherit);
    resize: vertical;
}

.cmsify-field-textarea:focus {
    outline: none;
    border-color: var(--cmsify-color-border-focus, #86b7fe);
    box-shadow: var(--cmsify-focus-ring, 0 0 0 0.2rem rgba(13, 110, 253, 0.25));
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj --filter RichTextFieldEditorTests`
Expected: PASS

- [ ] **Step 6: Write the failing test for `MarkdownFieldEditor`**

```csharp
using Bunit;
using Microsoft.AspNetCore.Components;
using Shouldly;
using Xunit;

namespace SyntaxCircus.Cmsify.Components.Tests.FieldEditors;

public sealed class MarkdownFieldEditorTests : TestContext
{
    [Fact]
    public void RendersTextareaPlusLivePreviewAndRaisesValueChanged()
    {
        string? changed = null;
        var cut = RenderComponent<MarkdownFieldEditor>(parameters => parameters
            .Add(p => p.Value, "# Heading")
            .Add(p => p.ValueChanged, EventCallback.Factory.Create<string?>(this, v => changed = v)));

        cut.Find("textarea").TextContent.ShouldBe("# Heading");
        cut.Find("pre.cmsify-field-markdown-preview").TextContent.ShouldBe("# Heading");

        cut.Find("textarea").Input("## Updated");

        changed.ShouldBe("## Updated");
    }
}
```

- [ ] **Step 7: Run test to verify it fails**

Run: `dotnet test tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj --filter MarkdownFieldEditorTests`
Expected: FAIL to build — `MarkdownFieldEditor` does not exist yet.

- [ ] **Step 8: Implement `MarkdownFieldEditor.razor`**

```razor
@namespace SyntaxCircus.Cmsify.Components

<RichTextFieldEditor Value="@Value" ValueChanged="ValueChanged" Rows="6" />
<pre class="cmsify-field-markdown-preview">@Value</pre>

@code {
    [Parameter] public string? Value { get; set; }
    [Parameter] public EventCallback<string?> ValueChanged { get; set; }
}
```

- [ ] **Step 9: Implement `MarkdownFieldEditor.razor.css`**

```css
.cmsify-field-markdown-preview {
    margin-top: var(--cmsify-spacing-sm, 0.375rem);
    padding: var(--cmsify-spacing-md, 0.75rem);
    border: 1px solid var(--cmsify-color-border, #ced4da);
    border-radius: var(--cmsify-radius, 0.375rem);
    color: var(--cmsify-color-muted, #6c757d);
    white-space: pre-wrap;
    word-break: break-word;
}
```

- [ ] **Step 10: Run test to verify it passes**

Run: `dotnet test tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj --filter MarkdownFieldEditorTests`
Expected: PASS

- [ ] **Step 11: Commit**

```bash
git add src/Cmsify.Components/FieldEditors/RichTextFieldEditor.razor src/Cmsify.Components/FieldEditors/RichTextFieldEditor.razor.css src/Cmsify.Components/FieldEditors/MarkdownFieldEditor.razor src/Cmsify.Components/FieldEditors/MarkdownFieldEditor.razor.css tests/Cmsify.Components.Tests/FieldEditors/RichTextFieldEditorTests.cs tests/Cmsify.Components.Tests/FieldEditors/MarkdownFieldEditorTests.cs
git commit -m "$(cat <<'EOF'
Add RichText and Markdown field editors

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3Smg2MFcrDKPuq2WYQEYU
EOF
)"
```

---

## Task 5: `PickListFieldEditor` (single and multiple selection)

**Files:**
- Create: `src/Cmsify.Components/FieldEditors/PickListFieldEditor.razor` + `.razor.css`
- Create: `tests/Cmsify.Components.Tests/FieldEditors/PickListFieldEditorTests.cs`

**Interfaces:**
- Consumes: `PickListResponse`/`PickListOptionResponse` from `Cmsify.Contracts`.
- Produces: `PickListFieldEditor { [Parameter] PickListResponse? PickList; [Parameter] bool Multiple; [Parameter] bool IsRequired; [Parameter] string? Value; [Parameter] EventCallback<string?> ValueChanged; [Parameter] IReadOnlyList<string> SelectedValues; [Parameter] EventCallback<IReadOnlyList<string>> SelectedValuesChanged; }` — consumed by the `FieldEditor` dispatcher in Task 9.

- [ ] **Step 1: Write the failing tests**

```csharp
using Bunit;
using Microsoft.AspNetCore.Components;
using Shouldly;
using Xunit;

namespace SyntaxCircus.Cmsify.Components.Tests.FieldEditors;

public sealed class PickListFieldEditorTests : TestContext
{
    private static PickListResponse CreatePickList() => new(
        Guid.NewGuid(),
        "Colors",
        "colors",
        null,
        [
            new PickListOptionResponse(Guid.NewGuid(), "Red", "red", 0),
            new PickListOptionResponse(Guid.NewGuid(), "Blue", "blue", 1),
        ]);

    [Fact]
    public void RendersWarningWhenNoPickListIsBound()
    {
        var cut = RenderComponent<PickListFieldEditor>(parameters => parameters.Add(p => p.PickList, null));

        cut.Find(".cmsify-field-warning").TextContent.ShouldContain("no picklist bound");
    }

    [Fact]
    public void RendersSingleSelectAndRaisesValueChanged()
    {
        string? changed = null;
        var cut = RenderComponent<PickListFieldEditor>(parameters => parameters
            .Add(p => p.PickList, CreatePickList())
            .Add(p => p.Multiple, false)
            .Add(p => p.Value, "red")
            .Add(p => p.ValueChanged, EventCallback.Factory.Create<string?>(this, v => changed = v)));

        var select = cut.Find("select");
        select.HasAttribute("multiple").ShouldBeFalse();
        cut.FindAll("option").Count.ShouldBe(3); // placeholder + 2 options

        select.Change("blue");

        changed.ShouldBe("blue");
    }

    [Fact]
    public void RendersMultiSelectAndRaisesSelectedValuesChanged()
    {
        IReadOnlyList<string>? changed = null;
        var cut = RenderComponent<PickListFieldEditor>(parameters => parameters
            .Add(p => p.PickList, CreatePickList())
            .Add(p => p.Multiple, true)
            .Add(p => p.SelectedValues, new[] { "red" })
            .Add(p => p.SelectedValuesChanged, EventCallback.Factory.Create<IReadOnlyList<string>>(this, v => changed = v)));

        var select = cut.Find("select");
        select.HasAttribute("multiple").ShouldBeTrue();

        select.Change(new[] { "red", "blue" });

        changed.ShouldNotBeNull();
        changed.ShouldBe(new[] { "red", "blue" });
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj --filter PickListFieldEditorTests`
Expected: FAIL to build — `PickListFieldEditor` does not exist yet.

- [ ] **Step 3: Implement `PickListFieldEditor.razor`**

```razor
@namespace SyntaxCircus.Cmsify.Components

@if (PickList is null)
{
    <div class="cmsify-field-warning">This field has no picklist bound. Edit the template to bind one.</div>
}
else if (Multiple)
{
    <select class="cmsify-field-select" multiple @onchange="OnMultiChange">
        @foreach (var option in PickList.Options.OrderBy(o => o.Order))
        {
            <option value="@option.Value" selected="@SelectedValues.Contains(option.Value)">@option.Label</option>
        }
    </select>
}
else
{
    <select class="cmsify-field-select" value="@Value" @onchange="OnSingleChange">
        <option value="">@(IsRequired ? "Select an option" : "None")</option>
        @foreach (var option in PickList.Options.OrderBy(o => o.Order))
        {
            <option value="@option.Value">@option.Label</option>
        }
    </select>
}

@code {
    [Parameter] public PickListResponse? PickList { get; set; }
    [Parameter] public bool Multiple { get; set; }
    [Parameter] public bool IsRequired { get; set; }
    [Parameter] public string? Value { get; set; }
    [Parameter] public EventCallback<string?> ValueChanged { get; set; }
    [Parameter] public IReadOnlyList<string> SelectedValues { get; set; } = [];
    [Parameter] public EventCallback<IReadOnlyList<string>> SelectedValuesChanged { get; set; }

    private Task OnSingleChange(ChangeEventArgs e) => ValueChanged.InvokeAsync(e.Value?.ToString());

    private Task OnMultiChange(ChangeEventArgs e)
    {
        IReadOnlyList<string> values = e.Value switch
        {
            string[] array => array,
            IEnumerable<object> enumerable => enumerable.Select(x => x?.ToString() ?? "").ToList(),
            _ => []
        };
        return SelectedValuesChanged.InvokeAsync(values);
    }
}
```

- [ ] **Step 4: Implement `PickListFieldEditor.razor.css`**

```css
.cmsify-field-select {
    display: block;
    width: 100%;
    box-sizing: border-box;
    padding: var(--cmsify-spacing-sm, 0.375rem) var(--cmsify-spacing-md, 0.75rem);
    border: 1px solid var(--cmsify-color-border, #ced4da);
    border-radius: var(--cmsify-radius, 0.375rem);
    font-family: var(--cmsify-font-family, inherit);
    color: var(--cmsify-color-text, inherit);
}

.cmsify-field-select:focus {
    outline: none;
    border-color: var(--cmsify-color-border-focus, #86b7fe);
    box-shadow: var(--cmsify-focus-ring, 0 0 0 0.2rem rgba(13, 110, 253, 0.25));
}

.cmsify-field-warning {
    padding: var(--cmsify-spacing-sm, 0.375rem) var(--cmsify-spacing-md, 0.75rem);
    border: 1px solid var(--cmsify-color-danger, #dc3545);
    border-radius: var(--cmsify-radius, 0.375rem);
    color: var(--cmsify-color-danger, #dc3545);
    font-size: 0.875rem;
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj --filter PickListFieldEditorTests`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/Cmsify.Components/FieldEditors/PickListFieldEditor.razor src/Cmsify.Components/FieldEditors/PickListFieldEditor.razor.css tests/Cmsify.Components.Tests/FieldEditors/PickListFieldEditorTests.cs
git commit -m "$(cat <<'EOF'
Add PickList field editor with single and multiple selection

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3Smg2MFcrDKPuq2WYQEYU
EOF
)"
```

---

## Task 6: Media and File field editors

**Files:**
- Create: `src/Cmsify.Components/FieldEditors/MediaFieldEditor.razor` + `.razor.css`
- Create: `src/Cmsify.Components/FieldEditors/FileFieldEditor.razor` + `.razor.css`
- Create: `tests/Cmsify.Components.Tests/FieldEditors/MediaFieldEditorTests.cs`
- Create: `tests/Cmsify.Components.Tests/FieldEditors/FileFieldEditorTests.cs`

These are presentational: they display the currently selected asset (if any) and a button that raises `OnPickRequested`. The parent (a smart component, added in Task 13) is responsible for owning an actual picker and calling back in with the newly selected `MediaAssetResponse`.

**Interfaces:**
- Consumes: `MediaAssetResponse` from `Cmsify.Contracts`.
- Produces: `MediaFieldEditor { [Parameter] MediaAssetResponse? SelectedAsset; [Parameter] EventCallback OnPickRequested; }`, `FileFieldEditor` (same shape) — both consumed by the `FieldEditor` dispatcher in Task 9 and by `Client.ContentEditPanel` in Task 13.

- [ ] **Step 1: Write the failing test for `MediaFieldEditor`**

```csharp
using Bunit;
using Microsoft.AspNetCore.Components;
using Shouldly;
using Xunit;

namespace SyntaxCircus.Cmsify.Components.Tests.FieldEditors;

public sealed class MediaFieldEditorTests : TestContext
{
    [Fact]
    public void ShowsPlaceholderWhenNoAssetSelectedAndRaisesOnPickRequested()
    {
        var requested = false;
        var cut = RenderComponent<MediaFieldEditor>(parameters => parameters
            .Add(p => p.OnPickRequested, EventCallback.Factory.Create(this, () => requested = true)));

        cut.Find("button").TextContent.ShouldBe("Select asset");

        cut.Find("button").Click();

        requested.ShouldBeTrue();
    }

    [Fact]
    public void ShowsSelectedAssetFileName()
    {
        var asset = new MediaAssetResponse(Guid.NewGuid(), "logo.png", "image/png", 1024, null, "/media/logo.png", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var cut = RenderComponent<MediaFieldEditor>(parameters => parameters.Add(p => p.SelectedAsset, asset));

        cut.Find("button").TextContent.ShouldBe("logo.png");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj --filter MediaFieldEditorTests`
Expected: FAIL to build — `MediaFieldEditor` does not exist yet.

- [ ] **Step 3: Implement `MediaFieldEditor.razor`**

```razor
@namespace SyntaxCircus.Cmsify.Components

<button type="button" class="cmsify-field-picker-button" @onclick="() => OnPickRequested.InvokeAsync()">
    @(SelectedAsset?.FileName ?? "Select asset")
</button>

@code {
    [Parameter] public MediaAssetResponse? SelectedAsset { get; set; }
    [Parameter] public EventCallback OnPickRequested { get; set; }
}
```

- [ ] **Step 4: Implement `MediaFieldEditor.razor.css`**

```css
.cmsify-field-picker-button {
    display: inline-flex;
    align-items: center;
    padding: var(--cmsify-spacing-sm, 0.375rem) var(--cmsify-spacing-md, 0.75rem);
    border: 1px solid var(--cmsify-color-border, #ced4da);
    border-radius: var(--cmsify-radius, 0.375rem);
    background: transparent;
    color: var(--cmsify-color-text, inherit);
    font-family: var(--cmsify-font-family, inherit);
    cursor: pointer;
}

.cmsify-field-picker-button:hover {
    border-color: var(--cmsify-color-border-focus, #86b7fe);
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj --filter MediaFieldEditorTests`
Expected: PASS

- [ ] **Step 6: Write the failing test for `FileFieldEditor`**

```csharp
using Bunit;
using Microsoft.AspNetCore.Components;
using Shouldly;
using Xunit;

namespace SyntaxCircus.Cmsify.Components.Tests.FieldEditors;

public sealed class FileFieldEditorTests : TestContext
{
    [Fact]
    public void ShowsPlaceholderWhenNoAssetSelectedAndRaisesOnPickRequested()
    {
        var requested = false;
        var cut = RenderComponent<FileFieldEditor>(parameters => parameters
            .Add(p => p.OnPickRequested, EventCallback.Factory.Create(this, () => requested = true)));

        cut.Find("button").TextContent.ShouldBe("Select asset");

        cut.Find("button").Click();

        requested.ShouldBeTrue();
    }

    [Fact]
    public void ShowsSelectedAssetFileName()
    {
        var asset = new MediaAssetResponse(Guid.NewGuid(), "report.pdf", "application/pdf", 2048, null, "/media/report.pdf", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var cut = RenderComponent<FileFieldEditor>(parameters => parameters.Add(p => p.SelectedAsset, asset));

        cut.Find("button").TextContent.ShouldBe("report.pdf");
    }
}
```

- [ ] **Step 7: Run test to verify it fails**

Run: `dotnet test tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj --filter FileFieldEditorTests`
Expected: FAIL to build — `FileFieldEditor` does not exist yet.

- [ ] **Step 8: Implement `FileFieldEditor.razor`**

```razor
@namespace SyntaxCircus.Cmsify.Components

<button type="button" class="cmsify-field-picker-button" @onclick="() => OnPickRequested.InvokeAsync()">
    @(SelectedAsset?.FileName ?? "Select asset")
</button>

@code {
    [Parameter] public MediaAssetResponse? SelectedAsset { get; set; }
    [Parameter] public EventCallback OnPickRequested { get; set; }
}
```

- [ ] **Step 9: Implement `FileFieldEditor.razor.css`**

```css
.cmsify-field-picker-button {
    display: inline-flex;
    align-items: center;
    padding: var(--cmsify-spacing-sm, 0.375rem) var(--cmsify-spacing-md, 0.75rem);
    border: 1px solid var(--cmsify-color-border, #ced4da);
    border-radius: var(--cmsify-radius, 0.375rem);
    background: transparent;
    color: var(--cmsify-color-text, inherit);
    font-family: var(--cmsify-font-family, inherit);
    cursor: pointer;
}

.cmsify-field-picker-button:hover {
    border-color: var(--cmsify-color-border-focus, #86b7fe);
}
```

- [ ] **Step 10: Run test to verify it passes**

Run: `dotnet test tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj --filter FileFieldEditorTests`
Expected: PASS

- [ ] **Step 11: Commit**

```bash
git add src/Cmsify.Components/FieldEditors/MediaFieldEditor.razor src/Cmsify.Components/FieldEditors/MediaFieldEditor.razor.css src/Cmsify.Components/FieldEditors/FileFieldEditor.razor src/Cmsify.Components/FieldEditors/FileFieldEditor.razor.css tests/Cmsify.Components.Tests/FieldEditors/MediaFieldEditorTests.cs tests/Cmsify.Components.Tests/FieldEditors/FileFieldEditorTests.cs
git commit -m "$(cat <<'EOF'
Add Media and File field editors

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3Smg2MFcrDKPuq2WYQEYU
EOF
)"
```

---

## Task 7: `ReferenceFieldEditor`

A "reference" field is not a `PrimitiveType` — it is a `TemplateFieldResponse` whose `TemplateId`/`IsOpen`/`AllowedTypes` designate it as a child-content composition field with `CompositionMode.Reference`. This component only renders the `<select>`; the dispatcher (Task 9) is what decides when to use it.

**Files:**
- Create: `src/Cmsify.Components/FieldEditors/ReferenceFieldEditor.razor` + `.razor.css`
- Create: `tests/Cmsify.Components.Tests/FieldEditors/ReferenceFieldEditorTests.cs`

**Interfaces:**
- Consumes: `ContentItemSummaryResponse` from `Cmsify.Contracts`.
- Produces: `ReferenceFieldEditor { [Parameter] IReadOnlyList<ContentItemSummaryResponse> Options; [Parameter] Guid? Value; [Parameter] EventCallback<Guid?> ValueChanged; [Parameter] bool IsRequired; }` — consumed by the `FieldEditor` dispatcher in Task 9.

- [ ] **Step 1: Write the failing tests**

```csharp
using Bunit;
using Microsoft.AspNetCore.Components;
using Shouldly;
using Xunit;

namespace SyntaxCircus.Cmsify.Components.Tests.FieldEditors;

public sealed class ReferenceFieldEditorTests : TestContext
{
    private static ContentItemSummaryResponse CreateOption(string slug) => new(
        Guid.NewGuid(), Guid.NewGuid(), "Article", ContentStatus.Draft, slug, null, null, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);

    [Fact]
    public void ListsOptionsAndRaisesValueChangedOnSelection()
    {
        var options = new[] { CreateOption("first-post"), CreateOption("second-post") };
        Guid? changed = null;
        var cut = RenderComponent<ReferenceFieldEditor>(parameters => parameters
            .Add(p => p.Options, options)
            .Add(p => p.IsRequired, true)
            .Add(p => p.ValueChanged, EventCallback.Factory.Create<Guid?>(this, v => changed = v)));

        cut.Find("option").TextContent.ShouldBe("Select referenced content");
        cut.FindAll("option").Count.ShouldBe(3);

        cut.Find("select").Change(options[1].Id.ToString());

        changed.ShouldBe(options[1].Id);
    }

    [Fact]
    public void ShowsNonePlaceholderWhenNotRequired()
    {
        var cut = RenderComponent<ReferenceFieldEditor>(parameters => parameters.Add(p => p.IsRequired, false));

        cut.Find("option").TextContent.ShouldBe("None");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj --filter ReferenceFieldEditorTests`
Expected: FAIL to build — `ReferenceFieldEditor` does not exist yet.

- [ ] **Step 3: Implement `ReferenceFieldEditor.razor`**

```razor
@namespace SyntaxCircus.Cmsify.Components

<select class="cmsify-field-select" value="@Value?.ToString()" @onchange="OnChange">
    <option value="">@(IsRequired ? "Select referenced content" : "None")</option>
    @foreach (var option in Options)
    {
        <option value="@option.Id">@(option.Slug ?? option.Id.ToString()) (@option.Status)</option>
    }
</select>

@code {
    [Parameter] public IReadOnlyList<ContentItemSummaryResponse> Options { get; set; } = [];
    [Parameter] public Guid? Value { get; set; }
    [Parameter] public EventCallback<Guid?> ValueChanged { get; set; }
    [Parameter] public bool IsRequired { get; set; }

    private Task OnChange(ChangeEventArgs e) =>
        ValueChanged.InvokeAsync(Guid.TryParse(e.Value?.ToString(), out var selected) ? selected : null);
}
```

- [ ] **Step 4: Implement `ReferenceFieldEditor.razor.css`**

```css
.cmsify-field-select {
    display: block;
    width: 100%;
    box-sizing: border-box;
    padding: var(--cmsify-spacing-sm, 0.375rem) var(--cmsify-spacing-md, 0.75rem);
    border: 1px solid var(--cmsify-color-border, #ced4da);
    border-radius: var(--cmsify-radius, 0.375rem);
    font-family: var(--cmsify-font-family, inherit);
    color: var(--cmsify-color-text, inherit);
}

.cmsify-field-select:focus {
    outline: none;
    border-color: var(--cmsify-color-border-focus, #86b7fe);
    box-shadow: var(--cmsify-focus-ring, 0 0 0 0.2rem rgba(13, 110, 253, 0.25));
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj --filter ReferenceFieldEditorTests`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/Cmsify.Components/FieldEditors/ReferenceFieldEditor.razor src/Cmsify.Components/FieldEditors/ReferenceFieldEditor.razor.css tests/Cmsify.Components.Tests/FieldEditors/ReferenceFieldEditorTests.cs
git commit -m "$(cat <<'EOF'
Add Reference field editor for child-content composition fields

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3Smg2MFcrDKPuq2WYQEYU
EOF
)"
```

---

## Task 8: `ComponentFieldEditor`

Matches today's actual Admin behavior for component-composition fields: repeatable raw-JSON textareas (one per occurrence, up to `MaxOccurrences`), not a structured nested field-by-field editor — building genuine nested field editing is a larger scope increase than current Admin supports and is explicitly out of scope (see spec's "Out of Scope").

**Files:**
- Create: `src/Cmsify.Components/FieldEditors/ComponentFieldEditor.razor` + `.razor.css`
- Create: `tests/Cmsify.Components.Tests/FieldEditors/ComponentFieldEditorTests.cs`

**Interfaces:**
- Consumes: nothing beyond framework types.
- Produces: `ComponentFieldEditor { [Parameter] IReadOnlyList<string> Values; [Parameter] EventCallback<IReadOnlyList<string>> ValuesChanged; [Parameter] int? MaxOccurrences; }` — consumed by the `FieldEditor` dispatcher in Task 9.

- [ ] **Step 1: Write the failing tests**

```csharp
using Bunit;
using Microsoft.AspNetCore.Components;
using Shouldly;
using Xunit;

namespace SyntaxCircus.Cmsify.Components.Tests.FieldEditors;

public sealed class ComponentFieldEditorTests : TestContext
{
    [Fact]
    public void DefaultsToOneEmptyJsonObjectWhenNoValuesProvided()
    {
        var cut = RenderComponent<ComponentFieldEditor>();

        cut.Find("textarea").TextContent.ShouldBe("{}");
    }

    [Fact]
    public void EditingATextareaRaisesValuesChangedWithUpdatedEntry()
    {
        IReadOnlyList<string>? changed = null;
        var cut = RenderComponent<ComponentFieldEditor>(parameters => parameters
            .Add(p => p.Values, new[] { "{\"a\":1}" })
            .Add(p => p.ValuesChanged, EventCallback.Factory.Create<IReadOnlyList<string>>(this, v => changed = v)));

        cut.Find("textarea").Input("{\"a\":2}");

        changed.ShouldBe(new[] { "{\"a\":2}" });
    }

    [Fact]
    public void AddComponentButtonAppendsANewEmptyEntry()
    {
        IReadOnlyList<string>? changed = null;
        var cut = RenderComponent<ComponentFieldEditor>(parameters => parameters
            .Add(p => p.Values, new[] { "{}" })
            .Add(p => p.ValuesChanged, EventCallback.Factory.Create<IReadOnlyList<string>>(this, v => changed = v)));

        cut.Find("button").Click();

        changed.ShouldBe(new[] { "{}", "{}" });
    }

    [Fact]
    public void HidesAddButtonWhenMaxOccurrencesReached()
    {
        var cut = RenderComponent<ComponentFieldEditor>(parameters => parameters
            .Add(p => p.Values, new[] { "{}" })
            .Add(p => p.MaxOccurrences, 1));

        cut.FindAll("button").ShouldBeEmpty();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj --filter ComponentFieldEditorTests`
Expected: FAIL to build — `ComponentFieldEditor` does not exist yet.

- [ ] **Step 3: Implement `ComponentFieldEditor.razor`**

```razor
@namespace SyntaxCircus.Cmsify.Components

@for (var index = 0; index < DisplayValues.Count; index++)
{
    var valueIndex = index;
    <textarea class="cmsify-field-textarea cmsify-field-textarea--code" rows="8"
              placeholder='{ "fieldKey": "value" }'
              @onchange="e => OnItemChanged(valueIndex, e)">@DisplayValues[valueIndex]</textarea>
}
@if (!MaxOccurrences.HasValue || DisplayValues.Count < MaxOccurrences.Value)
{
    <button type="button" class="cmsify-field-add-button" @onclick="AddValue">Add component</button>
}

@code {
    [Parameter] public IReadOnlyList<string> Values { get; set; } = [];
    [Parameter] public EventCallback<IReadOnlyList<string>> ValuesChanged { get; set; }
    [Parameter] public int? MaxOccurrences { get; set; }

    private IReadOnlyList<string> DisplayValues => Values.Count == 0 ? ["{}"] : Values;

    private Task OnItemChanged(int index, ChangeEventArgs e)
    {
        var updated = DisplayValues.ToList();
        updated[index] = e.Value?.ToString() ?? "{}";
        return ValuesChanged.InvokeAsync(updated);
    }

    private Task AddValue()
    {
        var updated = DisplayValues.ToList();
        updated.Add("{}");
        return ValuesChanged.InvokeAsync(updated);
    }
}
```

- [ ] **Step 4: Implement `ComponentFieldEditor.razor.css`**

```css
.cmsify-field-textarea--code {
    display: block;
    width: 100%;
    box-sizing: border-box;
    margin-bottom: var(--cmsify-spacing-sm, 0.375rem);
    padding: var(--cmsify-spacing-sm, 0.375rem) var(--cmsify-spacing-md, 0.75rem);
    border: 1px solid var(--cmsify-color-border, #ced4da);
    border-radius: var(--cmsify-radius, 0.375rem);
    font-family: ui-monospace, SFMono-Regular, Consolas, monospace;
    color: var(--cmsify-color-text, inherit);
    resize: vertical;
}

.cmsify-field-add-button {
    padding: var(--cmsify-spacing-sm, 0.375rem) var(--cmsify-spacing-md, 0.75rem);
    border: 1px solid var(--cmsify-color-border, #ced4da);
    border-radius: var(--cmsify-radius, 0.375rem);
    background: transparent;
    color: var(--cmsify-color-text, inherit);
    cursor: pointer;
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj --filter ComponentFieldEditorTests`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/Cmsify.Components/FieldEditors/ComponentFieldEditor.razor src/Cmsify.Components/FieldEditors/ComponentFieldEditor.razor.css tests/Cmsify.Components.Tests/FieldEditors/ComponentFieldEditorTests.cs
git commit -m "$(cat <<'EOF'
Add Component field editor with repeatable JSON entries

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3Smg2MFcrDKPuq2WYQEYU
EOF
)"
```

---

## Task 9: `FieldEditor` dispatcher

Ties every editor from Tasks 3–8 together, replicating `ContentEditor.razor`'s `RenderField` dispatch precedence exactly: Component fields first, then composition/reference fields, then `PrimitiveType`. Also adds the per-`PrimitiveType` override hook.

**Files:**
- Create: `src/Cmsify.Components/FieldEditors/FieldEditor.razor` + `.razor.css`
- Create: `tests/Cmsify.Components.Tests/TestFieldFactory.cs`
- Create: `tests/Cmsify.Components.Tests/FieldEditors/FieldEditorTests.cs`

**Interfaces:**
- Consumes: every field editor from Tasks 3–8, `ContentFieldEditorValue`/`FieldEditorRenderContext`/`PickListFieldBinding` from Task 2.
- Produces: `FieldEditor { [Parameter,EditorRequired] TemplateFieldResponse Field; [Parameter,EditorRequired] ContentFieldEditorValue Value; [Parameter] EventCallback<ContentFieldEditorValue> ValueChanged; [Parameter] PickListResponse? ResolvedPickList; [Parameter] IReadOnlyList<ContentItemSummaryResponse> ReferenceOptions; [Parameter] EventCallback OnMediaPickRequested; [Parameter] EventCallback OnFilePickRequested; [Parameter] IReadOnlyDictionary<PrimitiveType, RenderFragment<FieldEditorRenderContext>>? FieldTemplateOverrides; }` — consumed by `ContentEditForm` in Task 12. Also produces the reusable `TestFieldFactory.Create(...)` test helper used by Task 12 and Task 13's tests.

- [ ] **Step 1: Create the shared `TestFieldFactory` test helper**

```csharp
using System.Text.Json;

namespace SyntaxCircus.Cmsify.Components.Tests;

internal static class TestFieldFactory
{
    public static TemplateFieldResponse Create(
        PrimitiveType? primitiveType = null,
        bool isRequired = false,
        Guid? componentId = null,
        Guid? templateId = null,
        bool isOpen = false,
        CompositionMode compositionMode = CompositionMode.Reference,
        JsonElement? fieldConfig = null,
        int? maxOccurrences = null,
        IReadOnlyList<TemplateFieldAllowedTypeResponse>? allowedTypes = null) =>
        new(
            Guid.NewGuid(),
            null,
            "field-key",
            "Field Label",
            null,
            0,
            isRequired,
            0,
            maxOccurrences,
            isOpen,
            compositionMode,
            primitiveType,
            templateId,
            allowedTypes ?? [],
            fieldConfig,
            componentId);
}
```

- [ ] **Step 2: Write the failing tests**

```csharp
using Bunit;
using Microsoft.AspNetCore.Components;
using Shouldly;
using Xunit;

namespace SyntaxCircus.Cmsify.Components.Tests.FieldEditors;

using SyntaxCircus.Cmsify.Components.Tests;

public sealed class FieldEditorTests : TestContext
{
    [Fact]
    public void RendersTextFieldEditorForDefaultPrimitiveType()
    {
        var field = TestFieldFactory.Create(primitiveType: PrimitiveType.Text);
        var value = new ContentFieldEditorValue { TextValue = "abc" };

        var cut = RenderComponent<FieldEditor>(parameters => parameters
            .Add(p => p.Field, field)
            .Add(p => p.Value, value));

        cut.FindComponent<TextFieldEditor>().Instance.Value.ShouldBe("abc");
    }

    [Fact]
    public void RendersBooleanFieldEditorForBooleanPrimitiveType()
    {
        var field = TestFieldFactory.Create(primitiveType: PrimitiveType.Boolean);
        var value = new ContentFieldEditorValue { BoolValue = true };

        var cut = RenderComponent<FieldEditor>(parameters => parameters
            .Add(p => p.Field, field)
            .Add(p => p.Value, value));

        cut.FindComponent<BooleanFieldEditor>().Instance.Value.ShouldBeTrue();
    }

    [Fact]
    public void RendersSeparatorFieldEditorForSeparatorPrimitiveType()
    {
        var field = TestFieldFactory.Create(primitiveType: PrimitiveType.Separator);

        var cut = RenderComponent<FieldEditor>(parameters => parameters
            .Add(p => p.Field, field)
            .Add(p => p.Value, new ContentFieldEditorValue()));

        cut.FindComponent<SeparatorFieldEditor>().ShouldNotBeNull();
    }

    [Fact]
    public void RendersComponentFieldEditorWhenComponentIdIsSet()
    {
        var field = TestFieldFactory.Create(componentId: Guid.NewGuid());
        var value = new ContentFieldEditorValue { ComponentValues = ["{\"a\":1}"] };

        var cut = RenderComponent<FieldEditor>(parameters => parameters
            .Add(p => p.Field, field)
            .Add(p => p.Value, value));

        cut.FindComponent<ComponentFieldEditor>().Instance.Values.ShouldBe(new[] { "{\"a\":1}" });
    }

    [Fact]
    public void RendersReferenceFieldEditorForReferenceCompositionField()
    {
        var field = TestFieldFactory.Create(templateId: Guid.NewGuid(), compositionMode: CompositionMode.Reference);

        var cut = RenderComponent<FieldEditor>(parameters => parameters
            .Add(p => p.Field, field)
            .Add(p => p.Value, new ContentFieldEditorValue())
            .Add(p => p.ReferenceOptions, Array.Empty<ContentItemSummaryResponse>()));

        cut.FindComponent<ReferenceFieldEditor>().ShouldNotBeNull();
    }

    [Fact]
    public void RendersInlineNotAvailableWarningForInlineCompositionField()
    {
        var field = TestFieldFactory.Create(templateId: Guid.NewGuid(), compositionMode: CompositionMode.Inline);

        var cut = RenderComponent<FieldEditor>(parameters => parameters
            .Add(p => p.Field, field)
            .Add(p => p.Value, new ContentFieldEditorValue()));

        cut.Find(".cmsify-field-warning").TextContent.ShouldContain("not available yet");
    }

    [Fact]
    public void UsesOverrideTemplateWhenProvidedForMatchingPrimitiveType()
    {
        var field = TestFieldFactory.Create(primitiveType: PrimitiveType.Text);
        var overrides = new Dictionary<PrimitiveType, RenderFragment<FieldEditorRenderContext>>
        {
            [PrimitiveType.Text] = context => builder => builder.AddMarkupContent(0, "<span class=\"custom\">overridden</span>")
        };

        var cut = RenderComponent<FieldEditor>(parameters => parameters
            .Add(p => p.Field, field)
            .Add(p => p.Value, new ContentFieldEditorValue())
            .Add(p => p.FieldTemplateOverrides, overrides));

        cut.Find("span.custom").TextContent.ShouldBe("overridden");
        cut.FindComponents<TextFieldEditor>().ShouldBeEmpty();
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj --filter FieldEditorTests`
Expected: FAIL to build — `FieldEditor` does not exist yet.

- [ ] **Step 4: Implement `FieldEditor.razor`**

```razor
@namespace SyntaxCircus.Cmsify.Components

@if (TryGetOverride(out var overrideFragment))
{
    @overrideFragment(RenderContext)
}
else if (Field.ComponentId.HasValue)
{
    <ComponentFieldEditor Values="@Value.ComponentValues" MaxOccurrences="@Field.MaxOccurrences"
                          ValuesChanged="@(values => UpdateAsync(v => v.ComponentValues = values))" />
}
else if (IsCompositionField)
{
    @if (Field.CompositionMode == CompositionMode.Reference)
    {
        <ReferenceFieldEditor Options="@ReferenceOptions" Value="@Value.ChildContentItemId" IsRequired="@Field.IsRequired"
                              ValueChanged="@(id => UpdateAsync(v => v.ChildContentItemId = id))" />
    }
    else
    {
        <div class="cmsify-field-warning">Inline child content editing is not available yet. Use a reference field or create the child content separately.</div>
    }
}
else
{
    switch (Field.PrimitiveType)
    {
        case PrimitiveType.Boolean:
            <BooleanFieldEditor Value="@Value.BoolValue" ValueChanged="@(v => UpdateAsync(x => x.BoolValue = v))" />
            break;
        case PrimitiveType.Markdown:
            <MarkdownFieldEditor Value="@Value.TextValue" ValueChanged="@(v => UpdateAsync(x => x.TextValue = v))" />
            break;
        case PrimitiveType.RichText:
            <RichTextFieldEditor Value="@Value.TextValue" ValueChanged="@(v => UpdateAsync(x => x.TextValue = v))" />
            break;
        case PrimitiveType.Media:
            <MediaFieldEditor SelectedAsset="@Value.SelectedMediaAsset" OnPickRequested="OnMediaPickRequested" />
            break;
        case PrimitiveType.File:
            <FileFieldEditor SelectedAsset="@Value.SelectedFileAsset" OnPickRequested="OnFilePickRequested" />
            break;
        case PrimitiveType.PickList:
            <PickListFieldEditor PickList="@ResolvedPickList" Multiple="@PickListBinding.Multiple" IsRequired="@Field.IsRequired"
                                  Value="@Value.TextValue" ValueChanged="@(v => UpdateAsync(x => x.TextValue = v))"
                                  SelectedValues="@Value.MultiValues" SelectedValuesChanged="@(v => UpdateAsync(x => x.MultiValues = v))" />
            break;
        case PrimitiveType.Link:
            <LinkFieldEditor Value="@Value.TextValue" ValueChanged="@(v => UpdateAsync(x => x.TextValue = v))" />
            break;
        case PrimitiveType.Quote:
            <QuoteFieldEditor Value="@Value.TextValue" ValueChanged="@(v => UpdateAsync(x => x.TextValue = v))" />
            break;
        case PrimitiveType.Separator:
            <SeparatorFieldEditor />
            break;
        default:
            <TextFieldEditor Value="@Value.TextValue" ValueChanged="@(v => UpdateAsync(x => x.TextValue = v))" />
            break;
    }
}

@code {
    [Parameter, EditorRequired] public TemplateFieldResponse Field { get; set; } = null!;
    [Parameter, EditorRequired] public ContentFieldEditorValue Value { get; set; } = null!;
    [Parameter] public EventCallback<ContentFieldEditorValue> ValueChanged { get; set; }
    [Parameter] public PickListResponse? ResolvedPickList { get; set; }
    [Parameter] public IReadOnlyList<ContentItemSummaryResponse> ReferenceOptions { get; set; } = [];
    [Parameter] public EventCallback OnMediaPickRequested { get; set; }
    [Parameter] public EventCallback OnFilePickRequested { get; set; }
    [Parameter] public IReadOnlyDictionary<PrimitiveType, RenderFragment<FieldEditorRenderContext>>? FieldTemplateOverrides { get; set; }

    private bool IsCompositionField =>
        Field.TemplateId.HasValue || Field.IsOpen || Field.AllowedTypes.Any(allowed => allowed.AllowedTemplateId.HasValue);

    private PickListFieldBinding PickListBinding => PickListFieldBinding.FromFieldConfig(Field.FieldConfig);

    private FieldEditorRenderContext RenderContext => new(Field, Value, ValueChanged);

    private bool TryGetOverride(out RenderFragment<FieldEditorRenderContext> fragment)
    {
        if (Field.PrimitiveType.HasValue && FieldTemplateOverrides is not null &&
            FieldTemplateOverrides.TryGetValue(Field.PrimitiveType.Value, out var found))
        {
            fragment = found;
            return true;
        }

        fragment = null!;
        return false;
    }

    private Task UpdateAsync(Action<ContentFieldEditorValue> mutate)
    {
        mutate(Value);
        return ValueChanged.InvokeAsync(Value);
    }
}
```

- [ ] **Step 5: Implement `FieldEditor.razor.css`**

```css
.cmsify-field-warning {
    padding: var(--cmsify-spacing-sm, 0.375rem) var(--cmsify-spacing-md, 0.75rem);
    border: 1px solid var(--cmsify-color-danger, #dc3545);
    border-radius: var(--cmsify-radius, 0.375rem);
    color: var(--cmsify-color-danger, #dc3545);
    font-size: 0.875rem;
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj --filter FieldEditorTests`
Expected: PASS (7 tests)

- [ ] **Step 7: Run the full field-editor test suite to confirm no regressions**

Run: `dotnet test tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj --filter FullyQualifiedName~FieldEditors`
Expected: PASS (all classes from Tasks 3–9)

- [ ] **Step 8: Commit**

```bash
git add src/Cmsify.Components/FieldEditors/FieldEditor.razor src/Cmsify.Components/FieldEditors/FieldEditor.razor.css tests/Cmsify.Components.Tests/TestFieldFactory.cs tests/Cmsify.Components.Tests/FieldEditors/FieldEditorTests.cs
git commit -m "$(cat <<'EOF'
Add FieldEditor dispatcher with per-primitive-type override hook

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3Smg2MFcrDKPuq2WYQEYU
EOF
)"
```

---

## Task 10: `MediaPickerModal` (presentational)

**Files:**
- Create: `src/Cmsify.Components/Pickers/MediaPickerModal.razor` + `.razor.css`
- Create: `tests/Cmsify.Components.Tests/Pickers/MediaPickerModalTests.cs`

**Interfaces:**
- Consumes: `MediaAssetResponse` from `Cmsify.Contracts`.
- Produces: `MediaPickerModal { [Parameter] bool Visible; [Parameter] IReadOnlyList<MediaAssetResponse> Assets; [Parameter] EventCallback<string> OnSearch; [Parameter] EventCallback<InputFileChangeEventArgs> OnUpload; [Parameter] EventCallback<MediaAssetResponse> OnSelect; [Parameter] EventCallback Closed; }` — consumed by `Client.MediaPickerPanel` in Task 11.

- [ ] **Step 1: Write the failing tests**

```csharp
using Bunit;
using Microsoft.AspNetCore.Components;
using Shouldly;
using Xunit;

namespace SyntaxCircus.Cmsify.Components.Tests.Pickers;

public sealed class MediaPickerModalTests : TestContext
{
    private static MediaAssetResponse CreateAsset(string fileName) => new(
        Guid.NewGuid(), fileName, "image/png", 1024, null, $"/media/{fileName}", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    [Fact]
    public void RendersNothingWhenNotVisible()
    {
        var cut = RenderComponent<MediaPickerModal>(parameters => parameters.Add(p => p.Visible, false));

        cut.Markup.ShouldBeEmpty();
    }

    [Fact]
    public void ListsAssetsAndRaisesOnSelect()
    {
        MediaAssetResponse? selected = null;
        var asset = CreateAsset("logo.png");
        var cut = RenderComponent<MediaPickerModal>(parameters => parameters
            .Add(p => p.Visible, true)
            .Add(p => p.Assets, new[] { asset })
            .Add(p => p.OnSelect, EventCallback.Factory.Create<MediaAssetResponse>(this, a => selected = a)));

        cut.Find(".cmsify-picker-item").TextContent.ShouldContain("logo.png");

        cut.Find(".cmsify-picker-item").Click();

        selected.ShouldBe(asset);
    }

    [Fact]
    public void RaisesOnSearchWithCurrentSearchTerm()
    {
        string? searched = null;
        var cut = RenderComponent<MediaPickerModal>(parameters => parameters
            .Add(p => p.Visible, true)
            .Add(p => p.OnSearch, EventCallback.Factory.Create<string>(this, s => searched = s)));

        cut.Find(".cmsify-picker-search input").Input("logo");
        cut.Find(".cmsify-picker-search-button").Click();

        searched.ShouldBe("logo");
    }

    [Fact]
    public void RaisesClosedFromCancelButton()
    {
        var closed = false;
        var cut = RenderComponent<MediaPickerModal>(parameters => parameters
            .Add(p => p.Visible, true)
            .Add(p => p.Closed, EventCallback.Factory.Create(this, () => closed = true)));

        cut.Find(".cmsify-picker-cancel").Click();

        closed.ShouldBeTrue();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj --filter MediaPickerModalTests`
Expected: FAIL to build — `MediaPickerModal` does not exist yet.

- [ ] **Step 3: Implement `MediaPickerModal.razor`**

```razor
@namespace SyntaxCircus.Cmsify.Components

@if (Visible)
{
    <div class="cmsify-picker-backdrop">
        <div class="cmsify-picker-dialog" role="dialog" aria-modal="true">
            <div class="cmsify-picker-header">
                <h2 class="cmsify-picker-title">Select media</h2>
                <button type="button" class="cmsify-picker-close" @onclick="() => Closed.InvokeAsync()" aria-label="Close">&times;</button>
            </div>
            <div class="cmsify-picker-body">
                <div class="cmsify-picker-search">
                    <input class="cmsify-field-input" placeholder="Search filename" value="@searchTerm" @oninput="OnSearchInput" />
                    <button type="button" class="cmsify-picker-search-button" @onclick="() => OnSearch.InvokeAsync(searchTerm)">Search</button>
                </div>
                <InputFile OnChange="e => OnUpload.InvokeAsync(e)" />
                <div class="cmsify-picker-grid">
                    @foreach (var asset in Assets)
                    {
                        <button type="button" class="cmsify-picker-item" @onclick="() => OnSelect.InvokeAsync(asset)">
                            <strong>@asset.FileName</strong>
                            <span class="cmsify-picker-item-meta">@asset.MimeType</span>
                        </button>
                    }
                </div>
            </div>
            <div class="cmsify-picker-footer">
                <button type="button" class="cmsify-picker-cancel" @onclick="() => Closed.InvokeAsync()">Cancel</button>
            </div>
        </div>
    </div>
}

@code {
    [Parameter] public bool Visible { get; set; }
    [Parameter] public IReadOnlyList<MediaAssetResponse> Assets { get; set; } = [];
    [Parameter] public EventCallback<string> OnSearch { get; set; }
    [Parameter] public EventCallback<InputFileChangeEventArgs> OnUpload { get; set; }
    [Parameter] public EventCallback<MediaAssetResponse> OnSelect { get; set; }
    [Parameter] public EventCallback Closed { get; set; }

    private string searchTerm = "";

    private void OnSearchInput(ChangeEventArgs e) => searchTerm = e.Value?.ToString() ?? "";
}
```

- [ ] **Step 4: Implement `MediaPickerModal.razor.css`**

```css
.cmsify-picker-backdrop {
    position: fixed;
    inset: 0;
    background: rgba(0, 0, 0, 0.5);
    display: flex;
    align-items: center;
    justify-content: center;
    z-index: 1050;
}

.cmsify-picker-dialog {
    background: Canvas;
    color: var(--cmsify-color-text, inherit);
    border-radius: var(--cmsify-radius, 0.375rem);
    width: min(720px, 90vw);
    max-height: 85vh;
    display: flex;
    flex-direction: column;
}

.cmsify-picker-header, .cmsify-picker-footer {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: var(--cmsify-spacing-md, 0.75rem);
    border-bottom: 1px solid var(--cmsify-color-border, #ced4da);
}

.cmsify-picker-footer {
    border-bottom: none;
    border-top: 1px solid var(--cmsify-color-border, #ced4da);
}

.cmsify-picker-body {
    padding: var(--cmsify-spacing-md, 0.75rem);
    overflow-y: auto;
}

.cmsify-picker-search {
    display: flex;
    gap: var(--cmsify-spacing-sm, 0.375rem);
    margin-bottom: var(--cmsify-spacing-md, 0.75rem);
}

.cmsify-picker-search input {
    flex: 1;
}

.cmsify-picker-grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(160px, 1fr));
    gap: var(--cmsify-spacing-sm, 0.375rem);
    margin-top: var(--cmsify-spacing-md, 0.75rem);
}

.cmsify-picker-item {
    text-align: left;
    padding: var(--cmsify-spacing-sm, 0.375rem);
    border: 1px solid var(--cmsify-color-border, #ced4da);
    border-radius: var(--cmsify-radius, 0.375rem);
    background: transparent;
    cursor: pointer;
    display: flex;
    flex-direction: column;
    gap: 0.25rem;
}

.cmsify-picker-item-meta {
    color: var(--cmsify-color-muted, #6c757d);
    font-size: 0.75rem;
}

.cmsify-picker-close, .cmsify-picker-search-button, .cmsify-picker-cancel {
    border: 1px solid var(--cmsify-color-border, #ced4da);
    border-radius: var(--cmsify-radius, 0.375rem);
    background: transparent;
    color: var(--cmsify-color-text, inherit);
    cursor: pointer;
    padding: var(--cmsify-spacing-sm, 0.375rem) var(--cmsify-spacing-md, 0.75rem);
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj --filter MediaPickerModalTests`
Expected: PASS (4 tests)

- [ ] **Step 6: Commit**

```bash
git add src/Cmsify.Components/Pickers/MediaPickerModal.razor src/Cmsify.Components/Pickers/MediaPickerModal.razor.css tests/Cmsify.Components.Tests/Pickers/MediaPickerModalTests.cs
git commit -m "$(cat <<'EOF'
Add presentational MediaPickerModal component

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3Smg2MFcrDKPuq2WYQEYU
EOF
)"
```

---

## Task 11: `Client.MediaPickerPanel` (SDK-backed smart wrapper)

This is the first "smart" component. It takes a `CmsifyClient` as an explicit parameter (never `@inject` — see Global Constraints) so it stays testable without a DI container. Tests drive `CmsifyClient` through a real `HttpClient` backed by a fake `HttpMessageHandler`, since `CmsifyClient` is a concrete sealed class.

**Files:**
- Create: `tests/Cmsify.Components.Tests/FakeHttpMessageHandler.cs`
- Create: `src/Cmsify.Components/Client/_Imports.razor`
- Create: `src/Cmsify.Components/Client/MediaPickerPanel.razor`
- Create: `tests/Cmsify.Components.Tests/Client/MediaPickerPanelTests.cs`

**Interfaces:**
- Consumes: `MediaPickerModal` (Task 10), `CmsifyClient`/`MediaClient` (`sdk/dotnet/src/SyntaxCircus.Cmsify.Client/CmsifyServices.cs`).
- Produces: `SyntaxCircus.Cmsify.Components.Client.MediaPickerPanel { [Parameter,EditorRequired] CmsifyClient Client; [Parameter,EditorRequired] Guid WorkspaceId; [Parameter] bool Visible; [Parameter] string? MimePrefix; [Parameter] EventCallback<MediaAssetResponse> Selected; [Parameter] EventCallback Closed; }` — consumed by `Client.ContentEditPanel` in Task 13. Also produces the reusable `FakeHttpMessageHandler` and `TestCmsifyClientFactory.Create(...)` test helpers used by every remaining smart-component test in Tasks 13 and 15.

- [ ] **Step 1: Create the `FakeHttpMessageHandler` and `TestCmsifyClientFactory` test helpers**

```csharp
using System.Net;
using System.Text;

namespace SyntaxCircus.Cmsify.Components.Tests;

internal sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(respond(request));

    public static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
}

internal static class TestCmsifyClientFactory
{
    public static CmsifyClient Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var httpClient = new HttpClient(new FakeHttpMessageHandler(respond)) { BaseAddress = new Uri("https://cmsify.test/") };
        return new CmsifyClient(httpClient, new CmsifyClientOptions { EnableRetries = false });
    }
}
```

- [ ] **Step 2: Write the failing test**

```csharp
using Bunit;
using Microsoft.AspNetCore.Components;
using Shouldly;
using Xunit;

namespace SyntaxCircus.Cmsify.Components.Tests.Client;

using SyntaxCircus.Cmsify.Components.Tests;

public sealed class MediaPickerPanelTests : TestContext
{
    [Fact]
    public void LoadsAssetsWhenVisibleAndRaisesSelected()
    {
        var workspaceId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var client = TestCmsifyClientFactory.Create(request =>
        {
            request.RequestUri!.AbsolutePath.ShouldBe($"/api/v1/workspaces/{workspaceId}/media");
            return FakeHttpMessageHandler.Json($$"""
                { "items": [{ "id": "{{assetId}}", "fileName": "logo.png", "mimeType": "image/png", "sizeBytes": 10, "altText": null, "url": "/media/logo.png", "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z" }], "totalCount": 1, "page": 1, "pageSize": 20 }
                """);
        });

        MediaAssetResponse? selected = null;
        var cut = RenderComponent<SyntaxCircus.Cmsify.Components.Client.MediaPickerPanel>(parameters => parameters
            .Add(p => p.Client, client)
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.Visible, true)
            .Add(p => p.Selected, EventCallback.Factory.Create<MediaAssetResponse>(this, a => selected = a)));

        cut.Find(".cmsify-picker-item").TextContent.ShouldContain("logo.png");

        cut.Find(".cmsify-picker-item").Click();

        selected.ShouldNotBeNull();
        selected!.Id.ShouldBe(assetId);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj --filter MediaPickerPanelTests`
Expected: FAIL to build — `SyntaxCircus.Cmsify.Components.Client.MediaPickerPanel` does not exist yet.

- [ ] **Step 4: Create `src/Cmsify.Components/Client/_Imports.razor`**

```razor
@using SyntaxCircus.Cmsify.Components
```

- [ ] **Step 5: Implement `MediaPickerPanel.razor`**

```razor
@namespace SyntaxCircus.Cmsify.Components.Client

<MediaPickerModal Visible="@Visible" Assets="@assets" OnSearch="SearchAsync" OnUpload="UploadAsync" OnSelect="SelectAsync" Closed="() => Closed.InvokeAsync()" />

@code {
    [Parameter, EditorRequired] public CmsifyClient Client { get; set; } = null!;
    [Parameter, EditorRequired] public Guid WorkspaceId { get; set; }
    [Parameter] public bool Visible { get; set; }
    [Parameter] public string? MimePrefix { get; set; }
    [Parameter] public EventCallback<MediaAssetResponse> Selected { get; set; }
    [Parameter] public EventCallback Closed { get; set; }

    private IReadOnlyList<MediaAssetResponse> assets = [];

    protected override async Task OnParametersSetAsync()
    {
        if (Visible)
        {
            await LoadAsync(null);
        }
    }

    private async Task LoadAsync(string? search)
    {
        var page = await Client.Media.ListAsync(WorkspaceId, MimePrefix, search);
        assets = page?.Items ?? [];
    }

    private Task SearchAsync(string search) => LoadAsync(search);

    private async Task UploadAsync(InputFileChangeEventArgs args)
    {
        await using var stream = args.File.OpenReadStream(1_073_741_824);
        await Client.Media.UploadAsync(WorkspaceId, stream, args.File.Name, args.File.ContentType);
        await LoadAsync(null);
    }

    private async Task SelectAsync(MediaAssetResponse asset) => await Selected.InvokeAsync(asset);
}
```

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj --filter MediaPickerPanelTests`
Expected: PASS

- [ ] **Step 7: Commit**

```bash
git add tests/Cmsify.Components.Tests/FakeHttpMessageHandler.cs src/Cmsify.Components/Client/_Imports.razor src/Cmsify.Components/Client/MediaPickerPanel.razor tests/Cmsify.Components.Tests/Client/MediaPickerPanelTests.cs
git commit -m "$(cat <<'EOF'
Add SDK-backed MediaPickerPanel and fake-HTTP test infrastructure

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3Smg2MFcrDKPuq2WYQEYU
EOF
)"
```

---

## Task 12: `ContentEditForm` (presentational composed form)

Scope note: this covers field editing plus slug/locale/tags metadata and a Save button only. Lifecycle/workflow actions (submit/approve/publish/archive/restore, translation-linking) stay in `Cmsify.Admin` — see Global Constraints.

**Files:**
- Create: `src/Cmsify.Components/ContentEditForm.razor` + `.razor.css`
- Create: `tests/Cmsify.Components.Tests/ContentEditFormTests.cs`

**Interfaces:**
- Consumes: `FieldEditor` (Task 9), `PickListFieldBinding` (Task 2), `TemplateVersionResponse`/`ContentItemSummaryResponse`/`PickListResponse` from `Cmsify.Contracts`.
- Produces: `ContentEditForm { [Parameter,EditorRequired] TemplateVersionResponse TemplateVersion; [Parameter,EditorRequired] IReadOnlyDictionary<Guid,ContentFieldEditorValue> FieldValues; [Parameter] EventCallback<(TemplateFieldResponse Field, ContentFieldEditorValue Value)> FieldValueChanged; [Parameter] IReadOnlyDictionary<Guid,PickListResponse> PickListsByRevisionId; [Parameter] IReadOnlyDictionary<Guid,IReadOnlyList<ContentItemSummaryResponse>> ReferenceOptionsByTemplateId; [Parameter] string? Slug/Locale/Tags + matching *Changed callbacks; [Parameter] string? Error; [Parameter] EventCallback OnSave; [Parameter] EventCallback<TemplateFieldResponse> OnMediaPickRequested/OnFilePickRequested; [Parameter] IReadOnlyDictionary<PrimitiveType,RenderFragment<FieldEditorRenderContext>>? FieldTemplateOverrides; }` — consumed by `Client.ContentEditPanel` in Task 13.

- [ ] **Step 1: Write the failing tests**

```csharp
using Bunit;
using Microsoft.AspNetCore.Components;
using Shouldly;
using Xunit;

namespace SyntaxCircus.Cmsify.Components.Tests;

public sealed class ContentEditFormTests : TestContext
{
    private static TemplateVersionResponse CreateTemplateVersion(params TemplateFieldResponse[] fields) => new(
        Guid.NewGuid(), Guid.NewGuid(), 1, TemplateVersionStatus.Draft, null, null, [], fields);

    [Fact]
    public void RendersOneFieldEditorPerFieldOrderedByOrder()
    {
        var first = TestFieldFactory.Create(primitiveType: PrimitiveType.Text) with { Order = 1, Label = "Second" };
        var second = TestFieldFactory.Create(primitiveType: PrimitiveType.Text) with { Order = 0, Label = "First" };
        var templateVersion = CreateTemplateVersion(first, second);

        var cut = RenderComponent<ContentEditForm>(parameters => parameters
            .Add(p => p.TemplateVersion, templateVersion)
            .Add(p => p.FieldValues, new Dictionary<Guid, ContentFieldEditorValue>()));

        var labels = cut.FindAll(".cmsify-form-label").Select(el => el.TextContent.Trim()).ToList();
        labels[0].ShouldStartWith("First");
        labels[1].ShouldStartWith("Second");
    }

    [Fact]
    public void RaisesFieldValueChangedWhenAFieldEditorChanges()
    {
        var field = TestFieldFactory.Create(primitiveType: PrimitiveType.Text);
        var templateVersion = CreateTemplateVersion(field);
        (TemplateFieldResponse Field, ContentFieldEditorValue Value)? changed = null;

        var cut = RenderComponent<ContentEditForm>(parameters => parameters
            .Add(p => p.TemplateVersion, templateVersion)
            .Add(p => p.FieldValues, new Dictionary<Guid, ContentFieldEditorValue>())
            .Add(p => p.FieldValueChanged, EventCallback.Factory.Create<(TemplateFieldResponse, ContentFieldEditorValue)>(this, v => changed = v)));

        cut.Find("input").Input("new value");

        changed.ShouldNotBeNull();
        changed!.Value.Field.Id.ShouldBe(field.Id);
        changed.Value.Value.TextValue.ShouldBe("new value");
    }

    [Fact]
    public void RaisesOnSaveFromSaveButton()
    {
        var saved = false;
        var cut = RenderComponent<ContentEditForm>(parameters => parameters
            .Add(p => p.TemplateVersion, CreateTemplateVersion())
            .Add(p => p.FieldValues, new Dictionary<Guid, ContentFieldEditorValue>())
            .Add(p => p.OnSave, EventCallback.Factory.Create(this, () => saved = true)));

        cut.Find(".cmsify-form-save-button").Click();

        saved.ShouldBeTrue();
    }

    [Fact]
    public void RendersErrorBannerWhenErrorIsSet()
    {
        var cut = RenderComponent<ContentEditForm>(parameters => parameters
            .Add(p => p.TemplateVersion, CreateTemplateVersion())
            .Add(p => p.FieldValues, new Dictionary<Guid, ContentFieldEditorValue>())
            .Add(p => p.Error, "Something went wrong"));

        cut.Find(".cmsify-form-error").TextContent.ShouldBe("Something went wrong");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj --filter ContentEditFormTests`
Expected: FAIL to build — `ContentEditForm` does not exist yet.

- [ ] **Step 3: Implement `ContentEditForm.razor`**

```razor
@namespace SyntaxCircus.Cmsify.Components

<div class="cmsify-form">
    @if (!string.IsNullOrWhiteSpace(Error))
    {
        <div class="cmsify-form-error">@Error</div>
    }
    <div class="cmsify-form-fields">
        @foreach (var field in TemplateVersion.Fields.OrderBy(f => f.Order))
        {
            var binding = PickListFieldBinding.FromFieldConfig(field.FieldConfig);
            var resolvedPickList = binding.RevisionId.HasValue && PickListsByRevisionId.TryGetValue(binding.RevisionId.Value, out var pickList) ? pickList : null;
            var referenceOptions = ReferenceOptionsForField(field);
            <div class="cmsify-form-field">
                <label class="cmsify-form-label">@field.Label @(field.IsRequired ? "*" : "")</label>
                <FieldEditor Field="@field" Value="@GetOrCreateValue(field.Id)"
                             ValueChanged="@(value => FieldValueChanged.InvokeAsync((field, value)))"
                             ResolvedPickList="@resolvedPickList"
                             ReferenceOptions="@referenceOptions"
                             OnMediaPickRequested="@(() => OnMediaPickRequested.InvokeAsync(field))"
                             OnFilePickRequested="@(() => OnFilePickRequested.InvokeAsync(field))"
                             FieldTemplateOverrides="@FieldTemplateOverrides" />
                @if (!string.IsNullOrWhiteSpace(field.HelpText))
                {
                    <div class="cmsify-form-help">@field.HelpText</div>
                }
            </div>
        }
    </div>
    <div class="cmsify-form-metadata">
        <label class="cmsify-form-label">Slug</label>
        <input class="cmsify-field-input" value="@Slug" @oninput="e => SlugChanged.InvokeAsync(e.Value?.ToString())" />
        <label class="cmsify-form-label">Locale</label>
        <input class="cmsify-field-input" value="@Locale" @oninput="e => LocaleChanged.InvokeAsync(e.Value?.ToString())" />
        <label class="cmsify-form-label">Tags</label>
        <input class="cmsify-field-input" value="@Tags" @oninput="e => TagsChanged.InvokeAsync(e.Value?.ToString())" />
    </div>
    <button type="button" class="cmsify-form-save-button" @onclick="() => OnSave.InvokeAsync()">Save</button>
</div>

@code {
    [Parameter, EditorRequired] public TemplateVersionResponse TemplateVersion { get; set; } = null!;
    [Parameter, EditorRequired] public IReadOnlyDictionary<Guid, ContentFieldEditorValue> FieldValues { get; set; } = new Dictionary<Guid, ContentFieldEditorValue>();
    [Parameter] public EventCallback<(TemplateFieldResponse Field, ContentFieldEditorValue Value)> FieldValueChanged { get; set; }
    [Parameter] public IReadOnlyDictionary<Guid, PickListResponse> PickListsByRevisionId { get; set; } = new Dictionary<Guid, PickListResponse>();
    [Parameter] public IReadOnlyDictionary<Guid, IReadOnlyList<ContentItemSummaryResponse>> ReferenceOptionsByTemplateId { get; set; } = new Dictionary<Guid, IReadOnlyList<ContentItemSummaryResponse>>();
    [Parameter] public string? Slug { get; set; }
    [Parameter] public EventCallback<string?> SlugChanged { get; set; }
    [Parameter] public string? Locale { get; set; }
    [Parameter] public EventCallback<string?> LocaleChanged { get; set; }
    [Parameter] public string? Tags { get; set; }
    [Parameter] public EventCallback<string?> TagsChanged { get; set; }
    [Parameter] public string? Error { get; set; }
    [Parameter] public EventCallback OnSave { get; set; }
    [Parameter] public EventCallback<TemplateFieldResponse> OnMediaPickRequested { get; set; }
    [Parameter] public EventCallback<TemplateFieldResponse> OnFilePickRequested { get; set; }
    [Parameter] public IReadOnlyDictionary<PrimitiveType, RenderFragment<FieldEditorRenderContext>>? FieldTemplateOverrides { get; set; }

    private ContentFieldEditorValue GetOrCreateValue(Guid fieldId) =>
        FieldValues.TryGetValue(fieldId, out var value) ? value : new ContentFieldEditorValue();

    private IReadOnlyList<ContentItemSummaryResponse> ReferenceOptionsForField(TemplateFieldResponse field)
    {
        var templateIds = new List<Guid>();
        if (field.TemplateId.HasValue)
        {
            templateIds.Add(field.TemplateId.Value);
        }

        templateIds.AddRange(field.AllowedTypes.Where(a => a.AllowedTemplateId.HasValue).Select(a => a.AllowedTemplateId!.Value));

        return templateIds
            .SelectMany(id => ReferenceOptionsByTemplateId.TryGetValue(id, out var options) ? options : [])
            .GroupBy(option => option.Id)
            .Select(group => group.First())
            .OrderBy(option => option.Slug ?? option.Id.ToString())
            .ToList();
    }
}
```

- [ ] **Step 4: Implement `ContentEditForm.razor.css`**

```css
.cmsify-form-field {
    margin-bottom: var(--cmsify-spacing-md, 0.75rem);
}

.cmsify-form-label {
    display: block;
    margin-bottom: var(--cmsify-spacing-sm, 0.375rem);
    font-family: var(--cmsify-font-family, inherit);
    color: var(--cmsify-color-text, inherit);
}

.cmsify-form-help {
    margin-top: var(--cmsify-spacing-sm, 0.375rem);
    font-size: 0.8rem;
    color: var(--cmsify-color-muted, #6c757d);
}

.cmsify-form-error {
    padding: var(--cmsify-spacing-md, 0.75rem);
    margin-bottom: var(--cmsify-spacing-md, 0.75rem);
    border: 1px solid var(--cmsify-color-danger, #dc3545);
    border-radius: var(--cmsify-radius, 0.375rem);
    color: var(--cmsify-color-danger, #dc3545);
}

.cmsify-form-metadata {
    margin-top: var(--cmsify-spacing-md, 0.75rem);
    padding-top: var(--cmsify-spacing-md, 0.75rem);
    border-top: 1px solid var(--cmsify-color-border, #ced4da);
}

.cmsify-form-save-button {
    margin-top: var(--cmsify-spacing-md, 0.75rem);
    padding: var(--cmsify-spacing-sm, 0.375rem) var(--cmsify-spacing-md, 0.75rem);
    border: 1px solid var(--cmsify-color-border-focus, #0d6efd);
    border-radius: var(--cmsify-radius, 0.375rem);
    background: var(--cmsify-color-border-focus, #0d6efd);
    color: white;
    cursor: pointer;
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj --filter ContentEditFormTests`
Expected: PASS (4 tests)

- [ ] **Step 6: Commit**

```bash
git add src/Cmsify.Components/ContentEditForm.razor src/Cmsify.Components/ContentEditForm.razor.css tests/Cmsify.Components.Tests/ContentEditFormTests.cs
git commit -m "$(cat <<'EOF'
Add presentational ContentEditForm composed component

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3Smg2MFcrDKPuq2WYQEYU
EOF
)"
```

---

## Task 13: `Client.ContentEditPanel` (SDK-backed smart wrapper)

Ports `ContentEditor.razor`'s load/save logic (template resolution, pick-list/reference-option preloading, save payload construction) without the lifecycle/workflow pieces that stay in Admin (see Global Constraints). Creating new content requires a `TemplateId`; editing requires a `ContentId` — mirrors how Admin's own routes distinguish `/content/new` from `/content/{id}`.

**Files:**
- Create: `src/Cmsify.Components/Client/ContentEditPanel.razor`
- Create: `tests/Cmsify.Components.Tests/Client/ContentEditPanelTests.cs`

**Interfaces:**
- Consumes: `ContentEditForm` (Task 12), `MediaPickerPanel` (Task 11), `PickListFieldBinding` (Task 2), `CmsifyClient`'s `Templates`/`Content`/`PickLists`/`Media` clients (`sdk/dotnet/src/SyntaxCircus.Cmsify.Client/CmsifyServices.cs`), `TestCmsifyClientFactory`/`FakeHttpMessageHandler` (Task 11).
- Produces: `SyntaxCircus.Cmsify.Components.Client.ContentEditPanel { [Parameter,EditorRequired] CmsifyClient Client; [Parameter,EditorRequired] Guid WorkspaceId; [Parameter] Guid? ContentId; [Parameter] Guid? TemplateId; [Parameter] EventCallback<ContentItemDetailResponse> Saved; [Parameter] EventCallback<ContentItemDetailResponse> Created; [Parameter] IReadOnlyDictionary<PrimitiveType,RenderFragment<FieldEditorRenderContext>>? FieldTemplateOverrides; }` — consumed by `Cmsify.Admin`'s `ContentEditor.razor` in Task 17.

- [ ] **Step 1: Write the failing test for creating new content**

```csharp
using Bunit;
using Microsoft.AspNetCore.Components;
using Shouldly;
using Xunit;

namespace SyntaxCircus.Cmsify.Components.Tests.Client;

using SyntaxCircus.Cmsify.Components.Tests;

public sealed class ContentEditPanelTests : TestContext
{
    [Fact]
    public void CreatingNewContentLoadsTemplateAndSavesFieldValues()
    {
        var workspaceId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        var templateVersionId = Guid.NewGuid();
        var fieldId = Guid.NewGuid();
        var newContentId = Guid.NewGuid();
        string? capturedCreateBody = null;

        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
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
            if (request.Method == HttpMethod.Post && path == $"/api/v1/workspaces/{workspaceId}/content")
            {
                capturedCreateBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{newContentId}}", "templateVersionId": "{{templateVersionId}}", "templateName": "Article",
                      "status": "Draft", "slug": null, "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z", "publishedAt": null, "fields": [] }
                    """);
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        ContentItemDetailResponse? created = null;
        var cut = RenderComponent<SyntaxCircus.Cmsify.Components.Client.ContentEditPanel>(parameters => parameters
            .Add(p => p.Client, client)
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.TemplateId, templateId)
            .Add(p => p.Created, EventCallback.Factory.Create<ContentItemDetailResponse>(this, c => created = c)));

        cut.Find("input").Input("My Title");
        cut.Find(".cmsify-form-save-button").Click();

        capturedCreateBody.ShouldNotBeNull();
        capturedCreateBody.ShouldContain("My Title");
        created.ShouldNotBeNull();
        created!.Id.ShouldBe(newContentId);
    }

    [Fact]
    public void EditingExistingContentLoadsCurrentValuesAndSavesUpdates()
    {
        var workspaceId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        var templateVersionId = Guid.NewGuid();
        var contentId = Guid.NewGuid();
        var fieldId = Guid.NewGuid();
        string? capturedUpdateBody = null;

        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{contentId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{contentId}}", "templateVersionId": "{{templateVersionId}}", "templateName": "Article",
                      "status": "Draft", "slug": "existing-post", "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z", "publishedAt": null,
                      "fields": [
                        { "fieldId": "{{fieldId}}", "key": "title", "label": "Title", "order": 0, "valueKind": "Text",
                          "textValue": "Existing", "boolValue": null, "mediaAssetId": null, "fileAssetId": null,
                          "childContentItemId": null, "child": null, "jsonValue": null, "displayLabel": null }
                      ]
                    }
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
            if (request.Method == HttpMethod.Put && path == $"/api/v1/workspaces/{workspaceId}/content/{contentId}")
            {
                capturedUpdateBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{contentId}}", "templateVersionId": "{{templateVersionId}}", "templateName": "Article",
                      "status": "Draft", "slug": "existing-post", "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-02T00:00:00Z", "publishedAt": null, "fields": [] }
                    """);
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        ContentItemDetailResponse? saved = null;
        var cut = RenderComponent<SyntaxCircus.Cmsify.Components.Client.ContentEditPanel>(parameters => parameters
            .Add(p => p.Client, client)
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.ContentId, contentId)
            .Add(p => p.Saved, EventCallback.Factory.Create<ContentItemDetailResponse>(this, c => saved = c)));

        cut.Find("input").GetAttribute("value").ShouldBe("Existing");

        cut.Find("input").Input("Updated");
        cut.Find(".cmsify-form-save-button").Click();

        capturedUpdateBody.ShouldNotBeNull();
        capturedUpdateBody.ShouldContain("Updated");
        saved.ShouldNotBeNull();
        saved!.Id.ShouldBe(contentId);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj --filter ContentEditPanelTests`
Expected: FAIL to build — `SyntaxCircus.Cmsify.Components.Client.ContentEditPanel` does not exist yet.

- [ ] **Step 3: Implement `ContentEditPanel.razor`**

```razor
@namespace SyntaxCircus.Cmsify.Components.Client
@using System.Text.Json

<ContentEditForm TemplateVersion="@templateVersion" FieldValues="@fieldValues" FieldValueChanged="OnFieldValueChangedAsync"
                 PickListsByRevisionId="@pickListsByRevisionId" ReferenceOptionsByTemplateId="@referenceOptionsByTemplateId"
                 Slug="@slug" SlugChanged="@(v => slug = v)" Locale="@locale" LocaleChanged="@(v => locale = v)"
                 Tags="@tags" TagsChanged="@(v => tags = v)" Error="@error" OnSave="SaveAsync"
                 OnMediaPickRequested="OpenMediaPickerAsync" OnFilePickRequested="OpenFilePickerAsync"
                 FieldTemplateOverrides="@FieldTemplateOverrides" />

<MediaPickerPanel Client="@Client" WorkspaceId="@WorkspaceId" Visible="@pickerVisible" MimePrefix="@pickerMimePrefix"
                   Selected="OnAssetSelectedAsync" Closed="() => pickerVisible = false" />

@code {
    [Parameter, EditorRequired] public CmsifyClient Client { get; set; } = null!;
    [Parameter, EditorRequired] public Guid WorkspaceId { get; set; }
    [Parameter] public Guid? ContentId { get; set; }
    [Parameter] public Guid? TemplateId { get; set; }
    [Parameter] public EventCallback<ContentItemDetailResponse> Saved { get; set; }
    [Parameter] public EventCallback<ContentItemDetailResponse> Created { get; set; }
    [Parameter] public IReadOnlyDictionary<PrimitiveType, RenderFragment<FieldEditorRenderContext>>? FieldTemplateOverrides { get; set; }

    private TemplateVersionResponse templateVersion = new(Guid.Empty, Guid.Empty, 0, TemplateVersionStatus.Draft, null, null, [], []);
    private ContentItemDetailResponse? item;
    private readonly Dictionary<Guid, ContentFieldEditorValue> fieldValues = [];
    private readonly Dictionary<Guid, PickListResponse> pickListsByRevisionId = [];
    private readonly Dictionary<Guid, IReadOnlyList<ContentItemSummaryResponse>> referenceOptionsByTemplateId = [];
    private string? slug;
    private string? locale;
    private string? tags;
    private string? error;
    private bool pickerVisible;
    private string? pickerMimePrefix;
    private Guid pickerFieldId;
    private bool pickerIsFileField;
    private bool loaded;

    protected override async Task OnParametersSetAsync()
    {
        if (loaded)
        {
            return;
        }

        loaded = true;
        if (ContentId.HasValue)
        {
            await LoadContentAsync();
        }
        else if (TemplateId.HasValue)
        {
            await LoadTemplateVersionAsync(TemplateId.Value);
        }
    }

    private async Task LoadContentAsync()
    {
        var loadedItem = await Client.Content.GetAsync(WorkspaceId, ContentId!.Value)
            ?? throw new InvalidOperationException("Cmsify API returned no payload while loading content.");
        item = loadedItem;
        slug = loadedItem.Slug;
        locale = loadedItem.LocaleCode;
        tags = string.Join(",", loadedItem.Tags);

        var templates = (await Client.Templates.ListAsync(WorkspaceId))?.Items ?? [];
        var template = templates.FirstOrDefault(t => t.CurrentVersionId == loadedItem.TemplateVersionId);
        if (template is not null)
        {
            await LoadTemplateVersionAsync(template.Id);
        }

        fieldValues.Clear();
        foreach (var value in loadedItem.Fields)
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
                editorValue.SelectedMediaAsset = await Client.Media.GetAsync(WorkspaceId, value.MediaAssetId.Value);
            }
            if (value.FileAssetId.HasValue)
            {
                editorValue.SelectedFileAsset = await Client.Media.GetAsync(WorkspaceId, value.FileAssetId.Value);
            }
        }
    }

    private ContentFieldEditorValue GetOrAddValue(Guid fieldId)
    {
        if (!fieldValues.TryGetValue(fieldId, out var value))
        {
            value = new ContentFieldEditorValue();
            fieldValues[fieldId] = value;
        }

        return value;
    }

    private async Task LoadTemplateVersionAsync(Guid templateId)
    {
        var template = await Client.Templates.GetAsync(WorkspaceId, templateId)
            ?? throw new InvalidOperationException("Cmsify API returned no payload while loading a template.");
        templateVersion = template.CurrentVersion
            ?? await Client.Templates.CreateDraftAsync(WorkspaceId, templateId, "Content draft version")
            ?? throw new InvalidOperationException("Cmsify API returned no payload while creating a template draft.");

        await LoadPickListsAsync();
        await LoadReferenceOptionsAsync();
    }

    private async Task LoadPickListsAsync()
    {
        var bindings = templateVersion.Fields
            .Where(field => field.PrimitiveType == PrimitiveType.PickList)
            .Select(field => PickListFieldBinding.FromFieldConfig(field.FieldConfig))
            .Where(binding => binding.PickListId.HasValue && binding.RevisionId.HasValue)
            .Select(binding => (binding.PickListId!.Value, binding.RevisionId!.Value))
            .Distinct()
            .ToList();

        foreach (var (pickListId, revisionId) in bindings)
        {
            if (pickListsByRevisionId.ContainsKey(revisionId))
            {
                continue;
            }

            var pickList = await Client.PickLists.GetRevisionAsync(WorkspaceId, pickListId, revisionId);
            if (pickList is not null)
            {
                pickListsByRevisionId[revisionId] = pickList;
            }
        }
    }

    private async Task LoadReferenceOptionsAsync()
    {
        var templateIds = templateVersion.Fields
            .SelectMany(field => field.AllowedTypes.Where(a => a.AllowedTemplateId.HasValue).Select(a => a.AllowedTemplateId!.Value)
                .Concat(field.TemplateId.HasValue ? [field.TemplateId.Value] : []))
            .Distinct()
            .ToList();

        foreach (var templateId in templateIds)
        {
            if (referenceOptionsByTemplateId.ContainsKey(templateId))
            {
                continue;
            }

            var page = await Client.Content.ListAsync(WorkspaceId, null, templateId, null, null, null);
            referenceOptionsByTemplateId[templateId] = page?.Items ?? [];
        }
    }

    private Task OnFieldValueChangedAsync((TemplateFieldResponse Field, ContentFieldEditorValue Value) change)
    {
        fieldValues[change.Field.Id] = change.Value;
        return Task.CompletedTask;
    }

    private Task OpenMediaPickerAsync(TemplateFieldResponse field)
    {
        pickerFieldId = field.Id;
        pickerIsFileField = false;
        pickerMimePrefix = "image/";
        pickerVisible = true;
        return Task.CompletedTask;
    }

    private Task OpenFilePickerAsync(TemplateFieldResponse field)
    {
        pickerFieldId = field.Id;
        pickerIsFileField = true;
        pickerMimePrefix = null;
        pickerVisible = true;
        return Task.CompletedTask;
    }

    private Task OnAssetSelectedAsync(MediaAssetResponse asset)
    {
        var value = GetOrAddValue(pickerFieldId);
        if (pickerIsFileField)
        {
            value.SelectedFileAsset = asset;
        }
        else
        {
            value.SelectedMediaAsset = asset;
        }

        pickerVisible = false;
        return Task.CompletedTask;
    }

    private async Task SaveAsync()
    {
        var values = new List<ContentFieldValueRequest>();
        foreach (var field in templateVersion.Fields.OrderBy(f => f.Order))
        {
            var value = GetOrAddValue(field.Id);

            if (field.ComponentId.HasValue)
            {
                foreach (var raw in value.ComponentValues)
                {
                    using var document = JsonDocument.Parse(raw);
                    values.Add(new ContentFieldValueRequest(field.Id, field.Order, ValueKind.Component, null, null, null, null, null, document.RootElement.Clone()));
                }
                continue;
            }

            var isComposition = field.TemplateId.HasValue || field.IsOpen || field.AllowedTypes.Any(a => a.AllowedTemplateId.HasValue);
            if (isComposition)
            {
                if (value.ChildContentItemId is { } referencedId)
                {
                    values.Add(new ContentFieldValueRequest(field.Id, field.Order, ValueKind.ChildContent, null, null, null, null, referencedId, null));
                }
                continue;
            }

            if (field.PrimitiveType == PrimitiveType.PickList)
            {
                var binding = PickListFieldBinding.FromFieldConfig(field.FieldConfig);
                if (binding.Multiple)
                {
                    var index = 0;
                    foreach (var selectedValue in value.MultiValues)
                    {
                        values.Add(new ContentFieldValueRequest(field.Id, field.Order + index, ValueKind.PickList, selectedValue, null, null, null, null, null));
                        index++;
                    }
                }
                else if (!string.IsNullOrWhiteSpace(value.TextValue))
                {
                    values.Add(new ContentFieldValueRequest(field.Id, field.Order, ValueKind.PickList, value.TextValue, null, null, null, null, null));
                }
                continue;
            }

            values.Add(new ContentFieldValueRequest(
                field.Id,
                field.Order,
                field.PrimitiveType switch
                {
                    PrimitiveType.Boolean => ValueKind.Boolean,
                    PrimitiveType.Media => ValueKind.Media,
                    PrimitiveType.File => ValueKind.File,
                    PrimitiveType.Markdown => ValueKind.Markdown,
                    PrimitiveType.RichText => ValueKind.RichText,
                    PrimitiveType.Link => ValueKind.Link,
                    PrimitiveType.Quote => ValueKind.Quote,
                    PrimitiveType.Separator => ValueKind.Separator,
                    _ => ValueKind.Text
                },
                value.TextValue,
                value.BoolValue,
                field.PrimitiveType == PrimitiveType.Media ? value.SelectedMediaAsset?.Id : null,
                field.PrimitiveType == PrimitiveType.File ? value.SelectedFileAsset?.Id : null,
                null,
                null));
        }

        var tagList = (tags ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        try
        {
            error = null;
            if (ContentId.HasValue)
            {
                var updated = await Client.Content.UpdateAsync(WorkspaceId, ContentId.Value,
                    new UpdateContentItemRequest(string.IsNullOrWhiteSpace(slug) ? null : slug, locale, item?.TranslationGroupId, null, tagList, values))
                    ?? throw new InvalidOperationException("Cmsify API returned no payload while updating content.");
                item = updated;
                await Saved.InvokeAsync(updated);
            }
            else
            {
                var createdItem = await Client.Content.CreateAsync(WorkspaceId,
                    new CreateContentItemRequest(templateVersion.Id, string.IsNullOrWhiteSpace(slug) ? null : slug, locale, null, tagList, values))
                    ?? throw new InvalidOperationException("Cmsify API returned no payload while creating content.");
                item = createdItem;
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
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj --filter ContentEditPanelTests`
Expected: PASS (2 tests)

- [ ] **Step 5: Commit**

```bash
git add src/Cmsify.Components/Client/ContentEditPanel.razor tests/Cmsify.Components.Tests/Client/ContentEditPanelTests.cs
git commit -m "$(cat <<'EOF'
Add SDK-backed ContentEditPanel wiring load, pick lists, and save

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3Smg2MFcrDKPuq2WYQEYU
EOF
)"
```

---

## Task 14: `ContentListView` (presentational)

**Files:**
- Create: `src/Cmsify.Components/ContentListView.razor` + `.razor.css`
- Create: `tests/Cmsify.Components.Tests/ContentListViewTests.cs`

**Interfaces:**
- Consumes: `ContentItemSummaryResponse`/`TemplateSummaryResponse`/`ContentStatus` from `Cmsify.Contracts`.
- Produces: `ContentListView { [Parameter] IReadOnlyList<ContentItemSummaryResponse> Items; [Parameter] IReadOnlyList<TemplateSummaryResponse> Templates; [Parameter] string? SearchText; [Parameter] EventCallback<string?> SearchTextChanged; [Parameter] ContentStatus? Status; [Parameter] EventCallback<ContentStatus?> StatusChanged; [Parameter] Guid? TemplateId; [Parameter] EventCallback<Guid?> TemplateIdChanged; [Parameter] string? Locale; [Parameter] EventCallback<string?> LocaleChanged; [Parameter] EventCallback OnFilter; [Parameter] EventCallback<ContentItemSummaryResponse> OnEdit; }` — consumed by `Client.ContentListPanel` in Task 15.

- [ ] **Step 1: Write the failing tests**

```csharp
using Bunit;
using Microsoft.AspNetCore.Components;
using Shouldly;
using Xunit;

namespace SyntaxCircus.Cmsify.Components.Tests;

public sealed class ContentListViewTests : TestContext
{
    private static ContentItemSummaryResponse CreateItem(string slug) => new(
        Guid.NewGuid(), Guid.NewGuid(), "Article", ContentStatus.Draft, slug, null, null, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);

    [Fact]
    public void RendersOneRowPerItem()
    {
        var items = new[] { CreateItem("first"), CreateItem("second") };

        var cut = RenderComponent<ContentListView>(parameters => parameters.Add(p => p.Items, items));

        cut.FindAll("tbody tr").Count.ShouldBe(2);
    }

    [Fact]
    public void RaisesOnEditWhenEditButtonClicked()
    {
        var item = CreateItem("first");
        ContentItemSummaryResponse? edited = null;
        var cut = RenderComponent<ContentListView>(parameters => parameters
            .Add(p => p.Items, new[] { item })
            .Add(p => p.OnEdit, EventCallback.Factory.Create<ContentItemSummaryResponse>(this, i => edited = i)));

        cut.Find(".cmsify-list-edit-button").Click();

        edited.ShouldBe(item);
    }

    [Fact]
    public void RaisesOnFilterFromFilterButton()
    {
        var filtered = false;
        var cut = RenderComponent<ContentListView>(parameters => parameters
            .Add(p => p.OnFilter, EventCallback.Factory.Create(this, () => filtered = true)));

        cut.Find(".cmsify-list-filter-button").Click();

        filtered.ShouldBeTrue();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj --filter ContentListViewTests`
Expected: FAIL to build — `ContentListView` does not exist yet.

- [ ] **Step 3: Implement `ContentListView.razor`**

```razor
@namespace SyntaxCircus.Cmsify.Components

<div class="cmsify-list">
    <div class="cmsify-list-filters">
        <input class="cmsify-field-input" placeholder="Full-text search" value="@SearchText" @oninput="e => SearchTextChanged.InvokeAsync(e.Value?.ToString())" />
        <select class="cmsify-field-select" value="@Status?.ToString()" @onchange="OnStatusChanged">
            <option value="">Any status</option>
            @foreach (var status in Enum.GetValues<ContentStatus>())
            {
                <option value="@status">@status</option>
            }
        </select>
        <select class="cmsify-field-select" value="@TemplateId?.ToString()" @onchange="OnTemplateChanged">
            <option value="">Any template</option>
            @foreach (var template in Templates)
            {
                <option value="@template.Id">@template.Name</option>
            }
        </select>
        <input class="cmsify-field-input" placeholder="Locale" value="@Locale" @oninput="e => LocaleChanged.InvokeAsync(e.Value?.ToString())" />
        <button type="button" class="cmsify-list-filter-button" @onclick="() => OnFilter.InvokeAsync()">Filter</button>
    </div>
    <table class="cmsify-list-table">
        <thead>
            <tr><th>Title / Slug</th><th>Template</th><th>Status</th><th>Locale</th><th>Updated</th><th></th></tr>
        </thead>
        <tbody>
            @foreach (var contentItem in Items)
            {
                <tr>
                    <td>@(contentItem.Slug ?? contentItem.Id.ToString())</td>
                    <td>@contentItem.TemplateName</td>
                    <td>@contentItem.Status</td>
                    <td>@contentItem.LocaleCode</td>
                    <td>@contentItem.UpdatedAt</td>
                    <td><button type="button" class="cmsify-list-edit-button" @onclick="() => OnEdit.InvokeAsync(contentItem)">Edit</button></td>
                </tr>
            }
        </tbody>
    </table>
</div>

@code {
    [Parameter] public IReadOnlyList<ContentItemSummaryResponse> Items { get; set; } = [];
    [Parameter] public IReadOnlyList<TemplateSummaryResponse> Templates { get; set; } = [];
    [Parameter] public string? SearchText { get; set; }
    [Parameter] public EventCallback<string?> SearchTextChanged { get; set; }
    [Parameter] public ContentStatus? Status { get; set; }
    [Parameter] public EventCallback<ContentStatus?> StatusChanged { get; set; }
    [Parameter] public Guid? TemplateId { get; set; }
    [Parameter] public EventCallback<Guid?> TemplateIdChanged { get; set; }
    [Parameter] public string? Locale { get; set; }
    [Parameter] public EventCallback<string?> LocaleChanged { get; set; }
    [Parameter] public EventCallback OnFilter { get; set; }
    [Parameter] public EventCallback<ContentItemSummaryResponse> OnEdit { get; set; }

    private Task OnStatusChanged(ChangeEventArgs e) =>
        StatusChanged.InvokeAsync(Enum.TryParse<ContentStatus>(e.Value?.ToString(), out var status) ? status : null);

    private Task OnTemplateChanged(ChangeEventArgs e) =>
        TemplateIdChanged.InvokeAsync(Guid.TryParse(e.Value?.ToString(), out var id) ? id : null);
}
```

- [ ] **Step 4: Implement `ContentListView.razor.css`**

```css
.cmsify-list-filters {
    display: flex;
    flex-wrap: wrap;
    gap: var(--cmsify-spacing-sm, 0.375rem);
    margin-bottom: var(--cmsify-spacing-md, 0.75rem);
}

.cmsify-list-filters > * {
    flex: 1 1 160px;
}

.cmsify-list-table {
    width: 100%;
    border-collapse: collapse;
}

.cmsify-list-table th, .cmsify-list-table td {
    padding: var(--cmsify-spacing-sm, 0.375rem) var(--cmsify-spacing-md, 0.75rem);
    border-bottom: 1px solid var(--cmsify-color-border, #ced4da);
    text-align: left;
}

.cmsify-list-filter-button, .cmsify-list-edit-button {
    padding: var(--cmsify-spacing-sm, 0.375rem) var(--cmsify-spacing-md, 0.75rem);
    border: 1px solid var(--cmsify-color-border, #ced4da);
    border-radius: var(--cmsify-radius, 0.375rem);
    background: transparent;
    color: var(--cmsify-color-text, inherit);
    cursor: pointer;
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj --filter ContentListViewTests`
Expected: PASS (3 tests)

- [ ] **Step 6: Commit**

```bash
git add src/Cmsify.Components/ContentListView.razor src/Cmsify.Components/ContentListView.razor.css tests/Cmsify.Components.Tests/ContentListViewTests.cs
git commit -m "$(cat <<'EOF'
Add presentational ContentListView composed component

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3Smg2MFcrDKPuq2WYQEYU
EOF
)"
```

---

## Task 15: `Client.ContentListPanel` (SDK-backed smart wrapper)

**Files:**
- Create: `src/Cmsify.Components/Client/ContentListPanel.razor`
- Create: `tests/Cmsify.Components.Tests/Client/ContentListPanelTests.cs`

**Interfaces:**
- Consumes: `ContentListView` (Task 14), `CmsifyClient`'s `Templates`/`Content` clients, `TestCmsifyClientFactory`/`FakeHttpMessageHandler` (Task 11).
- Produces: `SyntaxCircus.Cmsify.Components.Client.ContentListPanel { [Parameter,EditorRequired] CmsifyClient Client; [Parameter,EditorRequired] Guid WorkspaceId; [Parameter] EventCallback<ContentItemSummaryResponse> OnEdit; }` — consumed by `Cmsify.Admin`'s `ContentList.razor` in Task 18.

- [ ] **Step 1: Write the failing test**

```csharp
using Bunit;
using Microsoft.AspNetCore.Components;
using Shouldly;
using Xunit;

namespace SyntaxCircus.Cmsify.Components.Tests.Client;

using SyntaxCircus.Cmsify.Components.Tests;

public sealed class ContentListPanelTests : TestContext
{
    [Fact]
    public void LoadsTemplatesAndContentOnInitAndRaisesOnEdit()
    {
        var workspaceId = Guid.NewGuid();
        var contentId = Guid.NewGuid();

        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == $"/api/v1/workspaces/{workspaceId}/templates")
            {
                return FakeHttpMessageHandler.Json("""{ "items": [], "totalCount": 0, "page": 1, "pageSize": 20 }""");
            }
            if (path == $"/api/v1/workspaces/{workspaceId}/content")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "items": [{ "id": "{{contentId}}", "templateVersionId": "{{Guid.NewGuid()}}", "templateName": "Article",
                      "status": "Draft", "slug": "first-post", "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z", "publishedAt": null }],
                      "totalCount": 1, "page": 1, "pageSize": 20 }
                    """);
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        ContentItemSummaryResponse? edited = null;
        var cut = RenderComponent<SyntaxCircus.Cmsify.Components.Client.ContentListPanel>(parameters => parameters
            .Add(p => p.Client, client)
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.OnEdit, EventCallback.Factory.Create<ContentItemSummaryResponse>(this, i => edited = i)));

        cut.Find(".cmsify-list-edit-button").Click();

        edited.ShouldNotBeNull();
        edited!.Id.ShouldBe(contentId);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj --filter ContentListPanelTests`
Expected: FAIL to build — `SyntaxCircus.Cmsify.Components.Client.ContentListPanel` does not exist yet.

- [ ] **Step 3: Implement `ContentListPanel.razor`**

```razor
@namespace SyntaxCircus.Cmsify.Components.Client

<ContentListView Items="@items" Templates="@templates" SearchText="@searchText" SearchTextChanged="@(v => searchText = v)"
                 Status="@status" StatusChanged="@(v => status = v)" TemplateId="@templateId" TemplateIdChanged="@(v => templateId = v)"
                 Locale="@locale" LocaleChanged="@(v => locale = v)" OnFilter="LoadAsync" OnEdit="@(item => OnEdit.InvokeAsync(item))" />

@code {
    [Parameter, EditorRequired] public CmsifyClient Client { get; set; } = null!;
    [Parameter, EditorRequired] public Guid WorkspaceId { get; set; }
    [Parameter] public EventCallback<ContentItemSummaryResponse> OnEdit { get; set; }

    private List<ContentItemSummaryResponse> items = [];
    private IReadOnlyList<TemplateSummaryResponse> templates = [];
    private string? searchText;
    private ContentStatus? status;
    private Guid? templateId;
    private string? locale;
    private bool loaded;

    protected override async Task OnParametersSetAsync()
    {
        if (loaded)
        {
            return;
        }

        loaded = true;
        var templatePage = await Client.Templates.ListAsync(WorkspaceId);
        templates = templatePage?.Items ?? [];
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var page = await Client.Content.ListAsync(WorkspaceId, status, templateId, locale, null, searchText);
        items = (page?.Items ?? []).ToList();
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj --filter ContentListPanelTests`
Expected: PASS

- [ ] **Step 5: Run the full test project to confirm no regressions**

Run: `dotnet test tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj`
Expected: PASS (every test from Tasks 1–15)

- [ ] **Step 6: Commit**

```bash
git add src/Cmsify.Components/Client/ContentListPanel.razor tests/Cmsify.Components.Tests/Client/ContentListPanelTests.cs
git commit -m "$(cat <<'EOF'
Add SDK-backed ContentListPanel

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3Smg2MFcrDKPuq2WYQEYU
EOF
)"
```

---

## Task 16: `SyntaxCircus.Cmsify.Components.Theme` package

A static-asset-only companion package: default values for every `--cmsify-*` variable, Bootstrap-flavored to match Admin's current `cms-*` look (`src/Cmsify.Admin/wwwroot/scss/_variables.scss`), plus a couple of small utility classes CSS variables can't express. No components, no code.

**Files:**
- Create: `src/Cmsify.Components.Theme/Cmsify.Components.Theme.csproj`
- Create: `src/Cmsify.Components.Theme/README.md`
- Create: `src/Cmsify.Components.Theme/wwwroot/cmsify-theme.css`
- Modify: `Cmsify.slnx`

**Interfaces:**
- Consumes: the `--cmsify-*` variable vocabulary from Task 1's README and every component's `.razor.css` fallback values (Tasks 3–14).
- Produces: a static web asset served at `_content/SyntaxCircus.Cmsify.Components.Theme/cmsify-theme.css` once referenced by a host app — consumed by `Cmsify.Admin` in Task 17.

- [ ] **Step 1: Create the project file**

```xml
<Project Sdk="Microsoft.NET.Sdk.Razor">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PackageId>SyntaxCircus.Cmsify.Components.Theme</PackageId>
    <Title>Syntax Circus LLC Cmsify Components Theme</Title>
    <Description>Optional default Bootstrap-flavored theme (CSS custom property values) for SyntaxCircus.Cmsify.Components. Purely static assets — no components, no code.</Description>
    <Authors>Syntax Circus LLC</Authors>
    <Company>Syntax Circus LLC</Company>
    <Copyright>Copyright © Syntax Circus LLC</Copyright>
    <PackageTags>cms;headless-cms;cmsify;blazor;components;theme</PackageTags>
    <PackageRequireLicenseAcceptance>false</PackageRequireLicenseAcceptance>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
    <PackageReadmeFile>README.md</PackageReadmeFile>
    <PackageProjectUrl>https://github.com/Syntax-Circus/cmsify</PackageProjectUrl>
    <RepositoryUrl>https://github.com/Syntax-Circus/cmsify</RepositoryUrl>
    <RepositoryType>git</RepositoryType>
    <PublishRepositoryUrl>true</PublishRepositoryUrl>
    <IncludeSymbols>true</IncludeSymbols>
    <SymbolPackageFormat>snupkg</SymbolPackageFormat>
  </PropertyGroup>
  <ItemGroup>
    <None Include="README.md" Pack="true" PackagePath="\" />
    <None Include="..\..\LICENSE-MIT.txt" Pack="true" PackagePath="\" Link="LICENSE-MIT.txt" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create `wwwroot/cmsify-theme.css`**

Values below match Admin's actual brand SCSS (`src/Cmsify.Admin/wwwroot/scss/_variables.scss`: `$primary: #6D28D9`, `$danger: #B91C1C`, `$font-family-base: system-ui, -apple-system, "Segoe UI", sans-serif`); border/muted/radius keep Bootstrap's own defaults since Admin doesn't override those:

```css
:root {
    --cmsify-color-text: #212529;
    --cmsify-color-border: #dee2e6;
    --cmsify-color-border-focus: #6D28D9;
    --cmsify-color-danger: #B91C1C;
    --cmsify-color-muted: #6c757d;
    --cmsify-radius: 0.375rem;
    --cmsify-spacing-sm: 0.375rem;
    --cmsify-spacing-md: 0.75rem;
    --cmsify-font-family: system-ui, -apple-system, "Segoe UI", sans-serif;
    --cmsify-focus-ring: 0 0 0 0.2rem rgba(109, 40, 217, 0.25);
}
```

- [ ] **Step 3: Create the README**

```markdown
# SyntaxCircus.Cmsify.Components.Theme

Default Bootstrap-flavored values for the `--cmsify-*` CSS custom properties used by `SyntaxCircus.Cmsify.Components`. Purely static assets — installing this package adds no components or code.

## Usage

Reference the stylesheet from your host page (e.g. in `App.razor`'s `<head>`):

```html
<link rel="stylesheet" href="_content/SyntaxCircus.Cmsify.Components.Theme/cmsify-theme.css" />
```

Omit this package entirely if you want to set the `--cmsify-*` variables yourself to match your own design system.
```

- [ ] **Step 4: Add the project to the solution**

Edit `Cmsify.slnx`, adding after the `Cmsify.Components` project line:

```xml
  <Project Path="src\Cmsify.Components.Theme\Cmsify.Components.Theme.csproj" />
```

- [ ] **Step 5: Build to verify the project compiles and packs its static asset**

Run: `dotnet build src/Cmsify.Components.Theme/Cmsify.Components.Theme.csproj`
Expected: builds cleanly with no components (Razor SDK is used only for its static-web-assets pipeline).

- [ ] **Step 6: Commit**

```bash
git add src/Cmsify.Components.Theme/Cmsify.Components.Theme.csproj src/Cmsify.Components.Theme/README.md src/Cmsify.Components.Theme/wwwroot/cmsify-theme.css Cmsify.slnx
git commit -m "$(cat <<'EOF'
Add SyntaxCircus.Cmsify.Components.Theme default styling package

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3Smg2MFcrDKPuq2WYQEYU
EOF
)"
```

---

## Task 17: Extend `ContentEditPanel` with an `ItemChanged` callback

Admin's thin host (Task 18) still owns the lifecycle/workflow sidebar (Submit/Approve/Reject/Publish/Archive/Restore), which needs the current `ContentItemDetailResponse` — including its `Status` — to compute button states via the existing `ContentWorkflowActions` helper. `ContentEditPanel` currently keeps `item` private; this task exposes it via a callback fired whenever it changes (after load and after a successful save), without changing any existing parameter.

**Files:**
- Modify: `src/Cmsify.Components/Client/ContentEditPanel.razor`
- Modify: `tests/Cmsify.Components.Tests/Client/ContentEditPanelTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: adds `[Parameter] public EventCallback<ContentItemDetailResponse?> ItemChanged { get; set; }` to `ContentEditPanel`, invoked after every assignment to its internal `item` field — consumed by `Cmsify.Admin`'s `ContentEditor.razor` in Task 18.

- [ ] **Step 1: Write the failing test**

Add this test method to `ContentEditPanelTests`:

```csharp
    [Fact]
    public void RaisesItemChangedAfterLoadingExistingContent()
    {
        var workspaceId = Guid.NewGuid();
        var contentId = Guid.NewGuid();
        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == $"/api/v1/workspaces/{workspaceId}/content/{contentId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{contentId}}", "templateVersionId": "{{Guid.NewGuid()}}", "templateName": "Article",
                      "status": "Draft", "slug": "existing-post", "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z", "publishedAt": null, "fields": [] }
                    """);
            }
            if (path == $"/api/v1/workspaces/{workspaceId}/templates")
            {
                return FakeHttpMessageHandler.Json("""{ "items": [], "totalCount": 0, "page": 1, "pageSize": 20 }""");
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        ContentItemDetailResponse? changed = null;
        RenderComponent<SyntaxCircus.Cmsify.Components.Client.ContentEditPanel>(parameters => parameters
            .Add(p => p.Client, client)
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.ContentId, contentId)
            .Add(p => p.ItemChanged, EventCallback.Factory.Create<ContentItemDetailResponse?>(this, i => changed = i)));

        changed.ShouldNotBeNull();
        changed!.Id.ShouldBe(contentId);
        changed.Status.ShouldBe(ContentStatus.Draft);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj --filter RaisesItemChangedAfterLoadingExistingContent`
Expected: FAIL — `ItemChanged` does not exist on `ContentEditPanel` yet.

- [ ] **Step 3: Add the parameter and invoke it after every `item` assignment**

In `src/Cmsify.Components/Client/ContentEditPanel.razor`, add the parameter next to the other `[Parameter]` declarations:

```csharp
    [Parameter] public EventCallback<ContentItemDetailResponse?> ItemChanged { get; set; }
```

Then invoke it right after each of the three places `item` is assigned. In `LoadContentAsync`, after `item = loadedItem;`:

```csharp
        item = loadedItem;
        await ItemChanged.InvokeAsync(item);
```

In `SaveAsync`'s update branch, after `item = updated;`:

```csharp
                item = updated;
                await ItemChanged.InvokeAsync(item);
                await Saved.InvokeAsync(updated);
```

In `SaveAsync`'s create branch, after `item = createdItem;`:

```csharp
                item = createdItem;
                await ItemChanged.InvokeAsync(item);
                await Created.InvokeAsync(createdItem);
```

- [ ] **Step 4: Run the full `ContentEditPanelTests` class to verify all tests pass**

Run: `dotnet test tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj --filter ContentEditPanelTests`
Expected: PASS (3 tests)

- [ ] **Step 5: Commit**

```bash
git add src/Cmsify.Components/Client/ContentEditPanel.razor tests/Cmsify.Components.Tests/Client/ContentEditPanelTests.cs
git commit -m "$(cat <<'EOF'
Expose current content item from ContentEditPanel via ItemChanged

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3Smg2MFcrDKPuq2WYQEYU
EOF
)"
```

---

## Task 18: Migrate `Cmsify.Admin`'s `ContentEditor.razor` to host `ContentEditPanel`

Deliberate simplifications versus today's page (acceptable per the spec's "dogfooding proof point, not pixel-perfect" allowance): the page-head "Save Draft" button is dropped in favor of `ContentEditForm`'s own Save button at the bottom of the panel; the error banner moves from a page-level field into the panel's own `ContentEditForm`; status-changing lifecycle actions (Submit/Approve/Reject) now update `item` directly from the transition endpoint's response instead of doing a full page-level reload. Lifecycle sidebar, publish dialog, and translation-linking stay in Admin, driven by the `item` state `ContentEditPanel` now reports via `ItemChanged` (Task 17).

**Files:**
- Modify: `src/Cmsify.Admin/Components/Pages/Content/ContentEditor.razor`
- Modify: `src/Cmsify.Admin/Cmsify.Admin.csproj`
- Modify: `src/Cmsify.Admin/Components/App.razor`

**Interfaces:**
- Consumes: `SyntaxCircus.Cmsify.Components.Client.ContentEditPanel` (Tasks 13, 17).
- Produces: nothing new (leaf consumer).

- [ ] **Step 1: Do not touch `Directory.Packages.props` for this**

`Directory.Packages.props` centrally versions only third-party and external `SyntaxCircus.*` NuGet dependencies — in-repo `ProjectReference`s (like `Cmsify.Contracts` and `SyntaxCircus.Cmsify.Client`, already referenced by Admin) never appear there. `SyntaxCircus.Cmsify.Components` and `.Theme` are in-repo `ProjectReference`s too (Step 2), so this file needs no change. This step exists only to record that the omission is deliberate, not an oversight — proceed to Step 2.

- [ ] **Step 2: Add `ProjectReference`s to `Cmsify.Admin.csproj`**

```xml
    <ProjectReference Include="..\Cmsify.Components\Cmsify.Components.csproj" />
    <ProjectReference Include="..\Cmsify.Components.Theme\Cmsify.Components.Theme.csproj" />
```

placed in the existing `<ItemGroup>` alongside the `Cmsify.Contracts`/`Cmsify.Core`/`SyntaxCircus.Cmsify.Client` references.

- [ ] **Step 3: Reference the theme stylesheet from Admin's host page**

In `src/Cmsify.Admin/Components/App.razor`, add immediately after the existing `<link rel="stylesheet" href="@Assets["css/app.css"]" />` line:

```html
<link rel="stylesheet" href="_content/SyntaxCircus.Cmsify.Components.Theme/cmsify-theme.css" />
```

- [ ] **Step 4: Rewrite `ContentEditor.razor`**

```razor
@page "/workspaces/{WorkspaceId:guid}/content/new"
@page "/workspaces/{WorkspaceId:guid}/content/{ContentId:guid}"
@rendermode InteractiveServer
@using SyntaxCircus.Cmsify.Components.Client
@inject CmsifyClient Cmsify
@inject ToastState Toasts
@inject NavigationManager Navigation
@inject AuthState Auth

<div class="cms-page-head">
    <div><h1>@(ContentId.HasValue ? "Edit Content" : "New Content")</h1><div class="lead">Dynamic form generated from the selected template version.</div></div>
    @if (ContentId.HasValue)
    {
        <a class="btn btn-outline-secondary" href="/workspaces/@WorkspaceId/content/@ContentId/versions">Versions</a>
    }
</div>

<div class="row g-3">
    <div class="col-lg-8">
        <div class="cms-card">
            @if (!ContentId.HasValue)
            {
                <label class="form-label">Template</label>
                <select class="form-select mb-3" @onchange="OnTemplateSelected">
                    <option value="">Select template</option>
                    @foreach (var template in templates)
                    {
                        <option value="@template.Id">@template.Name</option>
                    }
                </select>
            }
            @if (ContentId.HasValue || selectedTemplateId.HasValue)
            {
                <ContentEditPanel @key="@(ContentId ?? selectedTemplateId)" Client="@Cmsify" WorkspaceId="@WorkspaceId" ContentId="@ContentId"
                                  TemplateId="@(ContentId.HasValue ? null : selectedTemplateId)"
                                  ItemChanged="@(i => item = i)" Saved="OnSavedAsync" Created="OnCreatedAsync" />
            }
        </div>
    </div>
    <div class="col-lg-4">
        <div class="cms-card">
            <h2 class="h6">Lifecycle</h2>
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
            }
            <label class="form-label mt-3">Link Translation Target ID</label>
            <InputText class="form-control mb-3" @bind-Value="translationTarget" />
            <button class="btn btn-outline-secondary btn-sm" @onclick="LinkTranslationAsync" disabled="@(!ContentId.HasValue)">Link Translation</button>
        </div>
    </div>
</div>

@if (item is not null)
{
    <PublishContentDialog WorkspaceId="@WorkspaceId" ContentId="@publishContentId" ContentStatus="@item.Status"
                          CanOverrideWorkflow="@(CurrentRole >= UserRole.Admin)" Cmsify="@Cmsify"
                          OnClosed="ClosePublishDialog" OnPublished="OnPublishedAsync" />
}

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

    protected override async Task OnParametersSetAsync()
    {
        if (!ContentId.HasValue)
        {
            templates = ApiResponse.ItemsOrEmpty(await Templates.ListAsync(WorkspaceId));
        }
    }

    private void OnTemplateSelected(ChangeEventArgs e) =>
        selectedTemplateId = Guid.TryParse(e.Value?.ToString(), out var id) ? id : null;

    private async Task RunTransitionAsync(string action)
    {
        if (!ContentId.HasValue)
        {
            return;
        }

        try
        {
            item = await Content.TransitionAsync(WorkspaceId, ContentId.Value, action);
            Toasts.Success("Content updated.");
        }
        catch (Exception ex)
        {
            Toasts.Danger(ex.Message);
        }
    }

    private async Task RunRejectAsync()
    {
        if (!ContentId.HasValue)
        {
            return;
        }

        try
        {
            item = await Content.RejectAsync(WorkspaceId, ContentId.Value, new RejectContentRequest("Rejected from content editor"));
            Toasts.Success("Content updated.");
        }
        catch (Exception ex)
        {
            Toasts.Danger(ex.Message);
        }
    }

    private void OpenPublishDialog() => publishContentId = ContentId;

    private void ClosePublishDialog() => publishContentId = null;

    private Task OnPublishedAsync(PublishContentResponse response)
    {
        item = response.Content;
        Toasts.Success(response.Warnings.Count == 0 ? "Content published." : "Content published with warnings.");
        return Task.CompletedTask;
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

- [ ] **Step 5: Build Admin to confirm it compiles**

Run: `dotnet build src/Cmsify.Admin/Cmsify.Admin.csproj`
Expected: builds cleanly. If `MediaPickerModal.razor`/`ContentEditor.razor`'s old dictionaries or `RenderField` are still referenced anywhere else in Admin (they should not be — this file was the only consumer), the build will point at exactly what to remove.

- [ ] **Step 6: Run the existing Admin integration tests**

Run: `dotnet test tests/Cmsify.Admin.Integration.Tests/Cmsify.Admin.Integration.Tests.csproj`
Expected: PASS. If any test asserted on old markup (e.g. a `"Save Draft"` button text or old CSS classes), update that specific assertion to match the new markup — do not weaken the assertion's intent, just its literal target.

- [ ] **Step 7: Commit**

```bash
git add src/Cmsify.Admin/Components/Pages/Content/ContentEditor.razor src/Cmsify.Admin/Cmsify.Admin.csproj src/Cmsify.Admin/Components/App.razor
git commit -m "$(cat <<'EOF'
Migrate Admin ContentEditor to host the reusable ContentEditPanel

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3Smg2MFcrDKPuq2WYQEYU
EOF
)"
```

---

## Task 19: Extend `ContentListView`/`ContentListPanel` with row actions and reload, then migrate `ContentList.razor`

`ContentList.razor` renders lifecycle action buttons (Submit/Approve/Reject/Publish/Archive/Restore) per row — these stay in Admin (see Global Constraints), but they render inside the same table the reusable `ContentListView` owns, and after running one, Admin needs the row's status to refresh. This task adds a `RowActions` extension point (mirroring `FieldEditor`'s override hook) and a `ReloadAsync()` escape hatch, then migrates the page.

**Files:**
- Modify: `src/Cmsify.Components/ContentListView.razor`
- Modify: `tests/Cmsify.Components.Tests/ContentListViewTests.cs`
- Modify: `src/Cmsify.Components/Client/ContentListPanel.razor`
- Modify: `tests/Cmsify.Components.Tests/Client/ContentListPanelTests.cs`
- Modify: `src/Cmsify.Admin/Components/Pages/Content/ContentList.razor`

**Interfaces:**
- Consumes: nothing new beyond Tasks 14–15's own components.
- Produces: adds `[Parameter] public RenderFragment<ContentItemSummaryResponse>? RowActions { get; set; }` to both `ContentListView` and `ContentListPanel` (passed through), and a public `ReloadAsync()` method on `ContentListPanel` — consumed by `Cmsify.Admin`'s `ContentList.razor`.

- [ ] **Step 1: Write the failing test for `RowActions` on `ContentListView`**

Add to `ContentListViewTests`:

```csharp
    [Fact]
    public void RendersRowActionsFragmentWhenProvided()
    {
        var item = CreateItem("first");
        var cut = RenderComponent<ContentListView>(parameters => parameters
            .Add(p => p.Items, new[] { item })
            .Add(p => p.RowActions, (RenderFragment<ContentItemSummaryResponse>)(rowItem => builder =>
                builder.AddMarkupContent(0, $"<button class=\"custom-action\">Act on {rowItem.Slug}</button>"))));

        cut.Find(".custom-action").TextContent.ShouldBe("Act on first");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj --filter RendersRowActionsFragmentWhenProvided`
Expected: FAIL — `RowActions` does not exist on `ContentListView` yet.

- [ ] **Step 3: Add `RowActions` to `ContentListView.razor`**

Change the actions `<td>` to:

```razor
                    <td>
                        <button type="button" class="cmsify-list-edit-button" @onclick="() => OnEdit.InvokeAsync(contentItem)">Edit</button>
                        @if (RowActions is not null)
                        {
                            @RowActions(contentItem)
                        }
                    </td>
```

Add the parameter to `@code`:

```csharp
    [Parameter] public RenderFragment<ContentItemSummaryResponse>? RowActions { get; set; }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj --filter RendersRowActionsFragmentWhenProvided`
Expected: PASS

- [ ] **Step 5: Write the failing test for `ContentListPanel`'s `RowActions` passthrough and `ReloadAsync`**

Add to `ContentListPanelTests`:

```csharp
    [Fact]
    public async Task PassesThroughRowActionsAndSupportsExplicitReload()
    {
        var workspaceId = Guid.NewGuid();
        var loadCount = 0;
        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == $"/api/v1/workspaces/{workspaceId}/templates")
            {
                return FakeHttpMessageHandler.Json("""{ "items": [], "totalCount": 0, "page": 1, "pageSize": 20 }""");
            }
            if (path == $"/api/v1/workspaces/{workspaceId}/content")
            {
                loadCount++;
                return FakeHttpMessageHandler.Json("""{ "items": [], "totalCount": 0, "page": 1, "pageSize": 20 }""");
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        var cut = RenderComponent<SyntaxCircus.Cmsify.Components.Client.ContentListPanel>(parameters => parameters
            .Add(p => p.Client, client)
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.RowActions, (RenderFragment<ContentItemSummaryResponse>)(item => builder => builder.AddMarkupContent(0, "<span class=\"row-action\"></span>"))));

        loadCount.ShouldBe(1);

        await cut.Instance.ReloadAsync();

        loadCount.ShouldBe(2);
    }
```

- [ ] **Step 6: Run test to verify it fails**

Run: `dotnet test tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj --filter PassesThroughRowActionsAndSupportsExplicitReload`
Expected: FAIL — `RowActions`/`ReloadAsync` do not exist on `ContentListPanel` yet.

- [ ] **Step 7: Add `RowActions` passthrough and `ReloadAsync` to `ContentListPanel.razor`**

```razor
<ContentListView Items="@items" Templates="@templates" SearchText="@searchText" SearchTextChanged="@(v => searchText = v)"
                 Status="@status" StatusChanged="@(v => status = v)" TemplateId="@templateId" TemplateIdChanged="@(v => templateId = v)"
                 Locale="@locale" LocaleChanged="@(v => locale = v)" OnFilter="LoadAsync" OnEdit="@(item => OnEdit.InvokeAsync(item))"
                 RowActions="@RowActions" />
```

```csharp
    [Parameter] public RenderFragment<ContentItemSummaryResponse>? RowActions { get; set; }

    public Task ReloadAsync() => LoadAsync();
```

(add both to the existing `@code` block — `RowActions` alongside the other parameters, `ReloadAsync` alongside `LoadAsync`)

- [ ] **Step 8: Run test to verify it passes**

Run: `dotnet test tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj --filter PassesThroughRowActionsAndSupportsExplicitReload`
Expected: PASS

- [ ] **Step 9: Rewrite `ContentList.razor`**

```razor
@page "/workspaces/{WorkspaceId:guid}/content"
@rendermode InteractiveServer
@using SyntaxCircus.Cmsify.Components.Client
@inject CmsifyClient Cmsify
@inject NavigationManager Navigation
@inject AuthState Auth
@inject ToastState Toasts

<div class="cms-page-head">
    <div><h1>Content</h1><div class="lead">Find, filter, and move content through the lifecycle.</div></div>
    <button class="btn btn-primary" @onclick="NewContent">New Content</button>
</div>

<div class="cms-card p-0">
    <ContentListPanel @ref="listPanel" Client="@Cmsify" WorkspaceId="@WorkspaceId" OnEdit="EditContent">
        <RowActions Context="contentItem">
            @{
                var submitState = ContentWorkflowActions.SubmitState(contentItem.Status);
                var approveState = ContentWorkflowActions.ApproveState(contentItem.Status);
                var rejectState = ContentWorkflowActions.RejectState(contentItem.Status, CurrentRole);
                var publishState = ContentWorkflowActions.PublishState(contentItem.Status, CurrentRole);
                var archiveState = ContentWorkflowActions.ArchiveState(contentItem.Status);
                var restoreState = ContentWorkflowActions.RestoreState(contentItem.Status, CurrentRole);
            }
            <button class="btn btn-outline-secondary btn-sm" disabled="@(!submitState.Enabled)" title="@submitState.DisabledReason" @onclick="() => RunTransitionAsync(contentItem.Id, SubmitAction)">Submit</button>
            <button class="btn btn-outline-secondary btn-sm" disabled="@(!approveState.Enabled)" title="@approveState.DisabledReason" @onclick="() => RunTransitionAsync(contentItem.Id, ApproveAction)">Approve</button>
            <button class="btn btn-outline-warning btn-sm" disabled="@(!rejectState.Enabled)" title="@rejectState.DisabledReason" @onclick="() => RunRejectAsync(contentItem.Id)">Reject</button>
            <button class="btn btn-outline-secondary btn-sm" disabled="@(!publishState.Enabled)" title="@publishState.DisabledReason" @onclick="() => OpenPublishDialog(contentItem)">Publish</button>
            <button class="btn btn-outline-secondary btn-sm" disabled="@(!archiveState.Enabled)" title="@archiveState.DisabledReason" @onclick="() => RunTransitionAsync(contentItem.Id, ArchiveAction)">Archive</button>
            <button class="btn btn-outline-secondary btn-sm" disabled="@(!restoreState.Enabled)" title="@restoreState.DisabledReason" @onclick="() => RunTransitionAsync(contentItem.Id, RestoreAction)">Restore</button>
        </RowActions>
    </ContentListPanel>
</div>

<PublishContentDialog WorkspaceId="@WorkspaceId" ContentId="@publishContentId" ContentStatus="@publishContentStatus"
                      CanOverrideWorkflow="@(CurrentRole >= UserRole.Admin)" Cmsify="@Cmsify"
                      OnClosed="ClosePublishDialog" OnPublished="OnPublishedAsync" />

@code {
    private ContentClient Content => Cmsify.Content;
    [Parameter] public Guid WorkspaceId { get; set; }
    private ContentListPanel? listPanel;
    private const string SubmitAction = "submit";
    private const string ApproveAction = "approve";
    private const string ArchiveAction = "archive";
    private const string RestoreAction = "restore";
    private Guid? publishContentId;
    private ContentStatus publishContentStatus;

    private UserRole CurrentRole => Enum.TryParse<UserRole>(Auth.User?.Role, ignoreCase: true, out var role) ? role : UserRole.Reader;

    private void NewContent() => Navigation.NavigateTo($"/workspaces/{WorkspaceId}/content/new");

    private void EditContent(ContentItemSummaryResponse item) => Navigation.NavigateTo($"/workspaces/{WorkspaceId}/content/{item.Id}");

    private async Task RunTransitionAsync(Guid id, string action)
    {
        try
        {
            await Content.TransitionAsync(WorkspaceId, id, action);
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

    private async Task RunRejectAsync(Guid id)
    {
        try
        {
            await Content.RejectAsync(WorkspaceId, id, new RejectContentRequest("Rejected from content list"));
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

    private void OpenPublishDialog(ContentItemSummaryResponse item)
    {
        publishContentId = item.Id;
        publishContentStatus = item.Status;
    }

    private void ClosePublishDialog() => publishContentId = null;

    private async Task OnPublishedAsync(PublishContentResponse response)
    {
        Toasts.Success(response.Warnings.Count == 0 ? "Content published." : "Content published with warnings.");
        await ReloadListAsync();
    }

    private Task ReloadListAsync() => listPanel is null ? Task.CompletedTask : listPanel.ReloadAsync();
}
```

- [ ] **Step 10: Build Admin to confirm it compiles**

Run: `dotnet build src/Cmsify.Admin/Cmsify.Admin.csproj`
Expected: builds cleanly.

- [ ] **Step 11: Run the existing Admin integration tests**

Run: `dotnet test tests/Cmsify.Admin.Integration.Tests/Cmsify.Admin.Integration.Tests.csproj`
Expected: PASS. Update any assertion tied to old markup, as in Task 18 Step 6.

- [ ] **Step 12: Commit**

```bash
git add src/Cmsify.Components/ContentListView.razor tests/Cmsify.Components.Tests/ContentListViewTests.cs src/Cmsify.Components/Client/ContentListPanel.razor tests/Cmsify.Components.Tests/Client/ContentListPanelTests.cs src/Cmsify.Admin/Components/Pages/Content/ContentList.razor
git commit -m "$(cat <<'EOF'
Add RowActions/ReloadAsync escape hatches and migrate Admin ContentList

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3Smg2MFcrDKPuq2WYQEYU
EOF
)"
```

---

## Task 20: Remove dead Admin code

`Components/Shared/MediaPickerModal.razor` was only ever referenced from `ContentEditor.razor` (verified by repo-wide search); after Task 18's migration it is unused. Leaving it in place would be dead code the next reader has to figure out is safe to ignore.

**Files:**
- Delete: `src/Cmsify.Admin/Components/Shared/MediaPickerModal.razor`

**Interfaces:**
- Consumes: nothing.
- Produces: nothing (deletion only).

- [ ] **Step 1: Confirm nothing still references it**

Run: `grep -rl "MediaPickerModal" src/Cmsify.Admin` (or the equivalent Grep-tool search)
Expected: no matches (Task 18 already replaced the one reference).

- [ ] **Step 2: Delete the file**

```bash
git rm src/Cmsify.Admin/Components/Shared/MediaPickerModal.razor
```

- [ ] **Step 3: Build Admin to confirm nothing broke**

Run: `dotnet build src/Cmsify.Admin/Cmsify.Admin.csproj`
Expected: builds cleanly.

- [ ] **Step 4: Commit**

```bash
git commit -m "$(cat <<'EOF'
Remove Admin's MediaPickerModal, superseded by the Components package

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3Smg2MFcrDKPuq2WYQEYU
EOF
)"
```

---

## Task 21: Wire the two new packages into the release pipeline

`.github/workflows/publish-cmsify.yml` hardcodes the release's package list in four places: the `dotnet pack` commands, an SBOM-input restore loop (with a NuGet source-mapping config and an exact nupkg-count assertion), a `dotnet-consumer` restore loop (same shape), and the `promote` job's "reject existing NuGet versions" preflight. All four need the two new package names added, in the same style as the existing three. `.github/workflows/dotnet-test.yml` needs **no change** — `Cmsify.Components.Tests` depends (transitively, via the Client SDK) on the same gated `SyntaxCircus.Http.Resilience` prerelease as Admin and the Client SDK tests already do, so it correctly stays out of the PR-only "public-independent" restore list and only runs via the full `dotnet test Cmsify.slnx` step already covering the whole solution (Task 1 added both projects to `Cmsify.slnx`).

**Files:**
- Modify: `.github/workflows/publish-cmsify.yml`
- Verify (no planned edit unless the scripts below say otherwise): `scripts/release/verify-release-artifacts.mjs`, `scripts/release/verify-release-contract.mjs`, `tests/release-contract/*.test.mjs`

**Interfaces:**
- Consumes: `SyntaxCircus.Cmsify.Components`/`SyntaxCircus.Cmsify.Components.Theme` package identities (Tasks 1, 16).
- Produces: nothing (CI configuration only).

- [ ] **Step 1: Add pack commands for both new packages**

In the `build` job, immediately after the existing `SyntaxCircus.Cmsify.Client.DistributedCaching` pack line, add:

```yaml
          dotnet pack src/Cmsify.Components/Cmsify.Components.csproj --configuration Release --no-build --output artifacts/nuget -p:CmsifyReleaseBuild=true -p:PackageVersion="$VERSION" -p:RepositoryCommit="$SOURCE_SHA" -p:IncludeSymbols=false -p:WarningsNotAsErrors=NU5104
          dotnet pack src/Cmsify.Components.Theme/Cmsify.Components.Theme.csproj --configuration Release --no-build --output artifacts/nuget -p:CmsifyReleaseBuild=true -p:PackageVersion="$VERSION" -p:RepositoryCommit="$SOURCE_SHA" -p:IncludeSymbols=false
```

(`Cmsify.Components` needs the same `NU5104` suppression as the Client SDK pack line above it, since it transitively depends on the same prerelease-suffixed `SyntaxCircus.Http.Resilience`; `Cmsify.Components.Theme` has no dependencies, so it doesn't.)

- [ ] **Step 2: Extend the SBOM-input restore loop**

In the "Restore exact package candidates into isolated SBOM trees" step, there are two `for package in ...` loops with identical package lists — update both occurrences from:

```bash
          for package in SyntaxCircus.Cmsify.Contracts SyntaxCircus.Cmsify.Client SyntaxCircus.Cmsify.Client.DistributedCaching; do
```

to:

```bash
          for package in SyntaxCircus.Cmsify.Contracts SyntaxCircus.Cmsify.Client SyntaxCircus.Cmsify.Client.DistributedCaching SyntaxCircus.Cmsify.Components SyntaxCircus.Cmsify.Components.Theme; do
```

Update the count assertion in the same step from:

```bash
          test "$(find "$NUGET_SOURCE" -maxdepth 1 -type f -name '*.nupkg' | wc -l)" -eq 3
```

to:

```bash
          test "$(find "$NUGET_SOURCE" -maxdepth 1 -type f -name '*.nupkg' | wc -l)" -eq 5
```

Add two lines to that step's `NuGet.Config` `packageSourceMapping` block, after the existing `SyntaxCircus.Cmsify.Client.DistributedCaching` pattern:

```xml
                <package pattern="SyntaxCircus.Cmsify.Components" />
                <package pattern="SyntaxCircus.Cmsify.Components.Theme" />
```

- [ ] **Step 3: Extend the `dotnet-consumer` job identically**

The `dotnet-consumer` job's "Restore all three candidates through an isolated local source" step has the same shape (a `for package in ...` loop, a `NuGet.Config` with `packageSourceMapping`, and a nupkg-count assertion). Apply the exact same three edits as Step 2: add both new package names to the `for` loop, add the two `<package pattern="..." />` lines, and change `-eq 3` to `-eq 5`. Rename the step's title from "Restore all three candidates..." to "Restore all five candidates through an isolated local source" so the name stays accurate.

- [ ] **Step 4: Extend the `promote` job's "Reject existing NuGet versions" preflight**

Change:

```bash
          for package in syntaxcircus.cmsify.contracts syntaxcircus.cmsify.client syntaxcircus.cmsify.client.distributedcaching; do
```

to:

```bash
          for package in syntaxcircus.cmsify.contracts syntaxcircus.cmsify.client syntaxcircus.cmsify.client.distributedcaching syntaxcircus.cmsify.components syntaxcircus.cmsify.components.theme; do
```

(lowercase, matching the NuGet flat-container URL convention already used on this line).

- [ ] **Step 5: Locate every remaining hardcoded package-list expectation**

Run: `grep -rn "SyntaxCircus.Cmsify.Client.DistributedCaching" scripts/release tests/release-contract` (or the equivalent Grep-tool search)

This surfaces every release-contract script/test that enumerates the exact package list (`verify-release-artifacts.mjs`, `verify-release-contract.mjs`, and/or files under `tests/release-contract/`) beyond the workflow YAML already updated in Steps 1–4. For each hit, add `SyntaxCircus.Cmsify.Components` and `SyntaxCircus.Cmsify.Components.Theme` to that list following the exact pattern already used for `SyntaxCircus.Cmsify.Client.DistributedCaching` in the same file (same array/list shape, same casing convention as that file already uses).

- [ ] **Step 6: Run the release-contract test suite to verify the changes are complete**

Run: `node --test tests/upgrade/unit/*.test.mjs tests/release-contract/*.test.mjs`
Expected: PASS. Any failure here names the exact assertion still expecting the old 3-package list — fix it the same way as Step 5 and re-run until this passes.

- [ ] **Step 7: Run the release-contract verification script**

Run: `node scripts/release/verify-release-contract.mjs`
Expected: PASS (exits 0).

- [ ] **Step 8: Commit**

```bash
git add .github/workflows/publish-cmsify.yml
git commit -m "$(cat <<'EOF'
Wire SyntaxCircus.Cmsify.Components(.Theme) into the release pipeline

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3Smg2MFcrDKPuq2WYQEYU
EOF
)"
```

If Step 5 required edits to any `scripts/release/*.mjs` or `tests/release-contract/*` files, stage and include those exact files in this same commit instead of (or in addition to) the workflow YAML.

---

## Task 22: Final end-to-end verification

**Files:** none (verification only).

- [ ] **Step 1: Build the whole solution**

Run: `dotnet build Cmsify.slnx --configuration Release`
Expected: builds cleanly, including the two new projects and the migrated Admin pages.

- [ ] **Step 2: Run the whole test suite**

Run: `dotnet test Cmsify.slnx --configuration Release`
Expected: PASS — every test from Tasks 1–21, plus all pre-existing Core/Infrastructure/API/Admin/Client-SDK tests unaffected by this work.

- [ ] **Step 3: Start Cmsify locally**

Run: `docker compose up -d` (brings up Postgres/MinIO per `docker-compose.yml`), then `dotnet run --project src/Cmsify.Api` and `dotnet run --project src/Cmsify.Admin` (or whatever the project's existing `run`/dev-launch skill/script is — check for one before running these manually).

- [ ] **Step 4: Exercise the migrated create/edit/list flow in a browser**

Using the running Admin app: create a workspace/template with at least one of each interesting field type (Text, RichText, Boolean, PickList with a bound pick list, Media, a Reference field to another template, and a Component field) if one doesn't already exist from seed data; then:
- Create a new content item from `/workspaces/{id}/content/new`, filling every field type, and save. Confirm it redirects to the edit route and the toast reads "Draft created."
- Reopen it for edit, confirm every field's current value is pre-filled correctly (including the Media asset's file name and the PickList selection).
- Change a field, save, confirm the toast reads "Draft saved" and the change persisted after a page reload.
- Exercise the Media field's picker end-to-end: open it, search, upload a new file, select it, save.
- Visit `/workspaces/{id}/content`, confirm the list renders, filters work, and the Submit/Approve/Reject/Publish/Archive/Restore buttons still function and refresh the row's status without a full page reload.
- Trigger at least one validation/error path (e.g. save with a required field empty, or force a 412 by editing the same item in two tabs) and confirm the error banner renders inside the form and, for the 412 case, reloads the latest content per Task 13's conflict handling.

- [ ] **Step 5: Confirm the accessibility check still passes**

Run: `npm ci --prefix eng/accessibility` (if not already installed), then run the same harness `admin-accessibility.yml` uses (`node eng/accessibility/run.mjs --url <local admin URL>/login --output <some local dir>`) against the locally running Admin instance.
Expected: no new accessibility violations introduced by the migrated pages. If any appear on the extracted controls (e.g. missing labels on the new plain `<select>`/`<input>` elements), fix them in the offending component from Tasks 3–15 and re-run the affected task's bUnit tests plus this check.

- [ ] **Step 6: Manually verify theming**

In a scratch HTML page or browser devtools, override a couple of `--cmsify-*` variables (e.g. `--cmsify-color-border-focus`, `--cmsify-radius`) at a wrapping element's scope around the rendered form and confirm the field editors visibly re-skin without needing any `::deep` CSS overrides — this is the concrete proof that "headless by default, restylable" (the spec's core requirement) actually holds.

- [ ] **Step 7: Report completion**

Summarize what was verified (build, full test suite, manual browser walkthrough, accessibility check, theming check) so the next reviewer knows exactly what evidence backs "done," per the project's verification-before-completion practice.

