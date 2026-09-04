using Cmsify.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sentry.AspNetCore;
using Shouldly;
using Xunit;

namespace Cmsify.Observability.Tests;

public sealed class TelemetryOptionsTests
{
    [Fact]
    public void FromConfiguration_DefaultsToDisabledAndStrictGlitchTipPolicy()
    {
        var options = CmsifyTelemetryOptions.FromConfiguration(new ConfigurationBuilder().Build());

        options.OpenTelemetry.Enabled.ShouldBeFalse();
        options.OpenTelemetry.ExportLogs.ShouldBeTrue();
        options.OpenTelemetry.ExportTraces.ShouldBeTrue();
        options.OpenTelemetry.ExportMetrics.ShouldBeTrue();
        options.OpenTelemetry.TracesSampleRate.ShouldBe(1d);
        options.Sentry.Dsn.ShouldBeEmpty();
        options.Sentry.TracesSampleRate.ShouldBe(0d);
        options.Sentry.SendDefaultPii.ShouldBeFalse();
    }

    [Fact]
    public void FromConfiguration_UsesIndependentSignalAndSamplingSettings()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenTelemetry:Enabled"] = "true",
                ["OpenTelemetry:ExportLogs"] = "false",
                ["OpenTelemetry:ExportTraces"] = "true",
                ["OpenTelemetry:ExportMetrics"] = "false",
                ["OpenTelemetry:OtlpEndpoint"] = "https://signoz.example.test:4318",
                ["OpenTelemetry:TracesSampleRate"] = "0.25",
                ["Sentry:Dsn"] = "https://key@glitchtip.example.test/42",
                ["Sentry:TracesSampleRate"] = "0.5"
            })
            .Build();

        var options = CmsifyTelemetryOptions.FromConfiguration(configuration);

        options.OpenTelemetry.IsEnabled.ShouldBeTrue();
        options.OpenTelemetry.ExportLogs.ShouldBeFalse();
        options.OpenTelemetry.ExportTraces.ShouldBeTrue();
        options.OpenTelemetry.ExportMetrics.ShouldBeFalse();
        options.OpenTelemetry.TracesSampleRate.ShouldBe(0.25d);
        options.Sentry.IsEnabled.ShouldBeTrue();
        options.Sentry.TracesSampleRate.ShouldBe(0.5d);
    }

    [Fact]
    public void FromConfiguration_DisablesInvalidEnabledOtlpEndpointWithSafeWarning()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenTelemetry:Enabled"] = "true",
                ["OpenTelemetry:OtlpEndpoint"] = "not a uri"
            })
            .Build();

        var options = CmsifyTelemetryOptions.FromConfiguration(configuration);

        options.OpenTelemetry.IsEnabled.ShouldBeFalse();
        var warning = options.OpenTelemetry.StartupWarning;
        warning.ShouldNotBeNull();
        warning!.ShouldContain("disabled");
        warning.ShouldNotContain("not a uri");
    }

    [Fact]
    public void ConfigureSentry_DisablesPiiAndConfiguresStrictErrorReporting()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Sentry:Dsn"] = "https://public@example.invalid/1",
                ["Sentry:TracesSampleRate"] = "0.5"
            })
            .Build();
        var bootstrap = CmsifyTelemetryBootstrap.Register(
            new ServiceCollection(),
            configuration,
            new TestHostEnvironment(),
            "cmsify-test");
        var sentry = new SentryAspNetCoreOptions();

        bootstrap.ConfigureSentry(sentry);

        sentry.SendDefaultPii.ShouldBeFalse();
        sentry.MinimumEventLevel.ShouldBe(LogLevel.Error);
        sentry.MinimumBreadcrumbLevel.ShouldBe(LogLevel.Information);
        sentry.TracesSampler.ShouldNotBeNull();
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Cmsify.Observability.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
