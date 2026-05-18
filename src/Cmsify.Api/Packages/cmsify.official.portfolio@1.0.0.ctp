{
  "cmsifyPackage": "1.0",
  "namespace": "cmsify.official",
  "id": "portfolio",
  "version": "1.0.0",
  "name": "Portfolio Starter Pack",
  "description": "Project, case study, and testimonial templates.",
  "author": "Cmsify Team",
  "license": "MIT",
  "homepage": "https://cmsify.dev/packages/portfolio",
  "templates": [
    {
      "slug": "testimonial",
      "name": "Testimonial",
      "description": "A customer quote.",
      "sections": [],
      "fields": [
        { "key": "quote", "label": "Quote", "order": 0, "isRequired": true, "minOccurrences": 1, "maxOccurrences": 1, "isOpen": false, "compositionMode": "Inline", "primitiveType": "Quote" },
        { "key": "person", "label": "Person", "order": 1, "isRequired": true, "minOccurrences": 1, "maxOccurrences": 1, "isOpen": false, "compositionMode": "Inline", "primitiveType": "Text" }
      ]
    },
    {
      "slug": "project",
      "name": "Project",
      "description": "A portfolio project.",
      "sections": [],
      "fields": [
        { "key": "title", "label": "Title", "order": 0, "isRequired": true, "minOccurrences": 1, "maxOccurrences": 1, "isOpen": false, "compositionMode": "Inline", "primitiveType": "Text" },
        { "key": "summary", "label": "Summary", "order": 1, "isRequired": false, "minOccurrences": 0, "maxOccurrences": 1, "isOpen": false, "compositionMode": "Inline", "primitiveType": "Markdown" },
        { "key": "image", "label": "Image", "order": 2, "isRequired": false, "minOccurrences": 0, "maxOccurrences": 1, "isOpen": false, "compositionMode": "Inline", "primitiveType": "Media" }
      ]
    },
    {
      "slug": "case-study",
      "name": "Case Study",
      "description": "A detailed project story.",
      "sections": [],
      "fields": [
        { "key": "project", "label": "Project", "order": 0, "isRequired": true, "minOccurrences": 1, "maxOccurrences": 1, "isOpen": false, "compositionMode": "Reference", "templateRef": "project" },
        { "key": "testimonial", "label": "Testimonial", "order": 1, "isRequired": false, "minOccurrences": 0, "maxOccurrences": 1, "isOpen": false, "compositionMode": "Reference", "templateRef": "testimonial" },
        { "key": "body", "label": "Body", "order": 2, "isRequired": true, "minOccurrences": 1, "maxOccurrences": 1, "isOpen": false, "compositionMode": "Inline", "primitiveType": "Markdown" }
      ]
    }
  ]
}
