# Content Version Unification (Backend) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Promote `ContentVersion` to be the sole carrier of content fields and workflow lifecycle (Draft→Review→Approved→Published→Archived), shrinking `ContentItem` to a lightweight slug/template/locale identity header, so a single item can have multiple independently-editable, independently-scheduled date-bounded versions.

**Architecture:** `ContentVersion.Status` changes type from `ContentVersionStatus` (Published/Retired) to the full `ContentStatus` enum already used by `ContentItem` today, and gains its own `PublishAt`/publish-lease/audit fields. `ContentItem` drops `Status`, `FieldValues`, and all publish-scheduling fields. This is a breaking change to `ContentController`'s public API shape (confirmed acceptable: no external consumers of the TypeScript SDK yet, project is pre-release) — old item-level workflow routes (`/content/{id}/submit`, `/publish`, etc.) are replaced by version-level routes (`/content/{id}/versions/{versionNumber}/submit`, etc.).

**Tech Stack:** .NET 9 / C#, EF Core (Npgsql/Postgres), xUnit v3, Testcontainers.PostgreSql, FluentValidation, TypeScript SDK (openapi-typescript + hand-written client wrapper).

**Spec:** `C:\Users\jon\.claude\plans\stay-on-this-branch-wondrous-floyd.md` (approved architecture design — this plan implements its "Data Model Changes," "Service Changes," and "Migration Plan" sections; UI Changes are covered by a separate follow-up frontend plan, written after this one lands, since the frontend depends on the exact contracts this plan produces).

## Global Constraints

- No backward-compatibility shim: existing `ContentController` item-level workflow endpoints are replaced, not preserved. The TypeScript SDK (`sdk/typescript/`) is updated in this same plan to match.
- `ContentVersionConfiguration`'s existing unique index — one Published, null/null-window version per `ContentItemId` — must be preserved by any Status/column change.
- Every new/changed EF column follows the repo's existing snake_case convention (`ConfigureEntityId()`, `HasConversion<string>()` for enums, explicit `HasMaxLength`).
- All new backend logic follows TDD (failing test → minimal implementation → passing test → commit) per superpowers:test-driven-development.
- Migrations in this repo are additive-then-cleanup in style elsewhere, but since this is pre-release with no phased-rollout requirement, this plan uses **one squashed migration** that adds new columns, backfills data, and drops the old columns/table in a single `Up()`.
- `CHANGELOG.md` (Keep a Changelog format) must get a new entry explicitly calling this out as a **breaking change**, framed as acceptable pre-release — see the final task in this plan.

---

### Task 1: Domain model — extend ContentVersion, shrink ContentItem

**Files:**
- Modify: `src/Cmsify.Core/Domain/Entities/ContentModels.cs`
- Modify: `src/Cmsify.Core/Domain/Enums/DomainEnums.cs`

**Interfaces:**
- Produces: `ContentVersion.Status` is now typed `ContentStatus` (not `ContentVersionStatus`, which is deleted). `ContentVersion` gains `PublishAt`, `PublishLeaseOwner`, `PublishLeaseToken`, `PublishLeaseExpiresAt`, `CreatedAt`, `UpdatedAt`, `CreatedByUserId`, `UpdatedByUserId`. `ContentVersion.PublishedAt` becomes nullable. `ContentVersion.RetiredAt` is renamed `ArchivedAt`. `ContentItem` loses `Status`, `FieldValues`, `PublishAt`, `PendingEffectiveStartAt`, `PendingEffectiveEndAt`, `PublishedAt`, `ArchivedAt`, `PublishLeaseOwner`, `PublishLeaseToken`, `PublishLeaseExpiresAt`. `ContentFieldValue` class is deleted entirely.

- [ ] **Step 1: Remove `ContentVersionStatus` and keep `ContentStatus` as the single shared enum**

In `src/Cmsify.Core/Domain/Enums/DomainEnums.cs`, delete this block:

```csharp
public enum ContentVersionStatus
{
    Published,
    Retired
}
```

`ContentStatus` (Draft, Review, Approved, Published, Archived) is unchanged and now used by both `ContentItem`... actually only by `ContentVersion` after this task (see Step 2).

- [ ] **Step 2: Rewrite `ContentItem`, delete `ContentFieldValue`, rewrite `ContentVersion`**

Replace the full content of `src/Cmsify.Core/Domain/Entities/ContentModels.cs` with:

```csharp
using System.Text.Json;
using Cmsify.Core.Domain.Enums;

namespace Cmsify.Core.Domain.Entities;

public sealed class ContentItem : SoftDeletableEntity
{
    public Guid WorkspaceId { get; set; }

    public Guid TemplateVersionId { get; set; }

    public string? Slug { get; set; }

    public string? LocaleCode { get; set; }

    public Guid? TranslationGroupId { get; set; }

    public string? SearchVector { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public Guid? UpdatedByUserId { get; set; }

    public IList<ContentItemTag> Tags { get; } = new List<ContentItemTag>();
}

public sealed class ContentItemTag
{
    public Guid ContentItemId { get; set; }

    public Guid TagId { get; set; }
}

public sealed class ContentVersion : Entity
{
    public Guid ContentItemId { get; set; }

    public Guid WorkspaceId { get; set; }

    public int VersionNumber { get; set; }

    public ContentStatus Status { get; set; } = ContentStatus.Draft;

    public Guid TemplateVersionId { get; set; }

    public string? Slug { get; set; }

    public string? LocaleCode { get; set; }

    public Guid? TranslationGroupId { get; set; }

    public IList<string> Tags { get; set; } = new List<string>();

    public DateTimeOffset? EffectiveStartAt { get; set; }

    public DateTimeOffset? EffectiveEndAt { get; set; }

    public DateTimeOffset? PublishAt { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }

    public DateTimeOffset? ArchivedAt { get; set; }

    public Guid? PublishedByUserId { get; set; }

    public int? RolledBackFromVersionNumber { get; set; }

    public string? PublishLeaseOwner { get; set; }

    public Guid? PublishLeaseToken { get; set; }

    public DateTimeOffset? PublishLeaseExpiresAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Guid? CreatedByUserId { get; set; }

    public Guid? UpdatedByUserId { get; set; }

    public IList<ContentVersionFieldValue> FieldValues { get; } = new List<ContentVersionFieldValue>();
}

public sealed class ContentVersionFieldValue : Entity
{
    public Guid ContentVersionId { get; set; }

    public Guid FieldId { get; set; }

    public int Order { get; set; }

    public ValueKind ValueKind { get; set; }

    public string? TextValue { get; set; }

    /// <summary>Label selected at publication time for a pick-list value.</summary>
    public string? DisplayLabel { get; set; }

    public bool? BoolValue { get; set; }

    public Guid? MediaAssetId { get; set; }

    public Guid? FileAssetId { get; set; }

    public Guid? ChildContentItemId { get; set; }

    public JsonElement? JsonValue { get; set; }
}
```

