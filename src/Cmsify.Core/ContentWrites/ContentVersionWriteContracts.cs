using System.Text.Json;
using Cmsify.Core.Domain.Enums;

namespace Cmsify.Core.ContentWrites;

public sealed record UpdateContentVersionRequest(Guid WorkspaceId, Guid ContentItemId, int VersionNumber,
    ContentVersionRevisionCondition Revision, DateTimeOffset? EffectiveStartAt, DateTimeOffset? EffectiveEndAt,
    IReadOnlyList<ContentVersionFieldInput> Fields, bool ExpandChildren = true);
public sealed record ContentVersionRevisionCondition(long? Candidate)
{
    private const int TicksPerMicrosecond = 10;
    public bool Matches(DateTimeOffset updatedAt) => Candidate is { } candidate &&
        (candidate == updatedAt.UtcTicks / TicksPerMicrosecond || candidate == updatedAt.UtcTicks);
    public static long Normalize(DateTimeOffset updatedAt) => updatedAt.UtcTicks / TicksPerMicrosecond;
}
public sealed record ContentVersionEditValues(DateTimeOffset? EffectiveStartAt, DateTimeOffset? EffectiveEndAt,
    IReadOnlyList<ContentVersionFieldInput> Fields);
public sealed record ContentVersionEditSnapshot(Guid Id, Guid ContentItemId, Guid WorkspaceId, int VersionNumber,
    ContentStatus Status, DateTimeOffset UpdatedAt);
public sealed record UpdatedContentVersionOutput(long Revision, ContentVersionDetailOutput Version);
public sealed record ContentVersionFieldInput(Guid FieldId, int Order, ValueKind ValueKind, string? TextValue,
    bool? BoolValue, Guid? MediaAssetId, Guid? FileAssetId, Guid? ChildContentItemId, JsonElement? JsonValue);
public sealed record ContentVersionFieldOutput(Guid FieldId, string? Key, string? Label, int Order, ValueKind ValueKind,
    string? TextValue, bool? BoolValue, Guid? MediaAssetId, Guid? FileAssetId, Guid? ChildContentItemId,
    ContentVersionDetailOutput? Child, JsonElement? JsonValue, string? DisplayLabel = null);
public sealed record ContentVersionDetailOutput(Guid Id, Guid ContentItemId, int VersionNumber, ContentStatus Status,
    Guid TemplateVersionId, string TemplateName, string? Slug, string? LocaleCode, Guid? TranslationGroupId,
    DateTimeOffset? EffectiveStartAt, DateTimeOffset? EffectiveEndAt, DateTimeOffset? PublishAt,
    DateTimeOffset? PublishedAt, DateTimeOffset? ArchivedAt, Guid? PublishedByUserId, int? RolledBackFromVersionNumber,
    IReadOnlyList<string> Tags, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    IReadOnlyList<ContentVersionFieldOutput> Fields, string TemplateSlug = "");
public static class ContentVersionWriteErrors
{
    public const string AuthenticationRequired = "authentication-required";
    public const string Forbidden = "forbidden";
    public const string NotFound = "not-found";
    public const string NotEditable = "content-version-not-editable";
    public const string ConcurrencyMismatch = "concurrency-mismatch";
    public const string InvalidEffectiveRange = "invalid-effective-range";
    public const string ContentValidationFailed = "content-validation-failed";
}
