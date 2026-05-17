namespace Cmsify.Infrastructure.BackgroundServices;

public static class WebhookBackoffCalculator
{
    public static TimeSpan CalculateDelay(int attemptCount, TimeSpan? baseDelay = null, TimeSpan? maxDelay = null)
    {
        var baseDelayValue = baseDelay ?? TimeSpan.FromSeconds(30);
        var maxDelayValue = maxDelay ?? TimeSpan.FromHours(24);
        var exponent = Math.Max(0, attemptCount - 1);
        var multiplier = Math.Pow(2, Math.Min(exponent, 16));
        var delay = TimeSpan.FromTicks((long)Math.Min(baseDelayValue.Ticks * multiplier, maxDelayValue.Ticks));
        return delay > maxDelayValue ? maxDelayValue : delay;
    }
}
