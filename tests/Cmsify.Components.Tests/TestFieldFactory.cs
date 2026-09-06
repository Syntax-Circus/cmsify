using System.Text.Json;

namespace SyntaxCircus.Cmsify.Components.Tests;

internal static class TestFieldFactory
{
    public static TemplateFieldResponse Create(
        PrimitiveType? primitiveType = null,
        bool isRequired = false,
        Guid? componentId = null,
        Guid? templateId = null,
        bool isOpen = false,
        CompositionMode compositionMode = CompositionMode.Reference,
        JsonElement? fieldConfig = null,
        int? maxOccurrences = null,
        IReadOnlyList<TemplateFieldAllowedTypeResponse>? allowedTypes = null) =>
        new(
            Guid.NewGuid(),
            null,
            "field-key",
            "Field Label",
            null,
            0,
            isRequired,
            0,
            maxOccurrences,
            isOpen,
            compositionMode,
            primitiveType,
            templateId,
            allowedTypes ?? [],
            fieldConfig,
            componentId);
}
