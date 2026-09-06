# SyntaxCircus.Cmsify.Components.Theme

Default Bootstrap-flavored values for the `--cmsify-*` CSS custom properties used by `SyntaxCircus.Cmsify.Components`. Purely static assets — installing this package adds no components or code.

## Usage

Reference the stylesheet from your host page (e.g. in `App.razor`'s `<head>`):

```html
<link rel="stylesheet" href="_content/SyntaxCircus.Cmsify.Components.Theme/cmsify-theme.css" />
```

Omit this package entirely if you want to set the `--cmsify-*` variables yourself to match your own design system.

## Default values

| Variable | Default |
| --- | --- |
| `--cmsify-color-text` | `#212529` |
| `--cmsify-color-border` | `#dee2e6` |
| `--cmsify-color-border-focus` | `#6D28D9` |
| `--cmsify-color-danger` | `#B91C1C` |
| `--cmsify-color-muted` | `#6c757d` |
| `--cmsify-color-overlay` | `rgba(0, 0, 0, 0.5)` |
| `--cmsify-color-surface` | `Canvas` |
| `--cmsify-color-on-accent` | `white` |
| `--cmsify-radius` | `0.375rem` |
| `--cmsify-spacing-sm` | `0.375rem` |
| `--cmsify-spacing-md` | `0.75rem` |
| `--cmsify-font-family` | `system-ui, -apple-system, "Segoe UI", sans-serif` |
| `--cmsify-focus-ring` | `0 0 0 0.2rem rgba(109, 40, 217, 0.25)` |
