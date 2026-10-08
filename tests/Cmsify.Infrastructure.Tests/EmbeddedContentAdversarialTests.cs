using System.Data.Common;
using Cmsify.Core.ContentWrites;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.EmbeddedContent;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Shouldly;

namespace Cmsify.Infrastructure.Tests;

public sealed class EmbeddedContentAdversarialTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameKeyDifferentActorOrFieldsConflicts(bool version)
    {
        await using var fixture = await EmbeddedContentFixture.Create();
        var request = fixture.CreateRequest();
        var initial = await fixture.Repository.CreateEmbeddedContentAsync(request, fixture.Actor, EmbeddedContentFixture.Permit, EmbeddedContentFixture.Ct);
        initial.IsSuccess.ShouldBeTrue();
        var next = fixture.VersionRequest(initial.Value);
        if (version) (await fixture.Repository.CreateEmbeddedContentVersionAsync(next, fixture.Actor, EmbeddedContentFixture.Permit, EmbeddedContentFixture.Ct)).IsSuccess.ShouldBeTrue();
        var differentActor = version
            ? await fixture.Repository.CreateEmbeddedContentVersionAsync(next, Guid.NewGuid(), EmbeddedContentFixture.Permit, EmbeddedContentFixture.Ct)
            : await fixture.Repository.CreateEmbeddedContentAsync(request, Guid.NewGuid(), EmbeddedContentFixture.Permit, EmbeddedContentFixture.Ct);
        differentActor.Errors[0].Code.ShouldBe(EmbeddedContentErrors.OperationConflict);
        var differentFields = version
            ? await fixture.Repository.CreateEmbeddedContentVersionAsync(next with { Fields = fixture.Fields("Different") }, fixture.Actor, EmbeddedContentFixture.Permit, EmbeddedContentFixture.Ct)
            : await fixture.Repository.CreateEmbeddedContentAsync(request with { Fields = fixture.Fields("Different") }, fixture.Actor, EmbeddedContentFixture.Permit, EmbeddedContentFixture.Ct);
        differentFields.Errors[0].Code.ShouldBe(EmbeddedContentErrors.OperationConflict);
    }

    [Fact]
    public async Task ReservedIdCannotAdoptForeignItem()
    {
        await using var fixture = await EmbeddedContentFixture.Create();
        var request = fixture.CreateRequest();
        (await fixture.Repository.CreateEmbeddedContentAsync(request, fixture.Actor, EmbeddedContentFixture.Permit, EmbeddedContentFixture.Ct)).IsSuccess.ShouldBeTrue();
        var conflict = await fixture.Repository.CreateEmbeddedContentAsync(request with { OperationKey = Guid.NewGuid() }, fixture.Actor,
            EmbeddedContentFixture.Permit, EmbeddedContentFixture.Ct);
        conflict.Errors[0].Code.ShouldBe(EmbeddedContentErrors.OperationConflict);
        await using var db = fixture.Fresh();
        (await db.ContentVersions.CountAsync(EmbeddedContentFixture.Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task SourceRevisionCheckedBeforeCreation()
    {
        await using var fixture = await EmbeddedContentFixture.Create();
        var initial = await fixture.Repository.CreateEmbeddedContentAsync(fixture.CreateRequest(), fixture.Actor, EmbeddedContentFixture.Permit, EmbeddedContentFixture.Ct);
        var request = fixture.VersionRequest(initial.Value) with { SourceRevision = new(1) };
        var result = await fixture.Repository.CreateEmbeddedContentVersionAsync(request, fixture.Actor, EmbeddedContentFixture.Permit, EmbeddedContentFixture.Ct);
        result.Errors[0].Code.ShouldBe("concurrency-mismatch");
        await using var db = fixture.Fresh();
        (await db.ContentVersions.CountAsync(EmbeddedContentFixture.Ct)).ShouldBe(1);
        (await db.EmbeddedContentReceipts.CountAsync(EmbeddedContentFixture.Ct)).ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentSameOperationCreatesOneReceipt(bool version)
    {
        await using var fixture = await EmbeddedContentFixture.Create();
        var request = fixture.CreateRequest();
        if (!version)
        {
            var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => fixture.Repository.CreateEmbeddedContentAsync(request,
                fixture.Actor, EmbeddedContentFixture.Permit, EmbeddedContentFixture.Ct)));
            results.ShouldAllBe(x => x.IsSuccess);
            results.Select(x => x.Value.Version.Version.Id).Distinct().Count().ShouldBe(1);
        }
        else
        {
            var source = await fixture.Repository.CreateEmbeddedContentAsync(request, fixture.Actor, EmbeddedContentFixture.Permit, EmbeddedContentFixture.Ct);
            var next = fixture.VersionRequest(source.Value);
            var results = await Task.WhenAll(Enumerable.Range(0,4).Select(_ => fixture.Repository.CreateEmbeddedContentVersionAsync(next,
                fixture.Actor, EmbeddedContentFixture.Permit, EmbeddedContentFixture.Ct)));
            results.ShouldAllBe(x => x.IsSuccess);
            results.Select(x => x.Value.Version.Version.Id).Distinct().Count().ShouldBe(1);
        }
        await using var db = fixture.Fresh();
        (await db.EmbeddedContentReceipts.CountAsync(EmbeddedContentFixture.Ct)).ShouldBe(version ? 2 : 1);
        var audits = await db.AuditLogs.Where(x => x.EntityType == "ContentVersion").ToArrayAsync(EmbeddedContentFixture.Ct);
        audits.Length.ShouldBe(version ? 2 : 1);
        audits.ShouldAllBe(x => x.ActorUserId == fixture.Actor && x.ActorApiClientId == null);
    }

    [Fact]
    public async Task ConcurrentAllocationHasUniqueNumbers()
    {
        await using var fixture = await EmbeddedContentFixture.Create();
        var source = await fixture.Repository.CreateEmbeddedContentAsync(fixture.CreateRequest(), fixture.Actor, EmbeddedContentFixture.Permit, EmbeddedContentFixture.Ct);
        var results = await Task.WhenAll(Enumerable.Range(0,4).Select(_ => fixture.Repository.CreateEmbeddedContentVersionAsync(
            fixture.VersionRequest(source.Value), fixture.Actor, EmbeddedContentFixture.Permit, EmbeddedContentFixture.Ct)));
        results.ShouldAllBe(x => x.IsSuccess);
        results.Select(x => x.Value.Receipt.VersionNumber).Order().ShouldBe([2,3,4,5]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BeforeCommitFailureLeavesNoOrphans(bool version)
    {
        var observer = new CommitObserver();
        await using var fixture = await EmbeddedContentFixture.Create(observer);
        var source = version ? await fixture.Repository.CreateEmbeddedContentAsync(fixture.CreateRequest(), fixture.Actor, EmbeddedContentFixture.Permit, EmbeddedContentFixture.Ct) : null;
        observer.Before = () => throw new InvalidOperationException("before commit");
        await Should.ThrowAsync<InvalidOperationException>(async () => {
            if (version) await fixture.Repository.CreateEmbeddedContentVersionAsync(fixture.VersionRequest(source!.Value), fixture.Actor, EmbeddedContentFixture.Permit, EmbeddedContentFixture.Ct);
            else await fixture.Repository.CreateEmbeddedContentAsync(fixture.CreateRequest(), fixture.Actor, EmbeddedContentFixture.Permit, EmbeddedContentFixture.Ct);
        });
        await using var db = fixture.Fresh();
        (await db.ContentVersions.CountAsync(EmbeddedContentFixture.Ct)).ShouldBe(version ? 1 : 0);
        (await db.EmbeddedContentReceipts.CountAsync(EmbeddedContentFixture.Ct)).ShouldBe(version ? 1 : 0);
        (await db.WebhookOutboxEvents.CountAsync(EmbeddedContentFixture.Ct)).ShouldBe(version ? 2 : 1);
        (await db.AuditLogs.CountAsync(x => x.EntityType == "ContentVersion", EmbeddedContentFixture.Ct)).ShouldBe(version ? 1 : 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledAfterCommitReconcilesWithoutReplay(bool version)
    {
        var observer = new CommitObserver();
        await using var fixture = await EmbeddedContentFixture.Create(observer);
        var request = fixture.CreateRequest();
        var source = version ? await fixture.Repository.CreateEmbeddedContentAsync(request, fixture.Actor, EmbeddedContentFixture.Permit, EmbeddedContentFixture.Ct) : null;
        var next = source is null ? null : fixture.VersionRequest(source.Value);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(EmbeddedContentFixture.Ct);
        observer.After = (_, _) => { cancellation.Cancel(); return Task.CompletedTask; };
        await Should.ThrowAsync<OperationCanceledException>(async () => {
            if (version) await fixture.Repository.CreateEmbeddedContentVersionAsync(next!, fixture.Actor, EmbeddedContentFixture.Permit, cancellation.Token);
            else await fixture.Repository.CreateEmbeddedContentAsync(request, fixture.Actor, EmbeddedContentFixture.Permit, cancellation.Token);
        });
        observer.After = null;
        var receipt = await fixture.Repository.GetEmbeddedContentOperationReceiptAsync(new(request.WorkspaceId, request.ContentItemId,
            version ? EmbeddedWriteKind.VersionCreate : EmbeddedWriteKind.ItemCreate, version ? next!.OperationKey : request.OperationKey, request.ContractFingerprint),
            EmbeddedContentFixture.Permit, EmbeddedContentFixture.Ct);
        receipt.IsSuccess.ShouldBeTrue();
        await using var db = fixture.Fresh();
        (await db.ContentVersions.CountAsync(EmbeddedContentFixture.Ct)).ShouldBe(version ? 2 : 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalResponseRacingEditRequiresRefresh(bool version)
    {
        var observer = new CommitObserver();
        await using var fixture = await EmbeddedContentFixture.Create(observer);
        var source = version ? await fixture.Repository.CreateEmbeddedContentAsync(fixture.CreateRequest(), fixture.Actor, EmbeddedContentFixture.Permit, EmbeddedContentFixture.Ct) : null;
        observer.After = async (_, ct) => {
            await using var db = fixture.Fresh();
            var latest = await db.ContentVersions.OrderByDescending(x => x.VersionNumber).FirstAsync(ct);
            latest.UpdatedAt = latest.UpdatedAt.AddSeconds(1);
            await db.SaveChangesAsync(ct);
        };
        var result = version
            ? await fixture.Repository.CreateEmbeddedContentVersionAsync(fixture.VersionRequest(source!.Value), fixture.Actor, EmbeddedContentFixture.Permit, EmbeddedContentFixture.Ct)
            : await fixture.Repository.CreateEmbeddedContentAsync(fixture.CreateRequest(), fixture.Actor, EmbeddedContentFixture.Permit, EmbeddedContentFixture.Ct);
        result.Errors[0].Code.ShouldBe(EmbeddedContentErrors.RefreshRequired);
    }

    internal sealed class CommitObserver : DbTransactionInterceptor
    {
        internal Action? Before;
        internal Func<CmsifyDbContext, CancellationToken, Task>? After;
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        { Before?.Invoke(); return ValueTask.FromResult(result); }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
            => After?.Invoke((CmsifyDbContext)eventData.Context!, cancellationToken) ?? Task.CompletedTask;
    }
}
