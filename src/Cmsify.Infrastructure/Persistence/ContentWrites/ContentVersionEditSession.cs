using System.Text.Json;
using Cmsify.Core.ContentWrites;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Interfaces.Services;
using Microsoft.EntityFrameworkCore;
using SyntaxCircus.Common;

namespace Cmsify.Infrastructure.Persistence.ContentWrites;

/// <summary>Owns one tracked edit. Rejection or failure makes the operation unusable for saving.</summary>
internal sealed class ContentVersionEditSession : IContentVersionEditSession
{
    private const string UpdatedEventType = "content.version_updated";
    private readonly CmsifyDbContext _context;
    private readonly ContentVersion _version;
    private readonly ContentVersionFieldWriter _fieldWriter;
    private readonly ContentVersionDetailProjector _projector;
    private readonly IContentSearchVectorBuilder _searchVectorBuilder;
    private readonly TimeProvider _clock;
    private readonly EfWebhookOutbox _outbox;
    private TemplateVersion? _templateVersion;
    private Guid? _actorUserId;
    private SessionState _state = SessionState.Loaded;

    internal ContentVersionEditSession(CmsifyDbContext context, ContentVersion version, IContentValidator validator,
        IContentSearchVectorBuilder searchVectorBuilder, TimeProvider clock)
    {
        _context = context;
        _version = version;
        _fieldWriter = new(context, validator);
        _projector = new(context);
        _outbox = new(context);
        _searchVectorBuilder = searchVectorBuilder;
        _clock = clock;
        Snapshot = new(version.Id, version.ContentItemId, version.WorkspaceId, version.VersionNumber,
            version.Status, version.UpdatedAt) { TemplateVersionId = version.TemplateVersionId };
    }

    public ContentVersionEditSnapshot Snapshot { get; }

    public async Task<Result> PrepareAsync(ContentVersionEditValues values, Guid? actorUserId,
        CancellationToken cancellationToken)
    {
        RequireState(SessionState.Loaded);
        // Any exception, cancellation or validation rejection is terminal, even after tracked mutation.
        _state = SessionState.Failed;
        cancellationToken.ThrowIfCancellationRequested();
        _templateVersion = await _context.TemplateVersions
            .Include(version => version.Fields).ThenInclude(field => field.AllowedTypes)
            .FirstOrDefaultAsync(version => version.Id == _version.TemplateVersionId && !version.IsDeleted, cancellationToken);
        if (_templateVersion is null)
            return ValidationFailure("Template version is unavailable.");
        _version.EffectiveStartAt = values.EffectiveStartAt;
        _version.EffectiveEndAt = values.EffectiveEndAt;
        if (await _fieldWriter.ApplyAsync(_version, _templateVersion, values.Fields, cancellationToken) is { } error)
            return ValidationFailure(error);
        cancellationToken.ThrowIfCancellationRequested();
        _actorUserId = actorUserId;
        _state = SessionState.Prepared;
        return Result.Success();
    }

    public async Task<Result> CommitAsync(CancellationToken cancellationToken)
    {
        RequireState(SessionState.Prepared);
        _state = SessionState.Failed;
        cancellationToken.ThrowIfCancellationRequested();
        _version.PublishAt = null;
        _version.PublishLeaseOwner = null;
        _version.PublishLeaseToken = null;
        _version.PublishLeaseExpiresAt = null;
        _version.UpdatedAt = _clock.GetUtcNow();
        _version.UpdatedByUserId = _actorUserId;
        var item = await _context.ContentItems.FirstAsync(item => item.Id == _version.ContentItemId, cancellationToken);
        item.SearchVector = _searchVectorBuilder.Build(_version, _templateVersion!);
        item.UpdatedAt = _clock.GetUtcNow();
        var payload = JsonSerializer.SerializeToElement(new
        {
            contentItemId = item.Id,
            workspaceId = item.WorkspaceId,
            templateVersionId = item.TemplateVersionId,
            contentVersionId = (Guid?)_version.Id,
            versionNumber = (int?)_version.VersionNumber,
            status = _version.Status.ToString()
        });
        _outbox.Enqueue(UpdatedEventType, item.WorkspaceId, item.Id, payload, _clock.GetUtcNow());
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure(new(ContentVersionWriteErrors.ConcurrencyMismatch, "Concurrency mismatch", ResultErrorKind.Conflict));
        }
        _state = SessionState.Committed;
        return Result.Success();
    }

    public Task<ContentVersionDetailOutput> ReadDetailAsync(DateTimeOffset asOf, bool expandChildren,
        CancellationToken cancellationToken)
    {
        RequireState(SessionState.Committed);
        cancellationToken.ThrowIfCancellationRequested();
        return _projector.ProjectAsync(_version, asOf, expandChildren, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_state == SessionState.Disposed) return;
        _state = SessionState.Disposed;
        await _context.DisposeAsync();
    }

    private void RequireState(SessionState expected)
    {
        ObjectDisposedException.ThrowIf(_state == SessionState.Disposed, this);
        if (_state != expected)
            throw new InvalidOperationException($"The content-version edit must be {expected} but is {_state}.");
    }

    private static Result ValidationFailure(string message) => Result.Failure(new(
        ContentVersionWriteErrors.ContentValidationFailed, message, ResultErrorKind.Validation));
    private enum SessionState { Loaded, Prepared, Committed, Failed, Disposed }
}
