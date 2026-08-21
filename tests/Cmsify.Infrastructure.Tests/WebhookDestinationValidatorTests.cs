using Cmsify.Infrastructure.BackgroundServices;
using Microsoft.Extensions.Configuration;

namespace Cmsify.Infrastructure.Tests;

public sealed class WebhookDestinationValidatorTests
{
    [Theory]
    [InlineData("http://8.8.8.8/hooks")]
    [InlineData("https://127.0.0.1/hooks")]
    [InlineData("https://10.0.0.1/hooks")]
    [InlineData("https://169.254.1.1/hooks")]
    [InlineData("https://[::1]/hooks")]
    [InlineData("https://user:password@8.8.8.8/hooks")]
    public async Task ValidateAsync_RejectsUnsafeDestinations(string url)
    {
        var result = await CreateValidator().ValidateAsync(url);

        Assert.False(result.IsValid);
        Assert.Null(result.NormalizedUrl);
    }

    [Fact]
    public async Task ValidateAsync_AcceptsPublicHttpsIpAddress()
    {
        var result = await CreateValidator().ValidateAsync("https://8.8.8.8/hooks");

        Assert.True(result.IsValid);
        Assert.Equal("https://8.8.8.8/hooks", result.NormalizedUrl);
    }

    private static WebhookDestinationValidator CreateValidator() =>
        new(new ConfigurationBuilder().AddInMemoryCollection().Build());
}
