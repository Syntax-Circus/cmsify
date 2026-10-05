using System.Text.Json;
using Cmsify.Core.ContentWrites;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Services;
using Cmsify.Infrastructure.Persistence.ContentWrites;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Cmsify.Infrastructure.Tests;

public sealed class ContentVersionFieldWriterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ReplacementHasNoOldOrDuplicateRows(bool sqlite, bool empty)
    {
        await using var f = await ContentListQueryFixtures.Create(sqlite);
        await using var db = f.Context();
        var field = Field(f.TemplateVersion, PrimitiveType.Text);
        var item = f.Item("replacement"); var version = f.Version(item, 1);
        version.FieldValues.Add(new() { ContentVersionId = version.Id, FieldId = field.Id, ValueKind = ValueKind.Text, TextValue = "old" });
        db.AddRange(field, item, version); await db.SaveChangesAsync(Ct);
        var oldFieldIds = version.FieldValues.Select(x => x.Id).ToArray();
        ContentVersionFieldInput[] fields = empty ? [] : [Input(field, "new")];
        (await new ContentVersionFieldWriter(db, new ContentValidator()).ApplyAsync(version, f.TemplateVersion, fields, Ct)).ShouldBeNull();
        Assert.Equal(empty ? 0 : 1, version.FieldValues.Count);
        await db.SaveChangesAsync(Ct);
        await using var read = f.Context();
        var rows = await read.ContentVersionFieldValues.Where(x => x.ContentVersionId == version.Id).ToListAsync(Ct);
        var persistedFieldIds = rows.Select(x => x.Id).ToArray();
        Assert.Empty(oldFieldIds.Intersect(persistedFieldIds));
        Assert.Equal(empty ? 0 : 1, rows.Count);
        if (!empty) Assert.Equal("new", rows[0].TextValue);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateOrderPreservesSubmittedSequence(bool sqlite)
    {
        await using var f = await ContentListQueryFixtures.Create(sqlite); await using var db = f.Context();
        var field = Field(f.TemplateVersion, PrimitiveType.Text); var item = f.Item("order"); var version = f.Version(item, 1);
        db.AddRange(field, item, version); await db.SaveChangesAsync(Ct);
        var fields = new[] { Input(field, "third") with { Order = 7 }, Input(field, "first") with { Order = 7 }, Input(field, "second") with { Order = 7 } };
        (await new ContentVersionFieldWriter(db, new ContentValidator()).ApplyAsync(version, f.TemplateVersion, fields, Ct)).ShouldBeNull();
        await db.SaveChangesAsync(Ct);
        var output = await new ContentVersionDetailProjector(db).ProjectAsync(version, DateTimeOffset.UtcNow, false, Ct);
        var expectedSubmittedOrder = new[] { "third", "first", "second" };
        var projectedOrder = output.Fields.Select(x => x.TextValue).ToArray();
        Assert.Equal(expectedSubmittedOrder, projectedOrder);
        Assert.Equal(new[] { 7, 8, 9 }, output.Fields.Select(x => x.Order));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevisionLabelsRemainSnapshots(bool sqlite)
    {
        await using var f = await ContentListQueryFixtures.Create(sqlite); await using var db = f.Context();
        var list = new PickList { WorkspaceId = f.Workspace.Id, Name = "Colours", Slug = "colours" };
        var revision = new PickListRevision { PickListId = list.Id, VersionNumber = 1 };
        var current = new PickListOption { PickListId = list.Id, Value = "red", Label = "Current label" };
        var historic = new PickListRevisionOption { PickListRevisionId = revision.Id, Value = "red", Label = "Snapshot label" };
        var field = Field(f.TemplateVersion, PrimitiveType.PickList);
        field.FieldConfig = JsonSerializer.SerializeToElement(new { picklistId = list.Id, picklistRevisionId = revision.Id });
        var item = f.Item("labels"); var version = f.Version(item, 1);
        db.AddRange(list, revision, current, historic, field, item, version); await db.SaveChangesAsync(Ct);
        (await new ContentVersionFieldWriter(db, new ContentValidator()).ApplyAsync(version, f.TemplateVersion,
            [Input(field, "red") with { ValueKind = ValueKind.PickList }], Ct)).ShouldBeNull();
        await db.SaveChangesAsync(Ct); current.Label = "Later label"; await db.SaveChangesAsync(Ct);
        var output = await new ContentVersionDetailProjector(db).ProjectAsync(version, DateTimeOffset.UtcNow, false, Ct);
        Assert.Equal("Snapshot label", Assert.Single(output.Fields).DisplayLabel);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NestedComponentValidationPreservesErrors(bool sqlite)
    {
        await using var f = await ContentListQueryFixtures.Create(sqlite); await using var db = f.Context();
        var nested = new ComponentDefinition { WorkspaceId = f.Workspace.Id, Name = "Nested", Slug = "nested" };
        var nestedVersion = new ComponentVersion { ComponentId = nested.Id, VersionNumber = 1, Status = TemplateVersionStatus.Published };
        var pick = new ComponentField { ComponentVersionId = nestedVersion.Id, Key = "choice", Label = "Choice", PrimitiveType = PrimitiveType.PickList };
        var parent = new ComponentDefinition { WorkspaceId = f.Workspace.Id, Name = "Parent", Slug = "parent" };
        var parentVersion = new ComponentVersion { ComponentId = parent.Id, VersionNumber = 1, Status = TemplateVersionStatus.Published };
        var child = new ComponentField { ComponentVersionId = parentVersion.Id, Key = "nested", Label = "Nested", NestedComponentId = nested.Id };
        var field = Field(f.TemplateVersion, null); field.ComponentId = parent.Id;
        var item = f.Item("component"); var version = f.Version(item, 1);
        db.AddRange(nested, nestedVersion, pick, parent, parentVersion, child, field, item, version); await db.SaveChangesAsync(Ct);
        nested.CurrentVersionId = nestedVersion.Id; parent.CurrentVersionId = parentVersion.Id; await db.SaveChangesAsync(Ct);
        var input = Input(field, null) with { ValueKind = ValueKind.Component, JsonValue = JsonSerializer.SerializeToElement(new { nested = new { choice = "invalid" } }) };
        var error = await new ContentVersionFieldWriter(db, new ContentValidator()).ApplyAsync(version, f.TemplateVersion, [input], Ct);
        Assert.Equal("Field 'value': component field 'choice' must bind a PickList revision.", error);
    }

    private static TemplateField Field(TemplateVersion version, PrimitiveType? primitive)
    {
        var field = new TemplateField { TemplateVersionId = version.Id, Key = "value", Label = "Value", PrimitiveType = primitive };
        version.Fields.Add(field); return field;
    }
    private static ContentVersionFieldInput Input(TemplateField field, string? text) => new(field.Id, 0, ValueKind.Text, text, null, null, null, null, null);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AllPrimitiveKindsRetainSubmittedPayloadsAndOwnedJson(bool sqlite)
    {
        await using var f = await ContentListQueryFixtures.Create(sqlite); await using var db = f.Context();
        var item = f.Item("kinds"); var version = f.Version(item, 1);
        var kinds = new[] { (PrimitiveType.Text, ValueKind.Text), (PrimitiveType.RichText, ValueKind.RichText),
            (PrimitiveType.Markdown, ValueKind.Markdown), (PrimitiveType.Boolean, ValueKind.Boolean),
            (PrimitiveType.Media, ValueKind.Media), (PrimitiveType.File, ValueKind.File),
            (PrimitiveType.Link, ValueKind.Link), (PrimitiveType.Quote, ValueKind.Quote), (PrimitiveType.Separator, ValueKind.Separator) };
        var fields = kinds.Select((kind, index) => new TemplateField { TemplateVersionId = f.TemplateVersion.Id,
            Key = $"value-{index}", Label = $"Value {index}", PrimitiveType = kind.Item1, Order = index }).ToArray();
        foreach (var field in fields) f.TemplateVersion.Fields.Add(field);
        db.AddRange(fields); db.AddRange(item, version); await db.SaveChangesAsync(Ct);
        var media = Guid.NewGuid(); var file = Guid.NewGuid();
        using (var document = JsonDocument.Parse("{\"value\":\"retained\"}"))
        {
            var inputs = fields.Select((field, index) => new ContentVersionFieldInput(field.Id, index, kinds[index].Item2,
                $"submitted-{index}", true, media, file, null, document.RootElement)).ToArray();
            (await new ContentVersionFieldWriter(db, new ContentValidator()).ApplyAsync(version, f.TemplateVersion, inputs, Ct)).ShouldBeNull();
        }
        Assert.Equal(kinds.Select(x => x.Item2), version.FieldValues.Select(x => x.ValueKind));
        Assert.Equal(Enumerable.Range(0, 9).Select(x => $"submitted-{x}"), version.FieldValues.Select(x => x.TextValue));
        Assert.All(version.FieldValues, value =>
        {
            Assert.True(value.BoolValue); Assert.Equal(media, value.MediaAssetId); Assert.Equal(file, value.FileAssetId);
            Assert.Equal("retained", value.JsonValue!.Value.GetProperty("value").GetString());
        });
        // Unpersisted arbitrary asset IDs deliberately test payload copying without inventing assets.
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 3)]
    public async Task ValidationRetainsTemplateAndPickListErrorOrder(bool sqlite, int errorCase)
    {
        await using var f = await ContentListQueryFixtures.Create(sqlite); await using var db = f.Context();
        var field = Field(f.TemplateVersion, errorCase < 2 ? PrimitiveType.Text : PrimitiveType.PickList);
        field.IsRequired = errorCase < 2;
        if (errorCase == 3) field.FieldConfig = JsonSerializer.SerializeToElement(new { picklistRevisionId = Guid.NewGuid() });
        var item = f.Item("invalid"); var version = f.Version(item, 1);
        db.AddRange(field, item, version); await db.SaveChangesAsync(Ct);
        var writer = new ContentVersionFieldWriter(db, new ContentValidator());
        ContentVersionFieldInput[] inputs = errorCase switch
        {
            0 => [],
            1 => [Input(field, null) with { ValueKind = ValueKind.Boolean, BoolValue = true }],
            _ => [Input(field, "missing") with { ValueKind = ValueKind.PickList }]
        };
        var expected = errorCase switch
        {
            0 => "Field 'value' requires at least 1 value(s).",
            1 => "Field 'value' expects Text values.",
            2 => "Field 'value' must bind a PickList revision.",
            _ => "Field 'value' references an unavailable PickList revision."
        };
        Assert.Equal(expected, await writer.ApplyAsync(version, f.TemplateVersion, inputs, Ct));
    }
}
