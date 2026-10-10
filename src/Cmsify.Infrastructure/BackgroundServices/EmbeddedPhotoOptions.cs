namespace Cmsify.Infrastructure.BackgroundServices;

public sealed class EmbeddedPhotoOptions
{
    public bool Enabled { get; set; }
    public bool ProcessingEnabled { get; set; }
    public string PolicyId { get; set; } = "phone-sdr-webp-v1";
    public TimeSpan UploadLeaseDuration { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan UploadDeadline { get; set; } = TimeSpan.FromSeconds(90);
    public TimeSpan ProcessingLeaseDuration { get; set; } = TimeSpan.FromSeconds(45);
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);
}
