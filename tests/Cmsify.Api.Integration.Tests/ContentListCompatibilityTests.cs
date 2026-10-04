using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Data.Common;
using System.Text.Json;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Cmsify.Api.Integration.Tests;

// Characterizes the released PostgreSQL HTTP boundary before extraction. In particular,
// ordinary filters/search deliberately have different semantics from resolved queries.
public sealed class ContentListCompatibilityTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private const string ApiToken = "cmsify_content_list_compatibility_token";
    private const string AsOf = "2026-06-15T12:00:00Z";
    private static readonly DateTimeOffset PublishedAt = DateTimeOffset.Parse("2026-06-10T00:00:00Z");
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("cmsify").WithUsername("cmsify").WithPassword("cmsify").Build();

    public ContentListCompatibilityTests(ITestOutputHelper output) => _output = output;

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        Environment.SetEnvironmentVariable("ConnectionStrings__Cmsify", _postgres.GetConnectionString());
        Environment.SetEnvironmentVariable("Seed__Admin__Email", "admin@example.test");
        Environment.SetEnvironmentVariable("Seed__Admin__Password", "change-this-temporary-password");
        Environment.SetEnvironmentVariable("Seed__DefaultWorkspace__Name", "Default");
        Environment.SetEnvironmentVariable("Seed__DefaultWorkspace__Slug", "default");
    }

    public async ValueTask DisposeAsync()
    {
        await _postgres.DisposeAsync();
        foreach (var key in new[] { "ConnectionStrings__Cmsify", "Seed__Admin__Email", "Seed__Admin__Password",
                     "Seed__DefaultWorkspace__Name", "Seed__DefaultWorkspace__Slug" })
            Environment.SetEnvironmentVariable(key, null);
    }

    [Fact]
    public async Task OrdinaryFiltersMayMatchDifferentVersions()
    {
        await using var fixture = await CreateFixtureAsync();
        var owner = Owner(fixture, "different-versions");
        var draft = Version(owner, 1, "draft");
        draft.Status = ContentStatus.Draft;
        draft.PublishedAt = null;
        var published = Version(owner, 2, "published");
        fixture.Db.AddRange(owner, draft, published);
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var actualIds = Ids(await fixture.ListAsync("status=Draft&publishedAfter=2026-06-10T00:00:00Z"));
        Assert.Contains(owner.Id, actualIds);
        Assert.Empty(Ids(await fixture.ListAsync("resolve=true&status=Draft")));
    }

    [Fact]
    public async Task OrdinarySearchCanMatchNonServingText()
    {
        await using var fixture = await CreateFixtureAsync();
        var owner = Owner(fixture, "current-slug");
        var draft = Version(owner, 1, "old-slug");
        draft.Status = ContentStatus.Draft;
        draft.PublishedAt = null;
        var field = new TemplateField { TemplateVersionId = fixture.TemplateVersion.Id, Key = "body", Label = "Body",
            CompositionMode = CompositionMode.Inline, PrimitiveType = PrimitiveType.Text };
        draft.FieldValues.Add(new ContentVersionFieldValue { ContentVersionId = draft.Id, FieldId = field.Id,
            ValueKind = ValueKind.Text, TextValue = "needle in non-serving draft" });
        fixture.Db.AddRange(field, owner, draft, Version(owner, 2, "current-slug"));
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var ordinaryIds = Ids(await fixture.ListAsync("q=needle"));
        var resolvedIds = Ids(await fixture.ListAsync("resolve=true&q=needle"));
        Assert.Contains(owner.Id, ordinaryIds);
        Assert.DoesNotContain(owner.Id, resolvedIds);
    }

    [Theory]
    [InlineData("percent%value", true)]
    [InlineData("under_score", true)]
    [InlineData("back\\slash", false)]
    public async Task SearchModesPreserveWildcardDifference(string search, bool ordinaryMatchesBoth)
    {
        await using var fixture = await CreateFixtureAsync(_output);
        var literal = Owner(fixture, $"prefix-{search}-suffix");
        var alternate = Owner(fixture, search switch
        {
            "percent%value" => "prefix-percentXvalue-suffix",
            "under_score" => "prefix-underXscore-suffix",
            _ => "prefix-backslash-suffix"
        });
        fixture.Db.AddRange(literal, alternate, Version(literal, 1, literal.Slug!), Version(alternate, 1, alternate.Slug!));
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Npgsql's ordinary ILike translation treats backslash literally on this baseline;
        // percent and underscore remain wildcard operators. Resolved escapes all three.
        var expectedBaselineIds = ordinaryMatchesBoth ? new[] { literal.Id, alternate.Id } : new[] { literal.Id };
        Assert.Equal(expectedBaselineIds.Order(), Ids(await fixture.ListAsync($"q={Uri.EscapeDataString(search)}")).Order());
        Assert.Equal(new[] { literal.Id }, Ids(await fixture.ListAsync($"resolve=true&q={Uri.EscapeDataString(search)}")));
    }

    [Fact]
    public async Task ResolvedSnapshotIgnoresLaterItemTagAndIdentityEdits()
    {
        await using var fixture = await CreateFixtureAsync();
        var owner = Owner(fixture, "published-slug");
        owner.LocaleCode = "en";
        owner.TranslationGroupId = Guid.NewGuid();
        var version = Version(owner, 1, "published-slug");
        version.Tags = ["published-tag"];
        version.LocaleCode = "en";
        version.TranslationGroupId = owner.TranslationGroupId;
        fixture.Db.AddRange(owner, version);
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var laterTemplate = new Template { WorkspaceId = fixture.WorkspaceId, Name = "Later", Slug = "later" };
        var laterVersion = new TemplateVersion { TemplateId = laterTemplate.Id, VersionNumber = 1 };
        laterTemplate.Versions.Add(laterVersion);
        var laterTag = new Tag { WorkspaceId = fixture.WorkspaceId, Name = "later-tag" };
        fixture.Db.AddRange(laterTemplate, laterTag);
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        owner.Slug = "later-slug";
        owner.LocaleCode = "fr";
        owner.TranslationGroupId = Guid.NewGuid();
        owner.TemplateVersionId = laterVersion.Id;
        owner.Tags.Add(new ContentItemTag { ContentItemId = owner.Id, TagId = laterTag.Id });
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var resolved = Assert.Single((await fixture.ListAsync("resolve=true")).GetProperty("items").EnumerateArray());
        Assert.Equal("published-slug", resolved.GetProperty("slug").GetString());
        Assert.Equal(new[] { "published-tag" }, Strings(resolved.GetProperty("tags")));
        Assert.Equal(fixture.TemplateVersion.Id, resolved.GetProperty("templateVersionId").GetGuid());
        Assert.Equal("Compatibility", resolved.GetProperty("templateName").GetString());
        Assert.Equal("compatibility", resolved.GetProperty("templateSlug").GetString());
        Assert.Equal("en", resolved.GetProperty("localeCode").GetString());
        Assert.Equal(version.TranslationGroupId, resolved.GetProperty("translationGroupId").GetGuid());
        Assert.Equal(owner.Id, resolved.GetProperty("id").GetGuid());
        Assert.Equal(PublishedAt, resolved.GetProperty("createdAt").GetDateTimeOffset());
        Assert.Equal(PublishedAt, resolved.GetProperty("updatedAt").GetDateTimeOffset());
        Assert.Equal(1, resolved.GetProperty("versionCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, resolved.GetProperty("currentlyServingVersion").ValueKind);
        Assert.Equal(new[] { "createdAt", "currentlyServingVersion", "id", "localeCode", "slug", "tags", "templateName",
            "templateSlug", "templateVersionId", "translationGroupId", "updatedAt", "versionCount" },
            resolved.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(new[] { owner.Id }, Ids(await fixture.ListAsync("resolve=true&tags=PUBLISHED-TAG")));
        Assert.Empty(Ids(await fixture.ListAsync("tags=published-tag")));
        Assert.Equal(new[] { owner.Id }, Ids(await fixture.ListAsync("tags=later-tag")));
    }

    [Fact]
    public async Task OrdinarySummaryCopiesEveryServingVersionField()
    {
        await using var fixture = await CreateFixtureAsync();
        var owner = Owner(fixture, "owner-slug");
        var version = Version(owner, 7, "serving-slug");
        version.LocaleCode = "en";
        version.EffectiveStartAt = DateTimeOffset.Parse("2020-01-01T00:00:00Z");
        version.EffectiveEndAt = DateTimeOffset.Parse("2040-01-01T00:00:00Z");
        version.PublishAt = PublishedAt.AddHours(-1);
        version.ArchivedAt = PublishedAt.AddDays(1);
        version.PublishedByUserId = fixture.AdminId;
        version.RolledBackFromVersionNumber = 4;
        version.Tags = ["zebra", "alpha"];
        fixture.Db.AddRange(owner, version, Version(owner, 8, "non-serving"));
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        // Reload persisted audit timestamps; expected values are fixture data, never the API mapper.
        await fixture.Db.Entry(version).ReloadAsync(TestContext.Current.CancellationToken);
        await fixture.Db.Entry(owner).ReloadAsync(TestContext.Current.CancellationToken);
        var expectedSummary = JsonSerializer.SerializeToElement(new
        {
            version.Id, version.ContentItemId, version.VersionNumber, Status = "Published", version.TemplateVersionId,
            version.Slug, version.LocaleCode, version.EffectiveStartAt, version.EffectiveEndAt, version.PublishAt,
            version.PublishedAt, version.ArchivedAt, version.PublishedByUserId, version.RolledBackFromVersionNumber,
            Tags = new[] { "zebra", "alpha" }, version.CreatedAt, version.UpdatedAt
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var item = Assert.Single((await fixture.ListAsync("asOf=2050-01-01T00:00:00Z")).GetProperty("items").EnumerateArray());
        var actualSummary = item.GetProperty("currentlyServingVersion");
        Assert.True(JsonElement.DeepEquals(expectedSummary, actualSummary), $"Expected {expectedSummary}; actual {actualSummary}");
        Assert.Equal(2, item.GetProperty("versionCount").GetInt32());
        Assert.Equal("owner-slug", item.GetProperty("slug").GetString());
        Assert.Equal(owner.CreatedAt, item.GetProperty("createdAt").GetDateTimeOffset());
        Assert.Equal(owner.UpdatedAt, item.GetProperty("updatedAt").GetDateTimeOffset());
    }

    [Theory]
    [InlineData("status=Published")]
    [InlineData("publishedAfter=2026-06-10T00:00:00Z")]
    [InlineData("publishedBefore=2026-06-10T00:00:00Z")]
    public async Task OrdinaryVersionFiltersApplyIndependentlyAndInclusively(string filter)
    {
        await using var fixture = await CreateFixtureAsync();
        var matching = Owner(fixture, "match");
        var miss = Owner(fixture, "miss");
        var missVersion = Version(miss, 1, "miss");
        if (filter.StartsWith("status", StringComparison.Ordinal)) missVersion.Status = ContentStatus.Draft;
        else missVersion.PublishedAt = PublishedAt.AddDays(filter.StartsWith("publishedAfter", StringComparison.Ordinal) ? -1 : 1);
        fixture.Db.AddRange(matching, miss, Version(matching, 1, "match"), missVersion);
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new[] { matching.Id }, Ids(await fixture.ListAsync(filter)));
    }

    [Theory]
    [InlineData("", true, "null,b,a")]
    [InlineData("createdAt", false, "a,b,null")]
    [InlineData("createdAt", true, "null,b,a")]
    [InlineData("unrecognized", false, "a,b,null")]
    [InlineData("publishedAt", true, "null,b,a")]
    [InlineData("updatedAt", false, "null,b,a")]
    [InlineData("updatedAt", true, "a,b,null")]
    [InlineData("slug", false, "a,b,null")]
    [InlineData("slug", true, "null,b,a")]
    public async Task OrdinarySortBranchesPreservePostgresNullPlacement(string sortBy, bool descending, string expected)
    {
        await using var fixture = await CreateFixtureAsync();
        var items = new[] { Owner(fixture, "a"), Owner(fixture, "b"), Owner(fixture, null) };
        fixture.Db.AddRange(items);
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        // Audit interception owns normal writes. Set explicit persisted test keys after insertion.
        for (var index = 0; index < items.Length; index++)
        {
            var created = PublishedAt.AddDays(index);
            var updated = PublishedAt.AddDays(-index);
            await fixture.Db.ContentItems.Where(item => item.Id == items[index].Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.CreatedAt, created).SetProperty(item => item.UpdatedAt, updated), TestContext.Current.CancellationToken);
        }
        var query = sortBy.Length == 0 ? "" : $"sortBy={sortBy}&sortDesc={descending}";
        var actual = (await fixture.ListAsync(query)).GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("slug").GetString() ?? "null");
        Assert.Equal(expected.Split(','), actual);
    }

    [Fact]
    public async Task OrdinaryEqualKeysPromiseMembershipOnly()
    {
        await using var fixture = await CreateFixtureAsync();
        var items = new[] { Owner(fixture, null), Owner(fixture, null), Owner(fixture, null) };
        fixture.Db.AddRange(items);
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var allIds = Ids(await fixture.ListAsync("sortBy=slug&sortDesc=false"));
        Assert.Equal(items.Select(item => item.Id).Order(), allIds.Order());
        var page = Ids(await fixture.ListAsync("sortBy=slug&pageSize=1"));
        Assert.Contains(Assert.Single(page), allIds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlankFiltersAndPaginationRetainEnvelope(bool resolved)
    {
        await using var fixture = await CreateFixtureAsync();
        var owner = Owner(fixture, "single");
        fixture.Db.AddRange(owner, Version(owner, 1, "single"));
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var mode = $"resolve={resolved}&q=%20%09&tags=%20%09&localeCode=%20&slug=%20";
        foreach (var size in new[] { 1, 100 })
        {
            var page = await fixture.ListAsync($"{mode}&page=1&pageSize={size}");
            Assert.Equal(new[] { owner.Id }, Ids(page));
            Assert.Equal(1, page.GetProperty("totalCount").GetInt32());
            Assert.Equal(1, page.GetProperty("totalPages").GetInt32());
            Assert.Equal(size, page.GetProperty("pageSize").GetInt32());
        }
        var overflow = await fixture.ListAsync($"{mode}&page={int.MaxValue}&pageSize=100");
        Assert.Empty(Ids(overflow));
        Assert.Equal(int.MaxValue, overflow.GetProperty("page").GetInt32());
        Assert.Equal(100, overflow.GetProperty("pageSize").GetInt32());
        Assert.Equal(1, overflow.GetProperty("totalCount").GetInt32());
        foreach (var invalid in new[] { "page=0", "pageSize=0", "pageSize=101" })
        {
            using var response = await fixture.Client.GetAsync(fixture.Url($"{mode}&{invalid}"), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task ResolvedCreatedFiltersAreIgnoredWhileOrdinaryUsesItemDates()
    {
        await using var fixture = await CreateFixtureAsync();
        var owner = Owner(fixture, "single");
        fixture.Db.AddRange(owner, Version(owner, 1, "single"));
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        foreach (var filter in new[] { "createdAfter=2099-01-01T00:00:00Z", "createdBefore=2000-01-01T00:00:00Z" })
        {
            Assert.Empty(Ids(await fixture.ListAsync(filter)));
            Assert.Equal(new[] { owner.Id }, Ids(await fixture.ListAsync($"resolve=true&{filter}")));
        }
        await fixture.Db.Entry(owner).ReloadAsync(TestContext.Current.CancellationToken);
        var timestamp = Uri.EscapeDataString(owner.CreatedAt.ToString("O"));
        Assert.Equal(new[] { owner.Id }, Ids(await fixture.ListAsync($"createdAfter={timestamp}&createdBefore={timestamp}")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeletedOwnersAndTagsRespectModeSpecificSnapshots(bool resolved)
    {
        await using var fixture = await CreateFixtureAsync();
        var live = Owner(fixture, "live");
        var deleted = Owner(fixture, "deleted");
        deleted.IsDeleted = true;
        deleted.DeletedAt = PublishedAt;
        var tag = new Tag { WorkspaceId = fixture.WorkspaceId, Name = "deleted-tag", IsDeleted = true, DeletedAt = PublishedAt };
        live.Tags.Add(new ContentItemTag { ContentItemId = live.Id, TagId = tag.Id });
        var liveVersion = Version(live, 1, "live");
        liveVersion.Tags = ["deleted-tag"];
        fixture.Db.AddRange(tag, live, deleted, liveVersion, Version(deleted, 1, "deleted"));
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var page = await fixture.ListAsync($"resolve={resolved}");
        Assert.Equal(new[] { live.Id }, Ids(page));
        Assert.Equal(resolved ? new[] { "deleted-tag" } : [], Strings(page.GetProperty("items")[0].GetProperty("tags")));
        Assert.Equal(resolved ? new[] { live.Id } : [], Ids(await fixture.ListAsync($"resolve={resolved}&tags=deleted-tag")));
    }

    [Fact]
    public async Task OrdinaryMetadataFiltersUseExactItemIdentityAndAllLiveTags()
    {
        await using var fixture = await CreateFixtureAsync();
        var databaseVersion = await fixture.Db.Database.SqlQueryRaw<string>("SELECT version() AS \"Value\"")
            .SingleAsync(TestContext.Current.CancellationToken);
        var collation = await fixture.Db.Database.SqlQueryRaw<string>(
                "SELECT datcollate || '/' || datctype AS \"Value\" FROM pg_database WHERE datname = current_database()")
            .SingleAsync(TestContext.Current.CancellationToken);
        _output.WriteLine($"PostgreSQL baseline: {databaseVersion}; LC_COLLATE/LC_CTYPE={collation}");
        var otherTemplate = new Template { WorkspaceId = fixture.WorkspaceId, Name = "Other", Slug = "other" };
        var otherVersion = new TemplateVersion { TemplateId = otherTemplate.Id, VersionNumber = 1 };
        otherTemplate.Versions.Add(otherVersion);
        fixture.Db.Add(otherTemplate);
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var match = Owner(fixture, "exact");
        match.LocaleCode = "en";
        match.TranslationGroupId = Guid.NewGuid();
        var miss = Owner(fixture, "exact-extension");
        miss.LocaleCode = "en-US";
        miss.TranslationGroupId = Guid.NewGuid();
        miss.TemplateVersionId = otherVersion.Id;
        var zebraOnly = Owner(fixture, "zebra-only");
        zebraOnly.LocaleCode = "en-US";
        zebraOnly.TemplateVersionId = otherVersion.Id;
        var alpha = new Tag { WorkspaceId = fixture.WorkspaceId, Name = "alpha" };
        var zebra = new Tag { WorkspaceId = fixture.WorkspaceId, Name = "zebra" };
        match.Tags.Add(new ContentItemTag { ContentItemId = match.Id, TagId = zebra.Id });
        match.Tags.Add(new ContentItemTag { ContentItemId = match.Id, TagId = alpha.Id });
        miss.Tags.Add(new ContentItemTag { ContentItemId = miss.Id, TagId = alpha.Id });
        zebraOnly.Tags.Add(new ContentItemTag { ContentItemId = zebraOnly.Id, TagId = zebra.Id });
        fixture.Db.AddRange(match, miss, zebraOnly, alpha, zebra);
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        foreach (var filter in new[] { $"templateVersionId={fixture.TemplateVersion.Id}", $"templateId={fixture.TemplateVersion.TemplateId}",
                     "localeCode=en", $"translationGroupId={match.TranslationGroupId}", "slug=exact",
                     "tags=%20ZEBRA%20,alpha,alpha,,%20" })
            Assert.Equal(new[] { match.Id }, Ids(await fixture.ListAsync(filter)));
        Assert.Equal(new[] { match.Id, miss.Id }.Order(), Ids(await fixture.ListAsync("tags=alpha")).Order());
        Assert.Equal(new[] { match.Id, zebraOnly.Id }.Order(), Ids(await fixture.ListAsync("tags=zebra")).Order());
        var item = Assert.Single((await fixture.ListAsync("slug=exact")).GetProperty("items").EnumerateArray());
        Assert.Equal(new[] { "alpha", "zebra" }, Strings(item.GetProperty("tags")));
        Assert.Equal(0, item.GetProperty("versionCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("currentlyServingVersion").ValueKind);
        Assert.Empty(Ids(await fixture.ListAsync("q=%20exact%20")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeletedTemplateReferencesRetainReleasedFailureAndCountShape(bool deleteVersion)
    {
        await using var fixture = await CreateFixtureAsync();
        var owner = Owner(fixture, "orphaned-template");
        fixture.Db.AddRange(owner, Version(owner, 1, "orphaned-template"));
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        if (deleteVersion) fixture.TemplateVersion.IsDeleted = true;
        else
        {
            var template = await fixture.Db.Templates.SingleAsync(template => template.Id == fixture.TemplateVersion.TemplateId,
                TestContext.Current.CancellationToken);
            template.IsDeleted = true;
        }
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var ordinary = await fixture.Client.GetAsync(fixture.Url(""), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, ordinary.StatusCode);
        var problem = await ordinary.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(409, problem.GetProperty("status").GetInt32());
        _output.WriteLine($"Deleted {(deleteVersion ? "template version" : "template")} ordinary response: {problem}");
        var resolved = await fixture.ListAsync("resolve=true");
        Assert.Equal(1, resolved.GetProperty("totalCount").GetInt32());
        Assert.Empty(Ids(resolved));
    }

    private static ContentItem Owner(Fixture fixture, string? slug) => new()
    {
        WorkspaceId = fixture.WorkspaceId, TemplateVersionId = fixture.TemplateVersion.Id, Slug = slug
    };

    private static ContentVersion Version(ContentItem owner, int number, string slug) => new()
    {
        ContentItemId = owner.Id, WorkspaceId = owner.WorkspaceId, TemplateVersionId = owner.TemplateVersionId,
        VersionNumber = number, Status = ContentStatus.Published, Slug = slug, PublishedAt = PublishedAt
    };

    private static Guid[] Ids(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToArray();
    private static string[] Strings(JsonElement array) => array.EnumerateArray().Select(value => value.GetString()!).ToArray();

    private static async Task<Fixture> CreateFixtureAsync(ITestOutputHelper? searchOutput = null)
    {
        var factory = new WebApplicationFactory<Program>();
        if (searchOutput is not null)
            factory = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                services.AddDbContext<CmsifyDbContext>((_, options) => options.AddInterceptors(new SearchCommandEvidence(searchOutput)))));
        var client = factory.CreateClient();
        var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var workspaceId = await db.Workspaces.Select(workspace => workspace.Id).FirstAsync();
        var adminId = await db.Users.Select(user => user.Id).FirstAsync();
        db.ApiClients.Add(new ApiClient { Name = "Compatibility", TokenHash = BCrypt.Net.BCrypt.HashPassword(ApiToken, 4),
            Role = UserRole.Reader, WorkspaceId = workspaceId, CreatedByUserId = adminId });
        var template = new Template { WorkspaceId = workspaceId, Name = "Compatibility", Slug = "compatibility" };
        var templateVersion = new TemplateVersion { TemplateId = template.Id, VersionNumber = 1, Status = TemplateVersionStatus.Published };
        template.Versions.Add(templateVersion);
        db.Templates.Add(template);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
        return new Fixture(factory, client, scope, db, workspaceId, adminId, templateVersion);
    }

    // Same interception boundary as ResolvedContentListQueryTests, limited to actual HTTP
    // search SQL for diagnostic evidence; authentication/token commands are never recorded.
    private sealed class SearchCommandEvidence(ITestOutputHelper output) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("ILIKE", StringComparison.Ordinal))
            {
                output.WriteLine(command.CommandText);
                foreach (DbParameter parameter in command.Parameters)
                    output.WriteLine($"{parameter.ParameterName} ({parameter.DbType}) = {parameter.Value}");
            }
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed record Fixture(WebApplicationFactory<Program> Factory, HttpClient Client, IServiceScope Scope,
        CmsifyDbContext Db, Guid WorkspaceId, Guid AdminId, TemplateVersion TemplateVersion) : IAsyncDisposable
    {
        public string Url(string query) => $"/api/v1/workspaces/{WorkspaceId}/content?asOf={AsOf}&{query}";
        public Task<JsonElement> ListAsync(string query) => Client.GetFromJsonAsync<JsonElement>(Url(query), TestContext.Current.CancellationToken);
        public async ValueTask DisposeAsync()
        {
            Scope.Dispose();
            Client.Dispose();
            await Factory.DisposeAsync();
        }
    }
}
