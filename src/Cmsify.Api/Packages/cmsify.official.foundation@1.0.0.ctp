{
  "cmsifyPackage": "1.1",
  "namespace": "cmsify.official",
  "id": "foundation",
  "version": "1.0.0",
  "name": "Foundation Pack",
  "description": "Reusable components and choice sets for common content blocks.",
  "author": "Cmsify Team",
  "license": "MIT",
  "homepage": "https://cmsify.dev/packages/foundation",
  "templates": [],
  "picklists": [
    {
      "slug": "yes-no",
      "name": "Yes / No",
      "description": "Explicit yes/no choices for integrations and string-backed fields. Prefer the Boolean primitive for ordinary true/false data.",
      "options": [
        { "label": "Yes", "value": "yes", "order": 0 },
        { "label": "No", "value": "no", "order": 1 }
      ]
    },
    {
      "slug": "call-to-action-style",
      "name": "Call to Action Style",
      "description": "A shared display variant for call-to-action components.",
      "options": [
        { "label": "Primary", "value": "primary", "order": 0 },
        { "label": "Secondary", "value": "secondary", "order": 1 },
        { "label": "Link", "value": "link", "order": 2 }
      ]
    },
    {
      "slug": "notice-tone",
      "name": "Notice Tone",
      "description": "A shared semantic tone for notices and announcements.",
      "options": [
        { "label": "Information", "value": "info", "order": 0 },
        { "label": "Success", "value": "success", "order": 1 },
        { "label": "Warning", "value": "warning", "order": 2 },
        { "label": "Danger", "value": "danger", "order": 3 }
      ]
    }
  ],
  "components": [
    {
      "slug": "call-to-action",
      "name": "Call to Action",
      "description": "A heading, supporting copy, and linked action.",
      "fields": [
        { "key": "heading", "label": "Heading", "order": 0, "isRequired": true, "minOccurrences": 1, "maxOccurrences": 1, "primitiveType": "Text" },
        { "key": "body", "label": "Body", "order": 1, "isRequired": false, "minOccurrences": 0, "maxOccurrences": 1, "primitiveType": "Markdown" },
        { "key": "linkLabel", "label": "Link label", "order": 2, "isRequired": true, "minOccurrences": 1, "maxOccurrences": 1, "primitiveType": "Text" },
        { "key": "linkUrl", "label": "Link URL", "order": 3, "isRequired": true, "minOccurrences": 1, "maxOccurrences": 1, "primitiveType": "Link" },
        { "key": "style", "label": "Style", "order": 4, "isRequired": true, "minOccurrences": 1, "maxOccurrences": 1, "primitiveType": "PickList", "fieldConfig": { "picklistRef": "call-to-action-style" } }
      ]
    },
    {
      "slug": "notice",
      "name": "Notice",
      "description": "A semantic announcement or alert.",
      "fields": [
        { "key": "title", "label": "Title", "order": 0, "isRequired": false, "minOccurrences": 0, "maxOccurrences": 1, "primitiveType": "Text" },
        { "key": "body", "label": "Body", "order": 1, "isRequired": true, "minOccurrences": 1, "maxOccurrences": 1, "primitiveType": "Markdown" },
        { "key": "tone", "label": "Tone", "order": 2, "isRequired": true, "minOccurrences": 1, "maxOccurrences": 1, "primitiveType": "PickList", "fieldConfig": { "picklistRef": "notice-tone" } }
      ]
    },
    {
      "slug": "media-with-caption",
      "name": "Media with Caption",
      "description": "Media accompanied by optional caption and attribution text.",
      "fields": [
        { "key": "media", "label": "Media", "order": 0, "isRequired": true, "minOccurrences": 1, "maxOccurrences": 1, "primitiveType": "Media" },
        { "key": "caption", "label": "Caption", "order": 1, "isRequired": false, "minOccurrences": 0, "maxOccurrences": 1, "primitiveType": "Text" },
        { "key": "attribution", "label": "Attribution", "order": 2, "isRequired": false, "minOccurrences": 0, "maxOccurrences": 1, "primitiveType": "Text" }
      ]
    }
  ]
}
