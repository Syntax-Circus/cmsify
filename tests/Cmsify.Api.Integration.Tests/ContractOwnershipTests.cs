using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using SyntaxCircus.Cmsify.Contracts;

namespace Cmsify.Api.Integration.Tests;

public sealed class ContractOwnershipTests
{
    private static readonly Assembly ApiAssembly = typeof(Program).Assembly;
    private static readonly Assembly ContractsAssembly = typeof(PagedResponse<>).Assembly;

    [Fact]
    public void PublicControllerWireTypes_AreOwnedByContracts()
    {
        var controllerNamespaceTypes = ApiAssembly
            .GetExportedTypes()
            .Where(type => type.Namespace == "Cmsify.Api.Controllers")
            .Where(type => !typeof(ControllerBase).IsAssignableFrom(type))
            .Select(type => type.FullName)
            .Order()
            .ToArray();

        Assert.Empty(controllerNamespaceTypes);

        var actionPayloadTypes = ApiAssembly
            .GetExportedTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type))
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            .Where(method => method.GetCustomAttributes(inherit: true).OfType<IActionHttpMethodProvider>().Any())
            .SelectMany(GetPayloadTypes)
            .Distinct()
            .Where(type => !IsAllowedPayloadType(type))
            .Select(type => type.FullName)
            .Order()
            .ToArray();

        Assert.Empty(actionPayloadTypes);
    }

    [Fact]
    public void CtpImportContract_PreservesRequiredPickListsAndStringEnums()
    {
        var pickListsProperty = typeof(PackageImportResponse).GetProperty(nameof(PackageImportResponse.PickLists));
        Assert.NotNull(pickListsProperty);
        Assert.Equal(NullabilityState.NotNull, new NullabilityInfoContext().Create(pickListsProperty).ReadState);

        var pickListsParameter = Assert.Single(
            typeof(PackageImportResponse).GetConstructors().Single().GetParameters(),
            parameter => parameter.Name == "PickLists");
        Assert.False(pickListsParameter.HasDefaultValue);

        var manifest = new CtpPackageManifest(
            "1.1", "example", "sample", "1.0.0", "Sample", null, null, null, null,
            [new CtpTemplate("article", "Article", null, [],
                [new CtpField("title", "Title", null, 0, true, 1, 1, false, CompositionMode.Inline, PrimitiveType.Text, null, null)])]);
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(manifest, options));
        var field = json.RootElement.GetProperty("templates")[0].GetProperty("fields")[0];

        Assert.Equal("Inline", field.GetProperty("compositionMode").GetString());
        Assert.Equal("Text", field.GetProperty("primitiveType").GetString());
    }

    [Fact]
    public void BoundaryMappings_CoverEveryPublicAuditAction()
    {
        var mappings = ApiAssembly.GetType("Cmsify.Api.Controllers.ContractMappings");
        Assert.NotNull(mappings);
        var methods = mappings.GetMethods(BindingFlags.Static | BindingFlags.Public);
        var toCore = Assert.Single(methods, method =>
            method.Name == "ToCore" && method.GetParameters() is [{ ParameterType: var parameterType }] && parameterType == typeof(AuditAction));
        var toContract = Assert.Single(methods, method =>
            method.Name == "ToContract" && method.ReturnType == typeof(AuditAction) && method.GetParameters() is [{ ParameterType: var parameterType }] && parameterType == typeof(Cmsify.Core.Domain.Enums.AuditAction));

        foreach (var action in Enum.GetValues<AuditAction>())
        {
            var core = Assert.IsType<Cmsify.Core.Domain.Enums.AuditAction>(toCore.Invoke(null, [action]));
            Assert.Equal(action.ToString(), core.ToString());
            Assert.Equal(action, Assert.IsType<AuditAction>(toContract.Invoke(null, [core])));
        }
    }

    private static IEnumerable<Type> GetPayloadTypes(MethodInfo method) =>
        method.GetParameters().SelectMany(parameter => FlattenPayloadType(parameter.ParameterType))
            .Concat(FlattenPayloadType(method.ReturnType));

    private static IEnumerable<Type> FlattenPayloadType(Type type)
    {
        if (type.IsByRef || type.IsPointer)
        {
            yield break;
        }

        if (type.IsArray)
        {
            foreach (var item in FlattenPayloadType(type.GetElementType()!))
            {
                yield return item;
            }

            yield break;
        }

        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
            {
                foreach (var item in FlattenPayloadType(argument))
                {
                    yield return item;
                }
            }

            yield break;
        }

        yield return type;
    }

    private static bool IsAllowedPayloadType(Type type) =>
        type.Assembly == ContractsAssembly ||
        type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true ||
        type.Namespace?.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) == true ||
        type.Namespace?.StartsWith("Microsoft.Net", StringComparison.Ordinal) == true ||
        type == typeof(void);
}
