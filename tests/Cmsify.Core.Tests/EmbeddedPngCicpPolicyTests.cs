using Cmsify.Core.EmbeddedMedia;
using Shouldly;

namespace Cmsify.Core.Tests;

public sealed class EmbeddedPngCicpPolicyTests
{
    private const string Policy = "png-cicp-sdr-webp-v1";
    private const string Conversion = "png-cicp-p3-q8-srgb-strip-v1";
    [Fact]
    public void ExplicitP3ProofHasItsOwnRecipeAndFourBounds()
    {
        var source = new EmbeddedPhotoSourceMetadata("PNG",6063,4639,8,"sRGB",false,false) { Conversion = new(Conversion,16) };
        EmbeddedPhotoPolicy.ValidMetadata(source,Policy).ShouldBeTrue();
        EmbeddedPhotoPolicy.VariantsFor(Policy).Select(x => x.VariantKey).ShouldBe(new[] {"320","640","1280","4096"});
        EmbeddedPhotoPolicy.ValidMetadata(source,EmbeddedPhotoPolicy.PngNormalizationPolicyId).ShouldBeFalse();
        EmbeddedPhotoPolicy.ValidMetadata(source,"phone-sdr-webp-v1").ShouldBeFalse();
        EmbeddedPhotoPolicy.VariantsFor("unknown").Count.ShouldBe(3);
    }
    [Theory]
    [InlineData("version")]
    [InlineData("format")]
    [InlineData("depth")]
    [InlineData("selected")]
    [InlineData("sdr-base")]
    [InlineData("colour")]
    public void MismatchedProofIsNotAReadyP3Candidate(string change)
    {
        var source = new EmbeddedPhotoSourceMetadata("PNG",640,480,8,"sRGB",true,false) { Conversion = new(Conversion,8) };
        source = change switch
        {
            "version" => source with { Conversion = new(EmbeddedPhotoPolicy.PngNormalizationVersion,8) },
            "format" => source with { Format = "JPEG" },
            "depth" => source with { Conversion = new(Conversion,32) },
            "selected" => source with { BitDepth = 16 },
            "sdr-base" => source with { UsedSdrBase = true },
            "colour" => source with { ColorDescription = "DisplayP3" },
            _ => source
        };
        EmbeddedPhotoPolicy.ValidMetadata(source,Policy).ShouldBeFalse();
    }
}
