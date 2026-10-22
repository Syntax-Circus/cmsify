using Cmsify.Core.EmbeddedMedia;
using Shouldly;

namespace Cmsify.Core.Tests;

public sealed class EmbeddedPngPolicyTests
{
    [Fact]
    public void PngProfileHasReadPreviewWithoutChangingLegacyRequirements()
    {
        EmbeddedPhotoPolicy.VariantsFor("png-sdr-webp-v1").Select(x => x.VariantKey)
            .ShouldBe(new[] { "320", "640", "1280", "4096" });
        EmbeddedPhotoPolicy.RequiredVariants.Select(x => x.VariantKey).ShouldBe(new[] { "320", "640", "1280" });
        EmbeddedPhotoPolicy.ValidateDimensions(6063, 4639).ShouldBeFalse();
        EmbeddedPhotoPolicy.ValidateDimensions(6063, 4639, "png-sdr-webp-v1").ShouldBeTrue();
        EmbeddedPhotoPolicy.ValidateDimensions(6000, 5001, "png-sdr-webp-v1").ShouldBeFalse();
        EmbeddedPhotoPolicy.ValidateDimensions(8193, 1, "png-sdr-webp-v1").ShouldBeFalse();
    }
}
