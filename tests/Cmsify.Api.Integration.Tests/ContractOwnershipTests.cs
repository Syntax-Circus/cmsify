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
