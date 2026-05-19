using Cmsify.Core.Interfaces.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cmsify.Infrastructure.Storage;

public static class StorageServiceCollectionExtensions
{
    public static IServiceCollection AddStorageProvider(this IServiceCollection services, IConfiguration configuration)
    {
        var provider = configuration["Storage:Provider"] ?? LocalFileSystemStorageProvider.ProviderName;

        return provider.ToLowerInvariant() switch
        {
            LocalFileSystemStorageProvider.ProviderName => services.AddSingleton<IStorageProvider, LocalFileSystemStorageProvider>(),
            S3BlobStorageProvider.ProviderName => services.AddSingleton<IStorageProvider, S3BlobStorageProvider>(),
            _ => throw new InvalidOperationException($"Unsupported storage provider '{provider}'.")
        };
    }
}
