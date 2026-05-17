# 03 — Database Schema

## Goal
Configure EF Core to map all domain entities to PostgreSQL, establish a migrations strategy, and define indexing and constraints.

---

## EF Core Configuration

- **Provider:** `Npgsql.EntityFrameworkCore.PostgreSQL`
- **Primary keys:** `Guid` (UUID7 via `UUIDNext` library); configured as `HasDefaultValueSql("gen_random_uuid()")` at DB level as fallback, but generated in application code via UUIDNext
- **Naming convention:** snake_case column and table names via `UseSnakeCaseNamingConvention()`
- **JSONB columns:** EF Core owned types or `string` columns mapped with `.HasColumnType("jsonb")` for `ChangeDelta`, `Payload`, `JsonValue`, `FieldConfig`
- **Soft deletes:** user-visible entities use `IsDeleted` + `DeletedAt` + `DeletedByUserId`. A global EF query filter excludes soft-deleted rows by default. Join tables, session/token tables, and append-only log tables are hard-deleted. See `02_core_domain.md` for the canonical list.
- **Concurrency tokens:** all mutable user-visible entities map PostgreSQL's `xmin` system column as an EF Core concurrency token. See `25_cross_cutting.md`.
- **Migration locking:** the startup `MigrateAsync()` call acquires a PostgreSQL advisory lock (`pg_advisory_lock(0xCM51FYM1G)`) before applying migrations so that concurrent process starts (even though MVP is single-instance) cannot collide.

---

## DbContext

```csharp
// Cmsify.Infrastructure/Persistence/CmsifyDbContext.cs
public class CmsifyDbContext : DbContext
{
    public DbSet<Workspace> Workspaces { get; set; }
    public DbSet<Template> Templates { get; set; }
    public DbSet<TemplateVersion> TemplateVersions { get; set; }
    public DbSet<TemplateSection> TemplateSections { get; set; }
    public DbSet<TemplateField> TemplateFields { get; set; }
    public DbSet<TemplateFieldAllowedType> TemplateFieldAllowedTypes { get; set; }
    public DbSet<ContentItem> ContentItems { get; set; }
    public DbSet<ContentFieldValue> ContentFieldValues { get; set; }
    public DbSet<MediaAsset> MediaAssets { get; set; }
    public DbSet<Tag> Tags { get; set; }
    public DbSet<ContentItemTag> ContentItemTags { get; set; }
    public DbSet<User> Users { get; set; }
    public DbSet<ApiClient> ApiClients { get; set; }
    public DbSet<WebhookEndpoint> WebhookEndpoints { get; set; }
    public DbSet<WebhookSubscription> WebhookSubscriptions { get; set; }
    public DbSet<WebhookDeliveryLog> WebhookDeliveryLogs { get; set; }
    public DbSet<AuditLog> AuditLogs { get; set; }
}
```

Register `AuditInterceptor` on `DbContextOptionsBuilder` in `AddDbContext`.

---

## Entity Configurations (one file per entity)

### WorkspaceConfiguration
```csharp
builder.HasKey(w => w.Id);
builder.HasIndex(w => w.Slug).IsUnique();
builder.Property(w => w.Name).HasMaxLength(200).IsRequired();
builder.Property(w => w.Slug).HasMaxLength(100).IsRequired();
```

### TemplateConfiguration
```csharp
builder.HasKey(t => t.Id);
builder.HasIndex(t => new { t.WorkspaceId, t.Slug }).IsUnique();
builder.HasOne<Workspace>().WithMany().HasForeignKey(t => t.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
builder.HasOne<TemplateVersion>().WithMany().HasForeignKey(t => t.CurrentVersionId).OnDelete(DeleteBehavior.SetNull);
builder.Property(t => t.PackageNamespace).HasMaxLength(200);
builder.Property(t => t.PackageId).HasMaxLength(200);
builder.Property(t => t.PackageVersion).HasMaxLength(50);
```

### TemplateVersionConfiguration
```csharp
builder.HasKey(v => v.Id);
builder.HasIndex(v => new { v.TemplateId, v.VersionNumber }).IsUnique();
builder.HasOne<Template>().WithMany().HasForeignKey(v => v.TemplateId).OnDelete(DeleteBehavior.Cascade);
```