Note: `ContentItem` no longer extends anything that provides `CreatedAt`/`UpdatedAt` differently — it still extends `SoftDeletableEntity` (unchanged base class chain, so `Id`, `CreatedAt`, `UpdatedAt`, `IsDeleted`, `DeletedAt`, `DeletedByUserId` are still present via inheritance). `ContentVersion` extends plain `Entity` (just `Id`) as before, since it now declares its own `CreatedAt`/`UpdatedAt` directly (it needs independent semantics — a version's `CreatedAt` is when the draft was started, unrelated to `ContentItem.CreatedAt`).

- [ ] **Step 3: Remove the now-dead `ContentFieldValues` DbSet**

In `src/Cmsify.Infrastructure/Persistence/CmsifyDbContext.cs`, delete the line:

```csharp
    public DbSet<ContentFieldValue> ContentFieldValues => Set<ContentFieldValue>();
```

(`DbSet<ContentVersionFieldValue> ContentVersionFieldValues => Set<ContentVersionFieldValue>();` already exists a couple of lines below it and needs no change.)

- [ ] **Step 4: Build to confirm compile errors are only in the expected downstream files**

Run: `dotnet build src/Cmsify.Core/Cmsify.Core.csproj`
Expected: FAIL — errors in `DomainServices.cs`, `ContentLifecycleService.cs` (Task 4), and every other project referencing the removed members (expected; fixed in later tasks). Confirm no errors *within* `ContentModels.cs`/`DomainEnums.cs` themselves.

- [ ] **Step 5: Commit**

```bash
git add src/Cmsify.Core/Domain/Entities/ContentModels.cs src/Cmsify.Core/Domain/Enums/DomainEnums.cs src/Cmsify.Infrastructure/Persistence/CmsifyDbContext.cs
git commit -m "$(cat <<'EOF'
Promote ContentVersion to sole content+lifecycle carrier, shrink ContentItem

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01C9zyjQzBXAgvN5wKsUSRvJ
EOF
)"
```

---

### Task 2: Core interfaces — retarget lifecycle/publishing service contracts

**Files:**
- Modify: `src/Cmsify.Core/Interfaces/Services/DomainServices.cs`

**Interfaces:**
- Consumes: `ContentVersion`, `ContentStatus` from Task 1.
- Produces: `IContentLifecycleService.CanTransition(ContentStatus, ContentStatus, bool)` (unchanged signature), `IContentLifecycleService.TransitionAsync(ContentVersion version, ContentStatus to, Guid actorId, bool allowOverride = false)`. `IContentPublishingService.PublishAsync(ContentVersion version, Guid? actorUserId = null, CancellationToken ct = default)` returning `Task<ContentPublishResult>`. `ContentEffectiveRange` record is deleted (versions carry their own window from creation, so publish-time no longer takes a range parameter). `IContentValidator.Validate(ContentVersion version, TemplateVersion templateVersion)` and `IContentSearchVectorBuilder.Build(ContentVersion version, TemplateVersion templateVersion)` (both previously took `ContentItem`, since field values now live on `ContentVersion` instead).

- [ ] **Step 1: Edit the interfaces**

In `src/Cmsify.Core/Interfaces/Services/DomainServices.cs`, replace:

```csharp
public interface IContentLifecycleService
{
    bool CanTransition(ContentStatus from, ContentStatus to, bool allowOverride = false);

    Task TransitionAsync(ContentItem item, ContentStatus to, Guid actorId, bool allowOverride = false);
}
```

with:

```csharp
public interface IContentLifecycleService
{
    bool CanTransition(ContentStatus from, ContentStatus to, bool allowOverride = false);

    Task TransitionAsync(ContentVersion version, ContentStatus to, Guid actorId, bool allowOverride = false);
}
```

Replace:

```csharp
public sealed record ContentEffectiveRange(DateTimeOffset? StartAt, DateTimeOffset? EndAt)
{
    public bool IsDefault => !StartAt.HasValue && !EndAt.HasValue;
}

public sealed record ContentPublishResult(ContentVersion Version, IReadOnlyList<string> Warnings);

public interface IContentPublishingService
{
    Task<ContentPublishResult> PublishSnapshotAsync(
        ContentItem content,
        ContentEffectiveRange effectiveRange,
        int? rolledBackFromVersionNumber = null,
        Guid? actorUserId = null,
        CancellationToken ct = default);
}
```

with:

```csharp
public sealed record ContentPublishResult(ContentVersion Version, IReadOnlyList<string> Warnings);

public interface IContentPublishingService
{
    Task<ContentPublishResult> PublishAsync(
        ContentVersion version,
        Guid? actorUserId = null,
        CancellationToken ct = default);
}
```

- [ ] **Step 2: Retarget `IContentValidator` and `IContentSearchVectorBuilder` onto `ContentVersion`**

Both currently take `ContentItem item` to read its field values for validation/search-indexing — those field values now live on `ContentVersion`. Replace:

```csharp
public interface IContentValidator
{
    ValidationResult Validate(ContentItem item, TemplateVersion version);
}
```

with:

```csharp
public interface IContentValidator
{
    ValidationResult Validate(ContentVersion version, TemplateVersion templateVersion);
}
```

Replace:

```csharp
public interface IContentSearchVectorBuilder
{
    string Build(ContentItem item, TemplateVersion version);
}
```

with:

```csharp
public interface IContentSearchVectorBuilder
{
    string Build(ContentVersion version, TemplateVersion templateVersion);
}
```

- [ ] **Step 3: Commit**

```bash
git add src/Cmsify.Core/Interfaces/Services/DomainServices.cs
git commit -m "$(cat <<'EOF'
Retarget content lifecycle/publishing contracts onto ContentVersion

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01C9zyjQzBXAgvN5wKsUSRvJ
EOF
)"
```

---

### Task 3: ContentLifecycleService — retarget to ContentVersion (TDD)

**Files:**
- Modify: `src/Cmsify.Core/Services/ContentLifecycleService.cs`
- Modify: `tests/Cmsify.Core.Tests/ContentLifecycleServiceTests.cs`

**Interfaces:**
- Consumes: `IContentLifecycleService` (Task 2), `ContentVersion`, `ContentStatus` (Task 1).
- Produces: same transition-table behavior as before, now operating on `ContentVersion.Status`/`UpdatedAt`/`PublishedAt`/`ArchivedAt` instead of `ContentItem` equivalents.

- [ ] **Step 1: Update the failing tests first**

Replace the full content of `tests/Cmsify.Core.Tests/ContentLifecycleServiceTests.cs`:

```csharp
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Exceptions;
using Cmsify.Core.Services;

namespace Cmsify.Core.Tests;

public sealed class ContentLifecycleServiceTests
{
    [Theory]
    [InlineData(ContentStatus.Draft, ContentStatus.Review)]
    [InlineData(ContentStatus.Review, ContentStatus.Draft)]
    [InlineData(ContentStatus.Review, ContentStatus.Approved)]
    [InlineData(ContentStatus.Approved, ContentStatus.Published)]
    [InlineData(ContentStatus.Published, ContentStatus.Archived)]
    [InlineData(ContentStatus.Archived, ContentStatus.Draft)]
    public void CanTransition_ReturnsTrue_ForAllowedTransitions(ContentStatus from, ContentStatus to)
    {
        var service = new ContentLifecycleService();

        Assert.True(service.CanTransition(from, to));
    }

    [Theory]
    [InlineData(ContentStatus.Draft, ContentStatus.Published)]
    [InlineData(ContentStatus.Approved, ContentStatus.Draft)]
    [InlineData(ContentStatus.Archived, ContentStatus.Published)]
    public void CanTransition_ReturnsFalse_ForInvalidTransitions(ContentStatus from, ContentStatus to)
    {
        var service = new ContentLifecycleService();

        Assert.False(service.CanTransition(from, to));
    }

    [Fact]
    public async Task TransitionAsync_UpdatesStatusAndActor_ForAllowedTransition()
    {
        var actorId = Guid.CreateVersion7();
        var version = new ContentVersion
        {
            WorkspaceId = Guid.CreateVersion7(),
            TemplateVersionId = Guid.CreateVersion7(),
            Status = ContentStatus.Approved
        };

        await new ContentLifecycleService().TransitionAsync(version, ContentStatus.Published, actorId);

        Assert.Equal(ContentStatus.Published, version.Status);
        Assert.Equal(actorId, version.UpdatedByUserId);
        Assert.NotNull(version.PublishedAt);
    }

    [Fact]
    public async Task TransitionAsync_Throws_ForInvalidTransition()
    {
        var version = new ContentVersion
        {
            WorkspaceId = Guid.CreateVersion7(),
            TemplateVersionId = Guid.CreateVersion7(),
            Status = ContentStatus.Draft
        };

        await Assert.ThrowsAsync<DomainException>(() => new ContentLifecycleService().TransitionAsync(version, ContentStatus.Published, Guid.CreateVersion7()));
    }

    [Theory]
    [InlineData(ContentStatus.Draft, ContentStatus.Published)]
    [InlineData(ContentStatus.Review, ContentStatus.Published)]
    public void CanTransition_ReturnsTrue_ForOverrideTransitions_WhenAllowed(ContentStatus from, ContentStatus to)
    {
        var service = new ContentLifecycleService();

        Assert.True(service.CanTransition(from, to, allowOverride: true));
    }

    [Theory]
    [InlineData(ContentStatus.Draft, ContentStatus.Published)]
    [InlineData(ContentStatus.Review, ContentStatus.Published)]
    public void CanTransition_ReturnsFalse_ForOverrideTransitions_WhenNotAllowed(ContentStatus from, ContentStatus to)
    {
        var service = new ContentLifecycleService();

        Assert.False(service.CanTransition(from, to));
    }

    [Fact]
    public void CanTransition_ReturnsFalse_ForArchivedToPublished_EvenWithOverride()
    {
        var service = new ContentLifecycleService();

        Assert.False(service.CanTransition(ContentStatus.Archived, ContentStatus.Published, allowOverride: true));
    }

    [Fact]
    public async Task TransitionAsync_UpdatesStatus_ForOverrideTransition_WhenAllowed()
    {
        var actorId = Guid.CreateVersion7();
        var version = new ContentVersion
        {
            WorkspaceId = Guid.CreateVersion7(),
            TemplateVersionId = Guid.CreateVersion7(),
            Status = ContentStatus.Draft
        };

        await new ContentLifecycleService().TransitionAsync(version, ContentStatus.Published, actorId, allowOverride: true);

        Assert.Equal(ContentStatus.Published, version.Status);
        Assert.Equal(actorId, version.UpdatedByUserId);
        Assert.NotNull(version.PublishedAt);
    }

    [Fact]
    public async Task TransitionAsync_SetsArchivedAt_ForPublishedToArchived()
    {
        var version = new ContentVersion
        {
            WorkspaceId = Guid.CreateVersion7(),
            TemplateVersionId = Guid.CreateVersion7(),
            Status = ContentStatus.Published,
            PublishedAt = DateTimeOffset.UtcNow.AddDays(-1)
        };

        await new ContentLifecycleService().TransitionAsync(version, ContentStatus.Archived, Guid.CreateVersion7());

        Assert.Equal(ContentStatus.Archived, version.Status);
        Assert.NotNull(version.ArchivedAt);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail to compile (ContentLifecycleService still takes ContentItem)**

Run: `dotnet test tests/Cmsify.Core.Tests/Cmsify.Core.Tests.csproj --filter FullyQualifiedName~ContentLifecycleServiceTests`
Expected: FAIL — build error, `ContentLifecycleService.TransitionAsync` does not accept `ContentVersion`.

- [ ] **Step 3: Update the implementation**

Replace the full content of `src/Cmsify.Core/Services/ContentLifecycleService.cs`:

```csharp
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Exceptions;
using Cmsify.Core.Interfaces.Services;

namespace Cmsify.Core.Services;

public sealed class ContentLifecycleService : IContentLifecycleService
{
    private static readonly IReadOnlySet<(ContentStatus From, ContentStatus To)> AllowedTransitions = new HashSet<(ContentStatus, ContentStatus)>
    {
        (ContentStatus.Draft, ContentStatus.Review),
        (ContentStatus.Review, ContentStatus.Draft),
        (ContentStatus.Review, ContentStatus.Approved),
        (ContentStatus.Approved, ContentStatus.Published),
        (ContentStatus.Published, ContentStatus.Archived),
        (ContentStatus.Archived, ContentStatus.Draft)
    };

    private static readonly IReadOnlySet<(ContentStatus From, ContentStatus To)> AllowedOverrideTransitions = new HashSet<(ContentStatus, ContentStatus)>
    {
        (ContentStatus.Draft, ContentStatus.Published),
        (ContentStatus.Review, ContentStatus.Published)
    };

    public bool CanTransition(ContentStatus from, ContentStatus to, bool allowOverride = false)
    {
        return from == to
            || AllowedTransitions.Contains((from, to))
            || (allowOverride && AllowedOverrideTransitions.Contains((from, to)));
    }

    public Task TransitionAsync(ContentVersion version, ContentStatus to, Guid actorId, bool allowOverride = false)
    {
        ArgumentNullException.ThrowIfNull(version);

        if (!CanTransition(version.Status, to, allowOverride))
        {
            throw new DomainException($"Content version cannot transition from {version.Status} to {to}.");
        }

        var now = DateTimeOffset.UtcNow;
        version.Status = to;
        version.UpdatedAt = now;
        version.UpdatedByUserId = actorId;

        if (to == ContentStatus.Published)
        {
            version.PublishedAt ??= now;
        }
        else if (to == ContentStatus.Archived)
        {
            version.ArchivedAt ??= now;
        }

        return Task.CompletedTask;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Cmsify.Core.Tests/Cmsify.Core.Tests.csproj --filter FullyQualifiedName~ContentLifecycleServiceTests`
Expected: PASS (all 10 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Cmsify.Core/Services/ContentLifecycleService.cs tests/Cmsify.Core.Tests/ContentLifecycleServiceTests.cs
git commit -m "$(cat <<'EOF'
Retarget ContentLifecycleService transitions onto ContentVersion

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01C9zyjQzBXAgvN5wKsUSRvJ
EOF
)"
```

---

### Task 4: EF configurations — update ContentItem/ContentVersion mapping, delete ContentFieldValueConfiguration

**Files:**
- Modify: `src/Cmsify.Infrastructure/Persistence/Configurations/ContentItemConfiguration.cs`
- Modify: `src/Cmsify.Infrastructure/Persistence/Configurations/ContentVersionConfiguration.cs`
- Delete: `src/Cmsify.Infrastructure/Persistence/Configurations/ContentFieldValueConfiguration.cs`

**Interfaces:**
- Consumes: `ContentItem`, `ContentVersion`, `ContentVersionFieldValue` (Task 1).
- Produces: EF model matching the new entity shapes; this task does not touch the database directly (Task 5 generates the migration from this updated model).

- [ ] **Step 1: Rewrite `ContentItemConfiguration`**

Replace the full content of `src/Cmsify.Infrastructure/Persistence/Configurations/ContentItemConfiguration.cs`:

```csharp
using Cmsify.Core.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NpgsqlTypes;

namespace Cmsify.Infrastructure.Persistence.Configurations;

public sealed class ContentItemConfiguration : IEntityTypeConfiguration<ContentItem>
{
    public void Configure(EntityTypeBuilder<ContentItem> builder)
    {
        builder.ConfigureEntityId();
        builder.ConfigureSoftDelete();
        builder.ConfigureXminConcurrency();

        builder.HasIndex(content => new { content.WorkspaceId, content.TemplateVersionId, content.Slug })
            .IsUnique()
            .HasFilter("slug IS NOT NULL AND is_deleted = false");
        builder.HasIndex(content => content.TranslationGroupId);
        builder.HasIndex(content => content.WorkspaceId);
        builder.HasIndex(content => content.SearchVector).HasMethod("GIN");

        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(content => content.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<TemplateVersion>()
            .WithMany()
            .HasForeignKey(content => content.TemplateVersionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(content => content.Slug).HasMaxLength(200);
        builder.Property(content => content.LocaleCode).HasMaxLength(20);
#pragma warning disable CS0618
        builder.Property(content => content.SearchVector)
            .HasColumnType("tsvector")
            .HasConversion(
                value => NpgsqlTsVector.Parse(value ?? string.Empty),
                value => value.ToString());
#pragma warning restore CS0618
    }
}
```

Note what changed from today's version: the `Status` property config and the three `(Status, PublishAt, ...)` indexes are removed (those columns no longer exist on `ContentItem`); `PublishLeaseOwner` maxlength config is removed for the same reason.

- [ ] **Step 2: Rewrite `ContentVersionConfiguration`, delete `ContentFieldValueConfiguration`**

Replace the full content of `src/Cmsify.Infrastructure/Persistence/Configurations/ContentVersionConfiguration.cs`:

```csharp
using Cmsify.Core.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cmsify.Infrastructure.Persistence.Configurations;

public sealed class ContentVersionConfiguration : IEntityTypeConfiguration<ContentVersion>
{
    public void Configure(EntityTypeBuilder<ContentVersion> builder)
    {
        builder.ConfigureEntityId();

        builder.HasIndex(version => new { version.ContentItemId, version.VersionNumber }).IsUnique();
        builder.HasIndex(version => version.ContentItemId)
            .IsUnique()
            .HasFilter("status = 'Published' AND effective_start_at IS NULL AND effective_end_at IS NULL");
        builder.HasIndex(version => version.WorkspaceId);
        builder.HasIndex(version => new { version.ContentItemId, version.Status, version.EffectiveStartAt, version.EffectiveEndAt });
        builder.HasIndex(version => new { version.Status, version.PublishAt });
        builder.HasIndex(version => new { version.Status, version.PublishAt, version.PublishLeaseExpiresAt });

        builder.HasOne<ContentItem>()
            .WithMany()
            .HasForeignKey(version => version.ContentItemId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<TemplateVersion>()
            .WithMany()
            .HasForeignKey(version => version.TemplateVersionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(version => version.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(version => version.Status).HasConversion<string>().HasMaxLength(50);
        builder.Property(version => version.Slug).HasMaxLength(200);
        builder.Property(version => version.LocaleCode).HasMaxLength(20);
        builder.Property(version => version.PublishLeaseOwner).HasMaxLength(200);
        builder.ToTable(table => table.HasCheckConstraint(
            "ck_content_versions_effective_range",
            "(effective_start_at IS NULL AND effective_end_at IS NULL) OR (effective_start_at IS NOT NULL AND effective_end_at IS NOT NULL AND effective_start_at < effective_end_at)"));
        builder.Property(version => version.Tags)
            .HasColumnType("text[]")
            .HasConversion(
                tags => tags.ToArray(),
                array => array.ToList(),
                new Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<IList<string>>(
                    (left, right) => (left ?? new List<string>()).SequenceEqual(right ?? new List<string>()),
                    list => list == null ? 0 : list.Aggregate(0, (hash, item) => HashCode.Combine(hash, item.GetHashCode())),
                    list => (IList<string>)list.ToList()));
    }
}

public sealed class ContentVersionFieldValueConfiguration : IEntityTypeConfiguration<ContentVersionFieldValue>
{
    public void Configure(EntityTypeBuilder<ContentVersionFieldValue> builder)
    {
        builder.ConfigureEntityId();

        builder.HasOne<ContentVersion>()
            .WithMany(version => version.FieldValues)
            .HasForeignKey(value => value.ContentVersionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(value => new { value.ContentVersionId, value.FieldId, value.Order });
        builder.Property(value => value.ValueKind).HasConversion<string>().HasMaxLength(50);
        builder.Property(value => value.TextValue);
        builder.Property(value => value.DisplayLabel).HasMaxLength(200);
        builder.Property(value => value.JsonValue).HasColumnType("jsonb");
    }
}
```

Changed from today's version: `Status` conversion now applies to the wider `ContentStatus` enum (same `HasConversion<string>()` mechanics, just more member names to serialize). Two new indexes added for scheduled-publish claiming (`(Status, PublishAt)`, `(Status, PublishAt, PublishLeaseExpiresAt)`), mirroring the ones removed from `ContentItemConfiguration`. `PublishLeaseOwner` maxlength added (moved from `ContentItemConfiguration`).

Delete the file `src/Cmsify.Infrastructure/Persistence/Configurations/ContentFieldValueConfiguration.cs` entirely (its type, `ContentFieldValue`, no longer exists).

- [ ] **Step 3: Build to confirm the EF model layer compiles**

Run: `dotnet build src/Cmsify.Infrastructure/Cmsify.Infrastructure.csproj`
Expected: FAIL — remaining errors should now only be in `ContentPublishingService.cs`, `ScheduledPublishingRepository.cs`, `ContentItemRepository.cs`, `RepositoryMapping.cs` (fixed in later tasks). No errors in the `Configurations/` folder itself.

- [ ] **Step 4: Commit**

```bash
git add src/Cmsify.Infrastructure/Persistence/Configurations/ContentItemConfiguration.cs src/Cmsify.Infrastructure/Persistence/Configurations/ContentVersionConfiguration.cs
git rm src/Cmsify.Infrastructure/Persistence/Configurations/ContentFieldValueConfiguration.cs
git commit -m "$(cat <<'EOF'
Update EF configurations for the ContentItem/ContentVersion split

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01C9zyjQzBXAgvN5wKsUSRvJ
EOF
)"
```

---

### Task 5: Squashed migration — schema + backfill + drop old columns/table

**Files:**
- Create: `src/Cmsify.Infrastructure/Persistence/Migrations/{timestamp}_UnifyContentVersionLifecycle.cs` (generated, then hand-edited)
- Create: `src/Cmsify.Infrastructure/Persistence/Migrations/{timestamp}_UnifyContentVersionLifecycle.Designer.cs` (generated)
- Modify: `src/Cmsify.Infrastructure/Persistence/Migrations/CmsifyDbContextModelSnapshot.cs` (regenerated)

**Interfaces:**
- Consumes: the updated EF model from Tasks 1 and 4.
- Produces: a Postgres schema matching the new model, with existing `content_items`/`content_versions`/`content_field_values` data migrated, not lost.

EF's migration scaffolding tool computes the exact column/index names (including any Postgres 63-character truncation) from the model diff — hand-guessing those would risk a plan that doesn't match what EF actually generates. So this task generates the migration with the CLI first, then hand-inserts the backfill SQL into the generated file.

- [ ] **Step 1: Generate the migration skeleton**

Run (from the repo root, with the `dotnet-ef` tool available — check `dotnet tool list` first; install with `dotnet tool install --global dotnet-ef` if missing):

```bash
dotnet ef migrations add UnifyContentVersionLifecycle --project src/Cmsify.Infrastructure --startup-project src/Cmsify.Api
```

Expected: succeeds, creates the two files listed above and updates the snapshot. Inspect the generated `Up()` — it should contain, in some order: `AddColumn` for `content_versions.publish_at`/`publish_lease_owner`/`publish_lease_token`/`publish_lease_expires_at`/`created_at`/`updated_at`/`created_by_user_id`/`updated_by_user_id`; `RenameColumn` for `retired_at` → `archived_at` on `content_versions`; `AlterColumn` making `content_versions.published_at` nullable; `DropIndex`/`DropColumn` for the three `content_items` composite indexes and their columns (`status`, `publish_at`, `pending_effective_start_at`, `pending_effective_end_at`, `published_at`, `archived_at`, `publish_lease_owner`, `publish_lease_token`, `publish_lease_expires_at`); `DropTable` for `content_field_values`; `CreateIndex` for the two new `content_versions` scheduled-publish indexes.

If the generated diff is missing any of the above, the model changes from Tasks 1/4 are incomplete — stop and fix them before continuing.

- [ ] **Step 2: Insert the backfill SQL**

In the generated migration file's `Up()` method, insert the following `migrationBuilder.Sql(...)` call **after** all the `AddColumn`/`RenameColumn`/`AlterColumn`/`CreateIndex` statements and **before** the `DropIndex`/`DropColumn`/`DropTable` statements (the backfill needs the new columns to exist and the old ones to still exist and hold data):

```csharp
migrationBuilder.Sql("""
    UPDATE content_versions SET status = 'Archived' WHERE status = 'Retired';

    CREATE TEMP TABLE backfill_version_map (
        content_item_id uuid PRIMARY KEY,
        new_version_id uuid NOT NULL,
        new_version_number int NOT NULL
    ) ON COMMIT DROP;

    INSERT INTO backfill_version_map (content_item_id, new_version_id, new_version_number)
    SELECT
        ci.id,
        gen_random_uuid(),
        COALESCE((SELECT MAX(cv.version_number) FROM content_versions cv WHERE cv.content_item_id = ci.id), 0) + 1
    FROM content_items ci
    WHERE ci.status <> 'Published' AND NOT ci.is_deleted;

    INSERT INTO content_versions (
        id, content_item_id, workspace_id, version_number, status, template_version_id,
        slug, locale_code, translation_group_id, tags, effective_start_at, effective_end_at,
        published_at, archived_at, published_by_user_id, rolled_back_from_version_number,
        publish_at, publish_lease_owner, publish_lease_token, publish_lease_expires_at,
        created_at, updated_at, created_by_user_id, updated_by_user_id
    )
    SELECT
        m.new_version_id, ci.id, ci.workspace_id, m.new_version_number, ci.status, ci.template_version_id,
        ci.slug, ci.locale_code, ci.translation_group_id,
        COALESCE((
            SELECT array_agg(t.name ORDER BY t.name)
            FROM content_item_tags cit
            JOIN tags t ON t.id = cit.tag_id
            WHERE cit.content_item_id = ci.id
        ), ARRAY[]::text[]),
        NULL, NULL,
        NULL, NULL, NULL, NULL,
        CASE WHEN ci.status = 'Approved' THEN ci.publish_at ELSE NULL END,
        NULL, NULL, NULL,
        ci.created_at, ci.updated_at, ci.created_by_user_id, ci.updated_by_user_id
    FROM content_items ci
    JOIN backfill_version_map m ON m.content_item_id = ci.id;

    INSERT INTO content_version_field_values (
        id, content_version_id, field_id, "order", value_kind, text_value, display_label,
        bool_value, media_asset_id, file_asset_id, child_content_item_id, json_value
    )
    SELECT
        gen_random_uuid(), m.new_version_id, cfv.field_id, cfv."order", cfv.value_kind, cfv.text_value, NULL,
        cfv.bool_value, cfv.media_asset_id, cfv.file_asset_id, cfv.child_content_item_id, cfv.json_value
    FROM content_field_values cfv
    JOIN backfill_version_map m ON m.content_item_id = cfv.content_item_id;
    """);
```

This handles both backfill cases: items whose current status is `Published` already have a corresponding Published, null/null-window `ContentVersion` from the existing publish flow (nothing to do); every other item (`Draft`/`Review`/`Approved`/`Archived`, including ones that were never published) gets exactly one new `ContentVersion` row carrying its current field values, so no in-progress draft work is lost.

In `Down()`, add a corresponding comment (do not attempt to reverse the backfill — this migration's `Down()` should still restore the dropped columns/table structurally for emergency rollback, but reversing the data backfill itself is out of scope; add `migrationBuilder.Sql("-- Note: Down() restores dropped columns/table structure but does not reverse the data backfill performed in Up().");` immediately before the restored `CreateTable`/`AddColumn` calls EF already generated for `Down()`).

- [ ] **Step 3: Apply the migration to a local/dev Postgres and verify**

Run: `dotnet ef database update --project src/Cmsify.Infrastructure --startup-project src/Cmsify.Api`
Expected: succeeds with no errors.

Then verify backfill correctness with a direct query:

```sql
SELECT ci.id, ci.slug
FROM content_items ci
WHERE NOT ci.is_deleted
  AND NOT EXISTS (SELECT 1 FROM content_versions cv WHERE cv.content_item_id = ci.id);
```

Expected: zero rows (every non-deleted item has at least one version).

- [ ] **Step 4: Commit**

```bash
git add src/Cmsify.Infrastructure/Persistence/Migrations/
git commit -m "$(cat <<'EOF'
Add migration unifying content lifecycle onto ContentVersion

Squashed migration (pre-release, no phased-rollout requirement): adds
ContentVersion's new lifecycle/scheduling columns, backfills a
ContentVersion for every item not already represented by one, then
drops the now-dead ContentItem columns and content_field_values table.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01C9zyjQzBXAgvN5wKsUSRvJ
EOF
)"
```

---

### Task 6: ContentPublishingService — simplify to publishing an existing version (TDD)

**Files:**
- Modify: `src/Cmsify.Infrastructure/Persistence/ContentPublishingService.cs`
- Create: `tests/Cmsify.Api.Integration.Tests/ContentPublishingServiceTests.cs`

**Interfaces:**
- Consumes: `IContentPublishingService.PublishAsync(ContentVersion, Guid?, CancellationToken)` (Task 2), `ContentVersion`/`ContentStatus` (Task 1).
- Produces: `PublishAsync` transitions an already-existing Draft/Approved `ContentVersion` to Published (no longer creates a new row or copies field values from `ContentItem` — the version already owns its fields, populated at create/update time by Task 9). Pick-list `DisplayLabel` resolution (previously done here, at snapshot time) moves to Task 9's version create/update logic, since fields now live on the version from the moment it's created, not only at publish time.

- [ ] **Step 1: Write the failing test**

Create `tests/Cmsify.Api.Integration.Tests/ContentPublishingServiceTests.cs`:

```csharp
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Cmsify.Api.Integration.Tests;

public sealed class ContentPublishingServiceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("cmsify")
        .WithUsername("cmsify")
        .WithPassword("cmsify")
        .Build();

    private CmsifyDbContext dbContext = null!;

    public async ValueTask InitializeAsync()
    {
        await postgres.StartAsync();
        var options = new DbContextOptionsBuilder<CmsifyDbContext>()
            .UseNpgsql(postgres.GetConnectionString())
            .Options;
        dbContext = new CmsifyDbContext(options);
        await dbContext.Database.MigrateAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await dbContext.DisposeAsync();
        await postgres.DisposeAsync();
    }

    [Fact]
    public async Task PublishAsync_TransitionsExistingDraftVersion_WithoutCreatingNewRow()
    {
        var (item, version) = await SeedDraftVersionAsync(effectiveStartAt: null, effectiveEndAt: null);
        var service = new ContentPublishingService(dbContext, CurrentActorInfo.Anonymous);

        var result = await service.PublishAsync(version, actorUserId: item.CreatedByUserId, TestContext.Current.CancellationToken);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(version.Id, result.Version.Id);
        Assert.Equal(ContentStatus.Published, result.Version.Status);
        Assert.NotNull(result.Version.PublishedAt);
        Assert.Empty(result.Warnings);
        var versionCount = await dbContext.ContentVersions.CountAsync(v => v.ContentItemId == item.Id, TestContext.Current.CancellationToken);
        Assert.Equal(1, versionCount);
    }

    [Fact]
    public async Task PublishAsync_ArchivesPriorDefaultVersion_WhenPublishingNewDefault()
    {
        var (item, firstDefault) = await SeedDraftVersionAsync(effectiveStartAt: null, effectiveEndAt: null);
        var service = new ContentPublishingService(dbContext, CurrentActorInfo.Anonymous);
        await service.PublishAsync(firstDefault, ct: TestContext.Current.CancellationToken);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var secondDefault = await SeedAdditionalVersionAsync(item, effectiveStartAt: null, effectiveEndAt: null);
        await service.PublishAsync(secondDefault, ct: TestContext.Current.CancellationToken);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var reloadedFirst = await dbContext.ContentVersions.AsNoTracking().FirstAsync(v => v.Id == firstDefault.Id, TestContext.Current.CancellationToken);
        Assert.Equal(ContentStatus.Archived, reloadedFirst.Status);
        Assert.NotNull(reloadedFirst.ArchivedAt);
    }

    [Fact]
    public async Task PublishAsync_WarnsOnEqualSpecificityOverlap_ForBoundedRanges()
    {
        var (item, _) = await SeedDraftVersionAsync(effectiveStartAt: null, effectiveEndAt: null);
        var service = new ContentPublishingService(dbContext, CurrentActorInfo.Anonymous);
        var rangeA = await SeedAdditionalVersionAsync(item, DateTimeOffset.Parse("2026-12-01T00:00:00Z"), DateTimeOffset.Parse("2026-12-08T00:00:00Z"));
        await service.PublishAsync(rangeA, ct: TestContext.Current.CancellationToken);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var rangeB = await SeedAdditionalVersionAsync(item, DateTimeOffset.Parse("2026-12-04T00:00:00Z"), DateTimeOffset.Parse("2026-12-11T00:00:00Z"));
        var result = await service.PublishAsync(rangeB, ct: TestContext.Current.CancellationToken);

        Assert.Single(result.Warnings);
    }

    private async Task<(ContentItem Item, ContentVersion Version)> SeedDraftVersionAsync(DateTimeOffset? effectiveStartAt, DateTimeOffset? effectiveEndAt)
    {
        var workspace = new Workspace { Name = "Test", Slug = $"test-{Guid.CreateVersion7()}" };
        var template = new Template { WorkspaceId = workspace.Id, Name = "Page", Slug = $"page-{Guid.CreateVersion7()}" };
        var templateVersion = new TemplateVersion { TemplateId = template.Id, VersionNumber = 1, Status = TemplateVersionStatus.Published, PublishedAt = DateTimeOffset.UtcNow };
        var item = new ContentItem { WorkspaceId = workspace.Id, TemplateVersionId = templateVersion.Id, Slug = $"item-{Guid.CreateVersion7()}" };
        dbContext.Workspaces.Add(workspace);
        dbContext.Templates.Add(template);
        dbContext.TemplateVersions.Add(templateVersion);
        dbContext.ContentItems.Add(item);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var version = await SeedAdditionalVersionAsync(item, effectiveStartAt, effectiveEndAt);
        return (item, version);
    }

    private async Task<ContentVersion> SeedAdditionalVersionAsync(ContentItem item, DateTimeOffset? effectiveStartAt, DateTimeOffset? effectiveEndAt)
    {
        var nextNumber = 1 + await dbContext.ContentVersions.Where(v => v.ContentItemId == item.Id).Select(v => (int?)v.VersionNumber).MaxAsync(TestContext.Current.CancellationToken) ?? 1;
        var version = new ContentVersion
        {
            ContentItemId = item.Id,
            WorkspaceId = item.WorkspaceId,
            VersionNumber = nextNumber,
            Status = ContentStatus.Approved,
            TemplateVersionId = item.TemplateVersionId,
            Slug = item.Slug,
            EffectiveStartAt = effectiveStartAt,
            EffectiveEndAt = effectiveEndAt
        };
        dbContext.ContentVersions.Add(version);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        return version;
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Cmsify.Api.Integration.Tests/Cmsify.Api.Integration.Tests.csproj --filter FullyQualifiedName~ContentPublishingServiceTests`
Expected: FAIL — build error (`IContentPublishingService.PublishAsync` doesn't exist yet; the implementation still has the old `PublishSnapshotAsync` signature).

- [ ] **Step 3: Rewrite the implementation**

Replace the full content of `src/Cmsify.Infrastructure/Persistence/ContentPublishingService.cs`:

```csharp
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Persistence;

public sealed class ContentPublishingService : IContentPublishingService
{
    private readonly CmsifyDbContext dbContext;
    private readonly ICurrentActor currentActor;

    public ContentPublishingService(CmsifyDbContext dbContext, ICurrentActor currentActor)
    {
        this.dbContext = dbContext;
        this.currentActor = currentActor;
    }

    public async Task<ContentPublishResult> PublishAsync(
        ContentVersion version,
        Guid? actorUserId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(version);

        var now = DateTimeOffset.UtcNow;
        var isDefault = version.EffectiveStartAt is null && version.EffectiveEndAt is null;

        if (isDefault)
        {
            var priorDefaults = await dbContext.ContentVersions
                .Where(candidate =>
                    candidate.ContentItemId == version.ContentItemId
                    && candidate.Id != version.Id
                    && candidate.Status == ContentStatus.Published
                    && candidate.EffectiveStartAt == null
                    && candidate.EffectiveEndAt == null)
                .ToListAsync(ct);
            foreach (var prior in priorDefaults)
            {
                prior.Status = ContentStatus.Archived;
                prior.ArchivedAt = now;
                prior.UpdatedAt = now;
            }
        }

        var warnings = isDefault
            ? []
            : await FindEqualSpecificityWarningsAsync(version, ct);

        var actor = actorUserId ?? currentActor.UserId;
        version.Status = ContentStatus.Published;
        version.PublishedAt ??= now;
        version.PublishedByUserId = actor;
        version.UpdatedAt = now;
        version.UpdatedByUserId = actor;

        return new ContentPublishResult(version, warnings);
    }

    private async Task<IReadOnlyList<string>> FindEqualSpecificityWarningsAsync(ContentVersion version, CancellationToken ct)
    {
        var start = version.EffectiveStartAt!.Value;
        var end = version.EffectiveEndAt!.Value;
        var duration = end - start;
        var overlappingRanges = await dbContext.ContentVersions.AsNoTracking()
            .Where(candidate =>
                candidate.ContentItemId == version.ContentItemId
                && candidate.Id != version.Id
                && candidate.Status == ContentStatus.Published
                && candidate.EffectiveStartAt.HasValue
                && candidate.EffectiveEndAt.HasValue
                && candidate.EffectiveStartAt < end
                && start < candidate.EffectiveEndAt)
            .Select(candidate => new { candidate.EffectiveStartAt, candidate.EffectiveEndAt })
            .ToListAsync(ct);
        var hasEqualSpecificityOverlap = overlappingRanges.Any(candidate => candidate.EffectiveEndAt!.Value - candidate.EffectiveStartAt!.Value == duration);

        return hasEqualSpecificityOverlap
            ? ["Another published override with the same duration overlaps this range. The most recently published matching version will win."]
            : [];
    }
}
```

Note: the pick-list `DisplayLabel` resolution and `ValidateRange` logic present in the old implementation are removed from this service — `ValidateRange` is now enforced when a version's window is set (Task 9's create/update actions), and `DisplayLabel` resolution moves there too, since fields are now populated on the version at create/update time rather than copied at publish time.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Cmsify.Api.Integration.Tests/Cmsify.Api.Integration.Tests.csproj --filter FullyQualifiedName~ContentPublishingServiceTests`
Expected: PASS (all 3 tests). Requires Docker running locally for the Postgres Testcontainer.

- [ ] **Step 5: Commit**

```bash
git add src/Cmsify.Infrastructure/Persistence/ContentPublishingService.cs tests/Cmsify.Api.Integration.Tests/ContentPublishingServiceTests.cs
git commit -m "$(cat <<'EOF'
Simplify ContentPublishingService to publish an existing ContentVersion

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01C9zyjQzBXAgvN5wKsUSRvJ
EOF
)"
```

---

### Task 7: Scheduled-publish background worker — retarget to ContentVersion

**Files:**
- Modify: `src/Cmsify.Core/Interfaces/Repositories/RepositoryContracts.cs` (rename `ScheduledContentClaimDto.ContentItemId` → `ContentVersionId`)
- Modify: `src/Cmsify.Infrastructure/Persistence/Repositories/ScheduledPublishingRepository.cs`

**Interfaces:**
- Consumes: `IContentPublishingService.PublishAsync` (Task 6), `ContentVersion`/`ContentStatus` (Task 1).
- Produces: `ScheduledContentClaimDto(Guid ContentVersionId, string LeaseOwner, Guid LeaseToken, bool WasReclaimed = false)`. `IScheduledPublishingRepository`'s method signatures are unchanged (still `ClaimDueContentAsync`/`CompleteClaimAsync`), only their internal target table changes.

This is a production-critical path (raw SQL against `content_items.status`/`publish_at`/`publish_lease_*`, which Task 5's migration removes) — it must compile and behave correctly against `content_versions`, or scheduled publishing silently stops working.

- [ ] **Step 1: Rename the claim DTO's identifier field**

In `src/Cmsify.Core/Interfaces/Repositories/RepositoryContracts.cs`, find:

```csharp
public sealed record ScheduledContentClaimDto(Guid ContentItemId, string LeaseOwner, Guid LeaseToken, bool WasReclaimed = false);
```

Replace with:

```csharp
public sealed record ScheduledContentClaimDto(Guid ContentVersionId, string LeaseOwner, Guid LeaseToken, bool WasReclaimed = false);
```

- [ ] **Step 2: Rewrite `ScheduledPublishingRepository`**

Replace the full content of `src/Cmsify.Infrastructure/Persistence/Repositories/ScheduledPublishingRepository.cs`:

```csharp
using System.Text.Json;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Domain.ValueObjects;
using Cmsify.Core.Interfaces.Repositories;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.BackgroundServices;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Persistence.Repositories;

public sealed class ScheduledPublishingRepository(
    CmsifyDbContext dbContext,
    IContentPublishingService publishingService,
    IWebhookOutbox webhookOutbox) : IScheduledPublishingRepository
{
    public async Task<IReadOnlyList<ScheduledContentClaimDto>> ClaimDueContentAsync(string workerId, DateTimeOffset now, TimeSpan leaseDuration, int limit, CancellationToken ct = default)
    {
        ValidateClaimArguments(workerId, leaseDuration, limit);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);
        var ids = await dbContext.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM content_versions
            WHERE status = 'Approved' AND publish_at <= {now}
              AND EXISTS (SELECT 1 FROM content_items ci WHERE ci.id = content_versions.content_item_id AND NOT ci.is_deleted)
              AND (publish_lease_expires_at IS NULL OR publish_lease_expires_at <= {now})
            ORDER BY publish_at, id
            FOR UPDATE SKIP LOCKED
            LIMIT {limit}
            """).ToListAsync(ct);
        var versions = await dbContext.ContentVersions.Where(version => ids.Contains(version.Id)).ToListAsync(ct);

        var reclaimed = new Dictionary<Guid, bool>();
        foreach (var version in versions)
        {
            reclaimed[version.Id] = version.PublishLeaseExpiresAt.HasValue;
            version.PublishLeaseOwner = workerId;
            version.PublishLeaseToken = Guid.CreateVersion7();
            version.PublishLeaseExpiresAt = now.Add(leaseDuration);
        }

        await dbContext.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        var claims = versions.Select(version => new ScheduledContentClaimDto(version.Id, version.PublishLeaseOwner!, version.PublishLeaseToken!.Value, reclaimed[version.Id])).ToArray();
        foreach (var claim in claims)
        {
            CmsifyOperationalMetrics.RecordScheduledClaim(claim.WasReclaimed);
        }
        CmsifyOperationalMetrics.ReportDueScheduledDepth(await dbContext.ContentVersions.CountAsync(version => version.Status == ContentStatus.Approved && version.PublishAt <= now, ct));
        return claims;
    }

    public async Task<bool> CompleteClaimAsync(ScheduledContentClaimDto claim, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);
        var claimedId = await dbContext.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM content_versions
            WHERE id = {claim.ContentVersionId} AND status = 'Approved' AND publish_at <= {now}
              AND publish_lease_owner = {claim.LeaseOwner} AND publish_lease_token = {claim.LeaseToken}
              AND publish_lease_expires_at > {now}
            FOR UPDATE
            """).SingleOrDefaultAsync(ct);
        if (claimedId == Guid.Empty)
        {
            await transaction.RollbackAsync(ct);
            return false;
        }

        var version = await dbContext.ContentVersions.FirstOrDefaultAsync(candidate => candidate.Id == claimedId, ct);
        if (version is null)
        {
            await transaction.RollbackAsync(ct);
            return false;
        }

        version.PublishAt = null;
        version.PublishLeaseOwner = null;
        version.PublishLeaseToken = null;
        version.PublishLeaseExpiresAt = null;

        var publishResult = await publishingService.PublishAsync(version, actorUserId: null, ct);
        webhookOutbox.Enqueue(
            "content.published",
            version.WorkspaceId,
            version.ContentItemId,
            JsonSerializer.SerializeToElement(new
            {
                contentItemId = version.ContentItemId,
                contentVersionId = version.Id,
                versionNumber = version.VersionNumber,
                workspaceId = version.WorkspaceId,
                templateVersionId = version.TemplateVersionId,
                publishedAt = publishResult.Version.PublishedAt
            }),
            now);
        await dbContext.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        CmsifyOperationalMetrics.RecordScheduledPublished();
        return true;
    }

    private static void ValidateClaimArguments(string workerId, TimeSpan leaseDuration, int limit)
    {
        if (string.IsNullOrWhiteSpace(workerId) || workerId.Length > 200)
        {
            throw new ArgumentException("Scheduled publishing worker IDs must be nonblank and at most 200 characters.", nameof(workerId));
        }

        if (leaseDuration < TimeSpan.FromSeconds(1) || leaseDuration > TimeSpan.FromMinutes(30))
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        if (limit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }
    }
}
```

- [ ] **Step 3: Build to confirm this compiles**

Run: `dotnet build src/Cmsify.Infrastructure/Cmsify.Infrastructure.csproj`
Expected: FAIL only in `ContentItemRepository.cs`/`RepositoryMapping.cs` (Task 8) and the API layer (Tasks 9-13). No errors in `ScheduledPublishingRepository.cs` or `RepositoryContracts.cs`.

- [ ] **Step 4: Commit**

```bash
git add src/Cmsify.Core/Interfaces/Repositories/RepositoryContracts.cs src/Cmsify.Infrastructure/Persistence/Repositories/ScheduledPublishingRepository.cs
git commit -m "$(cat <<'EOF'
Retarget scheduled-publish background worker onto ContentVersion

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01C9zyjQzBXAgvN5wKsUSRvJ
EOF
)"
```

---

### Task 8: Delete the dead `IContentItemRepository` subsystem

**Files:**
- Delete: `src/Cmsify.Infrastructure/Persistence/Repositories/ContentItemRepository.cs`
- Modify: `src/Cmsify.Core/Interfaces/Repositories/Repositories.cs` (remove `IContentItemRepository`)
- Modify: `src/Cmsify.Core/Interfaces/Repositories/RepositoryContracts.cs` (remove `ContentItemDto`, `ContentFieldValueDto`, `CreateContentItemCommand`, `UpdateContentItemCommand`, `ContentFieldValueInput`, `ContentQuery`)
- Modify: `src/Cmsify.Core/Validation/CommandValidators.cs` (remove `CreateContentItemCommandValidator`, `UpdateContentItemCommandValidator`)
- Modify: `src/Cmsify.Infrastructure/Persistence/Repositories/RepositoryMapping.cs` (remove the `ContentItem.ToDto()` extension)
- Modify: `src/Cmsify.Infrastructure/Extensions/ServiceCollectionExtensions.cs` (remove the `IContentItemRepository` DI registration)

**Interfaces:**
- Consumes: nothing (this task only deletes code).
- Produces: nothing new — confirmed by an earlier grep that `IContentItemRepository`/`ContentItemRepository` is registered in DI but never injected by `ContentController` or any other live consumer, and `ContentItemDto`/`ContentQuery`/`CreateContentItemCommand`/`UpdateContentItemCommand`/`ContentFieldValueInput`/`ContentFieldValueDto` are used only by that dead repository and its validators. This subsystem was already out of sync with the live controller architecture before this plan (it referenced `ContentItem.FieldValues`/`Status`, which Task 1 removes) — updating it to compile against the new model would be pure waste, so it's deleted instead (YAGNI).

- [ ] **Step 1: Delete the repository file**

```bash
git rm src/Cmsify.Infrastructure/Persistence/Repositories/ContentItemRepository.cs
```

- [ ] **Step 2: Remove the interface**

In `src/Cmsify.Core/Interfaces/Repositories/Repositories.cs`, delete the `IContentItemRepository` interface (currently lines 44-59):

```csharp
public interface IContentItemRepository
{
    Task<ContentItemDto?> GetAsync(Guid id, CancellationToken ct = default);

