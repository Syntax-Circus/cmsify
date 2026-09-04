using Cmsify.Admin.Services;
using Cmsify.Admin.State;
using Microsoft.JSInterop;
using SyntaxCircus.Cmsify;

namespace Cmsify.Admin.Integration.Tests;

public sealed class WorkspaceStateTests
{
    [Fact]
    public async Task UpsertAvailable_MakesNewWorkspaceSelectableByRouteGuard()
    {
        var state = new WorkspaceState(
            new BrowserStorage(new NoOpJsRuntime()),
            new CmsifyClient(
                new HttpClient(new UnusedApiHandler()) { BaseAddress = new Uri("http://api.test") },
                new CmsifyClientOptions { EnableRetries = false }));
        var workspace = new WorkspaceDto(
            Guid.NewGuid(),
            "New workspace",
            "new-workspace",
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            CanWrite: true);

        state.UpsertAvailable(workspace);

        (await state.SelectAvailableAsync(workspace.Id)).ShouldBeTrue();
        state.Available.ShouldContain(workspace);
        state.Current.ShouldBe(workspace);
    }

    private sealed class UnusedApiHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This test does not call the API.");
    }

    private sealed class NoOpJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult(default(TValue)!);
    }
}
