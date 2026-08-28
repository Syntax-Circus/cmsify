using SyntaxCircus.Http.Resilience;

namespace SyntaxCircus.Cmsify;

public sealed class CmsifyClientOptions
{
    public Uri? BaseUrl { get; set; }

    public string? ApiToken { get; set; }

    public Func<CancellationToken, ValueTask<string?>>? TokenProvider { get; set; }

    /// <summary>
    /// Invoked for each HTTP response before it is retried, deserialized, or converted to an exception.
    /// This is intended for consumers that need response headers, such as renewed-session metadata.
    /// </summary>
    public Func<HttpResponseMessage, CancellationToken, Task>? ResponseObserver { get; set; }

    public bool EnableRetries { get; set; } = true;

    public int MaxRetryAttempts { get; set; } = 3;

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(100);

    public double CircuitFailureRatio { get; set; } = 0.5;

    public int CircuitMinimumThroughput { get; set; } = 5;

    public TimeSpan CircuitSamplingDuration { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan CircuitBreakDuration { get; set; } = TimeSpan.FromSeconds(30);

    public Func<HttpRetryTelemetry, CancellationToken, ValueTask>? OnRetry { get; set; }

    public Func<HttpCircuitTelemetry, CancellationToken, ValueTask>? OnCircuitStateChanged { get; set; }
}
