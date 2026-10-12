using Cmsify.Core.EmbeddedMedia;
using Shouldly;

namespace Cmsify.Core.Tests;

public sealed class EmbeddedPhotoContractTests
{
    [Fact]
    public void LimitsAndInstructionContractArePinned()
    {
        EmbeddedPhotoPolicy.MaxBytes.ShouldBe(20L * 1024 * 1024);
        EmbeddedPhotoPolicy.MaxEdge.ShouldBe(8192);
        EmbeddedPhotoPolicy.MaxPixels.ShouldBe(24_000_000L);
        EmbeddedPhotoPolicy.RequiredVariants.Select(x => x.VariantKey).ShouldBe(new[] { "320", "640", "1280" });
        EmbeddedPhotoPolicy.RequiredVariants.Select(x => x.Quality).ShouldBe(new[] { 80, 80, 80 });
        EmbeddedPhotoPolicy.ValidateInstructions(new(0)).ShouldBeTrue();
        EmbeddedPhotoPolicy.ValidateInstructions(new(3)).ShouldBeTrue();
        EmbeddedPhotoPolicy.ValidateInstructions(new(4)).ShouldBeFalse();
        EmbeddedPhotoPolicy.ValidateInstructions(new(-1)).ShouldBeFalse();
    }

    [Fact]
    public void ResponsiveBoundsNeverEnlargeAndUseContain()
    {
        EmbeddedPhotoPolicy.Fit(400, 300, 1280).ShouldBe((400, 300));
        EmbeddedPhotoPolicy.Fit(4000, 3000, 320).ShouldBe((320, 240));
        EmbeddedPhotoPolicy.Fit(3000, 4000, 640).ShouldBe((480, 640));
        EmbeddedPhotoPolicy.ValidateDimensions(8193, 1).ShouldBeFalse();
        EmbeddedPhotoPolicy.ValidateDimensions(6000, 4000).ShouldBeTrue();
        EmbeddedPhotoPolicy.ValidateDimensions(6001, 4000).ShouldBeFalse();
        EmbeddedPhotoPolicy.ValidateDimensions(0, 4000).ShouldBeFalse();
    }

    [Fact]
    public void ManifestOwnsItsDetachedVariantCollection()
    {
        var variants = new List<EmbeddedPhotoVariantDescriptor> { new("320", "image/webp", 320, 240, 99, new('a', 64)) };
        var manifest = new EmbeddedPhotoManifest(Guid.NewGuid(), new(Guid.NewGuid(), 1, new('b', 64)),
            new('c', 64), "test-policy", "test-processor", new(0), variants);
        variants.Clear();
        manifest.Variants.Count.ShouldBe(1);
        manifest.Variants[0].VariantKey.ShouldBe("320");
        Should.Throw<NotSupportedException>(() => ((IList<EmbeddedPhotoVariantDescriptor>)manifest.Variants).Clear());
    }

    [Fact]
    public void BlobAvailabilityAndImageReadinessRemainSeparate()
    {
        Enum.GetNames<EmbeddedPhotoState>().ShouldBe(new[] { "Queued", "Processing", "Ready", "Rejected", "Failed", "AwaitingProcessing" });
        Enum.GetValues<EmbeddedPhotoState>().Select(x => (int)x).ShouldBe(new[] { 0, 1, 2, 3, 4, 5 });
        typeof(EmbeddedPhotoState).ShouldNotBe(typeof(Cmsify.Core.Domain.Enums.MediaBlobState));
        typeof(EmbeddedPhotoPolicy).Assembly.GetReferencedAssemblies()
            .Any(x => x.Name!.Contains("Magick", StringComparison.OrdinalIgnoreCase)
                || x.Name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
                || x.Name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)).ShouldBeFalse();
    }
}
