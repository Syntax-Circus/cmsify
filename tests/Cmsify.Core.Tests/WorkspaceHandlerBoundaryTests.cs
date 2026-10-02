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
        method.GetParameters()[1].ParameterType.ShouldBe(typeof(CancellationToken));
        var result = method.ReturnType.GetGenericArguments()[0];
        result.Assembly.GetName().Name.ShouldBe("SyntaxCircus.Common");
        foreach (var output in result.GetGenericArguments())
        {
            output.Assembly.ShouldBe(assembly);
        }
    }
}
