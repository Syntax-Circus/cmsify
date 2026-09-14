using System.Net;
using System.Text;

namespace SyntaxCircus.Cmsify.Components.Tests;

internal sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond, ConcurrencyGate? gate = null, Func<HttpRequestMessage, bool>? gateWhen = null) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (gate is not null && (gateWhen is null || gateWhen(request)))
        {
            await gate.EnterAsync(cancellationToken);
        }

        return respond(request);
    }

    public static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
}

// Proves a set of requests were actually dispatched concurrently, deterministically rather than by
// racing a fixed delay against wall-clock time (which is exactly the kind of test that flakes on a
// slower/busier CI runner). Every gated request blocks here until RequiredConcurrency requests are
// simultaneously waiting, at which point they're all released together; MaxObserved records the
// highest number that were ever waiting at once. If the production code under test only ever issues
// one of these requests at a time (i.e. the parallelization regressed), the gate never reaches the
// threshold and each request falls through after Timeout instead of hanging forever - MaxObserved
// then correctly reports a number below RequiredConcurrency and the test's assertion fails.
internal sealed class ConcurrencyGate(int requiredConcurrency, TimeSpan? timeout = null)
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock sync = new();
    private int current;
    private int maxObserved;

    public int MaxObserved => Volatile.Read(ref maxObserved);

    public async Task EnterAsync(CancellationToken cancellationToken)
    {
        var value = Interlocked.Increment(ref current);
        lock (sync)
        {
            if (value > maxObserved)
            {
                maxObserved = value;
            }
        }

        if (value >= requiredConcurrency)
        {
            gate.TrySetResult();
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout ?? DefaultTimeout);
        try
        {
            await gate.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timed out waiting for the rest of the expected concurrent requests to arrive - fall
            // through so the caller gets a response instead of hanging; MaxObserved will correctly
            // be below RequiredConcurrency, which is what the test asserts on.
        }
        finally
        {
            Interlocked.Decrement(ref current);
        }
    }
}

internal static class TestCmsifyClientFactory
{
    public static CmsifyClient Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var httpClient = new HttpClient(new FakeHttpMessageHandler(respond)) { BaseAddress = new Uri("https://cmsify.test/") };
        return new CmsifyClient(httpClient, new CmsifyClientOptions { EnableRetries = false });
    }

    public static CmsifyClient CreateWithConcurrencyGate(Func<HttpRequestMessage, HttpResponseMessage> respond, ConcurrencyGate gate, Func<HttpRequestMessage, bool>? gateWhen = null)
    {
        var httpClient = new HttpClient(new FakeHttpMessageHandler(respond, gate, gateWhen)) { BaseAddress = new Uri("https://cmsify.test/") };
        return new CmsifyClient(httpClient, new CmsifyClientOptions { EnableRetries = false });
    }
}
