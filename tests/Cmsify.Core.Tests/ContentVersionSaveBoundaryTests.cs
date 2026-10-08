using Cmsify.Core.ContentWrites;
using Cmsify.Core.Interfaces.Services;
using Shouldly;
using System.Reflection;

namespace Cmsify.Core.Tests;

public sealed class ContentVersionSaveBoundaryTests
{
    [Fact]
    public void ApplicationAssemblyHasNoTransportOrPersistenceReferences()
    {
        var references = typeof(IUpdateContentVersionRequestHandler).Assembly.GetReferencedAssemblies();
        references.ShouldNotContain(reference => IsForbidden(reference.Name ?? ""));
        var contracts = typeof(IUpdateContentVersionRequestHandler).Assembly.GetExportedTypes()
            .Where(type => type.Namespace == "Cmsify.Core.ContentWrites");
        foreach (var type in contracts)
        {
            foreach (var property in type.GetProperties()) AssertAllowed(property.PropertyType);
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                AssertAllowed(method.ReturnType);
                foreach (var parameter in method.GetParameters()) AssertAllowed(parameter.ParameterType);
            }
        }
        typeof(UpdateContentVersionRequestHandler).GetConstructors().Single(c => c.GetParameters().Length == 5).GetParameters()
            .Select(parameter => parameter.ParameterType).ShouldBe(new[] { typeof(IContentVersionEditRepository),
                typeof(ICurrentActor), typeof(IWorkspaceAuthorizationService), typeof(TimeProvider), typeof(IContentVersionResourceGuard) });
        typeof(UpdateContentVersionRequestHandler).GetConstructors().Single(c => c.GetParameters().Length == 5).GetParameters().Last().IsOptional.ShouldBeTrue();
        typeof(UpdateContentVersionRequestHandler).GetConstructor([typeof(IContentVersionEditRepository),
            typeof(ICurrentActor), typeof(IWorkspaceAuthorizationService), typeof(TimeProvider)]).ShouldNotBeNull();
        typeof(IUpdateContentVersionRequestHandler).IsAssignableFrom(typeof(UpdateContentVersionRequestHandler)).ShouldBeTrue();
        typeof(IAsyncDisposable).IsAssignableFrom(typeof(IContentVersionEditSession)).ShouldBeTrue();
    }

    private static void AssertAllowed(Type type)
    {
        IsForbidden(type.FullName ?? "").ShouldBeFalse();
        (type.Namespace == "Cmsify.Core.Domain.Entities").ShouldBeFalse();
        if (type.HasElementType) AssertAllowed(type.GetElementType()!);
        foreach (var argument in type.GetGenericArguments()) AssertAllowed(argument);
    }
    private static bool IsForbidden(string name) => new[] { "Cmsify.Api", "Cmsify.Contracts", "Cmsify.Infrastructure",
        "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "System.Net.Http" }.Any(name.Contains);
}
