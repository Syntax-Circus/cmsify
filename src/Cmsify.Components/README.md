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
