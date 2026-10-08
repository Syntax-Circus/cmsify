using SyntaxCircus.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Domain.ValueObjects;

namespace Cmsify.Core.EmbeddedContent;

public static class EmbeddedContractRules
{
    public static Result<EmbeddedTemplateContract> ValidateAndCopy(EmbeddedTemplateContract contract)
    {
        if (contract is null || !Bounded(contract.ContractKey, 200) || !Bounded(contract.Name, 200)
            || !SlugRules.IsValid(contract.Slug) || !Bounded(contract.TitleFieldKey, 100)
            || contract.Fields is null || contract.Fields.Count == 0)
            return Invalid();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var orders = new HashSet<int>();
        foreach (var field in contract.Fields)
        {
            if (field is null || !Bounded(field.Key, 100) || !Bounded(field.Label, 200)
                || !keys.Add(field.Key) || !orders.Add(field.Order) || field.Order < 0
                || field.PrimitiveType != PrimitiveType.Text || field.ValueKind != ValueKind.Text
                || field.CompositionMode != CompositionMode.Inline || field.IsOpen
                || field.MaxOccurrences != 1 || field.MinOccurrences != (field.IsRequired ? 1 : 0)
                || !ValidConfig(field.FieldConfig)) return Invalid();
        }
        if (!keys.Contains(contract.TitleFieldKey)) return Invalid();
        return Result<EmbeddedTemplateContract>.Success(contract with {
            Fields = Array.AsReadOnly(contract.Fields.OrderBy(f => f.Order).ThenBy(f => f.Key, StringComparer.Ordinal)
                .Select(f => f with { FieldConfig = f.FieldConfig.Clone() }).ToArray()) });
    }

    public static string Fingerprint(EmbeddedTemplateContract contract)
    {
        var validated = ValidateAndCopy(contract);
        if (validated.IsFailure) throw new ArgumentException("Invalid contract.", nameof(contract));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            var element = JsonSerializer.SerializeToElement(validated.Value);
            Canonical(writer, element);
        }
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    public static bool IsFingerprint(string? fingerprint) => fingerprint is { Length: 64 }
        && fingerprint.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool Bounded(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum;
    private static Result<EmbeddedTemplateContract> Invalid() => Result<EmbeddedTemplateContract>.Failure(
        new(EmbeddedContentErrors.Validation, "Invalid contract.", ResultErrorKind.Validation));
    private static bool ValidConfig(JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object) return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        var hasMaximum = false;
        foreach (var property in config.EnumerateObject())
        {
            if (!names.Add(property.Name)) return false;
            if (property.Name == "maxLength")
            {
                if (!property.Value.TryGetInt32(out var maximum) || maximum <= 0) return false;
                hasMaximum = true;
            }
            else if (property.Name != "formatHint" || property.Value.ValueKind != JsonValueKind.String
                || property.Value.GetString() != "plaintext") return false;
        }
        return hasMaximum;
    }
    private static void Canonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                { writer.WritePropertyName(property.Name); Canonical(writer, property.Value); }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var value in element.EnumerateArray()) Canonical(writer, value);
                writer.WriteEndArray();
                break;
            default: element.WriteTo(writer); break;
        }
    }
}