    Task<PagedResult<ContentItemDto>> QueryAsync(ContentQuery query, CancellationToken ct = default);

    Task<ContentItemDto> CreateAsync(CreateContentItemCommand command, CancellationToken ct = default);

    Task<ContentItemDto> UpdateAsync(UpdateContentItemCommand command, CancellationToken ct = default);

    Task<ContentItemDto> SetStatusAsync(Guid id, ContentStatus status, Guid actorUserId, CancellationToken ct = default);

    Task<IReadOnlyList<ContentItemDto>> GetPendingScheduledPublishAsync(DateTimeOffset now, int limit = 100, CancellationToken ct = default);

    Task SoftDeleteAsync(Guid id, Guid actorUserId, CancellationToken ct = default);
}
```

- [ ] **Step 3: Remove the DTOs/commands**

In `src/Cmsify.Core/Interfaces/Repositories/RepositoryContracts.cs`, delete these six records (currently lines 81-152):

```csharp
public sealed record ContentItemDto(
    Guid Id,
    Guid WorkspaceId,
    Guid TemplateVersionId,
    ContentStatus Status,
    string? Slug,
    string? LocaleCode,
    Guid? TranslationGroupId,
    DateTimeOffset? PublishAt,
    DateTimeOffset? PublishedAt,
    DateTimeOffset? ArchivedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ContentFieldValueDto(
    Guid Id,
    Guid ContentItemId,
    Guid FieldId,
    int Order,
    ValueKind ValueKind,
    string? TextValue,
    bool? BoolValue,
    Guid? MediaAssetId,
    Guid? FileAssetId,
    Guid? ChildContentItemId,
    JsonElement? JsonValue);

public sealed record CreateContentItemCommand(
    Guid WorkspaceId,
    Guid TemplateVersionId,
    string? Slug,
    string? LocaleCode,
    Guid? TranslationGroupId,
    DateTimeOffset? PublishAt,
    IReadOnlyList<ContentFieldValueInput> FieldValues,
    IReadOnlyList<Guid> TagIds);

public sealed record UpdateContentItemCommand(
    Guid Id,
    string? Slug,
    string? LocaleCode,
    Guid? TranslationGroupId,
    DateTimeOffset? PublishAt,
    IReadOnlyList<ContentFieldValueInput> FieldValues,
    IReadOnlyList<Guid> TagIds);

public sealed record ContentFieldValueInput(
    Guid FieldId,
    int Order,
    ValueKind ValueKind,
    string? TextValue,
    bool? BoolValue,
    Guid? MediaAssetId,
    Guid? FileAssetId,
    Guid? ChildContentItemId,
    JsonElement? JsonValue);

public sealed record ContentQuery(
    Guid? WorkspaceId,
    Guid? TemplateId,
    ContentStatus? Status,
    string? LocaleCode,
    string? Slug,
    IReadOnlyList<string> Tags,
    DateTimeOffset? CreatedFrom,
    DateTimeOffset? CreatedTo,
    DateTimeOffset? PublishedFrom,
    DateTimeOffset? PublishedTo,
    string? Search,
    string? SortBy,
    bool SortDescending,
    PageRequest Page);
```

- [ ] **Step 4: Remove the orphaned validators**

In `src/Cmsify.Core/Validation/CommandValidators.cs`, delete `CreateContentItemCommandValidator` and `UpdateContentItemCommandValidator` (the two classes immediately following `SaveTemplateVersionStructureCommandValidator` and immediately preceding `CreateMediaAssetCommandValidator`).

- [ ] **Step 5: Remove the mapping extension**

In `src/Cmsify.Infrastructure/Persistence/Repositories/RepositoryMapping.cs`, delete:

```csharp
    public static ContentItemDto ToDto(this ContentItem entity) =>
        new(entity.Id, entity.WorkspaceId, entity.TemplateVersionId, entity.Status, entity.Slug, entity.LocaleCode, entity.TranslationGroupId, entity.PublishAt, entity.PublishedAt, entity.ArchivedAt, entity.CreatedAt, entity.UpdatedAt);
```

- [ ] **Step 6: Remove the DI registration**

In `src/Cmsify.Infrastructure/Extensions/ServiceCollectionExtensions.cs`, find and delete the line registering `IContentItemRepository`/`ContentItemRepository` (search for `IContentItemRepository` in that file).

- [ ] **Step 7: Build to confirm no remaining references**

Run: `dotnet build src/Cmsify.Infrastructure/Cmsify.Infrastructure.csproj`
Expected: FAIL only with errors in the API layer (`ContentController.cs`, `ResolvedContentListQuery.cs`, `WireContracts.cs` usages — fixed in Tasks 9-13). No errors referencing `ContentItemRepository`, `ContentItemDto`, `ContentQuery`, `CreateContentItemCommand`, `UpdateContentItemCommand`, or `ContentFieldValueInput`/`ContentFieldValueDto` anywhere in the solution (`grep -r "ContentItemDto\|ContentQuery\b\|CreateContentItemCommand\|UpdateContentItemCommand" src/` should return no matches).

- [ ] **Step 8: Commit**

```bash
git add src/Cmsify.Core/Interfaces/Repositories/Repositories.cs src/Cmsify.Core/Interfaces/Repositories/RepositoryContracts.cs src/Cmsify.Core/Validation/CommandValidators.cs src/Cmsify.Infrastructure/Persistence/Repositories/RepositoryMapping.cs src/Cmsify.Infrastructure/Extensions/ServiceCollectionExtensions.cs
git commit -m "$(cat <<'EOF'
Delete dead IContentItemRepository subsystem

Registered in DI but never consumed by ContentController or anything
else; already out of sync with the live controller's model before this
change. Deleted rather than updated to compile against the new
ContentItem/ContentVersion split for no purpose.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01C9zyjQzBXAgvN5wKsUSRvJ
EOF
)"
```

---

### Task 9: Public contracts — version-centric DTOs

**Files:**
- Modify: `src/Cmsify.Contracts/Enums.cs` (delete `ContentVersionStatus`)
- Modify: `src/Cmsify.Contracts/WireContracts.cs`

**Interfaces:**
- Produces: the new/changed contract shapes below, consumed by the SDK (Task 15) and every `ContentController` action rewritten in Tasks 10-12. `ContentFieldValueResponse` is deleted and consolidated into `ContentVersionFieldValueResponse` (which gains a `Child` field), since fields are now only ever returned in a version's context.

- [ ] **Step 1: Delete `ContentVersionStatus`**

In `src/Cmsify.Contracts/Enums.cs`, delete:

```csharp
public enum ContentVersionStatus
{
    Published,
    Retired
}
```

- [ ] **Step 2: Replace the content-related records in `WireContracts.cs`**

Replace:

```csharp
public sealed record ContentItemSummaryResponse(Guid Id, Guid TemplateVersionId, string TemplateName, ContentStatus Status, string? Slug, string? LocaleCode, Guid? TranslationGroupId, IReadOnlyList<string> Tags, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? PublishedAt);

