using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Repositories;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.BackgroundServices;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Persistence.Repositories;
using Cmsify.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using SyntaxCircus.EntityFrameworkCore.Postgres;
using Testcontainers.PostgreSql;
using System.Net;
using System.Data.Common;
using System.Text.Json;

namespace Cmsify.Infrastructure.Tests;

public sealed class WebhookDurabilityRepositoryTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("cmsify")
        .WithUsername("cmsify")
        .WithPassword("cmsify")
        .Build();

    public ValueTask InitializeAsync() => new(postgres.StartAsync());

    public async ValueTask DisposeAsync() => await postgres.DisposeAsync();

    [Fact]
    public async Task GetActiveEndpointsForEvent_WhenStoredSecretUsesAnUnconfiguredKey_RecordsOneBoundedDecryptFailureAndRethrows()
    {
        const string unconfiguredKeyId = "attacker-controlled-key";
        var configuredKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        await using (var setup = await CreateContextAsync())
        {
            var workspace = new Workspace { Name = "Telemetry", Slug = "telemetry" };
            var user = new User { Email = "telemetry@example.test", DisplayName = "Telemetry", PasswordHash = "hash", Role = UserRole.Admin };
            var endpoint = new WebhookEndpoint
            {
                WorkspaceId = workspace.Id,
                Name = "Telemetry endpoint",
                Url = "https://example.test/hook",
                Secret = $"v2.{unconfiguredKeyId}.AQIDBAUGBwgJCgsM.AQIDBAUGBwgJCgsMDQ4PEA==.AQ==",
                CreatedByUserId = user.Id
            };
            endpoint.Subscriptions.Add(new WebhookSubscription { WebhookEndpointId = endpoint.Id, EventType = "workspace.updated" });
            setup.AddRange(workspace, user, endpoint);
            await setup.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var options = Options.Create(new SecretProtectionOptions
        {
            ActiveKeyId = "key_current",
            EncryptionKeys = new Dictionary<string, string>(StringComparer.Ordinal) { ["key_current"] = configuredKey }
        });
        var protector = new AesSecretProtector(options);
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        var failures = new List<List<KeyValuePair<string, object?>>>();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == CmsifyOperationalMetrics.MeterName && instrument.Name == "cmsify.webhook.secret.decrypt_failures") meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            var capturedTags = new List<KeyValuePair<string, object?>>();
            foreach (var tag in tags) capturedTags.Add(tag);
            failures.Add(capturedTags);
        });
        listener.Start();

        await using var worker = await CreateContextAsync();
        var repository = new WebhookRepository(worker, CurrentActorInfo.Anonymous, protector, options);

        await Assert.ThrowsAnyAsync<System.Security.Cryptography.CryptographicException>(() => repository.GetActiveEndpointsForEventAsync("workspace.updated", null, TestContext.Current.CancellationToken));

        var tags = Assert.Single(failures);
        Assert.Equal(["version", "key_id", "reason"], tags.Select(tag => tag.Key));
        Assert.Equal(["v2", "unknown", "unknown_key"], tags.Select(tag => tag.Value));
        Assert.DoesNotContain(tags, tag => tag.Key is "endpoint" or "workspace" or "ciphertext" or "url");
        Assert.DoesNotContain(tags, tag => Equals(tag.Value, unconfiguredKeyId));
    }

    [Fact]
    public async Task DeliveryClaim_WhenStoredSecretHasAnUnknownVersion_RecordsOneBoundedDecryptFailureAndRethrows()
    {
        const string ciphertext = "unrecognized.secret.material";
        var configuredKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var now = DateTimeOffset.Parse("2026-08-26T00:01:00Z");
        await using (var setup = await CreateContextAsync())
        {
            var workspace = new Workspace { Name = "Claim telemetry", Slug = "claim-telemetry" };
            var user = new User { Email = "claim-telemetry@example.test", DisplayName = "Claim telemetry", PasswordHash = "hash", Role = UserRole.Admin };
            var endpoint = new WebhookEndpoint { WorkspaceId = workspace.Id, Name = "Claim telemetry endpoint", Url = "https://example.test/hook", Secret = ciphertext, CreatedByUserId = user.Id };
            setup.AddRange(workspace, user, endpoint);
            setup.WebhookDeliveryLogs.Add(new WebhookDeliveryLog { WebhookEndpointId = endpoint.Id, EventType = "workspace.updated", Payload = JsonDocument.Parse("{}").RootElement.Clone(), NextRetryAt = now });
            await setup.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var options = Options.Create(new SecretProtectionOptions
        {
            ActiveKeyId = "key_current",
            EncryptionKeys = new Dictionary<string, string>(StringComparer.Ordinal) { ["key_current"] = configuredKey }
        });
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        var failures = new List<List<KeyValuePair<string, object?>>>();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == CmsifyOperationalMetrics.MeterName && instrument.Name == "cmsify.webhook.secret.decrypt_failures") meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            var capturedTags = new List<KeyValuePair<string, object?>>();
            foreach (var tag in tags) capturedTags.Add(tag);
            failures.Add(capturedTags);
        });
        listener.Start();

        await using var worker = await CreateContextAsync();
        var repository = new WebhookRepository(worker, CurrentActorInfo.Anonymous, new AesSecretProtector(options), options);

        await Assert.ThrowsAnyAsync<System.Security.Cryptography.CryptographicException>(() => repository.ClaimPendingDeliveryLogsAsync("worker", now, TimeSpan.FromMinutes(1), 1, TestContext.Current.CancellationToken));

        var tags = Assert.Single(failures);
        Assert.Equal(["unknown", "unknown", "unknown_version"], tags.Select(tag => tag.Value));
        Assert.DoesNotContain(tags, tag => tag.Key is "endpoint" or "workspace" or "ciphertext" or "url");
    }

    [Fact]
    public async Task DeliveryClaim_PersistsWorkerOwnerAndLeaseTokenForTheClaimedIntent()
    {
        await using var setup = await CreateContextAsync();
        var workspace = new Workspace { Name = "Webhook claims", Slug = "webhook-claims" };
        var user = new User { Email = "webhook-claims@example.test", DisplayName = "Webhook claims", PasswordHash = "hash", Role = UserRole.Admin };
        var endpoint = new WebhookEndpoint { WorkspaceId = workspace.Id, Name = "Active", Url = "https://example.test/hook", Secret = "secret", CreatedByUserId = user.Id };
        endpoint.Subscriptions.Add(new WebhookSubscription { WebhookEndpointId = endpoint.Id, EventType = "workspace.updated" });
        setup.AddRange(workspace, user, endpoint);
        setup.WebhookDeliveryLogs.Add(new WebhookDeliveryLog
        {
            WebhookEndpointId = endpoint.Id,
            EventType = "workspace.updated",
            Payload = JsonDocument.Parse("{}").RootElement.Clone(),
            NextRetryAt = DateTimeOffset.Parse("2026-08-26T00:00:00Z")
        });
        await setup.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var workerContext = await CreateContextAsync();
        var protector = Substitute.For<ISecretProtector>();
        protector.Unprotect(Arg.Any<string>()).Returns(call => call.Arg<string>());
        var repository = new WebhookRepository(workerContext, CurrentActorInfo.Anonymous, protector, SecretProtectionOptions());
        var claims = await repository.ClaimPendingDeliveryLogsAsync("worker-a", DateTimeOffset.Parse("2026-08-26T00:01:00Z"), TimeSpan.FromMinutes(5), 10, TestContext.Current.CancellationToken);

        Assert.Single(claims);
        var owner = await workerContext.Database.SqlQueryRaw<string>("SELECT lease_owner AS \"Value\" FROM webhook_delivery_logs LIMIT 1").SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("worker-a", owner);
    }

    [Fact]
    public async Task DeliveryCompletion_RejectsAnExpiredLeaseOwnerAndAllowsTheCurrentOwnerToSucceed()
    {
        var claimedAt = DateTimeOffset.Parse("2026-08-26T04:00:00Z");
        await using var setup = await CreateContextAsync();
        var workspace = new Workspace { Name = "Stale completion", Slug = "stale-completion" };
        var user = new User { Email = "stale-completion@example.test", DisplayName = "Stale completion", PasswordHash = "hash", Role = UserRole.Admin };
        var endpoint = new WebhookEndpoint { WorkspaceId = workspace.Id, Name = "Active", Url = "https://example.test/hook", Secret = "secret", CreatedByUserId = user.Id };
        endpoint.Subscriptions.Add(new WebhookSubscription { WebhookEndpointId = endpoint.Id, EventType = "workspace.updated" });
        var intent = new WebhookDeliveryLog
        {
            WebhookEndpointId = endpoint.Id,
            EventType = "workspace.updated",
            Payload = JsonDocument.Parse("{}").RootElement.Clone(),
            NextRetryAt = claimedAt
        };
        setup.AddRange(workspace, user, endpoint, intent);
        await setup.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var firstWorkerContext = await CreateContextAsync();
        var firstWorker = CreateRepository(firstWorkerContext);
        var firstClaim = Assert.Single(await firstWorker.ClaimPendingDeliveryLogsAsync("worker-a", claimedAt, TimeSpan.FromMinutes(1), 1, TestContext.Current.CancellationToken));

        await using var secondWorkerContext = await CreateContextAsync();
        var secondWorker = CreateRepository(secondWorkerContext);
        var secondClaim = Assert.Single(await secondWorker.ClaimPendingDeliveryLogsAsync("worker-b", claimedAt.AddMinutes(1), TimeSpan.FromMinutes(1), 1, TestContext.Current.CancellationToken));
        Assert.NotEqual(firstClaim.LeaseToken, secondClaim.LeaseToken);

        var completedAt = claimedAt.AddMinutes(1).AddSeconds(30);
        Assert.False(await firstWorker.CompleteDeliverySucceededAsync(new WebhookDeliveryCompletionDto(firstClaim.Id, firstClaim.LeaseOwner, firstClaim.LeaseToken, completedAt), 204, TestContext.Current.CancellationToken));
        Assert.True(await secondWorker.CompleteDeliverySucceededAsync(new WebhookDeliveryCompletionDto(secondClaim.Id, secondClaim.LeaseOwner, secondClaim.LeaseToken, completedAt), 204, TestContext.Current.CancellationToken));

        await using var verification = await CreateContextAsync();
        var persisted = await verification.WebhookDeliveryLogs.SingleAsync(log => log.Id == intent.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(persisted.IsDelivered);
        Assert.Equal(1, persisted.AttemptCount);
        Assert.Equal(completedAt, persisted.LastAttemptAt);
        Assert.Equal(204, persisted.StatusCode);
        Assert.Null(persisted.LeaseOwner);
        Assert.Null(persisted.LeaseToken);
        Assert.Null(persisted.LeaseExpiresAt);
    }

    [Fact]
    public async Task DeliveryFailure_ClearsTheLeaseBeforeTheDeterministicRetryInstant()
    {
        var claimedAt = DateTimeOffset.Parse("2026-08-26T05:00:00Z");
        var retryAt = claimedAt.AddMinutes(3);
        await using var setup = await CreateContextAsync();
        var workspace = new Workspace { Name = "Retry durability", Slug = "retry-durability" };
        var user = new User { Email = "retry-durability@example.test", DisplayName = "Retry durability", PasswordHash = "hash", Role = UserRole.Admin };
        var endpoint = new WebhookEndpoint { WorkspaceId = workspace.Id, Name = "Active", Url = "https://example.test/hook", Secret = "secret", CreatedByUserId = user.Id };
        endpoint.Subscriptions.Add(new WebhookSubscription { WebhookEndpointId = endpoint.Id, EventType = "workspace.updated" });
        var intent = new WebhookDeliveryLog
        {
            WebhookEndpointId = endpoint.Id,
            EventType = "workspace.updated",
            Payload = JsonDocument.Parse("{}").RootElement.Clone(),
            NextRetryAt = claimedAt
        };
        setup.AddRange(workspace, user, endpoint, intent);
        await setup.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var workerContext = await CreateContextAsync();
        var worker = CreateRepository(workerContext);
        var claim = Assert.Single(await worker.ClaimPendingDeliveryLogsAsync("worker-a", claimedAt, TimeSpan.FromMinutes(1), 1, TestContext.Current.CancellationToken));
        var attemptedAt = claimedAt.AddSeconds(10);
        var error = "upstream unavailable " + new string('x', 4_000);
        Assert.True(await worker.CompleteDeliveryFailedAsync(new WebhookDeliveryCompletionDto(claim.Id, claim.LeaseOwner, claim.LeaseToken, attemptedAt), 503, error, retryAt, false, TestContext.Current.CancellationToken));

        await using var verification = await CreateContextAsync();
        var persisted = await verification.WebhookDeliveryLogs.SingleAsync(log => log.Id == intent.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, persisted.AttemptCount);
        Assert.Equal(attemptedAt, persisted.LastAttemptAt);
        Assert.Equal(503, persisted.StatusCode);
        Assert.Equal(4_000, persisted.LastError!.Length);
        Assert.StartsWith("upstream unavailable", persisted.LastError);
        Assert.Equal(retryAt, persisted.NextRetryAt);
        Assert.False(persisted.IsFailed);
        Assert.False(persisted.IsDeadLetter);
        Assert.Null(persisted.LeaseOwner);
        Assert.Null(persisted.LeaseToken);
        Assert.Null(persisted.LeaseExpiresAt);

        await using var beforeRetry = await CreateContextAsync();
        Assert.Empty(await CreateRepository(beforeRetry).ClaimPendingDeliveryLogsAsync("worker-b", retryAt.AddTicks(-1), TimeSpan.FromMinutes(1), 1, TestContext.Current.CancellationToken));
        await using var retryWorker = await CreateContextAsync();
        Assert.Single(await CreateRepository(retryWorker).ClaimPendingDeliveryLogsAsync("worker-b", retryAt, TimeSpan.FromMinutes(1), 1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeliveryFailure_RejectsTheStaleLeaseTokenAndAcceptsTheCurrentToken()
    {
        var claimedAt = DateTimeOffset.Parse("2026-08-26T05:30:00Z");
        await using var setup = await CreateContextAsync();
        var workspace = new Workspace { Name = "Stale failure", Slug = "stale-failure" };
        var user = new User { Email = "stale-failure@example.test", DisplayName = "Stale failure", PasswordHash = "hash", Role = UserRole.Admin };
        var endpoint = new WebhookEndpoint { WorkspaceId = workspace.Id, Name = "Active", Url = "https://example.test/hook", Secret = "secret", CreatedByUserId = user.Id };
        endpoint.Subscriptions.Add(new WebhookSubscription { WebhookEndpointId = endpoint.Id, EventType = "workspace.updated" });
        var intent = new WebhookDeliveryLog { WebhookEndpointId = endpoint.Id, EventType = "workspace.updated", Payload = JsonDocument.Parse("{}").RootElement.Clone(), NextRetryAt = claimedAt };
        setup.AddRange(workspace, user, endpoint, intent);
        await setup.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var firstContext = await CreateContextAsync();
        var first = CreateRepository(firstContext);
        var firstClaim = Assert.Single(await first.ClaimPendingDeliveryLogsAsync("worker-a", claimedAt, TimeSpan.FromMinutes(1), 1, TestContext.Current.CancellationToken));
        await using var secondContext = await CreateContextAsync();
        var second = CreateRepository(secondContext);
        var secondClaim = Assert.Single(await second.ClaimPendingDeliveryLogsAsync("worker-b", claimedAt.AddMinutes(1), TimeSpan.FromMinutes(1), 1, TestContext.Current.CancellationToken));
        var attemptedAt = claimedAt.AddMinutes(1).AddSeconds(30);

        Assert.False(await first.CompleteDeliveryFailedAsync(new WebhookDeliveryCompletionDto(firstClaim.Id, firstClaim.LeaseOwner, firstClaim.LeaseToken, attemptedAt), 503, "stale", attemptedAt.AddMinutes(1), false, TestContext.Current.CancellationToken));
        Assert.True(await second.CompleteDeliveryFailedAsync(new WebhookDeliveryCompletionDto(secondClaim.Id, secondClaim.LeaseOwner, secondClaim.LeaseToken, attemptedAt), 503, "current", attemptedAt.AddMinutes(1), false, TestContext.Current.CancellationToken));

        await using var verification = await CreateContextAsync();
        var persisted = await verification.WebhookDeliveryLogs.SingleAsync(log => log.Id == intent.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, persisted.AttemptCount);
        Assert.Equal("current", persisted.LastError);
        Assert.Equal(attemptedAt.AddMinutes(1), persisted.NextRetryAt);
    }

    [Fact]
    public async Task DeliveryProcessor_DeadLettersAtTheConfiguredMaximumAndPreventsReclaim()
    {
        var attemptedAt = DateTimeOffset.Parse("2026-08-26T05:45:00Z");
        await using var setup = await CreateContextAsync();
        var workspace = new Workspace { Name = "Dead letter", Slug = "dead-letter" };
        var user = new User { Email = "dead-letter@example.test", DisplayName = "Dead letter", PasswordHash = "hash", Role = UserRole.Admin };
        var endpoint = new WebhookEndpoint { WorkspaceId = workspace.Id, Name = "Active", Url = "https://example.test/hook", Secret = "secret", CreatedByUserId = user.Id };
        endpoint.Subscriptions.Add(new WebhookSubscription { WebhookEndpointId = endpoint.Id, EventType = "workspace.updated" });
        var intent = new WebhookDeliveryLog { WebhookEventId = Guid.CreateVersion7(), WebhookEndpointId = endpoint.Id, EventType = "workspace.updated", Payload = JsonDocument.Parse("{}").RootElement.Clone(), AttemptCount = 1, NextRetryAt = attemptedAt };
        setup.AddRange(workspace, user, endpoint, intent);
        await setup.SaveChangesAsync(TestContext.Current.CancellationToken);

        var handler = new CapturingHandler(HttpStatusCode.InternalServerError);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(nameof(WebhookDeliveryProcessor)).Returns(new HttpClient(handler));
        var validator = Substitute.For<IWebhookDestinationValidator>();
        validator.ValidateAsync(endpoint.Url, Arg.Any<CancellationToken>()).Returns(WebhookDestinationValidationResult.Valid(new Uri(endpoint.Url), [IPAddress.Parse("8.8.8.8")]));
        await using var workerContext = await CreateContextAsync();
        var worker = CreateRepository(workerContext);
        var claim = Assert.Single(await worker.ClaimPendingDeliveryLogsAsync("worker", attemptedAt, TimeSpan.FromMinutes(1), 1, TestContext.Current.CancellationToken));

        await new WebhookDeliveryProcessor(factory, worker, validator, new MutableTimeProvider(attemptedAt)).DeliverRetryAsync(claim, 2, CancellationToken.None);

        await using var verification = await CreateContextAsync();
        var persisted = await verification.WebhookDeliveryLogs.SingleAsync(log => log.Id == intent.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, persisted.AttemptCount);
        Assert.Equal(attemptedAt, persisted.LastAttemptAt);
        Assert.Equal(500, persisted.StatusCode);
        Assert.StartsWith("HTTP 500", persisted.LastError);
        Assert.True(persisted.IsFailed);
        Assert.True(persisted.IsDeadLetter);
        Assert.Equal(attemptedAt, persisted.DeadLetteredAt);
        Assert.Null(persisted.NextRetryAt);
        Assert.Null(persisted.LeaseOwner);
        Assert.Null(persisted.LeaseToken);
        Assert.Null(persisted.LeaseExpiresAt);
        Assert.Empty(await CreateRepository(verification).ClaimPendingDeliveryLogsAsync("another-worker", attemptedAt.AddDays(1), TimeSpan.FromMinutes(1), 1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeliveryProcessor_RecordsAValidatorExceptionAndReleasesThePersistedLease()
    {
        var attemptedAt = DateTimeOffset.Parse("2026-08-26T05:50:00Z");
        await using var setup = await CreateContextAsync();
        var workspace = new Workspace { Name = "Validator exception", Slug = "validator-exception" };
        var user = new User { Email = "validator-exception@example.test", DisplayName = "Validator exception", PasswordHash = "hash", Role = UserRole.Admin };
        var endpoint = new WebhookEndpoint { WorkspaceId = workspace.Id, Name = "Active", Url = "https://example.test/hook", Secret = "secret", CreatedByUserId = user.Id };
        endpoint.Subscriptions.Add(new WebhookSubscription { WebhookEndpointId = endpoint.Id, EventType = "workspace.updated" });
        var intent = new WebhookDeliveryLog { WebhookEventId = Guid.CreateVersion7(), WebhookEndpointId = endpoint.Id, EventType = "workspace.updated", Payload = JsonDocument.Parse("{}").RootElement.Clone(), NextRetryAt = attemptedAt };
        setup.AddRange(workspace, user, endpoint, intent);
        await setup.SaveChangesAsync(TestContext.Current.CancellationToken);

        var factory = Substitute.For<IHttpClientFactory>();
        var validator = Substitute.For<IWebhookDestinationValidator>();
        validator.ValidateAsync(endpoint.Url, Arg.Any<CancellationToken>()).Returns(Task.FromException<WebhookDestinationValidationResult>(new InvalidOperationException("resolver unavailable")));
        await using var workerContext = await CreateContextAsync();
        var worker = CreateRepository(workerContext);
        var claim = Assert.Single(await worker.ClaimPendingDeliveryLogsAsync("worker", attemptedAt, TimeSpan.FromMinutes(1), 1, TestContext.Current.CancellationToken));

        await new WebhookDeliveryProcessor(factory, worker, validator, new MutableTimeProvider(attemptedAt)).DeliverRetryAsync(claim, 2, CancellationToken.None);

        await using var verification = await CreateContextAsync();
        var persisted = await verification.WebhookDeliveryLogs.SingleAsync(log => log.Id == intent.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(persisted.LeaseOwner);
        Assert.Null(persisted.LeaseToken);
        Assert.Null(persisted.LeaseExpiresAt);
        Assert.Equal("resolver unavailable", persisted.LastError);
        Assert.Equal(attemptedAt.AddSeconds(30), persisted.NextRetryAt);
    }

    [Fact]
    public async Task DeliveryFailure_RejectsContradictoryTerminalRetryInstants()
    {
        var attemptedAt = DateTimeOffset.Parse("2026-08-26T05:55:00Z");
        await using var context = await CreateContextAsync();
        var repository = CreateRepository(context);
        var completion = new WebhookDeliveryCompletionDto(Guid.CreateVersion7(), "worker", Guid.CreateVersion7(), attemptedAt);

        await Assert.ThrowsAsync<ArgumentException>(() => repository.CompleteDeliveryFailedAsync(completion, 503, "failure", attemptedAt, true, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.CompleteDeliveryFailedAsync(completion, 503, "failure", null, false, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.CompleteDeliveryFailedAsync(completion, 503, "failure", attemptedAt.AddTicks(-1), false, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeliveryProcessor_RetriesAPersistedIntentWithItsStableEventIdHeader()
    {
        var firstAttemptAt = DateTimeOffset.Parse("2026-08-26T06:00:00Z");
        var eventId = Guid.Parse("0198e49b-c06a-78b3-af9d-847b7bae9f91");
        await using var setup = await CreateContextAsync();
        var workspace = new Workspace { Name = "HTTP delivery", Slug = "http-delivery" };
        var user = new User { Email = "http-delivery@example.test", DisplayName = "HTTP delivery", PasswordHash = "hash", Role = UserRole.Admin };
        var endpoint = new WebhookEndpoint { WorkspaceId = workspace.Id, Name = "Active", Url = "https://example.test/hook", Secret = "secret", CreatedByUserId = user.Id };
        endpoint.Subscriptions.Add(new WebhookSubscription { WebhookEndpointId = endpoint.Id, EventType = "workspace.updated" });
        var intent = new WebhookDeliveryLog
        {
            WebhookEventId = eventId,
            WebhookEndpointId = endpoint.Id,
            EventType = "workspace.updated",
            Payload = JsonDocument.Parse("{\"name\":\"durable\"}").RootElement.Clone(),
            NextRetryAt = firstAttemptAt
        };
        setup.AddRange(workspace, user, endpoint, intent);
        await setup.SaveChangesAsync(TestContext.Current.CancellationToken);

        var handler = new CapturingHandler(HttpStatusCode.ServiceUnavailable, HttpStatusCode.NoContent);
        var clientFactory = Substitute.For<IHttpClientFactory>();
        clientFactory.CreateClient(nameof(WebhookDeliveryProcessor)).Returns(new HttpClient(handler));
        var validator = Substitute.For<IWebhookDestinationValidator>();
        validator.ValidateAsync(endpoint.Url, Arg.Any<CancellationToken>()).Returns(WebhookDestinationValidationResult.Valid(new Uri(endpoint.Url), [IPAddress.Parse("8.8.8.8")]));
        var timeProvider = new MutableTimeProvider(firstAttemptAt);
        await using var firstWorkerContext = await CreateContextAsync();
        var firstWorker = CreateRepository(firstWorkerContext);
        var firstClaim = Assert.Single(await firstWorker.ClaimPendingDeliveryLogsAsync("worker-a", firstAttemptAt, TimeSpan.FromMinutes(1), 1, TestContext.Current.CancellationToken));
        var processor = new WebhookDeliveryProcessor(clientFactory, firstWorker, validator, timeProvider);

        await processor.DeliverRetryAsync(firstClaim, 2, CancellationToken.None);

        var retryAt = firstAttemptAt.AddSeconds(30);
        await using var retryWorkerContext = await CreateContextAsync();
        var retryWorker = CreateRepository(retryWorkerContext);
        var secondClaim = Assert.Single(await retryWorker.ClaimPendingDeliveryLogsAsync("worker-b", retryAt, TimeSpan.FromMinutes(1), 1, TestContext.Current.CancellationToken));
        timeProvider.UtcNow = retryAt;
        await new WebhookDeliveryProcessor(clientFactory, retryWorker, validator, timeProvider).DeliverRetryAsync(secondClaim, 2, CancellationToken.None);

        Assert.Equal([eventId.ToString("D"), eventId.ToString("D")], handler.EventIds);
        _ = validator.Received(2).ValidateAsync(endpoint.Url, Arg.Any<CancellationToken>());
        Assert.Equal(2, handler.Payloads.Count);
        Assert.Equal(handler.Payloads[0], handler.Payloads[1]);
        Assert.Equal(WebhookSigner.Sign("secret", handler.Payloads[0]), handler.Signatures[0]);
        Assert.Equal(WebhookSigner.Sign("secret", handler.Payloads[1]), handler.Signatures[1]);
        await using var verification = await CreateContextAsync();
        var persisted = await verification.WebhookDeliveryLogs.SingleAsync(log => log.Id == intent.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, persisted.AttemptCount);
        Assert.True(persisted.IsDelivered);
        Assert.Equal(204, persisted.StatusCode);
        Assert.Null(persisted.LeaseOwner);
        Assert.Null(persisted.LeaseToken);
        Assert.Null(persisted.LeaseExpiresAt);
    }

    [Fact]
    public async Task DeliveryProcessor_PinsTheSingleValidatedResolutionForTheAttempt()
    {
        var attemptedAt = DateTimeOffset.Parse("2026-08-26T06:30:00Z");
        await using var setup = await CreateContextAsync();
        var workspace = new Workspace { Name = "Pinned resolution", Slug = "pinned-resolution" };
        var user = new User { Email = "pinned-resolution@example.test", DisplayName = "Pinned resolution", PasswordHash = "hash", Role = UserRole.Admin };
        var endpoint = new WebhookEndpoint { WorkspaceId = workspace.Id, Name = "Active", Url = "https://hooks.example.test/rebinding", Secret = "secret", CreatedByUserId = user.Id };
        var intent = new WebhookDeliveryLog { WebhookEndpointId = endpoint.Id, EventType = "workspace.updated", Payload = JsonDocument.Parse("{}").RootElement.Clone(), NextRetryAt = attemptedAt };
        setup.AddRange(workspace, user, endpoint, intent);
        await setup.SaveChangesAsync(TestContext.Current.CancellationToken);

        var resolver = Substitute.For<IWebhookDnsResolver>();
        resolver.ResolveAsync("hooks.example.test", Arg.Any<CancellationToken>()).Returns(
            [IPAddress.Parse("8.8.8.8")],
            [IPAddress.Parse("10.0.0.1")]);
        var connector = new CapturingConnector();
        using var pinnedHandler = PinnedWebhookTransport.CreateHandler(connector, TimeSpan.FromSeconds(1));
        using var client = new HttpClient(pinnedHandler);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(nameof(WebhookDeliveryProcessor)).Returns(client);
        await using var worker = await CreateContextAsync();
        var claim = Assert.Single(await CreateRepository(worker).ClaimPendingDeliveryLogsAsync("worker", attemptedAt, TimeSpan.FromMinutes(1), 1, TestContext.Current.CancellationToken));

        await new WebhookDeliveryProcessor(factory, CreateRepository(worker), new WebhookDestinationValidator(resolver, new ConfigurationBuilder().Build()), new MutableTimeProvider(attemptedAt))
            .DeliverRetryAsync(claim, 2, CancellationToken.None);

        await resolver.Received(1).ResolveAsync("hooks.example.test", Arg.Any<CancellationToken>());
        Assert.Equal([IPAddress.Parse("8.8.8.8")], Assert.Single(connector.AddressSets));
    }

    [Fact]
    public async Task DeliveryProcessor_RevalidatesAndPinsEachDurableRetryAttempt()
    {
        var firstAttemptAt = DateTimeOffset.Parse("2026-08-26T06:45:00Z");
        var secondAttemptAt = firstAttemptAt.AddSeconds(30);
        await using var setup = await CreateContextAsync();
        var workspace = new Workspace { Name = "Retry pins", Slug = "retry-pins" };
        var user = new User { Email = "retry-pins@example.test", DisplayName = "Retry pins", PasswordHash = "hash", Role = UserRole.Admin };
        var endpoint = new WebhookEndpoint { WorkspaceId = workspace.Id, Name = "Active", Url = "https://hooks.example.test/retry", Secret = "secret", CreatedByUserId = user.Id };
        var intent = new WebhookDeliveryLog { WebhookEndpointId = endpoint.Id, EventType = "workspace.updated", Payload = JsonDocument.Parse("{}").RootElement.Clone(), NextRetryAt = firstAttemptAt };
        setup.AddRange(workspace, user, endpoint, intent);
        await setup.SaveChangesAsync(TestContext.Current.CancellationToken);

        var firstDestination = WebhookDestinationValidationResult.Valid(new Uri(endpoint.Url), [IPAddress.Parse("8.8.8.8")]);
        var secondDestination = WebhookDestinationValidationResult.Valid(new Uri(endpoint.Url), [IPAddress.Parse("1.1.1.1")]);
        var validator = Substitute.For<IWebhookDestinationValidator>();
        validator.ValidateAsync(endpoint.Url, Arg.Any<CancellationToken>()).Returns(firstDestination, secondDestination);
        var handler = new CapturingHandler(HttpStatusCode.ServiceUnavailable, HttpStatusCode.NoContent);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(nameof(WebhookDeliveryProcessor)).Returns(new HttpClient(handler));
        var clock = new MutableTimeProvider(firstAttemptAt);

        await using var firstWorker = await CreateContextAsync();
        var firstClaim = Assert.Single(await CreateRepository(firstWorker).ClaimPendingDeliveryLogsAsync("worker-a", firstAttemptAt, TimeSpan.FromMinutes(1), 1, TestContext.Current.CancellationToken));
        await new WebhookDeliveryProcessor(factory, CreateRepository(firstWorker), validator, clock).DeliverRetryAsync(firstClaim, 2, CancellationToken.None);

        await using var retryWorker = await CreateContextAsync();
        var secondClaim = Assert.Single(await CreateRepository(retryWorker).ClaimPendingDeliveryLogsAsync("worker-b", secondAttemptAt, TimeSpan.FromMinutes(1), 1, TestContext.Current.CancellationToken));
        clock.UtcNow = secondAttemptAt;
        await new WebhookDeliveryProcessor(factory, CreateRepository(retryWorker), validator, clock).DeliverRetryAsync(secondClaim, 2, CancellationToken.None);

        _ = validator.Received(2).ValidateAsync(endpoint.Url, Arg.Any<CancellationToken>());
        Assert.Same(firstDestination, handler.Destinations[0]);
        Assert.Same(secondDestination, handler.Destinations[1]);
    }

    [Fact]
    public async Task DeliveryProcessor_DoesNotCreateAClientWhenDestinationValidationFails()
    {
        var attemptedAt = DateTimeOffset.Parse("2026-08-26T06:50:00Z");
        await using var setup = await CreateContextAsync();
        var workspace = new Workspace { Name = "Rejected destination", Slug = "rejected-destination" };
        var user = new User { Email = "rejected-destination@example.test", DisplayName = "Rejected destination", PasswordHash = "hash", Role = UserRole.Admin };
        var endpoint = new WebhookEndpoint { WorkspaceId = workspace.Id, Name = "Active", Url = "https://hooks.example.test/rejected", Secret = "secret", CreatedByUserId = user.Id };
        var intent = new WebhookDeliveryLog { WebhookEndpointId = endpoint.Id, EventType = "workspace.updated", Payload = JsonDocument.Parse("{}").RootElement.Clone(), NextRetryAt = attemptedAt };
        setup.AddRange(workspace, user, endpoint, intent);
        await setup.SaveChangesAsync(TestContext.Current.CancellationToken);

        var validator = Substitute.For<IWebhookDestinationValidator>();
        validator.ValidateAsync(endpoint.Url, Arg.Any<CancellationToken>()).Returns(WebhookDestinationValidationResult.Invalid("blocked"));
        await using var worker = await CreateContextAsync();
        var claim = Assert.Single(await CreateRepository(worker).ClaimPendingDeliveryLogsAsync("worker", attemptedAt, TimeSpan.FromMinutes(1), 1, TestContext.Current.CancellationToken));

        await new WebhookDeliveryProcessor(new ThrowingHttpClientFactory(), CreateRepository(worker), validator, new MutableTimeProvider(attemptedAt))
            .DeliverRetryAsync(claim, 2, CancellationToken.None);
    }

    [Fact]
    public async Task DeliveryProcessor_FailedHttpUsesCompletionTimeForDurableRetrySchedule()
    {
        var startedAt = DateTimeOffset.Parse("2026-08-26T07:30:00Z");
        var completedAt = startedAt.AddMinutes(2);
        await using var setup = await CreateContextAsync();
        var workspace = new Workspace { Name = "Completion retry", Slug = "completion-retry" };
        var user = new User { Email = "completion-retry@example.test", DisplayName = "Completion retry", PasswordHash = "hash", Role = UserRole.Admin };
        var endpoint = new WebhookEndpoint { WorkspaceId = workspace.Id, Name = "Completion retry", Url = "https://example.test/completion-retry", Secret = "secret", CreatedByUserId = user.Id };
        var intent = new WebhookDeliveryLog { WebhookEndpointId = endpoint.Id, EventType = "workspace.updated", Payload = JsonDocument.Parse("{}").RootElement.Clone(), NextRetryAt = startedAt };
        setup.AddRange(workspace, user, endpoint, intent);
        await setup.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var worker = await CreateContextAsync();
        var repository = CreateRepository(worker);
        var claim = Assert.Single(await repository.ClaimPendingDeliveryLogsAsync("worker", startedAt, TimeSpan.FromMinutes(5), 1, TestContext.Current.CancellationToken));
        var clock = new MutableTimeProvider(startedAt);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(nameof(WebhookDeliveryProcessor)).Returns(new HttpClient(new AdvancingHandler(clock, completedAt, HttpStatusCode.ServiceUnavailable)));
        var validator = Substitute.For<IWebhookDestinationValidator>();
        validator.ValidateAsync(endpoint.Url, Arg.Any<CancellationToken>()).Returns(WebhookDestinationValidationResult.Valid(new Uri(endpoint.Url), [IPAddress.Parse("8.8.8.8")]));

        await new WebhookDeliveryProcessor(factory, repository, validator, clock).DeliverRetryAsync(claim, 3, CancellationToken.None);

        await using var verification = await CreateContextAsync();
        var persisted = await verification.WebhookDeliveryLogs.SingleAsync(log => log.Id == intent.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, persisted.AttemptCount);
        Assert.Equal(completedAt, persisted.LastAttemptAt);
        Assert.Equal(completedAt.AddSeconds(30), persisted.NextRetryAt);
        Assert.Equal(503, persisted.StatusCode);
        Assert.Contains("503", persisted.LastError);
        Assert.Null(persisted.LeaseOwner);
        Assert.Null(persisted.LeaseToken);
        Assert.Null(persisted.LeaseExpiresAt);
    }

    [Fact]
    public async Task LegacyDeliveryMigration_BackfillsStableEventIdThatReachesTheRetryPostHeader()
    {
        var now = DateTimeOffset.Parse("2026-08-26T07:00:00Z");
        await using var context = new CmsifyDbContext(new DbContextOptionsBuilder<CmsifyDbContext>()
            .UseNpgsql(postgres.GetConnectionString())
            .UseSyntaxCircusSnakeCaseNamingConvention()
            .Options);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20260821005219_AddPackageProvenanceToReusableModels", TestContext.Current.CancellationToken);

        var workspaceId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        var endpointId = Guid.CreateVersion7();
        var deliveryId = Guid.CreateVersion7();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO workspaces (id, name, slug, created_at, updated_at, is_deleted)
            VALUES ({workspaceId}, 'Legacy webhook workspace', 'legacy-webhook-workspace', {now}, {now}, false);
            INSERT INTO users (id, email, display_name, password_hash, role, must_change_password, is_active, created_at, updated_at, is_deleted)
            VALUES ({userId}, 'legacy-webhook@example.test', 'Legacy webhook', 'hash', 'Admin', false, true, {now}, {now}, false);
            INSERT INTO webhook_endpoints (id, workspace_id, name, url, secret, is_active, created_by_user_id, created_at, updated_at, is_deleted)
            VALUES ({endpointId}, {workspaceId}, 'Legacy endpoint', 'https://example.test/hook', 'secret', true, {userId}, {now}, {now}, false);
            INSERT INTO webhook_delivery_logs (id, webhook_endpoint_id, event_type, payload, attempt_count, is_delivered, is_failed, created_at)
            VALUES ({deliveryId}, {endpointId}, 'workspace.updated', jsonb_build_object(), 1, false, true, {now});
            """, cancellationToken: TestContext.Current.CancellationToken);

        await migrator.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);
        context.ChangeTracker.Clear();
        var migrated = await context.WebhookDeliveryLogs.SingleAsync(log => log.Id == deliveryId, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEqual(Guid.Empty, migrated.WebhookEventId);

        // This is the state created by the operator's manual retry: preserve
        // historical diagnostics but make the migrated terminal intent due.
        migrated.IsFailed = false;
        migrated.IsDeadLetter = false;
        migrated.NextRetryAt = now;
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var handler = new CapturingHandler(HttpStatusCode.NoContent);
        var clientFactory = Substitute.For<IHttpClientFactory>();
        clientFactory.CreateClient(nameof(WebhookDeliveryProcessor)).Returns(new HttpClient(handler));
        var validator = Substitute.For<IWebhookDestinationValidator>();
        validator.ValidateAsync("https://example.test/hook", Arg.Any<CancellationToken>()).Returns(WebhookDestinationValidationResult.Valid(new Uri("https://example.test/hook"), [IPAddress.Parse("8.8.8.8")]));
        var claim = Assert.Single(await CreateRepository(context).ClaimPendingDeliveryLogsAsync("legacy-worker", now, TimeSpan.FromMinutes(1), 1, TestContext.Current.CancellationToken));

        await new WebhookDeliveryProcessor(clientFactory, CreateRepository(context), validator, new MutableTimeProvider(now))
            .DeliverRetryAsync(claim, 2, CancellationToken.None);

        Assert.Equal(migrated.WebhookEventId.ToString("D"), Assert.Single(handler.EventIds));
    }

    [Fact]
    public async Task OutboxClaims_AreDisjointUntilExpiryThenReclaimWithANewToken()
    {
        var now = DateTimeOffset.Parse("2026-08-26T01:00:00Z");
        await using var setup = await CreateContextAsync();
        setup.WebhookOutboxEvents.AddRange(CreateOutboxEvent(), CreateOutboxEvent());
        await setup.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var firstContext = await CreateContextAsync();
        await using var secondContext = await CreateContextAsync();
        var first = CreateRepository(firstContext);
        var second = CreateRepository(secondContext);
        var releases = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstTask = Task.Run(async () => { await releases.Task; return await first.ClaimOutboxEventsAsync("worker-one", now, TimeSpan.FromMinutes(5), 2); });
        var secondTask = Task.Run(async () => { await releases.Task; return await second.ClaimOutboxEventsAsync("worker-two", now, TimeSpan.FromMinutes(5), 2); });
        releases.SetResult();
        var claims = await Task.WhenAll(firstTask, secondTask);

        Assert.Equal(2, claims.SelectMany(batch => batch).Select(claim => claim.Id).Distinct().Count());
        var firstClaim = claims.SelectMany(batch => batch).First();
        Assert.Empty(await CreateRepository(secondContext).ClaimOutboxEventsAsync("worker-two", now.AddMinutes(1), TimeSpan.FromMinutes(5), 2, TestContext.Current.CancellationToken));
        var recovered = await CreateRepository(secondContext).ClaimOutboxEventsAsync("worker-two", now.AddMinutes(6), TimeSpan.FromMinutes(5), 2, TestContext.Current.CancellationToken);
        Assert.Contains(recovered, claim => claim.Id == firstClaim.Id && claim.LeaseToken != firstClaim.LeaseToken && claim.LeaseOwner == "worker-two");
    }

    [Fact]
    public async Task Materialization_CreatesOneIntentForActiveMatchingEndpointAndReplayDoesNotDuplicate()
    {
        var now = DateTimeOffset.Parse("2026-08-26T02:00:00Z");
        await using var setup = await CreateContextAsync();
        var workspace = new Workspace { Name = "Outbox materialization", Slug = "outbox-materialization" };
        var user = new User { Email = "outbox-materialization@example.test", DisplayName = "Outbox", PasswordHash = "hash", Role = UserRole.Admin };
        var active = new WebhookEndpoint { WorkspaceId = workspace.Id, Name = "active", Url = "https://example.test/active", Secret = "secret", CreatedByUserId = user.Id };
        active.Subscriptions.Add(new WebhookSubscription { WebhookEndpointId = active.Id, EventType = "workspace.updated" });
        var inactive = new WebhookEndpoint { WorkspaceId = workspace.Id, Name = "inactive", Url = "https://example.test/inactive", Secret = "secret", CreatedByUserId = user.Id, IsActive = false };
        inactive.Subscriptions.Add(new WebhookSubscription { WebhookEndpointId = inactive.Id, EventType = "workspace.updated" });
        var other = new WebhookEndpoint { WorkspaceId = workspace.Id, Name = "other", Url = "https://example.test/other", Secret = "secret", CreatedByUserId = user.Id };
        other.Subscriptions.Add(new WebhookSubscription { WebhookEndpointId = other.Id, EventType = "content.published" });
        var evt = CreateOutboxEvent(workspace.Id);
        setup.AddRange(workspace, user, active, inactive, other, evt);
        await setup.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var workerContext = await CreateContextAsync();
        var repository = CreateRepository(workerContext);
        var claim = Assert.Single(await repository.ClaimOutboxEventsAsync("materializer", now, TimeSpan.FromMinutes(5), 10, TestContext.Current.CancellationToken));
        Assert.True(await repository.MaterializeOutboxEventAsync(claim, now, TestContext.Current.CancellationToken));
        Assert.False(await repository.MaterializeOutboxEventAsync(claim, now, TestContext.Current.CancellationToken));

        await using var verification = await CreateContextAsync();
        var intents = await verification.WebhookDeliveryLogs.Where(log => log.WebhookEventId == evt.Id).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(intents);
        Assert.Equal(active.Id, intents[0].WebhookEndpointId);
        Assert.Equal(evt.Id, intents[0].WebhookEventId);
        Assert.NotNull(await verification.WebhookOutboxEvents.Where(candidate => candidate.Id == evt.Id).Select(candidate => candidate.ProcessedAt).SingleAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Materialization_AfterLeaseRecoveryReusesTheExistingEventEndpointIntent()
    {
        var now = DateTimeOffset.Parse("2026-08-26T03:00:00Z");
        await using var setup = await CreateContextAsync();
        var workspace = new Workspace { Name = "Outbox recovery", Slug = "outbox-recovery" };
        var user = new User { Email = "outbox-recovery@example.test", DisplayName = "Recovery", PasswordHash = "hash", Role = UserRole.Admin };
        var endpoint = new WebhookEndpoint { WorkspaceId = workspace.Id, Name = "recovery", Url = "https://example.test/recovery", Secret = "secret", CreatedByUserId = user.Id };
        endpoint.Subscriptions.Add(new WebhookSubscription { WebhookEndpointId = endpoint.Id, EventType = "workspace.updated" });
        var evt = CreateOutboxEvent(workspace.Id);
        evt.LeaseOwner = "crashed-worker";
        evt.LeaseToken = Guid.CreateVersion7();
        evt.LeaseExpiresAt = now.AddMinutes(-1);
        setup.AddRange(workspace, user, endpoint, evt);
        setup.WebhookDeliveryLogs.Add(new WebhookDeliveryLog { WebhookEventId = evt.Id, WebhookEndpointId = endpoint.Id, EventType = evt.EventType, Payload = evt.Payload, NextRetryAt = evt.OccurredAt });
        await setup.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var workerContext = await CreateContextAsync();
        var repository = CreateRepository(workerContext);
        var claim = Assert.Single(await repository.ClaimOutboxEventsAsync("recovery-worker", now, TimeSpan.FromMinutes(5), 10, TestContext.Current.CancellationToken));
        Assert.True(await repository.MaterializeOutboxEventAsync(claim, now, TestContext.Current.CancellationToken));

        await using var verification = await CreateContextAsync();
        Assert.Equal(1, await verification.WebhookDeliveryLogs.CountAsync(log => log.WebhookEventId == evt.Id && log.WebhookEndpointId == endpoint.Id, cancellationToken: TestContext.Current.CancellationToken));
        Assert.NotNull(await verification.WebhookOutboxEvents.Where(candidate => candidate.Id == evt.Id).Select(candidate => candidate.ProcessedAt).SingleAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WebhookDispatchService_MaterializesThePersistedOutboxEvent()
    {
        var now = DateTimeOffset.Parse("2026-08-26T08:00:00Z");
        await using var setup = await CreateContextAsync();
        var workspace = new Workspace { Name = "Dispatcher", Slug = "dispatcher" };
        var user = new User { Email = "dispatcher@example.test", DisplayName = "Dispatcher", PasswordHash = "hash", Role = UserRole.Admin };
        var endpoint = new WebhookEndpoint { WorkspaceId = workspace.Id, Name = "Active", Url = "https://example.test/dispatcher", Secret = "secret", CreatedByUserId = user.Id };
        endpoint.Subscriptions.Add(new WebhookSubscription { WebhookEndpointId = endpoint.Id, EventType = "workspace.updated" });
        var evt = CreateOutboxEvent(workspace.Id);
        setup.AddRange(workspace, user, endpoint, evt);
        await setup.SaveChangesAsync(TestContext.Current.CancellationToken);

        var (services, service) = CreateDispatchService(now, "dispatcher-worker");
        using (services)
        {
            await service.RunOnceAsync(TestContext.Current.CancellationToken);
        }

        await using var verification = await CreateContextAsync();
        Assert.NotNull(await verification.WebhookOutboxEvents.Where(candidate => candidate.Id == evt.Id).Select(candidate => candidate.ProcessedAt).SingleAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, await verification.WebhookDeliveryLogs.CountAsync(log => log.WebhookEventId == evt.Id && log.WebhookEndpointId == endpoint.Id, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WebhookDispatchServices_DoNotDuplicateTheEventEndpointIntent()
    {
        var now = DateTimeOffset.Parse("2026-08-26T08:20:00Z");
        await using var setup = await CreateContextAsync();
        var workspace = new Workspace { Name = "Two dispatchers", Slug = "two-dispatchers" };
        var user = new User { Email = "two-dispatchers@example.test", DisplayName = "Two dispatchers", PasswordHash = "hash", Role = UserRole.Admin };
        var endpoint = new WebhookEndpoint { WorkspaceId = workspace.Id, Name = "Active", Url = "https://example.test/two", Secret = "secret", CreatedByUserId = user.Id };
        endpoint.Subscriptions.Add(new WebhookSubscription { WebhookEndpointId = endpoint.Id, EventType = "workspace.updated" });
        var evt = CreateOutboxEvent(workspace.Id);
        setup.AddRange(workspace, user, endpoint, evt);
        await setup.SaveChangesAsync(TestContext.Current.CancellationToken);

        var (firstServices, first) = CreateDispatchService(now, "dispatcher-one");
        using (firstServices)
        {
            await first.RunOnceAsync(TestContext.Current.CancellationToken);
        }
        var (secondServices, second) = CreateDispatchService(now, "dispatcher-two");
        using (secondServices)
        {
            await second.RunOnceAsync(TestContext.Current.CancellationToken);
        }

        await using var verification = await CreateContextAsync();
        Assert.Equal(1, await verification.WebhookDeliveryLogs.CountAsync(log => log.WebhookEventId == evt.Id && log.WebhookEndpointId == endpoint.Id, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WebhookDispatchService_ReclaimsAnExpiredOutboxLeaseButNotAnActiveLease()
    {
        var now = DateTimeOffset.Parse("2026-08-26T08:40:00Z");
        await using var setup = await CreateContextAsync();
        var workspace = new Workspace { Name = "Expired dispatcher", Slug = "expired-dispatcher" };
        var user = new User { Email = "expired-dispatcher@example.test", DisplayName = "Expired dispatcher", PasswordHash = "hash", Role = UserRole.Admin };
        var endpoint = new WebhookEndpoint { WorkspaceId = workspace.Id, Name = "Active", Url = "https://example.test/expired", Secret = "secret", CreatedByUserId = user.Id };
        endpoint.Subscriptions.Add(new WebhookSubscription { WebhookEndpointId = endpoint.Id, EventType = "workspace.updated" });
        var evt = CreateOutboxEvent(workspace.Id);
        evt.LeaseOwner = "dead-worker";
        evt.LeaseToken = Guid.CreateVersion7();
        evt.LeaseExpiresAt = now.AddMinutes(1);
        setup.AddRange(workspace, user, endpoint, evt);
        await setup.SaveChangesAsync(TestContext.Current.CancellationToken);

        var (earlyServices, early) = CreateDispatchService(now, "recovery-worker");
        using (earlyServices)
        {
            await early.RunOnceAsync(TestContext.Current.CancellationToken);
        }
        await using (var beforeExpiry = await CreateContextAsync())
        {
            Assert.Null(await beforeExpiry.WebhookOutboxEvents.Where(candidate => candidate.Id == evt.Id).Select(candidate => candidate.ProcessedAt).SingleAsync(cancellationToken: TestContext.Current.CancellationToken));
        }

        var (recoveryServices, recovery) = CreateDispatchService(now.AddMinutes(1), "recovery-worker");
        using (recoveryServices)
        {
            await recovery.RunOnceAsync(TestContext.Current.CancellationToken);
        }
        await using var verification = await CreateContextAsync();
        Assert.NotNull(await verification.WebhookOutboxEvents.Where(candidate => candidate.Id == evt.Id).Select(candidate => candidate.ProcessedAt).SingleAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, await verification.WebhookDeliveryLogs.CountAsync(log => log.WebhookEventId == evt.Id && log.WebhookEndpointId == endpoint.Id, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData("                                                                                                                                                                                                         ")]
    public async Task OutboxClaim_RejectsInvalidWorkerId(string workerId)
    {
        await using var context = await CreateContextAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => CreateRepository(context).ClaimOutboxEventsAsync(workerId, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), 1, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => CreateRepository(context).ClaimOutboxEventsAsync("worker", DateTimeOffset.UtcNow, TimeSpan.Zero, 1, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => CreateRepository(context).ClaimOutboxEventsAsync("worker", DateTimeOffset.UtcNow, TimeSpan.FromHours(1), 1, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => CreateRepository(context).ClaimOutboxEventsAsync("worker", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), 0, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RetentionCleanup_DeletesOnlyOldProcessedOutboxAndDeliveredLogsWithinEachBatch()
    {
        var cutoff = DateTimeOffset.Parse("2026-08-01T00:00:00Z");
        await using var setup = await CreateContextAsync();
        var workspace = new Workspace { Name = "Retention", Slug = "retention" };
        var user = new User { Email = "retention@example.test", DisplayName = "Retention", PasswordHash = "hash", Role = UserRole.Admin };
        var endpoint = new WebhookEndpoint { WorkspaceId = workspace.Id, Name = "Retention", Url = "https://example.test/retention", Secret = "secret", CreatedByUserId = user.Id };
        var oldProcessed = CreateOutboxEvent(workspace.Id);
        oldProcessed.ProcessedAt = cutoff.AddDays(-2);
        var secondOldProcessed = CreateOutboxEvent(workspace.Id);
        secondOldProcessed.ProcessedAt = cutoff.AddDays(-1);
        var recentProcessed = CreateOutboxEvent(workspace.Id);
        recentProcessed.ProcessedAt = cutoff.AddDays(1);
        var pending = CreateOutboxEvent(workspace.Id);
        var oldDelivered = new WebhookDeliveryLog { WebhookEndpointId = endpoint.Id, EventType = "workspace.updated", Payload = JsonDocument.Parse("{}").RootElement.Clone(), IsDelivered = true, LastAttemptAt = cutoff.AddDays(-2), CreatedAt = cutoff.AddDays(-2) };
        var secondOldDelivered = new WebhookDeliveryLog { WebhookEndpointId = endpoint.Id, EventType = "workspace.updated", Payload = JsonDocument.Parse("{}").RootElement.Clone(), IsDelivered = true, LastAttemptAt = cutoff.AddDays(-1), CreatedAt = cutoff.AddDays(-1) };
        var oldDeadLetter = new WebhookDeliveryLog { WebhookEndpointId = endpoint.Id, EventType = "workspace.updated", Payload = JsonDocument.Parse("{}").RootElement.Clone(), IsFailed = true, IsDeadLetter = true, DeadLetteredAt = cutoff.AddDays(-2), CreatedAt = cutoff.AddDays(-2) };
        setup.AddRange(workspace, user, endpoint, oldProcessed, secondOldProcessed, recentProcessed, pending, oldDelivered, secondOldDelivered, oldDeadLetter);
        await setup.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var cleanupContext = await CreateContextAsync();
        var result = await CreateRepository(cleanupContext).CleanupRetentionAsync(cutoff, 1, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ProcessedOutboxEventsDeleted);
        Assert.Equal(1, result.DeliveredLogsDeleted);
        await using var verification = await CreateContextAsync();
        Assert.Equal(3, await verification.WebhookOutboxEvents.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, await verification.WebhookDeliveryLogs.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(await verification.WebhookOutboxEvents.AnyAsync(evt => evt.Id == recentProcessed.Id, cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(await verification.WebhookOutboxEvents.AnyAsync(evt => evt.Id == pending.Id, cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(await verification.WebhookDeliveryLogs.AnyAsync(log => log.Id == oldDeadLetter.Id, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExpiredDeliveryLease_CannotCompleteBeforeAnotherWorkerReclaimsIt()
    {
        var now = DateTimeOffset.Parse("2026-08-26T16:00:00Z");
        await using var setup = await CreateContextAsync();
        var workspace = new Workspace { Name = "Expired delivery", Slug = "expired-delivery" };
        var user = new User { Email = "expired-delivery@example.test", DisplayName = "Expired delivery", PasswordHash = "hash", Role = UserRole.Admin };
        var endpoint = new WebhookEndpoint { WorkspaceId = workspace.Id, Name = "Expired delivery", Url = "https://example.test/expired-delivery", Secret = "secret", CreatedByUserId = user.Id };
        var leaseToken = Guid.CreateVersion7();
        var delivery = new WebhookDeliveryLog
        {
            WebhookEndpointId = endpoint.Id,
            WebhookEventId = Guid.CreateVersion7(),
            EventType = "workspace.updated",
            Payload = JsonDocument.Parse("{}").RootElement.Clone(),
            NextRetryAt = now.AddMinutes(-1),
            LeaseOwner = "expired-worker",
            LeaseToken = leaseToken,
            LeaseExpiresAt = now.AddTicks(-1)
        };
        setup.AddRange(workspace, user, endpoint, delivery);
        await setup.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var worker = await CreateContextAsync();
        var completed = await CreateRepository(worker).CompleteDeliverySucceededAsync(new WebhookDeliveryCompletionDto(delivery.Id, "expired-worker", leaseToken, now), 204, TestContext.Current.CancellationToken);

        Assert.False(completed);
        await using var verification = await CreateContextAsync();
        var persisted = await verification.WebhookDeliveryLogs.SingleAsync(log => log.Id == delivery.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(persisted.IsDelivered);
        Assert.Equal("expired-worker", persisted.LeaseOwner);
        Assert.Equal(leaseToken, persisted.LeaseToken);
    }

    [Fact]
    public async Task DeliveryCompletion_IsRejectedWhenTheClockPassesLeaseExpiryDuringHttp()
    {
        var now = DateTimeOffset.Parse("2026-08-26T16:02:00Z");
        await using var setup = await CreateContextAsync();
        var workspace = new Workspace { Name = "HTTP lease fence", Slug = "http-lease-fence" };
        var user = new User { Email = "http-lease-fence@example.test", DisplayName = "HTTP lease fence", PasswordHash = "hash", Role = UserRole.Admin };
        var endpoint = new WebhookEndpoint { WorkspaceId = workspace.Id, Name = "HTTP lease fence", Url = "https://example.test/http-lease-fence", Secret = "secret", CreatedByUserId = user.Id };
        var delivery = new WebhookDeliveryLog { WebhookEndpointId = endpoint.Id, EventType = "workspace.updated", Payload = JsonDocument.Parse("{}").RootElement.Clone(), NextRetryAt = now };
        setup.AddRange(workspace, user, endpoint, delivery);
        await setup.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var worker = await CreateContextAsync();
        var repository = CreateRepository(worker);
        var claim = Assert.Single(await repository.ClaimPendingDeliveryLogsAsync("worker", now, TimeSpan.FromSeconds(1), 1, TestContext.Current.CancellationToken));
        var clock = new MutableTimeProvider(now);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(nameof(WebhookDeliveryProcessor)).Returns(new HttpClient(new AdvancingHandler(clock, now.AddSeconds(2))));
        var validator = Substitute.For<IWebhookDestinationValidator>();
        validator.ValidateAsync(endpoint.Url, Arg.Any<CancellationToken>()).Returns(WebhookDestinationValidationResult.Valid(new Uri(endpoint.Url), [IPAddress.Parse("8.8.8.8")]));

        await new WebhookDeliveryProcessor(factory, repository, validator, clock).DeliverRetryAsync(claim, 2, CancellationToken.None);

        await using var verification = await CreateContextAsync();
        var persisted = await verification.WebhookDeliveryLogs.SingleAsync(log => log.Id == delivery.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(persisted.IsDelivered);
        Assert.Equal("worker", persisted.LeaseOwner);
        Assert.Equal(claim.LeaseToken, persisted.LeaseToken);
    }

    [Fact]
    public async Task ExpiredOutboxLease_CannotMaterializeBeforeAnotherWorkerReclaimsIt()
    {
        var now = DateTimeOffset.Parse("2026-08-26T16:05:00Z");
        await using var setup = await CreateContextAsync();
        var leaseToken = Guid.CreateVersion7();
        var evt = CreateOutboxEvent();
        evt.LeaseOwner = "expired-worker";
        evt.LeaseToken = leaseToken;
        evt.LeaseExpiresAt = now.AddTicks(-1);
        setup.WebhookOutboxEvents.Add(evt);
        await setup.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var worker = await CreateContextAsync();
        var materialized = await CreateRepository(worker).MaterializeOutboxEventAsync(new ClaimedWebhookOutboxEventDto(evt.Id, evt.EventType, evt.WorkspaceId, evt.EntityId, evt.Payload, evt.OccurredAt, "expired-worker", leaseToken), now, TestContext.Current.CancellationToken);

        Assert.False(materialized);
        await using var verification = await CreateContextAsync();
        var persisted = await verification.WebhookOutboxEvents.SingleAsync(candidate => candidate.Id == evt.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(persisted.ProcessedAt);
        Assert.Equal("expired-worker", persisted.LeaseOwner);
        Assert.Equal(leaseToken, persisted.LeaseToken);
    }

    [Fact]
    public async Task MaterializationLock_PreventsExpiredClaimFromBeingReclaimedBeforeItCommits()
    {
        var now = DateTimeOffset.Parse("2026-08-26T16:10:00Z");
        await using var setup = await CreateContextAsync();
        var workspace = new Workspace { Name = "Outbox lock", Slug = "outbox-lock" };
        var user = new User { Email = "outbox-lock@example.test", DisplayName = "Outbox lock", PasswordHash = "hash", Role = UserRole.Admin };
        var endpoint = new WebhookEndpoint { WorkspaceId = workspace.Id, Name = "Outbox lock", Url = "https://example.test/outbox-lock", Secret = "secret", CreatedByUserId = user.Id };
        endpoint.Subscriptions.Add(new WebhookSubscription { WebhookEndpointId = endpoint.Id, EventType = "workspace.updated" });
        var evt = CreateOutboxEvent(workspace.Id);
        setup.AddRange(workspace, user, endpoint, evt);
        await setup.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var claimantContext = await CreateContextAsync();
        var claim = Assert.Single(await CreateRepository(claimantContext).ClaimOutboxEventsAsync("worker-a", now, TimeSpan.FromSeconds(1), 1, TestContext.Current.CancellationToken));
        var pause = new PauseAfterForUpdateInterceptor("webhook_outbox_events");
        await using var completionContext = await CreateContextAsync(pause);
        var completing = CreateRepository(completionContext).MaterializeOutboxEventAsync(claim, now, CancellationToken.None);
        await pause.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await using var reclaimerContext = await CreateContextAsync();
        var reclaimed = await CreateRepository(reclaimerContext).ClaimOutboxEventsAsync("worker-b", now.AddMinutes(1), TimeSpan.FromMinutes(1), 1, TestContext.Current.CancellationToken);
        Assert.Empty(reclaimed);

        pause.Release.TrySetResult();
        Assert.True(await completing);
        await using var verification = await CreateContextAsync();
        var persisted = await verification.WebhookOutboxEvents.SingleAsync(candidate => candidate.Id == evt.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(persisted.ProcessedAt);
        Assert.Null(persisted.LeaseOwner);
        Assert.Null(persisted.LeaseToken);
        Assert.Equal(1, await verification.WebhookDeliveryLogs.CountAsync(log => log.WebhookEventId == evt.Id && log.WebhookEndpointId == endpoint.Id, cancellationToken: TestContext.Current.CancellationToken));
    }

    private static WebhookOutboxEvent CreateOutboxEvent(Guid? workspaceId = null) => new()
    {
        EventType = "workspace.updated",
        WorkspaceId = workspaceId,
        EntityId = Guid.CreateVersion7(),
        Payload = JsonDocument.Parse("{}").RootElement.Clone(),
        OccurredAt = DateTimeOffset.Parse("2026-08-26T00:00:00Z")
    };

    private static WebhookRepository CreateRepository(CmsifyDbContext context)
    {
        var protector = Substitute.For<ISecretProtector>();
        protector.Unprotect(Arg.Any<string>()).Returns(call => call.Arg<string>());
        return new WebhookRepository(context, CurrentActorInfo.Anonymous, protector, SecretProtectionOptions());
    }

    private static IOptions<SecretProtectionOptions> SecretProtectionOptions() => Options.Create(new SecretProtectionOptions
    {
        ActiveKeyId = "key_current",
        EncryptionKeys = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["key_current"] = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
        }
    });

    private (ServiceProvider Services, WebhookDispatchService Service) CreateDispatchService(DateTimeOffset now, string workerId)
    {
        var services = new ServiceCollection();
        services.AddDbContext<CmsifyDbContext>(options => options.UseNpgsql(postgres.GetConnectionString()).UseSyntaxCircusSnakeCaseNamingConvention());
        services.AddScoped<IWebhookRepository>(provider => CreateRepository(provider.GetRequiredService<CmsifyDbContext>()));
        var provider = services.BuildServiceProvider();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Webhook:OutboxPollIntervalSeconds"] = "30",
            ["Webhook:OutboxLeaseDurationSeconds"] = "300",
            ["Webhook:OutboxBatchSize"] = "10"
        }).Build();
        return (provider, new WebhookDispatchService(provider.GetRequiredService<IServiceScopeFactory>(), configuration, NullLogger<WebhookDispatchService>.Instance, new MutableTimeProvider(now), workerId));
    }

    private async Task<CmsifyDbContext> CreateContextAsync(DbCommandInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<CmsifyDbContext>()
            .UseNpgsql(postgres.GetConnectionString())
            .UseSyntaxCircusSnakeCaseNamingConvention();
        if (interceptor is not null)
        {
            builder.AddInterceptors(interceptor);
        }

        var options = builder.Options;
        var context = new CmsifyDbContext(options);
        await context.Database.MigrateAsync();
        return context;
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class CapturingHandler(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        private readonly Queue<HttpStatusCode> statuses = new(statuses);

        public List<string> EventIds { get; } = [];

        public List<byte[]> Payloads { get; } = [];

        public List<string> Signatures { get; } = [];

        public List<WebhookDestinationValidationResult?> Destinations { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            EventIds.Add(request.Headers.GetValues("X-Cmsify-Event-Id").Single());
            Payloads.Add(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            Signatures.Add(request.Headers.GetValues("X-Cmsify-Signature").Single());
            Destinations.Add(request.Options.TryGetValue(PinnedWebhookTransport.DestinationKey, out var destination) ? destination : null);
            return new HttpResponseMessage(this.statuses.Dequeue());
        }
    }

    private sealed class CapturingConnector : IWebhookSocketConnector
    {
        public List<IReadOnlyList<IPAddress>> AddressSets { get; } = [];

        public ValueTask<Stream> ConnectAsync(IReadOnlyList<IPAddress> addresses, int port, CancellationToken ct)
        {
            AddressSets.Add(addresses.ToArray());
            return ValueTask.FromException<Stream>(new HttpRequestException("test connection refused"));
        }
    }

    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("A rejected destination must not create an HTTP client.");
    }

    private sealed class AdvancingHandler(MutableTimeProvider clock, DateTimeOffset completedAt, HttpStatusCode status = HttpStatusCode.NoContent) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            clock.UtcNow = completedAt;
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    private sealed class PauseAfterForUpdateInterceptor(string table) : DbCommandInterceptor
    {
        private int paused;

        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains($"FROM {table}", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("WHERE id", StringComparison.OrdinalIgnoreCase)
                && Interlocked.Exchange(ref paused, 1) == 0)
            {
                Reached.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }

}
