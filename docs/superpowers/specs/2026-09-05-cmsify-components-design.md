# Reusable Blazor Content-Editing Components Design

Date: 2026-09-05
Status: Approved

## Context

Cmsify's Admin site currently hand-builds all content-editing UI inline. The
per-field input UI is a single `RenderField` `RenderTreeBuilder` switch in
`src/Cmsify.Admin/Components/Pages/Content/ContentEditor.razor` (lines
~378-576), and `ContentList.razor` similarly hand-rolls its list/filter UI.
None of this is reusable — a third-party site that wants to let its own
editors manage Cmsify content today must build the entire editing UI itself
against the raw HTTP API or the `SyntaxCircus.Cmsify.Client` SDK.

An earlier internal design doc (`docs/project plan/00_index.md`) explicitly
deferred this capability: *"Delivery mode for MVP: Standalone only (separate
API + Blazor admin). Embedded mode deferred post-MVP."* This design delivers
that deferred capability as a new publishable NuGet package of
opinionated-but-restylable Blazor components, which the Admin site itself
will consume (dogfooding) in place of its bespoke editing markup.

Repo facts this design relies on (from exploration):
- Admin (`src/Cmsify.Admin`) is Blazor **Server** ("Interactive Server" render
  mode) on `net10.0`, never accesses the database directly — all data access
  goes through `SyntaxCircus.Cmsify.Client` (`sdk/dotnet/src/SyntaxCircus.Cmsify.Client`).
- Wire DTOs (`TemplateFieldResponse`, `ContentFieldValueRequest`,
  `TemplateVersionResponse`, etc.) live in `src/Cmsify.Contracts`, already
  published as `SyntaxCircus.Cmsify.Contracts`.
- The content-model field-type enum (`PrimitiveType`: Text, RichText,
  Markdown, Boolean, PickList, Media, File, Link, Quote, Separator; plus
  `ValueKind` adding ChildContent/Component) lives in
  `src/Cmsify.Core/Domain/Enums/DomainEnums.cs`. There is currently no
  abstraction mapping a `PrimitiveType` to a UI component — that mapping is
  inlined imperatively in `RenderField`.
- Admin styling is plain Bootstrap via SCSS
  (`src/Cmsify.Admin/wwwroot/scss/app.scss`, `_custom.scss`) with a `cms-*`
  custom class convention. No CSS isolation (`*.razor.css`) is used anywhere
  in Admin today.
- Existing publishable packages (`Cmsify.Contracts`, `SyntaxCircus.Cmsify.Client`,
  `SyntaxCircus.Cmsify.Client.DistributedCaching`) follow one packaging
  convention: `IsPackable` is `false` by default (`Directory.Build.props`)
  and only flips to `true` when `$(CmsifyReleaseBuild) == 'true'`, a flag set
  only by the tag-triggered `.github/workflows/publish-cmsify.yml` release
  pipeline. Package versions are centrally managed
  (`Directory.Packages.props`).

## Decision

Ship a new package, `SyntaxCircus.Cmsify.Components`, plus a companion
opt-in theme package, `SyntaxCircus.Cmsify.Components.Theme`. Both follow the
existing `Cmsify.*` → `SyntaxCircus.Cmsify.*` project/package naming and
packaging convention used by `Cmsify.Contracts`.

Four scoped decisions, made during brainstorming:

1. **Hosting model: Blazor Server only for v1.** Matches Admin's current
   setup exactly. Multi-hosting-model support (WASM, Interactive Auto) is
   deferred until a consumer actually needs it — no design cost paid now for
   a hypothetical requirement.
2. **Data access: layered, not split by package.** Field editors are
   presentational only (parameters in, `EventCallback` out) — a field is
   inherently "value in, value out" and needs no SDK access. Composed
   screens (edit form, content list) get both a presentational component and
   an SDK-backed "smart" wrapper in a `Client` sub-namespace of the *same*
   package, rather than a second package — avoids an extra package to
   version/publish for a distinction that a namespace already expresses.
3. **Styling: headless by default, theme is a separate opt-in package.**
   Components carry CSS isolation (`*.razor.css`) for structure only
   (spacing, layout) and drive every visual property through documented
   `--cmsify-*` CSS custom properties. `SyntaxCircus.Cmsify.Components.Theme`
   is a static-asset-only package (one CSS file, no components/code) that
   sets default values for those variables, Bootstrap-flavored to match
   Admin's current `cms-*` look. It is inert without the Components package,
   and consumers who want their own look simply never reference it — no risk
   of "headless if you remember to skip the CSS file."
4. **V1 scope: field editors + composed edit form + composed content list.**
   Broad enough to let Admin fully replace its current hand-rolled editing
   and list UI with the package (real dogfooding), not just extract atomic
   pieces that nothing yet composes into a working screen.

## Component Architecture

### Field editors (presentational only)

One component per `PrimitiveType`: `TextFieldEditor`, `RichTextFieldEditor`,
`MarkdownFieldEditor`, `BooleanFieldEditor`, `PickListFieldEditor`,
`MediaFieldEditor`, `FileFieldEditor`, `LinkFieldEditor`, `QuoteFieldEditor`,
`SeparatorFieldEditor`, `ComponentFieldEditor` (nested/recursive, for inline
Component fields), `ReferenceFieldEditor`. Each is independently usable on
its own — a consumer can drop a single one onto a page without pulling in
the rest of the package's composition layer.

