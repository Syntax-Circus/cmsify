using System.Text.Json;
using System.Text.Json.Serialization;

namespace SyntaxCircus.Cmsify.Contracts;

/// <summary>Public wire model for a Cmsify content-template package manifest.</summary>
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
    [property: JsonPropertyName("picklists")] IReadOnlyList<CtpPickList>? PickLists = null,
    IReadOnlyList<CtpComponent>? Components = null);

public sealed record CtpPickList(string Slug, string Name, string? Description, IReadOnlyList<CtpPickListOption> Options);
public sealed record CtpPickListOption(string Label, string Value, int Order);
public sealed record CtpTemplate(string Slug, string Name, string? Description, IReadOnlyList<CtpSection> Sections, IReadOnlyList<CtpField> Fields);
public sealed record CtpSection(string Name, string? Description, int Order, bool IsCollapsible, IReadOnlyList<CtpField> Fields);
public sealed record CtpField(string Key, string Label, string? HelpText, int Order, bool IsRequired, int? MinOccurrences, int? MaxOccurrences, bool IsOpen, CompositionMode CompositionMode, PrimitiveType? PrimitiveType, string? TemplateRef, JsonElement? FieldConfig, string? ComponentRef = null);
public sealed record CtpComponent(string Slug, string Name, string? Description, IReadOnlyList<CtpComponentField> Fields);
public sealed record CtpComponentField(string Key, string Label, string? HelpText, int Order, bool IsRequired, int MinOccurrences, int? MaxOccurrences, PrimitiveType? PrimitiveType, string? ComponentRef, JsonElement? FieldConfig);

public static class PackagePickListResolution
{
    public const string UseExisting = "useExisting";
    public const string Replace = "replace";
    public const string ImportAsNew = "importAsNew";
}

public static class PackageComponentResolution
{
    public const string UseExisting = "useExisting";
    public const string Replace = "replace";
    public const string ImportAsNew = "importAsNew";
}

public static class CtpSchema
{
    public static object Build() => new
    {
        schema = "https://json-schema.org/draft/2020-12/schema",
        id = "https://cmsify.dev/schema/ctp-1.1.json",
        title = "Cmsify Reusable Model Package",
        type = "object",
        required = new[] { "cmsifyPackage", "namespace", "id", "version", "name", "templates" },
        properties = new Dictionary<string, object>
        {
            ["cmsifyPackage"] = new { type = "string", @enum = new[] { "1.0", "1.1" } },
            ["namespace"] = new { type = "string", minLength = 1, maxLength = 200 },
            ["id"] = new { type = "string", minLength = 1, maxLength = 200 },
            ["version"] = new { type = "string", minLength = 1, maxLength = 50 },
            ["name"] = new { type = "string", minLength = 1, maxLength = 200 },
            ["description"] = new { type = new[] { "string", "null" } },
            ["author"] = new { type = new[] { "string", "null" } },
            ["license"] = new { type = new[] { "string", "null" } },
            ["homepage"] = new { type = new[] { "string", "null" } },
            ["templates"] = new { type = "array" },
            ["picklists"] = new { type = new[] { "array", "null" } },
            ["components"] = new { type = new[] { "array", "null" } }
        }
    };
}