public sealed record ContentItemDetailResponse(Guid Id, Guid TemplateVersionId, string TemplateName, ContentStatus Status, string? Slug, string? LocaleCode, Guid? TranslationGroupId, IReadOnlyList<string> Tags, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? PublishedAt, DateTimeOffset? PublishAt, DateTimeOffset? PendingEffectiveStartAt, DateTimeOffset? PendingEffectiveEndAt, IReadOnlyList<ContentFieldValueResponse> Fields);

public sealed record ContentFieldValueRequest(Guid FieldId, int Order, ValueKind ValueKind, string? TextValue, bool? BoolValue, Guid? MediaAssetId, Guid? FileAssetId, Guid? ChildContentItemId, JsonElement? JsonValue);

public sealed record ContentFieldValueResponse(Guid FieldId, string? Key, string? Label, int Order, ValueKind ValueKind, string? TextValue, bool? BoolValue, Guid? MediaAssetId, Guid? FileAssetId, Guid? ChildContentItemId, ContentItemDetailResponse? Child, JsonElement? JsonValue, string? DisplayLabel = null);

public sealed record CreateContentItemRequest(Guid TemplateVersionId, string? Slug, string? LocaleCode, Guid? TranslationGroupId, IReadOnlyList<string> Tags, IReadOnlyList<ContentFieldValueRequest> Fields);

public sealed record UpdateContentItemRequest(string? Slug, string? LocaleCode, Guid? TranslationGroupId, DateTimeOffset? PublishAt, IReadOnlyList<string> Tags, IReadOnlyList<ContentFieldValueRequest> Fields);

public sealed record PublishContentRequest(DateTimeOffset? PublishAt, DateTimeOffset? EffectiveStartAt, DateTimeOffset? EffectiveEndAt, bool? OverrideWorkflow = null);
public sealed record PublishContentResponse(ContentItemDetailResponse Content, IReadOnlyList<string> Warnings);

public sealed record LinkTranslationRequest(Guid TargetContentItemId);

public sealed record ContentVersionSummaryResponse(Guid Id, Guid ContentItemId, int VersionNumber, ContentVersionStatus Status, Guid TemplateVersionId, string? Slug, string? LocaleCode, DateTimeOffset? EffectiveStartAt, DateTimeOffset? EffectiveEndAt, DateTimeOffset PublishedAt, DateTimeOffset? RetiredAt, Guid? PublishedByUserId, int? RolledBackFromVersionNumber, IReadOnlyList<string> Tags);

public sealed record ContentVersionFieldValueResponse(Guid FieldId, string? Key, string? Label, int Order, ValueKind ValueKind, string? TextValue, bool? BoolValue, Guid? MediaAssetId, Guid? FileAssetId, Guid? ChildContentItemId, JsonElement? JsonValue, string? DisplayLabel = null);

public sealed record ContentVersionDetailResponse(Guid Id, Guid ContentItemId, int VersionNumber, ContentVersionStatus Status, Guid TemplateVersionId, string TemplateName, string? Slug, string? LocaleCode, Guid? TranslationGroupId, DateTimeOffset? EffectiveStartAt, DateTimeOffset? EffectiveEndAt, DateTimeOffset PublishedAt, DateTimeOffset? RetiredAt, Guid? PublishedByUserId, int? RolledBackFromVersionNumber, IReadOnlyList<string> Tags, IReadOnlyList<ContentVersionFieldValueResponse> Fields);
```

with:

```csharp
public sealed record ContentItemSummaryResponse(Guid Id, Guid TemplateVersionId, string TemplateName, string? Slug, string? LocaleCode, Guid? TranslationGroupId, IReadOnlyList<string> Tags, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int VersionCount, ContentVersionSummaryResponse? CurrentlyServingVersion);

public sealed record ContentItemDetailResponse(Guid Id, Guid TemplateVersionId, string TemplateName, string? Slug, string? LocaleCode, Guid? TranslationGroupId, IReadOnlyList<string> Tags, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, ContentVersionSummaryResponse? CurrentlyServingVersion, IReadOnlyList<ContentVersionSummaryResponse> Versions);

public sealed record ContentFieldValueRequest(Guid FieldId, int Order, ValueKind ValueKind, string? TextValue, bool? BoolValue, Guid? MediaAssetId, Guid? FileAssetId, Guid? ChildContentItemId, JsonElement? JsonValue);

public sealed record CreateContentItemRequest(Guid TemplateVersionId, string? Slug, string? LocaleCode, Guid? TranslationGroupId, IReadOnlyList<string> Tags, IReadOnlyList<ContentFieldValueRequest> Fields);

public sealed record UpdateContentItemRequest(string? Slug, string? LocaleCode, Guid? TranslationGroupId, IReadOnlyList<string> Tags);

public sealed record LinkTranslationRequest(Guid TargetContentItemId);

public sealed record ContentVersionSummaryResponse(Guid Id, Guid ContentItemId, int VersionNumber, ContentStatus Status, Guid TemplateVersionId, string? Slug, string? LocaleCode, DateTimeOffset? EffectiveStartAt, DateTimeOffset? EffectiveEndAt, DateTimeOffset? PublishAt, DateTimeOffset? PublishedAt, DateTimeOffset? ArchivedAt, Guid? PublishedByUserId, int? RolledBackFromVersionNumber, IReadOnlyList<string> Tags, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record ContentVersionFieldValueResponse(Guid FieldId, string? Key, string? Label, int Order, ValueKind ValueKind, string? TextValue, bool? BoolValue, Guid? MediaAssetId, Guid? FileAssetId, Guid? ChildContentItemId, ContentVersionDetailResponse? Child, JsonElement? JsonValue, string? DisplayLabel = null);

public sealed record ContentVersionDetailResponse(Guid Id, Guid ContentItemId, int VersionNumber, ContentStatus Status, Guid TemplateVersionId, string TemplateName, string? Slug, string? LocaleCode, Guid? TranslationGroupId, DateTimeOffset? EffectiveStartAt, DateTimeOffset? EffectiveEndAt, DateTimeOffset? PublishAt, DateTimeOffset? PublishedAt, DateTimeOffset? ArchivedAt, Guid? PublishedByUserId, int? RolledBackFromVersionNumber, IReadOnlyList<string> Tags, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<ContentVersionFieldValueResponse> Fields);

public sealed record CreateContentVersionRequest(DateTimeOffset? EffectiveStartAt, DateTimeOffset? EffectiveEndAt, int? DuplicateFromVersionNumber, IReadOnlyList<ContentFieldValueRequest>? Fields);

public sealed record UpdateContentVersionRequest(DateTimeOffset? EffectiveStartAt, DateTimeOffset? EffectiveEndAt, IReadOnlyList<ContentFieldValueRequest> Fields);

public sealed record PublishContentVersionRequest(DateTimeOffset? PublishAt, bool? OverrideWorkflow = null);

public sealed record PublishContentVersionResponse(ContentVersionDetailResponse Version, IReadOnlyList<string> Warnings);
```

Notes on this change:
- `ContentFieldValueResponse` is deleted — `ContentVersionFieldValueResponse` (now with a `Child` field, typed `ContentVersionDetailResponse?`) is the one field-value response shape used everywhere, since fields only ever live on a version now.
- `GetBySlug` (Task 11) returns `ContentVersionDetailResponse` directly (the resolved winning version, fields included) instead of `ContentItemDetailResponse` — "give me the content for this slug" naturally resolves to a version's content, not an item header.
- `ContentListQuery` (unchanged — still references `ContentStatus?` for its `Status` filter parameter, which still exists and is valid).

- [ ] **Step 3: Build to confirm**

Run: `dotnet build src/Cmsify.Contracts/Cmsify.Contracts.csproj`
Expected: PASS (this project has no dependency on `ContentItem`/`ContentVersion` entities, only on its own records and `ContentStatus`).

- [ ] **Step 4: Commit**

```bash
git add src/Cmsify.Contracts/Enums.cs src/Cmsify.Contracts/WireContracts.cs
git commit -m "$(cat <<'EOF'
Update public contracts for version-centric content API

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01C9zyjQzBXAgvN5wKsUSRvJ
EOF
)"
```

---

### Task 10: ContentValidator / ContentSearchVectorBuilder — retarget onto ContentVersion

**Files:**
- Modify: `src/Cmsify.Core/Services/ContentValidator.cs`
- Modify: `src/Cmsify.Core/Services/ContentSearchVectorBuilder.cs`

**Interfaces:**
- Consumes: `IContentValidator`/`IContentSearchVectorBuilder` (Task 2), `ContentVersion`/`ContentVersionFieldValue` (Task 1).
- Produces: identical validation/search-indexing behavior, now reading `ContentVersion.FieldValues`/`ContentVersion.Slug` instead of the removed `ContentItem` equivalents.

- [ ] **Step 1: Rewrite `ContentValidator`**

In `src/Cmsify.Core/Services/ContentValidator.cs`, replace the `Validate` method and its helper:

```csharp
    public ValidationResult Validate(ContentVersion version, TemplateVersion templateVersion)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(templateVersion);

        var failures = new List<ValidationFailure>();
        var valuesByField = version.FieldValues.GroupBy(value => value.FieldId).ToDictionary(group => group.Key, group => group.ToList());
        var fieldIds = templateVersion.Fields.Select(field => field.Id).ToHashSet();

        foreach (var value in version.FieldValues.Where(value => !fieldIds.Contains(value.FieldId)))
        {
            failures.Add(new ValidationFailure(nameof(ContentVersion.FieldValues), $"Field value '{value.Id}' targets a field not present on the template version."));
        }

        foreach (var field in templateVersion.Fields)
        {
            valuesByField.TryGetValue(field.Id, out var values);
            var count = values?.Count ?? 0;
            var minimum = field.IsRequired ? Math.Max(1, field.MinOccurrences) : field.MinOccurrences;

            if (count < minimum)
            {
                failures.Add(new ValidationFailure(field.Key, $"Field '{field.Key}' requires at least {minimum} value(s)."));
            }

            if (field.MaxOccurrences.HasValue && count > field.MaxOccurrences.Value)
            {
                failures.Add(new ValidationFailure(field.Key, $"Field '{field.Key}' allows at most {field.MaxOccurrences.Value} value(s)."));
            }

            if (values is null)
            {
                continue;
            }

            foreach (var value in values)
            {
                ValidateValueKind(field, value, failures);
            }
        }

        return new ValidationResult(failures);
    }

    private static void ValidateValueKind(TemplateField field, ContentVersionFieldValue value, ICollection<ValidationFailure> failures)
```

(`ValidateValueKind`'s body is unchanged — only its parameter type changes from `ContentFieldValue` to `ContentVersionFieldValue`, both of which already expose the same `ValueKind`/`JsonValue`/`ChildContentItemId`/`TextValue` members.)

- [ ] **Step 2: Rewrite `ContentSearchVectorBuilder`**

In `src/Cmsify.Core/Services/ContentSearchVectorBuilder.cs`, replace the `Build` method:

```csharp
    public string Build(ContentVersion version, TemplateVersion templateVersion)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(templateVersion);

        var searchableFieldIds = templateVersion.Fields
            .Where(field => field.PrimitiveType is PrimitiveType.Text or PrimitiveType.RichText or PrimitiveType.Markdown or PrimitiveType.PickList or PrimitiveType.Link or PrimitiveType.Quote)
            .Where(field => field.PrimitiveType != PrimitiveType.Text || TextFormatHints.IsSearchIndexable(TextFormatHints.GetEffectiveHint(field.FieldConfig)))
            .Select(field => field.Id)
            .ToHashSet();

        var text = string.Join(' ', version.FieldValues
            .Where(value => searchableFieldIds.Contains(value.FieldId) && !string.IsNullOrWhiteSpace(value.TextValue))
            .OrderBy(value => value.Order)
            .Select(value => value.TextValue));

        if (!string.IsNullOrWhiteSpace(version.Slug))
        {
            text = $"{version.Slug} {text}";
        }

        var terms = TokenRegex().Matches(text.ToLowerInvariant())
            .Select((match, index) => (Term: match.Value.Replace("'", "''", StringComparison.Ordinal), Position: index + 1))
            .GroupBy(term => term.Term)
            .Select(group => $"'{group.Key}':{string.Join(",", group.Select(term => term.Position))}");

        return string.Join(' ', terms);
    }
```

- [ ] **Step 3: Build to confirm**

Run: `dotnet build src/Cmsify.Core/Cmsify.Core.csproj`
Expected: PASS — this is the last file in `Cmsify.Core` referencing the old signatures.

- [ ] **Step 4: Commit**

```bash
git add src/Cmsify.Core/Services/ContentValidator.cs src/Cmsify.Core/Services/ContentSearchVectorBuilder.cs
git commit -m "$(cat <<'EOF'
Retarget ContentValidator and ContentSearchVectorBuilder onto ContentVersion

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01C9zyjQzBXAgvN5wKsUSRvJ
EOF
)"
```

---

### Task 11: ContentController — full rewrite for the version-centric API

**Files:**
- Modify (full rewrite): `src/Cmsify.Api/Controllers/ContentController.cs`
- Create: `tests/Cmsify.Api.Integration.Tests/ContentVersionWorkflowTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1, 2, 6, 9, 10 (`ContentItem`/`ContentVersion`, `IContentLifecycleService`/`IContentPublishingService`, new contracts, retargeted `IContentValidator`/`IContentSearchVectorBuilder`).
- Produces: the new route table below. Old item-level workflow routes (`/content/{id}/submit`, `/approve`, `/reject`, `/publish`, `/archive`, `/restore`, `/rollback`, `/upgrade-version`) are removed. New version-level routes replace them.

New route table (workspace-scoped, same `api/v1/workspaces/{workspaceId:guid}/content` base as today):

