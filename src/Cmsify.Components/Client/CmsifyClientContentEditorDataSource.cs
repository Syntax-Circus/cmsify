namespace SyntaxCircus.Cmsify.Components.Client;

/// <summary>Preserves the SDK lookup behavior for existing component consumers.</summary>
public sealed class CmsifyClientContentEditorDataSource(CmsifyClient client) : IContentEditorDataSource
{
    private const int CandidatePageSize = 100;
    private readonly CmsifyClient _client = client;

    public Task<TemplateResponse?> GetTemplateAsync(Guid workspaceId, Guid templateId, CancellationToken cancellationToken) =>
        _client.Templates.GetAsync(workspaceId, templateId, cancellationToken);

    public async Task<IReadOnlyList<TemplateSummaryResponse>> ListTemplatesAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        var page = await _client.Templates.ListAsync(workspaceId, isSystem: null, search: null,
            page: 1, pageSize: CandidatePageSize, ct: cancellationToken);
        return page?.Items ?? [];
    }

    public async Task<PickListResponse?> GetPickListRevisionAsync(Guid workspaceId, Guid pickListId, Guid revisionId, CancellationToken cancellationToken)
    {
        try
        {
            return await _client.PickLists.GetRevisionAsync(workspaceId, pickListId, revisionId, cancellationToken);
        }
        catch (CmsifyApiException)
        {
            // A deleted/inaccessible revision remains an unresolved binding, as before.
            return null;
        }
    }

    public async Task<IReadOnlyList<ContentItemSummaryResponse>> ListReferenceOptionsAsync(Guid workspaceId, Guid templateId, CancellationToken cancellationToken)
    {
        var page = await _client.Content.ListAsync(workspaceId, null, templateId, null, null, null, cancellationToken);
        return page?.Items ?? [];
    }

    public async Task<ComponentResponse?> GetComponentAsync(Guid workspaceId, Guid componentId, CancellationToken cancellationToken)
    {
        try
        {
            return await _client.Components.GetAsync(workspaceId, componentId, cancellationToken);
        }
        catch (CmsifyApiException)
        {
            // One unresolved schema must not abort its independent siblings.
            return null;
        }
    }
}
