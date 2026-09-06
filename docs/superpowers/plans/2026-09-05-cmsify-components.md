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
- Create: `src/Cmsify.Components/README.md`
- Create: `tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj`
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

```razor
@using Microsoft.AspNetCore.Components
@using Microsoft.AspNetCore.Components.Forms
@using Microsoft.AspNetCore.Components.Web
@using SyntaxCircus.Cmsify
@using SyntaxCircus.Cmsify.Contracts
```

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
git add src/Cmsify.Components/Cmsify.Components.csproj src/Cmsify.Components/_Imports.razor src/Cmsify.Components/README.md tests/Cmsify.Components.Tests/Cmsify.Components.Tests.csproj tests/Cmsify.Components.Tests/SmokeTests.cs Cmsify.slnx Directory.Packages.props
git commit -m "$(cat <<'EOF'
Scaffold SyntaxCircus.Cmsify.Components project and test project

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3Smg2MFcrDKPuq2WYQEYU
EOF
)"
```

---
