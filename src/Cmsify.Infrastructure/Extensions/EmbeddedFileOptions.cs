using Cmsify.Core.EmbeddedFiles;

namespace Cmsify.Infrastructure.Extensions;

public sealed class EmbeddedFileOptions
{
    public bool Enabled { get; set; }
    public string PolicyId { get; set; }=EmbeddedFileRules.PolicyId;
    public TimeSpan UploadDeadline { get; set; }=TimeSpan.FromMinutes(2);
    public TimeSpan UploadLeaseDuration { get; set; }=TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaxUploadLease=TimeSpan.FromMinutes(30);
}
