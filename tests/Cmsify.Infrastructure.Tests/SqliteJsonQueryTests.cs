using System.Data.Common;
using System.Text.Json;
using Cmsify.Core.Domain.Entities;
using Cmsify.Infrastructure.Extensions;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Sqlite.Extensions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Testcontainers.PostgreSql;

namespace Cmsify.Infrastructure.Tests;

public sealed class SqliteJsonQueryTests(ITestOutputHelper output)
{
    // Removing the optional provider's translator must break the real WHERE query.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StringPropertyPredicate_FiltersOnServer(bool sqlite)
    {
        await using var fixture = await Fixture.Create(sqlite);
        await fixture.Seed("{\"name\":\"café 🐾\"}", "{\"name\":\"other\"}");
        var expected = "café 🐾";
        fixture.Commands.Clear();
        (await fixture.Context.TemplateFields.CountAsync(row => row.FieldConfig.HasValue
            && row.FieldConfig.Value.GetProperty("name").GetString() == expected,
            TestContext.Current.CancellationToken)).ShouldBe(1);
        fixture.Commands.Last().ShouldContain("WHERE");
        fixture.Commands.Last().ShouldContain("COUNT");
        fixture.Parameters.Last().ShouldContain(expected);
        output.WriteLine(fixture.Commands.Last());
        output.WriteLine(JsonSerializer.Serialize(fixture.Parameters.Last()));
    }

    // Unescaped paths or treating a property parameter as SQL would select the wrong row.
    [Theory]
    [InlineData("name")]
    [InlineData("a.b")]
    [InlineData("a[0]")]
    [InlineData("")]
    [InlineData("a\"b")]
    [InlineData("a\\b")]
    [InlineData("a\".b")]
    [InlineData("café 🐾")]
    [InlineData("x'); DROP TABLE template_fields; --")]
    public async Task ParameterizedPropertyNamesAndValues_StayLiteral(string name)
    {
        foreach (var sqlite in new[] { true, false })
        {
            await using var fixture = await Fixture.Create(sqlite);
            var expected = "café 🐾 \"quoted\" \\ value '";
            await fixture.Seed(JsonSerializer.Serialize(new Dictionary<string, string> { [name] = expected }),
                JsonSerializer.Serialize(new Dictionary<string, string> { [name] = "other" }));
            fixture.Commands.Clear();
            (await fixture.Context.TemplateFields.CountAsync(row => row.FieldConfig.HasValue
                && row.FieldConfig.Value.GetProperty(name).GetString() == expected,
                TestContext.Current.CancellationToken)).ShouldBe(1);
            fixture.Commands.ShouldHaveSingleItem();
            fixture.Commands[0].ShouldNotContain(expected);
            if (name.Length > 0) fixture.Commands[0].ShouldNotContain("'" + name.Replace("'", "''") + "'");
            fixture.Parameters.Last().ShouldContain(expected);
            fixture.Parameters.Last().ShouldContain(name);
            output.WriteLine(fixture.Commands[0]);
            output.WriteLine(JsonSerializer.Serialize(fixture.Parameters.Last()));
        }
    }

    // Flattening nested paths, or dropping SQL-null guards, changes these counts.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NestedPropertiesAndMissingNulls_HaveBoundedSqlSemantics(bool sqlite)
    {
        await using var fixture = await Fixture.Create(sqlite);
        await fixture.Seed("{\"outer\":{\"name\":\"hit\"}}", "{\"outer\":{\"name\":null}}",
            "{\"outer\":{}}", "{}", null, "{\"outer\":42}", "{\"outer\":[]}",
            "{\"outer\":\"{\\\"name\\\":\\\"hit\\\"}\"}");
        var outer = "outer";
        var inner = "name";
        (await fixture.Context.TemplateFields.CountAsync(row => row.FieldConfig.HasValue
            && row.FieldConfig.Value.GetProperty(outer).GetProperty(inner).GetString() == "hit",
            TestContext.Current.CancellationToken)).ShouldBe(1);
        (await fixture.Context.TemplateFields.CountAsync(row => row.FieldConfig.HasValue
            && row.FieldConfig.Value.GetProperty(outer).GetProperty(inner).GetString() == null,
            TestContext.Current.CancellationToken)).ShouldBe(6);
        (await fixture.Context.TemplateFields.CountAsync(row => row.FieldConfig!.Value
            .GetProperty(outer).GetProperty(inner).GetString() == null,
            TestContext.Current.CancellationToken)).ShouldBe(7);
    }

