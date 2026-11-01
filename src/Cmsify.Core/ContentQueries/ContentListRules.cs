namespace Cmsify.Core.ContentQueries;

public static class ContentListRules
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;
    public const string CreatedAtSort = "createdAt";
    public const string UpdatedAtSort = "updatedAt";
    public const string SlugSort = "slug";
    public const string InvalidFieldsCode = "invalid-fields";
    public const int MaxFieldKeys = 10;
    public const int MaxFieldKeyLength = 100;

    /// <summary>
    /// Parses the optional comma-separated <c>fields</c> projection. Null/blank means "no projection".
    /// Keys follow the template field-key shape, are de-duplicated, and are capped at <see cref="MaxFieldKeys"/>.
    /// </summary>
    public static bool TryParseFieldKeys(string? raw, out IReadOnlyList<string>? keys, out string? error)
    {
        keys = null;
        error = null;
        if (string.IsNullOrWhiteSpace(raw)) return true;
        var parsed = new List<string>();
        foreach (var part in raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Length > MaxFieldKeyLength || !IsFieldKey(part))
            {
                error = $"Invalid field key '{(part.Length > 40 ? part[..40] + "..." : part)}'.";
                return false;
            }
            if (!parsed.Contains(part, StringComparer.Ordinal)) parsed.Add(part);
        }
        if (parsed.Count > MaxFieldKeys)
        {
            error = $"At most {MaxFieldKeys} field keys may be requested.";
            return false;
        }
        keys = parsed.Count == 0 ? null : parsed.AsReadOnly();
        return true;
    }

    private static bool IsFieldKey(string value) =>
        char.IsAsciiLetter(value[0]) && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
}
