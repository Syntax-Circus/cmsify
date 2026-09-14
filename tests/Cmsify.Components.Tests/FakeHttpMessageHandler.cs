using System.Net;
using System.Text;

namespace SyntaxCircus.Cmsify.Components.Tests;

internal sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond, TimeSpan? delay = null, ConcurrencyTracker? tracker = null) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        tracker?.Enter();
        try
        {
            // A real yield (rather than Task.FromResult's already-completed task) is required for
            // concurrently-started requests to actually overlap in time, which is what lets
            // ConcurrencyTracker observe more than one in-flight request at once.
            if (delay.HasValue)
            {
                await Task.Delay(delay.Value, cancellationToken);
            }

            return respond(request);
        }
        finally
        {
            tracker?.Exit();
        }
    }

    public static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
}

// Tracks the highest number of requests this handler saw in flight at the same time, so a test
// can assert that a set of calls actually ran concurrently rather than one at a time.
internal sealed class ConcurrencyTracker
{
    private int current;
    private int maxObserved;

    public int MaxObserved => maxObserved;

    public void Enter()
    {
        var value = Interlocked.Increment(ref current);
        int observed;
        do
        {
            observed = maxObserved;
            if (value <= observed)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref maxObserved, value, observed) != observed);
    }

    public void Exit() => Interlocked.Decrement(ref current);
}

internal static class TestCmsifyClientFactory
{
    public static CmsifyClient Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var httpClient = new HttpClient(new FakeHttpMessageHandler(respond)) { BaseAddress = new Uri("https://cmsify.test/") };
        return new CmsifyClient(httpClient, new CmsifyClientOptions { EnableRetries = false });
    }

    public static CmsifyClient CreateWithConcurrencyTracking(Func<HttpRequestMessage, HttpResponseMessage> respond, ConcurrencyTracker tracker, TimeSpan? delay = null)
    {
        var httpClient = new HttpClient(new FakeHttpMessageHandler(respond, delay ?? TimeSpan.FromMilliseconds(25), tracker)) { BaseAddress = new Uri("https://cmsify.test/") };
        return new CmsifyClient(httpClient, new CmsifyClientOptions { EnableRetries = false });
    }
}
