using System.Data.Common;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;

namespace Cmsify.Api.Integration.Tests;

public sealed class ResolvedContentListQueryTests : IAsyncLifetime
{
    private const string ApiToken = "cmsify_resolved_query_test_token";
    private const int CapacityContentItemCount = 520;
    private const int CapacityLiveItemCount = 500;
    private const int CapacityVersionCount = CapacityContentItemCount * 5;
    private static readonly DateTimeOffset CapacityAsOf = DateTimeOffset.Parse("2026-06-15T12:00:00Z");
    private static readonly DateTimeOffset CapacityPublishedBase = DateTimeOffset.Parse("2026-04-01T00:00:00Z");

    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("cmsify")
        .WithUsername("cmsify")
        .WithPassword("cmsify")
        .Build();

    public async ValueTask InitializeAsync()
    {
        await postgres.StartAsync();
        Environment.SetEnvironmentVariable("ConnectionStrings__Cmsify", postgres.GetConnectionString());
        Environment.SetEnvironmentVariable("Seed__Admin__Email", "admin@example.test");
        Environment.SetEnvironmentVariable("Seed__Admin__Password", "change-this-temporary-password");
        Environment.SetEnvironmentVariable("Seed__DefaultWorkspace__Name", "Default");
        Environment.SetEnvironmentVariable("Seed__DefaultWorkspace__Slug", "default");
    }

    public async ValueTask DisposeAsync()
    {
        await postgres.DisposeAsync();
        ClearEnvironment();
    }

    [Fact]
    public async Task ValidResolvedList_UsesOneCountAndOneJoinedPageCommand()
    {
        var commands = new ContentCommandRecorder();
        await using var factory = CreateFactory(commands);
        using var client = factory.CreateClient();
        var workspaceId = await SeedTwoResolvedItemsAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
        commands.Start();

        var response = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/workspaces/{workspaceId}/content?resolve=true&asOf=2026-06-15T12:00:00Z&page=1&pageSize=2",
            TestContext.Current.CancellationToken);

