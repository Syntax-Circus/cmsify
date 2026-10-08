using Cmsify.Core.ContentWrites;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.EmbeddedContent;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Core.Services;
using Cmsify.Infrastructure.Auth;
using Cmsify.Infrastructure.Persistence.ContentWrites;
using Cmsify.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Shouldly;

namespace Cmsify.Infrastructure.Tests;

public sealed class EmbeddedContentReviewRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegisteredOrdinaryUpdateRejectsOverlengthAndSchemaDriftWithoutWrites(bool drift)
    {
        await using var fixture = await EmbeddedContentFixture.Create();
        var initial = (await fixture.Repository.CreateEmbeddedContentAsync(fixture.CreateRequest(), fixture.Actor,
            EmbeddedContentFixture.Permit, EmbeddedContentFixture.Ct)).Value;
        if (drift)
        {
            await using var mutate = fixture.Fresh();
            (await mutate.TemplateFields.SingleAsync(EmbeddedContentFixture.Ct)).Label = "Drifted";
            await mutate.SaveChangesAsync(EmbeddedContentFixture.Ct);
        }
        await using var before = fixture.Fresh();
        var audits = await before.AuditLogs.CountAsync(EmbeddedContentFixture.Ct);
        var outbox = await before.WebhookOutboxEvents.CountAsync(EmbeddedContentFixture.Ct);
        var result = await Update(fixture, initial, drift ? "Valid" : new string('x', 201));
        result.IsFailure.ShouldBeTrue();
        result.Errors[0].Code.ShouldBe(ContentVersionWriteErrors.ContentValidationFailed);
        await using var after = fixture.Fresh();
        var persisted = await after.ContentVersions.Include(v => v.FieldValues).SingleAsync(EmbeddedContentFixture.Ct);
        ContentVersionRevisionCondition.Normalize(persisted.UpdatedAt).ShouldBe(initial.Receipt.CommittedRevision);
        persisted.FieldValues.Single().TextValue.ShouldBe("Initial");
        (await after.AuditLogs.CountAsync(EmbeddedContentFixture.Ct)).ShouldBe(audits);
        (await after.WebhookOutboxEvents.CountAsync(EmbeddedContentFixture.Ct)).ShouldBe(outbox);
    }

    [Fact]
    public async Task EmptyOptionalDraftReplayIsCoherentDuringOrdinaryUpdate()
    {
        await using var fixture = await EmbeddedContentFixture.Create(optionalOnly: true);
        var request = fixture.CreateRequest() with { Fields = Array.Empty<ContentVersionFieldInput>() };
        var initial = (await fixture.Repository.CreateEmbeddedContentAsync(request, fixture.Actor,
            EmbeddedContentFixture.Permit, EmbeddedContentFixture.Ct)).Value;
        var updated = false;
        var replay = await fixture.Repository.CreateEmbeddedContentAsync(request, fixture.Actor, async (_, _) =>
        {
            if (!updated)
            {
                updated = true;
                (await Update(fixture, initial, "Later")).IsSuccess.ShouldBeTrue();
            }
            return true;
        }, EmbeddedContentFixture.Ct);
        updated.ShouldBeTrue();
        if (replay.IsFailure) replay.Errors[0].Code.ShouldBe(EmbeddedContentErrors.RefreshRequired);
        else
        {
            replay.Value.Version.Revision.ShouldBe(initial.Receipt.CommittedRevision);
            replay.Value.Version.Version.Fields.ShouldBeEmpty();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceiptProofSurvivesLaterInvalidEditableFields(bool versionCreate)
    {
        await using var fixture = await EmbeddedContentFixture.Create();
        var committed = (await fixture.Repository.CreateEmbeddedContentAsync(fixture.CreateRequest(), fixture.Actor,
            EmbeddedContentFixture.Permit, EmbeddedContentFixture.Ct)).Value;
        if (versionCreate) committed = (await fixture.Repository.CreateEmbeddedContentVersionAsync(fixture.VersionRequest(committed),
            fixture.Actor, EmbeddedContentFixture.Permit, EmbeddedContentFixture.Ct)).Value;
        await using (var mutate = fixture.Fresh())
        {
            var field = await mutate.ContentVersionFieldValues.SingleAsync(f => f.ContentVersionId == committed.Version.Version.Id, EmbeddedContentFixture.Ct);
            field.TextValue = new string('x', 201);
            await mutate.SaveChangesAsync(EmbeddedContentFixture.Ct);
        }
        await using var before = fixture.Fresh();
        var audits = await before.AuditLogs.CountAsync(EmbeddedContentFixture.Ct);
        var outbox = await before.WebhookOutboxEvents.CountAsync(EmbeddedContentFixture.Ct);
        var receipt = await fixture.Repository.GetEmbeddedContentOperationReceiptAsync(new(fixture.Schema.WorkspaceId,
            committed.Receipt.ContentItemId, committed.Receipt.Kind, committed.Receipt.OperationKey, fixture.Schema.Fingerprint),
            EmbeddedContentFixture.Permit, EmbeddedContentFixture.Ct);
        receipt.IsSuccess.ShouldBeTrue();
        receipt.Value.ShouldBe(committed.Receipt);
        await using var after = fixture.Fresh();
        (await after.AuditLogs.CountAsync(EmbeddedContentFixture.Ct)).ShouldBe(audits);
        (await after.WebhookOutboxEvents.CountAsync(EmbeddedContentFixture.Ct)).ShouldBe(outbox);
    }

    [Fact]
    public async Task VersionCreationKeepsPinnedSourceAuthorityAcrossAllocationResponseAndReplay()
    {
        await using var fixture = await EmbeddedContentFixture.Create();
        var source = (await fixture.Repository.CreateEmbeddedContentAsync(fixture.CreateRequest(), fixture.Actor,
            EmbeddedContentFixture.Permit, EmbeddedContentFixture.Ct)).Value;
        (await fixture.Repository.CreateEmbeddedContentVersionAsync(fixture.VersionRequest(source), fixture.Actor,
            EmbeddedContentFixture.Permit, EmbeddedContentFixture.Ct)).IsSuccess.ShouldBeTrue();
        Task<bool> Strict(EmbeddedContentOperation operation, CancellationToken ct) => Task.FromResult(
            operation.Kind == EmbeddedContentOperationKind.VersionCreate && operation.VersionNumber == 1
            && operation.ContentItemId == source.Receipt.ContentItemId && operation.TemplateVersionId == fixture.Schema.TemplateVersionId);
        var request = fixture.VersionRequest(source, "Third");
        var created = await fixture.Repository.CreateEmbeddedContentVersionAsync(request, fixture.Actor, Strict, EmbeddedContentFixture.Ct);
        created.IsSuccess.ShouldBeTrue();
        created.Value.Receipt.VersionNumber.ShouldBe(3);
        (await fixture.Repository.CreateEmbeddedContentVersionAsync(request, fixture.Actor, Strict, EmbeddedContentFixture.Ct)).IsSuccess.ShouldBeTrue();
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 2).Select(i => fixture.Repository.CreateEmbeddedContentVersionAsync(
            fixture.VersionRequest(source, "Concurrent" + i), fixture.Actor, Strict, EmbeddedContentFixture.Ct)));
        concurrent.ShouldAllBe(r => r.IsSuccess);
        concurrent.Select(r => r.Value.Receipt.VersionNumber).Order().ShouldBe(new[] { 4, 5 });
    }

    private static Task<SyntaxCircus.Common.Result<UpdatedContentVersionOutput>> Update(EmbeddedContentFixture fixture,
        EmbeddedContentWriteOutput source, string text)
    {
        var authorization = Substitute.For<IWorkspaceAuthorizationService>();
        authorization.CanWriteWorkspaceAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        var guard = Substitute.For<IContentVersionResourceGuard>();
        guard.CanEditAsync(Arg.Is<ContentVersionEditSnapshot>(s => s.ContentItemId == source.Receipt.ContentItemId
            && s.TemplateVersionId == fixture.Schema.TemplateVersionId), Arg.Any<CancellationToken>()).Returns(true);
        var handler = new UpdateContentVersionRequestHandler(new ContentVersionEditRepository(fixture.Options, new ContentValidator(),
            Substitute.For<IContentSearchVectorBuilder>(), TimeProvider.System),
            new CurrentActorInfo(fixture.Actor, null, UserRole.Editor, null, true, false), authorization, TimeProvider.System, guard);
        return handler.HandleAsync(new(fixture.Schema.WorkspaceId, source.Receipt.ContentItemId, source.Receipt.VersionNumber,
            new(source.Receipt.CommittedRevision), null, null, fixture.Fields(text), false), EmbeddedContentFixture.Ct);
    }
}
