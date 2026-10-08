using System.Text.Json;
using Cmsify.Core.EmbeddedContent;
using Cmsify.Core.Domain.Enums;
using Cmsify.Infrastructure.Persistence.EmbeddedContent;
using Shouldly;
using Microsoft.EntityFrameworkCore;
using Cmsify.Infrastructure.Tests.Fixtures;

namespace Cmsify.Infrastructure.Tests;

public sealed class EmbeddedTemplateRepositoryTests
{
    [Fact]
    public async Task RegisteredSchemaMustRemainCurrentPublishedSchema()
    {
        await using var fixture=await EmbeddedContentFixture.Create();
        await using(var db=fixture.Fresh()) { var template=await db.Templates.SingleAsync(x=>x.Id==fixture.Schema.TemplateId,EmbeddedContentFixture.Ct); template.CurrentVersionId=null; await db.SaveChangesAsync(EmbeddedContentFixture.Ct); }
        var result=await new EmbeddedTemplateRepository(fixture.Options).GetAsync(new(fixture.Schema.WorkspaceId,fixture.Schema.ContractKey,fixture.Schema.Fingerprint),EmbeddedContentFixture.Permit,EmbeddedContentFixture.Ct);
        result.IsFailure.ShouldBeTrue();
    }
    [Fact]
    public async Task EnsureRepeatPreservesHostAuditAndPublicationOutbox()
    {
        await using var fixture = await EmbeddedContentFixture.Create();
        await using var before = fixture.Fresh();
        var audit = await before.AuditLogs.ToArrayAsync(EmbeddedContentFixture.Ct);
        audit.ShouldNotBeEmpty();
        audit.ShouldAllBe(a => a.ActorUserId == fixture.Actor && a.ActorApiClientId == null);
        var repeat = await new EmbeddedTemplateRepository(fixture.Options).EnsureAsync(new(fixture.Schema.WorkspaceId,
            Contract()), fixture.Actor, (_, _) => Task.FromResult(true), EmbeddedContentFixture.Ct);
        repeat.IsSuccess.ShouldBeTrue();
        await using var after = fixture.Fresh();
        (await after.AuditLogs.CountAsync(EmbeddedContentFixture.Ct)).ShouldBe(audit.Length);
        (await after.WebhookOutboxEvents.CountAsync(EmbeddedContentFixture.Ct)).ShouldBe(1);
    }
    [Fact]
    public async Task EnsureTwiceReturnsSameIdsWithoutNewAudit()
    {
        await using var fixture = await ContentListQueryFixtures.Create(false);
        var repository = new EmbeddedTemplateRepository(fixture.Options);
        var request = new EnsureEmbeddedTemplateRequest(fixture.Workspace.Id, Contract());
        var token = TestContext.Current.CancellationToken;
        var first = await repository.EnsureAsync(request, Guid.NewGuid(), (_, _) => Task.FromResult(true), token);
        first.IsSuccess.ShouldBeTrue();
        var second = await repository.EnsureAsync(request, Guid.NewGuid(), (_, _) => Task.FromResult(true), token);
        second.IsSuccess.ShouldBeTrue();
        second.Value.TemplateVersionId.ShouldBe(first.Value.TemplateVersionId);
        second.Value.Fields[0].FieldId.ShouldBe(first.Value.Fields[0].FieldId);
        await using var db = fixture.Context();
        (await db.WebhookOutboxEvents.CountAsync(token)).ShouldBe(1);
    }

    internal static EmbeddedTemplateContract Contract() => new("sample.v1", "Sample", "sample-v1", "name", [
        new("name", "Name", 0, true, 1, 1, PrimitiveType.Text, ValueKind.Text, CompositionMode.Inline,
            false, JsonSerializer.Deserialize<JsonElement>("{\"maxLength\":200,\"formatHint\":\"plaintext\"}"))]);

