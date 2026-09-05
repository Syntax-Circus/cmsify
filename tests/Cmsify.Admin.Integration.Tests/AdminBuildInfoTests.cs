using Cmsify.Admin.Services;

namespace Cmsify.Admin.Integration.Tests;

public sealed class AdminBuildInfoTests
{
    [Fact]
    public void Version_IsNeverUnknownInABuiltAssembly()
    {
        AdminBuildInfo.Version.ShouldNotBeNullOrWhiteSpace();
        AdminBuildInfo.Version.ShouldNotBe("unknown");
    }
}
