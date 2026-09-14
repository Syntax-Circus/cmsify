using System.Text.Json;
using Shouldly;
using Xunit;

namespace SyntaxCircus.Cmsify.Components.Tests;

public sealed class ComponentValueSerializerTests
{
    [Fact]
    public void SerializesPropertiesKeyedByFieldKeyNotFieldId()
    {
        var titleField = TestComponentFactory.CreateField(key: "title", primitiveType: PrimitiveType.Text);
        var component = TestComponentFactory.Create(currentVersion: TestComponentFactory.CreateVersion(fields: [titleField]));
        var schemas = new Dictionary<Guid, ComponentResponse> { [component.Id] = component };

        var instance = new ComponentInstanceValue();
        instance.GetOrCreate(titleField.Id).TextValue = "Hello";

        var json = ComponentValueSerializer.Serialize(instance, component.Id, schemas);

        json.TryGetProperty("title", out var titleProperty).ShouldBeTrue();
        titleProperty.GetString().ShouldBe("Hello");
        json.TryGetProperty(titleField.Id.ToString(), out _).ShouldBeFalse();
        json.EnumerateObject().Select(p => p.Name).ShouldBe(["title"]);
    }

    [Fact]
    public void RoundTripsAllPrimitiveTypesThroughSerializeAndDeserialize()
    {
        var textField = TestComponentFactory.CreateField(key: "text", primitiveType: PrimitiveType.Text);
        var boolField = TestComponentFactory.CreateField(key: "flag", primitiveType: PrimitiveType.Boolean);
        var mediaField = TestComponentFactory.CreateField(key: "image", primitiveType: PrimitiveType.Media);
        var fileField = TestComponentFactory.CreateField(key: "attachment", primitiveType: PrimitiveType.File);
        var pickListSingleField = TestComponentFactory.CreateField(key: "color", primitiveType: PrimitiveType.PickList, fieldConfig: PickListConfig(multiple: false));
        var pickListMultiField = TestComponentFactory.CreateField(key: "tags", primitiveType: PrimitiveType.PickList, fieldConfig: PickListConfig(multiple: true));

        var component = TestComponentFactory.Create(currentVersion: TestComponentFactory.CreateVersion(
            fields: [textField, boolField, mediaField, fileField, pickListSingleField, pickListMultiField]));
        var schemas = new Dictionary<Guid, ComponentResponse> { [component.Id] = component };

        var mediaAssetId = Guid.NewGuid();
        var fileAssetId = Guid.NewGuid();

        var original = new ComponentInstanceValue();
        original.GetOrCreate(textField.Id).TextValue = "Some text";
        original.GetOrCreate(boolField.Id).BoolValue = true;
        original.GetOrCreate(mediaField.Id).FallbackMediaAssetId = mediaAssetId;
        original.GetOrCreate(fileField.Id).FallbackFileAssetId = fileAssetId;
        original.GetOrCreate(pickListSingleField.Id).TextValue = "red";
        original.GetOrCreate(pickListMultiField.Id).MultiValues = ["a", "b"];

        var json = ComponentValueSerializer.Serialize(original, component.Id, schemas);

        // Media/File round-trip as plain JSON strings of the asset guid.
        json.GetProperty("image").ValueKind.ShouldBe(JsonValueKind.String);
        json.GetProperty("image").GetString().ShouldBe(mediaAssetId.ToString());
        json.GetProperty("attachment").ValueKind.ShouldBe(JsonValueKind.String);
        json.GetProperty("attachment").GetString().ShouldBe(fileAssetId.ToString());
        json.GetProperty("tags").ValueKind.ShouldBe(JsonValueKind.Array);

        var decoded = ComponentValueSerializer.Deserialize(json, component.Id, schemas);

        decoded.FieldValues[textField.Id].TextValue.ShouldBe("Some text");
        decoded.FieldValues[boolField.Id].BoolValue.ShouldBeTrue();
        decoded.FieldValues[mediaField.Id].FallbackMediaAssetId.ShouldBe(mediaAssetId);
        decoded.FieldValues[fileField.Id].FallbackFileAssetId.ShouldBe(fileAssetId);
        decoded.FieldValues[pickListSingleField.Id].TextValue.ShouldBe("red");
        decoded.FieldValues[pickListMultiField.Id].MultiValues.ShouldBe(["a", "b"]);
    }

