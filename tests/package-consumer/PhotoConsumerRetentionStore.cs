using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cmsify.Core.EmbeddedMedia;

// Disposable host-owned intent journal. Contains no CMS storage keys or CMS-table access.
internal sealed class PhotoConsumerRetentionStore(string root) : IEmbeddedMediaRetentionGuard
{
    internal const string Namespace = "qualification-photos";
    internal sealed record Intent(Guid Workspace, Guid Reference, string State, int Revision, EmbeddedPhotoManifestIdentity[] Manifests);
    private string PathFor(Guid workspace, Guid reference) =>
        Path.Combine(root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(workspace + ":" + reference))) + ".json");
    internal void Reserve(Guid workspace, Guid reference, EmbeddedPhotoManifestIdentity[] manifests)
    {
        using var fence = Lock(workspace, reference);
        if (Read(workspace, reference) is not null) throw new InvalidOperationException("Reference intent already exists.");
        Write(new(workspace, reference, "pending", 1, manifests.ToArray()));
    }
    internal void Commit(Guid workspace, Guid reference)
    {
        using var fence = Lock(workspace, reference);
        var intent = Read(workspace, reference) ?? throw new InvalidOperationException("Reserve first.");
        if (intent.State != "pending") throw new InvalidOperationException("Only pending intent can commit.");
        Write(intent with { State = "committed" });
    }
    internal void Retire(Guid workspace, Guid reference)
    {
        using var fence = Lock(workspace, reference);
        var intent = Read(workspace, reference) ?? throw new InvalidOperationException("Unknown is retained.");
        Write(intent with { State = "retired" });
    }
    internal Intent? Read(Guid workspace, Guid reference)
    {
        var path = PathFor(workspace, reference);
        return File.Exists(path) ? JsonSerializer.Deserialize<Intent>(File.ReadAllText(path)) : null;
    }
    private FileStream Lock(Guid workspace, Guid reference)
    {
        Directory.CreateDirectory(root);
        return new FileStream(PathFor(workspace, reference) + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    private void Write(Intent intent)
    {
        Directory.CreateDirectory(root); var path = PathFor(intent.Workspace, intent.Reference);
        var staged = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var file = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(file, intent); file.Flush(flushToDisk: true); }
        File.Move(staged, path, overwrite: true);
    }
    public Task<EmbeddedMediaRetentionDecision> CheckAsync(EmbeddedMediaRetentionRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var intent = request.ConsumerNamespace == Namespace ? Read(request.WorkspaceId, request.ReferenceKey) : null;
            return Task.FromResult(intent is null || intent.State == "pending" ||
                intent.Revision != request.ProtectionRevision || !intent.Manifests.SequenceEqual(request.Manifests)
                ? EmbeddedMediaRetentionDecision.Unknown
                : intent.State == "retired" ? EmbeddedMediaRetentionDecision.ReleaseAllowed : EmbeddedMediaRetentionDecision.Retain);
        }
        catch (IOException) { return Task.FromResult(EmbeddedMediaRetentionDecision.Unknown); }
        catch (JsonException) { return Task.FromResult(EmbeddedMediaRetentionDecision.Unknown); }
    }
}
