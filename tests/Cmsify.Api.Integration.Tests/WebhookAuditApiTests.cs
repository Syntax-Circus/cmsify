using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;

namespace Cmsify.Api.Integration.Tests;

public sealed class WebhookAuditApiTests : IAsyncLifetime
{
    private const string ApiToken = "cmsify_webhook_audit_api_test_token";
    private static readonly string IntegrationEncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

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
        Environment.SetEnvironmentVariable("Secrets__ActiveKeyId", "integration");
        Environment.SetEnvironmentVariable("Secrets__EncryptionKeys__integration", IntegrationEncryptionKey);
    }

    public async ValueTask DisposeAsync()
    {
        await postgres.DisposeAsync();
        ClearEnvironment();
    }

    [Fact]
    public async Task ContentCreate_PersistsAClaimableWebhookOutboxEventWithoutInProcessDispatch()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var seed = await SeedApiClientAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
        Guid templateVersionId;

        using (var setupScope = factory.Services.CreateScope())
        {
            var setup = setupScope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
            var template = new Template { WorkspaceId = seed.WorkspaceId, Name = "Outbox content", Slug = "outbox-content" };
            var version = new TemplateVersion { TemplateId = template.Id, VersionNumber = 1, Status = TemplateVersionStatus.Published, PublishedAt = DateTimeOffset.UtcNow };
            setup.AddRange(template, version);
            await setup.SaveChangesAsync(TestContext.Current.CancellationToken);
            template.CurrentVersionId = version.Id;
            await setup.SaveChangesAsync(TestContext.Current.CancellationToken);
            templateVersionId = version.Id;
        }

        var createResponse = await client.PostAsJsonAsync($"/api/v1/workspaces/{seed.WorkspaceId}/content", new
        {
            templateVersionId,
            slug = "durable-content",
            tags = Array.Empty<string>(),
            fields = Array.Empty<object>()
        }, cancellationToken: TestContext.Current.CancellationToken);

        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        var contentId = created.GetProperty("id").GetGuid();
        using var verificationScope = factory.Services.CreateScope();
        var verification = verificationScope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var evt = await verification.WebhookOutboxEvents.AsNoTracking().SingleAsync(item => item.EntityId == contentId && item.EventType == "content.created", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEqual(Guid.Empty, evt.Id);
        Assert.Equal(seed.WorkspaceId, evt.WorkspaceId);
        Assert.Equal(contentId, evt.EntityId);
        Assert.Null(evt.ProcessedAt);
        Assert.Null(evt.LeaseOwner);
        Assert.Null(evt.LeaseToken);
        Assert.Null(evt.LeaseExpiresAt);
    }

    [Fact]
    public async Task ContentCreateUpdateAndPublish_PersistStableOutboxEvents()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var seed = await SeedApiClientAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
        var templateVersionId = await SeedPublishedTemplateVersionAsync(factory, seed.WorkspaceId, "content-mutations");

        var create = await client.PostAsJsonAsync($"/api/v1/workspaces/{seed.WorkspaceId}/content", new { templateVersionId, slug = "mutation-content", tags = Array.Empty<string>(), fields = Array.Empty<object>() }, cancellationToken: TestContext.Current.CancellationToken);
        create.EnsureSuccessStatusCode();
        var contentId = (await create.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken)).GetProperty("id").GetGuid();
        var current = await client.GetAsync($"/api/v1/workspaces/{seed.WorkspaceId}/content/{contentId}", TestContext.Current.CancellationToken);
        current.EnsureSuccessStatusCode();
        using var updateRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/workspaces/{seed.WorkspaceId}/content/{contentId}")
        {
            Content = JsonContent.Create(new { slug = "mutation-content-updated", tags = Array.Empty<string>(), fields = Array.Empty<object>() })
        };
        updateRequest.Headers.TryAddWithoutValidation("If-Match", current.Headers.ETag?.ToString());
        (await client.SendAsync(updateRequest, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        (await client.PostAsync($"/api/v1/workspaces/{seed.WorkspaceId}/content/{contentId}/submit", null, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        (await client.PostAsync($"/api/v1/workspaces/{seed.WorkspaceId}/content/{contentId}/approve", null, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        (await client.PostAsync($"/api/v1/workspaces/{seed.WorkspaceId}/content/{contentId}/publish", null, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        using var verificationScope = factory.Services.CreateScope();
        var verification = verificationScope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var events = await verification.WebhookOutboxEvents.AsNoTracking().Where(item => item.EntityId == contentId).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(6, events.Count);
        Assert.Equal(6, events.Select(item => item.Id).Distinct().Count());
        Assert.Contains(events, item => item.EventType == "content.created");
        Assert.Contains(events, item => item.EventType == "content.updated");
        Assert.Contains(events, item => item.EventType == "content.published");
        Assert.Equal(3, events.Count(item => item.EventType == "content.status_changed"));
        Assert.All(events, item => Assert.Equal(contentId, item.Payload.GetProperty("contentItemId").GetGuid()));
    }

    [Fact]
    public async Task ContentCreateEtag_AuthorizesImmediateConditionalUpdate()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var seed = await SeedApiClientAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
        var templateVersionId = await SeedPublishedTemplateVersionAsync(factory, seed.WorkspaceId, "content-create-etag");
        var fieldId = Guid.CreateVersion7();
        using (var setupScope = factory.Services.CreateScope())
        {
            var setup = setupScope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
            setup.TemplateFields.Add(new TemplateField
            {
                Id = fieldId,
                TemplateVersionId = templateVersionId,
                Key = "title",
                Label = "Title",
                IsRequired = true,
                MinOccurrences = 1,
                MaxOccurrences = 1,
                CompositionMode = CompositionMode.Inline,
                PrimitiveType = PrimitiveType.Text
            });
            await setup.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var create = await client.PostAsJsonAsync($"/api/v1/workspaces/{seed.WorkspaceId}/content", new
        {
            templateVersionId,
            slug = "create-etag",
            tags = Array.Empty<string>(),
            fields = new[] { new { fieldId, order = 0, valueKind = "Text", textValue = "Created" } }
        }, cancellationToken: TestContext.Current.CancellationToken);
        create.EnsureSuccessStatusCode();
        var contentId = (await create.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken)).GetProperty("id").GetGuid();
        Assert.NotNull(create.Headers.ETag);
        var updateBody = new
        {
            slug = "create-etag-updated",
            tags = Array.Empty<string>(),
            fields = new[] { new { fieldId, order = 0, valueKind = "Text", textValue = "Updated" } }
        };
        var missing = await client.PutAsJsonAsync($"/api/v1/workspaces/{seed.WorkspaceId}/content/{contentId}", updateBody, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.PreconditionFailed, missing.StatusCode);
        using (var staleRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/workspaces/{seed.WorkspaceId}/content/{contentId}")
        {
            Content = JsonContent.Create(updateBody)
        })
        {
            staleRequest.Headers.TryAddWithoutValidation("If-Match", "\"stale-etag\"");
            Assert.Equal(HttpStatusCode.PreconditionFailed, (await client.SendAsync(staleRequest, TestContext.Current.CancellationToken)).StatusCode);
        }
        using var updateRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/workspaces/{seed.WorkspaceId}/content/{contentId}")
        {
            Content = JsonContent.Create(updateBody)
        };
        updateRequest.Headers.TryAddWithoutValidation("If-Match", create.Headers.ETag.ToString());

        var update = await client.SendAsync(updateRequest, TestContext.Current.CancellationToken);

        update.EnsureSuccessStatusCode();
        var updatedContent = await update.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("create-etag-updated", updatedContent.GetProperty("slug").GetString());
        var updatedField = Assert.Single(updatedContent.GetProperty("fields").EnumerateArray());
        Assert.Equal(fieldId, updatedField.GetProperty("fieldId").GetGuid());
        Assert.Equal("Updated", updatedField.GetProperty("textValue").GetString());
    }

    [Fact]
    public async Task LegacyFullTickEtag_AuthorizesOnlyTheMatchingPostUpgradeMutation()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var seed = await SeedApiClientAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
        var templateVersionId = await SeedPublishedTemplateVersionAsync(factory, seed.WorkspaceId, "legacy-full-tick-etag");
        using var create = await client.PostAsJsonAsync($"/api/v1/workspaces/{seed.WorkspaceId}/content", new
        {
            templateVersionId,
            slug = "legacy-full-tick-etag",
            tags = Array.Empty<string>(),
            fields = Array.Empty<object>()
        }, cancellationToken: TestContext.Current.CancellationToken);
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        var contentId = created.GetProperty("id").GetGuid();
        var updatedAt = created.GetProperty("updatedAt").GetDateTimeOffset();
        var normalizedEtag = $"\"{updatedAt.UtcTicks / TimeSpan.TicksPerMicrosecond}\"";
        var legacyEtag = $"\"{updatedAt.UtcTicks}\"";
        Assert.Equal(normalizedEtag, create.Headers.ETag?.ToString());
        Assert.NotEqual(normalizedEtag, legacyEtag);

        var updateBody = new
        {
            slug = "legacy-full-tick-etag-updated",
            tags = Array.Empty<string>(),
            fields = Array.Empty<object>()
        };
        using (var updateRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/workspaces/{seed.WorkspaceId}/content/{contentId}")
        {
            Content = JsonContent.Create(updateBody)
        })
        {
            updateRequest.Headers.TryAddWithoutValidation("If-Match", legacyEtag);
            using var update = await client.SendAsync(updateRequest, TestContext.Current.CancellationToken);
            update.EnsureSuccessStatusCode();
        }

        using var staleRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/workspaces/{seed.WorkspaceId}/content/{contentId}")
        {
            Content = JsonContent.Create(updateBody)
        };
        staleRequest.Headers.TryAddWithoutValidation("If-Match", legacyEtag);
        using var stale = await client.SendAsync(staleRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
    }

    [Fact]
    public async Task TemplatePublish_PersistsTheEventWithThePublishedState()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var seed = await SeedApiClientAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
        var create = await client.PostAsJsonAsync($"/api/v1/workspaces/{seed.WorkspaceId}/templates", new { name = "Publish outbox", slug = "publish-outbox" }, cancellationToken: TestContext.Current.CancellationToken);
        create.EnsureSuccessStatusCode();
        var template = await create.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        var templateId = template.GetProperty("id").GetGuid();
        var versionNumber = template.GetProperty("currentVersion").GetProperty("versionNumber").GetInt32();

        (await client.PutAsync($"/api/v1/workspaces/{seed.WorkspaceId}/templates/{templateId}/versions/{versionNumber}/publish", null, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        using var verificationScope = factory.Services.CreateScope();
        var verification = verificationScope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var version = await verification.TemplateVersions.AsNoTracking().SingleAsync(item => item.TemplateId == templateId && item.VersionNumber == versionNumber, cancellationToken: TestContext.Current.CancellationToken);
        var evt = await verification.WebhookOutboxEvents.AsNoTracking().SingleAsync(item => item.EntityId == version.Id && item.EventType == "template.version_published", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TemplateVersionStatus.Published, version.Status);
        Assert.Equal(seed.WorkspaceId, evt.WorkspaceId);
        Assert.Equal(version.Id, evt.Payload.GetProperty("templateVersionId").GetGuid());
    }

    [Fact]
    public async Task ContentCreate_WhenTheOutboxInsertViolatesADatabaseConstraint_RollsBackTheContent()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var seed = await SeedApiClientAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
        var templateVersionId = await SeedPublishedTemplateVersionAsync(factory, seed.WorkspaceId, "rollback-content");
        using (var setupScope = factory.Services.CreateScope())
        {
            var setup = setupScope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
            await setup.Database.ExecuteSqlRawAsync("ALTER TABLE webhook_outbox_events ADD CONSTRAINT reject_content_created CHECK (event_type <> 'content.created')", cancellationToken: TestContext.Current.CancellationToken);
        }

        var response = await client.PostAsJsonAsync($"/api/v1/workspaces/{seed.WorkspaceId}/content", new { templateVersionId, slug = "rolled-back-content", tags = Array.Empty<string>(), fields = Array.Empty<object>() }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, response.StatusCode);
        using var verificationScope = factory.Services.CreateScope();
        var verification = verificationScope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        Assert.False(await verification.ContentItems.AnyAsync(item => item.WorkspaceId == seed.WorkspaceId && item.Slug == "rolled-back-content", cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await verification.WebhookOutboxEvents.AnyAsync(item => item.EventType == "content.created", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ImmediatePublish_ClearsAnExistingScheduledPublishLease()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var seed = await SeedApiClientAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
        var templateVersionId = await SeedPublishedTemplateVersionAsync(factory, seed.WorkspaceId, "manual-publish-lease");
        var leaseToken = Guid.CreateVersion7();
        Guid contentId;
        using (var setupScope = factory.Services.CreateScope())
        {
            var setup = setupScope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
            var content = new ContentItem
            {
                WorkspaceId = seed.WorkspaceId,
                TemplateVersionId = templateVersionId,
                Status = ContentStatus.Approved,
                Slug = "manual-publish-lease",
                PublishAt = DateTimeOffset.Parse("2026-08-27T00:00:00Z"),
                PublishLeaseOwner = "scheduled-worker",
                PublishLeaseToken = leaseToken,
                PublishLeaseExpiresAt = DateTimeOffset.Parse("2026-08-27T00:05:00Z")
            };
            setup.ContentItems.Add(content);
            await setup.SaveChangesAsync(TestContext.Current.CancellationToken);
            contentId = content.Id;
        }

        (await client.PostAsync($"/api/v1/workspaces/{seed.WorkspaceId}/content/{contentId}/publish", null, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        using var verificationScope = factory.Services.CreateScope();
        var verification = verificationScope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var persisted = await verification.ContentItems.AsNoTracking().SingleAsync(item => item.Id == contentId, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ContentStatus.Published, persisted.Status);
        Assert.Null(persisted.PublishLeaseOwner);
        Assert.Null(persisted.PublishLeaseToken);
        Assert.Null(persisted.PublishLeaseExpiresAt);
    }

    [Fact]
    public async Task LinkTranslation_ClearsScheduledPublishLeasesForBothInputs()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var seed = await SeedApiClientAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
        var templateVersionId = await SeedPublishedTemplateVersionAsync(factory, seed.WorkspaceId, "translation-lease");
        var sourceId = Guid.CreateVersion7();
        var targetId = Guid.CreateVersion7();
        using (var setupScope = factory.Services.CreateScope())
        {
            var setup = setupScope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
            setup.ContentItems.AddRange(
                new ContentItem
                {
                    Id = sourceId,
                    WorkspaceId = seed.WorkspaceId,
                    TemplateVersionId = templateVersionId,
                    Status = ContentStatus.Approved,
                    Slug = "translation-source",
                    PublishAt = DateTimeOffset.Parse("2026-08-27T00:00:00Z"),
                    PublishLeaseOwner = "scheduled-worker-a",
                    PublishLeaseToken = Guid.CreateVersion7(),
                    PublishLeaseExpiresAt = DateTimeOffset.Parse("2026-08-27T00:05:00Z")
                },
                new ContentItem
                {
                    Id = targetId,
                    WorkspaceId = seed.WorkspaceId,
                    TemplateVersionId = templateVersionId,
                    Status = ContentStatus.Approved,
                    Slug = "translation-target",
                    PublishAt = DateTimeOffset.Parse("2026-08-27T00:00:00Z"),
                    PublishLeaseOwner = "scheduled-worker-b",
                    PublishLeaseToken = Guid.CreateVersion7(),
                    PublishLeaseExpiresAt = DateTimeOffset.Parse("2026-08-27T00:05:00Z")
                });
            await setup.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        (await client.PostAsJsonAsync($"/api/v1/workspaces/{seed.WorkspaceId}/content/{sourceId}/link-translation", new { targetContentItemId = targetId }, cancellationToken: TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        using var verificationScope = factory.Services.CreateScope();
        var verification = verificationScope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var persisted = await verification.ContentItems.AsNoTracking()
            .Where(item => item.Id == sourceId || item.Id == targetId)
            .OrderBy(item => item.Id)
            .ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, persisted.Count);
        Assert.All(persisted, item =>
        {
            Assert.NotNull(item.TranslationGroupId);
            Assert.Null(item.PublishLeaseOwner);
            Assert.Null(item.PublishLeaseToken);
            Assert.Null(item.PublishLeaseExpiresAt);
        });
    }

    [Fact]
    public async Task DeliveryList_ProjectsStableEventAndTerminalDiagnostics()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var seed = await SeedApiClientAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
        var eventId = Guid.CreateVersion7();
        var deadLetteredAt = DateTimeOffset.Parse("2026-08-26T12:00:00Z");
        Guid endpointId;
        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
            var userId = await dbContext.Users.Select(user => user.Id).FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
            var endpoint = new WebhookEndpoint
            {
                WorkspaceId = seed.WorkspaceId,
                Name = "Terminal diagnostics",
                Url = "https://example.test/terminal",
                Secret = "secret",
                CreatedByUserId = userId
            };
            dbContext.AddRange(endpoint, new WebhookDeliveryLog
            {
                WebhookEndpointId = endpoint.Id,
                WebhookEventId = eventId,
                EventType = "content.published",
                Payload = JsonSerializer.SerializeToElement(new { contentItemId = Guid.CreateVersion7() }),
                AttemptCount = 10,
                LastError = "upstream returned 503",
                IsFailed = true,
                IsDeadLetter = true,
                DeadLetteredAt = deadLetteredAt
            });
            await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
            endpointId = endpoint.Id;
        }

        var response = await client.GetFromJsonAsync<JsonElement>($"/api/v1/workspaces/{seed.WorkspaceId}/webhooks/{endpointId}/deliveries?isFailed=true", cancellationToken: TestContext.Current.CancellationToken);

        var item = response.GetProperty("items")[0];
        Assert.Equal(eventId, item.GetProperty("eventId").GetGuid());
        Assert.Equal("upstream returned 503", item.GetProperty("lastError").GetString());
        Assert.True(item.GetProperty("isDeadLetter").GetBoolean());
        Assert.Equal(deadLetteredAt, item.GetProperty("deadLetteredAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task WebhookManagement_RotatesSecretsAndRequeuesFailedDeliveries()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var seed = await SeedApiClientAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);

        var createResponse = await client.PostAsJsonAsync($"/api/v1/workspaces/{seed.WorkspaceId}/webhooks", new
        {
            name = "Revalidator",
            url = "https://8.8.8.8/revalidate",
            secret = "plain-secret",
            events = new[] { "content.published" }
        }, cancellationToken: TestContext.Current.CancellationToken);
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        var endpointId = created.GetProperty("endpoint").GetProperty("id").GetGuid();
        Assert.Equal("plain-secret", created.GetProperty("secret").GetString());

        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
            var storedSecret = await dbContext.WebhookEndpoints.Where(endpoint => endpoint.Id == endpointId).Select(endpoint => endpoint.Secret).FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotEqual("plain-secret", storedSecret);

            dbContext.WebhookDeliveryLogs.Add(new WebhookDeliveryLog
            {
                WebhookEndpointId = endpointId,
                EventType = "content.published",
                Payload = JsonSerializer.SerializeToElement(new { contentItemId = Guid.CreateVersion7() }),
                AttemptCount = 3,
                IsDelivered = false,
                IsFailed = true,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var getResponse = await client.GetAsync($"/api/v1/workspaces/{seed.WorkspaceId}/webhooks/{endpointId}", TestContext.Current.CancellationToken);
        getResponse.EnsureSuccessStatusCode();
        var rotateResponse = await client.PostAsync($"/api/v1/workspaces/{seed.WorkspaceId}/webhooks/{endpointId}/rotate-secret", null, TestContext.Current.CancellationToken);
        rotateResponse.EnsureSuccessStatusCode();
        var rotated = await rotateResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.StartsWith("whsec_", rotated.GetProperty("secret").GetString());

        var deliveries = await client.GetFromJsonAsync<JsonElement>($"/api/v1/workspaces/{seed.WorkspaceId}/webhooks/{endpointId}/deliveries?isFailed=true", cancellationToken: TestContext.Current.CancellationToken);
        var deliveryId = deliveries.GetProperty("items")[0].GetProperty("id").GetGuid();
        var retryResponse = await client.PostAsync($"/api/v1/workspaces/{seed.WorkspaceId}/webhooks/{endpointId}/deliveries/{deliveryId}/retry", null, TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.Accepted, retryResponse.StatusCode);
    }

    [Fact]
    public async Task ManualRetry_ClearsTerminalAndLeaseStateWhileRetainingAttemptDiagnostics()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var seed = await SeedApiClientAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
        var attemptedAt = DateTimeOffset.Parse("2026-08-26T07:00:00Z");
        var deadLetteredAt = attemptedAt.AddMinutes(1);
        var leaseToken = Guid.CreateVersion7();
        Guid endpointId;
        Guid deliveryId;

        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
            var userId = await dbContext.Users.Select(user => user.Id).FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
            var endpoint = new WebhookEndpoint { WorkspaceId = seed.WorkspaceId, Name = "Manual retry", Url = "https://example.test/retry", Secret = "secret", CreatedByUserId = userId };
            var delivery = new WebhookDeliveryLog
            {
                WebhookEndpointId = endpoint.Id,
                EventType = "workspace.updated",
                Payload = JsonSerializer.SerializeToElement(new { workspaceId = seed.WorkspaceId }),
                AttemptCount = 4,
                LastAttemptAt = attemptedAt,
                LastError = "terminal upstream failure",
                IsFailed = true,
                IsDeadLetter = true,
                DeadLetteredAt = deadLetteredAt,
                LeaseOwner = "dead-worker",
                LeaseToken = leaseToken,
                LeaseExpiresAt = attemptedAt.AddMinutes(10)
            };
            dbContext.AddRange(endpoint, delivery);
            await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
            endpointId = endpoint.Id;
            deliveryId = delivery.Id;
        }

        var response = await client.PostAsync($"/api/v1/workspaces/{seed.WorkspaceId}/webhooks/{endpointId}/deliveries/{deliveryId}/retry", null, TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);
        using var verificationScope = factory.Services.CreateScope();
        var verification = verificationScope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var persisted = await verification.WebhookDeliveryLogs.AsNoTracking().SingleAsync(log => log.Id == deliveryId, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(persisted.IsFailed);
        Assert.False(persisted.IsDeadLetter);
        Assert.Null(persisted.DeadLetteredAt);
        Assert.Null(persisted.LeaseOwner);
        Assert.Null(persisted.LeaseToken);
        Assert.Null(persisted.LeaseExpiresAt);
        Assert.NotNull(persisted.NextRetryAt);
        Assert.Equal(4, persisted.AttemptCount);
        Assert.Equal(attemptedAt, persisted.LastAttemptAt);
        Assert.Equal("terminal upstream failure", persisted.LastError);
    }

    [Fact]
    public async Task ManualRetry_RejectsAnActivelyLeasedNonterminalDelivery()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var seed = await SeedApiClientAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
        var now = DateTimeOffset.Parse("2026-08-26T08:10:00Z");
        Guid endpointId;
        Guid deliveryId;

        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
            var userId = await dbContext.Users.Select(user => user.Id).FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
            var endpoint = new WebhookEndpoint { WorkspaceId = seed.WorkspaceId, Name = "In flight", Url = "https://example.test/in-flight", Secret = "secret", CreatedByUserId = userId };
            var delivery = new WebhookDeliveryLog
            {
                WebhookEndpointId = endpoint.Id,
                EventType = "workspace.updated",
                Payload = JsonSerializer.SerializeToElement(new { workspaceId = seed.WorkspaceId }),
                LeaseOwner = "active-worker",
                LeaseToken = Guid.CreateVersion7(),
                LeaseExpiresAt = now.AddMinutes(5),
                NextRetryAt = now
            };
            dbContext.AddRange(endpoint, delivery);
            await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
            endpointId = endpoint.Id;
            deliveryId = delivery.Id;
        }

        var response = await client.PostAsync($"/api/v1/workspaces/{seed.WorkspaceId}/webhooks/{endpointId}/deliveries/{deliveryId}/retry", null, TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);
        using var verificationScope = factory.Services.CreateScope();
        var verification = verificationScope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var persisted = await verification.WebhookDeliveryLogs.AsNoTracking().SingleAsync(log => log.Id == deliveryId, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("active-worker", persisted.LeaseOwner);
        Assert.NotNull(persisted.LeaseToken);
        Assert.Equal(now.AddMinutes(5), persisted.LeaseExpiresAt);
    }

    [Fact]
    public async Task WebhookManagement_RejectsPrivateDestinations()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var seed = await SeedApiClientAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);

        var response = await client.PostAsJsonAsync($"/api/v1/workspaces/{seed.WorkspaceId}/webhooks", new
        {
            name = "Unsafe",
            url = "https://127.0.0.1/hooks",
            events = new[] { "content.published" }
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task AuditQuery_ReturnsWorkspaceUpdatesWithActor()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var seed = await SeedApiClientAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);

        var workspaceResponse = await client.GetAsync($"/api/v1/workspaces/{seed.WorkspaceId}", TestContext.Current.CancellationToken);
        workspaceResponse.EnsureSuccessStatusCode();
        using var updateRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/workspaces/{seed.WorkspaceId}")
        {
            Content = JsonContent.Create(new { name = "Updated Default", slug = "updated-default", description = "Updated" })
        };
        updateRequest.Headers.TryAddWithoutValidation("If-Match", workspaceResponse.Headers.ETag?.ToString());
        var updateResponse = await client.SendAsync(updateRequest, TestContext.Current.CancellationToken);
        updateResponse.EnsureSuccessStatusCode();

        var audit = await client.GetFromJsonAsync<JsonElement>($"/api/v1/workspaces/{seed.WorkspaceId}/audit?entityType=Workspace&action=Updated&pageSize=10", cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(audit.GetProperty("totalCount").GetInt32() >= 1);
        var item = audit.GetProperty("items")[0];
        Assert.Equal("Workspace", item.GetProperty("entityType").GetString());
        Assert.Equal("ApiClient", item.GetProperty("actor").GetProperty("type").GetString());
        Assert.Equal(seed.ApiClientId, item.GetProperty("actor").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task WorkspaceCommit_PersistsAClaimableWebhookOutboxEventWithoutInProcessDispatch()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var seed = await SeedApiClientAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);

        var workspaceResponse = await client.GetAsync($"/api/v1/workspaces/{seed.WorkspaceId}", TestContext.Current.CancellationToken);
        workspaceResponse.EnsureSuccessStatusCode();
        using var updateRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/workspaces/{seed.WorkspaceId}")
        {
            Content = JsonContent.Create(new { name = "Outbox durable workspace", slug = "outbox-durable-workspace", description = "Durable webhook event" })
        };
        updateRequest.Headers.TryAddWithoutValidation("If-Match", workspaceResponse.Headers.ETag?.ToString());
        (await client.SendAsync(updateRequest, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var eventCount = await dbContext.Database.SqlQueryRaw<int>("""
            SELECT COUNT(*)::integer AS "Value"
            FROM webhook_outbox_events
            WHERE event_type = 'workspace.updated'
            """).SingleAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, eventCount);
    }

    private static WebApplicationFactory<Program> CreateFactory() => new WebApplicationFactory<Program>()
        .WithWebHostBuilder(builder => builder.ConfigureServices(services => services.RemoveAll<IHostedService>()));

    private static async Task<WebhookAuditSeed> SeedApiClientAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var workspaceId = await dbContext.Workspaces.Select(workspace => workspace.Id).FirstAsync();
        var adminUserId = await dbContext.Users.Select(user => user.Id).FirstAsync();
        var apiClient = await dbContext.ApiClients.FirstOrDefaultAsync(client => client.Name == "Webhook Audit API Test");
        if (apiClient is null)
        {
            apiClient = new ApiClient
            {
                Name = "Webhook Audit API Test",
                TokenHash = BCrypt.Net.BCrypt.HashPassword(ApiToken, 4),
                Role = UserRole.Admin,
                WorkspaceId = workspaceId,
                CreatedByUserId = adminUserId
            };
            dbContext.ApiClients.Add(apiClient);
            await dbContext.SaveChangesAsync();
        }

        return new WebhookAuditSeed(workspaceId, apiClient.Id);
    }

    private static async Task<Guid> SeedPublishedTemplateVersionAsync(WebApplicationFactory<Program> factory, Guid workspaceId, string slug)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var template = new Template { WorkspaceId = workspaceId, Name = slug, Slug = slug };
        var version = new TemplateVersion { TemplateId = template.Id, VersionNumber = 1, Status = TemplateVersionStatus.Published, PublishedAt = DateTimeOffset.UtcNow };
        dbContext.AddRange(template, version);
        await dbContext.SaveChangesAsync();
        template.CurrentVersionId = version.Id;
        await dbContext.SaveChangesAsync();
        return version.Id;
    }

    private static void ClearEnvironment()
    {
        foreach (var key in new[]
        {
            "ConnectionStrings__Cmsify",
            "Seed__Admin__Email",
            "Seed__Admin__Password",
            "Seed__DefaultWorkspace__Name",
            "Seed__DefaultWorkspace__Slug",
            "Secrets__ActiveKeyId",
            "Secrets__EncryptionKeys__integration"
        })
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    private sealed record WebhookAuditSeed(Guid WorkspaceId, Guid ApiClientId);
}
