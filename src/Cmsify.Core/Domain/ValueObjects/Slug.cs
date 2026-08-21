using System.Text.RegularExpressions;

namespace Cmsify.Core.Domain.ValueObjects;

public readonly partial record struct Slug
{
    public Slug(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Slug cannot be empty.", nameof(value));
        }

        if (!SlugRules.IsValid(value))
        {
            throw new ArgumentException(SlugRules.ValidationMessage, nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public static partial class SlugRules
{
    public const int MaxLength = 100;
    public const string ValidationMessage = "Slug must be 1 to 100 lowercase letters or digits, with single hyphen or underscore separators between alphanumeric segments.";

    public static bool IsValid(string? value) =>
        !string.IsNullOrEmpty(value)
        && value.Length <= MaxLength
        && SlugPattern().IsMatch(value);

    [GeneratedRegex("^[a-z0-9]+(?:[-_][a-z0-9]+)*$")]
    private static partial Regex SlugPattern();
}
