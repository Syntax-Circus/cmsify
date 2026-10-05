using System.Data.Common;
using System.Reflection;
using Cmsify.Api.Controllers;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Testcontainers.PostgreSql;
using ContentVersionDetailResponse = SyntaxCircus.Cmsify.Contracts.ContentVersionDetailResponse;

namespace Cmsify.Api.Integration.Tests;

public sealed class ContentVersionProjectionBaselineTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(12)]
    public async Task FixedDepthIncreasingSiblingsRetainsTenQueries(int siblings)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("projection_baseline").WithUsername("cmsify").WithPassword("cmsify").Build();
        await postgres.StartAsync(ct);
        var probe = new Probe();
        var options = new DbContextOptionsBuilder<CmsifyDbContext>().UseNpgsql(postgres.GetConnectionString())
            .UseSnakeCaseNamingConvention().AddInterceptors(probe).Options;
        await using var db = new CmsifyDbContext(options);
        await db.Database.MigrateAsync(ct);
        var workspace = new Workspace { Name = "Baseline", Slug = "baseline" };
        var template = new Template { WorkspaceId = workspace.Id, Name = "Node", Slug = "node" };
        var tv = new TemplateVersion { TemplateId = template.Id, VersionNumber = 1 };
        var field = new TemplateField { TemplateVersionId = tv.Id, Key = "child", Label = "Child", TemplateId = template.Id };
        db.AddRange(workspace, template, tv, field);
        ContentVersion Node(string slug)
        {
            var item = new ContentItem { WorkspaceId = workspace.Id, TemplateVersionId = tv.Id, Slug = slug };
            var version = new ContentVersion { ContentItemId = item.Id, WorkspaceId = workspace.Id,
                TemplateVersionId = tv.Id, VersionNumber = 1, Status = ContentStatus.Published };
            db.AddRange(item, version);
            return version;
        }
        var root = Node("root");
        for (var sibling = 0; sibling < siblings; sibling++)
        {
            var parent = root;
            for (var depth = 1; depth <= 10; depth++)
            {
                var child = Node($"node-{sibling}-{depth}");
                parent.FieldValues.Add(new ContentVersionFieldValue { ContentVersionId = parent.Id, FieldId = field.Id,
                    Order = sibling, ValueKind = Cmsify.Core.Domain.Enums.ValueKind.ChildContent, ChildContentItemId = child.ContentItemId });
                parent = child;
            }
        }
        await db.SaveChangesAsync(ct);
        probe.Count = 0;
        var controller = new ContentController(db, null!, null!, null!, null!, null!, null!, null!);
        var method = typeof(ContentController).GetMethod("ToVersionDetailResponseAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var output = await (Task<ContentVersionDetailResponse>)method.Invoke(controller,
            [root, new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero), true, ct])!;
        var baselineQueryCount = 10;
        Assert.Equal(baselineQueryCount, probe.Count);
        Assert.Equal(siblings, output.Fields.Count);
        var node = output;
        for (var depth = 0; depth < 8; depth++) node = Assert.IsType<ContentVersionDetailResponse>(node.Fields[0].Child);
        Assert.Null(node.Fields[0].Child);
        TestContext.Current.TestOutputHelper?.WriteLine($"BASELINE depth=8 siblings={siblings} queries={probe.Count}");
        var deletedChildId = root.FieldValues[0].ChildContentItemId!.Value;
        var deletedChild = await db.ContentItems.SingleAsync(x => x.Id == deletedChildId, ct);
        deletedChild.IsDeleted = true;
        await db.SaveChangesAsync(ct);
        Assert.True(await db.ContentItems.IgnoreQueryFilters().AnyAsync(x => x.Id == deletedChildId && x.IsDeleted, ct));
        output = await (Task<ContentVersionDetailResponse>)method.Invoke(controller,
            [root, new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero), true, ct])!;
        // Existing global filtering also affects the nested IsDeleted subquery. Characterize
        // the resulting published-child resolution; changing it belongs to a separate fix.
        Assert.Equal(deletedChildId, output.Fields[0].Child!.ContentItemId);
    }

    private sealed class Probe : DbCommandInterceptor
    {
        public int Count { get; set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Count++;
            return ValueTask.FromResult(result);
        }
    }
}
