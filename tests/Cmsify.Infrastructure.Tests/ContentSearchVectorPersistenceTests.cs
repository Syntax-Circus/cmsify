using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Services;
using Cmsify.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SyntaxCircus.EntityFrameworkCore.Postgres;

namespace Cmsify.Infrastructure.Tests;

/// <summary>
/// PostgreSQL has hard limits on a tsvector sent by a client, and a value beyond them fails the whole content save.
/// These tests save the builder's output on a real content row, so the EF mapping and the search index both apply.
/// </summary>
public sealed class ContentSearchVectorPersistenceTests : IAsyncLifetime
{
    private readonly TestPostgres postgres = new();

    public async ValueTask InitializeAsync() => await postgres.StartAsync();

    public async ValueTask DisposeAsync() => await postgres.DisposeAsync();

    public static TheoryData<string, string> LargeTexts() => new()
    {
        { "one word 256 times", Repeat("word", 256) },
        { "one word 257 times", Repeat("word", 257) },
        { "one word 5000 times", Repeat("word", 5000) },
        { "rich-content JSON key repeated per node", string.Concat(Enumerable.Repeat("{\"kind\":\"text\",\"text\":\"a\"}", 200)) },
        { "more words than the highest position", string.Join(' ', Enumerable.Range(0, 20000).Select(i => $"w{i}")) },
        { "one word repeated past the highest position", Repeat("filler", 17000) + " " + Repeat("tail", 300) },
        { "a very long word", new string('x', 5000) },
        { "a very long non-ASCII word", new string('é', 3000) },
        { "a word at the length limit", new string('x', 2046) },
        { "a word of 2047 bytes", new string('x', 2047) },
        { "more distinct text than one vector can hold", string.Join(' ', Enumerable.Range(0, 120_000).Select(i => $"distinct{i:D8}")) }
    };

    [Theory]
    [MemberData(nameof(LargeTexts))]
    public async Task BuiltVector_CanBeSavedReloadedAndSearched(string description, string text)
    {
        var ct = TestContext.Current.CancellationToken;
        var options = new DbContextOptionsBuilder<CmsifyDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).UseSyntaxCircusSnakeCaseNamingConvention().Options;
        var workspace = new Workspace { Name = "Search", Slug = "search" };
        var template = new Template { WorkspaceId = workspace.Id, Name = "Article", Slug = "article" };
        var templateVersion = new TemplateVersion { TemplateId = template.Id, VersionNumber = 1 };
        // The marker goes first: when a vector reaches its total size limit, the earliest words are kept.
        var item = new ContentItem
        {
            WorkspaceId = workspace.Id,
            TemplateVersionId = templateVersion.Id,
            Slug = "large",
            SearchVector = Build("marker " + text)
        };

        await using (var db = new CmsifyDbContext(options))
        {
            await db.Database.MigrateAsync(ct);
            db.AddRange(workspace, template, templateVersion, item);
            await db.SaveChangesAsync(ct);
        }

        await using (var db = new CmsifyDbContext(options))
        {
            var reloaded = await db.Set<ContentItem>().SingleAsync(row => row.Id == item.Id, ct);
            Assert.Contains("'marker':1", reloaded.SearchVector, StringComparison.Ordinal);
        }

        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync(ct);
        await using var query = new NpgsqlCommand("SELECT count(*) FROM content_items WHERE search_vector @@ 'marker'::tsquery", connection);
        Assert.True(1L == (long)(await query.ExecuteScalarAsync(ct))!, description);
    }

    private static string Repeat(string word, int count) => string.Join(' ', Enumerable.Repeat(word, count));

    private static string Build(string text)
    {
        var field = new TemplateField { TemplateVersionId = Guid.CreateVersion7(), Key = "body", Label = "Body", PrimitiveType = PrimitiveType.RichText };
        var schema = new TemplateVersion { TemplateId = Guid.CreateVersion7(), VersionNumber = 1 };
        schema.Fields.Add(field);
        var version = new ContentVersion { WorkspaceId = Guid.CreateVersion7(), TemplateVersionId = schema.Id };
        version.FieldValues.Add(new ContentVersionFieldValue { ContentVersionId = version.Id, FieldId = field.Id, ValueKind = ValueKind.Text, TextValue = text });
        return new ContentSearchVectorBuilder().Build(version, schema);
    }
}
