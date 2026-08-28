namespace Cmsify.Admin.Integration.Tests;

public sealed class BlazorStaticAssetTests : IAsyncLifetime
{
    private readonly AdminAuthTestFactory factory = new();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await factory.DisposeAsync();
    }

    [Fact]
    public async Task Root_ReferencesTheStaticAssetBootScript()
    {
        using var client = factory.CreateClient();
        var markup = await client.GetStringAsync("/", TestContext.Current.CancellationToken);

        markup.ShouldContain("blazor.web", Case.Insensitive);
    }
}
