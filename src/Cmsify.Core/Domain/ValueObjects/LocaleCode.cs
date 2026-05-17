using System.Globalization;

namespace Cmsify.Core.Domain.ValueObjects;

public readonly record struct LocaleCode
{
    public LocaleCode(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Locale code cannot be empty.", nameof(value));
        }

        try
        {
            _ = CultureInfo.GetCultureInfo(value);
        }
        catch (CultureNotFoundException ex)
        {
            throw new ArgumentException("Locale code must be a valid BCP-47 culture name.", nameof(value), ex);
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}
