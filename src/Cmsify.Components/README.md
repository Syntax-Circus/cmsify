# SyntaxCircus.Cmsify.Components

Reusable Blazor Server components for editing and managing Cmsify content: field editors for every content-model primitive type, a composed content edit form, and a content list view. Presentational components take data via parameters and raise events; SDK-backed "smart" wrappers under the `SyntaxCircus.Cmsify.Components.Client` namespace additionally accept a `CmsifyClient` and handle loading/saving.

## Getting Started

The quickest way to get a working editor is the SDK-backed `ContentEditPanel`, which handles loading, field editors, and saving for you:

```razor
@using SyntaxCircus.Cmsify.Components.Client

<ContentEditPanel Client="@Cmsify" WorkspaceId="@WorkspaceId" ContentId="@ContentId" />
```

If you'd rather compose the pieces yourself (for example, to drive field values from your own data source), render a single `FieldEditor` directly:

```razor
@using SyntaxCircus.Cmsify.Components

<FieldEditor Field="@field" Value="@value" ValueChanged="@(v => value = v)" />
```

**Important — CSS isolation:** these components rely on Blazor's CSS isolation feature, which packages each `.razor.css` file's rules into your *host app's* generated styles bundle (`_content/<YourAssembly>/...bundle.scp.css`), assembled into a single `<YourAppAssemblyName>.styles.css` file. Blazor does **not** link that file into your page automatically — you must add it yourself, or every visual rule in this package (including modal overlay positioning) will silently do nothing at runtime:

```razor
<link rel="stylesheet" href="@Assets["YourAppAssemblyName.styles.css"]" />
```

Add this to your app's `<head>` (in `App.razor` or equivalent). This is easy to miss — it is exactly the bug this note exists to prevent.

## Extensibility: FieldTemplateOverrides

`FieldEditor`, `ContentEditForm`, and `ContentEditPanel` all accept an optional `FieldTemplateOverrides` parameter:

```csharp
IReadOnlyDictionary<PrimitiveType, RenderFragment<FieldEditorRenderContext>>? FieldTemplateOverrides
```

This lets a host application override how a single primitive type is rendered — for example, to swap in a rich WYSIWYG editor for `PrimitiveType.RichText` — without forking the package or reimplementing the rest of the form. Any primitive type not present in the dictionary keeps rendering with the package's built-in editor. `FieldEditorRenderContext` carries the `Field`, current `Value`, a `ValueChanged` callback, and a `ReadOnly` flag (set when the containing form was given `ReadOnly="true"`) so your override fragment can read and update the field like any built-in editor, and can render a non-interactive view when `ReadOnly` is set.

```razor
<ContentEditPanel Client="@Cmsify" WorkspaceId="@WorkspaceId" ContentId="@ContentId"
                   FieldTemplateOverrides="@overrides" />

@code {
    private readonly IReadOnlyDictionary<PrimitiveType, RenderFragment<FieldEditorRenderContext>> overrides =
        new Dictionary<PrimitiveType, RenderFragment<FieldEditorRenderContext>>
        {
            [PrimitiveType.RichText] = context => @<MyCustomRichTextEditor
                Value="@context.Value.TextValue"
                ValueChanged="@(v => context.ValueChanged.InvokeAsync(context.Value))" />
        };
}
```

## Styling

Components ship with structural CSS only (via CSS isolation). Every visual property is a CSS custom property you can override at any scope:

| Variable | Purpose |
| --- | --- |
| `--cmsify-color-text` | Text color |
| `--cmsify-color-border` | Default border color |
| `--cmsify-color-border-focus` | Border color on focus |
| `--cmsify-color-danger` | Error/warning text and borders |
| `--cmsify-color-muted` | Secondary/help text |
| `--cmsify-color-overlay` | Modal backdrop background (e.g. `MediaPickerModal`) |
| `--cmsify-color-surface` | Modal/dialog surface background — defaults to an explicit light color (`#ffffff`) rather than a system keyword, so it stays readable against `--cmsify-color-text` regardless of the host page's own light/dark theme |
| `--cmsify-color-field-background` | Background of text inputs, textareas, and selects — defaults to `transparent` so fields blend into whatever surface they sit on; override this if you want filled-in form controls |
| `--cmsify-color-on-accent` | Text color on accent-colored surfaces (e.g. the save button) |
| `--cmsify-radius` | Corner radius for inputs, buttons, cards |
| `--cmsify-spacing-sm` | Small padding/gap |
| `--cmsify-spacing-md` | Medium padding/gap |
| `--cmsify-font-family` | Font family |
| `--cmsify-focus-ring` | Focus box-shadow |

Install `SyntaxCircus.Cmsify.Components.Theme` for a ready-made default look, or set these variables yourself to match your own design system.
