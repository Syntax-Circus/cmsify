using Cmsify.Admin.Services;

namespace Cmsify.Admin.Integration.Tests;

public sealed class AutoSlugStateTests
{
    [Fact]
    public void UpdateFromName_GeneratesAndTracksDefaultSlug()
    {
        var slug = new AutoSlugState();

        slug.UpdateFromName("Fart Muffin");
        slug.Value.ShouldBe("fart-muffin");

        slug.UpdateFromName("Chocolate Muffin");
        slug.Value.ShouldBe("chocolate-muffin");
    }

    [Fact]
    public void UpdateFromName_PreservesManualSlug()
    {
        var slug = new AutoSlugState();
        slug.UpdateFromName("Fart Muffin");
        slug.SetManually("custom-slug");

        slug.UpdateFromName("Chocolate Muffin");

        slug.Value.ShouldBe("custom-slug");
    }

    [Fact]
    public void ExistingSlug_IsNotChangedWhenNameChanges()
    {
        var slug = new AutoSlugState("published-slug", isManual: true);

        slug.UpdateFromName("Renamed resource");

        slug.Value.ShouldBe("published-slug");
    }

    [Fact]
    public void ClearingManualSlug_ReenablesDefaultGeneration()
    {
        var slug = new AutoSlugState("custom-slug", isManual: true);
        slug.SetManually(string.Empty);

        slug.EnsureDefault("Fart Muffin");

        slug.Value.ShouldBe("fart-muffin");
    }
}
