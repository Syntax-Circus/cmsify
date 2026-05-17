using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Repositories;
using Cmsify.Core.Interfaces.Services;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Persistence.Repositories;

public sealed class TemplateVersionRepository : ITemplateVersionRepository
{
    private readonly CmsifyDbContext dbContext;
    private readonly ICurrentActor currentActor;

    public TemplateVersionRepository(CmsifyDbContext dbContext, ICurrentActor currentActor)
    {
        this.dbContext = dbContext;
        this.currentActor = currentActor;
    }

    public async Task<TemplateVersionDto?> GetAsync(Guid id, CancellationToken ct = default) =>
        (await Scope(dbContext.TemplateVersions.AsNoTracking()).FirstOrDefaultAsync(version => version.Id == id, ct))?.ToDto();

    public async Task<IReadOnlyList<TemplateVersionDto>> ListByTemplateAsync(Guid templateId, CancellationToken ct = default)
    {
        var versions = await Scope(dbContext.TemplateVersions.AsNoTracking())
            .Where(version => version.TemplateId == templateId)
            .OrderByDescending(version => version.VersionNumber)
            .ToListAsync(ct);
        return versions.Select(version => version.ToDto()).ToArray();
    }

    public async Task<TemplateVersionDto> CreateDraftAsync(CreateTemplateVersionCommand command, CancellationToken ct = default)
    {
        var nextVersion = await Scope(dbContext.TemplateVersions)
            .Where(version => version.TemplateId == command.TemplateId)
            .Select(version => (int?)version.VersionNumber)
            .MaxAsync(ct) ?? 0;

        var entity = new TemplateVersion
        {
            TemplateId = command.TemplateId,
            VersionNumber = nextVersion + 1,
            Status = TemplateVersionStatus.Draft,
            Notes = command.Notes
        };
        dbContext.TemplateVersions.Add(entity);
        await dbContext.SaveChangesAsync(ct);
        return entity.ToDto();
    }

    public async Task SaveStructureAsync(SaveTemplateVersionStructureCommand command, CancellationToken ct = default)
    {
        var version = await Scope(dbContext.TemplateVersions)
            .Include(v => v.Sections)
            .Include(v => v.Fields)
            .ThenInclude(field => field.AllowedTypes)
            .FirstAsync(v => v.Id == command.TemplateVersionId, ct);

        if (version.Status != TemplateVersionStatus.Draft)
        {
            throw new InvalidOperationException("Only draft template versions can be edited.");
        }

        dbContext.TemplateFieldAllowedTypes.RemoveRange(version.Fields.SelectMany(field => field.AllowedTypes));
        dbContext.TemplateFields.RemoveRange(version.Fields);
        dbContext.TemplateSections.RemoveRange(version.Sections);

        var sectionIdsByOrder = new Dictionary<int, Guid>();
        foreach (var input in command.Sections)
        {
            var section = new TemplateSection
            {
                TemplateVersionId = version.Id,
                Name = input.Name,
                Description = input.Description,
                Order = input.Order,
                IsCollapsible = input.IsCollapsible
            };
            sectionIdsByOrder[input.Order] = section.Id;
            dbContext.TemplateSections.Add(section);
        }

        foreach (var input in command.Fields)
        {
            var field = new TemplateField
            {
                TemplateVersionId = version.Id,
                SectionId = input.SectionId,
                Key = input.Key,
                Label = input.Label,
                HelpText = input.HelpText,
                Order = input.Order,
                IsRequired = input.IsRequired,
                MinOccurrences = input.MinOccurrences,
                MaxOccurrences = input.MaxOccurrences,
                IsOpen = input.IsOpen,
                CompositionMode = input.CompositionMode,
                PrimitiveType = input.PrimitiveType,
                TemplateId = input.TemplateId,
                FieldConfig = input.FieldConfig
            };

            foreach (var allowedType in input.AllowedTypes)
            {
                field.AllowedTypes.Add(new TemplateFieldAllowedType
                {
                    FieldId = field.Id,
                    PrimitiveType = allowedType.PrimitiveType,
                    AllowedTemplateId = allowedType.AllowedTemplateId
                });
            }

            dbContext.TemplateFields.Add(field);
        }

        await dbContext.SaveChangesAsync(ct);
    }

    public async Task<TemplateVersionDto> PublishAsync(Guid id, Guid actorUserId, CancellationToken ct = default)
    {
        var entity = await Scope(dbContext.TemplateVersions).FirstAsync(version => version.Id == id, ct);
        if (entity.Status != TemplateVersionStatus.Draft)
        {
            throw new InvalidOperationException("Only draft template versions can be published.");
        }

        await dbContext.TemplateVersions
            .Where(version => version.TemplateId == entity.TemplateId && version.Status == TemplateVersionStatus.Published)
            .ExecuteUpdateAsync(updates => updates.SetProperty(version => version.Status, TemplateVersionStatus.Archived), ct);

        entity.Status = TemplateVersionStatus.Published;
        entity.PublishedAt = DateTimeOffset.UtcNow;

        var template = await dbContext.Templates.FirstAsync(template => template.Id == entity.TemplateId, ct);
        template.CurrentVersionId = entity.Id;
        template.UpdatedAt = DateTimeOffset.UtcNow;

        await dbContext.SaveChangesAsync(ct);
        return entity.ToDto();
    }

    private IQueryable<TemplateVersion> Scope(IQueryable<TemplateVersion> query) =>
        currentActor.WorkspaceId.HasValue
            ? query.Where(version => dbContext.Templates.Any(template => template.Id == version.TemplateId && template.WorkspaceId == currentActor.WorkspaceId.Value))
            : query;
}
