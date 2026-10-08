using System.Text.Json;
using Cmsify.Core.Domain.Enums;

namespace Cmsify.Core.EmbeddedContent;

public sealed record EmbeddedTemplateField(string Key, string Label, int Order, bool IsRequired,
    int MinOccurrences, int MaxOccurrences, PrimitiveType PrimitiveType, ValueKind ValueKind,
    CompositionMode CompositionMode, bool IsOpen, JsonElement FieldConfig);
public sealed record EmbeddedTemplateContract(string ContractKey, string Name, string Slug,
    string TitleFieldKey, IReadOnlyList<EmbeddedTemplateField> Fields);
public sealed record EmbeddedTemplateFieldOutput(Guid FieldId, EmbeddedTemplateField Schema);
public sealed record EmbeddedTemplateOutput(Guid WorkspaceId, string ContractKey, string Fingerprint,
    Guid TemplateId, Guid TemplateVersionId, int VersionNumber, IReadOnlyList<EmbeddedTemplateFieldOutput> Fields);
public sealed record EnsureEmbeddedTemplateRequest(Guid WorkspaceId, EmbeddedTemplateContract Contract);
public sealed record GetEmbeddedTemplateRequest(Guid WorkspaceId, string ContractKey, string ExpectedFingerprint);
