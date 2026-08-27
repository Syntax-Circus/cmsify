using System.Text;
using Cmsify.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Cmsify.Core.Interfaces.Services;

namespace Cmsify.Infrastructure.Tests;

public sealed class StorageProviderTests
{
    [Fact]
    public void AddStorageProvider_UsesSharedProviderAndMapsLegacyBasePath()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "cmsify-storage-registration", Guid.NewGuid().ToString("N"));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "LOCAL",
            ["Storage:Local:BasePath"] = basePath
        }).Build();
        var services = new ServiceCollection();
        services.AddStorageProvider(configuration);

        using var serviceProvider = services.BuildServiceProvider();

        serviceProvider.GetRequiredService<SyntaxCircus.Storage.IStorageProvider>()
            .ShouldBeOfType<SyntaxCircus.Storage.LocalFileStorageProvider>();
        serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<SyntaxCircus.Storage.LocalStorageOptions>>()
            .Value.RootPath.ShouldBe(basePath);
        serviceProvider.GetRequiredService<IStorageProvider>().ShouldNotBeNull();
    }

    [Fact]
    public async Task LocalFileSystemStorageProvider_StoresRetrievesAndDeletesFile()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "cmsify-storage-tests", Guid.NewGuid().ToString("N"));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:Local:BasePath"] = basePath
            })
            .Build();
        var provider = new LocalFileSystemStorageProvider(configuration);

        await using var input = new MemoryStream(Encoding.UTF8.GetBytes("hello cmsify"));
        var stored = await provider.StoreAsync(input, "hello.txt", "text/plain");

        Assert.Equal(LocalFileSystemStorageProvider.ProviderName, stored.Provider);
        Assert.True(await provider.ExistsAsync(stored.StorageKey));

        await using (var output = await provider.RetrieveAsync(stored.StorageKey))
        using (var reader = new StreamReader(output, Encoding.UTF8))
        {
            Assert.Equal("hello cmsify", await reader.ReadToEndAsync());
        }

        await provider.DeleteAsync(stored.StorageKey);
        Assert.False(await provider.ExistsAsync(stored.StorageKey));

        if (Directory.Exists(basePath))
        {
            Directory.Delete(basePath, recursive: true);
        }
    }
}
