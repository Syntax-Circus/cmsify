using Cmsify.Core.Interfaces.Services;
using SharedStorage = SyntaxCircus.Storage;

namespace Cmsify.Infrastructure.Storage;

internal sealed class SharedStorageProviderAdapter(SharedStorage.IStorageProvider storage, string providerName) : IStorageProvider
{
    public async Task<StoredFile> StoreAsync(Stream content, string fileName, string mimeType, CancellationToken ct = default)
    {
        var key = StorageKeyBuilder.Build(fileName);
        var stored = await storage.StoreAsync(new SharedStorage.StoreObjectRequest(key, content, mimeType), ct);
        return new StoredFile(stored.Key, providerName, stored.SizeBytes);
    }

    public async Task<Stream> RetrieveAsync(string storageKey, CancellationToken ct = default)
    {
        var result = await storage.ReadAsync(storageKey, ct);
        return result?.Content ?? throw new FileNotFoundException("Stored media was not found.");
    }

    public Task DeleteAsync(string storageKey, CancellationToken ct = default) => storage.DeleteAsync(storageKey, ct);

    public async Task<bool> ExistsAsync(string storageKey, CancellationToken ct = default) =>
        await storage.GetMetadataAsync(storageKey, ct) is not null;
}