    [Fact]
    public void SerializesASingleOccurrenceNestedComponentAsABareObject()
    {
        var labelField = TestComponentFactory.CreateField(key: "label", primitiveType: PrimitiveType.Text);
        var nestedComponent = TestComponentFactory.Create(currentVersion: TestComponentFactory.CreateVersion(fields: [labelField]));

        var childField = TestComponentFactory.CreateField(key: "child", nestedComponentId: nestedComponent.Id, maxOccurrences: 1);
        var parentComponent = TestComponentFactory.Create(currentVersion: TestComponentFactory.CreateVersion(fields: [childField]));

        var schemas = new Dictionary<Guid, ComponentResponse> { [parentComponent.Id] = parentComponent, [nestedComponent.Id] = nestedComponent };

        var nestedInstance = new ComponentInstanceValue();
        nestedInstance.GetOrCreate(labelField.Id).TextValue = "Hello";

        var parentInstance = new ComponentInstanceValue();
        parentInstance.GetOrCreate(childField.Id).ComponentValues = [nestedInstance];

        var json = ComponentValueSerializer.Serialize(parentInstance, parentComponent.Id, schemas);

        json.GetProperty("child").ValueKind.ShouldBe(JsonValueKind.Object);
        json.GetProperty("child").GetProperty("label").GetString().ShouldBe("Hello");
    }

    [Fact]
    public void SerializesAMultiOccurrenceNestedComponentAsAnArray()
    {
        var labelField = TestComponentFactory.CreateField(key: "label", primitiveType: PrimitiveType.Text);
        var nestedComponent = TestComponentFactory.Create(currentVersion: TestComponentFactory.CreateVersion(fields: [labelField]));

        var childField = TestComponentFactory.CreateField(key: "children", nestedComponentId: nestedComponent.Id, maxOccurrences: null);
        var parentComponent = TestComponentFactory.Create(currentVersion: TestComponentFactory.CreateVersion(fields: [childField]));

        var schemas = new Dictionary<Guid, ComponentResponse> { [parentComponent.Id] = parentComponent, [nestedComponent.Id] = nestedComponent };

        ComponentInstanceValue BuildNestedInstance(string label)
        {
            var nestedInstance = new ComponentInstanceValue();
            nestedInstance.GetOrCreate(labelField.Id).TextValue = label;
            return nestedInstance;
        }

        var parentInstance = new ComponentInstanceValue();
        parentInstance.GetOrCreate(childField.Id).ComponentValues = [BuildNestedInstance("A"), BuildNestedInstance("B")];

        var json = ComponentValueSerializer.Serialize(parentInstance, parentComponent.Id, schemas);

        json.GetProperty("children").ValueKind.ShouldBe(JsonValueKind.Array);
        json.GetProperty("children").EnumerateArray().Select(e => e.GetProperty("label").GetString()).ShouldBe(["A", "B"]);
    }

    [Fact]
    public void DeserializesANestedComponentFromABareObjectShape()
    {
        var labelField = TestComponentFactory.CreateField(key: "label", primitiveType: PrimitiveType.Text);
        var nestedComponent = TestComponentFactory.Create(currentVersion: TestComponentFactory.CreateVersion(fields: [labelField]));

        var childField = TestComponentFactory.CreateField(key: "child", nestedComponentId: nestedComponent.Id, maxOccurrences: 1);
        var parentComponent = TestComponentFactory.Create(currentVersion: TestComponentFactory.CreateVersion(fields: [childField]));

        var schemas = new Dictionary<Guid, ComponentResponse> { [parentComponent.Id] = parentComponent, [nestedComponent.Id] = nestedComponent };

        var json = JsonDocument.Parse("""{ "child": { "label": "Hello" } }""").RootElement;

        var instance = ComponentValueSerializer.Deserialize(json, parentComponent.Id, schemas);

        var childValue = instance.FieldValues[childField.Id];
        childValue.ComponentValues.Count.ShouldBe(1);
        childValue.ComponentValues[0].FieldValues[labelField.Id].TextValue.ShouldBe("Hello");
    }

