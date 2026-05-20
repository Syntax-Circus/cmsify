using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cmsify.Admin.State;

namespace Cmsify.Admin.Services;

public abstract class ApiClientBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string SessionExpiresAtHeaderName = "X-Session-Expires-At";
    private readonly Dictionary<string, string> etags = new(StringComparer.OrdinalIgnoreCase);
    private readonly IHttpClientFactory httpClientFactory;
    private readonly AuthState authState;

    protected ApiClientBase(IHttpClientFactory httpClientFactory, AuthState authState)
    {
        this.httpClientFactory = httpClientFactory;
        this.authState = authState;
    }

    protected async Task<(T Body, string? ETag)> GetWithETagAsync<T>(string url, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, url, body: null, ifMatch: null, ct);
        var body = await ReadAsync<T>(response, ct);
        var etag = response.Headers.ETag?.Tag;
        if (!string.IsNullOrWhiteSpace(etag))
        {
            etags[url] = etag;
        }

        return (body, etag);
    }

    protected async Task<T> GetAsync<T>(string url, CancellationToken ct = default)
    {
        var (body, _) = await GetWithETagAsync<T>(url, ct);
        return body;
    }

    protected async Task<T> PostAsync<T>(string url, object? body = null, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Post, url, body, ifMatch: null, ct);
        return await ReadAsync<T>(response, ct);
    }

    protected async Task PostAsync(string url, object? body = null, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Post, url, body, ifMatch: null, ct);
        await EnsureSuccessAsync(response, ct);
    }

    protected async Task<T> PutAsync<T>(string url, object body, string? ifMatch = null, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Put, url, body, ifMatch ?? FindETag(url), ct);
        var result = await ReadAsync<T>(response, ct);
        var etag = response.Headers.ETag?.Tag;
        if (!string.IsNullOrWhiteSpace(etag))
        {
            etags[url] = etag;
        }

        return result;
    }

    protected async Task DeleteAsync(string url, string? ifMatch = null, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Delete, url, body: null, ifMatch ?? FindETag(url), ct);
        await EnsureSuccessAsync(response, ct);
    }

    private string? FindETag(string url) =>
        etags.TryGetValue(url, out var etag) ? etag : null;

    protected async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body, string? ifMatch, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        if (!string.IsNullOrWhiteSpace(ifMatch))
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        return await SendAsync(request, ct);
    }

    protected async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient("CmsifyApi");
        request.Headers.Add("X-Correlation-Id", Guid.CreateVersion7().ToString());
        if (!string.IsNullOrWhiteSpace(authState.Token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authState.Token);
        }

        var response = await http.SendAsync(request, ct);
        if (response.Headers.TryGetValues(SessionExpiresAtHeaderName, out var expiresAtValues)
            && DateTimeOffset.TryParse(expiresAtValues.FirstOrDefault(), out var expiresAt))
        {
            await authState.UpdateExpiresAtAsync(expiresAt);
        }

        return response;
    }

    protected static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        await EnsureSuccessAsync(response, ct);
        var result = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
        return result is null ? throw new InvalidOperationException("API returned an empty response body.") : result;
    }

    protected static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new ProblemDetailsException((int)response.StatusCode, "Unauthorized", "Sign in is required.", null);
        }

        try
        {
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, ct);
            var title = problem.TryGetProperty("title", out var titleNode) ? titleNode.GetString() ?? response.ReasonPhrase ?? "API error" : response.ReasonPhrase ?? "API error";
            var detail = problem.TryGetProperty("detail", out var detailNode) ? detailNode.GetString() : null;
            Dictionary<string, string[]>? errors = null;
            if (problem.TryGetProperty("errors", out var errorsNode) && errorsNode.ValueKind == JsonValueKind.Object)
            {
                errors = errorsNode.EnumerateObject().ToDictionary(
                    property => property.Name,
                    property => property.Value.ValueKind == JsonValueKind.Array
                        ? property.Value.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray()
                        : [property.Value.GetString() ?? string.Empty]);
            }

            throw new ProblemDetailsException((int)response.StatusCode, title, detail, errors);
        }
        catch (JsonException)
        {
            throw new ProblemDetailsException((int)response.StatusCode, response.ReasonPhrase ?? "API error", await response.Content.ReadAsStringAsync(ct), null);
        }
    }
}
