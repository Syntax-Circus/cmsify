using System.Net;
using System.Net.Sockets;
using Cmsify.Infrastructure.BackgroundServices;
using Microsoft.Extensions.Configuration;
using NSubstitute;

namespace Cmsify.Infrastructure.Tests;

public sealed class WebhookDestinationValidatorTests
{
    [Fact]
    public async Task ValidateAsync_AcceptsPublicHttpsHost_AfterOneResolution()
    {
        var resolver = Substitute.For<IWebhookDnsResolver>();
        resolver.ResolveAsync("hooks.example.test", Arg.Any<CancellationToken>()).Returns([IPAddress.Parse("8.8.8.8")]);

        var result = await CreateValidator(resolver).ValidateAsync("HTTPS://hooks.example.test/a");

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

        var result = await CreateValidator(resolver).ValidateAsync("http://hooks.example.test/a");

        Assert.False(result.IsValid);
        Assert.Null(result.DestinationUri);
        await resolver.DidNotReceive().ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ValidateAsync_AcceptsHttp_WhenExplicitlyEnabled()
    {
        var resolver = Substitute.For<IWebhookDnsResolver>();
        resolver.ResolveAsync("hooks.example.test", Arg.Any<CancellationToken>()).Returns([IPAddress.Parse("8.8.8.8")]);

        var result = await CreateValidator(resolver, allowHttp: true).ValidateAsync("http://hooks.example.test/a");

        Assert.True(result.IsValid);
        Assert.Equal("http://hooks.example.test/a", result.NormalizedUrl);
    }

    [Fact]
    public async Task ValidateAsync_RejectsCredentials_WithoutResolving()
    {
        var resolver = Substitute.For<IWebhookDnsResolver>();

        var result = await CreateValidator(resolver).ValidateAsync("https://user:password@hooks.example.test/a");

        Assert.False(result.IsValid);
        await resolver.DidNotReceive().ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ValidateAsync_AcceptsPublicIpLiteral_WithoutResolving()
    {
        var resolver = Substitute.For<IWebhookDnsResolver>();

        var result = await CreateValidator(resolver).ValidateAsync("https://8.8.8.8/hooks");

        Assert.True(result.IsValid);
        Assert.Equal([IPAddress.Parse("8.8.8.8")], result.Addresses);
        await resolver.DidNotReceive().ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ValidateAsync_RejectsEmptyResolution()
    {
        var resolver = Substitute.For<IWebhookDnsResolver>();
        resolver.ResolveAsync("hooks.example.test", Arg.Any<CancellationToken>()).Returns([]);

        var result = await CreateValidator(resolver).ValidateAsync("https://hooks.example.test/a");

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task ValidateAsync_RejectsResolverException()
    {
        var resolver = Substitute.For<IWebhookDnsResolver>();
        resolver.ResolveAsync("hooks.example.test", Arg.Any<CancellationToken>())
            .Returns<Task<IPAddress[]>>(_ => throw new SocketException());

        var result = await CreateValidator(resolver).ValidateAsync("https://hooks.example.test/a");

        Assert.False(result.IsValid);
        Assert.Equal("Webhook host could not be resolved.", result.Error);
    }

    [Fact]
    public async Task ValidateAsync_RejectsUnexpectedResolverException()
    {
        var resolver = Substitute.For<IWebhookDnsResolver>();
        resolver.ResolveAsync("hooks.example.test", Arg.Any<CancellationToken>())
            .Returns<Task<IPAddress[]>>(_ => throw new InvalidOperationException());

        var result = await CreateValidator(resolver).ValidateAsync("https://hooks.example.test/a");

        Assert.False(result.IsValid);
        Assert.Equal("Webhook host could not be resolved.", result.Error);
    }

    [Fact]
    public async Task ValidateAsync_RejectsCancelledResolution_AndForwardsCancellationToken()
    {
        var resolver = Substitute.For<IWebhookDnsResolver>();
        using var cancellation = new CancellationTokenSource();
        resolver.ResolveAsync("hooks.example.test", cancellation.Token)
            .Returns<Task<IPAddress[]>>(_ => throw new OperationCanceledException(cancellation.Token));

        var result = await CreateValidator(resolver).ValidateAsync("https://hooks.example.test/a", cancellation.Token);

        Assert.False(result.IsValid);
        Assert.Equal("Webhook host could not be resolved.", result.Error);
        await resolver.Received(1).ResolveAsync("hooks.example.test", cancellation.Token);
    }

    [Fact]
    public async Task ValidateAsync_RejectsMixedGlobalAndProhibitedResolution_AfterOneLookup()
    {
        var resolver = Substitute.For<IWebhookDnsResolver>();
        resolver.ResolveAsync("hooks.example.test", Arg.Any<CancellationToken>())
            .Returns([IPAddress.Parse("8.8.8.8"), IPAddress.Parse("10.0.0.1")]);

        var result = await CreateValidator(resolver).ValidateAsync("https://hooks.example.test/a");

        Assert.False(result.IsValid);
        Assert.Equal("Webhook URLs must not resolve to private, loopback, or reserved addresses.", result.Error);
        await resolver.Received(1).ResolveAsync("hooks.example.test", Arg.Any<CancellationToken>());
    }

    [Theory]
    [MemberData(nameof(NonGlobalAddresses))]
    public void IsGlobal_RejectsNonGlobalAddresses(string value) =>
        Assert.False(WebhookAddressPolicy.IsGlobal(IPAddress.Parse(value)));

    [Theory]
    [MemberData(nameof(GlobalAddresses))]
    public void IsGlobal_AcceptsGlobalAddresses(string value) =>
        Assert.True(WebhookAddressPolicy.IsGlobal(IPAddress.Parse(value)));

    public static IEnumerable<object[]> NonGlobalAddresses()
    {
        foreach (var value in new[]
        {
            "0.0.0.0", "0.255.255.255", "10.0.0.0", "10.255.255.255", "100.64.0.0", "100.127.255.255",
            "127.0.0.0", "127.255.255.255", "169.254.0.0", "169.254.255.255", "172.16.0.0", "172.31.255.255",
            "192.0.0.0", "192.0.0.8", "192.0.0.170", "192.0.2.0", "192.0.2.255", "192.88.99.0",
            "192.168.0.0", "192.168.255.255", "198.18.0.0", "198.19.255.255", "198.51.100.0", "198.51.100.255",
            "203.0.113.0", "203.0.113.255", "224.0.0.0", "239.255.255.255", "240.0.0.0", "255.255.255.255",
            "::", "::1", "::ffff:10.0.0.1", "64:ff9b:1::", "64:ff9b:1:ffff:ffff:ffff:ffff:ffff", "100::",
            "100::ffff:ffff:ffff:ffff", "100:0:0:1::", "2001::", "2001:1::4", "2001:2::", "2001:db8::", "2002::",
            "3fff::", "5f00::", "fc00::", "fdff:ffff:ffff:ffff:ffff:ffff:ffff:ffff", "fe80::", "febf:ffff:ffff:ffff:ffff:ffff:ffff:ffff", "ff00::"
        })
        {
            yield return [value];
        }
    }

    public static IEnumerable<object[]> GlobalAddresses()
    {
        foreach (var value in new[]
        {
            "8.8.8.8", "9.255.255.255", "11.0.0.0", "100.63.255.255", "100.128.0.0", "126.255.255.255", "128.0.0.0",
            "169.253.255.255", "169.255.0.0", "172.15.255.255", "172.32.0.0", "192.0.0.9", "192.0.0.10", "192.0.1.0",
            "192.167.255.255", "192.169.0.0", "198.17.255.255", "198.20.0.0", "203.0.112.255", "203.0.114.0", "223.255.255.255",
            "64:ff9b::808:808", "2001:1::1", "2001:3::1", "2001:4860:4860::8888", "2001:db7::1", "2001:db9::1"
        })
        {
            yield return [value];
        }
    }

    private static WebhookDestinationValidator CreateValidator(IWebhookDnsResolver resolver, bool allowHttp = false) =>
        new(resolver, new ConfigurationBuilder()
            .AddInMemoryCollection(allowHttp ? new Dictionary<string, string?> { ["Webhook:AllowHttp"] = "true" } : null)
            .Build());
}
