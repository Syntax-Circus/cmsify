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

        if (!SlugPattern().IsMatch(value))
        {
            throw new ArgumentException("Slug must contain only lowercase letters, numbers, and hyphens.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    private static partial Regex SlugPattern();
}
