# 02 — Core Domain

## Goal
Define all domain entities, enums, value objects, and interfaces that live in `Cmsify.Core`. This is the heart of the system — all other projects depend on it.

---

## Primitive Types (System-Defined, Sealed)

These are the leaf-node field types shipped with Cmsify. They cannot be modified by users. Represented as an enum used throughout the domain.

```csharp
public enum PrimitiveType
{
    Text,
    RichText,
    Markdown,
    Boolean,
    PickList,
    Media,
    File,
    Link,
    Quote,
    Separator
}
```

> **Note on representation:** primitives live as a sealed enum *and* are surfaced through API responses as first-class field type options. They are **not** seeded into the `Template` table — only user-defined and package-imported templates exist as `Template` rows. The earlier "seed primitive Templates" idea has been dropped to remove dual representation.

---

## Soft Delete Convention

All user-visible entities (`Workspace`, `Template`, `TemplateVersion`, `ContentItem`, `MediaAsset`, `User`, `Tag`, `WebhookEndpoint`, `ApiClient`) include:

```
IsDeleted           bool                // default false
DeletedAt           DateTimeOffset?
DeletedByUserId     Guid?
```

EF Core global query filters exclude soft-deleted rows by default. Admin "deleted items" views may explicitly bypass the filter. `AuditLog` itself, `UserSession`, `WebhookSubscription`, `WebhookDeliveryLog`, and `ContentItemTag` are hard-deleted (no soft-delete columns).

The previously documented `Workspace.IsActive` flag is replaced by this convention.

---

## Concurrency Token

All mutable user-visible entities include a shadow `xmin` concurrency token (mapped via EF Core as the PostgreSQL system column) surfaced over HTTP as an ETag. See `25_cross_cutting.md` for details. Entity definitions below do not repeat the token property.

---

## Entities

### Workspace
Scoping container for all templates and content. Multi-tenant-lite.

```
Workspace
  Id                  Guid (UUID7)
  Name                string
  Slug                string          // unique globally
  Description         string?
  CreatedAt           DateTimeOffset
  UpdatedAt           DateTimeOffset
  // Soft-delete columns (see Soft Delete Convention)
```

---

### Template
A named, versioned schema. User-defined or system/package-imported.

```
Template
  Id                  Guid (UUID7)
  WorkspaceId         Guid
  Name                string
  Slug                string          // unique per workspace
  Description         string?
  PackageNamespace    string?         // e.g. "cmsify.official"
  PackageId           string?         // e.g. "blog"
  PackageVersion      string?         // e.g. "1.0.0"
  TitleFieldKey       string?         // key of the field within the current published version used for slug auto-gen and list displays
  CreatedAt           DateTimeOffset
  UpdatedAt           DateTimeOffset
  CurrentVersionId    Guid?           // points to the currently-published TemplateVersion
```

---

### TemplateVersion
Immutable-once-published snapshot of a template's structure. Content items pin to this.

```
TemplateVersion
  Id                  Guid (UUID7)
  TemplateId          Guid
  VersionNumber       int             // monotonically increasing
  Status              TemplateVersionStatus  // Draft | Published | Archived
  PublishedAt         DateTimeOffset?
  CreatedAt           DateTimeOffset
  CreatedByUserId     Guid?
  Notes               string?         // changelog / release notes
```

```csharp
public enum TemplateVersionStatus
{
    Draft,      // mutable; only one Draft per Template at a time
    Published,  // immutable; content may pin to it
    Archived    // historical; immutable; no new content may pin to it
}
```

**Invariants:**
- At most one `Draft` version per Template at any time
- `Published` and `Archived` versions are immutable (structure cannot change)
- A `Draft` becomes `Published` via the publish endpoint; on publish, the previous current published version is moved to `Archived`

---

### TemplateSection
Optional grouping of fields within a TemplateVersion. A template with no sections has all fields at root level.

```
TemplateSection
  Id                  Guid (UUID7)
  TemplateVersionId   Guid
  Name                string
  Description         string?
  Order               int
  IsCollapsible       bool
```

