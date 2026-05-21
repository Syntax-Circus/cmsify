namespace Cmsify.Admin.State;

public sealed class ToastState
{
    public event Action? Changed;

    public string? Message { get; private set; }

    public string Variant { get; private set; } = "success";

    public int Version { get; private set; }

    public void Success(string message) => Show(message, "success");

    public void Danger(string message) => Show(message, "danger");

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
