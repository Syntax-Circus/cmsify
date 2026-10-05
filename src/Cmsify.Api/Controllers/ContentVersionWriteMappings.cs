using System.Globalization;
using Cmsify.Core.ContentWrites;
using SyntaxCircus.Cmsify.Contracts;
using WriteRequest = Cmsify.Core.ContentWrites.UpdateContentVersionRequest;
using WireRequest = SyntaxCircus.Cmsify.Contracts.UpdateContentVersionRequest;

namespace Cmsify.Api.Controllers;

internal static class ContentVersionWriteMappings
{
    public static WriteRequest ToWriteRequest(this WireRequest request, Guid workspaceId, Guid itemId,
        int versionNumber, string revision, bool expandChildren) =>
        new(workspaceId, itemId, versionNumber, ParseRevision(revision), request.EffectiveStartAt,
            request.EffectiveEndAt, request.Fields.Select(ToFieldInput).ToList(), expandChildren);

    public static ContentVersionRevisionCondition ParseRevision(string raw)
    {
        if (raw.Length >= 3 && raw[0] == '"' && raw[^1] == '"'
            && long.TryParse(raw.AsSpan(1, raw.Length - 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var candidate)
            && string.Equals(raw, $"\"{candidate.ToString(CultureInfo.InvariantCulture)}\"", StringComparison.Ordinal))
            return new(candidate);
        return new(null);
    }

    public static ContentVersionFieldInput ToFieldInput(ContentFieldValueRequest field)
    {
        // MVC accepts null elements and undefined numeric enum values. Preserve them
        // until field processing so resource/state/revision/template decisions stay first.
        if (field is null) return null!;
        var kind = Enum.IsDefined(field.ValueKind)
            ? field.ValueKind.ToCore()
            : (Cmsify.Core.Domain.Enums.ValueKind)(int)field.ValueKind;
        return new(field.FieldId, field.Order, kind, field.TextValue, field.BoolValue,
            field.MediaAssetId, field.FileAssetId, field.ChildContentItemId, field.JsonValue?.Clone());
    }

    public static ContentVersionDetailResponse ToResponse(ContentVersionDetailOutput version) =>
        new(version.Id, version.ContentItemId, version.VersionNumber, version.Status.ToContract(),
            version.TemplateVersionId, version.TemplateName, version.Slug, version.LocaleCode,
            version.TranslationGroupId, version.EffectiveStartAt, version.EffectiveEndAt, version.PublishAt,
            version.PublishedAt, version.ArchivedAt, version.PublishedByUserId, version.RolledBackFromVersionNumber,
            version.Tags.ToList(), version.CreatedAt, version.UpdatedAt, version.Fields.Select(ToResponse).ToList(), version.TemplateSlug);

    private static ContentVersionFieldValueResponse ToResponse(ContentVersionFieldOutput field) =>
        new(field.FieldId, field.Key, field.Label, field.Order, field.ValueKind.ToContract(), field.TextValue,
            field.BoolValue, field.MediaAssetId, field.FileAssetId, field.ChildContentItemId,
            field.Child is null ? null : ToResponse(field.Child), field.JsonValue?.Clone(), field.DisplayLabel);
}
