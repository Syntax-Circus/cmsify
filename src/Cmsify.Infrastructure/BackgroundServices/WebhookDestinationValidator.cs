using System.Net;
using Cmsify.Core.Interfaces.Services;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cmsify.Infrastructure.BackgroundServices;

public sealed class WebhookDestinationValidator : IWebhookDestinationValidator
{
    private readonly bool allowHttp;
    private readonly IWebhookDnsResolver dnsResolver;

    [ActivatorUtilitiesConstructor]
    public WebhookDestinationValidator(IWebhookDnsResolver dnsResolver, IOptions<WebhookOperationalOptions> options)
    {
        this.dnsResolver = dnsResolver;
        allowHttp = options.Value.AllowHttp;
    }

    public WebhookDestinationValidator(IWebhookDnsResolver dnsResolver, IConfiguration configuration)
        : this(dnsResolver, Options.Create(OperationalOptions.ReadWebhook(configuration)))
    {
    }

    public async Task<WebhookDestinationValidationResult> ValidateAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                && !(allowHttp && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.IsLoopback)
        {
            return WebhookDestinationValidationResult.Invalid("Webhook URLs must use HTTPS and target a public host.");
        }

        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(uri.DnsSafeHost, out var address)
                ? [address]
                : (await dnsResolver.ResolveAsync(uri.DnsSafeHost, ct)).ToArray();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return WebhookDestinationValidationResult.Invalid("Webhook host could not be resolved.");
        }

        if (addresses.Length == 0 || addresses.Any(address => !WebhookAddressPolicy.IsGlobal(address)))
        {
            return WebhookDestinationValidationResult.Invalid("Webhook URLs must not resolve to private, loopback, or reserved addresses.");
        }

        return WebhookDestinationValidationResult.Valid(uri, addresses);
    }
}
