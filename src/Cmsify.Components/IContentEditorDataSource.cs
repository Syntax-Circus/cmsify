namespace SyntaxCircus.Cmsify.Components;

/// <summary>Presentation lookups used by recursive inline content editors.</summary>
/// <remarks>
/// Lookups may run concurrently, including sibling children and component graph layers.
/// Embedded implementations must create and dispose a scope/handler per operation; never
/// retain a DbContext for a Blazor circuit or share one across parallel lookups.
/// Missing data is represented by null or an empty list. Unexpected failures and
/// cancellation must propagate rather than appear as successful empty results.
/// </remarks>
public interface IContentEditorDataSource
{
    Task<TemplateResponse?> GetTemplateAsync(Guid workspaceId, Guid templateId, CancellationToken cancellationToken);
    Task<IReadOnlyList<TemplateSummaryResponse>> ListTemplatesAsync(Guid workspaceId, CancellationToken cancellationToken);
    Task<PickListResponse?> GetPickListRevisionAsync(Guid workspaceId, Guid pickListId, Guid revisionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ContentItemSummaryResponse>> ListReferenceOptionsAsync(Guid workspaceId, Guid templateId, CancellationToken cancellationToken);
    Task<ComponentResponse?> GetComponentAsync(Guid workspaceId, Guid componentId, CancellationToken cancellationToken);
}