A `FieldEditor` dispatcher component switches on `TemplateFieldResponse.Type`
and renders the matching editor. It accepts an optional per-type
`RenderFragment` override parameter, so a host can replace one field type's
rendering without forking the package. This is the concrete mechanism
satisfying the "must be stylable/customizable" requirement beyond CSS: it
covers the case where CSS variables aren't enough and a consumer needs
genuinely different markup for one field type.

This directly replaces `ContentEditor.razor`'s `RenderField` switch.

### Composed screens

Each composed screen is a presentational/smart pair:

- `ContentEditForm` (presentational: takes a `TemplateVersionResponse` and
  current field values, raises change/save events) + `Client.ContentEditPanel`
  (smart: takes content/template IDs, uses `CmsifyClient` to load/save,
  renders `ContentEditForm`). Replaces `ContentEditor.razor`.
- `ContentListView` (presentational: takes a page of content summaries and
  paging/filter state, raises events) + `Client.ContentListPanel` (smart:
  takes workspace/template context, calls `CmsifyClient`, manages
  paging/filtering, renders `ContentListView`). Replaces `ContentList.razor`.

### Pickers

`MediaFieldEditor` and `ReferenceFieldEditor` both need a "browse and pick an
item" modal to function standalone — this is new shared infrastructure, not
an extraction of something already reusable in Admin
(`Components/Shared/MediaPickerModal.razor` today is Admin-specific and
Bootstrap-coupled). One generic presentational `ItemPickerModal<TItem>`
(search/list/select, templated per-item rendering) is backed by two small
smart wrappers, `Client.MediaPickerPanel` and `Client.ReferencePickerPanel`,
used internally by the two field editors above.

Full media-library management (upload/organize existing assets,
`MediaLibrary.razor`) stays Admin-only — out of scope for this package. Only
the picker (search and select an existing item) is included.

## Styling and Theming

- `.razor.css` files hold structural styles only: spacing, flex/grid layout.
  No color, border, or radius decisions.
- Every visual property is a `--cmsify-*` CSS custom property (e.g.
  `--cmsify-input-border`, `--cmsify-color-danger`, `--cmsify-focus-ring`),
  documented in the package README, overridable at any CSS scope a consumer
  chooses. This covers the common retheming case without requiring `::deep`
  overrides.
- `SyntaxCircus.Cmsify.Components.Theme` defines default values for those
  variables matching Admin's current `cms-*` Bootstrap look, plus a small
  number of utility classes for cases variables can't express (e.g. base
  button styling).

## Admin Migration (Dogfooding)

`ContentEditor.razor` becomes a thin host around `Client.ContentEditPanel`;
`ContentList.razor` thins out around `Client.ContentListPanel`. Admin adds
package references to `SyntaxCircus.Cmsify.Components` and
`SyntaxCircus.Cmsify.Components.Theme`. Visual parity with today's Admin is a
dogfooding proof point, not a hard pixel-perfect requirement — surrounding
page chrome (nav, headers, and any `cms-*` classes outside the
extracted surfaces) is untouched.

## Packaging and CI

- `src/Cmsify.Components/Cmsify.Components.csproj` → `SyntaxCircus.Cmsify.Components`.
  References `Cmsify.Contracts` and `SyntaxCircus.Cmsify.Client`.
- `src/Cmsify.Components.Theme/Cmsify.Components.Theme.csproj` →
  `SyntaxCircus.Cmsify.Components.Theme`. No project references — static
  asset only.
- Both follow `Cmsify.Contracts.csproj`'s packable-project setup exactly
  (`PackageId`, `Title`, `Description`, `Authors`, `PackageTags`,
  `PackageLicenseExpression`, `PackageReadmeFile`, `IncludeSymbols`,
  `IsPackable` gated on `CmsifyReleaseBuild`).
- Wired into `Cmsify.slnx`, `Directory.Packages.props` (add `bunit` as a
  centrally managed test dependency), `.github/workflows/publish-cmsify.yml`,
  and `.github/workflows/dotnet-test.yml`, matching how `Cmsify.Contracts` is
  already wired into each.
- `tests/Cmsify.Components.Tests` — bUnit component tests, following the
  existing xUnit test project conventions in `tests/`.

## Test Strategy

- Every field editor gets a bUnit test asserting it renders the correct
  input markup for its `PrimitiveType` and raises the expected value-changed
  event on user interaction.
- `FieldEditor` dispatcher gets tests covering each `PrimitiveType` routing
  to the correct editor, plus a test proving the per-type `RenderFragment`
  override actually replaces default rendering.
- `ContentEditForm`/`ContentListView` get tests for their presentational
  contracts (parameters in, correct events raised) without any SDK
  dependency (no `CmsifyClient` in scope for these tests).
- `Client.ContentEditPanel`/`Client.ContentListPanel` get tests with a faked
  `CmsifyClient` (or its underlying `HttpClient`) verifying load/save/paging
  behavior and correct delegation to the presentational component.
- After migration, existing Admin integration tests
  (`tests/Cmsify.Admin.Integration.Tests`) and the `admin-accessibility.yml`
  CI check must continue passing unmodified in intent (assertions may need
  updating for new markup, but coverage must not shrink).

## Out of Scope

- WASM / Interactive Auto hosting support.
- A second package split by data-coupling (presentational-only vs.
  SDK-backed) — expressed as a namespace split within one package instead.
- Full media-library management (upload/organize) — only the picker moves.
- Pixel-perfect visual parity with current Admin styling.
- Template/schema builder UI (`TemplateBuilder.razor`), component-schema
  editor (`ComponentEditor.razor`), pick-list management
  (`PickListEditor.razor`) — none of these are content *editing* surfaces
  and are not part of this package's v1 scope.
