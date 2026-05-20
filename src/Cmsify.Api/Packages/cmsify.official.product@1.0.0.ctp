{
  "cmsifyPackage": "1.0",
  "namespace": "cmsify.official",
  "id": "product",
  "version": "1.0.0",
  "name": "Product Starter Pack",
  "description": "Product, variant, and review templates.",
  "author": "Cmsify Team",
  "license": "MIT",
  "homepage": "https://cmsify.dev/packages/product",
  "picklists": [
    {
      "slug": "review-rating",
      "name": "Review Rating",
      "description": "1\u20135 star product review rating.",
      "options": [
        { "label": "1 star", "value": "1", "order": 0 },
        { "label": "2 stars", "value": "2", "order": 1 },
        { "label": "3 stars", "value": "3", "order": 2 },
        { "label": "4 stars", "value": "4", "order": 3 },
        { "label": "5 stars", "value": "5", "order": 4 }
      ]
    }
  ],
  "templates": [
    {
      "slug": "review",
      "name": "Review",
      "description": "A product review.",
      "sections": [],
      "fields": [
        { "key": "rating", "label": "Rating", "order": 0, "isRequired": true, "minOccurrences": 1, "maxOccurrences": 1, "isOpen": false, "compositionMode": "Inline", "primitiveType": "PickList", "fieldConfig": { "picklistRef": "review-rating", "multiple": false } },
        { "key": "body", "label": "Body", "order": 1, "isRequired": false, "minOccurrences": 0, "maxOccurrences": 1, "isOpen": false, "compositionMode": "Inline", "primitiveType": "Markdown" }
      ]
    },
    {
      "slug": "product-variant",
      "name": "Product Variant",
      "description": "A purchasable product variant.",
      "sections": [],
      "fields": [
        { "key": "sku", "label": "SKU", "order": 0, "isRequired": true, "minOccurrences": 1, "maxOccurrences": 1, "isOpen": false, "compositionMode": "Inline", "primitiveType": "Text" },
        { "key": "name", "label": "Name", "order": 1, "isRequired": true, "minOccurrences": 1, "maxOccurrences": 1, "isOpen": false, "compositionMode": "Inline", "primitiveType": "Text" }
      ]
    },
    {
      "slug": "product",
      "name": "Product",
      "description": "A product detail page.",
      "sections": [],
      "fields": [
        { "key": "name", "label": "Name", "order": 0, "isRequired": true, "minOccurrences": 1, "maxOccurrences": 1, "isOpen": false, "compositionMode": "Inline", "primitiveType": "Text" },
        { "key": "variants", "label": "Variants", "order": 1, "isRequired": false, "minOccurrences": 0, "maxOccurrences": null, "isOpen": false, "compositionMode": "Inline", "templateRef": "product-variant" },
        { "key": "reviews", "label": "Reviews", "order": 2, "isRequired": false, "minOccurrences": 0, "maxOccurrences": null, "isOpen": false, "compositionMode": "Reference", "templateRef": "review" }
      ]
    }
  ]
}
