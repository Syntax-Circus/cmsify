using System.Text.Json;
using Microsoft.JSInterop;

namespace Cmsify.Admin.Services;

public sealed class BrowserStorage
{
    private readonly IJSRuntime jsRuntime;

    public BrowserStorage(IJSRuntime jsRuntime) => this.jsRuntime = jsRuntime;

    public ValueTask SetAsync<T>(string storage, string key, T value) =>
        jsRuntime.InvokeVoidAsync("cmsifyStorage.set", storage, key, JsonSerializer.Serialize(value));

    public async ValueTask<T?> GetAsync<T>(string storage, string key)
    {
        var json = await jsRuntime.InvokeAsync<string?>("cmsifyStorage.get", storage, key);
        return string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json);
    }

    public ValueTask RemoveAsync(string storage, string key) =>
        jsRuntime.InvokeVoidAsync("cmsifyStorage.remove", storage, key);

    public ValueTask<string?> GetThemeAsync() =>
        jsRuntime.InvokeAsync<string?>("cmsifyStorage.getTheme");

    public ValueTask<BrowserThemeState> InitializeThemeAsync() =>
        jsRuntime.InvokeAsync<BrowserThemeState>("cmsifyStorage.initTheme");

    public ValueTask<string> SetThemeAsync(string theme) =>
        jsRuntime.InvokeAsync<string>("cmsifyStorage.setTheme", theme);

    public sealed record BrowserThemeState(string Theme, string EffectiveTheme);
}