### TemplateSectionConfiguration
```csharp
builder.HasKey(s => s.Id);
builder.HasOne<TemplateVersion>().WithMany().HasForeignKey(s => s.TemplateVersionId).OnDelete(DeleteBehavior.Cascade);
builder.HasIndex(s => new { s.TemplateVersionId, s.Order });
```

### TemplateFieldConfiguration
```csharp
builder.HasKey(f => f.Id);
builder.HasIndex(f => new { f.TemplateVersionId, f.Key }).IsUnique();
builder.HasOne<TemplateVersion>().WithMany().HasForeignKey(f => f.TemplateVersionId).OnDelete(DeleteBehavior.Cascade);
builder.HasOne<TemplateSection>().WithMany().HasForeignKey(f => f.SectionId).OnDelete(DeleteBehavior.SetNull).IsRequired(false);
builder.Property(f => f.Key).HasMaxLength(100).IsRequired();
builder.Property(f => f.CompositionMode).HasConversion<string>();
builder.Property(f => f.FieldConfig).HasColumnType("jsonb");
```

### TemplateVersionConfiguration (continued)
```csharp
builder.Property(v => v.Status).HasConversion<string>();
// Partial unique index: only one Draft per Template at a time
builder.HasIndex(v => v.TemplateId)
       .IsUnique()
       .HasFilter("status = 'Draft'")
       .HasDatabaseName("ix_template_versions_one_draft_per_template");
```

### TemplateFieldAllowedTypeConfiguration
```csharp
builder.HasKey(a => new { a.FieldId, a.PrimitiveType, a.AllowedTemplateId }); // composite key
builder.HasOne<TemplateField>().WithMany().HasForeignKey(a => a.FieldId).OnDelete(DeleteBehavior.Cascade);
```

### ContentItemConfiguration
```csharp
builder.HasKey(c => c.Id);
builder.HasIndex(c => new { c.WorkspaceId, c.TemplateVersionId, c.Slug }).IsUnique().HasFilter("slug IS NOT NULL AND is_deleted = false");
builder.HasIndex(c => c.TranslationGroupId);
builder.HasIndex(c => new { c.Status, c.PublishAt }); // for scheduled publish query
builder.HasIndex(c => c.WorkspaceId);
builder.Property(c => c.Status).HasConversion<string>();
builder.Property(c => c.LocaleCode).HasMaxLength(20);

// Full-text search column (PostgreSQL tsvector)
builder.Property(c => c.SearchVector)
       .HasColumnType("tsvector")
       .ValueGeneratedOnAddOrUpdate();
builder.HasIndex(c => c.SearchVector).HasMethod("GIN");

// Soft delete: global query filter
builder.HasQueryFilter(c => !c.IsDeleted);
```

**SearchVector refresh:** maintained by application code on every `ContentItem` insert/update via `IContentSearchVectorBuilder` (concatenates the title field plus searchable primitive values: `Text`, `RichText` stripped of HTML, `Markdown` stripped, `Link` text, `Quote` body). Set via `EF.Functions.ToTsVector("english", text)` in the repository.

### ContentFieldValueConfiguration
```csharp
builder.HasKey(v => v.Id);
builder.HasOne<ContentItem>().WithMany().HasForeignKey(v => v.ContentItemId).OnDelete(DeleteBehavior.Cascade);
builder.HasIndex(v => new { v.ContentItemId, v.FieldId, v.Order });
builder.Property(v => v.ValueKind).HasConversion<string>();
builder.Property(v => v.JsonValue).HasColumnType("jsonb");
// Self-referencing child content: no cascade on ChildContentItemId (handle in application layer)
```

### MediaAssetConfiguration
```csharp
builder.HasKey(m => m.Id);
builder.HasIndex(m => m.WorkspaceId);
builder.Property(m => m.StorageProvider).HasMaxLength(50);
```

### TagConfiguration
```csharp
builder.HasKey(t => t.Id);
builder.HasIndex(t => new { t.WorkspaceId, t.Name }).IsUnique();
```

### ContentItemTagConfiguration
```csharp
builder.HasKey(ct => new { ct.ContentItemId, ct.TagId });
builder.HasOne<ContentItem>().WithMany().HasForeignKey(ct => ct.ContentItemId).OnDelete(DeleteBehavior.Cascade);
builder.HasOne<Tag>().WithMany().HasForeignKey(ct => ct.TagId).OnDelete(DeleteBehavior.Cascade);
```

