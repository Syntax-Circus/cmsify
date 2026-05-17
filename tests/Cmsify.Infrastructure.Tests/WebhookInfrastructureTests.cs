using System.Security.Cryptography;
using System.Text;
using Cmsify.Infrastructure.BackgroundServices;
using Cmsify.Infrastructure.Security;
using Microsoft.Extensions.Configuration;

namespace Cmsify.Infrastructure.Tests;

public sealed class WebhookInfrastructureTests
{
    [Fact]
    public void WebhookSigner_ReturnsExpectedHmacSha256Signature()
    {
        var payload = Encoding.UTF8.GetBytes("{\"event\":\"content.published\"}");

        var signature = WebhookSigner.Sign("secret", payload);

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes("secret"));
        var expected = $"sha256={Convert.ToHexString(hmac.ComputeHash(payload)).ToLowerInvariant()}";
        Assert.Equal(expected, signature);
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    public void WebhookBackoffCalculator_UsesExponentialBackoff(int attempt, int expectedSeconds)
    {
        var delay = WebhookBackoffCalculator.CalculateDelay(attempt, TimeSpan.FromSeconds(30), TimeSpan.FromHours(24));

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
    }

    [Fact]
    public void WebhookBackoffCalculator_CapsAtMaximumDelay()
    {
        var delay = WebhookBackoffCalculator.CalculateDelay(20, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5));

        Assert.Equal(TimeSpan.FromMinutes(5), delay);
    }

    [Fact]
    public void AesSecretProtector_RoundTripsAndDoesNotStorePlaintext()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Secrets:EncryptionKey"] = "unit-test-secret-key-with-enough-length"
            })
            .Build();
        var protector = new AesSecretProtector(configuration);

        var encrypted = protector.Protect("webhook-secret");

        Assert.NotEqual("webhook-secret", encrypted);
        Assert.Equal("webhook-secret", protector.Unprotect(encrypted));
    }
}