        Assert.Equal(2, response.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, response.GetProperty("items").GetArrayLength());
        Assert.Equal(2, commands.Commands.Count);
        Assert.Contains("content_versions", commands.Commands[0], StringComparison.Ordinal);
        Assert.Contains(" join ", commands.Commands[1], StringComparison.Ordinal);
        Assert.Contains("template_versions", commands.Commands[1], StringComparison.Ordinal);
        Assert.Contains("templates", commands.Commands[1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ResolvedList_CommandCountDoesNotGrowWithTheNumberOfPageResults(int pageSize)
    {
        var commands = new ContentCommandRecorder();
        await using var factory = CreateFactory(commands);
        using var client = factory.CreateClient();
        var workspaceId = await SeedTwoResolvedItemsAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
        commands.Start();

        var response = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/workspaces/{workspaceId}/content?resolve=true&asOf=2026-06-15T12:00:00Z&page=1&pageSize={pageSize}",
            TestContext.Current.CancellationToken);

        Assert.Equal(pageSize, response.GetProperty("items").GetArrayLength());
        Assert.Equal(2, commands.Commands.Count);
    }

    [Fact]
    [Trait("Category", "Capacity")]
    public async Task ContentCommandRecorder_CountsTemplateOnlyCommands()
    {
        var commands = new ContentCommandRecorder();
        await using var factory = CreateFactory(commands);
        using var client = factory.CreateClient();
        commands.Start();
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();

        _ = await dbContext.Templates.CountAsync(TestContext.Current.CancellationToken);

        Assert.Single(commands.Commands);
        Assert.Contains("templates", commands.Commands[0], StringComparison.Ordinal);
        Assert.DoesNotContain("content_versions", commands.Commands[0], StringComparison.Ordinal);
        Assert.DoesNotContain("template_versions", commands.Commands[0], StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Capacity")]
    public async Task RepresentativeResolvedList_HasStableBoundedPagesAndConstantCommandCount()
    {
        var commands = new ContentCommandRecorder();
        await using var factory = CreateFactory(commands);
        using var client = factory.CreateClient();
        var seed = await SeedRepresentativeCapacityDataAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);

        var filteredFirst = await ExecuteCapacityRequestAsync(
            client,
            commands,
            seed.WorkspaceId,
            "localeCode=en-US&page=1&pageSize=1");
        AssertCapacityPage(filteredFirst, seed, 250, [498]);

        var singleFirst = await ExecuteCapacityRequestAsync(
            client,
            commands,
            seed.WorkspaceId,
            "page=1&pageSize=1");
        AssertCapacityPage(singleFirst, seed, CapacityLiveItemCount, [499]);

        var singleLater = await ExecuteCapacityRequestAsync(
            client,
            commands,
            seed.WorkspaceId,
            "page=250&pageSize=1");
        AssertCapacityPage(singleLater, seed, CapacityLiveItemCount, [250]);

        var maximumFirst = await ExecuteCapacityRequestAsync(
            client,
            commands,
            seed.WorkspaceId,
            "page=1&pageSize=100");
        AssertCapacityPage(maximumFirst, seed, CapacityLiveItemCount, Enumerable.Range(400, 100).Reverse());

        var maximumLater = await ExecuteCapacityRequestAsync(
            client,
            commands,
            seed.WorkspaceId,
            "page=3&pageSize=100");
        AssertCapacityPage(maximumLater, seed, CapacityLiveItemCount, Enumerable.Range(200, 100).Reverse());

        var samples = new[] { filteredFirst, singleFirst, singleLater, maximumFirst, maximumLater };
        Assert.All(samples, sample => Assert.Equal(2, sample.Commands.Count));
        Assert.Equal(filteredFirst.Commands.Count, maximumFirst.Commands.Count);

        if (Environment.GetEnvironmentVariable("CMSIFY_CAPACITY_REPORT_DIR") is { } reportDirectory
            && !string.IsNullOrWhiteSpace(reportDirectory))
        {
            var elapsedMilliseconds = samples
                .Select(sample => Math.Round(sample.ElapsedMilliseconds, 3))
                .Order()
                .ToArray();
            var p50Milliseconds = Percentile(elapsedMilliseconds, 0.50);
            var p95Milliseconds = Percentile(elapsedMilliseconds, 0.95);
            var p99Milliseconds = Percentile(elapsedMilliseconds, 0.99);
            var p95AtOrBelow250Milliseconds = p95Milliseconds <= 250;
            var p99AtOrBelow500Milliseconds = p99Milliseconds <= 500;
            var fragment = new
            {
                databaseVersion = seed.DatabaseVersion,
                datasetCounts = new
                {
                    contentItems = CapacityContentItemCount,
                    publishedVersions = CapacityVersionCount,
                    eligibleItems = CapacityLiveItemCount,
                    filteredEligibleItems = 250,
                    deletedOwners = CapacityContentItemCount - CapacityLiveItemCount,
                    templates = seed.TemplateVersionIds.Count,
                    locales = 2,
                    tags = 7
                },
                queryCounts = samples.Select(sample => sample.Commands.Count).ToArray(),
                sampleCount = samples.Length,
                elapsedMilliseconds,
                p50Milliseconds,
                p95Milliseconds,
                p99Milliseconds,
                p95AtOrBelow250Milliseconds,
                p99AtOrBelow500Milliseconds,
                blockingInvariantsPassed = true
            };

            await CapacityReportFragmentWriter.WriteAsync(
                reportDirectory,
                "resolved-content.json",
                fragment,
                TestContext.Current.CancellationToken);
            if (!p95AtOrBelow250Milliseconds || !p99AtOrBelow500Milliseconds)
            {
                var warning =
                    $"::warning::Resolved-content capacity timing missed a diagnostic budget " +
                    $"(p95={p95Milliseconds:F3} ms, p99={p99Milliseconds:F3} ms).";
                TestContext.Current.SendDiagnosticMessage(warning);
                Console.Error.WriteLine(warning);
            }
        }
    }

    [Fact]
    [Trait("Category", "Capacity")]
    public async Task CapacityReportFragmentWriter_AtomicallyReplacesTheFragment()
    {
        var reportDirectory = Path.Combine(Path.GetTempPath(), $"cmsify-capacity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(reportDirectory);
        var reportPath = Path.Combine(reportDirectory, "resolved-content.json");
        try
        {
            await File.WriteAllTextAsync(reportPath, "stale", TestContext.Current.CancellationToken);
            var fragment = new { databaseVersion = "PostgreSQL test", sampleCount = 2 };

            await CapacityReportFragmentWriter.WriteAsync(
                reportDirectory,
                "resolved-content.json",
                fragment,
                TestContext.Current.CancellationToken);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath, TestContext.Current.CancellationToken));
            Assert.Equal("PostgreSQL test", document.RootElement.GetProperty("databaseVersion").GetString());
            Assert.Equal(2, document.RootElement.GetProperty("sampleCount").GetInt32());
            Assert.Equal([reportPath], Directory.GetFiles(reportDirectory));
        }
        finally
        {
            Directory.Delete(reportDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task OverflowedResolvedList_UsesOnlyTheCountCommand()
    {
        var commands = new ContentCommandRecorder();
        await using var factory = CreateFactory(commands);
        using var client = factory.CreateClient();
        var workspaceId = await SeedTwoResolvedItemsAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
        commands.Start();

        var response = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/workspaces/{workspaceId}/content?resolve=true&asOf=2026-06-15T12:00:00Z&page={int.MaxValue}&pageSize=100",
            TestContext.Current.CancellationToken);

        Assert.Equal(2, response.GetProperty("totalCount").GetInt32());
        Assert.Equal(0, response.GetProperty("items").GetArrayLength());
        Assert.Single(commands.Commands);
        Assert.Contains("count", commands.Commands[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonPublishedResolvedList_UsesNoContentCommands()
    {
        var commands = new ContentCommandRecorder();
        await using var factory = CreateFactory(commands);
        using var client = factory.CreateClient();
        var workspaceId = await SeedTwoResolvedItemsAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
        commands.Start();

        var response = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/workspaces/{workspaceId}/content?resolve=true&status=Draft&asOf=2026-06-15T12:00:00Z",
            TestContext.Current.CancellationToken);

        Assert.Equal(0, response.GetProperty("totalCount").GetInt32());
        Assert.Equal(0, response.GetProperty("items").GetArrayLength());
        Assert.Empty(commands.Commands);
    }

    [Fact]
    public async Task TagFilteredResolvedList_UsesAParameterizedArrayPredicate()
    {
        var commands = new ContentCommandRecorder();
        await using var factory = CreateFactory(commands);
        using var client = factory.CreateClient();
        var workspaceId = await SeedTwoResolvedItemsAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
        commands.Start();

        var response = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/workspaces/{workspaceId}/content?resolve=true&tags=%20Featured%20&asOf=2026-06-15T12:00:00Z",
            TestContext.Current.CancellationToken);

        Assert.Equal(1, response.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, commands.Commands.Count);
        Assert.All(commands.Commands, command => Assert.Contains("tags @> @", command, StringComparison.Ordinal));
        Assert.DoesNotContain("featured", string.Join(' ', commands.Commands), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchFilteredResolvedList_UsesAParameterizedPatternAndTwoCommands()
    {
        var commands = new ContentCommandRecorder();
        await using var factory = CreateFactory(commands);
        using var client = factory.CreateClient();
        var workspaceId = await SeedTwoResolvedItemsAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
        commands.Start();

        var response = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/workspaces/{workspaceId}/content?resolve=true&q=first_value&asOf=2026-06-15T12:00:00Z",
            TestContext.Current.CancellationToken);

        Assert.Equal(1, response.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, commands.Commands.Count);
        Assert.All(commands.Commands, command => Assert.Contains(" ilike ", command, StringComparison.Ordinal));
        Assert.DoesNotContain("first_value", string.Join(' ', commands.Commands), StringComparison.Ordinal);
    }

    private static WebApplicationFactory<Program> CreateFactory(ContentCommandRecorder commands) =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddDbContext<CmsifyDbContext>((_, options) => options.AddInterceptors(commands));
            }));

    private static async Task<Guid> SeedTwoResolvedItemsAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var adminUserId = await dbContext.Users.Select(user => user.Id).FirstAsync();
        var workspace = new Workspace { Name = "Resolved query", Slug = $"resolved-{Guid.NewGuid():N}" };
        var template = new Template { WorkspaceId = workspace.Id, Name = "Article", Slug = $"article-{Guid.NewGuid():N}" };
        var templateVersion = new TemplateVersion
        {
            TemplateId = template.Id,
            VersionNumber = 1,
            Status = TemplateVersionStatus.Published
        };
        template.Versions.Add(templateVersion);
        var first = PublishedItem(workspace.Id, templateVersion.Id, "first_value", DateTimeOffset.Parse("2026-06-01T00:00:00Z"), ["featured", "news"]);
        var second = PublishedItem(workspace.Id, templateVersion.Id, "firstXvalue", DateTimeOffset.Parse("2026-06-02T00:00:00Z"));

        dbContext.Workspaces.Add(workspace);
        dbContext.Templates.Add(template);
        await dbContext.SaveChangesAsync();
        template.CurrentVersionId = templateVersion.Id;
        dbContext.ApiClients.Add(new ApiClient
        {
            Name = $"Resolved Query {Guid.NewGuid():N}",
            TokenHash = BCrypt.Net.BCrypt.HashPassword(ApiToken, 4),
            Role = UserRole.Reader,
            WorkspaceId = workspace.Id,
            CreatedByUserId = adminUserId
        });
        dbContext.ContentItems.AddRange(first.Item, second.Item);
        dbContext.ContentVersions.AddRange(first.Version, second.Version);
        await dbContext.SaveChangesAsync();
        return workspace.Id;
    }

    private static async Task<RepresentativeCapacitySeed> SeedRepresentativeCapacityDataAsync(
        WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var cancellationToken = TestContext.Current.CancellationToken;
        var adminUserId = await dbContext.Users.Select(user => user.Id).FirstAsync(cancellationToken);
        var workspace = new Workspace
        {
            Id = StableGuid(0x60000000, 1),
            Name = "Resolved capacity",
            Slug = "resolved-capacity"
        };
        var templates = Enumerable.Range(0, 5)
            .Select(index => new Template
            {
                Id = StableGuid(0x60000001, index),
                WorkspaceId = workspace.Id,
                Name = $"Capacity Template {index}",
                Slug = $"capacity-template-{index}"
            })
            .ToList();
        var templateVersions = templates
            .Select((template, index) => new TemplateVersion
            {
                Id = StableGuid(0x60000002, index),
                TemplateId = template.Id,
                VersionNumber = 1,
                Status = TemplateVersionStatus.Published,
                PublishedAt = CapacityPublishedBase.AddDays(-1)
            })
            .ToList();
        for (var index = 0; index < templates.Count; index++)
        {
            templates[index].Versions.Add(templateVersions[index]);
        }

        dbContext.Workspaces.Add(workspace);
        dbContext.Templates.AddRange(templates);
        await dbContext.SaveChangesAsync(cancellationToken);
        for (var index = 0; index < templates.Count; index++)
        {
            templates[index].CurrentVersionId = templateVersions[index].Id;
        }

        dbContext.ApiClients.Add(new ApiClient
        {
            Id = StableGuid(0x60000003, 1),
            Name = "Resolved Capacity Reader",
            TokenHash = BCrypt.Net.BCrypt.HashPassword(ApiToken, 4),
            Role = UserRole.Reader,
            WorkspaceId = workspace.Id,
            CreatedByUserId = adminUserId
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        dbContext.ChangeTracker.Clear();

        var items = new List<ContentItem>(CapacityContentItemCount);
        var versions = new List<ContentVersion>(CapacityVersionCount);
        for (var itemIndex = 0; itemIndex < CapacityContentItemCount; itemIndex++)
        {
            var templateVersion = templateVersions[itemIndex % templateVersions.Count];
            var isDeleted = itemIndex >= CapacityLiveItemCount;
            var itemId = StableGuid(0x60000010, itemIndex);
            var locale = itemIndex % 2 == 0 ? "en-US" : "fr-FR";
            var translationGroupId = StableGuid(0x60000020, itemIndex / 2);
            var selectedPublishedAt = CapacityPublishedBase.AddMinutes(itemIndex).AddSeconds(5);
            var item = new ContentItem
            {
                Id = itemId,
                WorkspaceId = workspace.Id,
                TemplateVersionId = templateVersion.Id,
                Status = ContentStatus.Published,
                Slug = CapacitySelectedSlug(itemIndex),
                LocaleCode = locale,
                TranslationGroupId = translationGroupId,
                PublishedAt = selectedPublishedAt,
                IsDeleted = isDeleted,
                DeletedAt = isDeleted ? CapacityAsOf.AddDays(-1) : null
            };
            items.Add(item);

            versions.AddRange(
            [
                CapacityVersion(itemIndex, 1, item, templateVersion.Id, locale, translationGroupId, null, null, "fallback"),
                CapacityVersion(itemIndex, 2, item, templateVersion.Id, locale, translationGroupId, CapacityAsOf.AddDays(-30), CapacityAsOf.AddDays(30), "broad"),
                CapacityVersion(itemIndex, 3, item, templateVersion.Id, locale, translationGroupId, CapacityAsOf.AddDays(-2), CapacityAsOf.AddDays(2), "narrow"),
                CapacityVersion(itemIndex, 4, item, templateVersion.Id, locale, translationGroupId, CapacityAsOf.AddHours(-1), CapacityAsOf, "ended-at-boundary"),
                CapacityVersion(itemIndex, 5, item, templateVersion.Id, locale, translationGroupId, CapacityAsOf, CapacityAsOf.AddHours(2), "selected")
            ]);
        }

        const int itemBatchSize = 100;
        for (var offset = 0; offset < items.Count; offset += itemBatchSize)
        {
            dbContext.ContentItems.AddRange(items.Skip(offset).Take(itemBatchSize));
            dbContext.ContentVersions.AddRange(versions.Skip(offset * 5).Take(itemBatchSize * 5));
            await dbContext.SaveChangesAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
        }

        await dbContext.Database.OpenConnectionAsync(cancellationToken);
        var databaseVersion = $"PostgreSQL {dbContext.Database.GetDbConnection().ServerVersion}";
        await dbContext.Database.CloseConnectionAsync();
        return new RepresentativeCapacitySeed(
            workspace.Id,
            items.Take(CapacityLiveItemCount).Select(item => item.Id).ToArray(),
            templateVersions.Select(version => version.Id).ToArray(),
            databaseVersion);
    }

    private static ContentVersion CapacityVersion(
        int itemIndex,
        int versionNumber,
        ContentItem item,
        Guid templateVersionId,
        string locale,
        Guid translationGroupId,
        DateTimeOffset? effectiveStartAt,
        DateTimeOffset? effectiveEndAt,
        string slugSuffix) =>
        new()
        {
            Id = StableGuid(0x60000011, (itemIndex * 10) + versionNumber),
            ContentItemId = item.Id,
            WorkspaceId = item.WorkspaceId,
            VersionNumber = versionNumber,
            Status = ContentVersionStatus.Published,
            TemplateVersionId = templateVersionId,
            Slug = $"capacity-{itemIndex:D4}-{slugSuffix}",
            LocaleCode = locale,
            TranslationGroupId = translationGroupId,
            Tags = versionNumber == 5
                ? itemIndex % 2 == 0 ? ["capacity", "featured"] : ["capacity", "news"]
                : ["capacity", slugSuffix],
            EffectiveStartAt = effectiveStartAt,
            EffectiveEndAt = effectiveEndAt,
            PublishedAt = CapacityPublishedBase.AddMinutes(itemIndex).AddSeconds(versionNumber)
        };

    private static async Task<CapacityRequestResult> ExecuteCapacityRequestAsync(
        HttpClient client,
        ContentCommandRecorder commands,
        Guid workspaceId,
        string query)
    {
        commands.Start();
        var stopwatch = Stopwatch.StartNew();
        var response = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/workspaces/{workspaceId}/content?resolve=true&asOf=2026-06-15T12:00:00Z&{query}",
            TestContext.Current.CancellationToken);
        stopwatch.Stop();
        return new CapacityRequestResult(response, stopwatch.Elapsed.TotalMilliseconds, commands.Commands);
    }

    private static void AssertCapacityPage(
        CapacityRequestResult result,
        RepresentativeCapacitySeed seed,
        int expectedTotal,
        IEnumerable<int> expectedItemIndices)
    {
        var expectedIndices = expectedItemIndices.ToArray();
        Assert.Equal(expectedTotal, result.Response.GetProperty("totalCount").GetInt32());
        var items = result.Response.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(expectedIndices.Length, items.Length);
        for (var offset = 0; offset < expectedIndices.Length; offset++)
        {
            var itemIndex = expectedIndices[offset];
            var item = items[offset];
            Assert.Equal(seed.LiveItemIds[itemIndex], item.GetProperty("id").GetGuid());
            Assert.Equal(seed.TemplateVersionIds[itemIndex % seed.TemplateVersionIds.Count], item.GetProperty("templateVersionId").GetGuid());
            Assert.Equal($"Capacity Template {itemIndex % seed.TemplateVersionIds.Count}", item.GetProperty("templateName").GetString());
            Assert.Equal(CapacitySelectedSlug(itemIndex), item.GetProperty("slug").GetString());
            Assert.Equal(itemIndex % 2 == 0 ? "en-US" : "fr-FR", item.GetProperty("localeCode").GetString());
            Assert.Equal(
                itemIndex % 2 == 0 ? ["capacity", "featured"] : ["capacity", "news"],
                item.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()).ToArray());
            Assert.Equal(
                CapacityPublishedBase.AddMinutes(itemIndex).AddSeconds(5),
                item.GetProperty("publishedAt").GetDateTimeOffset());
        }

        Assert.Equal(2, result.Commands.Count);
        Assert.Single(result.Commands, command => command.Contains("templates", StringComparison.Ordinal));
    }

    private static string CapacitySelectedSlug(int itemIndex) => $"capacity-{itemIndex:D4}-selected";

    private static double Percentile(IReadOnlyList<double> sortedValues, double percentile)
    {
        Assert.NotEmpty(sortedValues);
        var index = Math.Clamp((int)Math.Ceiling(percentile * sortedValues.Count) - 1, 0, sortedValues.Count - 1);
        return sortedValues[index];
    }

    private static Guid StableGuid(int category, int index) =>
        Guid.Parse($"{category:x8}-0000-0000-0000-{index:x12}");

    private static (ContentItem Item, ContentVersion Version) PublishedItem(
        Guid workspaceId,
        Guid templateVersionId,
        string slug,
        DateTimeOffset publishedAt,
        IList<string>? tags = null)
    {
        var item = new ContentItem
        {
            WorkspaceId = workspaceId,
            TemplateVersionId = templateVersionId,
            Status = ContentStatus.Published,
            Slug = slug,
            PublishedAt = publishedAt
        };
        var version = new ContentVersion
        {
            ContentItemId = item.Id,
            WorkspaceId = workspaceId,
            VersionNumber = 1,
            Status = ContentVersionStatus.Published,
            TemplateVersionId = templateVersionId,
            Slug = slug,
            Tags = tags ?? [],
            PublishedAt = publishedAt
        };
        return (item, version);
    }

    private static void ClearEnvironment()
    {
        foreach (var key in new[]
        {
            "ConnectionStrings__Cmsify",
            "Seed__Admin__Email",
            "Seed__Admin__Password",
            "Seed__DefaultWorkspace__Name",
            "Seed__DefaultWorkspace__Slug"
        })
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    private sealed class ContentCommandRecorder : DbCommandInterceptor
    {
        private readonly Lock sync = new();
        private bool isStarted;

        public IReadOnlyList<string> Commands
        {
            get
            {
                lock (sync)
                {
                    return commands.ToList();
                }
            }
        }

        private readonly List<string> commands = [];

        public void Start()
        {
            lock (sync)
            {
                commands.Clear();
                isStarted = true;
            }
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Record(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Record(string commandText)
        {
            var normalized = Regex.Replace(commandText, @"\s+", " ").Trim().ToLowerInvariant();
            if (!normalized.Contains("content_versions", StringComparison.Ordinal)
                && !normalized.Contains("template_versions", StringComparison.Ordinal)
                && !normalized.Contains("templates", StringComparison.Ordinal))
            {
                return;
            }

            lock (sync)
            {
                if (isStarted)
                {
                    commands.Add(normalized);
                }
            }
        }
    }

    private sealed record RepresentativeCapacitySeed(
        Guid WorkspaceId,
        IReadOnlyList<Guid> LiveItemIds,
        IReadOnlyList<Guid> TemplateVersionIds,
        string DatabaseVersion);

    private sealed record CapacityRequestResult(
        JsonElement Response,
        double ElapsedMilliseconds,
        IReadOnlyList<string> Commands);
}