    // Unsupported getters must fail before executing a command, never filter on the client.
    [Fact]
    public async Task TypedGettersAndArrayTraversal_AreRejectedBeforeExecution()
    {
        await using var fixture = await Fixture.Create(true);
        await fixture.Seed("{\"number\":42,\"array\":[\"hit\"]}");
        fixture.Commands.Clear();
        await Should.ThrowAsync<NotSupportedException>(() => fixture.Context.TemplateFields.CountAsync(row =>
            row.FieldConfig!.Value.GetProperty("number").GetInt32() == 42, TestContext.Current.CancellationToken));
        await Should.ThrowAsync<NotSupportedException>(() => fixture.Context.TemplateFields.CountAsync(row =>
            row.FieldConfig!.Value.GetProperty("array")[0].GetString() == "hit", TestContext.Current.CancellationToken));
        fixture.Commands.ShouldBeEmpty();
    }

    // json_quote(NULL) is the text "null", not an object property name.
    [Fact]
    public async Task NullPropertyNameParameter_IsRejectedButLiteralNullNameWorks()
    {
        await using var fixture = await Fixture.Create(true);
        await fixture.Seed("{\"null\":\"hit\"}", "{\"null\":\"other\"}");
        string? name = null;
        var error = await Should.ThrowAsync<SqliteException>(() => fixture.Context.TemplateFields.CountAsync(row =>
            row.FieldConfig!.Value.GetProperty(name!).GetString() == "hit", TestContext.Current.CancellationToken));
        error.Message.ShouldContain("Cmsify SQLite GetProperty requires a non-null name");
        name = "null";
        (await fixture.Context.TemplateFields.CountAsync(row => row.FieldConfig!.Value.GetProperty(name).GetString() == "hit",
            TestContext.Current.CancellationToken)).ShouldBe(1);
        (await fixture.Context.TemplateFields.CountAsync(row => row.FieldConfig!.Value.GetProperty("null").GetString() == "hit",
            TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    // EF must not materialize a JSON column to evaluate an unsupported member projection.
    [Fact]
    public async Task UnsupportedJsonMemberProjection_IsRejectedBeforeCommand()
    {
        await using var fixture = await Fixture.Create(true);
        await fixture.Seed("{\"name\":\"hit\"}");
        fixture.Commands.Clear();
        var error = await Record.ExceptionAsync(() => fixture.Context.TemplateFields
            .Select(row => row.FieldConfig!.Value.ValueKind).ToListAsync(TestContext.Current.CancellationToken));
        output.WriteLine(error?.ToString() ?? "Projection completed without rejection.");
        output.WriteLine("Executed commands: " + fixture.Commands.Count);
        error.ShouldBeOfType<NotSupportedException>();
        fixture.Commands.ShouldBeEmpty();
    }

    // An untranslatable traversal root must not turn the whole projection into CLR code.
    [Fact]
    public async Task JsonTraversalFromDeserializedColumn_IsRejectedBeforeCommand()
    {
        await using var fixture = await Fixture.Create(true);
        await fixture.Seed("{\"name\":\"hit\"}");
        var field = await fixture.Context.TemplateFields.SingleAsync(TestContext.Current.CancellationToken);
        field.Label = "{\"name\":\"hit\"}";
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        fixture.Commands.Clear();
        await Should.ThrowAsync<NotSupportedException>(() => fixture.Context.TemplateFields
            .Select(row => JsonSerializer.Deserialize<JsonElement>(row.Label, (JsonSerializerOptions?)null)
                .GetProperty("name").GetString()).ToListAsync(TestContext.Current.CancellationToken));
        fixture.Commands.ShouldBeEmpty();
    }

    [Fact]
    public async Task JsonTraversalFromConstructedEntity_IsRejectedBeforeCommand()
    {
        await using var fixture = await Fixture.Create(true);
        await fixture.Seed("{\"name\":\"hit\"}");
        var field = await fixture.Context.TemplateFields.SingleAsync(TestContext.Current.CancellationToken);
        field.Label = "{\"name\":\"hit\"}";
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        fixture.Commands.Clear();
        await Should.ThrowAsync<NotSupportedException>(() => fixture.Context.TemplateFields
            .Select(row => new TemplateField
            {
                Key = row.Key, Label = row.Label,
                FieldConfig = JsonSerializer.Deserialize<JsonElement>(row.Label, (JsonSerializerOptions?)null)
            }.FieldConfig!.Value.GetProperty("name").GetString()).ToListAsync(TestContext.Current.CancellationToken));
        fixture.Commands.ShouldBeEmpty();
    }

    [Fact]
    public async Task ComposedComputedEntityJsonTraversal_IsRejectedBeforeCommand()
    {
        await using var fixture = await Fixture.Create(true);
        await fixture.Seed("{\"name\":\"hit\"}");
        var field = await fixture.Context.TemplateFields.SingleAsync(TestContext.Current.CancellationToken);
        field.Label = "{\"name\":\"hit\"}";
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        fixture.Commands.Clear();
        var error = await Record.ExceptionAsync(() => fixture.Context.TemplateFields
            .Select(row => new TemplateField
            {
                Key = row.Key, Label = row.Label,
                FieldConfig = JsonSerializer.Deserialize<JsonElement>(row.Label, (JsonSerializerOptions?)null)
            }).Select(alias => alias.FieldConfig!.Value.GetProperty("name").GetString())
            .ToListAsync(TestContext.Current.CancellationToken));
        output.WriteLine(error?.ToString() ?? "Projection completed without rejection.");
        output.WriteLine("Executed commands: " + fixture.Commands.Count);
        error.ShouldBeOfType<NotSupportedException>();
        fixture.Commands.ShouldBeEmpty();
    }

    [Fact]
    public async Task JsonProducingMethodProjection_IsRejectedBeforeCommand()
    {
        await using var fixture = await Fixture.Create(true);
        await fixture.Seed("{\"name\":\"hit\"}");
        var field = await fixture.Context.TemplateFields.SingleAsync(TestContext.Current.CancellationToken);
        field.Label = "{\"name\":\"hit\"}";
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        fixture.Commands.Clear();
        var error = await Record.ExceptionAsync(() => fixture.Context.TemplateFields
            .Select(row => JsonSerializer.Deserialize<JsonElement>(row.Label, (JsonSerializerOptions?)null))
            .ToListAsync(TestContext.Current.CancellationToken));
        output.WriteLine(error?.ToString() ?? "Projection completed without rejection.");
        output.WriteLine("Executed commands: " + fixture.Commands.Count);
        error.ShouldBeOfType<NotSupportedException>();
        fixture.Commands.ShouldBeEmpty();
    }

    [Fact]
    public async Task WholeJsonColumnsAndEfPropertyTraversal_RemainServerSupported()
    {
        await using var fixture = await Fixture.Create(true);
        await fixture.Seed("{\"name\":\"hit\"}", "null", null);
        var documents = await fixture.Context.TemplateFields.OrderBy(row => row.Key).Select(row => row.FieldConfig)
            .ToListAsync(TestContext.Current.CancellationToken);
        documents[0]!.Value.GetProperty("name").GetString().ShouldBe("hit");
        documents[1]!.Value.ValueKind.ShouldBe(JsonValueKind.Null);
        documents[2].HasValue.ShouldBeFalse();
        var nonNullDocuments = await fixture.Context.TemplateFields.OrderBy(row => row.Key)
            .Where(row => row.FieldConfig.HasValue).Select(row => row.FieldConfig!.Value)
            .ToListAsync(TestContext.Current.CancellationToken);
        nonNullDocuments.Select(document => document.ValueKind).ShouldBe([JsonValueKind.Object, JsonValueKind.Null]);
        var name = "name";
        fixture.Commands.Clear();
        (await fixture.Context.TemplateFields.CountAsync(row => EF.Property<JsonElement?>(row, nameof(TemplateField.FieldConfig))!.Value
            .GetProperty(name).GetString() == "hit", TestContext.Current.CancellationToken)).ShouldBe(1);
        fixture.Commands.ShouldHaveSingleItem();
        fixture.Commands[0].ShouldContain("WHERE");
        output.WriteLine(fixture.Commands[0]);
        output.WriteLine(JsonSerializer.Serialize(fixture.Parameters.Last()));
    }

    // Casting a number or object into a string would silently broaden SQLite's supported scope.
    [Theory]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("{\"x\":1}")]
    [InlineData("[\"hit\"]")]
    public async Task NonStringTerminalValues_AreExplicitlyRejected(string value)
    {
        await using var fixture = await Fixture.Create(true);
        await fixture.Seed("{\"name\":" + value + "}");
        var error = await Should.ThrowAsync<SqliteException>(() => fixture.Context.TemplateFields.CountAsync(row =>
            row.FieldConfig!.Value.GetProperty("name").GetString() == "hit", TestContext.Current.CancellationToken));
        error.Message.ShouldContain("Cmsify SQLite GetString supports only JSON strings or null");
    }

    [Fact]
    public async Task Postgres_NativeGetStringStillReturnsTextForMixedKinds()
    {
        await using var fixture = await Fixture.Create(false);
        await fixture.Seed("{\"name\":42}", "{\"name\":true}", "{\"name\":{\"x\":1}}", "{\"name\":[\"hit\"]}");
        var values = await fixture.Context.TemplateFields.OrderBy(row => row.Key)
            .Select(row => row.FieldConfig!.Value.GetProperty("name").GetString()).ToListAsync(TestContext.Current.CancellationToken);
        values.ShouldBe(["42", "true", "{\"x\": 1}", "[\"hit\"]"]);
    }

    // Characterize native path escaping before choosing a translation shape.
    [Theory]
    [InlineData("name")]
    [InlineData("a.b")]
    [InlineData("a[0]")]
    [InlineData("")]
    [InlineData("a\"b")]
    [InlineData("a\\b")]
    [InlineData("a\".b")]
    [InlineData("café 🐾")]
    public async Task NativeJsonPath_CanAddressEscapedObjectName(string name)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT json_extract(@json, '$.' || json_quote(@name))";
        command.Parameters.AddWithValue("@json", JsonSerializer.Serialize(new Dictionary<string, string> { [name] = "hit" }));
        command.Parameters.AddWithValue("@name", name);
        (await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)).ShouldBe("hit");
        command.CommandText = "SELECT sqlite_version()";
        output.WriteLine("Native SQLite: " + await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"cmsify-json-{Guid.NewGuid():N}.db");
        private ServiceProvider _services = null!;
        private PostgreSqlContainer? _postgres;
        private IServiceScope _scope = null!;
        private readonly CommandProbe _probe = new();
        public CmsifyDbContext Context { get; private set; } = null!;
        public List<string> Commands => _probe.Commands;
        public List<List<object?>> Parameters => _probe.Parameters;
        public static async Task<Fixture> Create(bool sqlite)
        {
            var fixture = new Fixture();
            try
            {
                var services = SqliteRegistrationTests.Services();
                if (sqlite)
                    services.AddCmsifySqliteInfrastructure(SqliteRegistrationTests.Configuration($"Data Source={fixture._path};Pooling=False"),
                        new() { Workers = CmsifyWorkers.None });
                else
                {
                    fixture._postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
                    await fixture._postgres.StartAsync(TestContext.Current.CancellationToken);
                    services.AddCmsifyInfrastructure(SqliteRegistrationTests.Configuration(fixture._postgres.GetConnectionString()),
                        new() { Workers = CmsifyWorkers.None });
                }
                services.AddDbContext<CmsifyDbContext>(options => options.AddInterceptors(fixture._probe));
                fixture._services = services.BuildServiceProvider();
                fixture._scope = fixture._services.CreateScope();
                fixture.Context = fixture._scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
                if (sqlite)
                    await fixture._scope.ServiceProvider.GetRequiredService<ICmsifyDatabaseMigrator>()
                        .MigrateAsync(TestContext.Current.CancellationToken);
                else await fixture.Context.Database.MigrateAsync(TestContext.Current.CancellationToken);
                (await fixture.Context.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken)).ShouldNotBeEmpty();
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        public async Task Seed(params string?[] json)
        {
            var workspace = new Workspace { Name = "JSON query", Slug = "json-query" };
            var template = new Template { WorkspaceId = workspace.Id, Name = "JSON", Slug = "json" };
            var version = new TemplateVersion { TemplateId = template.Id, VersionNumber = 1 };
            Context.AddRange(workspace, template, version);
            Context.AddRange(json.Select((value, index) => new TemplateField
            {
                TemplateVersionId = version.Id, Key = $"field-{index}", Label = $"Field {index}",
                PrimitiveType = Cmsify.Core.Domain.Enums.PrimitiveType.Text,
                FieldConfig = value is null ? null : JsonSerializer.Deserialize<JsonElement>(value)
            }));
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            _scope?.Dispose();
            if (_services is not null) await _services.DisposeAsync();
            if (_postgres is not null) await _postgres.DisposeAsync();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(_path + suffix);
        }
    }

    private sealed class CommandProbe : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public List<List<object?>> Parameters { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            Parameters.Add(command.Parameters.Cast<DbParameter>().Select(parameter => parameter.Value).ToList());
            return new(result);
        }
    }
}
