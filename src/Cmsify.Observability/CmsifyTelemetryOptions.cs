using Microsoft.Extensions.Configuration;

namespace Cmsify.Observability;

public sealed class CmsifyTelemetryOptions
{
    public OpenTelemetryOptions OpenTelemetry { get; init; } = new();

    public SentryOptions Sentry { get; init; } = new();

    public static CmsifyTelemetryOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var openTelemetry = new OpenTelemetryOptions();
        configuration.GetSection(OpenTelemetryOptions.SectionName).Bind(openTelemetry);

        var sentry = new SentryOptions();
        configuration.GetSection(SentryOptions.SectionName).Bind(sentry);

        return new CmsifyTelemetryOptions
        {
            OpenTelemetry = openTelemetry,
            Sentry = sentry
        };
    }
}

public sealed class OpenTelemetryOptions
{
    public const string SectionName = "OpenTelemetry";

    public bool Enabled { get; set; }

    public bool ExportLogs { get; set; } = true;

    public bool ExportTraces { get; set; } = true;

    public bool ExportMetrics { get; set; } = true;

    public string OtlpEndpoint { get; set; } = string.Empty;

    public string OtlpProtocol { get; set; } = "grpc";

    public string Headers { get; set; } = string.Empty;

    public double TracesSampleRate { get; set; } = 1d;

    public string ServiceName { get; set; } = string.Empty;

    public string ServiceVersion { get; set; } = string.Empty;

    public string Environment { get; set; } = string.Empty;

    public bool IsEnabled => Enabled && Uri.TryCreate(OtlpEndpoint, UriKind.Absolute, out _);

    public string? StartupWarning => Enabled && !IsEnabled
        ? "OpenTelemetry is disabled because its OTLP endpoint is missing or invalid."
        : null;
}

public sealed class SentryOptions
{
    public const string SectionName = "Sentry";

    public string Dsn { get; set; } = string.Empty;

    public string Environment { get; set; } = string.Empty;

    public bool Debug { get; set; }

    public double TracesSampleRate { get; set; }

    public bool SendDefaultPii => false;

    public bool IsEnabled => !string.IsNullOrWhiteSpace(Dsn);
}
