using Cmsify.Core.ContentWrites;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Shouldly;

namespace Cmsify.Infrastructure.Tests;

public sealed class ContentVersionEditSessionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedPreparationCannotLeakIntoNextSave(bool sqlite)
    {
        await using var f = await ContentVersionWriteFixture.Create(sqlite);
        var before = await f.DurableState();
        await using (var failed = await f.Open())
        {
            var invalid = f.Values("rejected") with { Fields = [f.Values().Fields[0] with { ValueKind = ValueKind.Boolean, BoolValue = true }] };
            var result = await failed.PrepareAsync(invalid, f.Actor.UserId, Ct);
            result.IsFailure.ShouldBeTrue();
            result.Errors[0].Code.ShouldBe(ContentVersionWriteErrors.ContentValidationFailed);
            await Should.ThrowAsync<InvalidOperationException>(() => failed.CommitAsync(Ct));
            await Should.ThrowAsync<InvalidOperationException>(() => failed.PrepareAsync(f.Values(), f.Actor.UserId, Ct));
            (await f.DurableState()).ShouldBe(before);
            // Another operation in the same scope must not flush this still-undisposed rejected graph.
            await using var next = await f.Open();
            (await next.PrepareAsync(f.Values(), f.Actor.UserId, Ct)).IsSuccess.ShouldBeTrue();
            (await next.CommitAsync(Ct)).IsSuccess.ShouldBeTrue();
        }
        await AssertSuccess(f, "saved");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnedContextPreservesHostAuditAndOptions(bool sqlite)
    {
        await using var f = await ContentVersionWriteFixture.Create(sqlite);
        var caller = f.Caller;
        await using var session = await f.Open();
        (await session.PrepareAsync(f.Values(), f.Actor.UserId, Ct)).IsSuccess.ShouldBeTrue();
        f.Observer.SavingContexts.ShouldBeEmpty();
        f.Clock.Reads.ShouldBe(0);
        (await session.CommitAsync(Ct)).IsSuccess.ShouldBeTrue();
        session.Snapshot.UpdatedAt.ShouldBe(ContentVersionWriteFixture.LoadedTime);
        var owned = f.Observer.SavingContexts.ShouldHaveSingleItem();
        owned.ShouldNotBeSameAs(caller);
        owned.GetService<IDbContextOptions>().Extensions.OfType<CoreOptionsExtension>().Single().DetailedErrorsEnabled.ShouldBeTrue();
        owned.Database.ProviderName.ShouldBe(caller.Database.ProviderName);
        owned.Database.GetConnectionString().ShouldBe(caller.Database.GetConnectionString());
        var detail = await session.ReadDetailAsync(ContentVersionWriteFixture.SequenceClock.Start.AddSeconds(4), false, Ct);
        detail.Fields.ShouldHaveSingleItem().TextValue.ShouldBe("saved");
        f.Clock.Reads.ShouldBe(3);
        await AssertSuccess(f, "saved");
        await session.DisposeAsync();
        await Should.ThrowAsync<ObjectDisposedException>(() => owned.ContentVersions.CountAsync(Ct));
        (await caller.ContentItems.CountAsync(Ct)).ShouldBe(1);
        // Detached detail remains usable after the operation context is disposed.
        detail.Fields[0].JsonValue!.Value.GetProperty("owned").GetString().ShouldBe("saved");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnrelatedCallerTrackingIsUntouched(bool sqlite)
    {
        await using var f = await ContentVersionWriteFixture.Create(sqlite);
        var tracked = await f.Caller.ContentItems.SingleAsync(x => x.Id == f.Item.Id, Ct);
        tracked.Slug = "caller-only";
        f.Caller.ChangeTracker.DetectChanges();
        var entry = f.Caller.Entry(tracked);
        await using (var first = await f.Open())
        await using (var independent = await f.Open())
        {
            first.ShouldNotBeSameAs(independent);
            (await first.PrepareAsync(f.Values(), f.Actor.UserId, Ct)).IsSuccess.ShouldBeTrue();
            (await first.CommitAsync(Ct)).IsSuccess.ShouldBeTrue();
        }
        entry.State.ShouldBe(EntityState.Modified);
        tracked.Slug.ShouldBe("caller-only");
        await using var read = f.Fresh();
        (await read.ContentItems.SingleAsync(x => x.Id == f.Item.Id, Ct)).Slug.ShouldBe("entry");
        (await f.Caller.TemplateVersions.CountAsync(Ct)).ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwoLoadedSavesHaveOneWinner(bool sqlite)
    {
        await using var f = await ContentVersionWriteFixture.Create(sqlite);
        var bothPrepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCommitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        string? winnerState = null;
        async Task<SyntaxCircus.Common.Result> Save(bool winner)
        {
            await using var session = await f.Open();
            session.Snapshot.UpdatedAt.ShouldBe(ContentVersionWriteFixture.LoadedTime);
            (await session.PrepareAsync(f.Values(winner ? "saved" : "stale"), f.Actor.UserId, Ct)).IsSuccess.ShouldBeTrue();
            if (Interlocked.Increment(ref arrivals) == 2) bothPrepared.SetResult();
            await bothPrepared.Task.WaitAsync(Ct);
            if (!winner) await firstCommitted.Task.WaitAsync(Ct);
            var result = await session.CommitAsync(Ct);
            if (winner)
            {
                winnerState = await f.DurableState();
                firstCommitted.SetResult();
            }
            else
            {
                await Should.ThrowAsync<InvalidOperationException>(() => session.CommitAsync(Ct));
                await Should.ThrowAsync<InvalidOperationException>(() => session.ReadDetailAsync(DateTimeOffset.UtcNow, false, Ct));
            }
            return result;
        }
        var results = await Task.WhenAll(Save(true), Save(false));
        results.Count(x => x.IsSuccess).ShouldBe(1);
        results.Count(x => x.IsFailure && x.Errors[0].Code == ContentVersionWriteErrors.ConcurrencyMismatch).ShouldBe(1);
        (await f.DurableState()).ShouldBe(winnerState);
        await using var fresh = f.Fresh();
        (await fresh.ContentVersionFieldValues.SingleAsync(x => x.ContentVersionId == f.Version.Id, Ct)).TextValue.ShouldBe("saved");
        (await fresh.WebhookOutboxEvents.ToListAsync(Ct)).ShouldHaveSingleItem();
        (await fresh.AuditLogs.Where(x => x.EntityId == f.Version.Id).ToListAsync(Ct)).ShouldHaveSingleItem();
        f.Observer.SavingContexts.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForcedCommitFailureRollsBackAllWrites(bool sqlite)
    {
        await using var f = await ContentVersionWriteFixture.Create(sqlite);
        var before = await f.DurableState();
        await using var failed = await f.Open();
        (await failed.PrepareAsync(f.Values("rejected"), f.Actor.UserId, Ct)).IsSuccess.ShouldBeTrue();
        f.Observer.FailOutbox = true;
        var error = await Should.ThrowAsync<DbUpdateException>(() => failed.CommitAsync(Ct));
        error.InnerException!.Message.ShouldBe("forced outbox SQL failure");
        f.Observer.OutboxInsertAttempts.ShouldBe(1);
        (await f.DurableState()).ShouldBe(before);
        await Should.ThrowAsync<InvalidOperationException>(() => failed.CommitAsync(Ct));
        f.Observer.FailOutbox = false;
        await using var next = await f.Open();
        (await next.PrepareAsync(f.Values(), f.Actor.UserId, Ct)).IsSuccess.ShouldBeTrue();
        (await next.CommitAsync(Ct)).IsSuccess.ShouldBeTrue();
        f.Observer.OutboxInsertAttempts.ShouldBe(2);
        await using var fresh = f.Fresh();
        (await fresh.WebhookOutboxEvents.ToListAsync(Ct)).ShouldHaveSingleItem();
        (await fresh.ContentVersionFieldValues.SingleAsync(x => x.ContentVersionId == f.Version.Id, Ct)).TextValue.ShouldBe("saved");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProjectionFailureLeavesOneCommittedSave(bool sqlite)
    {
        await using var f = await ContentVersionWriteFixture.Create(sqlite);
        f.Observer.ArmProjectionAfterSave = true;
        var exception = await Should.ThrowAsync<InvalidOperationException>(() => f.Handler.HandleAsync(f.Request(), Ct));
        exception.Message.ShouldBe("forced projection failure");
        f.Observer.SavingContexts.Count.ShouldBe(1);
        f.Observer.OutboxInsertAttempts.ShouldBe(1);
        f.Clock.Reads.ShouldBe(4);
        await AssertSuccess(f, "saved");
        await Should.ThrowAsync<ObjectDisposedException>(() => f.Observer.SavingContexts[0].ContentVersions.CountAsync(Ct));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CancelledPreparationOrCommitIsTerminalAndDoesNotLeak(bool sqlite, bool commit)
    {
        await using var f = await ContentVersionWriteFixture.Create(sqlite);
        var before = await f.DurableState();
        await using var session = await f.Open();
        if (commit) (await session.PrepareAsync(f.Values("cancelled"), f.Actor.UserId, Ct)).IsSuccess.ShouldBeTrue();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => commit
            ? session.CommitAsync(cancellation.Token) : session.PrepareAsync(f.Values("cancelled"), f.Actor.UserId, cancellation.Token));
        await Should.ThrowAsync<InvalidOperationException>(() => session.PrepareAsync(f.Values(), f.Actor.UserId, Ct));
        await Should.ThrowAsync<InvalidOperationException>(() => session.CommitAsync(Ct));
        (await f.DurableState()).ShouldBe(before);
        await using var next = await f.Open();
        (await next.PrepareAsync(f.Values(), f.Actor.UserId, Ct)).IsSuccess.ShouldBeTrue();
        (await next.CommitAsync(Ct)).IsSuccess.ShouldBeTrue();
        await AssertSuccess(f, "saved");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleTimestampPrecheckDoesNotPrepareOrWrite(bool sqlite)
    {
        await using var f = await ContentVersionWriteFixture.Create(sqlite);
        var before = await f.DurableState();
        var result = await f.Handler.HandleAsync(f.Request(123), Ct);
        result.IsFailure.ShouldBeTrue();
        result.Errors[0].Code.ShouldBe(ContentVersionWriteErrors.ConcurrencyMismatch);
        f.Observer.SavingContexts.ShouldBeEmpty();
        f.Clock.Reads.ShouldBe(0);
        (await f.DurableState()).ShouldBe(before);
        (await f.Handler.HandleAsync(f.Request(), Ct)).IsSuccess.ShouldBeTrue();
        await AssertSuccess(f, "saved");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionRejectsRepeatedStagesAndDetailBeforeCommit(bool sqlite)
    {
        await using var f = await ContentVersionWriteFixture.Create(sqlite);
        await using var session = await f.Open();
        await Should.ThrowAsync<InvalidOperationException>(() => session.CommitAsync(Ct));
        await Should.ThrowAsync<InvalidOperationException>(() => session.ReadDetailAsync(DateTimeOffset.UtcNow, false, Ct));
        (await session.PrepareAsync(f.Values(), f.Actor.UserId, Ct)).IsSuccess.ShouldBeTrue();
        await Should.ThrowAsync<InvalidOperationException>(() => session.PrepareAsync(f.Values(), f.Actor.UserId, Ct));
        (await session.CommitAsync(Ct)).IsSuccess.ShouldBeTrue();
        await Should.ThrowAsync<InvalidOperationException>(() => session.CommitAsync(Ct));
        f.Observer.SavingContexts.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task OpenDisposesOwnedContextOnFailureOrCancellation(bool sqlite, bool cancelled)
    {
        await using var f = await ContentVersionWriteFixture.Create(sqlite);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        if (cancelled) f.Observer.CancelOpen = cancellation;
        else f.Observer.FailOpen = true;
        if (cancelled) await Should.ThrowAsync<OperationCanceledException>(() => f.Repository.OpenAsync(f.Item.WorkspaceId, f.Item.Id, 1, cancellation.Token));
        else await Should.ThrowAsync<InvalidOperationException>(() => f.Repository.OpenAsync(f.Item.WorkspaceId, f.Item.Id, 1, Ct));
        var owned = f.Observer.LastQueryContext.ShouldNotBeNull();
        await Should.ThrowAsync<ObjectDisposedException>(() => owned.ContentVersions.CountAsync(Ct));
        f.Observer.CancelOpen = null;
        f.Observer.FailOpen = false;
        (await f.Caller.ContentItems.CountAsync(Ct)).ShouldBe(1);
        await using var next = await f.Open();
        next.Snapshot.Id.ShouldBe(f.Version.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenHidesDeletedWrongWorkspaceAndMissingVersions(bool sqlite)
    {
        await using var f = await ContentVersionWriteFixture.Create(sqlite);
        (await f.Repository.OpenAsync(Guid.NewGuid(), f.Item.Id, 1, Ct)).ShouldBeNull();
        (await f.Repository.OpenAsync(f.Item.WorkspaceId, Guid.NewGuid(), 1, Ct)).ShouldBeNull();
        (await f.Repository.OpenAsync(f.Item.WorkspaceId, f.Item.Id, 2, Ct)).ShouldBeNull();
        await using var db = f.Fresh();
        var item = await db.ContentItems.SingleAsync(x => x.Id == f.Item.Id, Ct);
        item.IsDeleted = true;
        await db.SaveChangesAsync(Ct);
        (await f.Repository.OpenAsync(f.Item.WorkspaceId, f.Item.Id, 1, Ct)).ShouldBeNull();
    }

    private static async Task AssertSuccess(ContentVersionWriteFixture f, string text)
    {
        await using var db = f.Fresh();
        var version = await db.ContentVersions.AsNoTracking().Include(x => x.FieldValues).SingleAsync(x => x.Id == f.Version.Id, Ct);
        version.Status.ShouldBe(ContentStatus.Approved);
        version.Id.ShouldBe(f.Version.Id);
        version.ContentItemId.ShouldBe(f.Item.Id);
        version.WorkspaceId.ShouldBe(f.Item.WorkspaceId);
        version.VersionNumber.ShouldBe(1);
        version.TemplateVersionId.ShouldBe(f.Version.TemplateVersionId);
        version.CreatedAt.ShouldBe(f.Version.CreatedAt);
        version.Tags.ShouldBe(["retained-tag"]);
        version.Slug.ShouldBe("entry");
        version.LocaleCode.ShouldBeNull();
        version.TranslationGroupId.ShouldBeNull();
        version.PublishedAt.ShouldBeNull();
        version.ArchivedAt.ShouldBeNull();
        version.PublishedByUserId.ShouldBeNull();
        version.RolledBackFromVersionNumber.ShouldBeNull();
        version.CreatedByUserId.ShouldBeNull();
        version.UpdatedByUserId.ShouldBe(f.Actor.UserId);
        version.UpdatedAt.ShouldBe(ContentVersionWriteFixture.SequenceClock.Start.AddSeconds(1));
        version.EffectiveStartAt.ShouldBe(ContentVersionWriteFixture.LoadedTime.AddDays(1));
        version.EffectiveEndAt.ShouldBe(ContentVersionWriteFixture.LoadedTime.AddDays(2));
        version.PublishAt.ShouldBeNull();
        version.PublishLeaseOwner.ShouldBeNull();
        version.PublishLeaseToken.ShouldBeNull();
        version.PublishLeaseExpiresAt.ShouldBeNull();
        var field = version.FieldValues.ShouldHaveSingleItem();
        field.Id.ShouldNotBe(f.Version.FieldValues[0].Id);
        field.FieldId.ShouldBe(f.Field.Id);
        field.ValueKind.ShouldBe(ValueKind.Text);
        field.Order.ShouldBe(0);
        field.TextValue.ShouldBe(text);
        field.BoolValue.ShouldBeNull();
        field.MediaAssetId.ShouldBeNull();
        field.FileAssetId.ShouldBeNull();
        field.ChildContentItemId.ShouldBeNull();
        field.DisplayLabel.ShouldBeNull();
        field.JsonValue!.Value.GetProperty("owned").GetString().ShouldBe(text);
        var item = await db.ContentItems.AsNoTracking().SingleAsync(x => x.Id == f.Item.Id, Ct);
        item.SearchVector.ShouldBe("'entry':1 'saved':2");
        item.UpdatedAt.ShouldBe(ContentVersionWriteFixture.SequenceClock.Start.AddSeconds(2));
        item.Slug.ShouldBe("entry");
        item.UpdatedByUserId.ShouldBeNull();
        var outbox = (await db.WebhookOutboxEvents.AsNoTracking().ToListAsync(Ct)).ShouldHaveSingleItem();
        outbox.EventType.ShouldBe("content.version_updated");
        outbox.WorkspaceId.ShouldBe(f.Item.WorkspaceId);
        outbox.EntityId.ShouldBe(f.Item.Id);
        outbox.OccurredAt.ShouldBe(ContentVersionWriteFixture.SequenceClock.Start.AddSeconds(3));
        outbox.CreatedAt.ShouldBe(outbox.OccurredAt);
        outbox.Payload.EnumerateObject().Count().ShouldBe(6);
        outbox.Payload.GetProperty("contentItemId").GetGuid().ShouldBe(f.Item.Id);
        outbox.Payload.GetProperty("workspaceId").GetGuid().ShouldBe(f.Item.WorkspaceId);
        outbox.Payload.GetProperty("templateVersionId").GetGuid().ShouldBe(f.Item.TemplateVersionId);
        outbox.Payload.GetProperty("contentVersionId").GetGuid().ShouldBe(f.Version.Id);
        outbox.Payload.GetProperty("versionNumber").GetInt32().ShouldBe(1);
        outbox.Payload.GetProperty("status").GetString().ShouldBe("Approved");
        var audit = await db.AuditLogs.AsNoTracking().ToListAsync(Ct);
        audit.Count.ShouldBe(5); // version + item + deleted field + added field + outbox
        audit.ShouldAllBe(x => x.ActorUserId == f.Actor.UserId && x.ActorApiClientId == null);
        audit.ShouldAllBe(x => x.Timestamp >= f.AuditEarliest && x.Timestamp <= DateTimeOffset.UtcNow);
        var versionAudit = audit.Single(x => x.EntityId == version.Id);
        versionAudit.EntityType.ShouldBe(nameof(ContentVersion));
        versionAudit.Action.ShouldBe(AuditAction.Updated);
        versionAudit.WorkspaceId.ShouldBe(f.Item.WorkspaceId);
        var delta = versionAudit.ChangeDelta.ShouldNotBeNull();
        delta.GetProperty(nameof(ContentVersion.UpdatedAt)).GetProperty("before").GetDateTimeOffset().ShouldBe(ContentVersionWriteFixture.LoadedTime);
        delta.GetProperty(nameof(ContentVersion.UpdatedAt)).GetProperty("after").GetDateTimeOffset().ShouldBe(ContentVersionWriteFixture.SequenceClock.Start.AddSeconds(1));
        delta.GetProperty(nameof(ContentVersion.UpdatedByUserId)).GetProperty("after").GetGuid().ShouldBe(f.Actor.UserId!.Value);
        delta.GetProperty(nameof(ContentVersion.PublishLeaseOwner)).GetProperty("before").GetString().ShouldBe("existing-worker");
        delta.GetProperty(nameof(ContentVersion.PublishLeaseOwner)).GetProperty("after").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
        var itemAudit = audit.Single(x => x.EntityId == item.Id);
        itemAudit.EntityType.ShouldBe(nameof(ContentItem));
        itemAudit.Action.ShouldBe(AuditAction.Updated);
        itemAudit.WorkspaceId.ShouldBe(f.Item.WorkspaceId);
        itemAudit.ChangeDelta!.Value.GetProperty(nameof(ContentItem.SearchVector)).GetProperty("before").GetString().ShouldBe("'old':1");
        itemAudit.ChangeDelta.Value.GetProperty(nameof(ContentItem.SearchVector)).GetProperty("after").GetString().ShouldBe("'entry':1 'saved':2");
        audit.Single(x => x.EntityId == f.Version.FieldValues[0].Id).Action.ShouldBe(AuditAction.Deleted);
        audit.Single(x => x.EntityId == field.Id).Action.ShouldBe(AuditAction.Created);
        var outboxAudit = audit.Single(x => x.EntityId == outbox.Id);
        outboxAudit.EntityType.ShouldBe(nameof(WebhookOutboxEvent));
        outboxAudit.Action.ShouldBe(AuditAction.Created);
        outboxAudit.WorkspaceId.ShouldBe(f.Item.WorkspaceId);
        f.Observer.SavingContexts.Count.ShouldBe(1);
    }
}
