using Cmsify.Core.EmbeddedMedia;

namespace Cmsify.Infrastructure.BackgroundServices;

public sealed class EmbeddedPhotoOptions : IEmbeddedPhotoUploadLimits
{
    public long MaximumOriginalBytes { get; set; } = EmbeddedPhotoPolicy.MaxBytes;
    public static readonly TimeSpan MaxUploadLease = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaxProcessingLease = TimeSpan.FromMinutes(2);
    public bool Enabled { get; set; }
    public bool UploadOnlyEnabled { get; set; }
    public bool ProcessingEnabled { get; set; }
    public bool PngNormalizationEnabled { get; set; }
    public bool JpegSdrBaseEnabled { get; set; }
    internal bool AllowsPolicy(string policyId) =>
        policyId == EmbeddedPhotoPolicy.PngNormalizationPolicyId ? PngNormalizationEnabled
            : policyId == EmbeddedPhotoPolicy.JpegSdrBasePolicyId ? JpegSdrBaseEnabled : policyId == PolicyId;
    public string PolicyId { get; set; } = "phone-sdr-webp-v1";
    public TimeSpan UploadLeaseDuration { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan UploadDeadline { get; set; } = TimeSpan.FromSeconds(90);
    public TimeSpan ProcessingLeaseDuration { get; set; } = TimeSpan.FromSeconds(45);
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);
}
