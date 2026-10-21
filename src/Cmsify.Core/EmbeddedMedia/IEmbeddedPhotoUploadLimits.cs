namespace Cmsify.Core.EmbeddedMedia;

/// <summary>Host-selected intake bound; existing consumers default to 20 MiB.</summary>
public interface IEmbeddedPhotoUploadLimits
{
    long MaximumOriginalBytes { get; }
}

public sealed record EmbeddedPhotoUploadLimits(long MaximumOriginalBytes = EmbeddedPhotoPolicy.MaxBytes) : IEmbeddedPhotoUploadLimits;
