using Cmsify.Admin.Services;

namespace Cmsify.Admin.State;

public sealed class WorkspaceState
{
    public event Action? Changed;

    public WorkspaceDto? Current { get; private set; }

    public void Set(WorkspaceDto workspace)
    {
        Current = workspace;
        Changed?.Invoke();
    }
}
