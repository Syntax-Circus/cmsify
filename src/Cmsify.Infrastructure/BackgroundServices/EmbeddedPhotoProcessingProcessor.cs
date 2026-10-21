using Cmsify.Core.EmbeddedMedia;
using Cmsify.Infrastructure.Persistence.EmbeddedMedia;
using Cmsify.Infrastructure.Storage;
using Microsoft.Extensions.Options;

namespace Cmsify.Infrastructure.BackgroundServices;

/// <summary>Orchestrates a qualified host adapter; never loads a native decoder in the CMS process.</summary>
public sealed class EmbeddedPhotoProcessingProcessor(EmbeddedPhotoProcessingRepository repository,
    EmbeddedPhotoStorage storage, IEmbeddedPhotoProcessor processor, IEmbeddedPhotoProcessorCapability capability,
    IOptions<EmbeddedPhotoOptions> options, TimeProvider clock) : IEmbeddedPhotoJobProcessor
{
    public async Task<bool> ProcessNextAsync(Guid ownerId, CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled || !options.Value.ProcessingEnabled) return false;
        var lease = await repository.TryClaimJobAsync(ownerId, cancellationToken);
        if (lease is null) return false;
        if (clock.GetUtcNow() >= lease.ExpiresAt) return true;
        if (!await repository.AuthorizeLeaseAsync(lease, cancellationToken)) return true;
        var available = await capability.GetAsync(cancellationToken);
        if (!available.Available || !available.Qualified || available.PolicyId != lease.PolicyId ||
            available.ProcessorVersion != lease.ProcessorVersion) return true;
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Cooperative convenience only. External termination/resource isolation remains the adapter's qualified obligation.
        attempt.CancelAfter(TimeSpan.FromSeconds(EmbeddedPhotoPolicy.DeadlineSeconds));
        try
        {
            var source = new StoredPhotoByteSource(storage.Provider, lease.OriginalKey);
            await VerifyOriginalAsync(source, lease, attempt.Token);
            var result = await processor.ProcessAsync(new(source, lease.SourceSha256, lease.OriginalLength,
                lease.Instructions, new(lease.PolicyId, lease.ProcessorVersion)), attempt.Token);
            if (result.IsFailure)
            {
                var code = EmbeddedPhotoProcessingRepository.SafeFailure(result.Errors.FirstOrDefault()?.Code ?? EmbeddedPhotoErrors.ProcessingFailed);
                await repository.FailJobAsync(lease, new(code is EmbeddedPhotoErrors.Unsupported or EmbeddedPhotoErrors.Corrupt or EmbeddedPhotoErrors.InvalidCandidate,
                    code), cancellationToken);
                return true;
            }
            if (result.Value is null) throw new PhotoByteValidationException(EmbeddedPhotoErrors.InvalidCandidate);
            await using var candidate = result.Value;
            ValidateCandidate(candidate, lease.Instructions);
            if (!await repository.ReserveVariantsAsync(lease, cancellationToken)) return true;
            var stored = new List<StoredPhotoVariant>();
            foreach (var requirement in EmbeddedPhotoPolicy.RequiredVariants)
            {
                var variant = candidate.Variants.Single(v => v.Descriptor.VariantKey == requirement.VariantKey);
                await using var bytes = await variant.Source.OpenReadAsync(attempt.Token);
                var key = EmbeddedPhotoProcessingRepository.VariantKey(lease, requirement.VariantKey);
                var output = await storage.StoreBoundedAsync(key, bytes, variant.Descriptor.Length,
                    EmbeddedPhotoPolicy.WebpContentType, attempt.Token);
                if (!string.Equals(output.Sha256, variant.Descriptor.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new PhotoByteValidationException(EmbeddedPhotoErrors.InvalidCandidate);
                await using var reread = await storage.Provider.ReadAsync(key, attempt.Token);
                if (reread is null) throw new IOException("Stored photo derivative is unavailable.");
                await EmbeddedPhotoWebpContainer.ValidateAsync(reread.Content, variant.Descriptor, candidate.Metadata.HasAlpha, attempt.Token);
                stored.Add(new(variant.Descriptor with { Sha256 = output.Sha256 }, output.Provider, output.StorageKey));
            }
            await repository.CompleteJobAsync(lease, new(candidate.Metadata, stored.ToArray()), cancellationToken);
        }
        catch (PhotoByteValidationException)
        {
            await repository.FailJobAsync(lease, new(true, EmbeddedPhotoErrors.InvalidCandidate), cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await repository.FailJobAsync(lease, new(false, EmbeddedPhotoErrors.Timeout), cancellationToken);
        }
        catch (IOException)
        {
            await repository.FailJobAsync(lease, new(false, EmbeddedPhotoErrors.Unavailable), cancellationToken);
        }
        return true;
    }

    private static async Task VerifyOriginalAsync(IEmbeddedPhotoByteSource source, PhotoJobLease lease, CancellationToken ct)
    {
        await using var original = await source.OpenReadAsync(ct);
        if (!EmbeddedPhotoPolicy.ValidOriginalLimit(lease.OriginalLength))
            throw new PhotoByteValidationException(EmbeddedPhotoErrors.TooLarge);
        using var bounded = new PhotoBoundedReadStream(original, lease.OriginalLength);
        await bounded.CopyToAsync(Stream.Null, ct);
        if (!bounded.EndObserved || bounded.BytesRead != lease.OriginalLength ||
            !string.Equals(bounded.CompleteHash(), lease.SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new PhotoByteValidationException(EmbeddedPhotoErrors.Corrupt);
    }
    private static void ValidateCandidate(EmbeddedPhotoCandidate candidate, EmbeddedPhotoInstructions instructions)
    {
        var source = candidate.Metadata;
        if (source is null || source.Format is not ("JPEG" or "PNG" or "WebP" or "HEIC") || source.BitDepth != 8 ||
            string.IsNullOrWhiteSpace(source.ColorDescription) || source.ColorDescription.Length > 256 ||
            !EmbeddedPhotoPolicy.ValidateDimensions(source.Width, source.Height) ||
            candidate.Variants.Count != EmbeddedPhotoPolicy.RequiredVariants.Count)
            throw new PhotoByteValidationException(EmbeddedPhotoErrors.InvalidCandidate);
        var dimensions = instructions.QuarterTurnsClockwise % 2 == 0 ? (source.Width, source.Height) : (source.Height, source.Width);
        foreach (var requirement in EmbeddedPhotoPolicy.RequiredVariants)
        {
            var variants = candidate.Variants.Where(v => v is not null && v.Descriptor?.VariantKey == requirement.VariantKey).ToArray();
            if (variants.Length != 1) throw new PhotoByteValidationException(EmbeddedPhotoErrors.InvalidCandidate);
            var variant = variants[0];
            var descriptor = variant.Descriptor;
            var fit = EmbeddedPhotoPolicy.Fit(dimensions.Item1, dimensions.Item2, requirement.Bound);
            if (variant.Source is null || descriptor.ContentType != EmbeddedPhotoPolicy.WebpContentType ||
                descriptor.Width != fit.Width || descriptor.Height != fit.Height || descriptor.Length <= 0 ||
                descriptor.Length > EmbeddedPhotoPolicy.MaxBytes || !EmbeddedPhotoPolicy.IsSha256(descriptor.Sha256))
                throw new PhotoByteValidationException(EmbeddedPhotoErrors.InvalidCandidate);
        }
    }
}
