using Cmsify.Admin.Services;

namespace Cmsify.Admin.State;

public sealed class UserPreferencesState
{
    private readonly BrowserStorage storage;
    private readonly SettingsApiClient settingsApiClient;

    public UserPreferencesState(BrowserStorage storage, SettingsApiClient settingsApiClient)
    {
        this.storage = storage;
        this.settingsApiClient = settingsApiClient;
    }

    public event Action? Changed;

    public string TimeZoneId { get; private set; } = TimeZoneInfo.Local.Id;

    public string Theme { get; private set; } = "auto";

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        Theme = await storage.GetThemeAsync() ?? "auto";
        try
        {
            var preferences = await settingsApiClient.GetPreferencesAsync(ct);
            TimeZoneId = string.IsNullOrWhiteSpace(preferences.TimeZoneId) ? TimeZoneInfo.Local.Id : preferences.TimeZoneId;
            Theme = string.IsNullOrWhiteSpace(preferences.Theme) ? Theme : preferences.Theme;
        }
        catch (ProblemDetailsException)
        {
            // Preferences require authentication; the theme still comes from local storage before login.
        }

        Changed?.Invoke();
    }

    public async Task SaveAsync(string timeZoneId, string theme, CancellationToken ct = default)
    {
        var preferences = await settingsApiClient.UpdatePreferencesAsync(new UpdateAccountPreferencesRequest(timeZoneId, theme), ct);
        TimeZoneId = preferences.TimeZoneId ?? timeZoneId;
        Theme = preferences.Theme;
        await storage.SetThemeAsync(Theme);
        Changed?.Invoke();
    }

    public async Task SetThemeAsync(string theme)
    {
        Theme = theme;
        await storage.SetThemeAsync(theme);
        Changed?.Invoke();
    }
}
