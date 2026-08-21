namespace Cmsify.Admin.Integration.Tests;

public sealed class BlazorStaticAssetTests : IAsyncLifetime
{
    private readonly AdminAuthTestFactory factory = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await factory.DisposeAsync();
    }

    [Fact]
    public async Task Root_ReferencesTheStaticAssetBootScript()
    {
        using var client = factory.CreateClient();
        var markup = await client.GetStringAsync("/");

        markup.ShouldContain("blazor.web", Case.Insensitive);
    }
}
