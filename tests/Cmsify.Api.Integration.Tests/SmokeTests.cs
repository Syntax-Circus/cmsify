namespace Cmsify.Api.Integration.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void TestAssembly_Loads()
    {
        Assert.NotNull(typeof(Program).Assembly);
    }
}