| Method | Route | Purpose |
|---|---|---|
| GET | `/` | List items (aggregate status); `?resolve=true` for the public resolved list (unchanged behavior) |
| POST | `/` | Create item + initial default (Draft) version |
| GET | `/{id}` | Item detail: header + all versions |
| PUT | `/{id}` | Update item header (slug/locale/tags/translationGroup) |
| DELETE | `/{id}` | Soft-delete item |
| GET | `/by-slug/{slug}` | Resolve winning version for a slug (returns `ContentVersionDetailResponse`) |
| POST | `/{id}/link-translation` | Unchanged |
| GET | `/{id}/translations` | Unchanged |
| POST | `/{id}/versions` | Create a version (blank or duplicated) |
| GET | `/{id}/versions` | List versions |
| GET | `/{id}/versions/{versionNumber}` | Version detail with fields |
| PUT | `/{id}/versions/{versionNumber}` | Update a Draft/Review/Approved version's fields/window |
| DELETE | `/{id}/versions/{versionNumber}` | Delete a Draft version |
| POST | `/{id}/versions/{versionNumber}/submit` | Draft → Review |
| POST | `/{id}/versions/{versionNumber}/approve` | Review → Approved |
| POST | `/{id}/versions/{versionNumber}/reject` | Review → Draft |
| POST | `/{id}/versions/{versionNumber}/publish` | → Published (or schedule via `PublishAt`) |
| POST | `/{id}/versions/{versionNumber}/archive` | Published → Archived |
| POST | `/{id}/versions/{versionNumber}/restore` | Archived → Draft |
| POST | `/{id}/versions/{versionNumber}/upgrade-template-version` | Bump this version to the latest published template version (replaces item-level UpgradeVersion) |

No separate "duplicate" or "rollback" route exists — `POST /{id}/versions` with `DuplicateFromVersionNumber` set (see `CreateVersion` below) already covers both "duplicate into a new date range" and "roll back by cloning a retired/published version's fields into a fresh Draft." A dedicated endpoint would just be a thinner wrapper around the same action.

