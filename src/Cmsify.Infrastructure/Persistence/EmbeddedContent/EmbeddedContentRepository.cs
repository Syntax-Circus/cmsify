using Cmsify.Core.EmbeddedContent;
using Cmsify.Core.Interfaces.Services;
using Microsoft.EntityFrameworkCore;
using SyntaxCircus.Common;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.ContentWrites;
using Cmsify.Infrastructure.Persistence.ContentWrites;
using System.Data;

namespace Cmsify.Infrastructure.Persistence.EmbeddedContent;

public sealed class EmbeddedContentRepository(DbContextOptions<CmsifyDbContext> options,
    IContentValidator validator, IContentSearchVectorBuilder searchVectorBuilder, TimeProvider clock) : IEmbeddedContentRepository
{
    public async Task<Result<EmbeddedContentVersionOutput>> GetEmbeddedContentVersionAsync(GetEmbeddedContentVersionRequest request,
        Func<EmbeddedContentOperation, CancellationToken, Task<bool>> authorizeLoaded, CancellationToken cancellationToken)
    {
        _ = searchVectorBuilder;
        cancellationToken.ThrowIfCancellationRequested();
        await using var db = new CmsifyDbContext(options);
        if (!EmbeddedTemplateRepository.Supported(db)) return Failure<EmbeddedContentVersionOutput>();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var loaded = await LoadAsync(db, request.WorkspaceId, request.ContentItemId, request.VersionNumber,
            request.TemplateVersionId, request.ContractFingerprint, EmbeddedContentOperationKind.VersionRead, authorizeLoaded, cancellationToken);
        if (loaded.IsFailure) return Result<EmbeddedContentVersionOutput>.Failure(loaded.Errors[0]);
        if (request.Revision is { } revision && !revision.Matches(loaded.Value.Version.UpdatedAt))
            return Denied<EmbeddedContentVersionOutput>("concurrency-mismatch", ResultErrorKind.Conflict);
        return await ProjectAsync(db, loaded.Value.Version, request.ContractFingerprint, cancellationToken);
    }
    public Task<Result<EmbeddedContentWriteOutput>> CreateEmbeddedContentAsync(CreateEmbeddedContentRequest request, Guid actorSubject,
        Func<EmbeddedContentOperation, CancellationToken, Task<bool>> authorizeLoaded, CancellationToken cancellationToken)
        => Task.FromResult(Failure<EmbeddedContentWriteOutput>());
    public Task<Result<EmbeddedContentWriteOutput>> CreateEmbeddedContentVersionAsync(CreateEmbeddedContentVersionRequest request, Guid actorSubject,
        Func<EmbeddedContentOperation, CancellationToken, Task<bool>> authorizeLoaded, CancellationToken cancellationToken)
        => Task.FromResult(Failure<EmbeddedContentWriteOutput>());
    public Task<Result<EmbeddedContentReceiptOutput>> GetEmbeddedContentOperationReceiptAsync(GetEmbeddedContentOperationReceiptRequest request,
        Func<EmbeddedContentOperation, CancellationToken, Task<bool>> authorizeLoaded, CancellationToken cancellationToken)
        => Task.FromResult(Failure<EmbeddedContentReceiptOutput>());
    private static Result<T> Failure<T>() => Result<T>.Failure(new(EmbeddedContentErrors.ProviderUnsupported, "Unsupported provider.", ResultErrorKind.Validation));

    private sealed record Loaded(ContentItem Item, ContentVersion Version, TemplateVersion Schema, EmbeddedTemplateRegistration Registration);
    private async Task<Result<Loaded>> LoadAsync(CmsifyDbContext db, Guid workspaceId, Guid itemId, int versionNumber,
        Guid templateVersionId, string fingerprint, EmbeddedContentOperationKind kind,
        Func<EmbeddedContentOperation, CancellationToken, Task<bool>> authorizeLoaded, CancellationToken cancellationToken)
    {
        var item = await db.ContentItems.AsNoTracking().SingleOrDefaultAsync(x => x.Id == itemId
            && x.WorkspaceId == workspaceId && !x.IsDeleted, cancellationToken);
        if (item is null) return Denied<Loaded>("not-found", ResultErrorKind.NotFound);
        var version = await db.ContentVersions.AsNoTracking().Include(x => x.FieldValues)
            .SingleOrDefaultAsync(x => x.ContentItemId == item.Id && x.WorkspaceId == item.WorkspaceId
                && x.VersionNumber == versionNumber, cancellationToken);
        if (version is null) return Denied<Loaded>("not-found", ResultErrorKind.NotFound);
        var registration = await db.EmbeddedTemplateRegistrations.AsNoTracking().SingleOrDefaultAsync(x => x.TemplateVersionId == version.TemplateVersionId, cancellationToken);
        if (registration is null || registration.WorkspaceId != item.WorkspaceId || item.TemplateVersionId != version.TemplateVersionId
            || version.TemplateVersionId != templateVersionId || registration.Fingerprint != fingerprint)
            return Denied<Loaded>(EmbeddedContentErrors.TemplateMismatch, ResultErrorKind.Conflict);
        if (!await authorizeLoaded(new(kind, item.WorkspaceId, registration.ContractKey, registration.Fingerprint,
            version.TemplateVersionId, item.Id, version.VersionNumber), cancellationToken))
            return Denied<Loaded>("forbidden", ResultErrorKind.Forbidden);
        var actual = await EmbeddedTemplateRepository.ReadActualAsync(db, registration, fingerprint, cancellationToken);
        if (actual.IsFailure) return Result<Loaded>.Failure(actual.Errors[0]);
        var schema = await db.TemplateVersions.AsNoTracking().Include(x => x.Fields).ThenInclude(x => x.AllowedTypes)
            .SingleAsync(x => x.Id == version.TemplateVersionId, cancellationToken);
        if (!validator.Validate(version, schema).IsValid || !ValidFields(version.FieldValues, schema))
            return Denied<Loaded>(EmbeddedContentErrors.Validation, ResultErrorKind.Validation);
        return Result<Loaded>.Success(new(item, version, schema, registration));
    }
    private async Task<Result<EmbeddedContentVersionOutput>> ProjectAsync(CmsifyDbContext db, ContentVersion version,
        string fingerprint, CancellationToken cancellationToken)
    {
        var detail = await new ContentVersionDetailProjector(db).ProjectAsync(version, clock.GetUtcNow(), false, cancellationToken);
        var owned = detail with { Tags = Array.AsReadOnly(detail.Tags.ToArray()), Fields = Array.AsReadOnly(detail.Fields.Select(f =>
            f with { JsonValue = f.JsonValue?.Clone(), Child = null }).ToArray()) };
        return Result<EmbeddedContentVersionOutput>.Success(new(fingerprint, ContentVersionRevisionCondition.Normalize(owned.UpdatedAt), owned));
    }
    private static bool ValidFields(IEnumerable<ContentVersionFieldValue> fields, TemplateVersion schema)
    {
        var seen = new HashSet<Guid>();
        foreach (var value in fields)
            if (!seen.Add(value.FieldId) || !schema.Fields.Any(f => f.Id == value.FieldId) || value.Order < 0
                || value.ValueKind != Cmsify.Core.Domain.Enums.ValueKind.Text || value.BoolValue is not null
                || value.MediaAssetId is not null || value.FileAssetId is not null || value.ChildContentItemId is not null
                || value.JsonValue is not null) return false;
        return true;
    }
    private static Result<T> Denied<T>(string code, ResultErrorKind kind)
        => Result<T>.Failure(new(code, "Resource unavailable or invalid request.", kind));
}