---

### TemplateField
A slot within a template (optionally within a section).

```
TemplateField
  Id                  Guid (UUID7)
  TemplateVersionId   Guid
  SectionId           Guid?           // null = root-level field
  Key                 string          // machine-readable, unique per template version
  Label               string          // human-readable
  HelpText            string?
  Order               int
  IsRequired          bool
  MinOccurrences      int             // default 0
  MaxOccurrences      int?            // null = unbounded
  IsOpen              bool            // if true, AllowedTypes is ignored
  CompositionMode     CompositionMode // Inline | Reference
  PrimitiveType       PrimitiveType?  // set if field is a primitive
  TemplateId          Guid?           // set if field references a user-defined template
  FieldConfig         jsonb?          // per-type settings (PickList options, Link schemes, Text max length, etc.)
```

**Constraint:** exactly one of `PrimitiveType` or `TemplateId` must be set (not both, not neither) — unless `IsOpen` is true, in which case `TemplateId` is null and `PrimitiveType` is null.

### FieldConfig Schemas

The `FieldConfig` shape is determined by `PrimitiveType`. Validated by `IFieldConfigValidator` on save. Initial set:

| PrimitiveType | FieldConfig shape |
|---------------|-------------------|
| `Text` | `{ "maxLength": 500, "pattern": "regex?", "multiline": false }` |
| `RichText` | `{ "maxLength": null, "allowedTags": ["p","strong","em","a","ul","ol","li","blockquote","code","pre","h1","h2","h3"] }` |
| `Markdown` | `{ "maxLength": null }` |
| `Boolean` | `{ "trueLabel": "Yes", "falseLabel": "No" }` |
| `PickList` | `{ "options": [{ "value": "...", "label": "..." }], "multiple": false }` |
| `Media` | `{ "allowedMimeTypePrefixes": ["image/"], "maxSizeBytes": null }` |
| `File` | `{ "allowedMimeTypePrefixes": ["application/","text/"], "maxSizeBytes": null }` |
| `Link` | `{ "allowedSchemes": ["http","https","mailto"], "requireText": true }` |
| `Quote` | `{ "requireAttribution": false }` |
| `Separator` | `{}` (no config) |

For Template-typed fields, `FieldConfig` is `null` (the referenced template defines its own structure).

---

### TemplateFieldAllowedType
For constrained fields (`IsOpen = false`) that accept multiple types. Ignored when `IsOpen = true`.

```
TemplateFieldAllowedType
  FieldId             Guid
  PrimitiveType       PrimitiveType?  // one of these two is set
  AllowedTemplateId   Guid?
```

---

### CompositionMode (Enum)

```csharp
public enum CompositionMode
{
    Inline,     // child ContentItem owned by parent; cascade deletes
    Reference   // child ContentItem is independent; shared across parents
}
```

---

### ContentItem
An instance of a TemplateVersion, holding real data.

```
ContentItem
  Id                  Guid (UUID7)
  WorkspaceId         Guid
  TemplateVersionId   Guid
  Status              ContentStatus
  Slug                string?         // optional; unique per workspace + template type
  LocaleCode          string?         // BCP-47, e.g. "en", "fr-CA"
  TranslationGroupId  Guid?           // links locale variants of the same logical content
  PublishAt           DateTimeOffset? // scheduled publish datetime
  PublishedAt         DateTimeOffset?
  ArchivedAt          DateTimeOffset?
  SearchVector        tsvector        // generated/refreshed on save for full-text search (see 03)
  CreatedAt           DateTimeOffset
  UpdatedAt           DateTimeOffset
  CreatedByUserId     Guid?
  UpdatedByUserId     Guid?
  // Soft-delete columns (see Soft Delete Convention)
```

---

### ContentStatus (Enum)

```csharp
public enum ContentStatus
{
    Draft,
    Review,
    Approved,
    Published,
    Archived
}
```

