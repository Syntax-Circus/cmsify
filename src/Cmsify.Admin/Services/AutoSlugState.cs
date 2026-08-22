namespace Cmsify.Admin.Services;

public sealed class AutoSlugState
{
    private bool isManual;

    public AutoSlugState(string? value = null, bool isManual = false)
    {
        Value = value ?? string.Empty;
        this.isManual = isManual;
    }

    public string Value { get; private set; }

    public void UpdateFromName(string? name)
    {
        if (!isManual)
        {
            Value = SlugFormatter.FromDisplayName(name);
        }
    }

    public void SetManually(string? value)
    {
        Value = value ?? string.Empty;
        isManual = !string.IsNullOrWhiteSpace(Value);
    }

    public void EnsureDefault(string? name) => UpdateFromName(name);
}
