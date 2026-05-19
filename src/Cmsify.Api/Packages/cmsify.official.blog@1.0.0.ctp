{
  "cmsifyPackage": "1.0",
  "namespace": "cmsify.official",
  "id": "blog",
  "version": "1.0.0",
  "name": "Blog Starter Pack",
  "description": "Blog post, author bio, and category templates.",
  "author": "Cmsify Team",
  "license": "MIT",
  "homepage": "https://cmsify.dev/packages/blog",
  "templates": [
    {
      "slug": "author-bio",
      "name": "Author Bio",
      "description": "An author profile.",
      "sections": [],
      "fields": [
        { "key": "name", "label": "Name", "order": 0, "isRequired": true, "minOccurrences": 1, "maxOccurrences": 1, "isOpen": false, "compositionMode": "Inline", "primitiveType": "Text" },
        { "key": "avatar", "label": "Avatar", "order": 1, "isRequired": false, "minOccurrences": 0, "maxOccurrences": 1, "isOpen": false, "compositionMode": "Inline", "primitiveType": "Media" },
        { "key": "bio", "label": "Bio", "order": 2, "isRequired": false, "minOccurrences": 0, "maxOccurrences": 1, "isOpen": false, "compositionMode": "Inline", "primitiveType": "Markdown" }
      ]
    },
    {
      "slug": "blog-category",
      "name": "Blog Category",
      "description": "A blog taxonomy entry.",
      "sections": [],
      "fields": [
        { "key": "name", "label": "Name", "order": 0, "isRequired": true, "minOccurrences": 1, "maxOccurrences": 1, "isOpen": false, "compositionMode": "Inline", "primitiveType": "Text" },
        { "key": "description", "label": "Description", "order": 1, "isRequired": false, "minOccurrences": 0, "maxOccurrences": 1, "isOpen": false, "compositionMode": "Inline", "primitiveType": "Markdown" }
      ]
    },
    {
      "slug": "blog-post",
      "name": "Blog Post",
      "description": "A standard blog article.",
      "sections": [
        {
          "name": "Header",
          "description": "Primary post metadata.",
          "order": 0,
          "isCollapsible": false,
          "fields": [
            { "key": "title", "label": "Title", "order": 0, "isRequired": true, "minOccurrences": 1, "maxOccurrences": 1, "isOpen": false, "compositionMode": "Inline", "primitiveType": "Text" },
            { "key": "author", "label": "Author", "order": 1, "isRequired": true, "minOccurrences": 1, "maxOccurrences": 1, "isOpen": false, "compositionMode": "Reference", "templateRef": "author-bio" },
            { "key": "category", "label": "Category", "order": 2, "isRequired": false, "minOccurrences": 0, "maxOccurrences": 1, "isOpen": false, "compositionMode": "Reference", "templateRef": "blog-category" }
          ]
        }
      ],
      "fields": [
        { "key": "heroImage", "label": "Hero image", "order": 10, "isRequired": false, "minOccurrences": 0, "maxOccurrences": 1, "isOpen": false, "compositionMode": "Inline", "primitiveType": "Media" },
        { "key": "body", "label": "Body", "order": 11, "isRequired": true, "minOccurrences": 1, "maxOccurrences": 1, "isOpen": false, "compositionMode": "Inline", "primitiveType": "Markdown" }
      ]
    }
  ]
}
