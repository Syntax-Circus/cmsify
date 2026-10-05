using System.Data.Common;
using System.Text.Json;
using Cmsify.Core.ContentWrites;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Persistence.ContentWrites;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Cmsify.Infrastructure.Tests;

public sealed class ContentVersionDetailProjectorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset _at = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 4)]
    [InlineData(false, 12)]
    [InlineData(true, 1)]
    [InlineData(true, 4)]
    [InlineData(true, 12)]
    public async Task ProjectionRetainsDepthEightAndBatching(bool sqlite, int siblings)
    {
        await using var f = await ContentListQueryFixtures.Create(sqlite);
        var probe = new Probe();
        await using var db = new CmsifyDbContext(new DbContextOptionsBuilder<CmsifyDbContext>(f.Options).AddInterceptors(probe).Options);
        var field = new TemplateField { TemplateVersionId = f.TemplateVersion.Id, Key = "child", Label = "Child", TemplateId = f.TemplateVersion.TemplateId };
        db.Add(field);
        ContentVersion Node(string slug)
        {
            var item = f.Item(slug); var version = f.Version(item, 1); db.AddRange(item, version); return version;
        }
        var root = Node("root");
        for (var sibling = 0; sibling < siblings; sibling++)
        {
            var parent = root;
            for (var depth = 1; depth <= 10; depth++)
            {
                var child = Node($"node-{sibling}-{depth}");
                parent.FieldValues.Add(new() { ContentVersionId = parent.Id, FieldId = field.Id, Order = sibling,
                    ValueKind = ValueKind.ChildContent, ChildContentItemId = child.ContentItemId });
                parent = child;
            }
        }
        await db.SaveChangesAsync(Ct); probe.Count = 0;
        var output = await new ContentVersionDetailProjector(db).ProjectAsync(root, _at, true, Ct);
        var baselineQueryCount = 10;
        var extractedQueryCount = probe.Count;
        Assert.Equal(baselineQueryCount, extractedQueryCount);
        Assert.Equal(siblings, output.Fields.Count);
        var node = output;
        for (var depth = 0; depth < 8; depth++) node = Assert.IsType<ContentVersionDetailOutput>(node.Fields[0].Child);
        Assert.Null(node.Fields[0].Child);
        Assert.NotNull(node.Fields[0].ChildContentItemId);
        probe.Count = 0;
        output = await new ContentVersionDetailProjector(db).ProjectAsync(root, _at, false, Ct);
        Assert.Equal(2, probe.Count);
        Assert.All(output.Fields, value => Assert.Null(value.Child));
        Assert.All(output.Fields, value => Assert.NotNull(value.ChildContentItemId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProjectionOwnsJsonAfterContextDisposal(bool sqlite)
    {
        await using var f = await ContentListQueryFixtures.Create(sqlite);
        ContentVersionDetailOutput output;
        using (var document = JsonDocument.Parse("{\"value\":\"retained\"}"))
        {
            await using var db = f.Context();
            var field = new TemplateField { TemplateVersionId = f.TemplateVersion.Id, Key = "json", Label = "JSON", PrimitiveType = PrimitiveType.Link };
            var item = f.Item("json"); var version = f.Version(item, 1);
            version.FieldValues.Add(new() { ContentVersionId = version.Id, FieldId = field.Id, ValueKind = ValueKind.Link, JsonValue = document.RootElement });
            db.AddRange(field, item, version); await db.SaveChangesAsync(Ct);
            output = await new ContentVersionDetailProjector(db).ProjectAsync(version, _at, false, Ct);
        }
        var outputJson = Assert.Single(output.Fields).JsonValue!.Value;
        Assert.Equal("retained", outputJson.GetProperty("value").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingAndOutOfRangeChildrenRemainUnexpanded(bool sqlite)
    {
        await using var f = await ContentListQueryFixtures.Create(sqlite); await using var db = f.Context();
        var field = new TemplateField { TemplateVersionId = f.TemplateVersion.Id, Key = "child", Label = "Child", TemplateId = f.TemplateVersion.TemplateId };
        var item = f.Item("root"); var root = f.Version(item, 1);
        var childItem = f.Item("unavailable"); var child = f.Version(childItem, 1, _at.AddDays(1), _at.AddDays(2));
        root.FieldValues.Add(new() { ContentVersionId = root.Id, FieldId = field.Id, ValueKind = ValueKind.ChildContent, ChildContentItemId = childItem.Id });
        db.AddRange(field, item, root, childItem, child); await db.SaveChangesAsync(Ct);
        var output = await new ContentVersionDetailProjector(db).ProjectAsync(root, _at, true, Ct);
        Assert.Null(Assert.Single(output.Fields).Child);
        child.EffectiveStartAt = null; child.EffectiveEndAt = null; child.Status = ContentStatus.Archived; await db.SaveChangesAsync(Ct);
        output = await new ContentVersionDetailProjector(db).ProjectAsync(root, _at, true, Ct);
        Assert.Null(Assert.Single(output.Fields).Child);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MostSpecificRangeAndFieldOrderingRemainStable(bool sqlite)
    {
        await using var f = await ContentListQueryFixtures.Create(sqlite); await using var db = f.Context();
        var earlier = new TemplateField { TemplateVersionId = f.TemplateVersion.Id, Key = "early", Label = "Early", Order = 1, PrimitiveType = PrimitiveType.Text };
        var later = new TemplateField { TemplateVersionId = f.TemplateVersion.Id, Key = "child", Label = "Child", Order = 2, TemplateId = f.TemplateVersion.TemplateId };
        var item = f.Item("root"); var root = f.Version(item, 1); var childItem = f.Item("winner");
        var fallback = f.Version(childItem, 9); fallback.PublishedAt = _at.AddDays(1);
        var wide = f.Version(childItem, 3, _at.AddDays(-3), _at.AddDays(3)); wide.PublishedAt = _at;
        var narrow = f.Version(childItem, 2, _at.AddHours(-1), _at.AddHours(1)); narrow.PublishedAt = _at.AddDays(-1);
        root.FieldValues.Add(new() { ContentVersionId = root.Id, FieldId = later.Id, Order = 0, ValueKind = ValueKind.ChildContent, ChildContentItemId = childItem.Id });
        root.FieldValues.Add(new() { ContentVersionId = root.Id, FieldId = earlier.Id, Order = 7, ValueKind = ValueKind.Text, TextValue = "second" });
        root.FieldValues.Add(new() { ContentVersionId = root.Id, FieldId = earlier.Id, Order = 3, ValueKind = ValueKind.Text, TextValue = "first" });
        db.AddRange(earlier, later, item, root, childItem, fallback, wide, narrow); await db.SaveChangesAsync(Ct);
        var output = await new ContentVersionDetailProjector(db).ProjectAsync(root, _at, true, Ct);
        Assert.Equal(new[] { "early", "early", "child" }, output.Fields.Select(x => x.Key));
        Assert.Equal(new[] { "first", "second" }, output.Fields.Take(2).Select(x => x.TextValue));
        Assert.Equal(narrow.Id, output.Fields[2].Child!.Id);
        output = await new ContentVersionDetailProjector(db).ProjectAsync(root, _at.AddHours(1), true, Ct);
        Assert.Equal(wide.Id, output.Fields[2].Child!.Id);
        output = await new ContentVersionDetailProjector(db).ProjectAsync(root, _at.AddDays(3), true, Ct);
        Assert.Equal(fallback.Id, output.Fields[2].Child!.Id);
    }

    private sealed class Probe : DbCommandInterceptor
    {
        public int Count { get; set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Count++; return ValueTask.FromResult(result); }
    }
}
