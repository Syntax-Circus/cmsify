# Reusable model packages

Cmsify packages (`.ctp` files) are portable JSON bundles for sharing reusable content models. Publish a package file in a Git repository or release asset; an operator downloads it and imports it in **Settings → Packages**. Cmsify does not fetch remote repositories directly.

## What a package can contain

A package can contain any combination of templates, components, and picklists. Cmsify exports required dependencies automatically:

- templates bring referenced templates, components, and picklists;
- components bring nested components and their picklists;
- picklists can be exported independently.

CTP `1.1` adds components and `componentRef` fields. CTP `1.0` template/picklist packages remain import-compatible.

```json
{
  "cmsifyPackage": "1.1",
  "namespace": "example.marketing",
  "id": "campaign-blocks",
  "version": "1.0.0",
  "name": "Campaign blocks",
  "templates": [],
  "picklists": [
    { "slug": "button-style", "name": "Button style", "options": [
      { "label": "Primary", "value": "primary", "order": 0 }
    ] }
  ],
  "components": [
    { "slug": "call-to-action", "name": "Call to action", "fields": [
      { "key": "heading", "label": "Heading", "order": 0, "isRequired": true, "minOccurrences": 1, "maxOccurrences": 1, "primitiveType": "Text" },
      { "key": "style", "label": "Style", "order": 1, "isRequired": false, "minOccurrences": 0, "maxOccurrences": 1, "primitiveType": "PickList", "fieldConfig": { "picklistRef": "button-style" } }
    ] }
  ]
}
```

References use package-local slugs (`templateRef`, `componentRef`, and `picklistRef`), never workspace IDs. Cmsify resolves them during import and rejects missing or circular component references.

## Import conflicts and upgrades

Matching template slugs receive a new published template version. For matching picklists and components, review the import preview and choose one of:

- **Use existing** — package references bind to the existing reusable model.
- **Replace** — picklists receive a new immutable revision; components receive a new published schema version.
- **Import as new** — Cmsify suffixes the slug and rewrites package-internal references to the copied model.

Imported templates, components, and picklists retain package namespace, ID, and version provenance. Existing published content snapshots never change when a package later updates a component or picklist.

## Official packages

Cmsify bundles official packages with the API. They appear alongside uploaded packages in **Settings → Packages** and onboarding. The same format, preview, conflict handling, and upgrade rules apply to official and custom packages.

### Foundation Pack

The official **Foundation Pack** supplies small, composable building blocks rather than whole content types: Call to Action, Notice, and Media with Caption components, plus the choice sets they need. Its `yes-no` choice set uses the stable values `yes` and `no` for integrations and string-backed fields. For ordinary true/false data, use Cmsify's native Boolean primitive instead.
