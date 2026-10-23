using Cmsify.Core.EmbeddedMedia;
using Shouldly;

namespace Cmsify.Core.Tests;

public sealed class EmbeddedJpegSdrPolicyTests
{
    private const string Policy="jpeg-sdr-base-webp-v1";
    private const string Conversion="apple-jpeg-sdr-base-q8-srgb-strip-v1";
    [Fact]
    public void ExplicitJpegSdrProofDoesNotChangeLegacyOrPngContracts()
    {
        var metadata=new EmbeddedPhotoSourceMetadata("JPEG",640,470,8,"sRGB",false,true){Conversion=new(Conversion,8)};
        EmbeddedPhotoPolicy.ValidMetadata(metadata,Policy).ShouldBeTrue();
        EmbeddedPhotoPolicy.ValidMetadata(metadata,"phone-sdr-webp-v1").ShouldBeFalse();
        EmbeddedPhotoPolicy.ValidMetadata(metadata,EmbeddedPhotoPolicy.PngNormalizationPolicyId).ShouldBeFalse();
        EmbeddedPhotoPolicy.VariantsFor(Policy).Select(x=>x.VariantKey).ShouldBe(new[]{"320","640","1280"});
    }
    [Theory]
    [InlineData("used-sdr")] [InlineData("version")] [InlineData("format")]
    [InlineData("depth")] [InlineData("colour")] [InlineData("alpha")]
    public void WrongConversionProofIsRejected(string change)
    {
        var metadata=new EmbeddedPhotoSourceMetadata("JPEG",640,470,8,"sRGB",false,true){Conversion=new(Conversion,8)};
        metadata=change switch{
            "used-sdr"=>metadata with{UsedSdrBase=false},"version"=>metadata with{Conversion=new("unknown",8)},
            "format"=>metadata with{Format="PNG"},"depth"=>metadata with{Conversion=new(Conversion,16)},
            "colour"=>metadata with{ColorDescription="DisplayP3"},"alpha"=>metadata with{HasAlpha=true},_=>metadata};
        EmbeddedPhotoPolicy.ValidMetadata(metadata,Policy).ShouldBeFalse();
    }
}
