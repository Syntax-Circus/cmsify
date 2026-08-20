using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cmsify.Api.Auth;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Api.Controllers;

[ApiController]
public sealed class PackagesController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true
    };

    private readonly CmsifyDbContext dbContext;
    private readonly IWebHostEnvironment environment;
    private readonly IWorkspaceAuthorizationService workspaceAuthorization;

    public PackagesController(CmsifyDbContext dbContext, IWebHostEnvironment environment, IWorkspaceAuthorizationService workspaceAuthorization)
    {
        this.dbContext = dbContext;
        this.environment = environment;
        this.workspaceAuthorization = workspaceAuthorization;
    }

    [HttpGet("/schema/ctp-1.0.json")]
    public IActionResult Schema() => Ok(CtpSchema.Build());

    [HttpGet("/api/v1/packages/official")]
    [RequireRole(UserRole.Reader)]
    public async Task<ActionResult<IReadOnlyList<OfficialPackageResponse>>> Official(CancellationToken ct)
    {
        var packages = new List<OfficialPackageResponse>();
        foreach (var manifest in await LoadOfficialPackagesAsync(ct))
        {
            packages.Add(new OfficialPackageResponse(
                manifest.PackageNamespace,
                manifest.Id,
                manifest.Version,
                manifest.Name,
                manifest.Description,
                manifest.Author,
                manifest.License,
                manifest.Homepage,
                manifest.Templates.Count,
                manifest.Templates.Select(template => new OfficialPackageTemplateResponse(template.Slug, template.Name, template.Description)).ToArray()));
        }

        return Ok(packages.OrderBy(package => package.Name).ToArray());
    }

    [HttpPost("/api/v1/workspaces/{workspaceId:guid}/packages/import")]
    [RequireRole(UserRole.TemplateAdmin)]
    [Consumes("application/json", "multipart/form-data")]
    public async Task<ActionResult<PackageImportResponse>> Import(Guid workspaceId, CancellationToken ct)
    {
        if (!await workspaceAuthorization.CanWriteWorkspaceAsync(workspaceId, ct))
        {
            return NotFound();
        }

        var (manifest, resolutions) = await ReadManifestAndResolutionsAsync(ct);
        return await ImportManifestAsync(workspaceId, manifest, resolutions, ct);
    }

    [HttpPost("/api/v1/workspaces/{workspaceId:guid}/packages/import/preview")]
    [RequireRole(UserRole.TemplateAdmin)]
    [Consumes("application/json", "multipart/form-data")]
    public async Task<ActionResult<PackageImportPreviewResponse>> ImportPreview(Guid workspaceId, CancellationToken ct)
    {
        if (!await workspaceAuthorization.CanWriteWorkspaceAsync(workspaceId, ct))
        {
            return NotFound();
        }

        var (manifest, _) = await ReadManifestAndResolutionsAsync(ct);
        return await BuildPreviewAsync(workspaceId, manifest, ct);
    }

    [HttpPost("/api/v1/workspaces/{workspaceId:guid}/packages/import/official/{packageId}/preview")]
    [RequireRole(UserRole.TemplateAdmin)]
    public async Task<ActionResult<PackageImportPreviewResponse>> ImportOfficialPreview(Guid workspaceId, string packageId, CancellationToken ct)
    {
        if (!await workspaceAuthorization.CanWriteWorkspaceAsync(workspaceId, ct))
        {
            return NotFound();
        }

        var manifest = (await LoadOfficialPackagesAsync(ct)).FirstOrDefault(package => string.Equals(package.Id, packageId, StringComparison.OrdinalIgnoreCase));
        if (manifest is null)
        {
            return this.Error(StatusCodes.Status404NotFound, CmsifyError.NotFound, "Official package not found");
        }

        return await BuildPreviewAsync(workspaceId, manifest, ct);
    }

    [HttpPost("/api/v1/workspaces/{workspaceId:guid}/packages/import/official/{packageId}")]
    [RequireRole(UserRole.TemplateAdmin)]
    public async Task<ActionResult<PackageImportResponse>> ImportOfficial(Guid workspaceId, string packageId, [FromBody] PackageImportResolutions? resolutions, CancellationToken ct)
    {
        if (!await workspaceAuthorization.CanWriteWorkspaceAsync(workspaceId, ct))
        {
            return NotFound();
        }

        var manifest = (await LoadOfficialPackagesAsync(ct)).FirstOrDefault(package => string.Equals(package.Id, packageId, StringComparison.OrdinalIgnoreCase));
        if (manifest is null)
        {
            return this.Error(StatusCodes.Status404NotFound, CmsifyError.NotFound, "Official package not found");
        }

        return await ImportManifestAsync(workspaceId, manifest, resolutions, ct);
    }

    private async Task<ActionResult<PackageImportResponse>> ImportManifestAsync(Guid workspaceId, CtpPackageManifest manifest, PackageImportResolutions? resolutions, CancellationToken ct)
    {
        var validationErrors = ValidateManifest(manifest);
        if (validationErrors.Count > 0)
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, CmsifyError.ValidationFailed, "Package manifest is invalid", extensions: new Dictionary<string, object?> { ["errors"] = validationErrors });
        }

        var existingPackageVersions = await dbContext.Templates.AsNoTracking()
            .Where(template => template.WorkspaceId == workspaceId
                && template.PackageNamespace == manifest.PackageNamespace
                && template.PackageId == manifest.Id
                && template.PackageVersion != null)
            .Select(template => template.PackageVersion!)
            .Distinct()
            .ToListAsync(ct);

        if (existingPackageVersions.Any(version => CompareVersions(version, manifest.Version) >= 0))
        {
            return this.Error(
                StatusCodes.Status409Conflict,
                CmsifyError.Conflict,
                "Package version already installed",
                $"Package {manifest.PackageNamespace}/{manifest.Id}@{manifest.Version} is already installed or older than the installed version.",
                new Dictionary<string, object?> { ["installedVersions"] = existingPackageVersions });
        }

        var existingPickLists = await dbContext.PickLists
            .Include(picklist => picklist.Options)
            .Where(picklist => picklist.WorkspaceId == workspaceId && !picklist.IsDeleted)
            .ToListAsync(ct);
        var existingBySlug = existingPickLists.ToDictionary(picklist => picklist.Slug, picklist => picklist, StringComparer.OrdinalIgnoreCase);
        var existingSlugs = new HashSet<string>(existingPickLists.Select(picklist => picklist.Slug), StringComparer.OrdinalIgnoreCase);

        var picklistResolutions = resolutions?.PickLists ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var picklistIdBySlug = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var picklistRevisionIdBySlug = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var revisionsToMakeCurrent = new List<(PickList PickList, PickListRevision Revision)>();
        var importedPickLists = new List<PackagePickListImportResult>();
        var unresolvedConflicts = new List<string>();

        foreach (var packagePickList in (manifest.PickLists ?? []))
        {
            if (!existingBySlug.TryGetValue(packagePickList.Slug, out var existing))
            {
                var picklist = CreatePickListFromPackage(workspaceId, packagePickList.Slug, packagePickList.Name, packagePickList.Description, packagePickList.Options);
                var revision = AddRevision(picklist, packagePickList.Options, 1);
                dbContext.PickLists.Add(picklist);
                revisionsToMakeCurrent.Add((picklist, revision));
                existingSlugs.Add(picklist.Slug);
                picklistIdBySlug[packagePickList.Slug] = picklist.Id;
                picklistRevisionIdBySlug[packagePickList.Slug] = revision.Id;
                importedPickLists.Add(new PackagePickListImportResult(packagePickList.Slug, picklist.Slug, picklist.Id, "imported"));
                continue;
            }

            var action = picklistResolutions.TryGetValue(packagePickList.Slug, out var resolvedAction) ? resolvedAction : null;
            if (action is null)
            {
                if (PickListsMatch(existing, packagePickList))
                {
                    action = PackagePickListResolution.UseExisting;
                }
                else
                {
                    unresolvedConflicts.Add(packagePickList.Slug);
                    continue;
                }
            }

            switch (action)
            {
                case PackagePickListResolution.UseExisting:
                    picklistIdBySlug[packagePickList.Slug] = existing.Id;
                    if (existing.CurrentRevisionId.HasValue)
                    {
                        picklistRevisionIdBySlug[packagePickList.Slug] = existing.CurrentRevisionId.Value;
                    }
                    importedPickLists.Add(new PackagePickListImportResult(packagePickList.Slug, existing.Slug, existing.Id, "useExisting"));
                    break;
                case PackagePickListResolution.Replace:
                    existing.Name = packagePickList.Name;
                    existing.Description = packagePickList.Description;
                    existing.UpdatedAt = DateTimeOffset.UtcNow;
                    dbContext.PickListOptions.RemoveRange(existing.Options);
                    existing.Options.Clear();
                    foreach (var option in OrderedOptionsForImport(packagePickList.Options))
                    {
                        var entity = new PickListOption
                        {
                            PickListId = existing.Id,
                            Label = option.Label,
                            Value = option.Value,
                            Order = option.Order
                        };
                        existing.Options.Add(entity);
                        dbContext.PickListOptions.Add(entity);
                    }

                    var nextRevision = await dbContext.PickListRevisions.Where(revision => revision.PickListId == existing.Id)
                        .Select(revision => (int?)revision.VersionNumber).MaxAsync(ct) ?? 0;
                    var replacementRevision = AddRevision(existing, packagePickList.Options, nextRevision + 1);
                    revisionsToMakeCurrent.Add((existing, replacementRevision));

                    picklistIdBySlug[packagePickList.Slug] = existing.Id;
                    picklistRevisionIdBySlug[packagePickList.Slug] = replacementRevision.Id;
                    importedPickLists.Add(new PackagePickListImportResult(packagePickList.Slug, existing.Slug, existing.Id, "replaced"));
                    break;
                case PackagePickListResolution.ImportAsNew:
                    var newSlug = NextAvailableSlug(packagePickList.Slug, existingSlugs);
                    var picklist = CreatePickListFromPackage(workspaceId, newSlug, packagePickList.Name, packagePickList.Description, packagePickList.Options);
                    var revision = AddRevision(picklist, packagePickList.Options, 1);
                    dbContext.PickLists.Add(picklist);
                    revisionsToMakeCurrent.Add((picklist, revision));
                    existingSlugs.Add(newSlug);
                    picklistIdBySlug[packagePickList.Slug] = picklist.Id;
                    picklistRevisionIdBySlug[packagePickList.Slug] = revision.Id;
                    importedPickLists.Add(new PackagePickListImportResult(packagePickList.Slug, newSlug, picklist.Id, "importedAsNew"));
                    break;
                default:
                    return this.Error(StatusCodes.Status400BadRequest, CmsifyError.BadRequest, "Invalid picklist resolution",
                        $"Resolution '{action}' is not supported for picklist '{packagePickList.Slug}'.");
            }
        }

        if (unresolvedConflicts.Count > 0)
        {
            var preview = await BuildPreviewPayloadAsync(workspaceId, manifest, ct);
            return this.Error(
                StatusCodes.Status409Conflict,
                CmsifyError.Conflict,
                "Package import has unresolved conflicts",
                "Provide resolutions for the listed picklists and try again.",
                new Dictionary<string, object?>
                {
                    ["unresolvedPicklists"] = unresolvedConflicts,
                    ["preview"] = preview
                });
        }

        // The current-revision foreign key points back to rows created above, so it
        // must be applied after the lists and immutable revisions have been saved.
        await dbContext.SaveChangesAsync(ct);
        foreach (var (picklist, revision) in revisionsToMakeCurrent)
        {
            picklist.CurrentRevisionId = revision.Id;
        }

        var sortedTemplates = TopologicalSort(manifest);
        var templatesBySlug = await dbContext.Templates
            .Where(template => template.WorkspaceId == workspaceId && !template.IsDeleted)
            .ToDictionaryAsync(template => template.Slug, template => template, StringComparer.OrdinalIgnoreCase, ct);

        var imported = new List<PackageTemplateImportResult>();
        var importedVersions = new List<(Template Template, TemplateVersion Version)>();
        foreach (var packageTemplate in sortedTemplates)
        {
            if (!templatesBySlug.TryGetValue(packageTemplate.Slug, out var template))
            {
                template = new Template
                {
                    WorkspaceId = workspaceId,
                    Slug = packageTemplate.Slug,
                    Name = packageTemplate.Name,
                    Description = packageTemplate.Description,
                    PackageNamespace = manifest.PackageNamespace,
                    PackageId = manifest.Id,
                    PackageVersion = manifest.Version
                };
                dbContext.Templates.Add(template);
                templatesBySlug[packageTemplate.Slug] = template;
            }
            else
            {
                template.Name = packageTemplate.Name;
                template.Description = packageTemplate.Description;
                template.PackageNamespace = manifest.PackageNamespace;
                template.PackageId = manifest.Id;
                template.PackageVersion = manifest.Version;
                template.UpdatedAt = DateTimeOffset.UtcNow;
            }

            var nextVersionNumber = await dbContext.TemplateVersions
                .Where(version => version.TemplateId == template.Id)
                .Select(version => (int?)version.VersionNumber)
                .MaxAsync(ct) ?? 0;
            var version = new TemplateVersion
            {
                TemplateId = template.Id,
                VersionNumber = nextVersionNumber + 1,
                Status = TemplateVersionStatus.Published,
                PublishedAt = DateTimeOffset.UtcNow,
                Notes = $"Imported from {manifest.PackageNamespace}/{manifest.Id}@{manifest.Version}"
            };

            AddStructure(version, packageTemplate, templatesBySlug, picklistIdBySlug, picklistRevisionIdBySlug);
            await dbContext.TemplateVersions
                .Where(candidate => candidate.TemplateId == template.Id && candidate.Status == TemplateVersionStatus.Published)
                .ExecuteUpdateAsync(updates => updates.SetProperty(candidate => candidate.Status, TemplateVersionStatus.Archived), ct);

            dbContext.TemplateVersions.Add(version);
            template.UpdatedAt = DateTimeOffset.UtcNow;
            importedVersions.Add((template, version));
            imported.Add(new PackageTemplateImportResult(template.Id, template.Slug, template.Name, version.Id, version.VersionNumber));
        }

        await dbContext.SaveChangesAsync(ct);
        foreach (var (template, version) in importedVersions)
        {
            template.CurrentVersionId = version.Id;
            template.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await dbContext.SaveChangesAsync(ct);
        return Ok(new PackageImportResponse(manifest.PackageNamespace, manifest.Id, manifest.Version, imported, [], [], importedPickLists));
    }

    [HttpGet("/api/v1/workspaces/{workspaceId:guid}/packages/export")]
    [RequireRole(UserRole.TemplateAdmin)]
    public async Task<IActionResult> Export(Guid workspaceId, [FromQuery] string templateIds, [FromQuery] string packageNamespace = "custom", [FromQuery] string id = "export", [FromQuery] string version = "1.0.0", CancellationToken ct = default)
    {
        if (!await workspaceAuthorization.CanWriteWorkspaceAsync(workspaceId, ct))
        {
            return NotFound();
        }

        var selectedIds = templateIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => Guid.TryParse(value, out var parsed) ? parsed : Guid.Empty)
            .Where(value => value != Guid.Empty)
            .ToArray();
        if (selectedIds.Length == 0)
        {
            return this.Error(StatusCodes.Status400BadRequest, CmsifyError.BadRequest, "No templates selected", "Provide one or more template IDs in the templateIds query parameter.");
        }

        var templates = await ResolveTemplatesAsync(workspaceId, selectedIds, ct);

        var pickListIds = templates
            .SelectMany(version => version.Fields)
            .Select(field => ExtractPickListId(field.PrimitiveType, field.FieldConfig))
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .Distinct()
            .ToArray();
        var revisionIdByPickListId = templates
            .SelectMany(version => version.Fields)
            .Where(field => field.PrimitiveType == PrimitiveType.PickList)
            .Select(field => (PickListId: ExtractPickListId(field.PrimitiveType, field.FieldConfig), RevisionId: ExtractPickListRevisionId(field.FieldConfig)))
            .Where(binding => binding.PickListId.HasValue && binding.RevisionId.HasValue)
            .GroupBy(binding => binding.PickListId!.Value)
            .ToDictionary(group => group.Key, group => group.First().RevisionId!.Value);
        var pickLists = pickListIds.Length == 0
            ? []
            : await dbContext.PickLists.AsNoTracking()
                .Include(picklist => picklist.Options)
                .Where(picklist => picklist.WorkspaceId == workspaceId && !picklist.IsDeleted && pickListIds.Contains(picklist.Id))
                .ToListAsync(ct);
        var picklistSlugById = pickLists.ToDictionary(picklist => picklist.Id, picklist => picklist.Slug);
        var revisions = revisionIdByPickListId.Count == 0
            ? []
            : await dbContext.PickListRevisions.AsNoTracking().Include(revision => revision.Options)
                .Where(revision => revisionIdByPickListId.Values.Contains(revision.Id))
                .ToListAsync(ct);
        var revisionOptionsById = revisions.ToDictionary(revision => revision.Id, revision => revision.Options);
        var packagePickLists = pickLists.OrderBy(picklist => picklist.Slug).Select(picklist =>
        {
            var options = revisionIdByPickListId.TryGetValue(picklist.Id, out var revisionId)
                && revisionOptionsById.TryGetValue(revisionId, out var revisionOptions)
                    ? revisionOptions.OrderBy(option => option.Order).Select(option => new CtpPickListOption(option.Label, option.Value, option.Order)).ToArray()
                    : picklist.Options.OrderBy(option => option.Order).Select(option => new CtpPickListOption(option.Label, option.Value, option.Order)).ToArray();
            return new CtpPickList(picklist.Slug, picklist.Name, picklist.Description, options);
        }).ToArray();

        var manifest = new CtpPackageManifest(
            "1.0",
            packageNamespace,
            id,
            version,
            "Cmsify export",
            "Exported from Cmsify.",
            null,
            null,
            null,
            templates.Select(templateVersion => ToPackageTemplate(templateVersion, picklistSlugById)).OrderBy(template => template.Name).ToArray(),
            packagePickLists);

        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        return File(Encoding.UTF8.GetBytes(json), "application/json", $"{packageNamespace}.{id}@{version}.ctp");
    }

    private async Task<(CtpPackageManifest Manifest, PackageImportResolutions? Resolutions)> ReadManifestAndResolutionsAsync(CancellationToken ct)
    {
        if (Request.HasFormContentType)
        {
            var form = await Request.ReadFormAsync(ct);
            var file = form.Files.FirstOrDefault();
            if (file is null)
            {
                throw new ArgumentException("Multipart import requires a .ctp file.");
            }

            CtpPackageManifest manifest;
            await using (var stream = file.OpenReadStream())
            {
                manifest = await JsonSerializer.DeserializeAsync<CtpPackageManifest>(stream, JsonOptions, ct)
                    ?? throw new ArgumentException("Package manifest is empty.");
            }

            PackageImportResolutions? resolutions = null;
            if (form.TryGetValue("resolutions", out var resolutionsValue) && !string.IsNullOrWhiteSpace(resolutionsValue))
            {
                resolutions = JsonSerializer.Deserialize<PackageImportResolutions>(resolutionsValue.ToString(), JsonOptions);
            }

            return (manifest, resolutions);
        }

        using var document = await JsonDocument.ParseAsync(Request.Body, cancellationToken: ct);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("manifest", out var manifestElement))
        {
            var manifest = manifestElement.Deserialize<CtpPackageManifest>(JsonOptions)
                ?? throw new ArgumentException("Package manifest is empty.");
            PackageImportResolutions? resolutions = null;
            if (root.TryGetProperty("resolutions", out var resolutionsElement) && resolutionsElement.ValueKind != JsonValueKind.Null)
            {
                resolutions = resolutionsElement.Deserialize<PackageImportResolutions>(JsonOptions);
            }

            return (manifest, resolutions);
        }

        var bare = root.Deserialize<CtpPackageManifest>(JsonOptions)
            ?? throw new ArgumentException("Package manifest is empty.");
        return (bare, null);
    }

    private static IReadOnlyList<string> ValidateManifest(CtpPackageManifest manifest)
    {
        var errors = new List<string>();
        if (manifest.CmsifyPackage != "1.0")
        {
            errors.Add("cmsifyPackage must be '1.0'.");
        }

        if (string.IsNullOrWhiteSpace(manifest.PackageNamespace))
        {
            errors.Add("namespace is required.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Id))
        {
            errors.Add("id is required.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Version))
        {
            errors.Add("version is required.");
        }

        var picklistSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var picklist in (manifest.PickLists ?? []))
        {
            if (string.IsNullOrWhiteSpace(picklist.Slug))
            {
                errors.Add("PickList slug is required.");
                continue;
            }

            if (!picklistSlugs.Add(picklist.Slug))
            {
                errors.Add($"Duplicate picklist slug '{picklist.Slug}'.");
            }

            var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var option in picklist.Options)
            {
                if (string.IsNullOrWhiteSpace(option.Label) || string.IsNullOrWhiteSpace(option.Value))
                {
                    errors.Add($"PickList '{picklist.Slug}' has an option missing a label or value.");
                }
                else if (!values.Add(option.Value))
                {
                    errors.Add($"PickList '{picklist.Slug}' has duplicate option value '{option.Value}'.");
                }
            }
        }

        var slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var template in manifest.Templates)
        {
            if (!slugs.Add(template.Slug))
            {
                errors.Add($"Duplicate template slug '{template.Slug}'.");
            }
        }

        foreach (var field in manifest.Templates.SelectMany(AllFields))
        {
            if (!string.IsNullOrWhiteSpace(field.TemplateRef) && !slugs.Contains(field.TemplateRef))
            {
                errors.Add($"Field '{field.Key}' references unknown template '{field.TemplateRef}'.");
            }

            if (field.PrimitiveType.HasValue == !string.IsNullOrWhiteSpace(field.TemplateRef))
            {
                errors.Add($"Field '{field.Key}' must define exactly one primitiveType or templateRef.");
            }

            var picklistRef = ExtractPickListRef(field.FieldConfig);
            if (!string.IsNullOrWhiteSpace(picklistRef) && !picklistSlugs.Contains(picklistRef))
            {
                errors.Add($"Field '{field.Key}' references unknown picklist '{picklistRef}'.");
            }
        }

        _ = TopologicalSort(manifest, errors);
        return errors;
    }

    private static IReadOnlyList<CtpTemplate> TopologicalSort(CtpPackageManifest manifest, ICollection<string>? errors = null)
    {
        var templates = manifest.Templates.ToDictionary(template => template.Slug, StringComparer.OrdinalIgnoreCase);
        var sorted = new List<CtpTemplate>();
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var template in manifest.Templates)
        {
            Visit(template);
        }

        return sorted;

        void Visit(CtpTemplate template)
        {
            if (visited.Contains(template.Slug))
            {
                return;
            }

            if (!visiting.Add(template.Slug))
            {
                errors?.Add($"Circular template reference involving '{template.Slug}'.");
                return;
            }

            foreach (var reference in AllFields(template).Select(field => field.TemplateRef).Where(reference => !string.IsNullOrWhiteSpace(reference)))
            {
                if (templates.TryGetValue(reference!, out var dependency))
                {
                    Visit(dependency);
                }
            }

            visiting.Remove(template.Slug);
            visited.Add(template.Slug);
            sorted.Add(template);
        }
    }

    private static void AddStructure(TemplateVersion version, CtpTemplate packageTemplate, IReadOnlyDictionary<string, Template> templatesBySlug, IReadOnlyDictionary<string, Guid> picklistIdBySlug, IReadOnlyDictionary<string, Guid> picklistRevisionIdBySlug)
    {
        foreach (var packageSection in packageTemplate.Sections.OrderBy(section => section.Order))
        {
            var section = new TemplateSection
            {
                TemplateVersionId = version.Id,
                Name = packageSection.Name,
                Description = packageSection.Description,
                Order = packageSection.Order,
                IsCollapsible = packageSection.IsCollapsible
            };
            version.Sections.Add(section);

            foreach (var packageField in packageSection.Fields.OrderBy(field => field.Order))
            {
                version.Fields.Add(ToField(version.Id, section.Id, packageField, templatesBySlug, picklistIdBySlug, picklistRevisionIdBySlug));
            }
        }

        foreach (var packageField in packageTemplate.Fields.OrderBy(field => field.Order))
        {
            version.Fields.Add(ToField(version.Id, null, packageField, templatesBySlug, picklistIdBySlug, picklistRevisionIdBySlug));
        }
    }

    private static TemplateField ToField(Guid versionId, Guid? sectionId, CtpField packageField, IReadOnlyDictionary<string, Template> templatesBySlug, IReadOnlyDictionary<string, Guid> picklistIdBySlug, IReadOnlyDictionary<string, Guid> picklistRevisionIdBySlug) =>
        new()
        {
            TemplateVersionId = versionId,
            SectionId = sectionId,
            Key = packageField.Key,
            Label = packageField.Label,
            HelpText = packageField.HelpText,
            Order = packageField.Order,
            IsRequired = packageField.IsRequired,
            MinOccurrences = packageField.MinOccurrences ?? (packageField.IsRequired ? 1 : 0),
            MaxOccurrences = packageField.MaxOccurrences,
            IsOpen = packageField.IsOpen,
            CompositionMode = packageField.CompositionMode,
            PrimitiveType = packageField.PrimitiveType,
            TemplateId = string.IsNullOrWhiteSpace(packageField.TemplateRef) ? null : templatesBySlug[packageField.TemplateRef].Id,
            FieldConfig = RewriteFieldConfigForImport(packageField.FieldConfig, picklistIdBySlug, picklistRevisionIdBySlug)
        };

    private async Task<IReadOnlyList<TemplateVersion>> ResolveTemplatesAsync(Guid workspaceId, IReadOnlyList<Guid> selectedIds, CancellationToken ct)
    {
        var resolved = new Dictionary<Guid, TemplateVersion>();
        var queue = new Queue<Guid>(selectedIds);
        while (queue.Count > 0)
        {
            var templateId = queue.Dequeue();
            if (resolved.ContainsKey(templateId))
            {
                continue;
            }

            var version = await dbContext.TemplateVersions.AsNoTracking()
                .Include(item => item.Sections)
                .Include(item => item.Fields).ThenInclude(field => field.AllowedTypes)
                .Where(item => item.TemplateId == templateId
                    && dbContext.Templates.Any(template => template.Id == templateId && template.WorkspaceId == workspaceId && !template.IsDeleted)
                    && !item.IsDeleted)
                .OrderByDescending(item => item.Status == TemplateVersionStatus.Published)
                .ThenByDescending(item => item.VersionNumber)
                .FirstOrDefaultAsync(ct);
            if (version is null)
            {
                continue;
            }

            resolved[templateId] = version;
            foreach (var reference in version.Fields.Where(field => field.TemplateId.HasValue).Select(field => field.TemplateId!.Value))
            {
                queue.Enqueue(reference);
            }
        }

        return resolved.Values.ToArray();
    }

    private CtpTemplate ToPackageTemplate(TemplateVersion version, IReadOnlyDictionary<Guid, string> picklistSlugById)
    {
        var template = dbContext.Templates.AsNoTracking().First(item => item.Id == version.TemplateId);
        return new CtpTemplate(
            template.Slug,
            template.Name,
            template.Description,
            version.Sections.OrderBy(section => section.Order).Select(section => new CtpSection(
                section.Name,
                section.Description,
                section.Order,
                section.IsCollapsible,
                version.Fields.Where(field => field.SectionId == section.Id).OrderBy(field => field.Order).Select(field => ToPackageField(field, picklistSlugById)).ToArray())).ToArray(),
            version.Fields.Where(field => field.SectionId is null).OrderBy(field => field.Order).Select(field => ToPackageField(field, picklistSlugById)).ToArray());
    }

    private CtpField ToPackageField(TemplateField field, IReadOnlyDictionary<Guid, string> picklistSlugById)
    {
        string? templateRef = null;
        if (field.TemplateId.HasValue)
        {
            templateRef = dbContext.Templates.AsNoTracking().Where(template => template.Id == field.TemplateId.Value).Select(template => template.Slug).FirstOrDefault();
        }

        var fieldConfig = RewriteFieldConfigForExport(field.FieldConfig, field.PrimitiveType, picklistSlugById);
        return new CtpField(field.Key, field.Label, field.HelpText, field.Order, field.IsRequired, field.MinOccurrences, field.MaxOccurrences, field.IsOpen, field.CompositionMode, field.PrimitiveType, templateRef, fieldConfig);
    }

    private async Task<IReadOnlyList<CtpPackageManifest>> LoadOfficialPackagesAsync(CancellationToken ct)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var manifests = new List<CtpPackageManifest>();
        foreach (var resourceName in assembly.GetManifestResourceNames().Where(name => name.EndsWith(".ctp", StringComparison.OrdinalIgnoreCase)))
        {
            await using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Official package resource '{resourceName}' could not be loaded.");
            manifests.Add(await JsonSerializer.DeserializeAsync<CtpPackageManifest>(stream, JsonOptions, ct)
                ?? throw new InvalidOperationException($"Official package resource '{resourceName}' is empty."));
        }

        if (manifests.Count > 0)
        {
            return manifests;
        }

        var packageDirectory = Path.Combine(environment.ContentRootPath, "Packages");
        if (!Directory.Exists(packageDirectory))
        {
            return [];
        }

        foreach (var path in Directory.EnumerateFiles(packageDirectory, "*.ctp"))
        {
            await using var stream = System.IO.File.OpenRead(path);
            manifests.Add(await JsonSerializer.DeserializeAsync<CtpPackageManifest>(stream, JsonOptions, ct)
                ?? throw new InvalidOperationException($"Official package file '{path}' is empty."));
        }

        return manifests;
    }

    private static IEnumerable<CtpField> AllFields(CtpTemplate template) =>
        template.Fields.Concat(template.Sections.SelectMany(section => section.Fields));

    private static int CompareVersions(string left, string right)
    {
        if (Version.TryParse(left.Split('-', 2)[0], out var leftVersion) && Version.TryParse(right.Split('-', 2)[0], out var rightVersion))
        {
            return leftVersion.CompareTo(rightVersion);
        }

        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<ActionResult<PackageImportPreviewResponse>> BuildPreviewAsync(Guid workspaceId, CtpPackageManifest manifest, CancellationToken ct)
    {
        var validationErrors = ValidateManifest(manifest);
        if (validationErrors.Count > 0)
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, CmsifyError.ValidationFailed, "Package manifest is invalid", extensions: new Dictionary<string, object?> { ["errors"] = validationErrors });
        }

        return Ok(await BuildPreviewPayloadAsync(workspaceId, manifest, ct));
    }

    private async Task<PackageImportPreviewResponse> BuildPreviewPayloadAsync(Guid workspaceId, CtpPackageManifest manifest, CancellationToken ct)
    {
        var existingPickLists = await dbContext.PickLists.AsNoTracking()
            .Include(picklist => picklist.Options)
            .Where(picklist => picklist.WorkspaceId == workspaceId && !picklist.IsDeleted)
            .ToListAsync(ct);
        var existingBySlug = existingPickLists.ToDictionary(picklist => picklist.Slug, picklist => picklist, StringComparer.OrdinalIgnoreCase);

        var picklistPreviews = (manifest.PickLists ?? []).Select(picklist =>
        {
            var incomingOptions = picklist.Options
                .OrderBy(option => option.Order)
                .Select(option => new PackagePickListOptionPreview(option.Label, option.Value, option.Order))
                .ToArray();
            if (!existingBySlug.TryGetValue(picklist.Slug, out var existing))
            {
                return new PackagePickListPreview(picklist.Slug, picklist.Name, picklist.Description, incomingOptions, "new", null, null, null, null, PackagePickListResolution.ImportAsNew);
            }

            var existingOptions = existing.Options
                .OrderBy(option => option.Order)
                .Select(option => new PackagePickListOptionPreview(option.Label, option.Value, option.Order))
                .ToArray();
            var identical = PickListsMatch(existing, picklist);
            var status = identical ? "identical" : "conflict";
            var suggested = identical ? PackagePickListResolution.UseExisting : PackagePickListResolution.ImportAsNew;
            return new PackagePickListPreview(picklist.Slug, picklist.Name, picklist.Description, incomingOptions, status, existing.Id, existing.Name, existing.Description, existingOptions, suggested);
        }).ToArray();

        var existingTemplateSlugs = await dbContext.Templates.AsNoTracking()
            .Where(template => template.WorkspaceId == workspaceId && !template.IsDeleted)
            .Select(template => template.Slug)
            .ToListAsync(ct);
        var existingTemplateSet = new HashSet<string>(existingTemplateSlugs, StringComparer.OrdinalIgnoreCase);
        var templatePreviews = manifest.Templates.Select(template => new PackageTemplatePreview(
            template.Slug,
            template.Name,
            existingTemplateSet.Contains(template.Slug) ? "update" : "new")).ToArray();

        return new PackageImportPreviewResponse(manifest.PackageNamespace, manifest.Id, manifest.Version, picklistPreviews, templatePreviews);
    }

    private static bool PickListsMatch(PickList existing, CtpPickList incoming)
    {
        if (existing.Options.Count != incoming.Options.Count)
        {
            return false;
        }

        var existingOrdered = existing.Options.OrderBy(option => option.Order).ToArray();
        var incomingOrdered = incoming.Options.OrderBy(option => option.Order).ToArray();
        for (var i = 0; i < existingOrdered.Length; i++)
        {
            if (!string.Equals(existingOrdered[i].Label, incomingOrdered[i].Label, StringComparison.Ordinal)
                || !string.Equals(existingOrdered[i].Value, incomingOrdered[i].Value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private PickListRevision AddRevision(PickList picklist, IReadOnlyList<CtpPickListOption> options, int versionNumber)
    {
        var revision = new PickListRevision { PickListId = picklist.Id, VersionNumber = versionNumber };
        foreach (var option in OrderedOptionsForImport(options))
        {
            revision.Options.Add(new PickListRevisionOption { PickListRevisionId = revision.Id, Label = option.Label, Value = option.Value, Order = option.Order });
        }

        dbContext.PickListRevisions.Add(revision);
        return revision;
    }

    private static PickList CreatePickListFromPackage(Guid workspaceId, string slug, string name, string? description, IReadOnlyList<CtpPickListOption> options)
    {
        var picklist = new PickList
        {
            WorkspaceId = workspaceId,
            Slug = slug,
            Name = name,
            Description = description
        };

        foreach (var option in OrderedOptionsForImport(options))
        {
            picklist.Options.Add(new PickListOption
            {
                PickListId = picklist.Id,
                Label = option.Label,
                Value = option.Value,
                Order = option.Order
            });
        }

        return picklist;
    }

    private static IEnumerable<CtpPickListOption> OrderedOptionsForImport(IReadOnlyList<CtpPickListOption> options) =>
        options.Select((option, index) => new CtpPickListOption(option.Label, option.Value, option.Order == 0 && index != 0 ? index : option.Order))
            .OrderBy(option => option.Order);

    private static string NextAvailableSlug(string baseSlug, ISet<string> taken)
    {
        for (var i = 2; i < int.MaxValue; i++)
        {
            var candidate = $"{baseSlug}-{i}";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"Could not find an available slug derived from '{baseSlug}'.");
    }

    private static Guid? ExtractPickListId(PrimitiveType? primitiveType, JsonElement? fieldConfig)
    {
        if (primitiveType != PrimitiveType.PickList || !fieldConfig.HasValue || fieldConfig.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!fieldConfig.Value.TryGetProperty("picklistId", out var idElement)
            || idElement.ValueKind != JsonValueKind.String
            || !Guid.TryParse(idElement.GetString(), out var id))
        {
            return null;
        }

        return id;
    }

    private static Guid? ExtractPickListRevisionId(JsonElement? fieldConfig)
    {
        if (fieldConfig is not { ValueKind: JsonValueKind.Object } config
            || !config.TryGetProperty("picklistRevisionId", out var idElement)
            || idElement.ValueKind != JsonValueKind.String
            || !Guid.TryParse(idElement.GetString(), out var id))
        {
            return null;
        }

        return id;
    }

    private static string? ExtractPickListRef(JsonElement? fieldConfig)
    {
        if (!fieldConfig.HasValue || fieldConfig.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!fieldConfig.Value.TryGetProperty("picklistRef", out var refElement) || refElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return refElement.GetString();
    }

    private static JsonElement? RewriteFieldConfigForImport(JsonElement? fieldConfig, IReadOnlyDictionary<string, Guid> picklistIdBySlug, IReadOnlyDictionary<string, Guid> picklistRevisionIdBySlug)
    {
        if (!fieldConfig.HasValue || fieldConfig.Value.ValueKind != JsonValueKind.Object)
        {
            return fieldConfig?.Clone();
        }

        var picklistRef = ExtractPickListRef(fieldConfig);
        if (picklistRef is null || !picklistIdBySlug.TryGetValue(picklistRef, out var id))
        {
            return fieldConfig?.Clone();
        }

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            foreach (var property in fieldConfig.Value.EnumerateObject())
            {
                if (property.NameEquals("picklistRef") || property.NameEquals("picklistId") || property.NameEquals("picklistRevisionId"))
                {
                    continue;
                }

                property.WriteTo(writer);
            }

            writer.WriteString("picklistId", id.ToString());
            if (picklistRevisionIdBySlug.TryGetValue(picklistRef, out var revisionId))
            {
                writer.WriteString("picklistRevisionId", revisionId.ToString());
            }
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(ms.ToArray());
        return document.RootElement.Clone();
    }

    private static JsonElement? RewriteFieldConfigForExport(JsonElement? fieldConfig, PrimitiveType? primitiveType, IReadOnlyDictionary<Guid, string> picklistSlugById)
    {
        if (primitiveType != PrimitiveType.PickList || !fieldConfig.HasValue || fieldConfig.Value.ValueKind != JsonValueKind.Object)
        {
            return fieldConfig?.Clone();
        }

        var picklistId = ExtractPickListId(primitiveType, fieldConfig);
        if (picklistId is null || !picklistSlugById.TryGetValue(picklistId.Value, out var slug))
        {
            return fieldConfig?.Clone();
        }

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            foreach (var property in fieldConfig.Value.EnumerateObject())
            {
                if (property.NameEquals("picklistId") || property.NameEquals("picklistRef") || property.NameEquals("picklistRevisionId"))
                {
                    continue;
                }

                property.WriteTo(writer);
            }

            writer.WriteString("picklistRef", slug);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(ms.ToArray());
        return document.RootElement.Clone();
    }
}

public sealed record CtpPackageManifest(
    string CmsifyPackage,
    [property: JsonPropertyName("namespace")] string PackageNamespace,
    string Id,
    string Version,
    string Name,
    string? Description,
    string? Author,
    string? License,
    string? Homepage,
    IReadOnlyList<CtpTemplate> Templates,
    [property: JsonPropertyName("picklists")] IReadOnlyList<CtpPickList>? PickLists = null);

public sealed record CtpPickList(string Slug, string Name, string? Description, IReadOnlyList<CtpPickListOption> Options);

public sealed record CtpPickListOption(string Label, string Value, int Order);

public sealed record CtpTemplate(string Slug, string Name, string? Description, IReadOnlyList<CtpSection> Sections, IReadOnlyList<CtpField> Fields);

public sealed record CtpSection(string Name, string? Description, int Order, bool IsCollapsible, IReadOnlyList<CtpField> Fields);

public sealed record CtpField(
    string Key,
    string Label,
    string? HelpText,
    int Order,
    bool IsRequired,
    int? MinOccurrences,
    int? MaxOccurrences,
    bool IsOpen,
    CompositionMode CompositionMode,
    PrimitiveType? PrimitiveType,
    string? TemplateRef,
    JsonElement? FieldConfig);

public sealed record PackageImportResponse(
    string PackageNamespace,
    string Id,
    string Version,
    IReadOnlyList<PackageTemplateImportResult> Imported,
    IReadOnlyList<string> Skipped,
    IReadOnlyList<string> Errors,
    IReadOnlyList<PackagePickListImportResult> PickLists);

public sealed record PackageTemplateImportResult(Guid TemplateId, string Slug, string Name, Guid TemplateVersionId, int VersionNumber);

public sealed record PackagePickListImportResult(string Slug, string ResolvedSlug, Guid PickListId, string Action);

public sealed record PackageImportResolutions(IReadOnlyDictionary<string, string>? PickLists);

public sealed record PackageImportPreviewResponse(
    string PackageNamespace,
    string Id,
    string Version,
    IReadOnlyList<PackagePickListPreview> PickLists,
    IReadOnlyList<PackageTemplatePreview> Templates);

public sealed record PackagePickListPreview(
    string Slug,
    string Name,
    string? Description,
    IReadOnlyList<PackagePickListOptionPreview> Options,
    string Status,
    Guid? ExistingId,
    string? ExistingName,
    string? ExistingDescription,
    IReadOnlyList<PackagePickListOptionPreview>? ExistingOptions,
    string SuggestedAction);

public sealed record PackagePickListOptionPreview(string Label, string Value, int Order);

public sealed record PackageTemplatePreview(string Slug, string Name, string Status);

public static class PackagePickListResolution
{
    public const string UseExisting = "useExisting";
    public const string Replace = "replace";
    public const string ImportAsNew = "importAsNew";
}

public sealed record OfficialPackageResponse(string PackageNamespace, string Id, string Version, string Name, string? Description, string? Author, string? License, string? Homepage, int TemplateCount, IReadOnlyList<OfficialPackageTemplateResponse> Templates);

public sealed record OfficialPackageTemplateResponse(string Slug, string Name, string? Description);

internal static class CtpSchema
{
    public static object Build() => new
    {
        schema = "https://json-schema.org/draft/2020-12/schema",
        id = "https://cmsify.dev/schema/ctp-1.0.json",
        title = "Cmsify Template Package",
        type = "object",
        required = new[] { "cmsifyPackage", "namespace", "id", "version", "name", "templates" },
        properties = new Dictionary<string, object>
        {
            ["cmsifyPackage"] = new { @const = "1.0" },
            ["namespace"] = new { type = "string", minLength = 1, maxLength = 200 },
            ["id"] = new { type = "string", minLength = 1, maxLength = 200 },
            ["version"] = new { type = "string", minLength = 1, maxLength = 50 },
            ["name"] = new { type = "string", minLength = 1, maxLength = 200 },
            ["description"] = new { type = new[] { "string", "null" } },
            ["author"] = new { type = new[] { "string", "null" } },
            ["license"] = new { type = new[] { "string", "null" } },
            ["homepage"] = new { type = new[] { "string", "null" } },
            ["templates"] = new { type = "array", minItems = 1 },
            ["picklists"] = new { type = new[] { "array", "null" } }
        }
    };
}
