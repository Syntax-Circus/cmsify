using Cmsify.Core.EmbeddedContent;
using Microsoft.EntityFrameworkCore;
using SyntaxCircus.Common;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using System.Data;

namespace Cmsify.Infrastructure.Persistence.EmbeddedContent;

public sealed class EmbeddedTemplateRepository(DbContextOptions<CmsifyDbContext> options) : IEmbeddedTemplateRepository
{
    public async Task<Result<EmbeddedTemplateOutput>> EnsureAsync(EnsureEmbeddedTemplateRequest request, Guid actorSubject,
        Func<EmbeddedTemplateContractScope, CancellationToken, Task<bool>> authorizeSetup, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var db = new CmsifyDbContext(options);
        if (!Supported(db)) return Failure();
        var validated = EmbeddedContractRules.ValidateAndCopy(request.Contract);
        if (validated.IsFailure || request.WorkspaceId == Guid.Empty || actorSubject == Guid.Empty)
            return Denied(EmbeddedContentErrors.Validation, ResultErrorKind.Validation);
        var contract = validated.Value;
        var fingerprint = EmbeddedContractRules.Fingerprint(contract);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await EmbeddedOperationLocks.LockAsync(db, $"template:{request.WorkspaceId:N}:{contract.ContractKey}", cancellationToken);
        if (!await authorizeSetup(new(request.WorkspaceId, contract.ContractKey, fingerprint), cancellationToken))
            return Denied("forbidden", ResultErrorKind.Forbidden);
        if (!await db.Workspaces.AnyAsync(x => x.Id == request.WorkspaceId && !x.IsDeleted, cancellationToken))
            return Denied("not-found", ResultErrorKind.NotFound);
        var existing = await db.EmbeddedTemplateRegistrations.SingleOrDefaultAsync(x => x.WorkspaceId == request.WorkspaceId
            && x.ContractKey == contract.ContractKey, cancellationToken);
        if (existing is not null) return await ReadActualAsync(db, existing, fingerprint, cancellationToken);
        if (await db.Templates.AnyAsync(x => x.WorkspaceId == request.WorkspaceId && x.Slug == contract.Slug, cancellationToken))
            return Denied(EmbeddedContentErrors.TemplateMismatch, ResultErrorKind.Conflict);
        var template = new Template { WorkspaceId = request.WorkspaceId, Name = contract.Name,
            Slug = contract.Slug, TitleFieldKey = contract.TitleFieldKey };
        var version = new TemplateVersion { TemplateId = template.Id, VersionNumber = 1,
            Status = TemplateVersionStatus.Published, PublishedAt = DateTimeOffset.UtcNow, CreatedByUserId = actorSubject };
        foreach (var field in contract.Fields)
            version.Fields.Add(new TemplateField { TemplateVersionId = version.Id, Key = field.Key, Label = field.Label,
                Order = field.Order, IsRequired = field.IsRequired, MinOccurrences = field.MinOccurrences,
                MaxOccurrences = field.MaxOccurrences, PrimitiveType = field.PrimitiveType,
                CompositionMode = field.CompositionMode, IsOpen = field.IsOpen, FieldConfig = field.FieldConfig.Clone() });
        var registration = new EmbeddedTemplateRegistration { WorkspaceId = request.WorkspaceId,
            ContractKey = contract.ContractKey, Fingerprint = fingerprint, TemplateId = template.Id, TemplateVersionId = version.Id };
        db.AddRange(template, version, registration);
        await db.SaveChangesAsync(cancellationToken);
        template.CurrentVersionId = version.Id;
        new EfWebhookOutbox(db).Enqueue("template.version_published", request.WorkspaceId, version.Id,
            System.Text.Json.JsonSerializer.SerializeToElement(new { templateId = template.Id, templateVersionId = version.Id,
                versionNumber = version.VersionNumber, workspaceId = request.WorkspaceId }), DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Output(registration, version);
    }
    public async Task<Result<EmbeddedTemplateOutput>> GetAsync(GetEmbeddedTemplateRequest request,
        Func<EmbeddedContentOperation, CancellationToken, Task<bool>> authorizeLoaded, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var db = new CmsifyDbContext(options);
        if (!Supported(db)) return Failure();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var registration = await db.EmbeddedTemplateRegistrations.SingleOrDefaultAsync(x => x.WorkspaceId == request.WorkspaceId
            && x.ContractKey == request.ContractKey, cancellationToken);
        if (registration is null) return Denied("not-found", ResultErrorKind.NotFound);
        if (!await authorizeLoaded(new(EmbeddedContentOperationKind.TemplateRead, registration.WorkspaceId,
            registration.ContractKey, registration.Fingerprint, registration.TemplateVersionId), cancellationToken))
            return Denied("forbidden", ResultErrorKind.Forbidden);
        return await ReadActualAsync(db, registration, request.ExpectedFingerprint, cancellationToken);
    }

    internal static bool Supported(CmsifyDbContext db) => db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL";
    internal static async Task<Result<EmbeddedTemplateOutput>> ReadActualAsync(CmsifyDbContext db,
        EmbeddedTemplateRegistration registration, string expectedFingerprint, CancellationToken cancellationToken)
    {
        var template = await db.Templates.AsNoTracking().SingleOrDefaultAsync(x => x.Id == registration.TemplateId, cancellationToken);
        var version = await db.TemplateVersions.AsNoTracking().Include(x => x.Fields).ThenInclude(x => x.AllowedTypes)
            .Include(x => x.Sections).SingleOrDefaultAsync(x => x.Id == registration.TemplateVersionId, cancellationToken);
        if (template is null || template.IsDeleted || template.WorkspaceId != registration.WorkspaceId
            || template.CurrentVersionId != registration.TemplateVersionId
            || version is null || version.IsDeleted || version.TemplateId != template.Id || version.Status != TemplateVersionStatus.Published
            || version.Sections.Count != 0 || version.Fields.Any(f => f.AllowedTypes.Count != 0 || f.SectionId is not null
                || f.TemplateId is not null || f.ComponentId is not null || f.PrimitiveType is null || f.FieldConfig is null || f.HelpText is not null)
            || registration.Fingerprint != expectedFingerprint)
            return Denied(EmbeddedContentErrors.TemplateMismatch, ResultErrorKind.Conflict);
        var actual = new EmbeddedTemplateContract(registration.ContractKey, template.Name, template.Slug, template.TitleFieldKey ?? "",
            version.Fields.Select(Schema).ToArray());
        if (EmbeddedContractRules.ValidateAndCopy(actual).IsFailure || EmbeddedContractRules.Fingerprint(actual) != registration.Fingerprint)
            return Denied(EmbeddedContentErrors.TemplateMismatch, ResultErrorKind.Conflict);
        return Output(registration, version);
    }
    internal static EmbeddedTemplateField Schema(TemplateField field) => new(field.Key, field.Label, field.Order,
        field.IsRequired, field.MinOccurrences, field.MaxOccurrences ?? 0, field.PrimitiveType ?? PrimitiveType.Text,
        ValueKind.Text, field.CompositionMode, field.IsOpen, field.FieldConfig!.Value.Clone());
    private static Result<EmbeddedTemplateOutput> Output(EmbeddedTemplateRegistration registration, TemplateVersion version)
        => Result<EmbeddedTemplateOutput>.Success(new(registration.WorkspaceId, registration.ContractKey, registration.Fingerprint,
            registration.TemplateId, registration.TemplateVersionId, version.VersionNumber,
            Array.AsReadOnly(version.Fields.OrderBy(f => f.Order).ThenBy(f => f.Key, StringComparer.Ordinal)
                .Select(f => new EmbeddedTemplateFieldOutput(f.Id, Schema(f))).ToArray())));
    private static Result<EmbeddedTemplateOutput> Denied(string code, ResultErrorKind kind)
        => Result<EmbeddedTemplateOutput>.Failure(new(code, "Resource unavailable or permission denied.", kind));
    private static Result<EmbeddedTemplateOutput> Failure() => Result<EmbeddedTemplateOutput>.Failure(
        new(EmbeddedContentErrors.ProviderUnsupported, "Unsupported provider.", ResultErrorKind.Validation));
}
