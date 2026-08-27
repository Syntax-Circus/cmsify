using System.Text;
using Cmsify.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SyntaxCircus.Storage;

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
        Cmsify.Infrastructure.Storage.StorageServiceCollectionExtensions.AddStorageProvider(services, configuration);

        using var serviceProvider = services.BuildServiceProvider();

        serviceProvider.GetRequiredService<SyntaxCircus.Storage.IStorageProvider>()
            .ShouldBeOfType<SyntaxCircus.Storage.LocalFileStorageProvider>();
        serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<SyntaxCircus.Storage.LocalStorageOptions>>()
            .Value.RootPath.ShouldBe(basePath);
    }

    [Fact]
    public void AddStorageProvider_PrefersLegacyBasePathWhenBothLocalPathsAreConfigured()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "cmsify-storage-base", Guid.NewGuid().ToString("N"));
        var rootPath = Path.Combine(Path.GetTempPath(), "cmsify-storage-root", Guid.NewGuid().ToString("N"));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "local",
            ["Storage:Local:BasePath"] = basePath,
            ["Storage:Local:RootPath"] = rootPath
        }).Build();
        var services = new ServiceCollection();

        Cmsify.Infrastructure.Storage.StorageServiceCollectionExtensions.AddStorageProvider(services, configuration);

        using var serviceProvider = services.BuildServiceProvider();
        serviceProvider.GetRequiredService<IOptions<LocalStorageOptions>>().Value.RootPath.ShouldBe(basePath);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("false", false)]
    public void AddStorageProvider_PreservesServiceUrlPathStyleCompatibility(string? configuredForcePathStyle, bool expected)
    {
        var values = new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "s3",
            ["Storage:S3:BucketName"] = "cmsify",
            ["Storage:S3:ServiceUrl"] = "http://minio:9000",
            ["Storage:S3:AccessKey"] = "test-access",
            ["Storage:S3:SecretKey"] = "test-secret"
        };
        if (configuredForcePathStyle is not null) values["Storage:S3:ForcePathStyle"] = configuredForcePathStyle;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();

        Cmsify.Infrastructure.Storage.StorageServiceCollectionExtensions.AddStorageProvider(services, configuration);

        using var serviceProvider = services.BuildServiceProvider();
        serviceProvider.GetRequiredService<IOptions<S3StorageOptions>>().Value.ForcePathStyle.ShouldBe(expected);
    }

    [Fact]
    public async Task SharedLocalStorageProvider_StoresRetrievesAndDeletesFile()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "cmsify-storage-tests", Guid.NewGuid().ToString("N"));
        var provider = new LocalFileStorageProvider(Options.Create(new LocalStorageOptions { RootPath = basePath }));

        await using var input = new MemoryStream(Encoding.UTF8.GetBytes("hello cmsify"));
        var stored = await provider.StoreAsync(new StoreObjectRequest("default/hello.txt", input, "text/plain"));

        Assert.Equal("default/hello.txt", stored.Key);
        Assert.NotNull(await provider.GetMetadataAsync(stored.Key));

        await using (var output = await provider.ReadAsync(stored.Key))
        using (var reader = new StreamReader(output!.Content, Encoding.UTF8))
        {
            Assert.Equal("hello cmsify", await reader.ReadToEndAsync());
        }

        await provider.DeleteAsync(stored.Key);
        Assert.Null(await provider.GetMetadataAsync(stored.Key));

        if (Directory.Exists(basePath))
        {
            Directory.Delete(basePath, recursive: true);
        }
    }
}
