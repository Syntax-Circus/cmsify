namespace Cmsify.Admin.State;

public sealed class ToastState(Cmsify.Admin.Auth.SessionExpiryHandler? sessionExpiry = null)
{
    public event Action? Changed;

    public string? Message { get; private set; }

    public string Variant { get; private set; } = "success";

    public int Version { get; private set; }

    public void Success(string message) => Show(message, "success");

    // Once the session-expired redirect is under way, error toasts caused by the dead session are noise.
    public void Danger(string message)
    {
        if (sessionExpiry?.HasFired == true)
        {
            return;
        }

        Show(message, "danger");
    }

    public void Clear()
    {
        Message = null;
        Changed?.Invoke();
    }

    private void Show(string message, string variant)
    {
        Message = message;
        Variant = variant;
        Version++;
        Changed?.Invoke();
    }
}