This is a full-file rewrite (the old and new controllers can't coexist mid-file — every action touches the same removed `ContentItem.Status`/`FieldValues` surface), so it lands as one task with several TDD checkpoints rather than split across multiple tasks.

- [ ] **Step 1: Write the failing integration tests**

Create `tests/Cmsify.Api.Integration.Tests/ContentVersionWorkflowTests.cs`:

```csharp
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cmsify.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using SyntaxCircus.Cmsify.Contracts;

namespace Cmsify.Api.Integration.Tests;

public sealed class ContentVersionWorkflowTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions ApiJsonOptions = CmsifyJsonOptions.Create();

    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("cmsify")
        .WithUsername("cmsify")
        .WithPassword("cmsify")
        .Build();

    public async ValueTask InitializeAsync()
    {
        await postgres.StartAsync();
        Environment.SetEnvironmentVariable("ConnectionStrings__Cmsify", postgres.GetConnectionString());
        Environment.SetEnvironmentVariable("Seed__Admin__Email", "admin@example.test");
        Environment.SetEnvironmentVariable("Seed__Admin__Password", "change-this-temporary-password");
        Environment.SetEnvironmentVariable("Seed__DefaultWorkspace__Name", "Default");
        Environment.SetEnvironmentVariable("Seed__DefaultWorkspace__Slug", "default");
    }

    public async ValueTask DisposeAsync()
    {
        await postgres.DisposeAsync();
        foreach (var key in new[]
        {
            "ConnectionStrings__Cmsify", "Seed__Admin__Email", "Seed__Admin__Password",
            "Seed__DefaultWorkspace__Name", "Seed__DefaultWorkspace__Slug"
        })
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    [Fact]
    public async Task Create_CreatesItemWithOneDraftVersion()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = await AuthenticatedClientAsync(factory);
        var (workspaceId, templateVersionId) = await SeedTemplateAsync(factory);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/content",
            new CreateContentItemRequest(templateVersionId, "hero", null, null, [], []),
            ApiJsonOptions,
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ContentItemDetailResponse>(ApiJsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Single(body.Versions);
        Assert.Equal(ContentStatus.Draft, body.Versions[0].Status);
        Assert.Null(body.Versions[0].EffectiveStartAt);
    }

    [Fact]
    public async Task CreateVersion_Duplicate_CopiesFieldsIntoNewDraftWithGivenWindow()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = await AuthenticatedClientAsync(factory);
        var (workspaceId, templateVersionId) = await SeedTemplateAsync(factory);
        var itemId = await CreateItemAsync(client, workspaceId, templateVersionId, "seasonal");
        var defaultVersionNumber = (await GetItemAsync(client, workspaceId, itemId)).Versions[0].VersionNumber;

        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions",
            new CreateContentVersionRequest(
                DateTimeOffset.Parse("2026-12-01T00:00:00Z"),
                DateTimeOffset.Parse("2026-12-26T00:00:00Z"),
                defaultVersionNumber,
                null),
            ApiJsonOptions,
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var version = await response.Content.ReadFromJsonAsync<ContentVersionDetailResponse>(ApiJsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(version);
        Assert.Equal(ContentStatus.Draft, version.Status);
        Assert.Equal(DateTimeOffset.Parse("2026-12-01T00:00:00Z"), version.EffectiveStartAt);
        var item = await GetItemAsync(client, workspaceId, itemId);
        Assert.Equal(2, item.Versions.Count);
    }

    [Fact]
    public async Task WorkflowRoundTrip_SubmitApprovePublish_UpdatesStatusAndResolvesBySlug()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = await AuthenticatedClientAsync(factory);
        var (workspaceId, templateVersionId) = await SeedTemplateAsync(factory);
        var itemId = await CreateItemAsync(client, workspaceId, templateVersionId, "roundtrip");
        var versionNumber = (await GetItemAsync(client, workspaceId, itemId)).Versions[0].VersionNumber;

        (await client.PostAsync($"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{versionNumber}/submit", null, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        (await client.PostAsync($"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{versionNumber}/approve", null, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        var publishResponse = await client.PostAsJsonAsync($"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{versionNumber}/publish", new PublishContentVersionRequest(null, null), ApiJsonOptions, TestContext.Current.CancellationToken);
        publishResponse.EnsureSuccessStatusCode();
        var published = await publishResponse.Content.ReadFromJsonAsync<PublishContentVersionResponse>(ApiJsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(published);
        Assert.Equal(ContentStatus.Published, published.Version.Status);

        var resolved = await client.GetFromJsonAsync<ContentVersionDetailResponse>($"/api/v1/workspaces/{workspaceId}/content/by-slug/roundtrip", ApiJsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(resolved);
        Assert.Equal(ContentStatus.Published, resolved.Status);

        var item = await GetItemAsync(client, workspaceId, itemId);
        Assert.NotNull(item.CurrentlyServingVersion);
        Assert.Equal(versionNumber, item.CurrentlyServingVersion.VersionNumber);
    }

    private static async Task<HttpClient> AuthenticatedClientAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("admin@example.test", "change-this-temporary-password"));
        response.EnsureSuccessStatusCode();
        var login = await response.Content.ReadFromJsonAsync<LoginResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.Token);
        return client;
    }

    private static async Task<(Guid WorkspaceId, Guid TemplateVersionId)> SeedTemplateAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var workspaceId = await dbContext.Workspaces.Select(workspace => workspace.Id).FirstAsync();
        var template = new Cmsify.Core.Domain.Entities.Template { WorkspaceId = workspaceId, Name = "Page", Slug = $"page-{Guid.CreateVersion7()}" };
        var templateVersion = new Cmsify.Core.Domain.Entities.TemplateVersion
        {
            TemplateId = template.Id,
            VersionNumber = 1,
            Status = Cmsify.Core.Domain.Enums.TemplateVersionStatus.Published,
            PublishedAt = DateTimeOffset.UtcNow
        };
        dbContext.Templates.Add(template);
        dbContext.TemplateVersions.Add(templateVersion);
        await dbContext.SaveChangesAsync();
        template.CurrentVersionId = templateVersion.Id;
        await dbContext.SaveChangesAsync();
        return (workspaceId, templateVersion.Id);
    }

    private static async Task<Guid> CreateItemAsync(HttpClient client, Guid workspaceId, Guid templateVersionId, string slug)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/content",
            new CreateContentItemRequest(templateVersionId, slug, null, null, [], []),
            ApiJsonOptions,
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ContentItemDetailResponse>(ApiJsonOptions, TestContext.Current.CancellationToken);
        return body!.Id;
    }

    private static async Task<ContentItemDetailResponse> GetItemAsync(HttpClient client, Guid workspaceId, Guid itemId) =>
        (await client.GetFromJsonAsync<ContentItemDetailResponse>($"/api/v1/workspaces/{workspaceId}/content/{itemId}", ApiJsonOptions, TestContext.Current.CancellationToken))!;
}
```

Additional coverage the implementer should add in this same task, following the patterns above (not written out in full here to keep this task readable, but each is a concrete, named scenario — not a vague "add more tests"):
- `Delete_Version_RemovesDraftVersion_ButRejectsNonDraft` — DELETE on a Draft version succeeds; DELETE on a Published version returns 409.
- `Publish_ArchivesPriorDefaultVersion_WhenPublishingNewDefault` — mirrors `ContentPublishingServiceTests.PublishAsync_ArchivesPriorDefaultVersion_WhenPublishingNewDefault` but through the HTTP workflow endpoints (submit/approve/publish twice on two default-window versions of the same item).
- `UpgradeTemplateVersion_MovesVersionToLatestPublishedTemplateVersion_AndDropsRemovedFields` — mirrors the old `UpgradeVersion` test coverage, retargeted to a version route.
- `Update_Item_RejectsEditWhenAllVersionsPublished` is **not** a rule in the new model (item header edits like slug/locale are independent of version status) — no equivalent test needed; the old `Update` action's `content.Status is not (Draft or Review)` gate is gone because item-level `Status` no longer exists.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Cmsify.Api.Integration.Tests/Cmsify.Api.Integration.Tests.csproj --filter FullyQualifiedName~ContentVersionWorkflowTests`
Expected: FAIL — build error, since `ContentController` doesn't expose `/versions` routes or the new contracts yet.

- [ ] **Step 2b: Fix `ResolvedContentListQuery`'s now-invalid enum reference**

`src/Cmsify.Api/Queries/ResolvedContentListQuery.cs` filters on `version.Status == ContentVersionStatus.Published` — that enum no longer exists (Task 9 deleted it). In its `ExecuteAsync` method, replace every occurrence of `ContentVersionStatus.Published` with `ContentStatus.Published` (there are two: in the initial `candidates` filter and nowhere else — the ordering/winner subquery further down doesn't reference the enum directly). Add `using Cmsify.Core.Domain.Enums;` to this file's usings if not already present (it already imports `Cmsify.Core.Domain.Entities`, so the enums namespace is the only addition needed).

- [ ] **Step 3a: Rewrite the controller — usings, constructor, item-level actions**

Replace the full content of `src/Cmsify.Api/Controllers/ContentController.cs` starting with this part (Step 3b below continues the same file):

```csharp
using System.Text.Json;
using Cmsify.Api.Auth;
using Cmsify.Api.Queries;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SyntaxCircus.Cmsify.Contracts;
using CompositionMode = Cmsify.Core.Domain.Enums.CompositionMode;
using ContentStatus = Cmsify.Core.Domain.Enums.ContentStatus;
using PrimitiveType = Cmsify.Core.Domain.Enums.PrimitiveType;
using TemplateVersionStatus = Cmsify.Core.Domain.Enums.TemplateVersionStatus;
using UserRole = Cmsify.Core.Domain.Enums.UserRole;
using ValueKind = Cmsify.Core.Domain.Enums.ValueKind;
using ContentListQuery = SyntaxCircus.Cmsify.Contracts.ContentListQuery;
using PaginationQuery = SyntaxCircus.Cmsify.Contracts.PaginationQuery;

namespace Cmsify.Api.Controllers;

[ApiController]
[Route("api/v1/workspaces/{workspaceId:guid}/content")]
[RequireRole(UserRole.Reader)]
public sealed class ContentController : ControllerBase
{
    private readonly CmsifyDbContext dbContext;
    private readonly IContentValidator contentValidator;
    private readonly IContentSearchVectorBuilder searchVectorBuilder;
    private readonly IContentLifecycleService lifecycleService;
    private readonly IContentPublishingService publishingService;
    private readonly ICurrentActor currentActor;
    private readonly IResolvedContentListQuery resolvedContentListQuery;
    private readonly IWorkspaceAuthorizationService workspaceAuthorization;
    private readonly IWebhookOutbox webhookOutbox;

    public ContentController(CmsifyDbContext dbContext, IContentValidator contentValidator, IContentSearchVectorBuilder searchVectorBuilder, IContentLifecycleService lifecycleService, IContentPublishingService publishingService, ICurrentActor currentActor, IServiceProvider serviceProvider, IWorkspaceAuthorizationService workspaceAuthorization, IWebhookOutbox webhookOutbox)
    {
        this.dbContext = dbContext;
        this.contentValidator = contentValidator;
        this.searchVectorBuilder = searchVectorBuilder;
        this.lifecycleService = lifecycleService;
        this.publishingService = publishingService;
        this.currentActor = currentActor;
        resolvedContentListQuery = serviceProvider.GetRequiredService<IResolvedContentListQuery>();
        this.workspaceAuthorization = workspaceAuthorization;
        this.webhookOutbox = webhookOutbox;
    }

    // ---------- Item-level actions ----------

    [HttpGet]
    public async Task<ActionResult<PagedResponse<ContentItemSummaryResponse>>> List(Guid workspaceId, [FromQuery] ContentListQuery query, CancellationToken ct)
    {
        if (!await workspaceAuthorization.CanReadWorkspaceAsync(workspaceId, ct))
        {
            return NotFound();
        }

        if (query.Resolve)
        {
            return await ListResolvedAsync(workspaceId, query, ct);
        }

        var items = BaseContentQuery(workspaceId).AsNoTracking();

        if (query.TemplateVersionId.HasValue)
        {
            items = items.Where(content => content.TemplateVersionId == query.TemplateVersionId.Value);
        }

        if (query.TemplateId.HasValue)
        {
            items = items.Where(content => dbContext.TemplateVersions.Any(version => version.Id == content.TemplateVersionId && version.TemplateId == query.TemplateId.Value));
        }

        if (!string.IsNullOrWhiteSpace(query.LocaleCode))
        {
            items = items.Where(content => content.LocaleCode == query.LocaleCode);
        }

        if (query.TranslationGroupId.HasValue)
        {
            items = items.Where(content => content.TranslationGroupId == query.TranslationGroupId.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.Slug))
        {
            items = items.Where(content => content.Slug == query.Slug);
        }

        if (!string.IsNullOrWhiteSpace(query.Tags))
        {
            var tags = query.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(NormalizeTag).ToArray();
            foreach (var tag in tags)
            {
                items = items.Where(content => dbContext.ContentItemTags.Any(join => join.ContentItemId == content.Id && dbContext.Tags.Any(candidate => candidate.Id == join.TagId && candidate.Name == tag && !candidate.IsDeleted)));
            }
        }

        if (query.CreatedAfter.HasValue)
        {
            items = items.Where(content => content.CreatedAt >= query.CreatedAfter.Value);
        }

        if (query.CreatedBefore.HasValue)
        {
            items = items.Where(content => content.CreatedAt <= query.CreatedBefore.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            items = items.Where(content => EF.Functions.ILike(content.Slug ?? string.Empty, $"%{query.Q}%")
                || dbContext.ContentVersions.Any(version => version.ContentItemId == content.Id && version.FieldValues.Any(value => value.TextValue != null && EF.Functions.ILike(value.TextValue, $"%{query.Q}%"))));
        }

        items = query.SortBy switch
        {
            "updatedAt" => query.SortDesc ? items.OrderByDescending(content => content.UpdatedAt) : items.OrderBy(content => content.UpdatedAt),
            "slug" => query.SortDesc ? items.OrderByDescending(content => content.Slug) : items.OrderBy(content => content.Slug),
            _ => query.SortDesc ? items.OrderByDescending(content => content.CreatedAt) : items.OrderBy(content => content.CreatedAt)
        };

        var total = await items.CountAsync(ct);
        if (!ControllerHelpers.TryOffset(query.Page, query.PageSize, out var offset))
        {
            return Ok(new PagedResponse<ContentItemSummaryResponse>([], total, query.Page, query.PageSize));
        }

        var pageItems = await items.Skip(offset).Take(ControllerHelpers.Limit(query.PageSize)).ToListAsync(ct);
        var responses = new List<ContentItemSummaryResponse>();
        foreach (var item in pageItems)
        {
            responses.Add(await ToItemSummaryResponseAsync(item, ct));
        }

        return Ok(new PagedResponse<ContentItemSummaryResponse>(responses, total, query.Page, query.PageSize));
    }

    [HttpPost]
    [RequireRole(UserRole.Editor)]
    public async Task<ActionResult<ContentItemDetailResponse>> Create(Guid workspaceId, CreateContentItemRequest request, CancellationToken ct)
    {
        if (!await workspaceAuthorization.CanWriteWorkspaceAsync(workspaceId, ct))
        {
            return NotFound();
        }

        if (request.Slug is not null && !SlugRules.IsValid(request.Slug))
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Content validation failed", SlugRules.ValidationMessage);
        }

        var templateVersion = await LoadTemplateVersionAsync(request.TemplateVersionId, ct);
        if (templateVersion is null || !await TemplateVersionBelongsToWorkspaceAsync(templateVersion.Id, workspaceId, ct))
        {
            return NotFound();
        }

        var content = new ContentItem
        {
            WorkspaceId = workspaceId,
            TemplateVersionId = request.TemplateVersionId,
            Slug = request.Slug,
            LocaleCode = request.LocaleCode,
            TranslationGroupId = request.TranslationGroupId,
            CreatedByUserId = currentActor.UserId,
            UpdatedByUserId = currentActor.UserId
        };
        await ApplyTagsAsync(content, workspaceId, request.Tags, ct);

        var version = new ContentVersion
        {
            ContentItemId = content.Id,
            WorkspaceId = workspaceId,
            VersionNumber = 1,
            Status = ContentStatus.Draft,
            TemplateVersionId = request.TemplateVersionId,
            Slug = content.Slug,
            LocaleCode = content.LocaleCode,
            TranslationGroupId = content.TranslationGroupId,
            Tags = request.Tags.Select(NormalizeTag).Where(tag => tag.Length > 0).Distinct().ToList(),
            CreatedByUserId = currentActor.UserId,
            UpdatedByUserId = currentActor.UserId
        };
        if (await ApplyVersionFieldValuesAsync(version, templateVersion, request.Fields, ct) is { } fieldError)
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Content validation failed", fieldError);
        }

        content.SearchVector = searchVectorBuilder.Build(version, templateVersion);
        dbContext.ContentItems.Add(content);
        dbContext.ContentVersions.Add(version);
        EnqueueContentEvent("content.created", content, version);
        await dbContext.SaveChangesAsync(ct);
        Response.Headers.ETag = ControllerHelpers.ETag(content.UpdatedAt);
        return CreatedAtAction(nameof(Get), new { workspaceId, id = content.Id }, await ToItemDetailResponseAsync(content.Id, ct));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ContentItemDetailResponse>> Get(Guid workspaceId, Guid id, CancellationToken ct)
    {
        if (!await workspaceAuthorization.CanReadWorkspaceAsync(workspaceId, ct))
        {
            return NotFound();
        }

        var content = await BaseContentQuery(workspaceId).AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (content is null)
        {
            return NotFound();
        }

        Response.Headers.ETag = ControllerHelpers.ETag(content.UpdatedAt);
        return Ok(await ToItemDetailResponseAsync(id, ct));
    }

    [HttpGet("by-slug/{slug}")]
    public async Task<ActionResult<ContentVersionDetailResponse>> GetBySlug(Guid workspaceId, string slug, [FromQuery] DateTimeOffset? asOf = null, CancellationToken ct = default)
    {
        if (!await workspaceAuthorization.CanReadWorkspaceAsync(workspaceId, ct))
        {
            return NotFound();
        }

        var resolvedAsOf = asOf ?? DateTimeOffset.UtcNow;
        var version = await ResolvePublishedVersionAsync(workspaceId, contentItemId: null, slug, resolvedAsOf, ct);
        if (version is null)
        {
            return NotFound();
        }

        Response.Headers.ETag = ControllerHelpers.ETag(version.PublishedAt ?? version.UpdatedAt);
        return Ok(await ToVersionDetailResponseAsync(version, resolvedAsOf, ct: ct));
    }

    [HttpPut("{id:guid}")]
    [RequireRole(UserRole.Editor)]
    public async Task<ActionResult<ContentItemDetailResponse>> Update(Guid workspaceId, Guid id, UpdateContentItemRequest request, CancellationToken ct)
    {
        if (request.Slug is not null && !SlugRules.IsValid(request.Slug))
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Content validation failed", SlugRules.ValidationMessage);
        }

        var content = await LoadContentForEditAsync(workspaceId, id, ct);
        if (content is null)
        {
            return NotFound();
        }

        if (!this.IfMatchMatches(content.UpdatedAt))
        {
            return this.Error(StatusCodes.Status412PreconditionFailed, "concurrency-mismatch", "Concurrency mismatch");
        }

        content.Slug = request.Slug;
        content.LocaleCode = request.LocaleCode;
        content.TranslationGroupId = request.TranslationGroupId;
        content.UpdatedAt = DateTimeOffset.UtcNow;
        content.UpdatedByUserId = currentActor.UserId;

        var existingTags = await GetTagNamesAsync(content.Id, ct);
        if (!TagsMatch(existingTags, request.Tags))
        {
            dbContext.ContentItemTags.RemoveRange(content.Tags);
            content.Tags.Clear();
            await ApplyTagsAsync(content, workspaceId, request.Tags, ct);
        }

        try
        {
            EnqueueContentEvent("content.updated", content, version: null);
            await dbContext.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return this.Error(StatusCodes.Status412PreconditionFailed, "concurrency-mismatch", "Concurrency mismatch");
        }

        Response.Headers.ETag = ControllerHelpers.ETag(content.UpdatedAt);
        return Ok(await ToItemDetailResponseAsync(content.Id, ct));
    }

    [HttpDelete("{id:guid}")]
    [RequireRole(UserRole.Editor)]
    public async Task<IActionResult> Delete(Guid workspaceId, Guid id, CancellationToken ct)
    {
        var content = await LoadContentForEditAsync(workspaceId, id, ct);
        if (content is null)
        {
            return NotFound();
        }

        if (!this.IfMatchMatches(content.UpdatedAt))
        {
            return this.Error(StatusCodes.Status412PreconditionFailed, "concurrency-mismatch", "Concurrency mismatch");
        }

        var referencedBy = await ReferencingContentIdsAsync(id, onlyReferenceFields: true, ct);
        if (referencedBy.Count > 0)
        {
            return this.Error(StatusCodes.Status409Conflict, "referenced-by-other-entity", "Content item is referenced by other content", extensions: new Dictionary<string, object?> { ["referencedBy"] = referencedBy });
        }

        SoftDelete(content);
        EnqueueContentEvent("content.deleted", content, version: null);
        await dbContext.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/link-translation")]
    [RequireRole(UserRole.Editor)]
    public async Task<ActionResult<IReadOnlyList<ContentItemSummaryResponse>>> LinkTranslation(Guid workspaceId, Guid id, LinkTranslationRequest request, CancellationToken ct)
    {
        if (!await workspaceAuthorization.CanWriteWorkspaceAsync(workspaceId, ct))
        {
            return NotFound();
        }

        var source = await BaseContentQuery(workspaceId).FirstOrDefaultAsync(content => content.Id == id, ct);
        var target = await BaseContentQuery(workspaceId).FirstOrDefaultAsync(content => content.Id == request.TargetContentItemId, ct);
        if (source is null || target is null)
        {
            return NotFound();
        }

        var groupId = source.TranslationGroupId ?? target.TranslationGroupId ?? Guid.CreateVersion7();
        source.TranslationGroupId = groupId;
        target.TranslationGroupId = groupId;
        await dbContext.SaveChangesAsync(ct);
        var translations = await BaseContentQuery(workspaceId).AsNoTracking()
            .Where(content => content.TranslationGroupId == groupId)
            .OrderBy(content => content.LocaleCode)
            .ToListAsync(ct);
        var responses = new List<ContentItemSummaryResponse>();
        foreach (var translation in translations)
        {
            responses.Add(await ToItemSummaryResponseAsync(translation, ct));
        }

        return Ok(responses);
    }

    [HttpGet("{id:guid}/translations")]
    public async Task<ActionResult<PagedResponse<ContentItemSummaryResponse>>> GetTranslations(Guid workspaceId, Guid id, [FromQuery] PaginationQuery pagination, CancellationToken ct)
    {
        if (!await workspaceAuthorization.CanReadWorkspaceAsync(workspaceId, ct))
        {
            return NotFound();
        }

        var source = await BaseContentQuery(workspaceId).AsNoTracking().FirstOrDefaultAsync(content => content.Id == id, ct);
        if (source is null)
        {
            return NotFound();
        }

        if (!source.TranslationGroupId.HasValue)
        {
            return Ok(new PagedResponse<ContentItemSummaryResponse>([], 0, pagination.Page, pagination.PageSize));
        }

        var query = BaseContentQuery(workspaceId).AsNoTracking().Where(content => content.TranslationGroupId == source.TranslationGroupId).OrderBy(content => content.LocaleCode);
        var total = await query.CountAsync(ct);
        if (!ControllerHelpers.TryOffset(pagination.Page, pagination.PageSize, out var offset))
        {
            return Ok(new PagedResponse<ContentItemSummaryResponse>([], total, pagination.Page, pagination.PageSize));
        }

        var translations = await query.Skip(offset).Take(pagination.PageSize).ToListAsync(ct);
        var responses = new List<ContentItemSummaryResponse>();
        foreach (var translation in translations)
        {
            responses.Add(await ToItemSummaryResponseAsync(translation, ct));
        }

        return Ok(new PagedResponse<ContentItemSummaryResponse>(responses, total, pagination.Page, pagination.PageSize));
    }

    private async Task<ActionResult<PagedResponse<ContentItemSummaryResponse>>> ListResolvedAsync(Guid workspaceId, ContentListQuery query, CancellationToken ct)
    {
        var asOf = query.AsOf ?? DateTimeOffset.UtcNow;
        var page = await resolvedContentListQuery.ExecuteAsync(workspaceId, query, asOf, ct);
        var responses = page.Items
            .Select(row => new ContentItemSummaryResponse(
                row.ContentItemId, row.TemplateVersionId, row.TemplateName, row.Slug, row.LocaleCode,
                row.TranslationGroupId, row.Tags, row.PublishedAt, row.PublishedAt, 1, null))
            .ToList();
        return Ok(new PagedResponse<ContentItemSummaryResponse>(responses, page.TotalCount, query.Page, query.PageSize));
    }
```

Note on `ListResolvedAsync`: it passes `1` for `VersionCount` and `null` for `CurrentlyServingVersion` — placeholders, not accurate values. `ResolvedContentListRow` (from the unchanged `ResolvedContentListQuery`) doesn't carry a version count or full version details, only the already-resolved winning row's identity fields. This is an acceptable gap for now since this path is the public/delivery-style resolved list (a delivery consumer wants the resolved content, not admin aggregate metadata) and `ResolvedContentListQuery`'s logic is explicitly out of scope for this plan (per the architecture spec: "no logic change needed"). If the frontend follow-up plan needs accurate aggregates on this path too, it should extend `ResolvedContentListRow` at that point rather than guessing here.

- [ ] **Step 3b: Rewrite the controller — version CRUD actions (continues the same file, inserted after the item-level actions above)**

```csharp
    // ---------- Version-level CRUD ----------

    [HttpPost("{id:guid}/versions")]
    [RequireRole(UserRole.Editor)]
    public async Task<ActionResult<ContentVersionDetailResponse>> CreateVersion(Guid workspaceId, Guid id, CreateContentVersionRequest request, CancellationToken ct)
    {
        if (!await workspaceAuthorization.CanWriteWorkspaceAsync(workspaceId, ct))
        {
            return NotFound();
        }

        var content = await BaseContentQuery(workspaceId).FirstOrDefaultAsync(item => item.Id == id, ct);
        if (content is null)
        {
            return NotFound();
        }

        if (ValidateEffectiveRange(request.EffectiveStartAt, request.EffectiveEndAt) is { } rangeError)
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Invalid effective range", rangeError);
        }

        var templateVersion = await LoadTemplateVersionAsync(content.TemplateVersionId, ct);
        if (templateVersion is null)
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Content validation failed", "Template version is unavailable.");
        }

        var nextNumber = 1 + (await dbContext.ContentVersions.Where(v => v.ContentItemId == id).Select(v => (int?)v.VersionNumber).MaxAsync(ct) ?? 0);
        var tagNames = await GetTagNamesAsync(id, ct);

        var version = new ContentVersion
        {
            ContentItemId = id,
            WorkspaceId = workspaceId,
            VersionNumber = nextNumber,
            Status = ContentStatus.Draft,
            TemplateVersionId = content.TemplateVersionId,
            Slug = content.Slug,
            LocaleCode = content.LocaleCode,
            TranslationGroupId = content.TranslationGroupId,
            Tags = tagNames.ToList(),
            EffectiveStartAt = request.EffectiveStartAt,
            EffectiveEndAt = request.EffectiveEndAt,
            CreatedByUserId = currentActor.UserId,
            UpdatedByUserId = currentActor.UserId
        };

        IReadOnlyList<ContentFieldValueRequest> fields;
        if (request.DuplicateFromVersionNumber.HasValue)
        {
            var source = await dbContext.ContentVersions.AsNoTracking()
                .Include(v => v.FieldValues)
                .FirstOrDefaultAsync(v => v.ContentItemId == id && v.VersionNumber == request.DuplicateFromVersionNumber.Value, ct);
            if (source is null)
            {
                return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Content validation failed", $"Version {request.DuplicateFromVersionNumber} does not exist.");
            }

            fields = source.FieldValues
                .Select(value => new ContentFieldValueRequest(value.FieldId, value.Order, value.ValueKind, value.TextValue, value.BoolValue, value.MediaAssetId, value.FileAssetId, value.ChildContentItemId, value.JsonValue))
                .ToList();
            version.RolledBackFromVersionNumber = source.VersionNumber;
        }
        else
        {
            fields = request.Fields ?? [];
        }

        if (await ApplyVersionFieldValuesAsync(version, templateVersion, fields, ct) is { } fieldError)
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Content validation failed", fieldError);
        }

        content.SearchVector = searchVectorBuilder.Build(version, templateVersion);
        content.UpdatedAt = DateTimeOffset.UtcNow;
        dbContext.ContentVersions.Add(version);
        EnqueueContentEvent("content.version_created", content, version);
        await dbContext.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(GetVersion), new { workspaceId, id, versionNumber = version.VersionNumber }, await ToVersionDetailResponseAsync(version, DateTimeOffset.UtcNow, ct: ct));
    }

    [HttpGet("{id:guid}/versions")]
    public async Task<ActionResult<PagedResponse<ContentVersionSummaryResponse>>> ListVersions(Guid workspaceId, Guid id, [FromQuery] PaginationQuery pagination, CancellationToken ct)
    {
        if (!await workspaceAuthorization.CanReadWorkspaceAsync(workspaceId, ct))
        {
            return NotFound();
        }

        var content = await BaseContentQuery(workspaceId).AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (content is null)
        {
            return NotFound();
        }

        var query = dbContext.ContentVersions.AsNoTracking()
            .Where(version => version.ContentItemId == id)
            .OrderByDescending(version => version.VersionNumber);
        var total = await query.CountAsync(ct);
        if (!ControllerHelpers.TryOffset(pagination.Page, pagination.PageSize, out var offset))
        {
            return Ok(new PagedResponse<ContentVersionSummaryResponse>([], total, pagination.Page, pagination.PageSize));
        }

        var versions = await query.Skip(offset).Take(pagination.PageSize).ToListAsync(ct);
        return Ok(new PagedResponse<ContentVersionSummaryResponse>(versions.Select(ToVersionSummaryResponse).ToList(), total, pagination.Page, pagination.PageSize));
    }

    [HttpGet("{id:guid}/versions/{versionNumber:int}")]
    public async Task<ActionResult<ContentVersionDetailResponse>> GetVersion(Guid workspaceId, Guid id, int versionNumber, CancellationToken ct)
    {
        if (!await workspaceAuthorization.CanReadWorkspaceAsync(workspaceId, ct))
        {
            return NotFound();
        }

        var content = await BaseContentQuery(workspaceId).AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (content is null)
        {
            return NotFound();
        }

        var version = await dbContext.ContentVersions.AsNoTracking()
            .Include(v => v.FieldValues)
            .FirstOrDefaultAsync(v => v.ContentItemId == id && v.VersionNumber == versionNumber, ct);
        if (version is null)
        {
            return NotFound();
        }

        return Ok(await ToVersionDetailResponseAsync(version, DateTimeOffset.UtcNow, ct: ct));
    }

    [HttpPut("{id:guid}/versions/{versionNumber:int}")]
    [RequireRole(UserRole.Editor)]
    public async Task<ActionResult<ContentVersionDetailResponse>> UpdateVersion(Guid workspaceId, Guid id, int versionNumber, UpdateContentVersionRequest request, CancellationToken ct)
    {
        var version = await LoadVersionForEditAsync(workspaceId, id, versionNumber, ct);
        if (version is null)
        {
            return NotFound();
        }

        if (version.Status is not (ContentStatus.Draft or ContentStatus.Review or ContentStatus.Approved))
        {
            return this.Error(StatusCodes.Status409Conflict, "conflict", "Only Draft, Review, or Approved versions can be edited");
        }

        if (!this.IfMatchMatches(version.UpdatedAt))
        {
            return this.Error(StatusCodes.Status412PreconditionFailed, "concurrency-mismatch", "Concurrency mismatch");
        }

        if (ValidateEffectiveRange(request.EffectiveStartAt, request.EffectiveEndAt) is { } rangeError)
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Invalid effective range", rangeError);
        }

        var templateVersion = await LoadTemplateVersionAsync(version.TemplateVersionId, ct);
        if (templateVersion is null)
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Content validation failed", "Template version is unavailable.");
        }

        version.EffectiveStartAt = request.EffectiveStartAt;
        version.EffectiveEndAt = request.EffectiveEndAt;
        if (await ApplyVersionFieldValuesAsync(version, templateVersion, request.Fields, ct) is { } fieldError)
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Content validation failed", fieldError);
        }

        version.UpdatedAt = DateTimeOffset.UtcNow;
        version.UpdatedByUserId = currentActor.UserId;
        var item = await dbContext.ContentItems.FirstAsync(candidate => candidate.Id == id, ct);
        item.SearchVector = searchVectorBuilder.Build(version, templateVersion);
        item.UpdatedAt = DateTimeOffset.UtcNow;

        try
        {
            await dbContext.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return this.Error(StatusCodes.Status412PreconditionFailed, "concurrency-mismatch", "Concurrency mismatch");
        }

        return Ok(await ToVersionDetailResponseAsync(version, DateTimeOffset.UtcNow, ct: ct));
    }

    [HttpDelete("{id:guid}/versions/{versionNumber:int}")]
    [RequireRole(UserRole.Editor)]
    public async Task<IActionResult> DeleteVersion(Guid workspaceId, Guid id, int versionNumber, CancellationToken ct)
    {
        var version = await LoadVersionForEditAsync(workspaceId, id, versionNumber, ct);
        if (version is null)
        {
            return NotFound();
        }

        if (version.Status != ContentStatus.Draft)
        {
            return this.Error(StatusCodes.Status409Conflict, "conflict", "Only Draft versions can be deleted");
        }

        dbContext.ContentVersions.Remove(version);
        await dbContext.SaveChangesAsync(ct);
        return NoContent();
    }
```

- [ ] **Step 3c: Rewrite the controller — version workflow actions (continues the same file, inserted after the version CRUD actions above)**

```csharp
    // ---------- Version-level workflow ----------

    [HttpPost("{id:guid}/versions/{versionNumber:int}/submit")]
    [RequireRole(UserRole.Editor)]
    public Task<ActionResult<ContentVersionDetailResponse>> Submit(Guid workspaceId, Guid id, int versionNumber, CancellationToken ct) => Transition(workspaceId, id, versionNumber, ContentStatus.Review, ct);

    [HttpPost("{id:guid}/versions/{versionNumber:int}/approve")]
    [RequireRole(UserRole.TemplateAdmin)]
    public Task<ActionResult<ContentVersionDetailResponse>> Approve(Guid workspaceId, Guid id, int versionNumber, CancellationToken ct) => Transition(workspaceId, id, versionNumber, ContentStatus.Approved, ct);

    [HttpPost("{id:guid}/versions/{versionNumber:int}/reject")]
    [RequireRole(UserRole.TemplateAdmin)]
    public Task<ActionResult<ContentVersionDetailResponse>> Reject(Guid workspaceId, Guid id, int versionNumber, RejectContentRequest request, CancellationToken ct) => Transition(workspaceId, id, versionNumber, ContentStatus.Draft, ct);

    [HttpPost("{id:guid}/versions/{versionNumber:int}/archive")]
    [RequireRole(UserRole.Editor)]
    public Task<ActionResult<ContentVersionDetailResponse>> Archive(Guid workspaceId, Guid id, int versionNumber, CancellationToken ct) => Transition(workspaceId, id, versionNumber, ContentStatus.Archived, ct);

    [HttpPost("{id:guid}/versions/{versionNumber:int}/restore")]
    [RequireRole(UserRole.Editor)]
    public Task<ActionResult<ContentVersionDetailResponse>> Restore(Guid workspaceId, Guid id, int versionNumber, CancellationToken ct) => Transition(workspaceId, id, versionNumber, ContentStatus.Draft, ct);

    [HttpPost("{id:guid}/versions/{versionNumber:int}/publish")]
    [RequireRole(UserRole.Editor)]
    public async Task<ActionResult<PublishContentVersionResponse>> Publish(Guid workspaceId, Guid id, int versionNumber, PublishContentVersionRequest? request, CancellationToken ct)
    {
        var version = await LoadVersionForEditAsync(workspaceId, id, versionNumber, ct);
        if (version is null)
        {
            return NotFound();
        }

        var templateVersion = await LoadTemplateVersionAsync(version.TemplateVersionId, ct);
        if (templateVersion is null)
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Content validation failed", "Template version is unavailable.");
        }
        if (await ValidatePickListValuesAsync(version, templateVersion, ct) is { } pickListError)
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Content validation failed", pickListError);
        }

        var allowOverride = (request?.OverrideWorkflow ?? false) && currentActor.Role >= UserRole.Admin;

        if (request?.PublishAt is not null)
        {
            if (request.OverrideWorkflow == true)
            {
                return this.Error(StatusCodes.Status422UnprocessableEntity, "invalid-state-transition", "Invalid content state transition", "Workflow override is not supported for scheduled publication.");
            }

            if (version.Status != ContentStatus.Approved)
            {
                return this.Error(StatusCodes.Status422UnprocessableEntity, "invalid-state-transition", "Content must be approved before scheduling publication");
            }

            version.PublishAt = request.PublishAt;
            version.UpdatedAt = DateTimeOffset.UtcNow;
            version.UpdatedByUserId = currentActor.UserId;
            await dbContext.SaveChangesAsync(ct);
            return Ok(new PublishContentVersionResponse(await ToVersionDetailResponseAsync(version, DateTimeOffset.UtcNow, ct: ct), []));
        }

        if (!lifecycleService.CanTransition(version.Status, ContentStatus.Published, allowOverride))
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "invalid-state-transition", "Invalid content state transition", $"Content version cannot transition from {version.Status} to {ContentStatus.Published}.");
        }

        await lifecycleService.TransitionAsync(version, ContentStatus.Published, currentActor.UserId ?? Guid.Empty, allowOverride);
        version.PublishAt = null;
        var publishResult = await publishingService.PublishAsync(version, currentActor.UserId, ct);
        var publishedItem = await dbContext.ContentItems.FirstAsync(candidate => candidate.Id == id, ct);
        EnqueueContentEvent("content.version_published", publishedItem, publishResult.Version);
        await dbContext.SaveChangesAsync(ct);

        return Ok(new PublishContentVersionResponse(await ToVersionDetailResponseAsync(publishResult.Version, DateTimeOffset.UtcNow, ct: ct), publishResult.Warnings));
    }

    [HttpPost("{id:guid}/versions/{versionNumber:int}/upgrade-template-version")]
    [RequireRole(UserRole.Editor)]
    public async Task<ActionResult<ContentVersionDetailResponse>> UpgradeTemplateVersion(Guid workspaceId, Guid id, int versionNumber, CancellationToken ct)
    {
        var version = await LoadVersionForEditAsync(workspaceId, id, versionNumber, ct);
        if (version is null)
        {
            return NotFound();
        }

        if (version.Status is not (ContentStatus.Draft or ContentStatus.Review or ContentStatus.Approved))
        {
            return this.Error(StatusCodes.Status409Conflict, "conflict", "Only Draft, Review, or Approved versions can be upgraded");
        }

        var currentTemplateVersion = await dbContext.TemplateVersions.AsNoTracking().FirstAsync(tv => tv.Id == version.TemplateVersionId, ct);
        var target = await dbContext.TemplateVersions
            .Include(tv => tv.Fields).ThenInclude(field => field.AllowedTypes)
            .Where(tv => tv.TemplateId == currentTemplateVersion.TemplateId && tv.Status == TemplateVersionStatus.Published && !tv.IsDeleted)
            .OrderByDescending(tv => tv.VersionNumber)
            .FirstOrDefaultAsync(ct);
        if (target is null)
        {
            return this.Error(StatusCodes.Status409Conflict, "conflict", "No published template version is available");
        }

        version.TemplateVersionId = target.Id;
        var targetFieldIds = target.Fields.Select(field => field.Id).ToHashSet();
        var stale = version.FieldValues.Where(value => !targetFieldIds.Contains(value.FieldId)).ToList();
        dbContext.ContentVersionFieldValues.RemoveRange(stale);
        foreach (var value in stale)
        {
            version.FieldValues.Remove(value);
        }

        var validation = contentValidator.Validate(version, target);
        if (!validation.IsValid)
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Content does not satisfy the target template version", string.Join(" ", validation.Errors.Select(error => error.ErrorMessage)));
        }
        if (await ValidatePickListValuesAsync(version, target, ct) is { } pickListError)
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Content does not satisfy the target template version", pickListError);
        }

        var item = await dbContext.ContentItems.FirstAsync(candidate => candidate.Id == id, ct);
        item.SearchVector = searchVectorBuilder.Build(version, target);
        version.UpdatedAt = DateTimeOffset.UtcNow;
        version.UpdatedByUserId = currentActor.UserId;
        await dbContext.SaveChangesAsync(ct);
        return Ok(await ToVersionDetailResponseAsync(version, DateTimeOffset.UtcNow, ct: ct));
    }

    private async Task<ActionResult<ContentVersionDetailResponse>> Transition(Guid workspaceId, Guid id, int versionNumber, ContentStatus targetStatus, CancellationToken ct)
    {
        var version = await LoadVersionForEditAsync(workspaceId, id, versionNumber, ct);
        if (version is null)
        {
            return NotFound();
        }

        if (!lifecycleService.CanTransition(version.Status, targetStatus))
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "invalid-state-transition", "Invalid content state transition", $"Content version cannot transition from {version.Status} to {targetStatus}.");
        }

        await lifecycleService.TransitionAsync(version, targetStatus, currentActor.UserId ?? Guid.Empty);
        var item = await dbContext.ContentItems.FirstAsync(candidate => candidate.Id == id, ct);
        EnqueueContentEvent("content.version_status_changed", item, version);
        await dbContext.SaveChangesAsync(ct);

        return Ok(await ToVersionDetailResponseAsync(version, DateTimeOffset.UtcNow, ct: ct));
    }
```

- [ ] **Step 3d: Rewrite the controller — private helpers (continues and completes the same file)**

The first group below (`BaseContentQuery` through `TagsMatch`) is **unchanged verbatim** from today's implementation — included here only because Step 3a-3c said "replace the full file," so the complete final file needs them in place:

```csharp
    private IQueryable<ContentItem> BaseContentQuery(Guid workspaceId) =>
        dbContext.ContentItems.Where(content => content.WorkspaceId == workspaceId && !content.IsDeleted);

    private async Task<TemplateVersion?> LoadTemplateVersionAsync(Guid id, CancellationToken ct) =>
        await dbContext.TemplateVersions
            .Include(version => version.Fields).ThenInclude(field => field.AllowedTypes)
            .FirstOrDefaultAsync(version => version.Id == id && !version.IsDeleted, ct);

    private async Task<bool> TemplateVersionBelongsToWorkspaceAsync(Guid versionId, Guid workspaceId, CancellationToken ct) =>
        await dbContext.TemplateVersions.AnyAsync(version => version.Id == versionId && dbContext.Templates.Any(template => template.Id == version.TemplateId && template.WorkspaceId == workspaceId && !template.IsDeleted), ct);

    private async Task ApplyTagsAsync(ContentItem content, Guid workspaceId, IEnumerable<string> tags, CancellationToken ct)
    {
        foreach (var tagName in tags.Select(NormalizeTag).Where(tag => tag.Length > 0).Distinct())
        {
            var tag = await dbContext.Tags.FirstOrDefaultAsync(candidate => candidate.WorkspaceId == workspaceId && candidate.Name == tagName && !candidate.IsDeleted, ct);
            if (tag is null)
            {
                tag = new Tag { WorkspaceId = workspaceId, Name = tagName };
                dbContext.Tags.Add(tag);
            }

            content.Tags.Add(new ContentItemTag { ContentItemId = content.Id, TagId = tag.Id });
        }
    }

    private async Task<IReadOnlyList<string>> GetTagNamesAsync(Guid contentItemId, CancellationToken ct) =>
        await dbContext.ContentItemTags.AsNoTracking()
            .Where(join => join.ContentItemId == contentItemId)
            .Join(dbContext.Tags.AsNoTracking(), join => join.TagId, tag => tag.Id, (_, tag) => tag.Name)
            .OrderBy(tag => tag)
            .ToListAsync(ct);

    private static string NormalizeTag(string tag) => tag.Trim().ToLowerInvariant();

    private static bool TagsMatch(IEnumerable<string> existing, IEnumerable<string> requested) =>
        existing.Select(NormalizeTag).Where(tag => tag.Length > 0).Distinct().Order()
            .SequenceEqual(requested.Select(NormalizeTag).Where(tag => tag.Length > 0).Distinct().Order());
```

The rest are new or retargeted onto `ContentVersion`:

```csharp
    private async Task<ContentItem?> LoadContentForEditAsync(Guid workspaceId, Guid id, CancellationToken ct)
    {
        if (!await workspaceAuthorization.CanWriteWorkspaceAsync(workspaceId, ct))
        {
            return null;
        }

        return await BaseContentQuery(workspaceId)
            .Include(content => content.Tags)
            .FirstOrDefaultAsync(content => content.Id == id, ct);
    }

    private async Task<ContentVersion?> LoadVersionForEditAsync(Guid workspaceId, Guid contentItemId, int versionNumber, CancellationToken ct)
    {
        if (!await workspaceAuthorization.CanWriteWorkspaceAsync(workspaceId, ct))
        {
            return null;
        }

        var itemExists = await BaseContentQuery(workspaceId).AnyAsync(item => item.Id == contentItemId, ct);
        if (!itemExists)
        {
            return null;
        }

        return await dbContext.ContentVersions
            .Include(version => version.FieldValues)
            .FirstOrDefaultAsync(version => version.ContentItemId == contentItemId && version.VersionNumber == versionNumber && version.WorkspaceId == workspaceId, ct);
    }

    private static string? ValidateEffectiveRange(DateTimeOffset? start, DateTimeOffset? end)
    {
        if (start.HasValue != end.HasValue)
        {
            return "Provide both effectiveStartAt and effectiveEndAt, or neither.";
        }

        if (start.HasValue && start.Value >= end!.Value)
        {
            return "effectiveStartAt must be before effectiveEndAt.";
        }

        return null;
    }

    private async Task<string?> ApplyVersionFieldValuesAsync(ContentVersion version, TemplateVersion templateVersion, IReadOnlyList<ContentFieldValueRequest> fields, CancellationToken ct)
    {
        version.FieldValues.Clear();

        var fieldPickLists = templateVersion.Fields
            .Where(field => field.PrimitiveType == PrimitiveType.PickList)
            .Select(field => (field.Id, PickListId: GetPickListId(field), RevisionId: GetPickListRevisionId(field)))
            .Where(x => x.PickListId.HasValue)
            .ToDictionary(x => x.Id);
        var pickListIds = fieldPickLists.Values.Select(x => x.PickListId!.Value).Distinct().ToArray();
        var currentLabels = await dbContext.PickLists.AsNoTracking().Include(list => list.Options)
            .Where(list => pickListIds.Contains(list.Id))
            .ToDictionaryAsync(list => list.Id, list => list.Options.ToDictionary(option => option.Value, option => option.Label, StringComparer.OrdinalIgnoreCase), ct);
        var revisionIds = fieldPickLists.Values.Select(x => x.RevisionId).Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToArray();
        var revisionLabels = await dbContext.PickListRevisions.AsNoTracking().Include(revision => revision.Options)
            .Where(revision => revisionIds.Contains(revision.Id))
            .ToDictionaryAsync(revision => revision.Id, revision => revision.Options.ToDictionary(option => option.Value, option => option.Label, StringComparer.OrdinalIgnoreCase), ct);

        foreach (var input in fields)
        {
            var displayLabel = input.ValueKind == ValueKind.PickList && input.TextValue is not null && fieldPickLists.TryGetValue(input.FieldId, out var binding)
                ? (binding.RevisionId.HasValue && revisionLabels.TryGetValue(binding.RevisionId.Value, out var versionedOptions) ? versionedOptions : currentLabels.GetValueOrDefault(binding.PickListId!.Value))?.GetValueOrDefault(input.TextValue)
                : null;

            version.FieldValues.Add(new ContentVersionFieldValue
            {
                ContentVersionId = version.Id,
                FieldId = input.FieldId,
                Order = input.Order,
                ValueKind = input.ValueKind.ToCore(),
                TextValue = input.TextValue,
                DisplayLabel = displayLabel,
                BoolValue = input.BoolValue,
                MediaAssetId = input.MediaAssetId,
                FileAssetId = input.FileAssetId,
                ChildContentItemId = input.ChildContentItemId,
                JsonValue = input.JsonValue?.Clone()
            });
        }

        var validation = contentValidator.Validate(version, templateVersion);
        if (!validation.IsValid)
        {
            return string.Join(" ", validation.Errors.Select(error => error.ErrorMessage));
        }

        return await ValidatePickListValuesAsync(version, templateVersion, ct);
    }

    private static Guid? GetPickListId(TemplateField field)
    {
        if (field.FieldConfig is not { ValueKind: JsonValueKind.Object } config || !config.TryGetProperty("picklistId", out var id) || id.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        return Guid.TryParse(id.GetString(), out var parsed) ? parsed : null;
    }

    private static Guid? GetPickListRevisionId(TemplateField field)
    {
        if (field.FieldConfig is not { ValueKind: JsonValueKind.Object } config || !config.TryGetProperty("picklistRevisionId", out var id) || id.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        return Guid.TryParse(id.GetString(), out var parsed) ? parsed : null;
    }

    private async Task<string?> ValidatePickListValuesAsync(ContentVersion version, TemplateVersion templateVersion, CancellationToken ct)
    {
        var bindings = new Dictionary<Guid, (string Key, Guid RevisionId, bool Multiple)>();
        foreach (var field in templateVersion.Fields.Where(field => field.PrimitiveType == PrimitiveType.PickList))
        {
            if (!TryGetPickListBinding(field.FieldConfig, out var revisionId, out var multiple))
            {
                return $"Field '{field.Key}' must bind a PickList revision.";
            }

            bindings[field.Id] = (field.Key, revisionId, multiple);
        }

        var valuesByRevision = new Dictionary<Guid, HashSet<string>>();
        if (bindings.Count > 0)
        {
            var optionValues = await dbContext.PickListRevisionOptions.AsNoTracking()
                .Where(option => bindings.Values.Select(binding => binding.RevisionId).Contains(option.PickListRevisionId))
                .Select(option => new { option.PickListRevisionId, option.Value })
                .ToListAsync(ct);
            valuesByRevision = optionValues
                .GroupBy(option => option.PickListRevisionId)
                .ToDictionary(group => group.Key, group => group.Select(option => option.Value).ToHashSet(StringComparer.OrdinalIgnoreCase));
        }

        foreach (var group in version.FieldValues.Where(value => bindings.ContainsKey(value.FieldId)).GroupBy(value => value.FieldId))
        {
            var binding = bindings[group.Key];
            if (!binding.Multiple && group.Count() > 1)
            {
                return $"Field '{binding.Key}' allows only one PickList selection.";
            }

            if (!valuesByRevision.TryGetValue(binding.RevisionId, out var allowedValues))
            {
                return $"Field '{binding.Key}' references an unavailable PickList revision.";
            }

            foreach (var value in group)
            {
                if (string.IsNullOrWhiteSpace(value.TextValue) || !allowedValues.Contains(value.TextValue))
                {
                    return $"Field '{binding.Key}' contains a value that is not in its PickList revision.";
                }
            }
        }

        var revisionValueCache = valuesByRevision;
        foreach (var field in templateVersion.Fields.Where(field => field.ComponentId.HasValue))
        {
            foreach (var value in version.FieldValues.Where(value => value.FieldId == field.Id && value.JsonValue is not null))
            {
                if (await ValidateComponentPickListValuesAsync(field.ComponentId!.Value, value.JsonValue!.Value, revisionValueCache, ct) is { } componentError)
                {
                    return $"Field '{field.Key}': {componentError}";
                }
            }
        }

        return null;
    }
```

`ValidateComponentPickListValuesAsync` and `TryGetPickListBinding` are **unchanged verbatim** from today's implementation — neither takes a `ContentItem`/`ContentVersion` parameter (they operate on a raw `JsonElement` component value and a `JsonElement?` field config respectively), so copy them into the new file exactly as they exist today.

```csharp
    private async Task<ContentItemSummaryResponse> ToItemSummaryResponseAsync(ContentItem content, CancellationToken ct)
    {
        var template = await dbContext.TemplateVersions.AsNoTracking()
            .Where(version => version.Id == content.TemplateVersionId)
            .Select(version => dbContext.Templates.Where(t => t.Id == version.TemplateId).Select(t => t.Name).First())
            .FirstAsync(ct);
        var tags = await GetTagNamesAsync(content.Id, ct);
        var versions = await dbContext.ContentVersions.AsNoTracking().Where(v => v.ContentItemId == content.Id).ToListAsync(ct);
        var currentlyServing = ComputeCurrentlyServing(versions, DateTimeOffset.UtcNow);
        return new ContentItemSummaryResponse(
            content.Id, content.TemplateVersionId, template, content.Slug, content.LocaleCode, content.TranslationGroupId,
            tags, content.CreatedAt, content.UpdatedAt, versions.Count,
            currentlyServing is null ? null : ToVersionSummaryResponse(currentlyServing));
    }

    private async Task<ContentItemDetailResponse> ToItemDetailResponseAsync(Guid id, CancellationToken ct)
    {
        var content = await dbContext.ContentItems.AsNoTracking().FirstAsync(item => item.Id == id, ct);
        var template = await dbContext.TemplateVersions.AsNoTracking()
            .Where(version => version.Id == content.TemplateVersionId)
            .Select(version => dbContext.Templates.Where(t => t.Id == version.TemplateId).Select(t => t.Name).First())
            .FirstAsync(ct);
        var tags = await GetTagNamesAsync(content.Id, ct);
        var versions = await dbContext.ContentVersions.AsNoTracking()
            .Where(v => v.ContentItemId == id)
            .OrderByDescending(v => v.VersionNumber)
            .ToListAsync(ct);
        var currentlyServing = ComputeCurrentlyServing(versions, DateTimeOffset.UtcNow);
        return new ContentItemDetailResponse(
            content.Id, content.TemplateVersionId, template, content.Slug, content.LocaleCode, content.TranslationGroupId,
            tags, content.CreatedAt, content.UpdatedAt,
            currentlyServing is null ? null : ToVersionSummaryResponse(currentlyServing),
            versions.Select(ToVersionSummaryResponse).ToList());
    }

    private static ContentVersion? ComputeCurrentlyServing(IReadOnlyList<ContentVersion> versions, DateTimeOffset asOf)
    {
        var candidates = versions
            .Where(version => version.Status == ContentStatus.Published)
            .Where(version => (version.EffectiveStartAt is null && version.EffectiveEndAt is null)
                || (version.EffectiveStartAt <= asOf && asOf < version.EffectiveEndAt))
            .ToList();
        return candidates.Count == 0 ? null : SelectMostSpecific(candidates, asOf);
    }

    private static ContentVersion SelectMostSpecific(IEnumerable<ContentVersion> versions, DateTimeOffset asOf) =>
        versions
            .OrderBy(version => version.EffectiveStartAt.HasValue && version.EffectiveEndAt.HasValue ? 0 : 1)
            .ThenBy(version => version.EffectiveStartAt.HasValue && version.EffectiveEndAt.HasValue ? version.EffectiveEndAt!.Value - version.EffectiveStartAt!.Value : TimeSpan.MaxValue)
            .ThenByDescending(version => version.PublishedAt)
            .ThenByDescending(version => version.VersionNumber)
            .First();

    private async Task<ContentVersion?> ResolvePublishedVersionAsync(Guid workspaceId, Guid? contentItemId, string? slug, DateTimeOffset asOf, CancellationToken ct)
    {
        var query = dbContext.ContentVersions.AsNoTracking()
            .Include(version => version.FieldValues)
            .Where(version => version.WorkspaceId == workspaceId && version.Status == ContentStatus.Published)
            .Where(version =>
                (version.EffectiveStartAt == null && version.EffectiveEndAt == null)
                || (version.EffectiveStartAt <= asOf && asOf < version.EffectiveEndAt))
            .Where(version => !dbContext.ContentItems.Any(content => content.Id == version.ContentItemId && content.IsDeleted));

        if (contentItemId.HasValue)
        {
            query = query.Where(version => version.ContentItemId == contentItemId.Value);
        }

        if (!string.IsNullOrWhiteSpace(slug))
        {
            query = query.Where(version => version.Slug == slug);
        }

        var candidates = await query.ToListAsync(ct);
        return candidates.Count == 0 ? null : SelectMostSpecific(candidates, asOf);
    }

    private static ContentVersionSummaryResponse ToVersionSummaryResponse(ContentVersion version) =>
        new(version.Id, version.ContentItemId, version.VersionNumber, version.Status.ToContract(), version.TemplateVersionId,
            version.Slug, version.LocaleCode, version.EffectiveStartAt, version.EffectiveEndAt, version.PublishAt, version.PublishedAt,
            version.ArchivedAt, version.PublishedByUserId, version.RolledBackFromVersionNumber, version.Tags.ToList(),
            version.CreatedAt, version.UpdatedAt);

    private async Task<ContentVersionDetailResponse> ToVersionDetailResponseAsync(ContentVersion version, DateTimeOffset asOf, int depth = 0, CancellationToken ct = default)
    {
        var templateName = await dbContext.TemplateVersions.AsNoTracking()
            .Where(tv => tv.Id == version.TemplateVersionId)
            .Select(tv => dbContext.Templates.Where(t => t.Id == tv.TemplateId).Select(t => t.Name).First())
            .FirstOrDefaultAsync(ct) ?? string.Empty;
        var templateFields = await dbContext.TemplateFields.AsNoTracking()
            .Where(field => field.TemplateVersionId == version.TemplateVersionId)
            .ToDictionaryAsync(field => field.Id, ct);
        var fieldValues = version.FieldValues.Count > 0
            ? version.FieldValues
            : await dbContext.ContentVersionFieldValues.AsNoTracking().Where(value => value.ContentVersionId == version.Id).ToListAsync(ct);

        var fields = new List<ContentVersionFieldValueResponse>();
        foreach (var value in fieldValues.OrderBy(value => templateFields.GetValueOrDefault(value.FieldId)?.Order ?? 0).ThenBy(value => value.Order))
        {
            templateFields.TryGetValue(value.FieldId, out var field);
            ContentVersionDetailResponse? child = null;
            if (depth < 8 && value.ChildContentItemId.HasValue)
            {
                var childVersion = await ResolvePublishedVersionAsync(version.WorkspaceId, value.ChildContentItemId.Value, slug: null, asOf, ct);
                if (childVersion is not null)
                {
                    child = await ToVersionDetailResponseAsync(childVersion, asOf, depth + 1, ct);
                }
            }

            fields.Add(new ContentVersionFieldValueResponse(value.FieldId, field?.Key, field?.Label, value.Order, value.ValueKind.ToContract(), value.TextValue, value.BoolValue, value.MediaAssetId, value.FileAssetId, value.ChildContentItemId, child, value.JsonValue?.Clone(), value.DisplayLabel));
        }

        return new ContentVersionDetailResponse(
            version.Id, version.ContentItemId, version.VersionNumber, version.Status.ToContract(), version.TemplateVersionId, templateName,
            version.Slug, version.LocaleCode, version.TranslationGroupId, version.EffectiveStartAt, version.EffectiveEndAt,
            version.PublishAt, version.PublishedAt, version.ArchivedAt, version.PublishedByUserId, version.RolledBackFromVersionNumber,
            version.Tags.ToList(), version.CreatedAt, version.UpdatedAt, fields);
    }

    private void EnqueueContentEvent(string eventType, ContentItem content, ContentVersion? version)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            contentItemId = content.Id,
            workspaceId = content.WorkspaceId,
            templateVersionId = content.TemplateVersionId,
            contentVersionId = version?.Id,
            versionNumber = version?.VersionNumber,
            status = version?.Status.ToString()
        });
        webhookOutbox.Enqueue(eventType, content.WorkspaceId, content.Id, payload, DateTimeOffset.UtcNow);
    }

    private void SoftDelete(ContentItem content)
    {
        content.IsDeleted = true;
        content.DeletedAt = DateTimeOffset.UtcNow;
        content.DeletedByUserId = currentActor.UserId;
        content.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private async Task<IReadOnlyList<Guid>> ReferencingContentIdsAsync(Guid id, bool onlyReferenceFields, CancellationToken ct)
    {
        var query = dbContext.ContentVersionFieldValues.AsNoTracking().Where(value => value.ChildContentItemId == id);
        if (onlyReferenceFields)
        {
            query = query.Where(value => dbContext.TemplateFields.Any(field => field.Id == value.FieldId && field.CompositionMode == CompositionMode.Reference));
        }

        return await query
            .Join(dbContext.ContentVersions.AsNoTracking(), value => value.ContentVersionId, version => version.Id, (value, version) => version.ContentItemId)
            .Distinct()
            .ToListAsync(ct);
    }
}
```

This closes the class (final `}` above matches the `public sealed class ContentController` opening from Step 3a).

Practical note on assembling this as one file: since Steps 3a-3d together are a full-file replacement, do the edit with the *current* `ContentController.cs` still open/available (e.g. in git history, `git show HEAD:src/Cmsify.Api/Controllers/ContentController.cs`) so `ValidateComponentPickListValuesAsync` and `TryGetPickListBinding` (noted above as unchanged verbatim) can be copied across rather than retyped from memory.

- [ ] **Step 4: Build**

Run: `dotnet build src/Cmsify.Api/Cmsify.Api.csproj`
Expected: PASS. If there are errors, they should only be about the `Cmsify.Admin` project (which still references the old contracts/routes — fixed by the frontend follow-up plan, out of scope here) — confirm no errors within `Cmsify.Api`/`Cmsify.Core`/`Cmsify.Infrastructure`/`Cmsify.Contracts`.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Cmsify.Api.Integration.Tests/Cmsify.Api.Integration.Tests.csproj --filter FullyQualifiedName~ContentVersionWorkflowTests`
Expected: PASS (all 3 written tests, plus whichever additional tests from the "additional coverage" list in Step 1 were added). Requires Docker running locally.

- [ ] **Step 6: Commit**

```bash
git add src/Cmsify.Api/Controllers/ContentController.cs src/Cmsify.Api/Queries/ResolvedContentListQuery.cs tests/Cmsify.Api.Integration.Tests/ContentVersionWorkflowTests.cs
git commit -m "$(cat <<'EOF'
Rewrite ContentController for the version-centric content API

Item-level workflow routes are replaced by version-level ones
(/content/{id}/versions/{versionNumber}/submit, etc.). GetBySlug now
resolves to a ContentVersionDetailResponse directly. Rollback is
replaced by CreateVersion's DuplicateFromVersionNumber. Item responses
gain a computed CurrentlyServingVersion.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01C9zyjQzBXAgvN5wKsUSRvJ
EOF
)"
```

---

### Task 12: Fix existing tests broken by the entity/contract changes

**Files:**
- Modify: `tests/Cmsify.Api.Integration.Tests/ContentPublishRangeTests.cs`
- Modify: `tests/Cmsify.Api.Integration.Tests/ResolvedContentListQueryTests.cs`

**Interfaces:**
- Consumes: `ContentItem`/`ContentVersion` (Task 1), `ContentStatus` (Task 9 removed `ContentVersionStatus`).
- Produces: both files compiling and passing again against the new model.

- [ ] **Step 1: Fix `ContentPublishRangeTests.cs`**

In `SeedContentWithRangesAsync`, the `ContentItem` initializer currently sets `Status = ContentStatus.Published` and `PublishedAt = ...` — both properties no longer exist on `ContentItem`. Remove those two lines from the initializer entirely (the item becomes just `WorkspaceId`, `TemplateVersionId`, `Slug`). Every `ContentVersion` initializer in the same method sets `Status = ContentVersionStatus.Published` — change each to `Status = ContentStatus.Published` (the type this plan unified onto).

- [ ] **Step 2: Run this test to verify it still passes**

Run: `dotnet test tests/Cmsify.Api.Integration.Tests/Cmsify.Api.Integration.Tests.csproj --filter FullyQualifiedName~ContentPublishRangeTests`
Expected: PASS. This test exercises `GetBySlug`'s specificity resolution end-to-end (unchanged logic per Task 11), so a pass here is strong confirmation that Task 11's `ResolvePublishedVersionAsync`/`SelectMostSpecific` port is correct.

- [ ] **Step 3: Fix `ResolvedContentListQueryTests.cs`**

This file (778 lines) seeds `ContentItem`/`ContentVersion` rows at scale and asserts on generated SQL shape via a `DbCommandInterceptor`. Rather than hand-editing it blind, build the test project and fix each compile error following the exact same patterns established in Tasks 1-11:

Run: `dotnet build tests/Cmsify.Api.Integration.Tests/Cmsify.Api.Integration.Tests.csproj`

Expected failure categories and fixes:
- Any `ContentItem` initializer setting `Status`/`FieldValues`/`PublishAt`/`Pending*`/`PublishedAt`/`ArchivedAt`/`PublishLease*` — remove those property assignments (Task 1 removed them from the entity).
- Any `ContentVersionStatus.Published`/`.Retired` reference — change to `ContentStatus.Published`/`ContentStatus.Archived` respectively (Task 9 deleted the enum; `Archived` is the renamed equivalent of the old `Retired`, per Task 6).
- Any reference to `version.RetiredAt` — change to `version.ArchivedAt` (Task 1 renamed the column/property).
- Any SQL-shape assertion string-matching on `content_items` columns that Task 5's migration dropped (`status`, `publish_at`, etc. on that table) — if the test's SQL assertions were about the *item* table's shape, they now need to assert against `content_versions` instead, since that's where those columns live post-migration.
- If the test seeds via `ContentFieldValue`/`dbContext.ContentFieldValues`, switch to `ContentVersionFieldValue`/`dbContext.ContentVersionFieldValues`, keyed by `ContentVersionId` instead of `ContentItemId`.

- [ ] **Step 4: Run the full test suite to verify it passes**

Run: `dotnet test tests/Cmsify.Api.Integration.Tests/Cmsify.Api.Integration.Tests.csproj --filter FullyQualifiedName~ResolvedContentListQueryTests`
Expected: PASS.

- [ ] **Step 5: Run the entire solution's test suite as a final sanity check**

Run: `dotnet test`
Expected: PASS across every test project (`Cmsify.Core.Tests`, `Cmsify.Api.Integration.Tests`, and any others in the solution). Investigate and fix anything still failing before moving to Task 13 — a red test suite here means something in Tasks 1-12 was missed.

- [ ] **Step 6: Commit**

```bash
git add tests/Cmsify.Api.Integration.Tests/ContentPublishRangeTests.cs tests/Cmsify.Api.Integration.Tests/ResolvedContentListQueryTests.cs
git commit -m "$(cat <<'EOF'
Fix existing integration tests for the ContentItem/ContentVersion split

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01C9zyjQzBXAgvN5wKsUSRvJ
EOF
)"
```

---

### Task 13: TypeScript SDK — regenerate schema, fix the one breaking type

**Files:**
- Regenerate: `sdk/typescript/src/generated/schema.ts`, `sdk/typescript/src/generated/client.ts`
- Modify: `sdk/typescript/src/types.ts`
- Modify: `sdk/typescript/src/client.ts`

**Interfaces:**
- Consumes: the new contracts from Task 9 (`ContentItemDetailResponse`'s new shape, new `ContentVersionDetailResponse`).
- Produces: `ContentVersion` type export from `types.ts`; `CmsifyClient.content.bySlug` returns `Promise<ContentVersion>` instead of `Promise<ContentItem>`.

Good news from inspecting the actual SDK: `sdk/typescript/src/client.ts` is a minimal **read-only delivery facade** — it only exposes `content.list`/`content.get`/`content.bySlug`/`content.translations`, `templates.list`/`get`, and `media.list`/`get`/`download`. It has **no create/update/workflow methods at all** (those live only in the Admin UI and raw HTTP, not this SDK). So the actual breaking surface here is much smaller than the item-vs-version routes suggested: only `content.bySlug`'s return type changes, since `GetBySlug` now resolves to a `ContentVersionDetailResponse` (Task 11) instead of `ContentItemDetailResponse`. `content.get`/`content.list`'s response *shapes* change (new `Versions`/`CurrentlyServingVersion` fields, dropped `Status`/`Fields`) but their type *aliases* stay pointed at the same schema names, so no manual code change is needed there beyond regenerating the schema.

- [ ] **Step 1: Regenerate the OpenAPI schema**

Run (from repo root):

```bash
node scripts/openapi.mjs update
```

Expected: succeeds, rewrites `sdk/typescript/src/generated/schema.ts` (and `generated/client.ts` if the generator touches it) to reflect Task 9's new/changed contracts. This requires Tasks 1-12 to already compile (the script builds `Cmsify.Api` in Release and reflects over the assembly to produce the OpenAPI document — see `scripts/openapi.mjs`).

- [ ] **Step 2: Add the `ContentVersion` type export**

In `sdk/typescript/src/types.ts`, add alongside the existing `ContentItem` export:

```typescript
export type ContentVersion = components["schemas"]["ContentVersionDetailResponse"];
```

- [ ] **Step 3: Fix `bySlug`'s return type**

In `sdk/typescript/src/client.ts`:
- Add `ContentVersion` to the existing type-only import: change `import type { ContentItem, ContentListItem, MediaAsset, PagedResult, Template, TemplateListItem } from "./types";` to also include `ContentVersion`.
- Change the `bySlug` method:

```typescript
bySlug: (slug: string, options: Pick<ContentListOptions, "asOf"> = {}) => this.request<ContentVersion>(this.workspacePath(`/content/by-slug/${encodeURIComponent(slug)}`, detailQuery(options, false))),
```

(only the generic type argument changes, from `<ContentItem>` to `<ContentVersion>` — the URL/query construction is unchanged, since the route itself didn't move.)

- [ ] **Step 4: Typecheck and run the SDK's own test suite**

Run (from `sdk/typescript/`):

```bash
npm run typecheck
npm test
```

Expected: `typecheck` passes once Steps 1-3 land. `npm test` runs `client.test.ts`, `openapi-workflow.test.ts`, `templating.test.ts`, `formatting.test.ts`. Fix any remaining compile/assertion errors the same way: if a test asserts on `ContentItemDetailResponse` having `status`/`fields`/`publishAt` (old shape), update it to assert on the new `versions`/`currentlyServingVersion` shape instead; if `openapi-workflow.test.ts` exercises any raw generated-client path beyond the hand-wrapped methods above, point it at the new route table from Task 11 (e.g. `/content/{id}/versions/{versionNumber}/...`).

- [ ] **Step 5: Run the clean-consumer check**

Run (from `sdk/typescript/`): `npm run test:consumer`
Expected: PASS — confirms the built package still has no leaked internal types/paths for external consumers.

- [ ] **Step 6: Commit**

```bash
git add sdk/typescript/src/generated/ sdk/typescript/src/types.ts sdk/typescript/src/client.ts
git commit -m "$(cat <<'EOF'
Update TypeScript SDK for the version-centric content API

