using System.Text;
using Cmsify.Core.Domain.ValueObjects;

namespace Cmsify.Admin.Services;

public static class SlugFormatter
{
    public static string FromDisplayName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(value.Length, SlugRules.MaxLength));
        char pendingSeparator = '\0';
        foreach (var character in value)
        {
            if (character is >= 'A' and <= 'Z')
            {
                AppendAlphanumeric(builder, char.ToLowerInvariant(character), ref pendingSeparator);
            }
            else if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                AppendAlphanumeric(builder, character, ref pendingSeparator);
            }
            else if (character is '-' or '_')
            {
                pendingSeparator = pendingSeparator == '\0' ? character : '-';
            }
            else
            {
                pendingSeparator = '-';
            }
        }

        return builder.ToString();
    }

    private static void AppendAlphanumeric(StringBuilder builder, char character, ref char pendingSeparator)
    {
        if (pendingSeparator != '\0' && builder.Length > 0 && builder.Length < SlugRules.MaxLength - 1)
        {
            builder.Append(pendingSeparator);
        }

        if (builder.Length < SlugRules.MaxLength)
        {
            builder.Append(character);
        }

        pendingSeparator = '\0';
    }
}
