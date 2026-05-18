{
  "cmsifyPackage": "1.0",
  "namespace": "cmsify.official",
  "id": "docs",
  "version": "1.0.0",
  "name": "Docs Starter Pack",
  "description": "Documentation page, section, and changelog templates.",
  "author": "Cmsify Team",
  "license": "MIT",
  "homepage": "https://cmsify.dev/packages/docs",
  "templates": [
    {
      "slug": "doc-section",
      "name": "Doc Section",
      "description": "A reusable documentation section.",
      "sections": [],
      "fields": [
        { "key": "heading", "label": "Heading", "order": 0, "isRequired": true, "minOccurrences": 1, "maxOccurrences": 1, "isOpen": false, "compositionMode": "Inline", "primitiveType": "Text" },
        { "key": "body", "label": "Body", "order": 1, "isRequired": true, "minOccurrences": 1, "maxOccurrences": 1, "isOpen": false, "compositionMode": "Inline", "primitiveType": "Markdown" }
      ]
    },
    {
      "slug": "doc-page",
      "name": "Doc Page",
      "description": "A documentation page.",
      "sections": [],
      "fields": [
        { "key": "title", "label": "Title", "order": 0, "isRequired": true, "minOccurrences": 1, "maxOccurrences": 1, "isOpen": false, "compositionMode": "Inline", "primitiveType": "Text" },
        { "key": "sections", "label": "Sections", "order": 1, "isRequired": false, "minOccurrences": 0, "maxOccurrences": null, "isOpen": false, "compositionMode": "Inline", "templateRef": "doc-section" }
      ]
    },
    {
      "slug": "changelog",
      "name": "Changelog",
      "description": "A release note entry.",
      "sections": [],
      "fields": [
        { "key": "version", "label": "Version", "order": 0, "isRequired": true, "minOccurrences": 1, "maxOccurrences": 1, "isOpen": false, "compositionMode": "Inline", "primitiveType": "Text" },
        { "key": "changes", "label": "Changes", "order": 1, "isRequired": true, "minOccurrences": 1, "maxOccurrences": 1, "isOpen": false, "compositionMode": "Inline", "primitiveType": "Markdown" }
      ]
    }
  ]
}
