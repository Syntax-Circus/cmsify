using Cmsify.Api.Controllers;
using Cmsify.Core.Workspaces;
using Microsoft.AspNetCore.Mvc;
using Shouldly;

namespace Cmsify.Api.Integration.Tests;

public sealed class WorkspaceAdapterBoundaryTests
{
    [Theory]
    [InlineData("List", typeof(IWorkspacesListRequestHandler))]
    [InlineData("Create", typeof(IWorkspacesCreateRequestHandler))]
    [InlineData("Get", typeof(IWorkspacesGetRequestHandler))]
    [InlineData("Update", typeof(IWorkspacesUpdateRequestHandler))]
    [InlineData("Delete", typeof(IWorkspacesDeleteRequestHandler))]
    public void Action_InjectsItsWorkflowThroughFromServices(string action, Type contract)
    {
        var method = typeof(WorkspacesController).GetMethod(action)!;
        var parameter = method.GetParameters().SingleOrDefault(parameter => parameter.ParameterType == contract);
        parameter.ShouldNotBeNull();
        parameter.GetCustomAttributes(typeof(FromServicesAttribute), false).Length.ShouldBe(1);
        typeof(WorkspacesController).GetConstructors().Single().GetParameters().ShouldBeEmpty();
    }
}
