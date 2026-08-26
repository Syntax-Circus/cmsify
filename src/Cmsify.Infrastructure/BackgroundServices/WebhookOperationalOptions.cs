using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;

namespace Cmsify.Infrastructure.BackgroundServices;

public sealed class WebhookOperationalOptions
{
    public const string SectionName = "Webhook";
    public int OutboxPollIntervalSeconds { get; set; } = 30;
    public int OutboxLeaseDurationSeconds { get; set; } = 300;
    public int OutboxBatchSize { get; set; } = 100;
    public int RetryIntervalSeconds { get; set; } = 30;
    public int DeliveryLeaseDurationSeconds { get; set; } = 300;
    public int DeliveryBatchSize { get; set; } = 100;
    public int MaxAttempts { get; set; } = 10;
    public int RequestTimeoutSeconds { get; set; } = 15;
    public bool AllowHttp { get; set; }
    public int RetentionDays { get; set; } = 30;
    public int CleanupBatchSize { get; set; } = 100;
    public int CleanupIntervalSeconds { get; set; } = 3_600;
}

public sealed class SchedulerOperationalOptions
{
    public const string SectionName = "Scheduler";
    public int PublishingIntervalSeconds { get; set; } = 60;
    public int PublishingLeaseDurationSeconds { get; set; } = 300;
    public int PublishingBatchSize { get; set; } = 100;
}

public sealed class WebhookOperationalOptionsValidator : IValidateOptions<WebhookOperationalOptions>
{
    public ValidateOptionsResult Validate(string? name, WebhookOperationalOptions options)
    {
        var failures = new List<string>();
        Validate(options.OutboxPollIntervalSeconds, 1, 3_600, nameof(options.OutboxPollIntervalSeconds), failures);
        Validate(options.OutboxLeaseDurationSeconds, 1, 1_800, nameof(options.OutboxLeaseDurationSeconds), failures);
        Validate(options.OutboxBatchSize, 1, 500, nameof(options.OutboxBatchSize), failures);
        Validate(options.RetryIntervalSeconds, 1, 3_600, nameof(options.RetryIntervalSeconds), failures);
        Validate(options.DeliveryLeaseDurationSeconds, 1, 1_800, nameof(options.DeliveryLeaseDurationSeconds), failures);
        Validate(options.DeliveryBatchSize, 1, 500, nameof(options.DeliveryBatchSize), failures);
        Validate(options.MaxAttempts, 1, 100, nameof(options.MaxAttempts), failures);
        Validate(options.RequestTimeoutSeconds, 1, 120, nameof(options.RequestTimeoutSeconds), failures);
        Validate(options.RetentionDays, 1, 3_650, nameof(options.RetentionDays), failures);
        Validate(options.CleanupBatchSize, 1, 500, nameof(options.CleanupBatchSize), failures);
        Validate(options.CleanupIntervalSeconds, 1, 86_400, nameof(options.CleanupIntervalSeconds), failures);
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void Validate(int value, int minimum, int maximum, string name, ICollection<string> failures)
    {
        if (value < minimum || value > maximum)
        {
            failures.Add($"{name} must be between {minimum} and {maximum}.");
        }
    }
}

public sealed class SchedulerOperationalOptionsValidator : IValidateOptions<SchedulerOperationalOptions>
{
    public ValidateOptionsResult Validate(string? name, SchedulerOperationalOptions options)
    {
        var failures = new List<string>();
        Validate(options.PublishingIntervalSeconds, 1, 3_600, nameof(options.PublishingIntervalSeconds), failures);
        Validate(options.PublishingLeaseDurationSeconds, 1, 1_800, nameof(options.PublishingLeaseDurationSeconds), failures);
        Validate(options.PublishingBatchSize, 1, 500, nameof(options.PublishingBatchSize), failures);
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void Validate(int value, int minimum, int maximum, string name, ICollection<string> failures)
    {
        if (value < minimum || value > maximum)
        {
            failures.Add($"{name} must be between {minimum} and {maximum}.");
        }
    }
}

public static class OperationalOptions
{
    public static WebhookOperationalOptions ReadWebhook(IConfiguration configuration)
    {
        var options = configuration.GetSection(WebhookOperationalOptions.SectionName).Get<WebhookOperationalOptions>() ?? new WebhookOperationalOptions();
        EnsureValid(new WebhookOperationalOptionsValidator().Validate(null, options));
        return options;
    }

    public static SchedulerOperationalOptions ReadScheduler(IConfiguration configuration)
    {
        var options = configuration.GetSection(SchedulerOperationalOptions.SectionName).Get<SchedulerOperationalOptions>() ?? new SchedulerOperationalOptions();
        EnsureValid(new SchedulerOperationalOptionsValidator().Validate(null, options));
        return options;
    }

    private static void EnsureValid(ValidateOptionsResult result)
    {
        if (result.Failed)
        {
            throw new ArgumentOutOfRangeException("configuration", string.Join(" ", result.Failures));
        }
    }
}
