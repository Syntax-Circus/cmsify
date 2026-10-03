namespace Cmsify.Infrastructure.Persistence;

/// <summary>
/// Atomic scope for legacy content identity and translation mutations. Start before
/// mutable reads; caller transactions retain ownership and failed operations restore
/// their tracked checkpoint without discarding pending caller intent.
/// </summary>
public sealed class ContentIdentityWriteScope : IAsyncDisposable
{
    private readonly LegacyWriteScope _scope;

    private ContentIdentityWriteScope(LegacyWriteScope scope) => _scope = scope;

    public static async Task<ContentIdentityWriteScope> BeginAsync(CmsifyDbContext context, CancellationToken cancellationToken) =>
        new(await LegacyWriteScope.BeginAsync(context, "cmsify_content_identity",
            "Content identity mutations require savepoints in a caller-owned transaction.", cancellationToken));

    public Task CompleteAsync(CancellationToken cancellationToken) => _scope.CompleteAsync(cancellationToken);

    public ValueTask DisposeAsync() => _scope.DisposeAsync();
}