**Allowed transitions:**
- `Draft → Review`
- `Review → Draft` (send back)
- `Review → Approved`
- `Approved → Published` (manual or scheduled via `PublishAt`)
- `Published → Archived`
- `Archived → Draft` (restore)

---

### ContentFieldValue
Stores the actual value for one field occurrence within a ContentItem.

```
ContentFieldValue
  Id                  Guid (UUID7)
  ContentItemId       Guid
  FieldId             Guid            // TemplateField this value satisfies
  Order               int             // for multi-occurrence fields
  ValueKind           ValueKind       // discriminator
  TextValue           string?
  BoolValue           bool?
  MediaAssetId        Guid?           // → MediaAsset
  FileAssetId         Guid?           // → FileAsset
  ChildContentItemId  Guid?           // → ContentItem (for Template-typed fields)
  JsonValue           jsonb?          // for PickList selections, Link metadata, etc.
```

---

### ValueKind (Enum)

```csharp
public enum ValueKind
{
    Text,
    RichText,
    Markdown,
    Boolean,
    PickList,
    Media,
    File,
    Link,
    Quote,
    Separator,
    ChildContent    // references a child ContentItem
}
```

---

### MediaAsset

```
MediaAsset
  Id                  Guid (UUID7)
  WorkspaceId         Guid
  FileName            string
  MimeType            string
  SizeBytes           long
  StorageKey          string          // path or object key within the provider
  StorageProvider     string          // "local" | "s3"
  AltText             string?
  CreatedAt           DateTimeOffset
  CreatedByUserId     Guid?
```

---

### Tag

```
Tag
  Id                  Guid (UUID7)
  WorkspaceId         Guid
  Name                string          // unique per workspace, case-insensitive
```

### ContentItemTag (join)
```
ContentItemTag
  ContentItemId       Guid
  TagId               Guid
```

---

### User

```
User
  Id                  Guid (UUID7)
  Email               string          // unique
  DisplayName         string
  PasswordHash        string          // bcrypt
  Role                UserRole
  MustChangePassword  bool            // true after admin-set or temp password issued
  TimeZoneId          string?         // IANA TZ identifier; admin UI display preference
  IsActive            bool            // operator can deactivate without soft-deleting
  CreatedAt           DateTimeOffset
  LastLoginAt         DateTimeOffset?
  // Soft-delete columns (see Soft Delete Convention)
```

---

### UserRole (Enum)

```csharp
public enum UserRole
{
    Reader,
    Editor,
    TemplateAdmin,
    Admin
}
```

---

### ApiClient
Machine consumer identity for programmatic API access.

```
ApiClient
  Id                  Guid (UUID7)
  Name                string
  Description         string?
  TokenHash           string          // bcrypt hash of the issued token
  Role                UserRole        // reuses same role model
  WorkspaceId         Guid?           // null = all workspaces
  IsActive            bool
  ExpiresAt           DateTimeOffset?
  CreatedAt           DateTimeOffset
  CreatedByUserId     Guid
  LastUsedAt          DateTimeOffset?
```

---

### WebhookEndpoint

```
WebhookEndpoint
  Id                  Guid (UUID7)
  WorkspaceId         Guid
  Name                string
  Url                 string
  Secret              string          // HMAC signing secret (stored encrypted)
  IsActive            bool
  CreatedAt           DateTimeOffset
  CreatedByUserId     Guid
```

### WebhookSubscription (join)
```
WebhookSubscription
  WebhookEndpointId   Guid
  EventType           string          // e.g. "content.published"
```

### WebhookDeliveryLog

```
WebhookDeliveryLog
  Id                  Guid (UUID7)
  WebhookEndpointId   Guid
  EventType           string
  Payload             jsonb
  AttemptCount        int
  LastAttemptAt       DateTimeOffset?
  NextRetryAt         DateTimeOffset?
  StatusCode          int?
  IsDelivered         bool
  IsFailed            bool            // max retries exceeded
  CreatedAt           DateTimeOffset
```

---

### AuditLog

