using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using Shouldly;
using Xunit;

namespace SyntaxCircus.Cmsify.Components.Tests.Media;

public sealed class MediaThumbnailTests : BunitContext
{
    private static readonly Guid Workspace = Guid.NewGuid();
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47];

    private static MediaAssetResponse Asset(string mime = "image/png", long size = 1024, string name = "logo.png") =>
        new(Guid.NewGuid(), name, mime, size, "Logo", "/api/v1/workspaces/w/media/i/file", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static HttpResponseMessage Bytes(byte[] content, string mime = "image/png") =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(content) { Headers = { { "Content-Type", mime } } } };

    private IRenderedComponent<MediaThumbnailScope> RenderScoped(
        MediaAssetResponse asset, CmsifyClient? client, Func<MediaAssetResponse, string?>? resolver = null) =>
        Render<MediaThumbnailScope>(p => p
            .Add(x => x.Client, client)
            .Add(x => x.WorkspaceId, Workspace)
            .Add(x => x.UrlResolver, resolver)
            .Add(x => x.ChildContent, (RenderFragment)(builder =>
            {
                builder.OpenComponent<MediaThumbnail>(0);
                builder.AddComponentParameter(1, nameof(MediaThumbnail.Asset), asset);
                builder.AddComponentParameter(2, nameof(MediaThumbnail.CssClass), "thumb");
                builder.CloseComponent();
            })));

    [Fact]
    public void WithoutAScopeItRendersTheRawUrl()
    {
        var asset = Asset();
        var cut = Render<MediaThumbnail>(p => p.Add(x => x.Asset, asset).Add(x => x.CssClass, "thumb"));

        cut.Find("img.thumb").GetAttribute("src").ShouldBe(asset.Url);
    }

    [Fact]
    public void ResolverMapsTheUrl()
    {
        var cut = RenderScoped(Asset(), client: null, resolver: a => "https://cms.test" + a.Url);

        cut.Find("img.thumb").GetAttribute("src").ShouldBe("https://cms.test/api/v1/workspaces/w/media/i/file");
    }

    [Fact]
    public void ResolverReturningNullShowsAPlaceholder()
    {
        var cut = RenderScoped(Asset(), client: null, resolver: _ => null);

        cut.FindAll("img").ShouldBeEmpty();
        cut.Find("span.thumb.cmsify-media-thumb-placeholder").GetAttribute("role").ShouldBe("img");
    }

    [Fact]
    public void ClientLoadsTheImageAsADataUrl()
    {
        var requests = 0;
        var client = TestCmsifyClientFactory.Create(request =>
        {
            Interlocked.Increment(ref requests);
            request.RequestUri!.AbsolutePath.ShouldEndWith("/file");
            return Bytes(Png);
        });

        var cut = RenderScoped(Asset(), client);

        cut.WaitForAssertion(() => cut.Find("img.thumb").GetAttribute("src")
            .ShouldBe("data:image/png;base64," + Convert.ToBase64String(Png)));
        requests.ShouldBe(1);
    }

    [Fact]
    public void RepeatedRendersOfTheSameAssetFetchOnce()
    {
        var requests = 0;
        var client = TestCmsifyClientFactory.Create(_ => { Interlocked.Increment(ref requests); return Bytes(Png); });
        var asset = Asset();

        var cut = RenderScoped(asset, client);
        cut.WaitForAssertion(() => cut.Find("img.thumb"));
        cut.Render(); // re-render the scope with the same inputs
        cut.WaitForAssertion(() => cut.Find("img.thumb"));

        requests.ShouldBe(1);
    }

    [Fact]
    public void OversizedImageIsNotDownloaded()
    {
        var requests = 0;
        var client = TestCmsifyClientFactory.Create(_ => { Interlocked.Increment(ref requests); return Bytes(Png); });

        var cut = RenderScoped(Asset(size: MediaThumbnailContext.DefaultMaxInlineBytes + 1), client);

        cut.WaitForAssertion(() => cut.Find("span.cmsify-media-thumb-placeholder"));
        requests.ShouldBe(0);
    }

    [Fact]
    public void NonImageIsNotDownloaded()
    {
        var requests = 0;
        var client = TestCmsifyClientFactory.Create(_ => { Interlocked.Increment(ref requests); return Bytes(Png); });

        var cut = RenderScoped(Asset(mime: "application/pdf", name: "doc.pdf"), client);

        cut.WaitForAssertion(() => cut.Find("span.cmsify-media-thumb-placeholder"));
        requests.ShouldBe(0);
    }

    [Fact]
    public void FailedDownloadShowsAPlaceholderInsteadOfABrokenImage()
    {
        var client = TestCmsifyClientFactory.Create(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var cut = RenderScoped(Asset(), client);

        cut.WaitForAssertion(() => cut.Find("span.cmsify-media-thumb-placeholder"));
        cut.FindAll("img").ShouldBeEmpty();
    }
}
