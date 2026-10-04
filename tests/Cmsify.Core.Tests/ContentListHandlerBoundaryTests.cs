using System.Reflection;
using Cmsify.Core.ContentQueries;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Interfaces.Services;

namespace Cmsify.Core.Tests;

public sealed class ContentListHandlerBoundaryTests
{
    [Fact]
    public void HandlerAndRepository_ExposeOnlyApplicationContracts()
    {
        var assembly = typeof(ListContentRequest).Assembly;
        Assert.True(typeof(IListContentRequestHandler).IsAssignableFrom(typeof(ListContentRequestHandler)));
        Assert.Equal(new[] { typeof(IContentListQueryRepository), typeof(ICurrentActor), typeof(IWorkspaceAuthorizationService), typeof(TimeProvider) },
            typeof(ListContentRequestHandler).GetConstructors().Single().GetParameters().Select(parameter => parameter.ParameterType));
        foreach (var type in assembly.GetExportedTypes().Where(type => type.Namespace == "Cmsify.Core.ContentQueries"))
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)) AssertPayload(property.PropertyType);
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                         .Where(method => method.Name.EndsWith("Async", StringComparison.Ordinal)))
            {
                foreach (var parameter in method.GetParameters()) AssertPayload(parameter.ParameterType);
                AssertPayload(method.ReturnType);
            }
        }
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name!.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
            || reference.Name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
            || reference.Name.Contains("Contracts", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(typeof(HttpWrapper))]
    [InlineData(typeof(EntityWrapper))]
    public void PayloadGuard_RejectsForbiddenTypesInsideOwnedWrappers(Type type)
        => Assert.Throws<InvalidOperationException>(() => AssertPayload(type, type.Assembly));

    private static void AssertPayload(Type type, Assembly? owner = null, HashSet<Type>? visited = null)
    {
        owner ??= typeof(ListContentRequest).Assembly;
        visited ??= [];
        if (!visited.Add(type)) return;
        if (type.IsPrimitive || type == typeof(string) || type == typeof(Guid) || type == typeof(DateTimeOffset)
            || type == typeof(CancellationToken)) return;
        if (Nullable.GetUnderlyingType(type) is { } underlying) { AssertPayload(underlying, owner, visited); return; }
        if (type.IsArray) { AssertPayload(type.GetElementType()!, owner, visited); return; }
        var wrapper = type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(Task<>)
            || type.GetGenericTypeDefinition() == typeof(SyntaxCircus.Common.Result<>)
            || type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>));
        if (!wrapper && (type.Assembly != owner || type.Namespace?.StartsWith("Cmsify.Core.Domain.Entities", StringComparison.Ordinal) == true))
            throw new InvalidOperationException($"Forbidden application payload: {type.FullName}");
        foreach (var argument in type.GetGenericArguments()) AssertPayload(argument, owner, visited);
        if (wrapper || type.IsEnum) return;
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)) AssertPayload(property.PropertyType, owner, visited);
    }

    private sealed record HttpWrapper(HttpRequestMessage Request);
    private sealed record EntityWrapper(IReadOnlyList<ContentItem> Items);
}
