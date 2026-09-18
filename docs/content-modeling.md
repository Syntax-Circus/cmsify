# Content modeling

Cmsify separates a content model from the content authored with it. Use templates to define whole content types, components to reuse inline structures, and content items to hold the authored, publishable values.

| Concept | Purpose | Can stand alone? | Versioning and publishing |
| --- | --- | --- | --- |
| Template | Defines a content type and its fields, such as a blog post or landing page. | No. It is a schema. | Template schema versions are drafted and published. |
| Component | Defines a reusable inline block within a template, such as a call to action, card, or profile snippet. | No. It is an inline-only schema. | Component schema versions are drafted and published. Its values are stored in the parent content snapshot. |
| Content | An authored item created from a specific template version. | Yes. It is what consumers query and render. | Content follows the draft, review, publish, retire, and archive lifecycle. |

## How they fit together

Start by creating a template for the kind of thing editors will publish. Its fields can be primitive values (text, media, and so on), references to other content, or inline component fields.

Use a component when the same structured block belongs in more than one template, or when a template needs a repeatable nested structure. A component is not a child content item: its values are embedded JSON in the parent content item. Publishing a later component schema does not change values already captured in published content.

Then create content from a template. A content item is pinned to the template version it was created from until it is deliberately upgraded. This preserves the schema and values used by each published version.

### Upgrading content to a newer template version

`POST .../content/{id}/versions/{n}/upgrade-template-version` moves a Draft, Review, or Approved version onto the template's latest published version. By default (no request body) it remaps each existing value onto the target's field with the same key and drops values whose key no longer exists, then validates - this is why upgrading onto a template version that *adds a required field* fails with 422: there is no old value to carry over for a field that didn't previously exist.

To upgrade onto such a version, pass an optional `fields` array in the request body: `{ "fields": [...] }`. When supplied, `fields` **fully replaces** the version's values (the same full-replacement semantics as updating a version's fields) instead of remapping by key, and the replaced result is validated as one atomic step - if validation fails (for example, a required field is still missing, or a field id isn't present on the target), nothing is persisted and the version stays on its original template version. Field ids in `fields` refer to the **target** template version's fields, not the version's current one.

## Choosing between a component field and a template-as-child-field

When a template field needs to reference structured content, you can use either a **component field** or a **template-as-child-field**. Each serves a different purpose:

### Component field (`TemplateField.ComponentId`)

Use a component field when the content is a small, reusable inline block with no independent lifecycle. The component's value is stored as snapshot JSON directly in the parent content item's version. Component fields are ideal for:

- Structural elements that appear in multiple templates (a hero block, a call to action, a card with a title and description)
- SEO metadata or configuration (Open Graph tags, analytics settings)
- Repeatable nested structures (gallery items, FAQ question-answer pairs)

**Important:** When you edit a component field in the UI, you always see the component's *current* published schema, not the schema in effect when the value was originally captured. This means the editor form may show fields that did not exist when the value was saved.

### Template-as-child-field (`TemplateField.TemplateId` and `TemplateFieldAllowedType.AllowedTemplateId`)

Use a template-as-child-field when the content needs its own lifecycle, URL/slug, and independent permissions. The value is a real, independent `ContentItem` with `ValueKind.ChildContent`. How the editor interacts with this child depends on the `CompositionMode`:

**`CompositionMode.Reference`:** The child is meant to be shared and reused independently across multiple parents. The editor picks the child from a list of existing content items using a reference selector. This is ideal for:

- Related articles or resources
- Reusable landing page modules
- Shared testimonials or case studies
- Any content that needs its own independent publish cycle and URL

**`CompositionMode.Inline`:** The child is owned by and intended to be removed alongside its parent. The editor creates and edits the child directly, embedded within the parent's form. This is ideal for:

- Child content that has no independent value (e.g., a "gallery" that only makes sense as part of its parent page)
- Keeping related content together in a single edit session
- Simplified authoring workflows where child and parent are always published together

The child can be edited, saved, and removed entirely within the parent's editing context: removing a child inside the editor and saving deletes that child. **This is a known limitation, not a guarantee:** deleting the parent content item itself (for example, from the content list's delete action) does not cascade-delete its Inline children today. There is no server-side cascade delete for Inline children, so a child removed any other way than through the editor's own explicit remove-then-save flow becomes orphaned.

## Example: a blog post with a call to action

1. Create a **Call to Action** component with `heading`, `body`, `buttonLabel`, and `buttonUrl` fields.
2. Create a **Blog Post** template with `title`, `body`, and an inline `callToAction` field bound to that component.
3. Create the **Introducing Cmsify** content item from the Blog Post template and supply its title, body, and call-to-action values.
4. Review and publish the content item. Consumers resolve and render this published Blog Post; they never fetch the Call to Action as standalone content.

Use separate content items and a reference field when the related item needs its own lifecycle, URL/slug, permissions, or independent reuse. Use a component when its values should travel with and be published as part of the parent item.

## Related guides

- [Components and versioned choice sets](content-components-and-choice-sets.md) explains component nesting and picklist bindings.
- [Reusable model packages](packages.md) explains how to share templates, components, and picklists between workspaces.
- [Integrating with Cmsify](integrating.md) explains how API consumers query published content.
