using System.Runtime.ExceptionServices;
using Cmsify.Admin.Auth;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;

namespace Cmsify.Admin.Components;

/// <summary>
/// Catches API 401s that escape a page and redirects to the session-expired endpoint instead of crashing the
/// circuit. Any other exception (or a 401 where no redirect is possible) is rethrown, preserving prior behaviour.
/// </summary>
public sealed class SessionExpiryBoundary : ErrorBoundaryBase
{
    [Inject] private SessionExpiryHandler SessionExpiry { get; set; } = default!;

    protected override Task OnErrorAsync(Exception exception)
    {
        SessionExpiry.TryHandleException(exception);
        return Task.CompletedTask;
    }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        if (CurrentException is null)
        {
            builder.AddContent(0, ChildContent);
        }
        else if (!SessionExpiryHandler.IsUnauthorized(CurrentException) || !SessionExpiry.HasFired)
        {
            ExceptionDispatchInfo.Capture(CurrentException).Throw();
        }
        // else: the session-expired redirect is under way; render nothing.
    }
}