content.bySlug now resolves to a ContentVersionDetailResponse (the
resolved version's fields), not ContentItemDetailResponse. This is a
breaking change, accepted because the project is pre-release with no
external SDK consumers yet.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01C9zyjQzBXAgvN5wKsUSRvJ
EOF
)"
```

---

### Task 14: CHANGELOG entry

**Files:**
- Modify: `CHANGELOG.md`

**Interfaces:**
- Consumes: nothing.
- Produces: a changelog entry documenting the breaking change, in the repo's existing Keep a Changelog format.

- [ ] **Step 1: Add the entry**

In `CHANGELOG.md`, the file currently starts:

```markdown
## [Unreleased]

## [0.3.2] - 2026-09-06
```

Replace with (adding content under the existing empty `[Unreleased]` header — leave the version-numbering/dating of this section to the repo's own release process rather than guessing a version number here):

```markdown
## [Unreleased]

### Changed

- **Breaking:** `ContentVersion` is now the sole carrier of content fields and workflow lifecycle (Draft → Review → Approved → Published → Archived); `ContentItem` is now a lightweight slug/template/locale identity header. This enables independently-editable, independently-scheduled date-bounded versions of the same content item. The Content API's item-level workflow routes (`/content/{id}/submit`, `/approve`, `/reject`, `/publish`, `/archive`, `/restore`, `/rollback`, `/upgrade-version`) are replaced by version-level equivalents (`/content/{id}/versions/{versionNumber}/submit`, etc.); `GetBySlug` now resolves to a `ContentVersionDetailResponse` instead of `ContentItemDetailResponse`. The TypeScript SDK's `content.bySlug` return type changes accordingly. This is a breaking change to the Content API and its contracts — accepted as acceptable pre-release, since there are no external consumers of the API or the TypeScript SDK yet.

## [0.3.2] - 2026-09-06
```

- [ ] **Step 2: Commit**

```bash
git add CHANGELOG.md
git commit -m "$(cat <<'EOF'
Document the content-version-unification breaking change in CHANGELOG

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01C9zyjQzBXAgvN5wKsUSRvJ
EOF
)"
```

---

## Follow-up (out of scope for this plan)

Once this backend plan lands, write a second plan for the **frontend** UI changes described in the architecture spec (`stay-on-this-branch-wondrous-floyd.md`'s "UI Changes" section): the Content list's "group by template" toggle and aggregate/currently-serving status badge, and the version-set manager page merging `ContentEditor.razor`/`ContentVersions.razor`. That plan depends on the exact contracts this one produces (Task 9) and should be written fresh against the landed API rather than guessed now — `Cmsify.Admin` will not compile again until that follow-up plan updates it to call the new version-level routes and consume the new response shapes.
