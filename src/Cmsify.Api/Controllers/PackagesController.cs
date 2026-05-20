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

        var manifest = await ReadManifestAsync(ct);
        return await ImportManifestAsync(workspaceId, manifest, ct);
    }

    [HttpPost("/api/v1/workspaces/{workspaceId:guid}/packages/import/official/{packageId}")]
    [RequireRole(UserRole.TemplateAdmin)]
    public async Task<ActionResult<PackageImportResponse>> ImportOfficial(Guid workspaceId, string packageId, CancellationToken ct)
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

        return await ImportManifestAsync(workspaceId, manifest, ct);
    }

    private async Task<ActionResult<PackageImportResponse>> ImportManifestAsync(Guid workspaceId, CtpPackageManifest manifest, CancellationToken ct)
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

            AddStructure(version, packageTemplate, templatesBySlug);
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
        return Ok(new PackageImportResponse(manifest.PackageNamespace, manifest.Id, manifest.Version, imported, [], []));
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
            templates.Select(ToPackageTemplate).OrderBy(template => template.Name).ToArray());

        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        return File(Encoding.UTF8.GetBytes(json), "application/json", $"{packageNamespace}.{id}@{version}.ctp");
    }

    private async Task<CtpPackageManifest> ReadManifestAsync(CancellationToken ct)
    {
        if (Request.HasFormContentType)
        {
            var form = await Request.ReadFormAsync(ct);
            var file = form.Files.FirstOrDefault();
            if (file is null)
            {
                throw new ArgumentException("Multipart import requires a .ctp file.");
            }

            await using var stream = file.OpenReadStream();
            return await JsonSerializer.DeserializeAsync<CtpPackageManifest>(stream, JsonOptions, ct)
                ?? throw new ArgumentException("Package manifest is empty.");
        }

        return await JsonSerializer.DeserializeAsync<CtpPackageManifest>(Request.Body, JsonOptions, ct)
            ?? throw new ArgumentException("Package manifest is empty.");
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

    private static void AddStructure(TemplateVersion version, CtpTemplate packageTemplate, IReadOnlyDictionary<string, Template> templatesBySlug)
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
                version.Fields.Add(ToField(version.Id, section.Id, packageField, templatesBySlug));
            }
        }

        foreach (var packageField in packageTemplate.Fields.OrderBy(field => field.Order))
        {
            version.Fields.Add(ToField(version.Id, null, packageField, templatesBySlug));
        }
    }

    private static TemplateField ToField(Guid versionId, Guid? sectionId, CtpField packageField, IReadOnlyDictionary<string, Template> templatesBySlug) =>
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
            FieldConfig = packageField.FieldConfig?.Clone()
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

    private CtpTemplate ToPackageTemplate(TemplateVersion version)
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
                version.Fields.Where(field => field.SectionId == section.Id).OrderBy(field => field.Order).Select(field => ToPackageField(field)).ToArray())).ToArray(),
            version.Fields.Where(field => field.SectionId is null).OrderBy(field => field.Order).Select(ToPackageField).ToArray());
    }

    private CtpField ToPackageField(TemplateField field)
    {
        string? templateRef = null;
        if (field.TemplateId.HasValue)
        {
            templateRef = dbContext.Templates.AsNoTracking().Where(template => template.Id == field.TemplateId.Value).Select(template => template.Slug).FirstOrDefault();
        }

        return new CtpField(field.Key, field.Label, field.HelpText, field.Order, field.IsRequired, field.MinOccurrences, field.MaxOccurrences, field.IsOpen, field.CompositionMode, field.PrimitiveType, templateRef, field.FieldConfig);
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
    IReadOnlyList<CtpTemplate> Templates);

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

public sealed record PackageImportResponse(string PackageNamespace, string Id, string Version, IReadOnlyList<PackageTemplateImportResult> Imported, IReadOnlyList<string> Skipped, IReadOnlyList<string> Errors);

public sealed record PackageTemplateImportResult(Guid TemplateId, string Slug, string Name, Guid TemplateVersionId, int VersionNumber);

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
            ["templates"] = new { type = "array", minItems = 1 }
        }
    };
}
