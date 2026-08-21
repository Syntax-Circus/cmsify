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