```
AuditLog
  Id                  Guid (UUID7)
  EntityType          string          // e.g. "ContentItem", "Template"
  EntityId            Guid
  Action              AuditAction
  ActorUserId         Guid?
  ActorApiClientId    Guid?
  Timestamp           DateTimeOffset
  ChangeDelta         jsonb?          // before/after diff
  WorkspaceId         Guid?
```

```csharp
public enum AuditAction { Created, Updated, Deleted, StatusChanged }
```

---

## Repository Interfaces (defined in Core, implemented in Infrastructure)

Repository interfaces are DTO-based contracts. They accept query/command DTOs and return DTO projections only; domain entities and EF entities remain internal implementation details and must never cross the repository boundary.

```csharp
public interface IWorkspaceRepository { ... }
public interface ITemplateRepository { ... }
public interface ITemplateVersionRepository { ... }
public interface IContentItemRepository { ... }
public interface IMediaAssetRepository { ... }
public interface ITagRepository { ... }
public interface IUserRepository { ... }
public interface IApiClientRepository { ... }
public interface IWebhookRepository { ... }
public interface IAuditLogRepository { ... }
```

---

## Domain Service Interfaces

```csharp
// Validates a TemplateVersion graph for circular references
public interface ITemplateGraphValidator
{
    ValidationResult ValidateCycles(TemplateVersion version);
}

// Validates a ContentItem's field values against its TemplateVersion
public interface IContentValidator
{
    ValidationResult Validate(ContentItem item, TemplateVersion version);
}

// Validates FieldConfig jsonb against its PrimitiveType schema
public interface IFieldConfigValidator
{
    ValidationResult Validate(PrimitiveType type, JsonElement? config);
}

// Enforces allowed status transitions
public interface IContentLifecycleService
{
    bool CanTransition(ContentStatus from, ContentStatus to);
    Task TransitionAsync(ContentItem item, ContentStatus to, Guid actorId);
}

// Builds the tsvector content for a ContentItem from its field values
public interface IContentSearchVectorBuilder
{
    string Build(ContentItem item, TemplateVersion version);
}

// Scale-out seam: abstracts in-process channel today, outbox table tomorrow
public interface IWebhookQueue
{
    ValueTask EnqueueAsync(WebhookEvent evt, CancellationToken ct = default);
    IAsyncEnumerable<WebhookEvent> DequeueAllAsync(CancellationToken ct = default);
}

// Scale-out seam: abstracts single-instance polling today, leader-elected dispatch tomorrow
public interface IScheduledPublishingDispatcher
{
    Task RunOnceAsync(CancellationToken ct = default);
}
```

---

## Tasks

- [ ] Define all enums in `Cmsify.Core/Domain/Enums/`
- [ ] Define all entity classes in `Cmsify.Core/Domain/Entities/`
- [ ] Define value objects (e.g. `LocaleCode`, `Slug`) in `Cmsify.Core/Domain/ValueObjects/`
- [ ] Define all repository interfaces in `Cmsify.Core/Interfaces/Repositories/`
- [ ] Define repository input/output DTO contracts for all repository interfaces
- [ ] Define all domain service interfaces in `Cmsify.Core/Interfaces/Services/`
- [ ] Implement `ITemplateGraphValidator` (DFS cycle detection)
- [ ] Implement `IContentLifecycleService` (transition guard + state machine)
- [ ] Implement `IContentValidator` (field cardinality, required field checks)
- [ ] Write FluentValidation validators for all create/update request models
- [ ] Unit test: cycle detection (direct cycle, transitive cycle, no cycle)
- [ ] Unit test: lifecycle transitions (all valid and invalid paths)
- [ ] Unit test: content validation (missing required fields, cardinality violations)

---

## Deliverables
- All domain entities defined in `Cmsify.Core`
- All repository DTO contracts and repository/service interfaces defined
- Template cycle detection implemented and unit tested
- Content lifecycle state machine implemented and unit tested
- Content field validation implemented and unit tested
