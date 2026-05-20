using System.Text.Json;
using System.Xml;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;

namespace Cmsify.Core.Services;

public static class TextFormatHints
{
    public const string ConfigKey = "formatHint";
    public const string LanguageConfigKey = "formatLanguage";
    public const string ValidateFormatConfigKey = "validateFormat";

    private static readonly Dictionary<string, TextFormatHint> Lookup = Enum
        .GetValues<TextFormatHint>()
        .ToDictionary(value => value.ToString(), value => value, StringComparer.OrdinalIgnoreCase);

    public static bool TryParse(string? value, out TextFormatHint hint)
    {
        if (!string.IsNullOrWhiteSpace(value) && Lookup.TryGetValue(value, out var parsed))
        {
            hint = parsed;
            return true;
        }

        hint = TextFormatHint.PlainText;
        return false;
    }

    public static TextFormatHint GetEffectiveHint(JsonElement? config)
    {
        if (config is null || config.Value.ValueKind != JsonValueKind.Object)
        {
            return TextFormatHint.PlainText;
        }

        if (!config.Value.TryGetProperty(ConfigKey, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return TextFormatHint.PlainText;
        }

        return TryParse(element.GetString(), out var hint) ? hint : TextFormatHint.PlainText;
    }

    public static bool ShouldValidateFormat(JsonElement? config)
    {
        if (config is null || config.Value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return config.Value.TryGetProperty(ValidateFormatConfigKey, out var element)
            && element.ValueKind == JsonValueKind.True;
    }

    public static bool IsSearchIndexable(TextFormatHint hint) => hint switch
    {
        TextFormatHint.PlainText or TextFormatHint.Markdown or TextFormatHint.Html => true,
        _ => false
    };

    public static string ToMimeType(TextFormatHint hint) => hint switch
    {
        TextFormatHint.Html => "text/html",
        TextFormatHint.Markdown => "text/markdown",
        TextFormatHint.Json => "application/json",
        TextFormatHint.Xml => "application/xml",
        TextFormatHint.Yaml => "application/yaml",
        TextFormatHint.Csv => "text/csv",
        TextFormatHint.Toml => "application/toml",
        TextFormatHint.Sql => "application/sql",
        TextFormatHint.Code => "text/plain",
        TextFormatHint.Url => "text/uri-list",
        TextFormatHint.Email => "text/plain",
        TextFormatHint.Regex => "text/plain",
        _ => "text/plain"
    };

    public static bool TryValidateValue(TextFormatHint hint, string? value, out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        try
        {
            switch (hint)
            {
                case TextFormatHint.Json:
                    using (JsonDocument.Parse(value)) { }
                    return true;

                case TextFormatHint.Xml:
                    var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                    using (var reader = XmlReader.Create(new StringReader(value), settings))
                    {
                        while (reader.Read()) { }
                    }

                    return true;

                case TextFormatHint.Url:
                    if (!Uri.TryCreate(value, UriKind.Absolute, out _))
                    {
                        error = "Value is not a valid absolute URL.";
                        return false;
                    }

                    return true;

                case TextFormatHint.Email:
                    var trimmed = value.Trim();
                    var at = trimmed.IndexOf('@', StringComparison.Ordinal);
                    if (at <= 0 || at == trimmed.Length - 1 || trimmed.IndexOf('@', at + 1) >= 0 || trimmed.Contains(' ', StringComparison.Ordinal))
                    {
                        error = "Value is not a valid email address.";
                        return false;
                    }

                    return true;

                case TextFormatHint.Regex:
                    _ = new System.Text.RegularExpressions.Regex(value);
                    return true;

                default:
                    return true;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool AppliesTo(PrimitiveType type) => type == PrimitiveType.Text;

    public static TextFormatHint GetEffectiveHint(TemplateField field) =>
        AppliesTo(field.PrimitiveType ?? PrimitiveType.Separator)
            ? GetEffectiveHint(field.FieldConfig)
            : TextFormatHint.PlainText;
}
