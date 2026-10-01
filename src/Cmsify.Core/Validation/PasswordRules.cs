using System.Globalization;
using System.Text;

namespace Cmsify.Core.Validation;

/// <summary>
/// Rules applied whenever a password is SET (change, admin reset, create user, seeding). They are never
/// applied at login, so existing passwords that violate them can still sign in.
/// </summary>
public static class PasswordRules
{
    public const string ValidationMessage = "Password must not start or end with whitespace or contain invisible/control characters. Please retype it rather than pasting.";

    public static bool IsValid(string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return false;
        }

        if (char.IsWhiteSpace(password[0]) || char.IsWhiteSpace(password[^1]))
        {
            return false;
        }

        // Enumerate runes so surrogate pairs (emoji) are classified as one scalar value, not two lone surrogates.
        foreach (var rune in password.EnumerateRunes())
        {
            switch (Rune.GetUnicodeCategory(rune))
            {
                case UnicodeCategory.Control:
                case UnicodeCategory.Format:
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                    return false;
                case UnicodeCategory.SpaceSeparator when rune.Value != ' ':
                    return false;
            }
        }

        return true;
    }
}
