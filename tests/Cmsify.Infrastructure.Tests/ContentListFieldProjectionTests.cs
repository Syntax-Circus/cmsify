using System.Data.Common;
using Cmsify.Core.ContentQueries;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Persistence.ContentQueries;
using Cmsify.Infrastructure.Sqlite.Persistence.ContentQueries;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Cmsify.Infrastructure.Tests;

public sealed class ContentListFieldProjectionTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestedFieldsAreBatchLoadedForServingAndResolvedVersions(bool sqlite)
    {
        await using var f = await ContentListQueryFixtures.Create(sqlite); await using var seed = f.Context();
        var title = new TemplateField { TemplateVersionId = f.TemplateVersion.Id, Key = "title", Label = "Title", Order = 0, PrimitiveType = PrimitiveType.Text };
        var summary = new TemplateField { TemplateVersionId = f.TemplateVersion.Id, Key = "summary", Label = "Summary", Order = 1, PrimitiveType = PrimitiveType.Text };
        var body = new TemplateField { TemplateVersionId = f.TemplateVersion.Id, Key = "body", Label = "Body", Order = 2, PrimitiveType = PrimitiveType.Text };
        var a = f.Item("a"); var b = f.Item("b"); var draftOnly = f.Item("c");
        var va = f.Version(a, 1); va.PublishedAt = At; var vb = f.Version(b, 1); vb.PublishedAt = At.AddDays(-1);
        var vc = f.Version(draftOnly, 1); vc.Status = ContentStatus.Draft;
        seed.AddRange(title, summary, body, a, b, draftOnly, va, vb, vc);
        foreach (var (version, prefix) in new[] { (va, "A"), (vb, "B"), (vc, "C") })
            seed.AddRange(Value(version, title, $"{prefix} title"), Value(version, summary, $"{prefix} summary"), Value(version, body, $"{prefix} body"));
        await seed.SaveChangesAsync(Ct);

        foreach (var resolve in new[] { false, true })
        {
            var request = new ListContentRequest(f.Workspace.Id, Resolve: resolve, SortBy: "slug", SortDesc: false);
            var (page, commands) = await Read(f, request with { Fields = "title,summary" }, ["title", "summary"]);
            var (plain, plainCommands) = await Read(f, request, null);
            Assert.All(plain.Items, item => Assert.Null(item.Fields));
            Assert.Equal(plainCommands + 1, commands);
            var byslug = page.Items.ToDictionary(item => item.Slug!);
            Assert.Equal(["A title", "A summary"], byslug["a"].Fields!.Select(x => x.TextValue));
            Assert.Equal(["title", "summary"], byslug["b"].Fields!.Select(x => x.Key));
            Assert.DoesNotContain(byslug.Values.SelectMany(i => i.Fields!), x => x.Key == "body");
            if (!resolve)
            {
                Assert.Empty(byslug["c"].Fields!);
            }
        }

        var (unknown, _) = await Read(f, new ListContentRequest(f.Workspace.Id), ["nope"]);
        Assert.All(unknown.Items, item => Assert.Empty(item.Fields!));
    }

    private static ContentVersionFieldValue Value(ContentVersion version, TemplateField field, string text) => new()
    { ContentVersionId = version.Id, FieldId = field.Id, ValueKind = ValueKind.Text, TextValue = text };

    private static async Task<(ContentListPage Page, int Commands)> Read(ContentListQueryFixtures f, ListContentRequest r, string[]? keys)
    {
        var probe = new CommandCounter();
        await using var db = new CmsifyDbContext(new DbContextOptionsBuilder<CmsifyDbContext>(f.Options).AddInterceptors(probe).Options);
        IContentListQueryRepository repository = f.Sqlite ? new SqliteContentListQueryRepository(db) : new PostgresContentListQueryRepository(db);
        var criteria = new ContentListCriteria(r, At, 0, keys);
        var page = r.Resolve ? await repository.ListResolvedAsync(criteria, Ct) : await repository.ListItemsAsync(criteria, Ct);
        return (page, probe.Count);
    }

    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int Count { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        { Count++; return base.ReaderExecutingAsync(command, data, result, ct); }
    }
}
