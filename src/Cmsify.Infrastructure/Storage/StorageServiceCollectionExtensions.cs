using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharedStorage = SyntaxCircus.Storage;

namespace Cmsify.Infrastructure.Storage;

public static class StorageServiceCollectionExtensions
{
    public static IServiceCollection AddStorageProvider(this IServiceCollection services, IConfiguration configuration)
    {
        var provider = (configuration["Storage:Provider"] ?? "local").ToLowerInvariant();
        services.Configure<SharedStorage.LocalStorageOptions>(configuration.GetSection(SharedStorage.LocalStorageOptions.SectionName));
        services.PostConfigure<SharedStorage.LocalStorageOptions>(options =>
        {
            var legacyBasePath = configuration["Storage:Local:BasePath"];
            if (!string.IsNullOrWhiteSpace(legacyBasePath))
            {
                options.RootPath = legacyBasePath;
            }
            else if (string.IsNullOrWhiteSpace(options.RootPath))
            {
                options.RootPath = Path.Combine(AppContext.BaseDirectory, "storage");
            }
        });
        services.AddOptions<SharedStorage.S3StorageOptions>()
            .Bind(configuration.GetSection(SharedStorage.S3StorageOptions.SectionName))
            .Validate(options => SharedStorage.S3StorageOptions.Validate(options).Succeeded, "Storage:S3 configuration is invalid.");
        services.PostConfigure<SharedStorage.S3StorageOptions>(options =>
        {
            if (!string.IsNullOrWhiteSpace(options.ServiceUrl) &&
                string.IsNullOrWhiteSpace(configuration["Storage:S3:ForcePathStyle"]))
            {
                options.ForcePathStyle = true;
            }
        });

        switch (provider)
        {
            case "local":
                services.AddSingleton<SharedStorage.IStorageProvider, SharedStorage.LocalFileStorageProvider>();
                break;
            case "s3":
                services.AddSingleton<SharedStorage.IStorageProvider, SharedStorage.S3StorageProvider>();
                break;
            default:
                throw new InvalidOperationException($"Unsupported storage provider '{provider}'.");
        }
        return services;
    }
}
