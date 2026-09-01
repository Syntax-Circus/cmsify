using System.Net;
using System.Net.Sockets;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Cmsify.Core.Interfaces.Repositories;
using Cmsify.Infrastructure.BackgroundServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cmsify.Infrastructure.Tests;

[Collection(OperationalMetricsTestGroup.Name)]
public sealed class WebhookDestinationValidatorTests
{
    [Fact]
    public async Task ReleaseSmokePrivateDestination_IsAcceptedOnlyForExactDevelopmentRunHost()
    {
        const string runId = "cmsify-smoke-1234abcd";
        var resolver = Substitute.For<IWebhookDnsResolver>();
        resolver.ResolveAsync($"webhook.{runId}.release-smoke.invalid", Arg.Any<CancellationToken>())
            .Returns([IPAddress.Parse("172.21.0.6")]);
        var options = Options.Create(new WebhookOperationalOptions { AllowHttp = true, ReleaseSmokeRunId = runId });

        var result = await new WebhookDestinationValidator(resolver, options, "Development")
            .ValidateAsync($"http://webhook.{runId}.release-smoke.invalid:8080/hook", TestContext.Current.CancellationToken);

        Assert.True(result.IsValid);
        Assert.Equal([IPAddress.Parse("172.21.0.6")], result.Addresses);
    }

