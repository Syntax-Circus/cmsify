using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cmsify.Core.Domain.Entities;
using Cmsify.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SyntaxCircus.Cmsify.Contracts;
using Testcontainers.PostgreSql;
using CoreStatus = Cmsify.Core.Domain.Enums.ContentStatus;
using CoreValueKind = Cmsify.Core.Domain.Enums.ValueKind;

namespace Cmsify.Api.Integration.Tests;

public sealed class ContentVersionSaveCompatibilityTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions _json = CmsifyJsonOptions.Create();
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("cmsify").WithUsername("cmsify").WithPassword("cmsify").Build();

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
        foreach (var key in new[] { "ConnectionStrings__Cmsify", "Seed__Admin__Email", "Seed__Admin__Password", "Seed__DefaultWorkspace__Name", "Seed__DefaultWorkspace__Slug" })
            Environment.SetEnvironmentVariable(key, null);
    }

    [Theory]
    [InlineData("missing-item", 404, "Not Found", null, null)]
    [InlineData("wrong-workspace", 404, "Not Found", null, null)]
    [InlineData("read-only-workspace", 404, "Not Found", null, null)]
    [InlineData("deleted-item", 404, "Not Found", null, null)]
    [InlineData("missing-version", 404, "Not Found", null, null)]
    [InlineData("published", 409, "Only Draft, Review, or Approved versions can be edited", null, "conflict")]
    [InlineData("archived", 409, "Only Draft, Review, or Approved versions can be edited", null, "conflict")]
    [InlineData("revision", 412, "Concurrency mismatch", null, "concurrency-mismatch")]
    [InlineData("range-pair", 422, "Invalid effective range", "Provide both effectiveStartAt and effectiveEndAt, or neither.", "validation-failed")]
    [InlineData("range-order", 422, "Invalid effective range", "effectiveStartAt must be before effectiveEndAt.", "validation-failed")]
    [InlineData("template", 422, "Content validation failed", "Template version is unavailable.", "validation-failed")]
    [InlineData("field", 422, "Content validation failed", "Field 'title' expects Text values.", "validation-failed")]
    public async Task SavePreservesErrorPrecedence(string scenario, int status, string title, string? detail, string? code)
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = await LoginAsync(factory);
        var seed = await SeedAsync(factory);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
            var version = await db.ContentVersions.SingleAsync(v => v.Id == seed.VersionId, TestContext.Current.CancellationToken);
            if (scenario == "published") version.Status = CoreStatus.Published;
            if (scenario == "archived") version.Status = CoreStatus.Archived;
            if (scenario == "deleted-item") (await db.ContentItems.SingleAsync(i => i.Id == seed.ItemId, TestContext.Current.CancellationToken)).IsDeleted = true;
            if (scenario == "template") (await db.TemplateVersions.SingleAsync(t => t.Id == version.TemplateVersionId, TestContext.Current.CancellationToken)).IsDeleted = true;
            if (scenario == "read-only-workspace")
            {
                var editor = new User { Email = "restricted@example.test", DisplayName = "Restricted Editor", PasswordHash = BCrypt.Net.BCrypt.HashPassword("restricted-password", 4), Role = Cmsify.Core.Domain.Enums.UserRole.Editor, IsActive = true };
                editor.WorkspaceAccesses.Add(new UserWorkspaceAccess { WorkspaceId = seed.WorkspaceId, AccessLevel = Cmsify.Core.Domain.Enums.WorkspaceAccessLevel.Read });
                db.Users.Add(editor);
            }
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        if (scenario == "read-only-workspace")
        {
            using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("restricted@example.test", "restricted-password"), TestContext.Current.CancellationToken);
            login.EnsureSuccessStatusCode();
            var token = (await login.Content.ReadFromJsonAsync<LoginResponse>(TestContext.Current.CancellationToken))!.Token;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        var workspace = scenario == "wrong-workspace" ? Guid.NewGuid() : seed.WorkspaceId;
        var item = scenario == "missing-item" ? Guid.NewGuid() : seed.ItemId;
        var number = scenario == "missing-version" ? 99 : 1;
        var start = DateTimeOffset.Parse("2026-12-01T00:00:00Z");
        var fields = scenario == "field"
            ? new[] { new ContentFieldValueRequest(seed.FieldId, 0, ValueKind.Boolean, null, true, null, null, null, null) }
            : new[] { Field(Guid.NewGuid(), "invalid field") };
        var validatesContent = scenario is "template" or "field";
        var body = new UpdateContentVersionRequest(validatesContent ? null : start, scenario == "range-order" ? start : null, fields);
        var revision = scenario.StartsWith("range-", StringComparison.Ordinal) || validatesContent ? await RevisionAsync(factory, seed) : "\"0\"";
        using var response = await SaveAsync(client, $"/api/v1/workspaces/{workspace}/content/{item}/versions/{number}", revision, body);
        response.StatusCode.ShouldBe((HttpStatusCode)status);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        problem.RootElement.GetProperty("title").GetString().ShouldBe(title);
        if (detail is null) problem.RootElement.TryGetProperty("detail", out _).ShouldBeFalse();
        else problem.RootElement.GetProperty("detail").GetString().ShouldBe(detail);
        if (code is not null) problem.RootElement.GetProperty("type").GetString().ShouldBe("https://cmsify.dev/errors/" + code);
    }

    [Theory]
    [InlineData("current", true)]
    [InlineData("legacy", true)]
    [InlineData("missing", false)]
    [InlineData("blank", false)]
    [InlineData("weak", false)]
    [InlineData("wildcard", false)]
    [InlineData("list", false)]
    [InlineData("padding", true)]
    [InlineData("inner-padding", false)]
    [InlineData("leading-zero", false)]
    [InlineData("plus-sign", false)]
    [InlineData("overflow", false)]
    [InlineData("stale", false)]
    public async Task SaveAcceptsOnlyExactCurrentOrLegacyRevision(string spelling, bool accepted)
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = await LoginAsync(factory);
        var seed = await SeedAsync(factory);
        var current = await RevisionAsync(factory, seed);
        var number = current.Trim('"');
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var timestamp = (await db.ContentVersions.AsNoTracking().SingleAsync(v => v.Id == seed.VersionId, TestContext.Current.CancellationToken)).UpdatedAt;
        var header = spelling switch
        {
            "current" => current, "legacy" => $"\"{timestamp.UtcTicks}\"", "missing" => null,
            "blank" => " ", "weak" => "W/" + current, "wildcard" => "*", "list" => current + ", \"0\"",
            "padding" => " " + current + " ", "inner-padding" => $"\" {number} \"",
            "leading-zero" => $"\"0{number}\"", "plus-sign" => $"\"+{number}\"",
            "overflow" => "\"9223372036854775808\"", _ => "\"0\""
        };
        // Outer HTTP whitespace is normalized by this transport; whitespace inside quotes is not.
        // This captures the baseline HttpClient/TestServer path, not a raw network-server probe.
        using var response = await SaveAsync(client, seed.Url, header, new(null, null, [Field(seed.FieldId, "changed")]));
        response.StatusCode.ShouldBe(accepted ? HttpStatusCode.OK : HttpStatusCode.PreconditionFailed);
        if (accepted)
        {
            var output = (await response.Content.ReadFromJsonAsync<ContentVersionDetailResponse>(_json, TestContext.Current.CancellationToken))!;
            response.Headers.ETag!.Tag.ShouldBe($"\"{output.UpdatedAt.UtcTicks / TimeSpan.TicksPerMicrosecond}\"");
        }
        else
        {
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            problem.RootElement.GetProperty("title").GetString().ShouldBe("Concurrency mismatch");
            problem.RootElement.GetProperty("type").GetString().ShouldBe("https://cmsify.dev/errors/concurrency-mismatch");
            problem.RootElement.TryGetProperty("detail", out _).ShouldBeFalse();
        }
    }

    [Theory]
    [InlineData(CoreStatus.Draft)]
    [InlineData(CoreStatus.Review)]
    [InlineData(CoreStatus.Approved)]
    public async Task SaveReplacesFieldsAndInvalidatesScheduleWithoutChangingStatus(CoreStatus status)
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = await LoginAsync(factory);
        var seed = await SeedAsync(factory);
        Guid originalFieldId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
            var version = await db.ContentVersions.Include(v => v.FieldValues).SingleAsync(v => v.Id == seed.VersionId, TestContext.Current.CancellationToken);
            originalFieldId = version.FieldValues.Single().Id;
            version.Status = status;
            version.PublishAt = DateTimeOffset.UtcNow.AddDays(1);
            version.PublishLeaseOwner = "baseline-owner";
            version.PublishLeaseToken = Guid.NewGuid();
            version.PublishLeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        using var response = await SaveAsync(client, seed.Url, await RevisionAsync(factory, seed), new(null, null, [Field(seed.FieldId, "replacement")]));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var readScope = factory.Services.CreateScope();
        var read = readScope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var saved = await read.ContentVersions.Include(v => v.FieldValues).SingleAsync(v => v.Id == seed.VersionId, TestContext.Current.CancellationToken);
        saved.Status.ShouldBe(status);
        saved.PublishAt.ShouldBeNull(); saved.PublishLeaseOwner.ShouldBeNull(); saved.PublishLeaseToken.ShouldBeNull(); saved.PublishLeaseExpiresAt.ShouldBeNull();
        saved.FieldValues.Count.ShouldBe(1);
        saved.FieldValues.Single().TextValue.ShouldBe("replacement");
        saved.FieldValues.Single().Id.ShouldNotBe(originalFieldId);
        (await read.ContentVersionFieldValues.AnyAsync(f => f.Id == originalFieldId, TestContext.Current.CancellationToken)).ShouldBeFalse();
        using var empty = await SaveAsync(client, seed.Url, response.Headers.ETag!.Tag, new(null, null, []));
        empty.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await read.ContentVersionFieldValues.AsNoTracking().CountAsync(f => f.ContentVersionId == seed.VersionId, TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    public static IEnumerable<object[]> MalformedFieldCases()
    {
        foreach (var input in new[] { "unknown-kind", "null-element", "unknown-before-null" })
        foreach (var scenario in new[] { "missing-item", "hidden-workspace", "published", "revision", "range-pair", "range-order", "template", "reached" })
            yield return [input, scenario];
    }

    [Theory]
    [MemberData(nameof(MalformedFieldCases))]
    public async Task SaveMalformedFieldsRetainsBaselineHttpPrecedence(string input, string scenario)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new WebApplicationFactory<Program>();
        using var client = await LoginAsync(factory);
        var seed = await SeedAsync(factory);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
            var version = await db.ContentVersions.SingleAsync(v => v.Id == seed.VersionId, ct);
            if (scenario == "published") version.Status = CoreStatus.Published;
            if (scenario == "template") (await db.TemplateVersions.SingleAsync(t => t.Id == version.TemplateVersionId, ct)).IsDeleted = true;
            db.ApiClients.Add(new ApiClient { Name = "Malformed-field Editor", Role = Cmsify.Core.Domain.Enums.UserRole.Editor,
                WorkspaceId = seed.WorkspaceId, CreatedByUserId = await db.Users.Select(u => u.Id).FirstAsync(ct),
                TokenHash = BCrypt.Net.BCrypt.HashPassword("cmsify_malformed_field_editor", 4) });
            await db.SaveChangesAsync(ct);
        }
        client.DefaultRequestHeaders.Authorization = new("Bearer", "cmsify_malformed_field_editor");
        var workspace = scenario == "hidden-workspace" ? Guid.NewGuid() : seed.WorkspaceId;
        var item = scenario == "missing-item" ? Guid.NewGuid() : seed.ItemId;
        var kind = new ContentFieldValueRequest(seed.FieldId, 0, (ValueKind)999, null, null, null, null, null, null);
        ContentFieldValueRequest[] fields = input switch { "unknown-kind" => [kind], "null-element" => [null!], _ => [kind, null!] };
        var start = DateTimeOffset.Parse("2026-12-01T00:00:00Z");
        var body = new UpdateContentVersionRequest(scenario.StartsWith("range-", StringComparison.Ordinal) ? start : null,
            scenario == "range-order" ? start : null, fields);
        var revision = scenario == "revision" ? "\"0\"" : await RevisionAsync(factory, seed);
        using var response = await SaveAsync(client, $"/api/v1/workspaces/{workspace}/content/{item}/versions/1", revision, body);
        var (status, title, detail, code) = scenario switch
        {
            "missing-item" or "hidden-workspace" => (404, "Not Found", (string?)null, "not-found"),
            "published" => (409, "Only Draft, Review, or Approved versions can be edited", null, "conflict"),
            "revision" => (412, "Concurrency mismatch", null, "concurrency-mismatch"),
            "range-pair" => (422, "Invalid effective range", "Provide both effectiveStartAt and effectiveEndAt, or neither.", "validation-failed"),
            "range-order" => (422, "Invalid effective range", "effectiveStartAt must be before effectiveEndAt.", "validation-failed"),
            "template" => (422, "Content validation failed", "Template version is unavailable.", "validation-failed"),
            _ when input == "unknown-kind" => (400, (string?)null, "Specified argument was out of the range of valid values. (Parameter 'value')" + Environment.NewLine + "Actual value was 999.", "bad-request"),
            _ => (500, null, "An unexpected error occurred.", "internal-server-error")
        };
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        Console.WriteLine($"MALFORMED {input}/{scenario}: {(int)response.StatusCode} {problem}");
        ((int)response.StatusCode).ShouldBe(status);
        if (title is null) problem.TryGetProperty("title", out _).ShouldBeFalse();
        else problem.GetProperty("title").GetString().ShouldBe(title);
        if (detail is null) problem.TryGetProperty("detail", out _).ShouldBeFalse();
        else problem.GetProperty("detail").GetString().ShouldBe(detail);
        problem.GetProperty("type").GetString().ShouldBe("https://cmsify.dev/errors/" + code);
        using var readScope = factory.Services.CreateScope();
        var read = readScope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        (await read.ContentVersionFieldValues.Where(f => f.ContentVersionId == seed.VersionId).Select(f => f.TextValue).SingleAsync(ct)).ShouldBe("original");
        (await read.WebhookOutboxEvents.CountAsync(e => e.EntityId == seed.ItemId, ct)).ShouldBe(0);
    }

    private sealed record Seed(Guid WorkspaceId, Guid ItemId, Guid VersionId, Guid FieldId)
    {
        public string Url => $"/api/v1/workspaces/{WorkspaceId}/content/{ItemId}/versions/1";
    }

    private static ContentFieldValueRequest Field(Guid id, string value) => new(id, 0, ValueKind.Text, value, null, null, null, null, null);

    private static async Task<HttpClient> LoginAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("admin@example.test", "change-this-temporary-password"), TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var login = (await response.Content.ReadFromJsonAsync<LoginResponse>(TestContext.Current.CancellationToken))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        return client;
    }

    private static async Task<Seed> SeedAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var workspace = await db.Workspaces.Select(w => w.Id).FirstAsync(TestContext.Current.CancellationToken);
        var template = new Template { WorkspaceId = workspace, Name = "Page", Slug = $"page-{Guid.NewGuid():N}" };
        var templateVersion = new TemplateVersion { TemplateId = template.Id, VersionNumber = 1, Status = Cmsify.Core.Domain.Enums.TemplateVersionStatus.Published };
        var field = new TemplateField { TemplateVersionId = templateVersion.Id, Key = "title", Label = "Title", PrimitiveType = Cmsify.Core.Domain.Enums.PrimitiveType.Text };
        var item = new ContentItem { WorkspaceId = workspace, TemplateVersionId = templateVersion.Id, Slug = $"page-{Guid.NewGuid():N}" };
        var version = new ContentVersion { WorkspaceId = workspace, ContentItemId = item.Id, TemplateVersionId = templateVersion.Id, VersionNumber = 1, Status = CoreStatus.Draft, Slug = item.Slug };
        version.FieldValues.Add(new ContentVersionFieldValue { ContentVersionId = version.Id, FieldId = field.Id, ValueKind = CoreValueKind.Text, TextValue = "original" });
        db.Templates.Add(template); db.TemplateVersions.Add(templateVersion); db.TemplateFields.Add(field);
        db.ContentItems.Add(item); db.ContentVersions.Add(version);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return new(workspace, item.Id, version.Id, field.Id);
    }

    private static async Task<string> RevisionAsync(WebApplicationFactory<Program> factory, Seed seed)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var timestamp = await db.ContentVersions.Where(v => v.Id == seed.VersionId).Select(v => v.UpdatedAt).SingleAsync(TestContext.Current.CancellationToken);
        return $"\"{timestamp.UtcTicks / TimeSpan.TicksPerMicrosecond}\"";
    }

    private static async Task<HttpResponseMessage> SaveAsync(HttpClient client, string url, string? header, UpdateContentVersionRequest body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(body, options: _json) };
        if (header is not null) request.Headers.TryAddWithoutValidation("If-Match", header).ShouldBeTrue();
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
