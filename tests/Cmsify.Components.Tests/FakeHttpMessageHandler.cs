using System.Net;
using System.Text;

namespace SyntaxCircus.Cmsify.Components.Tests;

internal sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(respond(request));

    public static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
}

internal static class TestCmsifyClientFactory
{
    public static CmsifyClient Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var httpClient = new HttpClient(new FakeHttpMessageHandler(respond)) { BaseAddress = new Uri("https://cmsify.test/") };
        return new CmsifyClient(httpClient, new CmsifyClientOptions { EnableRetries = false });
    }
}
