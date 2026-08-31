using Microsoft.Extensions.Options;

namespace Cmsify.Infrastructure.BackgroundServices;

public sealed class MediaOperationalOptions
{
    public const string SectionName = "Media:Operations";
    public int ReconciliationIntervalSeconds { get; set; } = 300;
    public int LeaseDurationSeconds { get; set; } = 300;
    public int BatchSize { get; set; } = 100;
    public int RetryBaseSeconds { get; set; } = 30;
    public int RetryCapSeconds { get; set; } = 3_600;
    public int RetentionDays { get; set; } = 30;
    public int OrphanGraceHours { get; set; } = 24;
    public int AbandonedUploadMinutes { get; set; } = 30;
    public string[] ManagedPrefixes { get; set; } = ["cmsify/media/", "default/"];
}

public sealed class MediaOperationalOptionsValidator : IValidateOptions<MediaOperationalOptions>
{
    private static readonly HashSet<string> AllowedPrefixes = ["cmsify/media/", "default/"];

    public ValidateOptionsResult Validate(string? name, MediaOperationalOptions options)
    {
        var failures = new List<string>();
        ValidateRange(options.ReconciliationIntervalSeconds, 1, 86_400, nameof(options.ReconciliationIntervalSeconds), failures);
        ValidateRange(options.LeaseDurationSeconds, 1, 3_600, nameof(options.LeaseDurationSeconds), failures);
        ValidateRange(options.BatchSize, 1, 1_000, nameof(options.BatchSize), failures);
        ValidateRange(options.RetryBaseSeconds, 1, 3_600, nameof(options.RetryBaseSeconds), failures);
        ValidateRange(options.RetryCapSeconds, options.RetryBaseSeconds, 86_400, nameof(options.RetryCapSeconds), failures);
        ValidateRange(options.RetentionDays, 1, 3_650, nameof(options.RetentionDays), failures);
        ValidateRange(options.OrphanGraceHours, 1, 720, nameof(options.OrphanGraceHours), failures);
        ValidateRange(options.AbandonedUploadMinutes, 1, 10_080, nameof(options.AbandonedUploadMinutes), failures);
        if (options.ManagedPrefixes is null || options.ManagedPrefixes.Length == 0 ||
            options.ManagedPrefixes.Any(prefix => !AllowedPrefixes.Contains(prefix)))
        {
            failures.Add($"{nameof(options.ManagedPrefixes)} may contain only Cmsify-managed prefixes.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateRange(int value, int minimum, int maximum, string property, ICollection<string> failures)
    {
        if (value < minimum || value > maximum)
        {
            failures.Add($"{property} must be between {minimum} and {maximum}.");
        }
    }
}

public static class MediaRetryBackoff
{
    public static TimeSpan Calculate(int attemptCount, TimeSpan baseDelay, TimeSpan cap)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attemptCount, 1);
        var exponent = Math.Min(attemptCount - 1, 30);
        var ticks = Math.Min(baseDelay.Ticks * Math.Pow(2, exponent), cap.Ticks);
        return TimeSpan.FromTicks((long)ticks);
    }
}
