using System.Data.Common;
using System.Globalization;
using Cmsify.Core.ContentQueries;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Persistence.ContentQueries;
using Cmsify.Infrastructure.Sqlite.Persistence.ContentQueries;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Cmsify.Infrastructure.Tests;

public sealed class ContentListQueryRepositoryTests
{
    static ContentListQueryRepositoryTests()
    {
        if (Environment.GetEnvironmentVariable("CMSIFY_CONTENT_QUERY_CULTURE") is { Length: > 0 } culture)
        {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo(culture);
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo(culture);
        }
    }
    private static readonly DateTimeOffset At = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentItemAndSnapshotFiltersAndCompleteSummary(bool sqlite)
    {
        await using var f = await ContentListQueryFixtures.Create(sqlite); await using var seed = f.Context();
        var template = new Template { WorkspaceId = f.Workspace.Id, Name = "Alternate", Slug = "alternate" };
        var tv = new TemplateVersion { TemplateId = template.Id, VersionNumber = 1 };
        var group = Guid.NewGuid();
        var owner = f.Item("live"); owner.LocaleCode = "en"; owner.TranslationGroupId = group;
        owner.CreatedAt = At; owner.UpdatedAt = At.AddDays(1);
        var other = f.Item("other"); other.TemplateVersionId = tv.Id; other.LocaleCode = "fr";
        other.CreatedAt = At.AddDays(-1);
        var snapshot = f.Version(owner, 1); snapshot.TemplateVersionId = tv.Id; snapshot.Slug = "snapshot"; snapshot.LocaleCode = "de";
        snapshot.TranslationGroupId = group; snapshot.Tags = ["alpha", "zebra"]; snapshot.PublishedAt = At;
        snapshot.PublishAt = At.AddDays(-1); snapshot.ArchivedAt = At.AddDays(1); snapshot.RolledBackFromVersionNumber = 7;
        snapshot.CreatedAt = At.AddDays(-2); snapshot.UpdatedAt = At.AddDays(-1);
        var before = f.Version(owner, 2); before.Status = ContentStatus.Archived; before.PublishedAt = At.AddDays(-3);
        var after = f.Version(owner, 3); after.Status = ContentStatus.Draft; after.PublishedAt = At.AddDays(3);
        var wrong = f.Version(other, 1); wrong.Tags = ["alpha"]; wrong.LocaleCode = "fr"; wrong.PublishedAt = At.AddDays(-4);
        var alpha = new Tag { WorkspaceId = f.Workspace.Id, Name = "alpha" };
        var zebra = new Tag { WorkspaceId = f.Workspace.Id, Name = "zebra" };
        seed.AddRange(template, tv, owner, other, snapshot, before, after, wrong, alpha, zebra);
        seed.AddRange(new ContentItemTag { ContentItemId = owner.Id, TagId = alpha.Id },
            new ContentItemTag { ContentItemId = owner.Id, TagId = zebra.Id }, new ContentItemTag { ContentItemId = other.Id, TagId = alpha.Id });
        await seed.SaveChangesAsync(Ct);
        var r = new ListContentRequest(f.Workspace.Id);
        foreach (var vector in new[] {
            r with { TemplateVersionId = f.TemplateVersion.Id }, r with { TemplateId = f.TemplateVersion.TemplateId },
            r with { LocaleCode = "en" }, r with { TranslationGroupId = group }, r with { Slug = "live" },
            r with { Tags = " ZEBRA,alpha,alpha " }, r with { Status = ContentStatus.Draft },
            r with { PublishedAfter = At.AddDays(3) }, r with { PublishedBefore = At.AddDays(-3), Slug = "live" },
            r with { CreatedAfter = At }, r with { CreatedBefore = At, Slug = "live" },
            r with { Status = ContentStatus.Draft, PublishedAfter = At.AddDays(3), PublishedBefore = At.AddDays(-3) } })
            await Check(f, vector, [owner.Id], 1);
        foreach (var vector in new[] {
            r with { Resolve = true, TemplateVersionId = tv.Id }, r with { Resolve = true, TemplateId = template.Id },
            r with { Resolve = true, LocaleCode = "de" }, r with { Resolve = true, TranslationGroupId = group },
            r with { Resolve = true, Slug = "snapshot" }, r with { Resolve = true, Tags = " ZEBRA,alpha,alpha,, " },
            r with { Resolve = true, PublishedAfter = At }, r with { Resolve = true, PublishedBefore = At, Slug = "snapshot" },
            r with { Resolve = true, CreatedAfter = At.AddYears(2), CreatedBefore = At.AddYears(-2), Slug = "snapshot" } })
            await Check(f, vector, [owner.Id], 1);
        var item = Assert.Single((await Read(f, r with { Slug = "live" })).Items);
        Assert.Equal("Article", item.TemplateName); Assert.Equal("article", item.TemplateSlug);
        Assert.Equal(At, item.CreatedAt); Assert.Equal(At.AddDays(1), item.UpdatedAt); Assert.Equal(3, item.VersionCount);
        Assert.Equal(new[] { "alpha", "zebra" }, item.Tags);
        var serving = Assert.IsType<ContentListVersionOutput>(item.CurrentlyServingVersion);
        Assert.Equal(new ContentListVersionOutput(snapshot.Id, owner.Id, 1, ContentStatus.Published, tv.Id, "snapshot", "de",
            null, null, snapshot.PublishAt, At, snapshot.ArchivedAt, null, 7, ["alpha", "zebra"], snapshot.CreatedAt, snapshot.UpdatedAt) with { Tags = serving.Tags }, serving);
        Assert.Equal(snapshot.Tags, serving.Tags);
        item = Assert.Single((await Read(f, r with { Resolve = true, Slug = "snapshot" })).Items);
        Assert.Equal("Alternate", item.TemplateName); Assert.Equal("alternate", item.TemplateSlug); Assert.Equal("snapshot", item.Slug);
        Assert.Equal("de", item.LocaleCode); Assert.Equal(group, item.TranslationGroupId); Assert.Equal(snapshot.Tags, item.Tags);
        Assert.Equal(At, item.CreatedAt); Assert.Equal(At, item.UpdatedAt); Assert.Equal(1, item.VersionCount); Assert.Null(item.CurrentlyServingVersion);
        await Check(f, r with { WorkspaceId = Guid.NewGuid() }, [], 0);
        await Check(f, r with { WorkspaceId = Guid.NewGuid(), Resolve = true }, [], 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CandidateFiltersPrecedeRankingAndQFollowsWinner(bool sqlite)
    {
        await using var f = await ContentListQueryFixtures.Create(sqlite); await using var seed = f.Context();
        var template = new Template { WorkspaceId = f.Workspace.Id, Name = "Alternate", Slug = "alternate" };
        var tv = new TemplateVersion { TemplateId = template.Id, VersionNumber = 1 }; var group = Guid.NewGuid();
        var owner = f.Item("owner"); var fallback = f.Version(owner, 1); fallback.TemplateVersionId = tv.Id;
        fallback.Slug = "needle"; fallback.LocaleCode = "de"; fallback.TranslationGroupId = group;
        fallback.Tags = ["zebra"]; fallback.PublishedAt = At.AddDays(-1);
        var winner = f.Version(owner, 2, At.AddHours(-1), At.AddHours(1)); winner.Slug = "winner";
        winner.LocaleCode = "en"; winner.Tags = ["alpha"]; winner.PublishedAt = At;
        seed.AddRange(template, tv, owner, fallback, winner); await seed.SaveChangesAsync(Ct);
        var r = new ListContentRequest(f.Workspace.Id, Resolve: true);
        foreach (var vector in new[] { r with { TemplateVersionId = tv.Id }, r with { TemplateId = template.Id },
            r with { LocaleCode = "de" }, r with { TranslationGroupId = group }, r with { Slug = "needle" },
            r with { Tags = "zebra" }, r with { PublishedBefore = At.AddDays(-1) } })
            Assert.Equal("needle", Assert.Single((await Read(f, vector)).Items).Slug);
        await Check(f, r with { Q = "needle" }, [], 0); await Check(f, r with { Q = "winner" }, [owner.Id], 1);
        await Check(f, r with { Status = ContentStatus.Archived }, [], 0, commands: 0);
        // The better bounded candidate is older. Applying PublishedAfter only after
        // ranking would discard it without selecting the newer default fallback.
        winner.PublishedAt = At.AddDays(-1); fallback.PublishedAt = At;
        await seed.SaveChangesAsync(Ct);
        Assert.Equal("winner", Assert.Single((await Read(f, r)).Items).Slug);
        var probe = new Probe();
        var filtered = await Read(f, r with { PublishedAfter = At }, probe: probe);
        Assert.Equal("needle", Assert.Single(filtered.Items).Slug);
        Assert.Equal(owner.Id, filtered.Items[0].Id); Assert.Equal(1, filtered.TotalCount);
        Assert.Equal(2, probe.Commands.Count); Assert.Equal(0, probe.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RankDiscriminatorsAndBoundaryUseExactMappedPrecision(bool sqlite)
    {
        await using var f = await ContentListQueryFixtures.Create(sqlite); await using var seed = f.Context();
        var expected = new List<(ContentItem Owner, Guid Winner, string Slug, string WinnerSlug)>();
        void Pair(string slug, Action<ContentVersion, ContentVersion> configure, bool second)
        {
            var owner = f.Item(slug); var a = f.Version(owner, 1, At.AddHours(-1), At.AddHours(1));
            var b = f.Version(owner, 2, At.AddHours(-1), At.AddHours(1));
            a.PublishedAt = b.PublishedAt = At; a.Slug = slug + "-a"; b.Slug = slug + "-b";
            configure(a, b); seed.AddRange(owner, a, b);
            expected.Add((owner, second ? b.Id : a.Id, slug, second ? b.Slug : a.Slug));
        }
        Pair("bounded", (a,b) => { a.EffectiveStartAt = a.EffectiveEndAt = null; b.PublishedAt = At.AddDays(-1); }, true);
        Pair("duration", (a,b) => { b.EffectiveStartAt = At.AddHours(-2); b.EffectiveEndAt = At.AddHours(2); }, false);
        Pair("publication", (a,b) => { a.PublishedAt = At.AddDays(1); }, false);
        Pair("version", (a,b) => { }, true);
        Pair("null", (a,b) => { b.PublishedAt = null; }, false);
        Pair("ticks", (a,b) => { a.EffectiveStartAt = b.EffectiveStartAt = At; a.EffectiveEndAt = At.AddTicks(100); b.EffectiveEndAt = At.AddTicks(110); }, false);
        Pair("literal", (a,b) => { a.EffectiveStartAt = b.EffectiveStartAt = At; a.EffectiveEndAt = At.AddTicks(10); b.EffectiveEndAt = At.AddTicks(11); }, !sqlite);
        Pair("extreme", (a,b) => { a.EffectiveStartAt = b.EffectiveStartAt = new(2,1,1,0,0,0,TimeSpan.Zero);
            a.EffectiveEndAt = new(9998,1,1,0,0,0,TimeSpan.Zero); b.EffectiveEndAt = a.EffectiveEndAt.Value.AddTicks(100); }, false);
        await seed.SaveChangesAsync(Ct);
        foreach (var vector in expected)
        {
            var r = new ListContentRequest(f.Workspace.Id, Slug: vector.Slug);
            Assert.Equal(vector.Winner, Assert.Single((await Read(f, r)).Items).CurrentlyServingVersion!.Id);
            var resolved = (await Read(f, r with { Resolve = true, Slug = null })).Items.Single(x => x.Id == vector.Owner.Id);
            Assert.Equal(vector.WinnerSlug, resolved.Slug);
        }
        var owner = f.Item("boundary"); var version = f.Version(owner, 1, At, At.AddHours(1));
        seed.AddRange(owner, version); await seed.SaveChangesAsync(Ct);
        var boundary = new ListContentRequest(f.Workspace.Id, Resolve: true, Slug: "boundary");
        Assert.Single((await Read(f, boundary, at: At.ToOffset(TimeSpan.FromHours(5)))).Items);
        Assert.Empty((await Read(f, boundary, at: At.AddHours(1))).Items);
        Assert.Empty((await Read(f, boundary, at: At.AddTicks(-100))).Items);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TextAndExactSnapshotMembershipSurviveCultureAndReopen(bool sqlite)
    {
        await using var f = await ContentListQueryFixtures.Create(sqlite); await using var seed = f.Context();
        var texts = new[] { "a","A","café","CAFÉ","I","i","İ","ı","ß","SS","é","e\u0301","😀",
            "percent%value","percentXvalue","under_score","underXscore",@"back\slash","backslash","quote'value" };
        var owners = texts.Select(f.Item).ToArray();
        foreach (var owner in owners)
        {
            var v = f.Version(owner, 1); v.PublishedAt = At;
            v.Tags = owner.Slug == "a" ? ["a'\\%_", "café", "x'); drop table content_items; --", "alpha", "zebra"]
                : owner.Slug == "A" ? ["alpha"] : owner.Slug == "I" ? ["zebra"] : ["alphabet"];
            seed.AddRange(owner, v);
        }
        var plain = f.Item("plain"); var draft = f.Version(plain, 1); draft.Status = ContentStatus.Draft;
        draft.FieldValues.Add(new() { ContentVersionId = draft.Id, FieldId = Guid.NewGuid(), TextValue = "fieldneedle" });
        seed.AddRange(plain, draft); await seed.SaveChangesAsync(Ct);
        var vectors = new (string Q, string[] Slugs)[] {
            ("café", sqlite ? ["café"] : ["café","CAFÉ"]), ("I", sqlite ? ["I","i"] : ["I","i","İ"]),
            ("İ", sqlite ? ["İ"] : ["I","i","İ"]), ("ı",["ı"]), ("ß",["ß"]), ("SS",["SS"]),
            ("e\u0301",["e\u0301"]), ("😀",["😀"]), ("quote'value",["quote'value"]), (@"back\slash", [@"back\slash"]),
            ("x'); DROP TABLE content_items; --",[]) };
        foreach (var culture in new[] { "en-US", "tr-TR" })
        {
            var prior = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                foreach (var vector in vectors)
                foreach (var resolved in new[] { false, true })
                {
                    var page = await Read(f, new(f.Workspace.Id, Q: vector.Q, Resolve: resolved, PageSize: 100));
                    var expected = vector.Slugs.Concat(!resolved && (vector.Q == "I" || (!sqlite && vector.Q == "İ")) ? ["plain"] : []);
                    Assert.Equal(expected.Order(StringComparer.Ordinal), page.Items.Select(x => x.Slug!).Order(StringComparer.Ordinal));
                    Assert.Equal(page.Items.Count, page.TotalCount);
                }
            }
            finally { CultureInfo.CurrentCulture = prior; }
        }
        foreach (var (q, literal, wildcard) in new[] { ("percent%value","percent%value","percentXvalue"), ("under_score","under_score","underXscore") })
        {
            Assert.Equal(new[] { literal,wildcard }.Order(), (await Read(f,new(f.Workspace.Id,Q:q))).Items.Select(x=>x.Slug!).Order());
            Assert.Equal(literal,Assert.Single((await Read(f,new(f.Workspace.Id,Q:q,Resolve:true))).Items).Slug);
        }
        await Check(f,new(f.Workspace.Id,Q:"fieldneedle"),[plain.Id],1); await Check(f,new(f.Workspace.Id,Q:"fieldneedle",Resolve:true),[],0);
        foreach (var tag in new[] { "a'\\%_", "café", "x'); DROP TABLE content_items; --", "alpha,zebra", " ALPHA,zebra,alpha,, " })
            await Check(f,new(f.Workspace.Id,Resolve:true,Tags:tag),[owners[0].Id],1);
        await Check(f,new(f.Workspace.Id,Resolve:true,Tags:"alpha"),[owners[0].Id,owners[1].Id],2,unordered:true);
        await Check(f,new(f.Workspace.Id,Resolve:true,Tags:"zebra"),[owners[0].Id,owners[4].Id],2,unordered:true);
        await Check(f,new(f.Workspace.Id,Q:"   ",PageSize:100),owners.Select(x=>x.Id).Append(plain.Id).ToArray(),21,unordered:true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeletedReferencesRetainOrdinaryErrorAndResolvedCountQuirk(bool sqlite)
    {
        await using var f = await ContentListQueryFixtures.Create(sqlite); await using var seed = f.Context();
        var live=f.Item("live"); var deleted=f.Item("deleted"); deleted.IsDeleted=true;
        var tag=new Tag { WorkspaceId=f.Workspace.Id,Name="deleted-tag",IsDeleted=true };
        seed.AddRange(live,deleted,f.Version(live,1),f.Version(deleted,1),tag,new ContentItemTag { ContentItemId=live.Id,TagId=tag.Id });
        await seed.SaveChangesAsync(Ct);
        await Check(f,new(f.Workspace.Id),[live.Id],1); await Check(f,new(f.Workspace.Id,Resolve:true),[live.Id],1);
        await Check(f,new(f.Workspace.Id,Tags:"deleted-tag"),[],0);
        Assert.Empty(Assert.Single((await Read(f,new(f.Workspace.Id))).Items).Tags);
        var reference = await seed.TemplateVersions.IgnoreQueryFilters().SingleAsync(x => x.Id == f.TemplateVersion.Id, Ct);
        foreach(var deleteTemplate in new[] { false,true })
        {
            reference.IsDeleted=!deleteTemplate;
            var template=await seed.Templates.IgnoreQueryFilters().SingleAsync(x=>x.Id==f.TemplateVersion.TemplateId,Ct);
            template.IsDeleted=deleteTemplate; await seed.SaveChangesAsync(Ct);
            Assert.Equal("Sequence contains no elements.",(await Assert.ThrowsAsync<InvalidOperationException>(()=>Read(f,new(f.Workspace.Id)))).Message);
            await Check(f,new(f.Workspace.Id,Resolve:true),[],1);
        }
    }

    [Theory]
    [InlineData(false,1)]
    [InlineData(false,100)]
    [InlineData(false,1000)]
    [InlineData(true,1)]
    [InlineData(true,100)]
    [InlineData(true,1000)]
    public async Task BudgetsStayFixedAcrossPageAndHistory(bool sqlite,int history)
    {
        await using var f=await ContentListQueryFixtures.Create(sqlite); await using var seed=f.Context();
        foreach(var owner in Enumerable.Range(0,100).Select(i=>f.Item($"owner-{i:000}")))
        {
            seed.Add(owner);
            for(var n=1;n<=history;n++) { var v=f.Version(owner,n); v.Status=n==1?ContentStatus.Published:ContentStatus.Archived;
                v.PublishedAt=At; seed.Add(v); }
        }
        await seed.SaveChangesAsync(Ct);
        foreach(var resolved in new[] { false,true })
        {
            var small=new Probe(); var large=new Probe(); var r=new ListContentRequest(f.Workspace.Id,Resolve:resolved,PageSize:1);
            var one=await Read(f,r,probe:small,measurementHistory:history);
            var hundred=await Read(f,r with { PageSize=100 },probe:large,measurementHistory:history);
            Assert.Single(one.Items); Assert.Equal(100,hundred.Items.Count); Assert.Equal(100,one.TotalCount); Assert.Equal(100,hundred.TotalCount);
            Assert.Equal(small.Commands.Count,large.Commands.Count); Assert.Equal(resolved?2:4,large.Commands.Count);
            Assert.Equal(0,small.Writes); Assert.Equal(0,large.Writes); Assert.Empty(large.Tracked);
            Assert.Equal(1,small.VersionRows); Assert.Equal(100,large.VersionRows);
            if(!resolved) Assert.All(hundred.Items,item=>Assert.Equal(history,item.VersionCount));
            await Check(f,r,[],100,commands:1,offset:null);
            Assert.Empty((await Read(f,r,offset:100)).Items);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicationBetweenStatementsCanChangeCountWithoutDuplicatesOrWrites(bool sqlite)
    {
        await using var f=await ContentListQueryFixtures.Create(sqlite); await using var seed=f.Context();
        var first=f.Item("first"); var v=f.Version(first,1); v.PublishedAt=At; seed.AddRange(first,v); await seed.SaveChangesAsync(Ct);
        var probe=new Probe { AfterCount=async()=> { await using var writer=f.Context(); var second=f.Item("second"); var pub=f.Version(second,1);
            pub.PublishedAt=At; writer.AddRange(second,pub); await writer.SaveChangesAsync(Ct); } };
        var page=await Read(f,new(f.Workspace.Id,Resolve:true),probe:probe);
        Assert.Equal(1,page.TotalCount); Assert.Equal(2,page.Items.Count); Assert.Equal(2,page.Items.Select(x=>x.Id).Distinct().Count());
        Assert.Equal(2,probe.Commands.Count); Assert.Equal(0,probe.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SortNullPlacementPagingAndBlankFiltersRetainModeRules(bool sqlite)
    {
        await using var f = await ContentListQueryFixtures.Create(sqlite); await using var seed = f.Context();
        var a=f.Item("a"); var b=f.Item("b"); var n=f.Item("null-owner");
        a.CreatedAt=At; b.CreatedAt=At.AddDays(1); n.CreatedAt=At.AddDays(2);
        a.UpdatedAt=At.AddDays(2); b.UpdatedAt=At.AddDays(1); n.UpdatedAt=At;
        var va=f.Version(a,1); va.PublishedAt=At.AddDays(2);
        var vb=f.Version(b,1); vb.PublishedAt=At.AddDays(1);
        var vn=f.Version(n,1); vn.PublishedAt=At; vn.Slug=null;
        seed.AddRange(a,b,n,va,vb,vn); await seed.SaveChangesAsync(Ct);
        var r=new ListContentRequest(f.Workspace.Id,PageSize:100);
        await Check(f,r with { SortBy="createdAt",SortDesc=false },[a.Id,b.Id,n.Id],3);
        await Check(f,r with { SortBy="createdAt" },[n.Id,b.Id,a.Id],3);
        await Check(f,r with { SortBy="updatedAt",SortDesc=false },[n.Id,b.Id,a.Id],3);
        await Check(f,r with { SortBy="updatedAt" },[a.Id,b.Id,n.Id],3);
        await Check(f,r with { SortBy="slug",SortDesc=false },[a.Id,b.Id,n.Id],3);
        await Check(f,r with { SortBy="slug" },[n.Id,b.Id,a.Id],3);
        await Check(f,r with { SortBy="x'); DROP TABLE content_items; --",SortDesc=false },[a.Id,b.Id,n.Id],3);
        r=r with { Resolve=true };
        await Check(f,r with { SortBy="slug",SortDesc=false },[n.Id,a.Id,b.Id],3);
        await Check(f,r with { SortBy="slug" },[b.Id,a.Id,n.Id],3);
        await Check(f,r with { SortBy="updatedAt",SortDesc=false },[n.Id,b.Id,a.Id],3);
        await Check(f,r with { SortBy="createdAt" },[a.Id,b.Id,n.Id],3);
        await Check(f,r with { SortBy="slug",SortDesc=false,PageSize=1,Page=2 },[a.Id],3,offset:1);
        await Check(f,r with { Q=" ",Slug=" ",LocaleCode=" ",Tags=" ,,, " },[a.Id,b.Id,n.Id],3);
        vb.PublishedAt=va.PublishedAt; await seed.SaveChangesAsync(Ct);
        var ties=(await Read(f,r with { SortBy="createdAt" })).Items.Take(2).Select(x=>x.Id).ToArray();
        // Native GUID representation has the same UUID byte order for these generated IDs.
        Assert.Equal(new[] { a.Id,b.Id }.Order().ToArray(),ties);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParameterValuesRemainDataAndAmbientTransactionOwnsConnection(bool sqlite)
    {
        await using var f=await ContentListQueryFixtures.Create(sqlite); await using var seed=f.Context();
        const string injection="x'); drop table content_items; --";
        var owner=f.Item(injection); owner.LocaleCode=injection[..15];
        var version=f.Version(owner,1); version.Tags=[injection]; version.PublishedAt=At;
        version.LocaleCode=owner.LocaleCode;
        seed.AddRange(owner,version); await seed.SaveChangesAsync(Ct);
        foreach(var resolved in new[] { false,true })
        {
            var probe=new Probe();
            await using var db=new CmsifyDbContext(new DbContextOptionsBuilder<CmsifyDbContext>(f.Options).AddInterceptors(probe).Options);
            await using var transaction=await db.Database.BeginTransactionAsync(Ct);
            IContentListQueryRepository repository=sqlite?new SqliteContentListQueryRepository(db):new PostgresContentListQueryRepository(db);
            var r=new ListContentRequest(f.Workspace.Id,Q:injection,Slug:injection,LocaleCode:owner.LocaleCode,Resolve:resolved,Tags:resolved?injection:null);
            var c=new ContentListCriteria(r,At,0);
            var page=resolved?await repository.ListResolvedAsync(c,Ct):await repository.ListItemsAsync(c,Ct);
            Assert.Equal(owner.Id,Assert.Single(page.Items).Id);
            Assert.Equal(0,probe.Writes); Assert.NotEmpty(probe.Values);
            Assert.All(probe.Commands,sql=>Assert.DoesNotContain(injection,sql,StringComparison.Ordinal));
            Assert.Contains(probe.Values,x=>x is string text&&text.Contains(injection,StringComparison.Ordinal)
                || x is string[] tags&&tags.Contains(injection));
            Assert.Same(transaction,db.Database.CurrentTransaction);
            Assert.Equal(System.Data.ConnectionState.Open,db.Database.GetDbConnection().State);
            await transaction.RollbackAsync(Ct);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EachDateBoundAndRequiredLiveTagHasAnIndependentDiscriminator(bool sqlite)
    {
        await using var f=await ContentListQueryFixtures.Create(sqlite); await using var seed=f.Context();
        var a=f.Item("a"); var b=f.Item("b"); var both=f.Item("both");
        a.CreatedAt=At.AddDays(-1); b.CreatedAt=At.AddDays(1); both.CreatedAt=At;
        var va=f.Version(a,1); va.PublishedAt=At.AddDays(-1);
        var vb=f.Version(b,1); vb.PublishedAt=At.AddDays(1);
        var vc=f.Version(both,1); vc.PublishedAt=At;
        va.Tags=["alpha"]; vb.Tags=["zebra"]; vc.Tags=["", "alpha", "alpha", "zebra"];
        var alpha=new Tag { WorkspaceId=f.Workspace.Id,Name="alpha" }; var zebra=new Tag { WorkspaceId=f.Workspace.Id,Name="zebra" };
        seed.AddRange(a,b,both,va,vb,vc,alpha,zebra);
        seed.AddRange(new ContentItemTag { ContentItemId=a.Id,TagId=alpha.Id },new ContentItemTag { ContentItemId=b.Id,TagId=zebra.Id },
            new ContentItemTag { ContentItemId=both.Id,TagId=alpha.Id },new ContentItemTag { ContentItemId=both.Id,TagId=zebra.Id });
        await seed.SaveChangesAsync(Ct);
        var r=new ListContentRequest(f.Workspace.Id);
        await Check(f,r with { CreatedAfter=At.AddDays(1) },[b.Id],1);
        await Check(f,r with { CreatedBefore=At.AddDays(-1) },[a.Id],1);
        foreach(var resolved in new[] { false,true })
        {
            r=r with { Resolve=resolved };
            await Check(f,r with { PublishedAfter=At.AddDays(1) },[b.Id],1);
            await Check(f,r with { PublishedBefore=At.AddDays(-1) },[a.Id],1);
            await Check(f,r with { PublishedAfter=At,PublishedBefore=At },[both.Id],1);
            await Check(f,r with { Tags="alpha" },[a.Id,both.Id],2,unordered:true);
            await Check(f,r with { Tags="zebra" },[b.Id,both.Id],2,unordered:true);
            await Check(f,r with { Tags=" ALPHA,zebra,alpha " },[both.Id],1);
            await Check(f,r with { Tags="alph" },[],0);
        }
        if(sqlite)
        {
            await seed.Database.ExecuteSqlInterpolatedAsync($"UPDATE content_versions SET tags = {"[null,1,true,{\"value\":\"alpha\"},[\"alpha\"],\"alphabet\"]"} WHERE id = {va.Id.ToString().ToUpperInvariant()}",Ct);
            await Check(f,r with { Tags="alpha" },[both.Id],1);
        }
    }

    private static async Task Check(ContentListQueryFixtures f,ListContentRequest r,Guid[] ids,int total,int? commands=null,
        bool unordered=false,int? offset=0)
    {
        var probe=new Probe(); var page=await Read(f,r,offset,probe:probe);
        Assert.Equal(unordered?ids.Order().ToArray():ids,unordered?page.Items.Select(x=>x.Id).Order().ToArray():page.Items.Select(x=>x.Id).ToArray());
        Assert.Equal(total,page.TotalCount); Assert.Equal(commands??(r.Resolve?2:4),probe.Commands.Count); Assert.Equal(0,probe.Writes);
    }
    private static async Task<ContentListPage> Read(ContentListQueryFixtures f,ListContentRequest r,int? offset=0,DateTimeOffset? at=null,Probe? probe=null,
        int? measurementHistory=null)
    {
        probe??=new(); await using var db=new CmsifyDbContext(new DbContextOptionsBuilder<CmsifyDbContext>(f.Options).AddInterceptors(probe).Options);
        IContentListQueryRepository repository=f.Sqlite?new SqliteContentListQueryRepository(db):new PostgresContentListQueryRepository(db);
        var c=new ContentListCriteria(r,at??At,offset);
        // Measure only the awaited runtime repository operation: all content commands,
        // connection opens, EF translation, interception and DTO materialization.
        // Fixture/migration/seed, context/criteria construction and disposal are outside.
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var page=r.Resolve?await repository.ListResolvedAsync(c,Ct):await repository.ListItemsAsync(c,Ct);
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
        if (measurementHistory is { } history)
            TestContext.Current.TestOutputHelper?.WriteLine("MEASUREMENT: " + System.Text.Json.JsonSerializer.Serialize(new
            {
                provider = f.Sqlite ? "Sqlite" : "Postgres",
                processCulture = CultureInfo.DefaultThreadCurrentCulture?.Name ?? CultureInfo.CurrentCulture.Name,
                mode = r.Resolve ? "resolved" : "ordinary", ownerCount = 100, versionsPerOwner = history,
                pageSize = r.PageSize, offset, commands = probe.Commands.Count, returnedRows = page.Items.Count,
                versionRows = probe.VersionRows, writes = probe.Writes, elapsedMilliseconds = elapsed.TotalMilliseconds
            }));
        probe.Tracked.AddRange(db.ChangeTracker.Entries().Select(x=>x.Entity)); return page;
    }
    private sealed class Probe:DbCommandInterceptor
    {
        public List<string> Commands { get; }=[]; public List<object> Tracked { get; }=[];
        public List<object?> Values { get; } = [];
        public int VersionRows { get; private set; } public int Writes { get; private set; } public Func<Task>? AfterCount { get; set; }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData data,
            InterceptionResult<DbDataReader> result,CancellationToken ct=default)
        {
            if (AfterCount is { } action && Commands.Count == 1)
            { AfterCount = null; await action(); }
            Commands.Add(command.CommandText);
            if(command.CommandText.Contains("INSERT ",StringComparison.OrdinalIgnoreCase)||command.CommandText.Contains("UPDATE ",StringComparison.OrdinalIgnoreCase)
                ||command.CommandText.Contains("DELETE ",StringComparison.OrdinalIgnoreCase)) Writes++;
            TestContext.Current.TestOutputHelper?.WriteLine("SQL: "+command.CommandText);
            foreach(DbParameter p in command.Parameters)
            { Values.Add(p.Value); TestContext.Current.TestOutputHelper?.WriteLine($"PARAM: {p.ParameterName}={p.Value}"); }
            return result;
        }
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,CommandExecutedEventData data,
            DbDataReader result,CancellationToken ct=default)
        {
            var versionProjection = command.CommandText.Contains("winner_rank", StringComparison.Ordinal)
                && !command.CommandText.Contains("count(", StringComparison.OrdinalIgnoreCase);
            return ValueTask.FromResult<DbDataReader>(new RowCountingReader(result, () => { if (versionProjection) VersionRows++; }));
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,CommandEventData data,
            InterceptionResult<int> result,CancellationToken ct=default) { Writes++; return ValueTask.FromResult(result); }
    }
    private sealed class RowCountingReader(DbDataReader inner, Action row) : DbDataReader
    {
        public override bool Read() { var found = inner.Read(); if (found) row(); return found; }
        public override async Task<bool> ReadAsync(CancellationToken cancellationToken) { var found = await inner.ReadAsync(cancellationToken); if (found) row(); return found; }
        public override bool NextResult() => inner.NextResult();
        public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => inner.NextResultAsync(cancellationToken);
        public override int Depth => inner.Depth;
        public override int FieldCount => inner.FieldCount;
        public override bool HasRows => inner.HasRows;
        public override bool IsClosed => inner.IsClosed;
        public override int RecordsAffected => inner.RecordsAffected;
        public override object this[int ordinal] => inner[ordinal];
        public override object this[string name] => inner[name];
        public override bool GetBoolean(int ordinal) => inner.GetBoolean(ordinal);
        public override byte GetByte(int ordinal) => inner.GetByte(ordinal);
        public override long GetBytes(int ordinal, long offset, byte[]? buffer, int bufferOffset, int length) => inner.GetBytes(ordinal, offset, buffer, bufferOffset, length);
        public override char GetChar(int ordinal) => inner.GetChar(ordinal);
        public override long GetChars(int ordinal, long offset, char[]? buffer, int bufferOffset, int length) => inner.GetChars(ordinal, offset, buffer, bufferOffset, length);
        public override string GetDataTypeName(int ordinal) => inner.GetDataTypeName(ordinal);
        public override DateTime GetDateTime(int ordinal) => inner.GetDateTime(ordinal);
        public override decimal GetDecimal(int ordinal) => inner.GetDecimal(ordinal);
        public override double GetDouble(int ordinal) => inner.GetDouble(ordinal);
        public override Type GetFieldType(int ordinal) => inner.GetFieldType(ordinal);
        public override float GetFloat(int ordinal) => inner.GetFloat(ordinal);
        public override Guid GetGuid(int ordinal) => inner.GetGuid(ordinal);
        public override short GetInt16(int ordinal) => inner.GetInt16(ordinal);
        public override int GetInt32(int ordinal) => inner.GetInt32(ordinal);
        public override long GetInt64(int ordinal) => inner.GetInt64(ordinal);
        public override string GetName(int ordinal) => inner.GetName(ordinal);
        public override int GetOrdinal(string name) => inner.GetOrdinal(name);
        public override string GetString(int ordinal) => inner.GetString(ordinal);
        public override object GetValue(int ordinal) => inner.GetValue(ordinal);
        public override int GetValues(object[] values) => inner.GetValues(values);
        public override bool IsDBNull(int ordinal) => inner.IsDBNull(ordinal);
        public override T GetFieldValue<T>(int ordinal) => inner.GetFieldValue<T>(ordinal);
        public override Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken) => inner.GetFieldValueAsync<T>(ordinal, cancellationToken);
        public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken) => inner.IsDBNullAsync(ordinal, cancellationToken);
        public override System.Collections.IEnumerator GetEnumerator() => ((System.Collections.IEnumerable)inner).GetEnumerator();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