### UserConfiguration
```csharp
builder.HasKey(u => u.Id);
builder.HasIndex(u => u.Email).IsUnique();
builder.Property(u => u.Role).HasConversion<string>();
builder.Property(u => u.PasswordHash).HasMaxLength(500);
```

### ApiClientConfiguration
```csharp
builder.HasKey(a => a.Id);
builder.HasIndex(a => a.WorkspaceId);
builder.Property(a => a.Role).HasConversion<string>();
builder.Property(a => a.TokenHash).HasMaxLength(500);
```

### WebhookEndpointConfiguration
```csharp
builder.HasKey(w => w.Id);
builder.HasOne<Workspace>().WithMany().HasForeignKey(w => w.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
```

### WebhookSubscriptionConfiguration
```csharp
builder.HasKey(ws => new { ws.WebhookEndpointId, ws.EventType });
builder.HasOne<WebhookEndpoint>().WithMany().HasForeignKey(ws => ws.WebhookEndpointId).OnDelete(DeleteBehavior.Cascade);
```

### WebhookDeliveryLogConfiguration
```csharp
builder.HasKey(d => d.Id);
builder.HasIndex(d => new { d.IsDelivered, d.IsFailed, d.NextRetryAt }); // for retry query
builder.Property(d => d.Payload).HasColumnType("jsonb");
```

### AuditLogConfiguration
```csharp
builder.HasKey(a => a.Id);
builder.HasIndex(a => new { a.EntityType, a.EntityId });
builder.HasIndex(a => a.WorkspaceId);
builder.HasIndex(a => a.Timestamp);
builder.Property(a => a.ChangeDelta).HasColumnType("jsonb");
builder.Property(a => a.Action).HasConversion<string>();
// No FK to User/ApiClient — audit log is append-only and must survive actor deletion
```

---

## Migrations Strategy

- **Tool:** EF Core Migrations (`dotnet ef`)
- **Location:** `Cmsify.Infrastructure/Persistence/Migrations/`
- **Startup behavior:** `app.Services.GetRequiredService<CmsifyDbContext>().Database.MigrateAsync()` on API startup — applies pending migrations automatically
- **Naming convention:** descriptive names, e.g. `InitialSchema`, `AddTranslationGroup`, `AddPackageProvenance`
- **No hand-edited SQL in migrations** — EF-generated only; raw SQL only for seeding primitives

---

## Seed Data

On first run (via `IDbSeeder` called after migration):
- Insert system `User` (admin) if no users exist — credentials from config/env, with `MustChangePassword = true`
- Insert a default `Workspace` if none exists

Primitive types are **not** seeded as `Template` rows — they exist only as the `PrimitiveType` enum and are surfaced through API responses.

---

## Tasks

- [x] Install `Npgsql.EntityFrameworkCore.PostgreSQL`, `EFCore.NamingConventions`, `UUIDNext`
- [x] Implement `CmsifyDbContext` with all `DbSet` properties
- [x] Implement all `IEntityTypeConfiguration<T>` classes
- [x] Register `CmsifyDbContext` in `IServiceCollection` extension with snake_case naming
- [x] Register `AuditInterceptor` on `DbContextOptionsBuilder`
- [ ] Create and apply initial migration (`InitialSchema`) _(migration created; applying is blocked by PostgreSQL authentication for user `cmsify`)_
- [x] Implement `IDbSeeder` for default admin user and default workspace; primitives remain enum-only per phase 02
- [x] Wire `MigrateAsync()` + seeder call in `Cmsify.Api` startup
- [ ] Verify all indexes exist via `\d tablename` in psql _(blocked until the migration can be applied against PostgreSQL)_
- [x] Write infrastructure unit tests for key schema/model configuration

---

## Deliverables
- [x] `CmsifyDbContext` fully configured
- [x] All entity configurations implemented
- [ ] Initial migration created and applies cleanly against a fresh Postgres instance _(migration created; apply blocked by PostgreSQL authentication for user `cmsify`)_
- [x] Seed data: enum-only primitives, default admin user, default workspace
- [x] Database model ready for API layer to be built against
