using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Tests;

public sealed class ContentListQueryFeasibilityTests(ITestOutputHelper output)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset _start = new(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);

    static ContentListQueryFeasibilityTests()
    {
        // Only the focused diagnostic processes opt in; ordinary suite culture is untouched.
        if (Environment.GetEnvironmentVariable("CMSIFY_CONTENT_QUERY_CULTURE") is { Length: > 0 } name)
        {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo(name);
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo(name);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistedTimestampPrecisionIsMeasuredBeforeRanking(bool sqlite)
    {
        await using var fixture = await ContentListQueryFixtures.Create(sqlite);
        await using var context = fixture.Context();
        var item = fixture.Item("precision");
        var start = new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);
        var shorter = fixture.Version(item, 1, start, start.AddTicks(10));
        var longer = fixture.Version(item, 2, start, start.AddTicks(11));
        context.AddRange(item, shorter, longer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.ChangeTracker.Clear();
        var stored = await context.ContentVersions.OrderBy(v => v.VersionNumber).ToArrayAsync(TestContext.Current.CancellationToken);
        var durations = stored.Select(v => (v.EffectiveEndAt!.Value - v.EffectiveStartAt!.Value).Ticks).ToArray();
        output.WriteLine($"Provider={context.Database.ProviderName}; requested ticks=10,11; stored ticks={string.Join(',', durations)}");
        Assert.Equal(sqlite ? new long[] { 10, 11 } : new long[] { 10, 10 }, durations);
        Assert.Equal(sqlite ? shorter.Id : longer.Id, await WinnerAt(fixture, item.Id, start));
        await Metadata(fixture);
    }

    [Fact]
    public async Task CurrentPostgresExpressionsAreExplicitlyUnsupportedOnMigratedSqlite()
    {
        await using var fixture = await ContentListQueryFixtures.Create(true);
        await using var context = fixture.Context();
        var textFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => context.ContentVersions
            .Where(v => EF.Functions.ILike(v.Slug!, "%needle%", "")).CountAsync(Ct));
        output.WriteLine("Direct ILIKE failure: " + textFailure.Message);
        Assert.Contains("could not be translated", textFailure.Message);
        var tags = new[] { "news" };
        // SQLite cannot translate the released PostgreSQL array containment shape.
        var tagFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => context.ContentVersions
            .Where(v => tags.All(tag => v.Tags.Contains(tag))).CountAsync(Ct));
        Assert.Contains("could not be translated", tagFailure.Message);
        output.WriteLine("Direct array containment failure: " + tagFailure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TagsAreParameterizedExactMembersAndParsingRetainsCommaSeparator(bool sqlite)
    {
        await using var fixture = await ContentListQueryFixtures.Create(sqlite);
        await using var context = fixture.Context();
        var exact = fixture.Item("exact");
        var substring = fixture.Item("substring");
        var firstOnly = fixture.Item("first-only");
        var secondOnly = fixture.Item("second-only");
        const string unusual = "a'\\%_";
        const string sqlLooking = "'); DROP TABLE content_versions; --";
        var exactVersion = fixture.Version(exact, 1);
        exactVersion.Tags = [unusual, "café", "news", "", "news", sqlLooking, "comma,tag"];
        var substringVersion = fixture.Version(substring, 1);
        substringVersion.Tags = ["newspaper", unusual + "x", "caféx"];
        var firstVersion = fixture.Version(firstOnly, 1);
        firstVersion.Tags = [unusual];
        var secondVersion = fixture.Version(secondOnly, 1);
        secondVersion.Tags = ["café"];
        context.AddRange(exact, substring, firstOnly, secondOnly, exactVersion, substringVersion, firstVersion, secondVersion);
        await context.SaveChangesAsync(Ct);
        Assert.Equal(new[] { exact.Id }, await MatchTags(fixture, unusual, "café"));
        Assert.DoesNotContain(substring.Id, await MatchTags(fixture, "news"));
        Assert.Equal(new[] { exact.Id }, await MatchTags(fixture, "news", "news"));
        Assert.Equal(new[] { exact.Id }, await MatchTags(fixture, ""));
        Assert.Equal(new[] { exact.Id }, await MatchTags(fixture, sqlLooking));
        Assert.Equal(new[] { exact.Id }, await MatchTags(fixture, "comma,tag"));
        Assert.Equal(4L, Assert.Single(await Read(fixture, "SELECT COUNT(*) FROM content_versions", r => r.GetInt64(0))));
        var parsed = " comma,tag,NEWS,news, ".Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(tag => tag.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "comma", "tag", "news" }, parsed);
        Assert.Empty(await MatchTags(fixture, parsed));
        if (sqlite)
        {
            // The stored JSON envelope can contain non-strings even though the CLR list cannot.
            await Execute(fixture, "UPDATE content_versions SET tags=@tags WHERE id=@id", ("tags", "[null,1,true,{},[],\"news\",\"1\",\"\"]"), ("id", exactVersion.Id));
            Assert.Equal(new[] { exact.Id }, await MatchTags(fixture, "news", "1", ""));
            Assert.Empty(await MatchTags(fixture, "true"));
            Assert.Empty(await MatchTags(fixture, "null"));
        }
        await Metadata(fixture);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactMappedWindowRankBoundariesInvalidBoundsAndExtremeSpans(bool sqlite)
    {
        await using var fixture = await ContentListQueryFixtures.Create(sqlite);
        await using var context = fixture.Context();
        var item = fixture.Item("window");
        var end = _start.AddTicks(110);
        var boundedShorter = fixture.Version(item, 1, _start, end);
        var longer = fixture.Version(item, 3, _start.AddTicks(-10), end);
        var fallback = fixture.Version(item, 9);
        context.AddRange(item, boundedShorter, longer, fallback);
        await context.SaveChangesAsync(Ct);
        Assert.Equal(boundedShorter.Id, await WinnerAt(fixture, item.Id, _start));
        Assert.Equal(fallback.Id, await WinnerAt(fixture, item.Id, end));
        Assert.Equal(fallback.Id, await WinnerAt(fixture, item.Id, _start.AddTicks(-20)));
        await WinnerWithDurations(fixture, 100, 110);
        await WinnerWithDurations(fixture, 10, 11);

        var extremeItem = fixture.Item("extreme");
        var extremeStart = new DateTimeOffset(2, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var extremeEnd = new DateTimeOffset(9998, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var extremeLong = fixture.Version(extremeItem, 5, extremeStart, extremeEnd);
        var extremeShort = fixture.Version(extremeItem, 1, extremeStart, extremeEnd.AddTicks(-10));
        context.AddRange(extremeItem, extremeLong, extremeShort);
        await context.SaveChangesAsync(Ct);
        Assert.Equal(extremeShort.Id, await WinnerAt(fixture, extremeItem.Id, _start));
        output.WriteLine($"Extreme duration requested ticks={(extremeEnd - extremeStart).Ticks}; winner={extremeShort.Id}");

        var tiedItem = fixture.Item("publication-tie");
        var highVersionNoPublication = fixture.Version(tiedItem, 10, _start, end);
        var publishedOlder = fixture.Version(tiedItem, 8, _start, end);
        publishedOlder.PublishedAt = _start.AddDays(-2);
        var publishedNewer = fixture.Version(tiedItem, 1, _start, end);
        publishedNewer.PublishedAt = _start.AddDays(-1);
        context.AddRange(tiedItem, highVersionNoPublication, publishedOlder, publishedNewer);
        await context.SaveChangesAsync(Ct);
        Assert.Equal(publishedNewer.Id, await WinnerAt(fixture, tiedItem.Id, _start));

        foreach (var startOnly in new[] { true, false })
        {
            await using var invalidContext = fixture.Context();
            var invalid = fixture.Version(item, startOnly ? 20 : 21, startOnly ? _start : null, startOnly ? null : _start);
            invalidContext.Add(invalid);
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => invalidContext.SaveChangesAsync(Ct));
            output.WriteLine($"One-sided bounds rejected startOnly={startOnly}: {error.InnerException?.Message}");
        }
        await Metadata(fixture);
    }

    [Theory]
    [InlineData(false, "en-US")]
    [InlineData(false, "tr-TR")]
    [InlineData(true, "en-US")]
    [InlineData(true, "tr-TR")]
    public async Task TextMatchingAndNativeOrderingAreMeasuredAcrossCulturesAndReopenedPools(bool sqlite, string culture)
    {
        var prior = CultureInfo.CurrentCulture;
        var priorUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            await using var fixture = await ContentListQueryFixtures.Create(sqlite);
            string[] texts = ["a", "A", "café", "CAFÉ", "I", "i", "İ", "ı", "ß", "SS", "é", "e\u0301", "😀", "percent%value", "percentXvalue", "under_score", "underXscore", "back\\slash", "backslash", "quote'value"];
            var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
            await using (var context = fixture.Context())
            {
                foreach (var text in texts)
                {
                    var item = fixture.Item(text);
                    ids.Add(text, item.Id);
                    context.AddRange(item, fixture.Version(item, 1));
                }
                await context.SaveChangesAsync(Ct);
            }
            foreach (var resolved in new[] { false, true })
            {
                foreach (var input in texts.Concat(new[] { "", "  ", "\\", "%", "_" }))
                {
                    var matched = await MatchText(fixture, input, resolved);
                    Assert.Equal(matched, await MatchText(fixture, input, resolved));
                    if (ids.TryGetValue(input, out var exact)) Assert.Contains(exact, matched);
                    if (string.IsNullOrWhiteSpace(input)) Assert.Equal(texts.Length, matched.Length);
                    if (input == "a") Assert.Contains(ids["A"], matched);
                    if (input is "😀" or "quote'value" or "e\u0301" or "ı" or "ß" or "SS") Assert.Equal(new[] { ids[input] }, matched);
                    if (resolved && input is "%" or "_" or "\\")
                    {
                        var literal = input switch { "%" => "percent%value", "_" => "under_score", _ => "back\\slash" };
                        Assert.Equal(new[] { ids[literal] }, matched);
                    }
                    if (!resolved && input is "%" or "_") Assert.Equal(texts.Length, matched.Length);
                    if (input == "percent%value") Assert.Equal(Sorted(ids["percent%value"], resolved ? null : ids["percentXvalue"]), matched);
                    if (input == "under_score") Assert.Equal(Sorted(ids["under_score"], resolved ? null : ids["underXscore"]), matched);
                    if (input == "back\\slash") Assert.Equal(new[] { ids[input] }, matched);
                    output.WriteLine($"culture={culture}; sqlite={sqlite}; resolved={resolved}; Q={JsonSerializer.Serialize(input)}; matches={JsonSerializer.Serialize(texts.Where(text => matched.Contains(ids[text])))}");
                }
            }
            var ordered = await Read(fixture, "SELECT slug FROM content_versions ORDER BY slug", r => r.GetString(0));
            Assert.Equal(ordered, await Read(fixture, "SELECT slug FROM content_versions ORDER BY slug", r => r.GetString(0)));
            output.WriteLine($"culture={culture}; sqlite={sqlite}; native order={JsonSerializer.Serialize(ordered)}");
            await Metadata(fixture);
        }
        finally
        {
            CultureInfo.CurrentCulture = prior;
            CultureInfo.CurrentUICulture = priorUi;
        }
    }

    private async Task<Guid> WinnerWithDurations(ContentListQueryFixtures fixture, long shorterTicks, long longerTicks)
    {
        await using var context = fixture.Context();
        var item = fixture.Item($"duration-{shorterTicks}-{longerTicks}");
        var shorter = fixture.Version(item, 1, _start, _start.AddTicks(shorterTicks));
        var longer = fixture.Version(item, 2, _start, _start.AddTicks(longerTicks));
        context.AddRange(item, shorter, longer);
        await context.SaveChangesAsync(Ct);
        context.ChangeTracker.Clear();
        var stored = await context.ContentVersions.Where(v => v.ContentItemId == item.Id).OrderBy(v => v.VersionNumber).ToArrayAsync(Ct);
        var durations = stored.Select(v => (v.EffectiveEndAt!.Value - v.EffectiveStartAt!.Value).Ticks).ToArray();
        Assert.Equal(fixture.Sqlite || shorterTicks == 100 ? new[] { shorterTicks, longerTicks } : new long[] { 10, 10 }, durations);
        var expectedVersion = fixture.Sqlite || shorterTicks == 100 ? shorter.Id : longer.Id;
        var actual = await WinnerAt(fixture, item.Id, _start);
        output.WriteLine($"requested ticks={shorterTicks},{longerTicks}; persisted ticks={string.Join(',', durations)}; expected={expectedVersion}; actual={actual}");
        Assert.Equal(expectedVersion, actual);
        return actual;
    }

    private async Task<Guid> WinnerAt(ContentListQueryFixtures fixture, Guid owner, DateTimeOffset asOf)
    {
        const string sql = "SELECT id FROM content_versions WHERE content_item_id=@owner AND status='Published' AND ((effective_start_at IS NULL AND effective_end_at IS NULL) OR (effective_start_at <= @at AND @at < effective_end_at)) ORDER BY CASE WHEN effective_start_at IS NOT NULL AND effective_end_at IS NOT NULL THEN 0 ELSE 1 END, (effective_end_at-effective_start_at), published_at DESC NULLS LAST, version_number DESC LIMIT 1";
        return Assert.Single(await Read(fixture, sql, r => GuidValue(r, 0), ("owner", owner), ("at", fixture.Sqlite ? asOf.UtcTicks : asOf)));
    }

    private async Task<Guid[]> MatchTags(ContentListQueryFixtures fixture, params string[] tags)
    {
        var predicates = tags.Select((_, i) => fixture.Sqlite
            ? $"EXISTS (SELECT 1 FROM json_each(v.tags) AS tag WHERE tag.type='text' AND tag.value=@tag{i} COLLATE BINARY)"
            : $"v.tags @> @tag{i}");
        var sql = "SELECT DISTINCT content_item_id FROM content_versions v" + (tags.Length == 0 ? "" : " WHERE " + string.Join(" AND ", predicates)) + " ORDER BY content_item_id";
        return await CountAndPageMatches(fixture, sql, tags.Select((tag, i) => ($"tag{i}", fixture.Sqlite ? (object)tag : new[] { tag })).ToArray());
    }

    private async Task<Guid[]> MatchText(ContentListQueryFixtures fixture, string input, bool resolved)
    {
        var pattern = resolved ? input.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) : input;
        var predicate = fixture.Sqlite ? "slug LIKE @pattern" + (resolved ? " ESCAPE '\\'" : "") : "slug ILIKE @pattern ESCAPE " + (resolved ? "'\\'" : "''");
        var sql = "SELECT content_item_id FROM content_versions" + (string.IsNullOrWhiteSpace(input) ? "" : " WHERE " + predicate) + " ORDER BY content_item_id";
        return await CountAndPageMatches(fixture, sql, ("pattern", "%" + pattern + "%"));
    }

    private async Task<Guid[]> CountAndPageMatches(ContentListQueryFixtures fixture, string sql, params (string Name, object Value)[] parameters)
    {
        var rows = await Read(fixture, sql, r => GuidValue(r, 0), parameters);
        var count = Assert.Single(await Read(fixture, "SELECT COUNT(*) FROM (" + sql + ") AS matched", r => r.GetInt64(0), parameters));
        Assert.Equal(rows.LongLength, count);
        var page = await Read(fixture, sql + " LIMIT @take OFFSET @skip", r => GuidValue(r, 0), parameters.Concat(new[] { ("take", (object)1), ("skip", (object)1) }).ToArray());
        Assert.Equal(rows.Skip(1).Take(1), page);
        return rows;
    }

    private static Guid[] Sorted(Guid first, Guid? second) => second is null ? [first] : new[] { first, second.Value }.Order().ToArray();
    private static Guid GuidValue(DbDataReader reader, int index) => reader.GetValue(index) is Guid guid ? guid : Guid.Parse(reader.GetString(index));

    private async Task<T[]> Read<T>(ContentListQueryFixtures fixture, string sql, Func<DbDataReader, T> project, params (string Name, object Value)[] parameters)
    {
        await using var context = fixture.Context();
        await context.Database.OpenConnectionAsync(Ct);
        await using var command = context.Database.GetDbConnection().CreateCommand();
        Configure(command, sql, fixture.Sqlite, parameters);
        output.WriteLine("SQL: " + sql + "; parameter values=" + JsonSerializer.Serialize(parameters.Select(p => new { p.Name, p.Value })));
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<T>();
        while (await reader.ReadAsync(Ct)) rows.Add(project(reader));
        return rows.ToArray();
    }

    private static void Configure(DbCommand command, string sql, bool sqlite, (string Name, object Value)[] parameters)
    {
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = sqlite && value is Guid guid ? guid.ToString().ToUpperInvariant() : value;
            command.Parameters.Add(parameter);
        }
    }

    private static async Task Execute(ContentListQueryFixtures fixture, string sql, params (string Name, object Value)[] parameters)
    {
        await using var context = fixture.Context();
        await context.Database.OpenConnectionAsync(Ct);
        await using var command = context.Database.GetDbConnection().CreateCommand();
        Configure(command, sql, fixture.Sqlite, parameters);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task Metadata(ContentListQueryFixtures fixture)
    {
        var sql = fixture.Sqlite ? "SELECT sqlite_version()" : "SELECT version() || '; LC_COLLATE=' || datcollate || '; LC_CTYPE=' || datctype FROM pg_database WHERE datname=current_database()";
        output.WriteLine("Database: " + Assert.Single(await Read(fixture, sql, r => r.GetString(0))));
        await using var context = fixture.Context();
        output.WriteLine($"Provider={context.Database.ProviderName}; EF={typeof(DbContext).Assembly.GetName().Version}; Sqlite={typeof(Microsoft.Data.Sqlite.SqliteConnection).Assembly.GetName().Version}; Npgsql={typeof(Npgsql.NpgsqlConnection).Assembly.GetName().Version}; runtime={Environment.Version}; process default culture={CultureInfo.DefaultThreadCurrentCulture?.Name ?? "system"}");
    }
}
