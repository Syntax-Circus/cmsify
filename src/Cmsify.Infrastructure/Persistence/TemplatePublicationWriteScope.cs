namespace Cmsify.Infrastructure.Persistence;

/// <summary>
/// Database write scope for the legacy API publication/import paths. A failed
/// operation rolls back its own writes, including saves preceding archival.
/// Caller transactions retain commit ownership and any work before the savepoint.
/// </summary>
public sealed class TemplatePublicationWriteScope : IAsyncDisposable
{
    private readonly LegacyWriteScope _scope;

    private TemplatePublicationWriteScope(LegacyWriteScope scope) => _scope = scope;

    public static async Task<TemplatePublicationWriteScope> BeginAsync(CmsifyDbContext context, CancellationToken cancellationToken) =>
        new(await LegacyWriteScope.BeginAsync(context, "cmsify_template",
            "Template publication/import requires savepoints in a caller-owned transaction.", cancellationToken));

    public Task CompleteAsync(CancellationToken cancellationToken) => _scope.CompleteAsync(cancellationToken);

    public ValueTask DisposeAsync() => _scope.DisposeAsync();
}
