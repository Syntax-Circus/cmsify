using System.Reflection;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Interfaces.Repositories;
using Cmsify.Core.Interfaces.Services;
using Shouldly;

namespace Cmsify.Core.Tests;

public sealed class WorkspaceHandlerBoundaryTests
{
    [Theory]
    [InlineData("List")]
    [InlineData("Create")]
    [InlineData("Get")]
    [InlineData("Update")]
    [InlineData("Delete")]
    public void Workflow_ExposesAnApplicationOwnedHandler(string operation)
    {
        var assembly = typeof(ICurrentActor).Assembly;
        var contract = assembly.GetType($"Cmsify.Core.Workspaces.IWorkspaces{operation}RequestHandler");
        contract.ShouldNotBeNull();
        contract.IsInterface.ShouldBeTrue();
        var handler = assembly.GetType($"Cmsify.Core.Workspaces.Workspaces{operation}RequestHandler");
        handler.ShouldNotBeNull();
        contract.IsAssignableFrom(handler).ShouldBeTrue();
        foreach (var parameter in handler.GetConstructors().Single().GetParameters())
        {
            parameter.ParameterType.IsInterface.ShouldBeTrue();
            parameter.ParameterType.Assembly.ShouldBe(assembly);
        }
        var method = contract.GetMethod("HandleAsync");
        method.ShouldNotBeNull();
        method.GetParameters()[0].ParameterType.Assembly.ShouldBe(assembly);
        AssertApplicationPayload(method.GetParameters()[0].ParameterType, assembly);
        method.GetParameters()[1].ParameterType.ShouldBe(typeof(CancellationToken));
        var result = method.ReturnType.GetGenericArguments()[0];
        result.Assembly.GetName().Name.ShouldBe("SyntaxCircus.Common");
        foreach (var output in result.GetGenericArguments())
        {
            output.Assembly.ShouldBe(assembly);
            AssertApplicationPayload(output, assembly);
        }
    }

    [Theory]
    [InlineData(typeof(HttpPayloadWrapper), typeof(HttpRequestMessage))]
    [InlineData(typeof(PersistencePayloadWrapper), typeof(Workspace))]
    [InlineData(typeof(PagedResult<HttpRequestMessage>), typeof(HttpRequestMessage))]
    [InlineData(typeof(PagedResult<Workspace>), typeof(Workspace))]
    public void PayloadGuard_RejectsForbiddenPayloadHiddenByOwnedWrapper(Type wrapper, Type forbidden)
    {
        var exception = Should.Throw<ShouldAssertException>(() => AssertApplicationPayload(wrapper, wrapper.Assembly));
        exception.Message.ShouldContain(forbidden.FullName!);
    }

    [Fact]
    public void PayloadGuard_AllowsScalarsNullableCollectionsAndCycles()
    {
        AssertApplicationPayload(typeof(ScalarPayloadWrapper), typeof(ScalarPayloadWrapper).Assembly);
        AssertApplicationPayload(typeof(PagedResult<WorkspaceDto>), typeof(ICurrentActor).Assembly);
    }

    private static void AssertApplicationPayload(Type type, Assembly applicationAssembly)
        => AssertApplicationPayload(type, applicationAssembly, new HashSet<Type>());

    private static void AssertApplicationPayload(Type type, Assembly applicationAssembly, HashSet<Type> visited)
    {
        if (!visited.Add(type)) return;
        if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal) || type == typeof(Guid)
            || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(DateOnly)
            || type == typeof(TimeOnly) || type == typeof(TimeSpan)) return;

        if (type.HasElementType)
        {
            type.IsArray.ShouldBeTrue($"Only arrays may wrap payload elements: {type.FullName}");
            AssertApplicationPayload(type.GetElementType()!, applicationAssembly, visited);
            return;
        }

        var nullable = Nullable.GetUnderlyingType(type);
        if (nullable is not null)
        {
            AssertApplicationPayload(nullable, applicationAssembly, visited);
            return;
        }

        var collection = type.IsGenericType && type.Namespace == "System.Collections.Generic"
            && typeof(System.Collections.IEnumerable).IsAssignableFrom(type);
        if (!collection)
        {
            (type.Namespace?.StartsWith("Cmsify.Core.Domain.Entities", StringComparison.Ordinal) == true)
                .ShouldBeFalse($"Persistence entity cannot be an application payload: {type.FullName}");
            type.Assembly.ShouldBe(applicationAssembly, $"Payload must be application-owned: {type.FullName}");
        }

        foreach (var argument in type.GetGenericArguments())
            AssertApplicationPayload(argument, applicationAssembly, visited);
        if (collection || type.IsEnum) return;
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            AssertApplicationPayload(property.PropertyType, applicationAssembly, visited);
    }

    private sealed record HttpPayloadWrapper(HttpRequestMessage Request);
    private sealed record PersistencePayloadWrapper(IReadOnlyList<Workspace[]> Items);
    private sealed record ScalarPayloadWrapper(Guid? Id, long? Revision, decimal Amount, DateTimeOffset CreatedAt,
        IReadOnlyList<string> Names, int[] Counts, ScalarPayloadWrapper? Next);
}
