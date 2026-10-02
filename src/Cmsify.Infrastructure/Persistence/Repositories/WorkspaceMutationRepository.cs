using System.Text.Json;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Interfaces.Repositories;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Core.Workspaces;
using Microsoft.EntityFrameworkCore;
using SyntaxCircus.Common;

namespace Cmsify.Infrastructure.Persistence.Repositories;

public sealed class WorkspaceMutationRepository(
    CmsifyDbContext dbContext, ICurrentActor actor, IWebhookOutbox outbox) : IWorkspaceMutationRepository
{
    private const string UpdatedEventType = "workspace.updated";
    private static readonly ResultError _conflict = new("concurrency-mismatch", "Workspace revision has changed.", ResultErrorKind.Conflict);
    private static readonly ResultError _notFound = new("not-found", "Workspace not found.", ResultErrorKind.NotFound);

    public async Task<Result<WorkspaceDto>> UpdateAsync(UpdateWorkspaceCommand command, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var trackedBefore = dbContext.ChangeTracker.Entries().Select(entry => entry.Entity).ToHashSet(ReferenceEqualityComparer.Instance);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var entity = await dbContext.Workspaces.ScopeWorkspacesToReadableActor(dbContext, actor)
            .FirstOrDefaultAsync(workspace => workspace.Id == command.Id, cancellationToken);
        if (entity is null) return Result<WorkspaceDto>.Failure(_notFound);
        if (entity.UpdatedAt.UtcTicks != expectedRevision) return Result<WorkspaceDto>.Failure(_conflict);
        try
        {
            entity.Name = command.Name;
            entity.Slug = command.Slug;
            entity.Description = command.Description;
            entity.UpdatedAt = WorkspaceRevision.Next(entity.UpdatedAt);
            outbox.Enqueue(UpdatedEventType, command.Id, command.Id,
                JsonSerializer.SerializeToElement(new { workspaceId = command.Id, entity.Name, entity.Slug }), DateTimeOffset.UtcNow);
            // The loaded xmin token participates in this save. Workspace, outbox and audit commit together.
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Result<WorkspaceDto>.Success(entity.ToDto());
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            DiscardMutation(entity, trackedBefore);
            return Result<WorkspaceDto>.Failure(_conflict);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            DiscardMutation(entity, trackedBefore);
            throw;
        }
    }

    public async Task<Result> DeleteAsync(Guid id, long expectedRevision, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        var trackedBefore = dbContext.ChangeTracker.Entries().Select(entry => entry.Entity).ToHashSet(ReferenceEqualityComparer.Instance);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var entity = await dbContext.Workspaces.ScopeWorkspacesToReadableActor(dbContext, actor)
            .FirstOrDefaultAsync(workspace => workspace.Id == id, cancellationToken);
        if (entity is null) return Result.Failure(_notFound);
        if (entity.UpdatedAt.UtcTicks != expectedRevision) return Result.Failure(_conflict);
        var revision = WorkspaceRevision.Next(entity.UpdatedAt);
        entity.SoftDelete(actorUserId);
        entity.UpdatedAt = revision;
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Result.Success();
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            DiscardMutation(entity, trackedBefore);
            return Result.Failure(_conflict);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            DiscardMutation(entity, trackedBefore);
            throw;
        }
    }

    private void DiscardMutation(Workspace workspace, HashSet<object> trackedBefore)
    {
        // Rollback also discards this attempt's tracked writes so reusing the scope cannot save them later.
        // Keep unrelated pre-existing tracked entries rather than clearing the caller's entire context.
        foreach (var entry in dbContext.ChangeTracker.Entries().ToArray())
            if (ReferenceEquals(entry.Entity, workspace) || !trackedBefore.Contains(entry.Entity))
                entry.State = EntityState.Detached;
    }
}

internal static class WorkspaceRevision
{
    // PostgreSQL timestamps retain microseconds. Persist exactly the revision returned to callers.
    internal static DateTimeOffset Normalize(DateTimeOffset value) =>
        new(value.UtcTicks - value.UtcTicks % TimeSpan.TicksPerMicrosecond, TimeSpan.Zero);
    internal static DateTimeOffset Next(DateTimeOffset previous)
    {
        var now = Normalize(DateTimeOffset.UtcNow);
        return now > previous ? now : Normalize(previous).AddTicks(TimeSpan.TicksPerMicrosecond);
    }
}
