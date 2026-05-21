using Cmsify.Admin.Services;
using Microsoft.JSInterop;

namespace Cmsify.Admin.State;

public sealed class AuthState
{
    private const string StorageKey = "cmsify.auth";
    private readonly BrowserStorage storage;
    private readonly AuthService authService;
    private bool initialized;
    private bool persisted;

    public AuthState(BrowserStorage storage, AuthService authService)
    {
        this.storage = storage;
        this.authService = authService;
    }

    public event Action? Changed;

    public string? Token { get; private set; }

    public DateTimeOffset? ExpiresAt { get; private set; }

    public UserSummary? User { get; private set; }

    public bool MustChangePassword { get; private set; }

    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(Token) && ExpiresAt > DateTimeOffset.UtcNow;

    public async Task InitializeAsync()
    {
        if (initialized)
        {
            return;
        }

        var saved = await storage.GetAsync<SavedAuth>("local", StorageKey);
        persisted = saved is not null;
        saved ??= await storage.GetAsync<SavedAuth>("session", StorageKey);
        if (saved is not null)
        {
            Token = saved.Token;
            ExpiresAt = saved.ExpiresAt;
            User = saved.User;
            MustChangePassword = saved.MustChangePassword;
            if (!IsAuthenticated)
            {
                await ClearAsync();
            }
        }

        initialized = true;
    }

    public async Task LoginAsync(string email, string password, bool rememberMe, CancellationToken ct = default)
    {
        var response = await authService.LoginAsync(email, password, ct);
        Token = response.Token;
        ExpiresAt = response.ExpiresAt;
        User = response.User;
        MustChangePassword = response.MustChangePassword;
        persisted = rememberMe;
        await StoreAsync();
        Changed?.Invoke();
    }

    public async Task ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken ct = default)
    {
        await authService.ChangePasswordAsync(Token, currentPassword, newPassword, ct);
        MustChangePassword = false;
        await StoreAsync();
        Changed?.Invoke();
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        if (IsAuthenticated)
        {
            await authService.LogoutAsync(Token, ct);
        }

        await ClearAsync();
    }

    public async Task ClearAsync()
    {
        Token = null;
        ExpiresAt = null;
        User = null;
        MustChangePassword = false;
        await storage.RemoveAsync("local", StorageKey);
        await storage.RemoveAsync("session", StorageKey);
        Changed?.Invoke();
    }

    public async Task UpdateExpiresAtAsync(DateTimeOffset expiresAt)
    {
        if (string.IsNullOrWhiteSpace(Token) || !ExpiresAt.HasValue || expiresAt <= ExpiresAt.Value)
        {
            return;
        }

        ExpiresAt = expiresAt;
        try
        {
            await StoreAsync();
        }
        catch (JSDisconnectedException)
        {
            // Session lifetime refresh is opportunistic; the next live circuit can refresh storage again.
        }
    }

    private async Task StoreAsync()
    {
        var saved = new SavedAuth(Token!, ExpiresAt!.Value, User!, MustChangePassword);
        await storage.RemoveAsync(persisted ? "session" : "local", StorageKey);
        await storage.SetAsync(persisted ? "local" : "session", StorageKey, saved);
    }

    private sealed record SavedAuth(string Token, DateTimeOffset ExpiresAt, UserSummary User, bool MustChangePassword);
}
