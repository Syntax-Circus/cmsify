using System.Security.Cryptography;
using System.Text;
using Cmsify.Infrastructure.BackgroundServices;
using Cmsify.Infrastructure.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

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
    public void SecretProtectionOptionsValidator_RejectsMissingOrInvalidActiveKeyConfiguration()
    {
        var validator = new SecretProtectionOptionsValidator(Environments.Development);
        var missingId = CreateOptions();
        missingId.ActiveKeyId = string.Empty;
        var invalidId = CreateOptions();
        invalidId.ActiveKeyId = "invalid.id";
        var missingEntry = CreateOptions();
        missingEntry.ActiveKeyId = "missing";

        Assert.False(validator.Validate(null, missingId).Succeeded);
        Assert.False(validator.Validate(null, invalidId).Succeeded);
        Assert.False(validator.Validate(null, missingEntry).Succeeded);
    }

    [Theory]
    [InlineData("not-base64")]
    [InlineData("AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA")]
    [InlineData("AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA==")]
    public void SecretProtectionOptionsValidator_RejectsMalformedOrNonCanonicalKeyMaterial(string key)
    {
        var options = CreateOptions(key);

        var result = new SecretProtectionOptionsValidator(Environments.Development).Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(key, string.Join(' ', result.Failures ?? []));
    }

    [Theory]
    [InlineData(31)]
    [InlineData(33)]
    public void SecretProtectionOptionsValidator_RejectsKeysWithWrongLength(int length)
    {
        var options = CreateOptions(Convert.ToBase64String(Enumerable.Range(0, length).Select(value => (byte)value).ToArray()));

        Assert.False(new SecretProtectionOptionsValidator(Environments.Development).Validate(null, options).Succeeded);
    }

    [Fact]
    public void SecretProtectionOptionsValidator_RejectsKnownDevelopmentKeyInProduction()
    {
        var developmentKey = Convert.ToBase64String(Encoding.UTF8.GetBytes("cmsify-development-key-32-bytes!"));

        var result = new SecretProtectionOptionsValidator(Environments.Production).Validate(null, CreateOptions(developmentKey));

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(developmentKey, string.Join(' ', result.Failures ?? []));
    }

    [Theory]
    [MemberData(nameof(WeakProductionKeys))]
    public void SecretProtectionOptionsValidator_RejectsWeakProductionKeyMaterial(byte[] key)
    {
        var options = CreateOptions(Convert.ToBase64String(key));

        Assert.False(new SecretProtectionOptionsValidator(Environments.Production).Validate(null, options).Succeeded);
    }

    [Fact]
    public void SecretProtectionOptionsValidator_AcceptsGeneratedProductionKeyAndValidRotationBounds()
    {
        var options = CreateOptions(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        options.Rotation.BatchSize = 500;
        options.Rotation.DelaySeconds = 3_600;

        Assert.True(new SecretProtectionOptionsValidator(Environments.Production).Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(501, 5)]
    [InlineData(100, 0)]
    [InlineData(100, 3_601)]
    public void SecretProtectionOptionsValidator_RejectsRotationValuesOutsideTheirBounds(int batchSize, int delaySeconds)
    {
        var options = CreateOptions();
        options.Rotation.BatchSize = batchSize;
        options.Rotation.DelaySeconds = delaySeconds;

        Assert.False(new SecretProtectionOptionsValidator(Environments.Development).Validate(null, options).Succeeded);
    }

    [Fact]
    public void AesSecretProtector_ProtectsWithActiveV2KeyAndRoundTrips()
    {
        var protector = CreateProtector(activeKeyId: "key_2026_08");

        var encrypted = protector.Protect("webhook-secret");

        Assert.NotEqual("webhook-secret", encrypted);
        var parts = encrypted.Split('.');
        Assert.Equal(5, parts.Length);
        Assert.Equal("v2", parts[0]);
        Assert.Equal("key_2026_08", parts[1]);
        Assert.Equal("webhook-secret", protector.Unprotect(encrypted));
    }

    [Fact]
    public void AesSecretProtector_UsesRandomizedV2Ciphertext()
    {
        var protector = CreateProtector();

        Assert.NotEqual(protector.Protect("webhook-secret"), protector.Protect("webhook-secret"));
    }

    [Fact]
    public void AesSecretProtector_ReadsRetainedV2KeyAfterActiveKeyChanges()
    {
        var oldProtector = CreateProtector(activeKeyId: "key_old");
        var encrypted = oldProtector.Protect("webhook-secret");
        var currentProtector = CreateProtector(activeKeyId: "key_current");

        Assert.Equal("webhook-secret", currentProtector.Unprotect(encrypted));
    }

    [Fact]
    public void AesSecretProtector_AuthenticatesTheV2KeyIdentifier()
    {
        var options = CreateOptions();
        options.EncryptionKeys["key_other"] = options.EncryptionKeys[options.ActiveKeyId];
        var protector = new AesSecretProtector(Options.Create(options));
        var parts = protector.Protect("webhook-secret").Split('.');
        parts[1] = "key_other";

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(string.Join('.', parts)));
    }

    [Fact]
    public void AesSecretProtector_RejectsTamperedUnknownVersionAndUnknownKeyV2Payloads()
    {
        var protector = CreateProtector();
        var parts = protector.Protect("webhook-secret").Split('.');
        var tampered = parts.ToArray();
        tampered[4] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var unknownKey = parts.ToArray();
        unknownKey[1] = "removed_key";
        var unknownVersion = parts.ToArray();
        unknownVersion[0] = "v3";

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(string.Join('.', tampered)));
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(string.Join('.', unknownKey)));
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(string.Join('.', unknownVersion)));
    }

    [Theory]
    [InlineData("AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=", "v1.AQIDBAUGBwgJCgsM.fM0nPxnrBKOlY+UlJs2fNw==.2UvO9V8ESMAc2JtXho4=")]
    [InlineData("legacy secret that is intentionally not base64", "v1.AQIDBAUGBwgJCgsM.jRteJ717LPcfABl4gnoKcg==.6h8oehvsdiQ+iTBcxZU=")]
    public void AesSecretProtector_ReadsFixedV1FixturesWithBothHistoricalKeyDerivationBranches(string legacyKey, string fixture)
    {
        const string secret = "webhook-secret";
        var options = CreateOptions();
        options.EncryptionKey = legacyKey;

        Assert.Equal(secret, new AesSecretProtector(Options.Create(options)).Unprotect(fixture));
    }

    [Fact]
    public void AesSecretProtector_RejectsMalformedV2Segments()
    {
        var protector = CreateProtector();
        var parts = protector.Protect("webhook-secret").Split('.');
        var nonCanonicalNonce = parts.ToArray();
        nonCanonicalNonce[2] = "AQIDBAUGBwgJCgs";
        var shortTag = parts.ToArray();
        shortTag[3] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(15));
        var emptyCiphertext = parts.ToArray();
        emptyCiphertext[4] = string.Empty;

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(string.Join('.', nonCanonicalNonce)));
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(string.Join('.', shortTag)));
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(string.Join('.', emptyCiphertext)));
    }

    public static IEnumerable<object[]> WeakProductionKeys()
    {
        yield return [Enumerable.Range(0, 15).Select(value => (byte)value).Concat(Enumerable.Repeat((byte)0, 17)).ToArray()];
        yield return [Enumerable.Range(0, 8).Select(value => (byte)value).Concat(Enumerable.Range(0, 8).Select(value => (byte)value)).Concat(Enumerable.Range(8, 8).Select(value => (byte)value)).Concat(Enumerable.Range(8, 8).Select(value => (byte)value)).ToArray()];
        yield return [Enumerable.Range(0, 16).Select(value => (byte)value).Concat(Enumerable.Repeat((byte)0, 15)).Append((byte)1).ToArray()];
    }

    private static SecretProtectionOptions CreateOptions(string? key = null)
    {
        var options = new SecretProtectionOptions { ActiveKeyId = "key_2026_08" };
        options.EncryptionKeys[options.ActiveKeyId] = key ?? "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=";
        options.EncryptionKeys["key_old"] = "ISIjJCUmJygpKissLS4vMDEyMzQ1Njc4OTo7PD0+P0A=";
        options.EncryptionKeys["key_current"] = "QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVpbXF1eX2A=";
        return options;
    }

    private static AesSecretProtector CreateProtector(string activeKeyId = "key_2026_08")
    {
        var options = CreateOptions();
        options.ActiveKeyId = activeKeyId;
        return new AesSecretProtector(Options.Create(options));
    }

}
