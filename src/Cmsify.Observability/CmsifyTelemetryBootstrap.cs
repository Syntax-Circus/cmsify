using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Sentry.AspNetCore;
using Serilog;
using SyntaxCircus.AspNetCore.Common;

namespace Cmsify.Observability;

public sealed class CmsifyTelemetryBootstrap
{
    private readonly string serviceName;
    private readonly string serviceVersion;
    private readonly string environment;

    private CmsifyTelemetryBootstrap(CmsifyTelemetryOptions options, string serviceName, string serviceVersion, string environment)
    {
        Options = options;
        this.serviceName = string.IsNullOrWhiteSpace(options.OpenTelemetry.ServiceName) ? serviceName : options.OpenTelemetry.ServiceName;
        this.serviceVersion = string.IsNullOrWhiteSpace(options.OpenTelemetry.ServiceVersion) ? serviceVersion : options.OpenTelemetry.ServiceVersion;
        this.environment = string.IsNullOrWhiteSpace(options.OpenTelemetry.Environment) ? environment : options.OpenTelemetry.Environment;
    }

    public CmsifyTelemetryOptions Options { get; }

    public static CmsifyTelemetryBootstrap Register(
        IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment hostEnvironment,
        string defaultServiceName,
        IEnumerable<string>? meterNames = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(hostEnvironment);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultServiceName);

        var options = CmsifyTelemetryOptions.FromConfiguration(configuration);
        var bootstrap = new CmsifyTelemetryBootstrap(
            options,
            defaultServiceName,
            System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown",
            hostEnvironment.EnvironmentName);
        if (!options.OpenTelemetry.IsEnabled)
        {
            return bootstrap;
        }

        var openTelemetry = services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(bootstrap.serviceName, serviceVersion: bootstrap.serviceVersion)
                .AddAttributes(new Dictionary<string, object>
                {
                    ["deployment.environment"] = bootstrap.environment
                }));

        if (options.OpenTelemetry.ExportTraces)
        {
            openTelemetry.WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(Math.Clamp(options.OpenTelemetry.TracesSampleRate, 0d, 1d))))
                .AddOtlpExporter(exporter => ConfigureExporter(exporter, options.OpenTelemetry)));
        }

        if (options.OpenTelemetry.ExportMetrics)
        {
            openTelemetry.WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
                if (meterNames is not null)
                {
                    metrics.AddMeter(meterNames.ToArray());
                }
                metrics.AddOtlpExporter(exporter => ConfigureExporter(exporter, options.OpenTelemetry));
            });
        }

        return bootstrap;
    }

    public void ConfigureSerilog(LoggerConfiguration loggerConfiguration)
    {
        if (!Options.OpenTelemetry.IsEnabled || !Options.OpenTelemetry.ExportLogs)
        {
            return;
        }

        loggerConfiguration.WriteTo.OpenTelemetry(exporter =>
        {
            exporter.Endpoint = Options.OpenTelemetry.OtlpEndpoint;
            exporter.Protocol = Options.OpenTelemetry.OtlpProtocol.Equals("http/protobuf", StringComparison.OrdinalIgnoreCase)
                ? Serilog.Sinks.OpenTelemetry.OtlpProtocol.HttpProtobuf
                : Serilog.Sinks.OpenTelemetry.OtlpProtocol.Grpc;
            exporter.Headers = ParseHeaders(Options.OpenTelemetry.Headers);
            exporter.ResourceAttributes = new Dictionary<string, object>
            {
                ["service.name"] = serviceName,
                ["service.version"] = serviceVersion,
                ["deployment.environment"] = environment
            };
        });
    }

    public void ConfigureSentry(SentryAspNetCoreOptions sentry)
    {
        if (!Options.Sentry.IsEnabled)
        {
            return;
        }

        sentry.Dsn = Options.Sentry.Dsn;
        sentry.Debug = Options.Sentry.Debug;
        sentry.TracesSampleRate = Math.Clamp(Options.Sentry.TracesSampleRate, 0d, 1d);
        if (!string.IsNullOrWhiteSpace(Options.Sentry.Environment))
        {
            sentry.Environment = Options.Sentry.Environment;
        }
        sentry.SendDefaultPii = false;
        sentry.MinimumEventLevel = LogLevel.Error;
        sentry.MinimumBreadcrumbLevel = LogLevel.Information;
        sentry.TracesSampler = context => context.TransactionContext.Name.Contains("/_blazor", StringComparison.OrdinalIgnoreCase)
            ? 0d
            : Math.Clamp(Options.Sentry.TracesSampleRate, 0d, 1d);
        sentry.SetBeforeSend(@event =>
        {
            if (Activity.Current is { } activity)
            {
                @event.SetTag("trace_id", activity.TraceId.ToString());
            }
            if (CorrelationContextAccessor.CurrentCorrelationId is { Length: > 0 } correlationId)
            {
                @event.SetTag("correlation_id", correlationId);
            }
            return @event;
        });
    }

    public void LogStartupWarning(Microsoft.Extensions.Logging.ILogger logger)
    {
        if (Options.OpenTelemetry.StartupWarning is { } warning)
        {
            logger.LogWarning("{TelemetryWarning}", warning);
        }
    }

    private static void ConfigureExporter(OtlpExporterOptions exporter, OpenTelemetryOptions options)
    {
        exporter.Endpoint = new Uri(options.OtlpEndpoint);
        exporter.Protocol = options.OtlpProtocol.Equals("http/protobuf", StringComparison.OrdinalIgnoreCase)
            ? OtlpExportProtocol.HttpProtobuf
            : OtlpExportProtocol.Grpc;
        exporter.Headers = options.Headers;
    }

    private static Dictionary<string, string> ParseHeaders(string headers)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in headers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator > 0 && separator < pair.Length - 1)
            {
                result[pair[..separator].Trim()] = pair[(separator + 1)..].Trim();
            }
        }
        return result;
    }
}
