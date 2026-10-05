using Cmsify.Core.ContentWrites;
using SyntaxCircus.Cmsify.Contracts;

namespace Cmsify.Api.Controllers;

internal static class ContentVersionWriteMappings
{
    public static ContentVersionFieldInput ToFieldInput(ContentFieldValueRequest field) =>
        new(field.FieldId, field.Order, field.ValueKind.ToCore(), field.TextValue, field.BoolValue,
            field.MediaAssetId, field.FileAssetId, field.ChildContentItemId, field.JsonValue?.Clone());

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
