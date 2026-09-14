using System.Text.Json;

namespace SyntaxCircus.Cmsify.Components.Tests;

internal static class TestComponentFactory
{
    public static ComponentFieldResponse CreateField(
        string key = "field-key",
        string label = "Field Label",
        string? helpText = null,
        PrimitiveType? primitiveType = null,
        Guid? nestedComponentId = null,
        bool isRequired = false,
        int minOccurrences = 0,
        int? maxOccurrences = null,
        JsonElement? fieldConfig = null) =>
        new(
            Guid.NewGuid(),
            key,
            label,
            helpText,
            0,
            isRequired,
            minOccurrences,
            maxOccurrences,
            primitiveType,
            nestedComponentId,
            fieldConfig);

    public static ComponentVersionResponse CreateVersion(
        Guid? id = null,
        Guid? componentId = null,
        int versionNumber = 1,
        TemplateVersionStatus status = TemplateVersionStatus.Published,
        IReadOnlyList<ComponentFieldResponse>? fields = null) =>
        new(
            id ?? Guid.NewGuid(),
            componentId ?? Guid.NewGuid(),
            versionNumber,
            status,
            null,
            null,
            fields ?? []);

    public static ComponentResponse Create(
        Guid? id = null,
        Guid? workspaceId = null,
        string name = "Component",
        string slug = "component",
        ComponentVersionResponse? currentVersion = null)
    {
        var componentId = id ?? Guid.NewGuid();
        return new ComponentResponse(
            componentId,
            workspaceId ?? Guid.NewGuid(),
            name,
            slug,
            null,
            currentVersion ?? CreateVersion(componentId: componentId));
    }
}
