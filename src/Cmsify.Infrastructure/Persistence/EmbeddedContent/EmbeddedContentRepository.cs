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
    public async Task<Result<EmbeddedContentWriteOutput>> CreateEmbeddedContentAsync(CreateEmbeddedContentRequest request, Guid actorSubject,
        Func<EmbeddedContentOperation, CancellationToken, Task<bool>> authorizeLoaded, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var db = new CmsifyDbContext(options);
        if (!EmbeddedTemplateRepository.Supported(db)) return Failure<EmbeddedContentWriteOutput>();
        if (!ValidRequest(request.WorkspaceId, request.ContentItemId, request.TemplateVersionId, request.OperationKey, actorSubject,
            request.ContractFingerprint, request.Fields)) return Denied<EmbeddedContentWriteOutput>(EmbeddedContentErrors.Validation, ResultErrorKind.Validation);
        request = request with { Fields = Array.AsReadOnly(request.Fields.ToArray()) };
        var hash = EmbeddedWriteFingerprint.Create(actorSubject, request.WorkspaceId, EmbeddedWriteKind.ItemCreate,
            request.ContentItemId, request.TemplateVersionId, request.ContractFingerprint, null, null, request.Fields);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await EmbeddedOperationLocks.LockAsync(db, $"receipt:{request.WorkspaceId:N}:{EmbeddedWriteKind.ItemCreate}:{request.OperationKey:N}", cancellationToken);
        await EmbeddedOperationLocks.LockAsync(db, $"item:{request.ContentItemId:N}", cancellationToken);
        var receipt = await db.EmbeddedContentReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.WorkspaceId == request.WorkspaceId
            && x.Kind == EmbeddedWriteKind.ItemCreate && x.OperationKey == request.OperationKey, cancellationToken);
        if (receipt is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return await ReadResponseAsync(receipt, request.ContentItemId, hash, authorizeLoaded, cancellationToken);
        }
        var registration = await db.EmbeddedTemplateRegistrations.AsNoTracking().SingleOrDefaultAsync(x => x.TemplateVersionId == request.TemplateVersionId, cancellationToken);
        if (registration is null || registration.WorkspaceId != request.WorkspaceId || registration.Fingerprint != request.ContractFingerprint)
            return Denied<EmbeddedContentWriteOutput>(EmbeddedContentErrors.TemplateMismatch, ResultErrorKind.Conflict);
        if (!await authorizeLoaded(new(EmbeddedContentOperationKind.ItemCreate, registration.WorkspaceId, registration.ContractKey,
            registration.Fingerprint, registration.TemplateVersionId, request.ContentItemId, 1), cancellationToken))
            return Denied<EmbeddedContentWriteOutput>("forbidden", ResultErrorKind.Forbidden);
        var actual = await EmbeddedTemplateRepository.ReadActualAsync(db, registration, request.ContractFingerprint, cancellationToken);
        if (actual.IsFailure) return Result<EmbeddedContentWriteOutput>.Failure(actual.Errors[0]);
        if (await db.ContentItems.AnyAsync(x => x.Id == request.ContentItemId, cancellationToken))
            return Denied<EmbeddedContentWriteOutput>(EmbeddedContentErrors.OperationConflict, ResultErrorKind.Conflict);
        var schema = await db.TemplateVersions.Include(x => x.Fields).ThenInclude(x => x.AllowedTypes)
            .SingleAsync(x => x.Id == registration.TemplateVersionId, cancellationToken);
        var time = clock.GetUtcNow();
        var item = new ContentItem { Id = request.ContentItemId, WorkspaceId = request.WorkspaceId,
            TemplateVersionId = request.TemplateVersionId, CreatedByUserId = actorSubject, UpdatedByUserId = actorSubject, CreatedAt = time, UpdatedAt = time };
        var version = new ContentVersion { ContentItemId = item.Id, WorkspaceId = item.WorkspaceId,
            TemplateVersionId = item.TemplateVersionId, VersionNumber = 1, CreatedByUserId = actorSubject,
            UpdatedByUserId = actorSubject, CreatedAt = time, UpdatedAt = time };
        if (await new ContentVersionFieldWriter(db, validator).ApplyAsync(version, schema, request.Fields, cancellationToken) is not null)
            return Denied<EmbeddedContentWriteOutput>(EmbeddedContentErrors.Validation, ResultErrorKind.Validation);
        if (!ValidFields(version.FieldValues, schema))
            return Denied<EmbeddedContentWriteOutput>(EmbeddedContentErrors.Validation, ResultErrorKind.Validation);
        item.SearchVector = searchVectorBuilder.Build(version, schema);
        receipt = NewReceipt(request.WorkspaceId, EmbeddedWriteKind.ItemCreate, request.OperationKey, version,
            request.ContractFingerprint, hash, actorSubject, time);
        db.AddRange(item, version, receipt);
        new EfWebhookOutbox(db).Enqueue("content.created", item.WorkspaceId, item.Id,
            System.Text.Json.JsonSerializer.SerializeToElement(new { contentItemId = item.Id, workspaceId = item.WorkspaceId,
                templateVersionId = item.TemplateVersionId, contentVersionId = version.Id, versionNumber = version.VersionNumber, status = version.Status.ToString() }), time);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        // A durable commit can be followed by cancellation/projection failure; never replay automatically.
        cancellationToken.ThrowIfCancellationRequested();
        return await OriginalResponseAsync(receipt, authorizeLoaded, cancellationToken);
    }
    public async Task<Result<EmbeddedContentWriteOutput>> CreateEmbeddedContentVersionAsync(CreateEmbeddedContentVersionRequest request, Guid actorSubject,
        Func<EmbeddedContentOperation, CancellationToken, Task<bool>> authorizeLoaded, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var db = new CmsifyDbContext(options);
        if (!EmbeddedTemplateRepository.Supported(db)) return Failure<EmbeddedContentWriteOutput>();
        if (!ValidRequest(request.WorkspaceId, request.ContentItemId, request.TemplateVersionId, request.OperationKey,
            actorSubject, request.ContractFingerprint, request.Fields) || request.SourceVersionNumber <= 0 || request.SourceRevision?.Candidate is null)
            return Denied<EmbeddedContentWriteOutput>(EmbeddedContentErrors.Validation, ResultErrorKind.Validation);
        request = request with { Fields = Array.AsReadOnly(request.Fields.ToArray()) };
        var hash = EmbeddedWriteFingerprint.Create(actorSubject, request.WorkspaceId, EmbeddedWriteKind.VersionCreate,
            request.ContentItemId, request.TemplateVersionId, request.ContractFingerprint, request.SourceVersionNumber, request.SourceRevision, request.Fields);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await EmbeddedOperationLocks.LockAsync(db, $"receipt:{request.WorkspaceId:N}:{EmbeddedWriteKind.VersionCreate}:{request.OperationKey:N}", cancellationToken);
        await EmbeddedOperationLocks.LockAsync(db, $"item:{request.ContentItemId:N}", cancellationToken);
        var receipt = await db.EmbeddedContentReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.WorkspaceId == request.WorkspaceId
            && x.Kind == EmbeddedWriteKind.VersionCreate && x.OperationKey == request.OperationKey, cancellationToken);
        if (receipt is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return await ReadResponseAsync(receipt, request.ContentItemId, hash, authorizeLoaded, cancellationToken, request.SourceVersionNumber);
        }
        var loaded = await LoadAsync(db, request.WorkspaceId, request.ContentItemId, request.SourceVersionNumber,
            request.TemplateVersionId, request.ContractFingerprint, EmbeddedContentOperationKind.VersionCreate, authorizeLoaded, cancellationToken);
        if (loaded.IsFailure) return Result<EmbeddedContentWriteOutput>.Failure(loaded.Errors[0]);
        if (!request.SourceRevision.Matches(loaded.Value.Version.UpdatedAt))
            return Denied<EmbeddedContentWriteOutput>("concurrency-mismatch", ResultErrorKind.Conflict);
        // Row lock serializes allocation and prevents a concurrent ordinary update changing the checked source.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM content_versions WHERE id = {loaded.Value.Version.Id} FOR UPDATE", cancellationToken);
        var source = await db.ContentVersions.AsNoTracking().SingleAsync(x => x.Id == loaded.Value.Version.Id, cancellationToken);
        if (!request.SourceRevision.Matches(source.UpdatedAt))
            return Denied<EmbeddedContentWriteOutput>("concurrency-mismatch", ResultErrorKind.Conflict);
        var next = await db.ContentVersions.Where(x => x.ContentItemId == request.ContentItemId).MaxAsync(x => x.VersionNumber, cancellationToken) + 1;
        var time = clock.GetUtcNow();
        var version = new ContentVersion { ContentItemId = source.ContentItemId, WorkspaceId = source.WorkspaceId,
            TemplateVersionId = source.TemplateVersionId, VersionNumber = next, Slug = source.Slug, LocaleCode = source.LocaleCode,
            TranslationGroupId = source.TranslationGroupId, Tags = source.Tags.ToArray(), EffectiveStartAt = source.EffectiveStartAt,
            EffectiveEndAt = source.EffectiveEndAt, CreatedAt = time, UpdatedAt = time, CreatedByUserId = actorSubject, UpdatedByUserId = actorSubject };
        if (await new ContentVersionFieldWriter(db, validator).ApplyAsync(version, loaded.Value.Schema, request.Fields, cancellationToken) is not null)
            return Denied<EmbeddedContentWriteOutput>(EmbeddedContentErrors.Validation, ResultErrorKind.Validation);
        if (!ValidFields(version.FieldValues, loaded.Value.Schema))
            return Denied<EmbeddedContentWriteOutput>(EmbeddedContentErrors.Validation, ResultErrorKind.Validation);
        var item = await db.ContentItems.SingleAsync(x => x.Id == source.ContentItemId, cancellationToken);
        item.SearchVector = searchVectorBuilder.Build(version, loaded.Value.Schema);
        item.UpdatedAt = time; item.UpdatedByUserId = actorSubject;
        receipt = NewReceipt(request.WorkspaceId, EmbeddedWriteKind.VersionCreate, request.OperationKey, version,
            request.ContractFingerprint, hash, actorSubject, time);
        db.AddRange(version, receipt);
        new EfWebhookOutbox(db).Enqueue("content.version_created", item.WorkspaceId, item.Id,
            System.Text.Json.JsonSerializer.SerializeToElement(new { contentItemId = item.Id, workspaceId = item.WorkspaceId,
                templateVersionId = item.TemplateVersionId, contentVersionId = version.Id, versionNumber = version.VersionNumber, status = version.Status.ToString() }), time);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return await OriginalResponseAsync(receipt, authorizeLoaded, cancellationToken, request.SourceVersionNumber);
    }
    public async Task<Result<EmbeddedContentReceiptOutput>> GetEmbeddedContentOperationReceiptAsync(GetEmbeddedContentOperationReceiptRequest request,
        Func<EmbeddedContentOperation, CancellationToken, Task<bool>> authorizeLoaded, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var db = new CmsifyDbContext(options);
        if (!EmbeddedTemplateRepository.Supported(db)) return Failure<EmbeddedContentReceiptOutput>();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var receipt = await db.EmbeddedContentReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.WorkspaceId == request.WorkspaceId
            && x.ContentItemId == request.ContentItemId && x.Kind == request.Kind && x.OperationKey == request.OperationKey, cancellationToken);
        if (receipt is null) return Denied<EmbeddedContentReceiptOutput>("not-found", ResultErrorKind.NotFound);
        if (receipt.ContractFingerprint != request.ContractFingerprint)
            return Denied<EmbeddedContentReceiptOutput>("not-found", ResultErrorKind.NotFound);
        var loaded = await LoadAsync(db, request.WorkspaceId, request.ContentItemId, receipt.VersionNumber,
            receipt.TemplateVersionId, receipt.ContractFingerprint, EmbeddedContentOperationKind.ReceiptRead, authorizeLoaded, cancellationToken,
            validateFields: false);
        if (loaded.IsFailure) return Result<EmbeddedContentReceiptOutput>.Failure(loaded.Errors[0]);
        if (loaded.Value.Version.Id != receipt.ContentVersionId) return Denied<EmbeddedContentReceiptOutput>("not-found", ResultErrorKind.NotFound);
        return Result<EmbeddedContentReceiptOutput>.Success(ReceiptOutput(receipt));
    }
    private static Result<T> Failure<T>() => Result<T>.Failure(new(EmbeddedContentErrors.ProviderUnsupported, "Unsupported provider.", ResultErrorKind.Validation));

    private sealed record Loaded(ContentItem Item, ContentVersion Version, TemplateVersion Schema, EmbeddedTemplateRegistration Registration);
    private async Task<Result<Loaded>> LoadAsync(CmsifyDbContext db, Guid workspaceId, Guid itemId, int versionNumber,
        Guid templateVersionId, string fingerprint, EmbeddedContentOperationKind kind,
        Func<EmbeddedContentOperation, CancellationToken, Task<bool>> authorizeLoaded, CancellationToken cancellationToken,
        bool validateFields = true, int? authorizationSourceVersion = null)
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
        if (authorizationSourceVersion is { } sourceNumber && !await db.ContentVersions.AsNoTracking().AnyAsync(x =>
            x.ContentItemId == item.Id && x.WorkspaceId == item.WorkspaceId && x.TemplateVersionId == version.TemplateVersionId
            && x.VersionNumber == sourceNumber, cancellationToken))
            return Denied<Loaded>("not-found", ResultErrorKind.NotFound);
        if (!await authorizeLoaded(new(kind, item.WorkspaceId, registration.ContractKey, registration.Fingerprint,
            version.TemplateVersionId, item.Id, authorizationSourceVersion ?? version.VersionNumber), cancellationToken))
            return Denied<Loaded>("forbidden", ResultErrorKind.Forbidden);
        var actual = await EmbeddedTemplateRepository.ReadActualAsync(db, registration, fingerprint, cancellationToken);
        if (actual.IsFailure) return Result<Loaded>.Failure(actual.Errors[0]);
        var schema = await db.TemplateVersions.AsNoTracking().Include(x => x.Fields).ThenInclude(x => x.AllowedTypes)
            .SingleAsync(x => x.Id == version.TemplateVersionId, cancellationToken);
        if (validateFields && (!Enum.IsDefined(version.Status) || !validator.Validate(version, schema).IsValid || !ValidFields(version.FieldValues, schema)))
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
    internal static bool ValidFields(IEnumerable<ContentVersionFieldValue> fields, TemplateVersion schema)
    {
        var seen = new HashSet<Guid>();
        foreach (var value in fields)
        {
            if (!seen.Add(value.FieldId) || !schema.Fields.Any(f => f.Id == value.FieldId) || value.Order < 0
                || value.ValueKind != Cmsify.Core.Domain.Enums.ValueKind.Text || value.BoolValue is not null
                || value.MediaAssetId is not null || value.FileAssetId is not null || value.ChildContentItemId is not null
                || value.JsonValue is not null) return false;
            var field = schema.Fields.Single(f => f.Id == value.FieldId);
            if (value.TextValue is null || value.TextValue.Length > field.FieldConfig!.Value.GetProperty("maxLength").GetInt32()) return false;
        }
        return true;
    }
    private static Result<T> Denied<T>(string code, ResultErrorKind kind)
        => Result<T>.Failure(new(code, "Resource unavailable or invalid request.", kind));

    private static bool ValidRequest(Guid workspace, Guid item, Guid template, Guid operation, Guid actor, string fingerprint,
        IReadOnlyList<ContentVersionFieldInput> fields) => workspace != Guid.Empty && item != Guid.Empty && template != Guid.Empty
        && operation != Guid.Empty && actor != Guid.Empty && EmbeddedContractRules.IsFingerprint(fingerprint)
        && fields is not null && fields.All(f => f is not null && f.FieldId != Guid.Empty && f.Order >= 0
            && f.ValueKind == Cmsify.Core.Domain.Enums.ValueKind.Text && f.BoolValue is null && f.MediaAssetId is null
            && f.FileAssetId is null && f.ChildContentItemId is null && f.JsonValue is null)
        && fields.Select(f => f.FieldId).Distinct().Count() == fields.Count;
    private static EmbeddedContentReceipt NewReceipt(Guid workspace, EmbeddedWriteKind kind, Guid operation, ContentVersion version,
        string fingerprint, string hash, Guid actor, DateTimeOffset time) => new() { WorkspaceId = workspace, Kind = kind,
            OperationKey = operation, ContentItemId = version.ContentItemId, ContentVersionId = version.Id,
            VersionNumber = version.VersionNumber, TemplateVersionId = version.TemplateVersionId, ContractFingerprint = fingerprint,
            InputFingerprint = hash, ActorSubject = actor, CommittedRevision = ContentVersionRevisionCondition.Normalize(version.UpdatedAt),
            CommittedAt = new DateTimeOffset(time.UtcTicks / 10 * 10, TimeSpan.Zero) };
    private static EmbeddedContentReceiptOutput ReceiptOutput(EmbeddedContentReceipt receipt) => new(receipt.WorkspaceId,
        receipt.ContentItemId, receipt.Kind, receipt.OperationKey, receipt.VersionNumber, receipt.CommittedRevision,
        receipt.ActorSubject, receipt.CommittedAt);
    private async Task<Result<EmbeddedContentWriteOutput>> ReplayAsync(CmsifyDbContext db, EmbeddedContentReceipt receipt,
        Guid requestedItem, string hash, Func<EmbeddedContentOperation, CancellationToken, Task<bool>> authorizeLoaded,
        CancellationToken cancellationToken, int? sourceVersionNumber = null)
    {
        // Authorization precedes conflict metadata: a receipt never grants resource access.
        var loaded = await LoadAsync(db, receipt.WorkspaceId, requestedItem, receipt.VersionNumber,
            receipt.TemplateVersionId, receipt.ContractFingerprint, receipt.Kind == EmbeddedWriteKind.ItemCreate
                ? EmbeddedContentOperationKind.ItemCreate : EmbeddedContentOperationKind.VersionCreate, authorizeLoaded, cancellationToken,
            authorizationSourceVersion: sourceVersionNumber);
        if (loaded.IsFailure) return Result<EmbeddedContentWriteOutput>.Failure(loaded.Errors[0]);
        if (receipt.ContentItemId != requestedItem || receipt.InputFingerprint != hash)
            return Denied<EmbeddedContentWriteOutput>(EmbeddedContentErrors.OperationConflict, ResultErrorKind.Conflict);
        if (loaded.Value.Version.Id != receipt.ContentVersionId
            || ContentVersionRevisionCondition.Normalize(loaded.Value.Version.UpdatedAt) != receipt.CommittedRevision)
            return Denied<EmbeddedContentWriteOutput>(EmbeddedContentErrors.RefreshRequired, ResultErrorKind.Conflict);
        var projected = await ProjectAsync(db, loaded.Value.Version, receipt.ContractFingerprint, cancellationToken);
        if (projected.IsFailure) return Result<EmbeddedContentWriteOutput>.Failure(projected.Errors[0]);
        if (projected.Value.Revision != receipt.CommittedRevision)
            return Denied<EmbeddedContentWriteOutput>(EmbeddedContentErrors.RefreshRequired, ResultErrorKind.Conflict);
        return Result<EmbeddedContentWriteOutput>.Success(new(ReceiptOutput(receipt), projected.Value));
    }
    private async Task<Result<EmbeddedContentWriteOutput>> OriginalResponseAsync(EmbeddedContentReceipt receipt,
        Func<EmbeddedContentOperation, CancellationToken, Task<bool>> authorizeLoaded, CancellationToken cancellationToken,
        int? sourceVersionNumber = null)
        => await ReadResponseAsync(receipt, receipt.ContentItemId, receipt.InputFingerprint, authorizeLoaded, cancellationToken, sourceVersionNumber);

    private async Task<Result<EmbeddedContentWriteOutput>> ReadResponseAsync(EmbeddedContentReceipt receipt, Guid requestedItem, string hash,
        Func<EmbeddedContentOperation, CancellationToken, Task<bool>> authorizeLoaded, CancellationToken cancellationToken,
        int? sourceVersionNumber = null)
    {
        await using var db = new CmsifyDbContext(options);
        await using var read = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        return await ReplayAsync(db, receipt, requestedItem, hash, authorizeLoaded, cancellationToken, sourceVersionNumber);
    }
}
