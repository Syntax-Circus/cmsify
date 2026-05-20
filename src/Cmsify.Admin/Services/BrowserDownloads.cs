using Microsoft.JSInterop;

namespace Cmsify.Admin.Services;

public sealed class BrowserDownloads
{
    private readonly IJSRuntime jsRuntime;

    public BrowserDownloads(IJSRuntime jsRuntime) => this.jsRuntime = jsRuntime;

    public ValueTask SaveAsync(FileDownloadResponse file) =>
        jsRuntime.InvokeVoidAsync("cmsifyDownloads.save", file.FileName, file.ContentType, Convert.ToBase64String(file.Content));
}
