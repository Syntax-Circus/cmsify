using Cmsify.Core.Interfaces.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharedStorage = SyntaxCircus.Storage;

namespace Cmsify.Infrastructure.Storage;

public static class StorageServiceCollectionExtensions
{
    public static IServiceCollection AddStorageProvider(this IServiceCollection services, IConfiguration configuration)
    {
        var provider = (configuration["Storage:Provider"] ?? LocalFileSystemStorageProvider.ProviderName).ToLowerInvariant();
        services.Configure<SharedStorage.LocalStorageOptions>(configuration.GetSection(SharedStorage.LocalStorageOptions.SectionName));
        services.PostConfigure<SharedStorage.LocalStorageOptions>(options =>
        {
            if (string.IsNullOrWhiteSpace(options.RootPath))
            {
                options.RootPath = configuration["Storage:Local:BasePath"] ?? Path.Combine(AppContext.BaseDirectory, "storage");
            }
        });
        services.AddOptions<SharedStorage.S3StorageOptions>()
            .Bind(configuration.GetSection(SharedStorage.S3StorageOptions.SectionName))
            .Validate(options => SharedStorage.S3StorageOptions.Validate(options).Succeeded, "Storage:S3 configuration is invalid.");

        switch (provider)
        {
            case LocalFileSystemStorageProvider.ProviderName:
                services.AddSingleton<SharedStorage.IStorageProvider, SharedStorage.LocalFileStorageProvider>();
                break;
            case S3BlobStorageProvider.ProviderName:
                services.AddSingleton<SharedStorage.IStorageProvider, SharedStorage.S3StorageProvider>();
                break;
            default:
                throw new InvalidOperationException($"Unsupported storage provider '{provider}'.");
        }

        services.AddSingleton<IStorageProvider>(serviceProvider => new SharedStorageProviderAdapter(
            serviceProvider.GetRequiredService<SharedStorage.IStorageProvider>(), provider));
        return services;
    }
}
