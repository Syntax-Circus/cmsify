namespace Cmsify.Admin.Integration.Tests;

public sealed class ReconnectModalRenderingTests : IAsyncLifetime
{
    private readonly AdminAuthTestFactory factory = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await factory.DisposeAsync();
    }

    [Fact]
    public async Task Root_RendersThePackageReconnectModalWithCmsifyContent()
    {
        var client = factory.CreateClient();

        var markup = await client.GetStringAsync("/");

        markup.ShouldContain("id=\"components-reconnect-modal\"");
        markup.ShouldContain("class=\"cms-reconnect-modal\"");
        markup.ShouldContain("data-reconnect-action=\"retry\"");
        markup.ShouldContain("We couldn't reconnect. Retry now, or reload the page.");
    }
}
