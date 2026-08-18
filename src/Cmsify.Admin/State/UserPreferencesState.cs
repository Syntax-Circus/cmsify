using Cmsify.Admin.Services;
using SyntaxCircus.Http.Resilience;

namespace Cmsify.Admin.State;

public sealed class UserPreferencesState
{
    private readonly BrowserStorage storage;
    private readonly SettingsApiClient settingsApiClient;
    private bool initialized;

    public UserPreferencesState(BrowserStorage storage, SettingsApiClient settingsApiClient)
    {
        this.storage = storage;
        this.settingsApiClient = settingsApiClient;
    }

    public event Action? Changed;

    public string TimeZoneId { get; private set; } = TimeZoneInfo.Local.Id;

    public string Theme { get; private set; } = "auto";

    public string EffectiveTheme { get; private set; } = "light";

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (initialized)
        {
            return;
        }

        var themeState = await storage.InitializeThemeAsync();
        Theme = NormalizeTheme(themeState.Theme);
        EffectiveTheme = NormalizeEffectiveTheme(themeState.EffectiveTheme);

        try
        {
            var preferences = await settingsApiClient.GetPreferencesAsync(ct);
            TimeZoneId = string.IsNullOrWhiteSpace(preferences.TimeZoneId) ? TimeZoneInfo.Local.Id : preferences.TimeZoneId;
            Theme = string.IsNullOrWhiteSpace(preferences.Theme) ? Theme : NormalizeTheme(preferences.Theme);
        }
        catch (ProblemDetailsException)
        {
            // Preferences require authentication; the theme still comes from local storage before login.
        }

        EffectiveTheme = NormalizeEffectiveTheme(await storage.SetThemeAsync(Theme));
        initialized = true;
        Changed?.Invoke();
    }

    public async Task SaveAsync(string timeZoneId, string theme, CancellationToken ct = default)
    {
        var preferences = await settingsApiClient.UpdatePreferencesAsync(new UpdateAccountPreferencesRequest(timeZoneId, theme), ct);
        TimeZoneId = preferences.TimeZoneId ?? timeZoneId;
        Theme = NormalizeTheme(preferences.Theme);
        EffectiveTheme = NormalizeEffectiveTheme(await storage.SetThemeAsync(Theme));
        Changed?.Invoke();
    }

    public async Task SetThemeAsync(string theme)
    {
        Theme = NormalizeTheme(theme);
        EffectiveTheme = NormalizeEffectiveTheme(await storage.SetThemeAsync(Theme));
        Changed?.Invoke();
    }

    private static string NormalizeTheme(string? theme) =>
        theme is "light" or "dark" or "auto" ? theme : "auto";

    private static string NormalizeEffectiveTheme(string? theme) =>
        theme == "dark" ? "dark" : "light";
}
