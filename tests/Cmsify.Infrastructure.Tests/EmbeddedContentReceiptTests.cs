using Cmsify.Core.ContentWrites;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.EmbeddedContent;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Core.Services;
using Cmsify.Infrastructure.Persistence.EmbeddedContent;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Shouldly;
using Cmsify.Infrastructure.Tests.Fixtures;

namespace Cmsify.Infrastructure.Tests;

public sealed class EmbeddedContentReceiptTests
{
    [Fact]
    public async Task ReceiptReadAfterLaterEditStillProvesCommit()
    {
        await using var fixture = await ContentListQueryFixtures.Create(false);
        var token = TestContext.Current.CancellationToken;
        var schema = await new EmbeddedTemplateRepository(fixture.Options).EnsureAsync(new(fixture.Workspace.Id,
            EmbeddedTemplateRepositoryTests.Contract()), Guid.NewGuid(), (_, _) => Task.FromResult(true), token);
        schema.IsSuccess.ShouldBeTrue();
        var actor = Guid.NewGuid();
        var repository = new EmbeddedContentRepository(fixture.Options, new ContentValidator(),
            Substitute.For<IContentSearchVectorBuilder>(), TimeProvider.System);
        var request = new CreateEmbeddedContentRequest(fixture.Workspace.Id, schema.Value.TemplateVersionId,
            schema.Value.Fingerprint, Guid.NewGuid(), Guid.NewGuid(), [new(schema.Value.Fields[0].FieldId,
                0, ValueKind.Text, "Initial", null, null, null, null, null)]);
        var initial = await repository.CreateEmbeddedContentAsync(request, actor, (_, _) => Task.FromResult(true), token);
        initial.IsSuccess.ShouldBeTrue();
        await using (var db = fixture.Context())
        {
            var version = await db.ContentVersions.SingleAsync(token);
            version.UpdatedAt = version.UpdatedAt.AddSeconds(1);
            await db.SaveChangesAsync(token);
        }
        var result = await repository.GetEmbeddedContentOperationReceiptAsync(new(request.WorkspaceId, request.ContentItemId,
            EmbeddedWriteKind.ItemCreate, request.OperationKey, request.ContractFingerprint), (_, _) => Task.FromResult(true), token);
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(initial.Value.Receipt);
        var replay = await repository.CreateEmbeddedContentAsync(request, actor, (_, _) => Task.FromResult(true), token);
        replay.Errors[0].Code.ShouldBe(EmbeddedContentErrors.RefreshRequired);
    }

    [Fact]
    public async Task RevocationBlocksReceiptAndReplay()
    {
        await using var fixture = await EmbeddedContentFixture.Create();
        var request = fixture.CreateRequest();
        var initial = await fixture.Repository.CreateEmbeddedContentAsync(request, fixture.Actor, EmbeddedContentFixture.Permit, EmbeddedContentFixture.Ct);
        initial.IsSuccess.ShouldBeTrue();
        var replay = await fixture.Repository.CreateEmbeddedContentAsync(request, fixture.Actor, (_, _) => Task.FromResult(false), EmbeddedContentFixture.Ct);
        replay.Errors[0].Code.ShouldBe("forbidden");
        var receipt = await fixture.Repository.GetEmbeddedContentOperationReceiptAsync(new(request.WorkspaceId, request.ContentItemId,
            EmbeddedWriteKind.ItemCreate, request.OperationKey, request.ContractFingerprint), (_, _) => Task.FromResult(false), EmbeddedContentFixture.Ct);
        receipt.Errors[0].Code.ShouldBe("forbidden");
        await using var db = fixture.Fresh();
        (await db.ContentVersions.CountAsync(EmbeddedContentFixture.Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task ForeignOperationKeyDoesNotLeak()
    {
        await using var fixture = await EmbeddedContentFixture.Create();
        var first = fixture.CreateRequest(); var second = fixture.CreateRequest();
        (await fixture.Repository.CreateEmbeddedContentAsync(first, fixture.Actor, EmbeddedContentFixture.Permit, EmbeddedContentFixture.Ct)).IsSuccess.ShouldBeTrue();
        (await fixture.Repository.CreateEmbeddedContentAsync(second, fixture.Actor, EmbeddedContentFixture.Permit, EmbeddedContentFixture.Ct)).IsSuccess.ShouldBeTrue();
        await using var before = fixture.Fresh();
        var auditCount = await before.AuditLogs.CountAsync(EmbeddedContentFixture.Ct);
        var outboxCount = await before.WebhookOutboxEvents.CountAsync(EmbeddedContentFixture.Ct);
        var receipt = await fixture.Repository.GetEmbeddedContentOperationReceiptAsync(new(second.WorkspaceId, second.ContentItemId,
            EmbeddedWriteKind.ItemCreate, first.OperationKey, second.ContractFingerprint), EmbeddedContentFixture.Permit, EmbeddedContentFixture.Ct);
        receipt.Errors[0].Code.ShouldBe("not-found");
        var valid = await fixture.Repository.GetEmbeddedContentOperationReceiptAsync(new(second.WorkspaceId, second.ContentItemId,
            EmbeddedWriteKind.ItemCreate, second.OperationKey, second.ContractFingerprint), EmbeddedContentFixture.Permit, EmbeddedContentFixture.Ct);
        valid.IsSuccess.ShouldBeTrue();
        await using var after = fixture.Fresh();
        (await after.AuditLogs.CountAsync(EmbeddedContentFixture.Ct)).ShouldBe(auditCount);
        (await after.WebhookOutboxEvents.CountAsync(EmbeddedContentFixture.Ct)).ShouldBe(outboxCount);
        typeof(EmbeddedContentReceiptOutput).GetProperties().ShouldNotContain(p => p.Name.Contains("Field") || p.Name.Contains("Fingerprint"));
    }
}
