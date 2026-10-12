namespace Cmsify.Infrastructure.BackgroundServices;

/// <summary>Host-local bounded traversal state, shared across worker scopes; restart safely wraps.</summary>
public sealed class EmbeddedPhotoQueueCursor
{
    private readonly object _gate = new();
    private (DateTimeOffset Due, Guid Id)? _position;
    private (DateTimeOffset Due, Guid Id)? _recoveryPosition;
    internal (DateTimeOffset Due, Guid Id)? Read(bool recovery = false)
    { lock (_gate) return recovery ? _recoveryPosition : _position; }
    internal void Advance(DateTimeOffset due, Guid id, bool recovery = false)
    { lock (_gate) { if (recovery) _recoveryPosition = (due, id); else _position = (due, id); } }
    internal void Reset(bool recovery = false)
    { lock (_gate) { if (recovery) _recoveryPosition = null; else _position = null; } }
}