    [Fact]
    public void DeserializesANestedComponentFromAnArrayShape()
    {
        var labelField = TestComponentFactory.CreateField(key: "label", primitiveType: PrimitiveType.Text);
        var nestedComponent = TestComponentFactory.Create(currentVersion: TestComponentFactory.CreateVersion(fields: [labelField]));

        var childField = TestComponentFactory.CreateField(key: "children", nestedComponentId: nestedComponent.Id, maxOccurrences: null);
        var parentComponent = TestComponentFactory.Create(currentVersion: TestComponentFactory.CreateVersion(fields: [childField]));

        var schemas = new Dictionary<Guid, ComponentResponse> { [parentComponent.Id] = parentComponent, [nestedComponent.Id] = nestedComponent };

        var json = JsonDocument.Parse("""{ "children": [ { "label": "A" }, { "label": "B" } ] }""").RootElement;

        var instance = ComponentValueSerializer.Deserialize(json, parentComponent.Id, schemas);

        var childValue = instance.FieldValues[childField.Id];
        childValue.ComponentValues.Count.ShouldBe(2);
        childValue.ComponentValues
            .Select(nested => nested.FieldValues[labelField.Id].TextValue)
            .ShouldBe(["A", "B"]);
    }

    [Fact]
    public void IgnoresAnUnknownJsonPropertyOnDecodeWithoutThrowing()
    {
        var titleField = TestComponentFactory.CreateField(key: "title", primitiveType: PrimitiveType.Text);
        var component = TestComponentFactory.Create(currentVersion: TestComponentFactory.CreateVersion(fields: [titleField]));
        var schemas = new Dictionary<Guid, ComponentResponse> { [component.Id] = component };

        var json = JsonDocument.Parse("""{ "title": "Hello", "mystery": "unexpected" }""").RootElement;

        var instance = ComponentValueSerializer.Deserialize(json, component.Id, schemas);

        instance.FieldValues[titleField.Id].TextValue.ShouldBe("Hello");
    }

    [Fact]
    public void DecodesASchemaFieldMissingFromTheJsonToAnEmptyValueWithoutThrowing()
    {
        var titleField = TestComponentFactory.CreateField(key: "title", primitiveType: PrimitiveType.Text);
        var subtitleField = TestComponentFactory.CreateField(key: "subtitle", primitiveType: PrimitiveType.Text);
        var component = TestComponentFactory.Create(currentVersion: TestComponentFactory.CreateVersion(fields: [titleField, subtitleField]));
        var schemas = new Dictionary<Guid, ComponentResponse> { [component.Id] = component };

        var json = JsonDocument.Parse("""{ "title": "Hello" }""").RootElement;

        var instance = ComponentValueSerializer.Deserialize(json, component.Id, schemas);

        instance.FieldValues[titleField.Id].TextValue.ShouldBe("Hello");
        instance.FieldValues[subtitleField.Id].TextValue.ShouldBeNull();
    }

    [Fact]
    public void DeserializeReturnsAnEmptyInstanceWhenTheSchemaIsUnresolved()
    {
        var json = JsonDocument.Parse("""{ "title": "Hello" }""").RootElement;

        var instance = ComponentValueSerializer.Deserialize(json, Guid.NewGuid(), new Dictionary<Guid, ComponentResponse>());

        instance.FieldValues.ShouldBeEmpty();
    }

    [Fact]
    public void SerializeReturnsAnEmptyJsonObjectWhenTheSchemaIsUnresolved()
    {
        var json = ComponentValueSerializer.Serialize(new ComponentInstanceValue(), Guid.NewGuid(), new Dictionary<Guid, ComponentResponse>());

        json.ValueKind.ShouldBe(JsonValueKind.Object);
        json.EnumerateObject().ShouldBeEmpty();
    }

    private static JsonElement PickListConfig(bool multiple) =>
        JsonDocument.Parse($$"""
            { "picklistId": "{{Guid.NewGuid()}}", "picklistRevisionId": "{{Guid.NewGuid()}}", "multiple": {{(multiple ? "true" : "false")}} }
            """).RootElement;
}