    [Theory]
    [InlineData("Production", "webhook.cmsify-smoke-1234abcd.release-smoke.invalid")]
    [InlineData("Development", "webhook.cmsify-smoke-foreign.release-smoke.invalid")]
    [InlineData("Development", "private.example.test")]
    public async Task ReleaseSmokePrivateDestination_CannotEscapeEnvironmentOrRunBoundaries(string environment, string host)
    {
        const string runId = "cmsify-smoke-1234abcd";
        var resolver = Substitute.For<IWebhookDnsResolver>();
        resolver.ResolveAsync(host, Arg.Any<CancellationToken>()).Returns([IPAddress.Parse("172.21.0.6")]);
        var options = Options.Create(new WebhookOperationalOptions { AllowHttp = true, ReleaseSmokeRunId = runId });

        var result = await new WebhookDestinationValidator(resolver, options, environment)
            .ValidateAsync($"http://{host}:8080/hook", TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Equal("Webhook URLs must not resolve to private, loopback, or reserved addresses.", result.Error);
    }

    [Fact]
    public async Task ValidateAsync_AcceptsPublicHttpsHost_AfterOneResolution()
    {
        var resolver = Substitute.For<IWebhookDnsResolver>();
        resolver.ResolveAsync("hooks.example.test", Arg.Any<CancellationToken>()).Returns([IPAddress.Parse("8.8.8.8")]);

        var result = await CreateValidator(resolver).ValidateAsync("HTTPS://hooks.example.test/a", TestContext.Current.CancellationToken);

        Assert.True(result.IsValid);
        Assert.Equal("https://hooks.example.test/a", result.NormalizedUrl);
        Assert.Equal(new Uri("https://hooks.example.test/a"), result.DestinationUri);
        Assert.Equal([IPAddress.Parse("8.8.8.8")], result.Addresses);
        await resolver.Received(1).ResolveAsync("hooks.example.test", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ValidateAsync_RejectsHttpByDefault_WithoutResolving()
    {
        var resolver = Substitute.For<IWebhookDnsResolver>();

        var result = await CreateValidator(resolver).ValidateAsync("http://hooks.example.test/a", TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Null(result.DestinationUri);
        await resolver.DidNotReceive().ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ValidateAsync_AcceptsHttp_WhenExplicitlyEnabled()
    {
        var resolver = Substitute.For<IWebhookDnsResolver>();
        resolver.ResolveAsync("hooks.example.test", Arg.Any<CancellationToken>()).Returns([IPAddress.Parse("8.8.8.8")]);

        var result = await CreateValidator(resolver, allowHttp: true).ValidateAsync("http://hooks.example.test/a", TestContext.Current.CancellationToken);

        Assert.True(result.IsValid);
        Assert.Equal("http://hooks.example.test/a", result.NormalizedUrl);
    }

    [Fact]
    public async Task ValidateAsync_RejectsCredentials_WithoutResolving()
    {
        var resolver = Substitute.For<IWebhookDnsResolver>();

        var result = await CreateValidator(resolver).ValidateAsync("https://user:password@hooks.example.test/a", TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        await resolver.DidNotReceive().ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ValidateAsync_AcceptsPublicIpLiteral_WithoutResolving()
    {
        var resolver = Substitute.For<IWebhookDnsResolver>();

        var result = await CreateValidator(resolver).ValidateAsync("https://8.8.8.8/hooks", TestContext.Current.CancellationToken);

        Assert.True(result.IsValid);
        Assert.Equal([IPAddress.Parse("8.8.8.8")], result.Addresses);
        await resolver.DidNotReceive().ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ValidateAsync_RejectsEmptyResolution()
    {
        var resolver = Substitute.For<IWebhookDnsResolver>();
        resolver.ResolveAsync("hooks.example.test", Arg.Any<CancellationToken>()).Returns([]);

        var result = await CreateValidator(resolver).ValidateAsync("https://hooks.example.test/a", TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task ValidateAsync_RejectsResolverException()
    {
        var resolver = Substitute.For<IWebhookDnsResolver>();
        resolver.ResolveAsync("hooks.example.test", Arg.Any<CancellationToken>())
            .Returns<Task<IPAddress[]>>(_ => throw new SocketException());

        var result = await CreateValidator(resolver).ValidateAsync("https://hooks.example.test/a", TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Equal("Webhook host could not be resolved.", result.Error);
    }

    [Fact]
    public async Task ValidateAsync_RejectsUnexpectedResolverException()
    {
        var resolver = Substitute.For<IWebhookDnsResolver>();
        resolver.ResolveAsync("hooks.example.test", Arg.Any<CancellationToken>())
            .Returns<Task<IPAddress[]>>(_ => throw new InvalidOperationException());

        var result = await CreateValidator(resolver).ValidateAsync("https://hooks.example.test/a", TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Equal("Webhook host could not be resolved.", result.Error);
    }

    [Fact]
    public async Task ValidateAsync_RethrowsCallerRequestedCancellation_AndForwardsCancellationToken()
    {
        var resolver = Substitute.For<IWebhookDnsResolver>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        resolver.ResolveAsync("hooks.example.test", cancellation.Token)
            .Returns<Task<IPAddress[]>>(_ => throw new OperationCanceledException(cancellation.Token));

        await Assert.ThrowsAsync<OperationCanceledException>(() => CreateValidator(resolver).ValidateAsync("https://hooks.example.test/a", cancellation.Token));

        await resolver.Received(1).ResolveAsync("hooks.example.test", cancellation.Token);
    }

    [Fact]
    public async Task DeliverRetryAsync_RethrowsCallerRequestedDnsCancellationWithoutSideEffects()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var resolver = Substitute.For<IWebhookDnsResolver>();
        resolver.ResolveAsync("hooks.example.test", cancellation.Token)
            .Returns<Task<IPAddress[]>>(_ => throw new OperationCanceledException(cancellation.Token));
        var factory = Substitute.For<IHttpClientFactory>();
        var repository = Substitute.For<IWebhookRepository>();
        using var listener = new MeterListener();
        var securityMeasurements = new List<string>();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == CmsifyOperationalMetrics.MeterName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            if (instrument.Name is "cmsify.webhook.destination.rejected" or "cmsify.webhook.connection.failed")
            {
                securityMeasurements.Add(instrument.Name);
            }
        });
        listener.Start();
        using var payload = JsonDocument.Parse("{}");
        var delivery = new PendingWebhookDeliveryDto(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "workspace.updated",
            "https://hooks.example.test/cancelled", "secret", payload.RootElement.Clone(), 0,
            DateTimeOffset.UtcNow, "worker", Guid.NewGuid());

        await Assert.ThrowsAsync<OperationCanceledException>(() => new WebhookDeliveryProcessor(factory, repository, CreateValidator(resolver))
            .DeliverRetryAsync(delivery, 2, cancellation.Token));

        factory.DidNotReceive().CreateClient(Arg.Any<string>());
        _ = repository.DidNotReceive().CompleteDeliverySucceededAsync(Arg.Any<WebhookDeliveryCompletionDto>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        _ = repository.DidNotReceive().CompleteDeliveryFailedAsync(Arg.Any<WebhookDeliveryCompletionDto>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        Assert.Empty(securityMeasurements);
    }

    [Fact]
    public async Task ValidateAsync_RejectsMixedGlobalAndProhibitedResolution_AfterOneLookup()
    {
        var resolver = Substitute.For<IWebhookDnsResolver>();
        resolver.ResolveAsync("hooks.example.test", Arg.Any<CancellationToken>())
            .Returns([IPAddress.Parse("8.8.8.8"), IPAddress.Parse("10.0.0.1")]);

        var result = await CreateValidator(resolver).ValidateAsync("https://hooks.example.test/a", TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Equal("Webhook URLs must not resolve to private, loopback, or reserved addresses.", result.Error);
        await resolver.Received(1).ResolveAsync("hooks.example.test", Arg.Any<CancellationToken>());
    }

    [Theory]
    [MemberData(nameof(Ipv4NonGlobalPrefixBoundaries))]
    public void IsGlobal_RejectsIpv4NonGlobalPrefixBoundaries(string prefix, string value) =>
        Assert.False(WebhookAddressPolicy.IsGlobal(IPAddress.Parse(value)), prefix);

    [Theory]
    [MemberData(nameof(Ipv4PermittedAdjacentAddresses))]
    public void IsGlobal_AcceptsIpv4PermittedAdjacentAddresses(string prefix, string value) =>
        Assert.True(WebhookAddressPolicy.IsGlobal(IPAddress.Parse(value)), prefix);

    [Theory]
    [MemberData(nameof(Ipv6NonGlobalPrefixBoundaries))]
    public void IsGlobal_RejectsIpv6NonGlobalPrefixBoundaries(string prefix, string value) =>
        Assert.False(WebhookAddressPolicy.IsGlobal(IPAddress.Parse(value)), prefix);

    [Theory]
    [MemberData(nameof(Ipv6PermittedAddressesAndExceptions))]
    public void IsGlobal_AcceptsIpv6PermittedAddressesAndExceptions(string prefix, string value) =>
        Assert.True(WebhookAddressPolicy.IsGlobal(IPAddress.Parse(value)), prefix);

    [Theory]
    [MemberData(nameof(Nat64WithNonGlobalEmbeddedIpv4Addresses))]
    public void IsGlobal_RejectsNat64WithNonGlobalEmbeddedIpv4(string category, string value) =>
        Assert.False(WebhookAddressPolicy.IsGlobal(IPAddress.Parse(value)), category);

    [Fact]
    public void IsGlobal_AcceptsNat64WithGlobalEmbeddedIpv4() =>
        Assert.True(WebhookAddressPolicy.IsGlobal(IPAddress.Parse("64:ff9b::808:808")));

    public static IEnumerable<object[]> Ipv4NonGlobalPrefixBoundaries()
    {
        foreach (var (prefix, value) in new[]
        {
            ("0.0.0.0/8", "0.0.0.0"), ("0.0.0.0/8", "0.255.255.255"),
            ("10.0.0.0/8", "10.0.0.0"), ("10.0.0.0/8", "10.255.255.255"),
            ("100.64.0.0/10", "100.64.0.0"), ("100.64.0.0/10", "100.127.255.255"),
            ("127.0.0.0/8", "127.0.0.0"), ("127.0.0.0/8", "127.255.255.255"),
            ("169.254.0.0/16", "169.254.0.0"), ("169.254.0.0/16", "169.254.255.255"),
            ("172.16.0.0/12", "172.16.0.0"), ("172.16.0.0/12", "172.31.255.255"),
            ("192.0.0.0/24", "192.0.0.0"), ("192.0.0.0/24", "192.0.0.255"),
            ("192.0.2.0/24", "192.0.2.0"), ("192.0.2.0/24", "192.0.2.255"),
            ("192.88.99.0/24", "192.88.99.0"), ("192.88.99.0/24", "192.88.99.255"),
            ("192.168.0.0/16", "192.168.0.0"), ("192.168.0.0/16", "192.168.255.255"),
            ("198.18.0.0/15", "198.18.0.0"), ("198.18.0.0/15", "198.19.255.255"),
            ("198.51.100.0/24", "198.51.100.0"), ("198.51.100.0/24", "198.51.100.255"),
            ("203.0.113.0/24", "203.0.113.0"), ("203.0.113.0/24", "203.0.113.255"),
            ("224.0.0.0/4", "224.0.0.0"), ("224.0.0.0/4", "239.255.255.255"),
            ("240.0.0.0/4", "240.0.0.0"), ("240.0.0.0/4", "255.255.255.255")
        })
        {
            yield return [prefix, value];
        }
    }

    public static IEnumerable<object[]> Nat64WithNonGlobalEmbeddedIpv4Addresses()
    {
        foreach (var (category, value) in new[]
        {
            ("private", "64:ff9b::a00:1"), ("loopback", "64:ff9b::7f00:1"),
            ("shared address space", "64:ff9b::6440:1"), ("documentation", "64:ff9b::c000:201"),
            ("reserved", "64:ff9b::f000:1"), ("limited broadcast", "64:ff9b::ffff:ffff")
        })
        {
            yield return [category, value];
        }
    }

    public static IEnumerable<object[]> Ipv4PermittedAdjacentAddresses()
    {
        foreach (var (prefix, value) in new[]
        {
            ("0.0.0.0/8", "1.0.0.0"),
            ("10.0.0.0/8", "9.255.255.255"), ("10.0.0.0/8", "11.0.0.0"),
            ("100.64.0.0/10", "100.63.255.255"), ("100.64.0.0/10", "100.128.0.0"),
            ("127.0.0.0/8", "126.255.255.255"), ("127.0.0.0/8", "128.0.0.0"),
            ("169.254.0.0/16", "169.253.255.255"), ("169.254.0.0/16", "169.255.0.0"),
            ("172.16.0.0/12", "172.15.255.255"), ("172.16.0.0/12", "172.32.0.0"),
            ("192.0.0.0/24", "191.255.255.255"), ("192.0.0.0/24", "192.0.0.9"), ("192.0.0.0/24", "192.0.0.10"), ("192.0.0.0/24", "192.0.1.0"),
            ("192.0.2.0/24", "192.0.1.255"), ("192.0.2.0/24", "192.0.3.0"),
            ("192.88.99.0/24", "192.88.98.255"), ("192.88.99.0/24", "192.88.100.0"),
            ("192.168.0.0/16", "192.167.255.255"), ("192.168.0.0/16", "192.169.0.0"),
            ("198.18.0.0/15", "198.17.255.255"), ("198.18.0.0/15", "198.20.0.0"),
            ("198.51.100.0/24", "198.51.99.255"), ("198.51.100.0/24", "198.51.101.0"),
            ("203.0.113.0/24", "203.0.112.255"), ("203.0.113.0/24", "203.0.114.0"),
            ("224.0.0.0/4", "223.255.255.255")
        })
        {
            yield return [prefix, value];
        }
    }

    public static IEnumerable<object[]> Ipv6NonGlobalPrefixBoundaries()
    {
        foreach (var (prefix, value) in new[]
        {
            ("::/128", "::"), ("::1/128", "::1"), ("::ffff:0:0/96 (mapped private)", "::ffff:10.0.0.1"),
            ("64:ff9b:1::/48", "64:ff9b:1::"), ("64:ff9b:1::/48", "64:ff9b:1:ffff:ffff:ffff:ffff:ffff"),
            ("100::/64", "100::"), ("100::/64", "100::ffff:ffff:ffff:ffff"),
            ("100:0:0:1::/64", "100:0:0:1::"), ("100:0:0:1::/64", "100:0:0:1:ffff:ffff:ffff:ffff"),
            ("2001::/23", "2001::"), ("2001::/23", "2001:1ff:ffff:ffff:ffff:ffff:ffff:ffff"),
            ("2001:2::/48", "2001:2::"), ("2001:2::/48", "2001:2:0:ffff:ffff:ffff:ffff:ffff"),
            ("2001:db8::/32", "2001:db8::"), ("2001:db8::/32", "2001:db8:ffff:ffff:ffff:ffff:ffff:ffff"),
            ("2002::/16", "2002::"), ("2002::/16", "2002:ffff:ffff:ffff:ffff:ffff:ffff:ffff"),
            ("3fff::/20", "3fff::"), ("3fff::/20", "3fff:fff:ffff:ffff:ffff:ffff:ffff:ffff"),
            ("5f00::/16", "5f00::"), ("5f00::/16", "5f00:ffff:ffff:ffff:ffff:ffff:ffff:ffff"),
            ("fc00::/7", "fc00::"), ("fc00::/7", "fdff:ffff:ffff:ffff:ffff:ffff:ffff:ffff"),
            ("fe80::/10", "fe80::"), ("fe80::/10", "febf:ffff:ffff:ffff:ffff:ffff:ffff:ffff"),
            ("ff00::/8", "ff00::"), ("ff00::/8", "ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff")
        })
        {
            yield return [prefix, value];
        }
    }

    public static IEnumerable<object[]> Ipv6PermittedAddressesAndExceptions()
    {
        foreach (var (prefix, value) in new[]
        {
            ("IPv4-mapped global address", "::ffff:8.8.8.8"),
            ("64:ff9b::/96", "64:ff9b::808:808"),
            ("2001::/23 exception 2001:1::1", "2001:1::1"), ("2001::/23 exception 2001:1::2", "2001:1::2"),
            ("2001::/23 exception 2001:1::3", "2001:1::3"), ("2001::/23 exception 2001:3::/32", "2001:3::1"),
            ("2001::/23 exception 2001:4:112::/48", "2001:4:112::1"),
            ("2001::/23 exception 2001:20::/28", "2001:20::1"), ("2001::/23 exception 2001:30::/28", "2001:30::1"),
            ("2001::/23 permitted adjacent", "2000:ffff:ffff:ffff:ffff:ffff:ffff:ffff"), ("2001::/23 permitted adjacent", "2001:200::"),
            ("2001:db8::/32 permitted adjacent", "2001:db7:ffff:ffff:ffff:ffff:ffff:ffff"),
            ("2001:db8::/32 permitted adjacent", "2001:db9::"),
            ("2001:2::/48 permitted adjacent", "2001:3::1"),
            ("2002::/16 permitted adjacent", "2001:ffff:ffff:ffff:ffff:ffff:ffff:ffff"), ("2002::/16 permitted adjacent", "2003::"),
            ("3fff::/20 permitted adjacent", "3ffe:ffff:ffff:ffff:ffff:ffff:ffff:ffff"), ("3fff::/20 permitted adjacent", "3fff:1000::"),
            ("representative global unicast", "2001:4860:4860::8888")
        })
        {
            yield return [prefix, value];
        }
    }

    private static WebhookDestinationValidator CreateValidator(IWebhookDnsResolver resolver, bool allowHttp = false) =>
        new(resolver, new ConfigurationBuilder()
            .AddInMemoryCollection(allowHttp ? new Dictionary<string, string?> { ["Webhook:AllowHttp"] = "true" } : null)
            .Build());
}
