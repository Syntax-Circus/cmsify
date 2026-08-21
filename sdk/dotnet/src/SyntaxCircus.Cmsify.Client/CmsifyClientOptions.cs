namespace SyntaxCircus.Cmsify;

public sealed class CmsifyClientOptions
{
    public Uri? BaseUrl { get; set; }

    public string? ApiToken { get; set; }

    public Func<CancellationToken, ValueTask<string?>>? TokenProvider { get; set; }

    public bool EnableRetries { get; set; } = true;

    public int MaxRetryAttempts { get; set; } = 3;

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(100);
}
