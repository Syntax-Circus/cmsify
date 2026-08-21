using System.Net.Http.Headers;
using Cmsify.Admin.Auth;
using SyntaxCircus.Http.Resilience;

namespace Cmsify.Admin.Services;

/// <summary>
/// Cmsify.Admin's typed-client base: attaches per-user bearer-token auth and a correlation ID to every
/// request, and keeps the local session's expiry in sync with the API's sliding session on every response.
/// A normal scoped, constructor-injected typed client (not a pooled <c>DelegatingHandler</c>) so
/// <see cref="IApiTokenAccessor"/> — scoped per Blazor Server circuit/user — resolves correctly. See
/// <see cref="ApiClientBase"/>'s doc comment for why a <c>DelegatingHandler</c> registered via
/// <c>AddHttpMessageHandler</c> would be unsafe here.
/// </summary>
public abstract class CmsifyApiClientBase : ApiClientBase
{
    private const string SessionExpiresAtHeaderName = "X-Session-Expires-At";

    private readonly IApiTokenAccessor apiTokenAccessor;

    protected CmsifyApiClientBase(IHttpClientFactory httpClientFactory, IApiTokenAccessor apiTokenAccessor)
        : base(httpClientFactory.CreateClient("CmsifyApi"))
    {
        this.apiTokenAccessor = apiTokenAccessor;
    }

    protected override async Task OnRequestSendingAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Add("X-Correlation-Id", Guid.CreateVersion7().ToString());
        var token = await apiTokenAccessor.GetTokenAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    protected override async Task OnResponseReceivedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Headers.TryGetValues(SessionExpiresAtHeaderName, out var expiresAtValues)
            && DateTimeOffset.TryParse(expiresAtValues.FirstOrDefault(), out var expiresAt))
        {
            await apiTokenAccessor.NoteSessionExpiryAsync(expiresAt, cancellationToken);
        }
    }

    /// <summary>
    /// Awaits a nullable-result verb helper (<c>GetAsync</c>/<c>GetWithETagAsync</c>/<c>PostAsync</c>/<c>PutAsync</c>
    /// with a typed response) and throws if the API returned an empty body, for endpoints that always return a
    /// resource on success.
    /// </summary>
    protected static async Task<T> RequireAsync<T>(Task<T?> task)
    {
        var result = await task.ConfigureAwait(false);
        return result ?? throw new InvalidOperationException("API returned an empty response body.");
    }
}