    [Fact]
    public async Task ConcurrentEnsureConverges()
    {
        await using var fixture = await ContentListQueryFixtures.Create(false);
        var request = new EnsureEmbeddedTemplateRequest(fixture.Workspace.Id, Contract());
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => new EmbeddedTemplateRepository(fixture.Options)
            .EnsureAsync(request, Guid.NewGuid(), (_, _) => Task.FromResult(true), TestContext.Current.CancellationToken)));
        results.ShouldAllBe(x => x.IsSuccess);
        results.Select(x => x.Value.TemplateVersionId).Distinct().Count().ShouldBe(1);
        await using var db = fixture.Context();
        (await db.EmbeddedTemplateRegistrations.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public async Task SchemaDriftAndUnregisteredSlugCollisionDeny()
    {
        await using var fixture = await ContentListQueryFixtures.Create(false);
        var repository = new EmbeddedTemplateRepository(fixture.Options);
        var token = TestContext.Current.CancellationToken;
        var request = new EnsureEmbeddedTemplateRequest(fixture.Workspace.Id, Contract());
        var first = await repository.EnsureAsync(request, Guid.NewGuid(), (_, _) => Task.FromResult(true), token);
        first.IsSuccess.ShouldBeTrue();
        var drift = await repository.EnsureAsync(request with { Contract = request.Contract with { Name = "Changed" } },
            Guid.NewGuid(), (_, _) => Task.FromResult(true), token);
        drift.Errors[0].Code.ShouldBe(EmbeddedContentErrors.TemplateMismatch);
        var collision = await repository.EnsureAsync(request with { Contract = request.Contract with { ContractKey = "another.v1" } },
            Guid.NewGuid(), (_, _) => Task.FromResult(true), token);
        collision.Errors[0].Code.ShouldBe(EmbeddedContentErrors.TemplateMismatch);
    }

    [Fact]
    public async Task DeniedSetupWritesNothing()
    {
        await using var fixture = await ContentListQueryFixtures.Create(false);
        var result = await new EmbeddedTemplateRepository(fixture.Options).EnsureAsync(new(fixture.Workspace.Id, Contract()),
            Guid.NewGuid(), (_, _) => Task.FromResult(false), TestContext.Current.CancellationToken);
        result.Errors[0].Code.ShouldBe("forbidden");
        await using var db = fixture.Context();
        (await db.EmbeddedTemplateRegistrations.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
        (await db.Templates.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public async Task GetChecksActualSchema()
    {
        await using var fixture = await ContentListQueryFixtures.Create(false);
        var token = TestContext.Current.CancellationToken;
        var repository = new EmbeddedTemplateRepository(fixture.Options);
        var ensured = await repository.EnsureAsync(new(fixture.Workspace.Id, Contract()), Guid.NewGuid(), (_, _) => Task.FromResult(true), token);
        ensured.IsSuccess.ShouldBeTrue();
        await using (var db = fixture.Context())
        {
            var field = await db.TemplateFields.SingleAsync(f => f.Id == ensured.Value.Fields[0].FieldId, token);
            field.Label = "Tampered";
            await db.SaveChangesAsync(token);
        }
        var result = await repository.GetAsync(new(fixture.Workspace.Id, Contract().ContractKey, ensured.Value.Fingerprint),
            (_, _) => Task.FromResult(true), token);
        result.Errors[0].Code.ShouldBe(EmbeddedContentErrors.TemplateMismatch);
    }

    [Fact]
    public async Task UnsupportedProviderWritesNothing()
    {
        await using var fixture = await ContentListQueryFixtures.Create(true);
        var result = await new EmbeddedTemplateRepository(fixture.Options).EnsureAsync(new(fixture.Workspace.Id, Contract()),
            Guid.NewGuid(), (_, _) => Task.FromResult(true), TestContext.Current.CancellationToken);
        result.Errors[0].Code.ShouldBe(EmbeddedContentErrors.ProviderUnsupported);
        await using var db = fixture.Context();
        (await db.Templates.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
    }
}
