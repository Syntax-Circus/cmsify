using System.Net;
using System.Net.Http.Json;
using Cmsify.Admin.Services;
using Cmsify.Admin.State;
using Microsoft.JSInterop;
using SyntaxCircus.Cmsify;

namespace Cmsify.Admin.Integration.Tests;

public sealed class WorkspaceStateTests
{
    [Fact]
    public async Task InitializeAsync_WhenApiReturns401_ThrowsAndRetriesOnNextCall()
    {
        var workspaceId = Guid.NewGuid();
        var handler = new ScriptedHandler();
        var state = new WorkspaceState(new BrowserStorage(new NullJsRuntime()), new CmsifyClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://api.test") },
            new CmsifyClientOptions { EnableRetries = false }));
        var userId = Guid.NewGuid();

        handler.Response = () => new HttpResponseMessage(HttpStatusCode.Unauthorized);
        await Should.ThrowAsync<CmsifyApiException>(() => state.InitializeAsync(userId, TestContext.Current.CancellationToken));

        handler.Response = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                items = new[] { new { id = workspaceId, name = "Only", slug = "only" } },
                totalCount = 1,
                page = 1,
                pageSize = 20
            })
        };
        await state.InitializeAsync(userId, TestContext.Current.CancellationToken);

        handler.RequestCount.ShouldBe(2);
        state.Available.Count.ShouldBe(1);
        state.Current!.Id.ShouldBe(workspaceId);
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public Func<HttpResponseMessage> Response { get; set; } = () => new HttpResponseMessage(HttpStatusCode.NotFound);
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(Response());
        }
    }

    private sealed class NullJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult(default(TValue)!);
    }
}
