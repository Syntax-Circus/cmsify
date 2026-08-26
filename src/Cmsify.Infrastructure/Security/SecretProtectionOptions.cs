using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Cmsify.Infrastructure.Security;

public sealed class SecretProtectionOptions
{
    public const string SectionName = "Secrets";

    public string ActiveKeyId { get; set; } = string.Empty;

    public Dictionary<string, string> EncryptionKeys { get; set; } = new(StringComparer.Ordinal);

    public string? EncryptionKey { get; set; }

    public SecretRotationOptions Rotation { get; set; } = new();
}

public sealed class SecretRotationOptions
{
    public bool Enabled { get; set; }

    public int BatchSize { get; set; } = 100;

    public int DelaySeconds { get; set; } = 5;
}

public sealed partial class SecretProtectionOptionsValidator : IValidateOptions<SecretProtectionOptions>
{
    private const double MinimumEntropyBitsPerByte = 3.5;
    private const string DevelopmentKey = "Y21zaWZ5LWRldmVsb3BtZW50LWtleS0zMi1ieXRlcyE=";
    private readonly string environmentName;

    public SecretProtectionOptionsValidator(IHostEnvironment environment)
        : this(environment.EnvironmentName)
    {
    }

    public SecretProtectionOptionsValidator(string environmentName)
    {
        this.environmentName = environmentName;
    }

    public ValidateOptionsResult Validate(string? name, SecretProtectionOptions options)
    {
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.ActiveKeyId) || !KeyIdPattern().IsMatch(options.ActiveKeyId))
        {
            failures.Add("Secrets active key ID must contain 1 to 64 letters, digits, underscores, or hyphens.");
        }
        else if (!options.EncryptionKeys.TryGetValue(options.ActiveKeyId, out var activeKey))
        {
            failures.Add("Secrets active key ID must name a configured encryption key.");
        }

        foreach (var entry in options.EncryptionKeys)
        {
            if (!KeyIdPattern().IsMatch(entry.Key))
            {
                failures.Add("Secrets encryption key IDs must contain 1 to 64 letters, digits, underscores, or hyphens.");
                continue;
            }

            if (!TryDecodeCanonicalKey(entry.Value, out var key))
            {
                failures.Add("Secrets encryption keys must be canonical Base64 values encoding exactly 32 bytes.");
                continue;
            }

            if (string.Equals(environmentName, Environments.Production, StringComparison.OrdinalIgnoreCase))
            {
                ValidateProductionKey(key, entry.Value, failures);
            }
        }

        ValidateRange(options.Rotation.BatchSize, 1, 500, "Secrets rotation batch size", failures);
        ValidateRange(options.Rotation.DelaySeconds, 1, 3_600, "Secrets rotation delay seconds", failures);
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    internal static bool TryDecodeCanonicalKey(string? value, out byte[] key)
    {
        key = [];
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            var decoded = Convert.FromBase64String(value);
            if (decoded.Length != 32 || !string.Equals(Convert.ToBase64String(decoded), value, StringComparison.Ordinal))
            {
                return false;
            }

            key = decoded;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void ValidateProductionKey(byte[] key, string configuredValue, ICollection<string> failures)
    {
        if (string.Equals(configuredValue, DevelopmentKey, StringComparison.Ordinal))
        {
            failures.Add("Secrets encryption keys must not use checked-in development material in production.");
        }

        if (key.Distinct().Count() < 16)
        {
            failures.Add("Secrets encryption keys must contain at least 16 distinct byte values in production.");
        }

        if (HasRepeatedEightByteBlock(key))
        {
            failures.Add("Secrets encryption keys must not repeat fixed-size blocks in production.");
        }

        if (CalculateEntropy(key) < MinimumEntropyBitsPerByte)
        {
            failures.Add("Secrets encryption keys must meet the minimum byte-distribution entropy in production.");
        }
    }

    private static bool HasRepeatedEightByteBlock(byte[] key) =>
        key.Chunk(8).Select(Convert.ToHexString).Distinct(StringComparer.Ordinal).Count() != key.Length / 8;

    private static double CalculateEntropy(byte[] key)
    {
        var length = key.Length;
        return key.GroupBy(value => value)
            .Select(group => group.Count() / (double)length)
            .Sum(probability => -probability * Math.Log2(probability));
    }

    private static void ValidateRange(int value, int minimum, int maximum, string name, ICollection<string> failures)
    {
        if (value < minimum || value > maximum)
        {
            failures.Add($"{name} must be between {minimum} and {maximum}.");
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyIdPattern();
}
