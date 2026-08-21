using Cmsify.Admin.Services;

namespace Cmsify.Admin.State;

public sealed class WorkspaceState
{
    private const string StorageArea = "local";
    private const string StorageKeyPrefix = "cmsify.workspace.current.";
    private readonly BrowserStorage storage;
    private readonly WorkspaceApiClient workspaces;
    private Guid initializedForUserId;

    public WorkspaceState(BrowserStorage storage, WorkspaceApiClient workspaces)
    {
        this.storage = storage;
        this.workspaces = workspaces;
    }

    public event Action? Changed;

    public WorkspaceDto? Current { get; private set; }

    public IReadOnlyList<WorkspaceDto> Available { get; private set; } = [];

    public async Task InitializeAsync(Guid userId, CancellationToken ct = default)
    {
        if (userId == Guid.Empty || initializedForUserId == userId)
        {
            return;
        }

        initializedForUserId = userId;
        Current = null;

        var page = await workspaces.ListAsync(ct);
        Available = page.Items;

        var savedWorkspaceId = await storage.GetAsync<Guid?>(StorageArea, StorageKey(userId));
        var selected = savedWorkspaceId.HasValue
            ? Available.FirstOrDefault(workspace => workspace.Id == savedWorkspaceId.Value)
            : null;

        if (selected is null && savedWorkspaceId.HasValue)
        {
            await storage.RemoveAsync(StorageArea, StorageKey(userId));
        }

        selected ??= Available.Count == 1 ? Available[0] : null;
        if (selected is not null)
        {
            await SelectAsync(selected);
            return;
        }

        Changed?.Invoke();
    }

    public async Task<bool> SelectAvailableAsync(Guid workspaceId)
    {
        var workspace = Available.FirstOrDefault(candidate => candidate.Id == workspaceId);
        if (workspace is null)
        {
            return false;
        }

        await SelectAsync(workspace);
        return true;
    }

    public async Task SelectAsync(WorkspaceDto workspace)
    {
        Current = workspace;
        if (initializedForUserId != Guid.Empty)
        {
            await storage.SetAsync(StorageArea, StorageKey(initializedForUserId), workspace.Id);
        }

        Changed?.Invoke();
    }

    public async Task ClearAsync()
    {
        Current = null;
        if (initializedForUserId != Guid.Empty)
        {
            await storage.RemoveAsync(StorageArea, StorageKey(initializedForUserId));
        }

        Changed?.Invoke();
    }

    private static string StorageKey(Guid userId) => $"{StorageKeyPrefix}{userId:N}";
}
