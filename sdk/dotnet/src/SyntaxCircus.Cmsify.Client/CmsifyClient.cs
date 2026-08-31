using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SyntaxCircus.Cmsify.Contracts;
using SyntaxCircus.Http.Resilience;

namespace SyntaxCircus.Cmsify;

public sealed record CmsifyDownload(byte[] Content, string FileName, string ContentType);

public sealed class CmsifyClient
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly object ResiliencePipelineServiceKey = new();
    private readonly HttpClient httpClient;
    private readonly CmsifyClientOptions options;
    private readonly HttpRequestResiliencePipeline resiliencePipeline;
    private readonly bool enableRetries;
    private readonly ConcurrentDictionary<string, string> etags = new(StringComparer.OrdinalIgnoreCase);

    public CmsifyClient(CmsifyClientOptions options)
        : this(new HttpClient(), options) { }

    public CmsifyClient(HttpClient httpClient, IOptions<CmsifyClientOptions> options)
        : this(httpClient, options?.Value ?? throw new ArgumentNullException(nameof(options))) { }

    public CmsifyClient(HttpClient httpClient, CmsifyClientOptions options)
        : this(httpClient, options, CreateResiliencePipeline(options)) { }

    public CmsifyClient(HttpClient httpClient, CmsifyClientOptions options, HttpRequestResiliencePipeline resiliencePipeline)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.resiliencePipeline = resiliencePipeline ?? throw new ArgumentNullException(nameof(resiliencePipeline));
        enableRetries = options.EnableRetries;
        if (options.BaseUrl is not null)
        {
            httpClient.BaseAddress = options.BaseUrl;
        }

        httpClient.Timeout = Timeout.InfiniteTimeSpan;
        Auth = new AuthClient(this);
        Health = new HealthClient(this);
        Workspaces = new WorkspaceClient(this);
        Templates = new TemplateClient(this);
        Content = new ContentClient(this);
        Media = new MediaClient(this);
        PickLists = new PickListClient(this);
        Components = new ComponentClient(this);
        Tags = new TagClient(this);
        Webhooks = new WebhookClient(this);
        Audit = new AuditClient(this);
        Users = new UserClient(this);
        ApiClients = new ApiClientManagementClient(this);
        Settings = new SettingsClient(this);
        Packages = new PackageClient(this);
    }

    public AuthClient Auth { get; }
    public HealthClient Health { get; }
    public WorkspaceClient Workspaces { get; }
    public TemplateClient Templates { get; }
    public ContentClient Content { get; }
    public MediaClient Media { get; }
    public PickListClient PickLists { get; }
    public ComponentClient Components { get; }
    public TagClient Tags { get; }
    public WebhookClient Webhooks { get; }
    public AuditClient Audit { get; }
    public UserClient Users { get; }
    public ApiClientManagementClient ApiClients { get; }
    public SettingsClient Settings { get; }
    public PackageClient Packages { get; }

    public Task<T?> GetAsync<T>(string path, CancellationToken cancellationToken = default) => SendAsync<T>(HttpMethod.Get, path, null, cancellationToken);

    public Task<T?> PostAsync<T>(string path, object? body = null, CancellationToken cancellationToken = default) => SendAsync<T>(HttpMethod.Post, path, body, cancellationToken);

    public Task<T?> PutAsync<T>(string path, object? body = null, CancellationToken cancellationToken = default) => SendAsync<T>(HttpMethod.Put, path, body, cancellationToken);

    public Task<T?> DeleteAsync<T>(string path, CancellationToken cancellationToken = default) => SendAsync<T>(HttpMethod.Delete, path, null, cancellationToken);

    public Task<T?> PutAsync<T>(string path, object? body, string ifMatch, CancellationToken cancellationToken = default) => SendAsync<T>(HttpMethod.Put, path, body, cancellationToken, ifMatch);

    public Task<T?> DeleteAsync<T>(string path, string ifMatch, CancellationToken cancellationToken = default) => SendAsync<T>(HttpMethod.Delete, path, null, cancellationToken, ifMatch);

    public async Task<byte[]> DownloadAsync(string path, CancellationToken cancellationToken = default)
    {
        return (await DownloadWithMetadataAsync(path, cancellationToken).ConfigureAwait(false)).Content;
    }

    /// <summary>Downloads a response and preserves its filename and content type metadata.</summary>
    public async Task<CmsifyDownload> DownloadWithMetadataAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var buffer = new MemoryStream();
        var metadata = await DownloadToAsyncCore(path, buffer, cancellationToken).ConfigureAwait(false);
        return new CmsifyDownload(buffer.ToArray(), metadata.FileName, metadata.ContentType);
    }

    /// <summary>Downloads a response directly to <paramref name="destination"/> without buffering it in memory.</summary>
    public async Task DownloadToAsync(string path, Stream destination, CancellationToken cancellationToken = default)
    {
        await DownloadToAsyncCore(path, destination, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(string FileName, string ContentType)> DownloadToAsyncCore(string path, Stream destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var uri = CreateUri(path);
        var correlationId = string.Empty;
        using var response = await resiliencePipeline.SendAsync(
            async (_, attemptToken) =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
                correlationId = await AddAuthenticationAndCorrelationAsync(request, attemptToken).ConfigureAwait(false);
                return request;
            },
            SendHttpRequestAsync,
            HttpCompletionOption.ResponseHeadersRead,
            GetReplaySafety(HttpMethod.Get),
            ObserveResponseAsync,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new CmsifyApiException(response.StatusCode, await ReadProblemAsync(response, cancellationToken).ConfigureAwait(false), GetCorrelationId(response, correlationId));
        }

        await response.Content.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        var disposition = response.Content.Headers.ContentDisposition;
        var fileName = disposition?.FileNameStar ?? disposition?.FileName?.Trim('"') ?? "download";
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        return (fileName, contentType);
    }

    internal async Task<T?> SendMultipartAsync<T>(string path, MultipartFormDataContent content, CancellationToken cancellationToken)
    {
        var uri = CreateUri(path);
        var correlationId = string.Empty;
        using var response = await resiliencePipeline.SendAsync(
            async (_, attemptToken) =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = content };
                correlationId = await AddAuthenticationAndCorrelationAsync(request, attemptToken).ConfigureAwait(false);
                return request;
            },
            SendHttpRequestAsync,
            HttpCompletionOption.ResponseContentRead,
            HttpRequestReplaySafety.NotReplayable,
            ObserveResponseAsync,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new CmsifyApiException(response.StatusCode, await ReadProblemAsync(response, cancellationToken).ConfigureAwait(false), GetCorrelationId(response, correlationId));
        }

        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<T?> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken, string? ifMatch = null)
    {
        var uri = CreateUri(path);
        var serializedBody = body is null ? null : JsonSerializer.Serialize(body, JsonOptions);
        var correlationId = string.Empty;
        using var response = await resiliencePipeline.SendAsync(
            async (_, attemptToken) =>
            {
                var request = new HttpRequestMessage(method, uri);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                correlationId = await AddAuthenticationAndCorrelationAsync(request, attemptToken).ConfigureAwait(false);

                if (serializedBody is not null)
                {
                    request.Content = new StringContent(serializedBody, Encoding.UTF8, "application/json");
                }

                if (ifMatch is not null)
                {
                    request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
                }
                else if (etags.TryGetValue(uri.ToString(), out var trackedEtag) && method != HttpMethod.Get)
                {
                    request.Headers.TryAddWithoutValidation("If-Match", trackedEtag);
                }

                return request;
            },
            SendHttpRequestAsync,
            HttpCompletionOption.ResponseHeadersRead,
            GetReplaySafety(method),
            ObserveResponseAsync,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var problem = await ReadProblemAsync(response, cancellationToken).ConfigureAwait(false);
            throw new CmsifyApiException(response.StatusCode, problem, GetCorrelationId(response, correlationId));
        }

        if (response.Headers.ETag?.Tag is { } etag)
        {
            etags[uri.ToString()] = etag;
        }

        if (response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0 || typeof(T) == typeof(object))
        {
            return default;
        }

        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ProblemDetailsModel> ReadProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, cancellationToken).ConfigureAwait(false);
            if (problem.ValueKind != JsonValueKind.Object)
            {
                return new ProblemDetailsModel(null, response.ReasonPhrase, (int)response.StatusCode, null, null, null, null, null);
            }

            var errors = problem.TryGetProperty("errors", out var errorsElement)
                ? JsonSerializer.Deserialize<Dictionary<string, string[]>>(errorsElement.GetRawText(), JsonOptions)
                : null;
            var extensions = problem.EnumerateObject().Where(p => p.Name is not ("type" or "title" or "status" or "detail" or "instance" or "traceId" or "errors"))
                .ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.OrdinalIgnoreCase);
            return new ProblemDetailsModel(
                GetString(problem, "type"), GetString(problem, "title"), GetInt(problem, "status"), GetString(problem, "detail"),
                GetString(problem, "instance"), GetString(problem, "traceId"), errors, extensions);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or NotSupportedException)
        {
            return new ProblemDetailsModel(null, response.ReasonPhrase, (int)response.StatusCode, null, null, null, null, null);
        }
    }

    private static string? GetString(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static int? GetInt(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.TryGetInt32(out var result) ? result : null;

    internal static string WorkspacePath(Guid workspaceId, string suffix) => $"/api/v1/workspaces/{workspaceId}{suffix}";
    internal static async IAsyncEnumerable<T> ListAll<T>(Func<int, CancellationToken, Task<PagedResponse<T>?>> loader, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (var page = 1; ; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await loader(page, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("Cmsify returned an empty page response.");
            foreach (var item in result.Items)
            {
                yield return item;
            }

            if (page >= result.TotalPages || result.Items.Count == 0)
            {
                yield break;
            }
        }
    }

    internal static async Task<IReadOnlyList<T>> ListAllToListAsync<T>(Func<int, CancellationToken, Task<PagedResponse<T>?>> loader, CancellationToken cancellationToken = default)
    {
        var items = new List<T>();
        await foreach (var item in ListAll(loader, cancellationToken))
        {
            items.Add(item);
        }

        return items;
    }

    private static JsonSerializerOptions CreateJsonOptions() => new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private Uri CreateUri(string path) => new(httpClient.BaseAddress ?? throw new InvalidOperationException("Cmsify BaseUrl is not configured."), path.TrimStart('/'));

    private HttpRequestReplaySafety GetReplaySafety(HttpMethod method) =>
        enableRetries && (method == HttpMethod.Get || method == HttpMethod.Head || method == HttpMethod.Options)
            ? HttpRequestReplaySafety.Replayable
            : HttpRequestReplaySafety.NotReplayable;

    private Task<HttpResponseMessage> SendHttpRequestAsync(
        HttpRequestMessage request,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken) =>
        httpClient.SendAsync(request, completionOption, cancellationToken);

    private async ValueTask<string> AddAuthenticationAndCorrelationAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var correlationId = Guid.CreateVersion7().ToString();
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", correlationId);
        var token = options.TokenProvider is not null
            ? await options.TokenProvider(cancellationToken).ConfigureAwait(false)
            : options.ApiToken;
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return correlationId;
    }

    private ValueTask ObserveResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        options.ResponseObserver is { } observer
            ? new ValueTask(observer(response, cancellationToken))
            : ValueTask.CompletedTask;

    private static string GetCorrelationId(HttpResponseMessage response, string fallback) =>
        response.Headers.TryGetValues("X-Correlation-Id", out var values) ? values.FirstOrDefault() ?? fallback : fallback;

    internal static HttpRequestResiliencePipeline CreateResiliencePipeline(CmsifyClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new HttpRequestResiliencePipeline("CmsifyClient", new HttpRequestResilienceOptions
        {
            MaxAttempts = options.EnableRetries ? Math.Max(1, options.MaxRetryAttempts) : 1,
            TotalRequestTimeout = options.RequestTimeout == Timeout.InfiniteTimeSpan
                ? TimeSpan.MaxValue
                : options.RequestTimeout,
            CircuitFailureRatio = options.CircuitFailureRatio,
            CircuitMinimumThroughput = options.CircuitMinimumThroughput,
            CircuitSamplingDuration = options.CircuitSamplingDuration,
            CircuitBreakDuration = options.CircuitBreakDuration,
            OnRetry = options.OnRetry,
            OnTimeout = options.OnTimeout,
            OnCircuitStateChanged = options.OnCircuitStateChanged,
        });
    }

    internal static object PipelineServiceKey => ResiliencePipelineServiceKey;
}

public static class CmsifyClientServiceCollectionExtensions
{
    public static IHttpClientBuilder AddCmsifyClient(this IServiceCollection services, Action<CmsifyClientOptions> configure)
    {
        services.Configure(configure);
        services.AddKeyedSingleton<HttpRequestResiliencePipeline>(
            CmsifyClient.PipelineServiceKey,
            (provider, _) => CmsifyClient.CreateResiliencePipeline(provider.GetRequiredService<IOptions<CmsifyClientOptions>>().Value));

        var builder = services.AddHttpClient(nameof(CmsifyClient));
        builder.AddTypedClient((httpClient, provider) => new CmsifyClient(
            httpClient,
            provider.GetRequiredService<IOptions<CmsifyClientOptions>>().Value,
            provider.GetRequiredKeyedService<HttpRequestResiliencePipeline>(CmsifyClient.PipelineServiceKey)));
        return builder;
    }
}
