using Cmsify.Core.Interfaces.Services;
using Microsoft.Extensions.Configuration;

namespace Cmsify.Infrastructure.Storage;

public sealed class LocalFileSystemStorageProvider : IStorageProvider
{
    public const string ProviderName = "local";

    private readonly string basePath;

    public LocalFileSystemStorageProvider(IConfiguration configuration)
    {
        basePath = configuration["Storage:Local:BasePath"]
            ?? configuration["Storage:Local:RootPath"]
            ?? Path.Combine(AppContext.BaseDirectory, "storage");
    }

    public async Task<StoredFile> StoreAsync(Stream content, string fileName, string mimeType, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var storageKey = StorageKeyBuilder.Build(fileName);
        var fullPath = GetFullPath(storageKey);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? basePath);

        await using var output = File.Create(fullPath);
        await content.CopyToAsync(output, ct);
        return new StoredFile(storageKey, ProviderName, output.Length);
    }

    public Task<Stream> RetrieveAsync(string storageKey, CancellationToken ct = default)
    {
        var fullPath = GetFullPath(storageKey);
        Stream stream = File.OpenRead(fullPath);
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string storageKey, CancellationToken ct = default)
    {
        var fullPath = GetFullPath(storageKey);
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }

        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string storageKey, CancellationToken ct = default) =>
        Task.FromResult(File.Exists(GetFullPath(storageKey)));

    private string GetFullPath(string storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey))
        {
            throw new ArgumentException("Storage key is required.", nameof(storageKey));
        }

        var normalizedKey = storageKey.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(basePath, normalizedKey));
        var fullBasePath = Path.GetFullPath(basePath);

        if (!fullPath.StartsWith(fullBasePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Storage key resolves outside of the configured storage base path.");
        }

        return fullPath;
    }
}
