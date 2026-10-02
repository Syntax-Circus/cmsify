using Cmsify.Api.Controllers;
using Cmsify.Core.Workspaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Shouldly;
using SyntaxCircus.Common;
using SyntaxCircus.Cmsify.Contracts;

namespace Cmsify.Api.Integration.Tests;

public sealed class WorkspaceAdapterTests
{
    private readonly WorkspaceOutput _workspace = new(Guid.NewGuid(), "Workspace", "workspace", null,
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, true);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static WorkspacesController Controller() => new() { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };

    [Theory]
    [InlineData("\"621355968000000000\"", true)]
    [InlineData("621355968000000000", false)]
    [InlineData("W/\"621355968000000000\"", false)]
    [InlineData("*", false)]
    [InlineData("\"0621355968000000000\"", false)]
    [InlineData("\"+621355968000000000\"", false)]
    [InlineData(" \"621355968000000000\"", false)]
    [InlineData("\"621355968000000000\" ", false)]
    [InlineData("\" 621355968000000000\"", false)]
    [InlineData("\"621355968000000000\",\"621355968000000000\"", false)]
    [InlineData("\"9223372036854775808\"", false)]
    [InlineData("", false)]
    public async Task MutationAdapters_OnlyForwardCanonicalQuotedTickRevision(string header, bool accepted)
    {
        var controller = Controller();
        controller.Request.Headers.IfMatch = header;
        var update = Substitute.For<IWorkspacesUpdateRequestHandler>();
        var delete = Substitute.For<IWorkspacesDeleteRequestHandler>();
        var conflict = new ResultError("concurrency-mismatch", "Conflict", ResultErrorKind.Conflict);
        update.HandleAsync(Arg.Any<WorkspacesUpdateRequest>(), Ct).Returns(call =>
            call.Arg<WorkspacesUpdateRequest>().ExpectedRevision == 621355968000000000L
                ? Result<WorkspaceOutput>.Success(_workspace) : Result<WorkspaceOutput>.Failure(conflict));
        delete.HandleAsync(Arg.Any<WorkspacesDeleteRequest>(), Ct).Returns(call =>
            call.Arg<WorkspacesDeleteRequest>().ExpectedRevision == 621355968000000000L
                ? Result.Success() : Result.Failure(conflict));
        var updated = await controller.Update(_workspace.Id, new("Workspace", "workspace", null), update, Ct);
        var deleted = await controller.Delete(_workspace.Id, delete, Ct);
        ((StatusCodeResult?)deleted)?.StatusCode.ShouldBe(accepted ? StatusCodes.Status204NoContent : StatusCodes.Status412PreconditionFailed);
        if (accepted)
        {
            updated.Result.ShouldBeOfType<OkObjectResult>();
            controller.Response.Headers.ETag.ToString().ShouldBe("\"621355968000000000\"");
        }
        else updated.Result.ShouldBeOfType<StatusCodeResult>().StatusCode.ShouldBe(StatusCodes.Status412PreconditionFailed);
    }

    [Fact]
    public async Task ReadAndCreateAdapters_DelegateRequestCancellationAndPreserveWireResponses()
    {
        var controller = Controller();
        var list = Substitute.For<IWorkspacesListRequestHandler>();
        var get = Substitute.For<IWorkspacesGetRequestHandler>();
        var create = Substitute.For<IWorkspacesCreateRequestHandler>();
        list.HandleAsync(new WorkspacesListRequest(2, 20), Ct).Returns(Result<WorkspacesListOutput>.Success(new([_workspace], 27, 2, 20)));
        get.HandleAsync(new WorkspacesGetRequest(_workspace.Id), Ct).Returns(Result<WorkspaceOutput>.Success(_workspace));
        create.HandleAsync(new WorkspacesCreateRequest("Workspace", "workspace", null), Ct).Returns(Result<WorkspaceOutput>.Success(_workspace));
        var page = (await controller.List(new(2, 20), list, Ct)).Result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<PagedResponse<WorkspaceDto>>();
        page.TotalCount.ShouldBe(27);
        page.Page.ShouldBe(2);
        page.Items.Single().CanWrite.ShouldBeTrue();
        (await controller.Get(_workspace.Id, get, Ct)).Result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<WorkspaceDto>().Id.ShouldBe(_workspace.Id);
        var created = (await controller.Create(new("Workspace", "workspace", null), create, Ct)).Result.ShouldBeOfType<CreatedAtActionResult>();
        created.ActionName.ShouldBe(nameof(WorkspacesController.Get));
        created.RouteValues!["id"].ShouldBe(_workspace.Id);
        created.Value.ShouldBeOfType<WorkspaceDto>().Slug.ShouldBe("workspace");
        controller.Response.Headers.ETag.ToString().ShouldBe("\"621355968000000000\"");
    }

    [Theory]
    [InlineData(ResultErrorKind.Unauthenticated, StatusCodes.Status401Unauthorized)]
    [InlineData(ResultErrorKind.Forbidden, StatusCodes.Status403Forbidden)]
    [InlineData(ResultErrorKind.NotFound, StatusCodes.Status404NotFound)]
    [InlineData(ResultErrorKind.Conflict, StatusCodes.Status412PreconditionFailed)]
    [InlineData(ResultErrorKind.Validation, StatusCodes.Status422UnprocessableEntity)]
    public async Task ExpectedFailures_RetainStatusAndValidationProblemShape(ResultErrorKind kind, int status)
    {
        var controller = Controller();
        var handler = Substitute.For<IWorkspacesCreateRequestHandler>();
        handler.HandleAsync(Arg.Any<WorkspacesCreateRequest>(), Ct).Returns(Result<WorkspaceOutput>.Failure(new("validation-failed", "Safe validation detail", kind)));
        var result = (await controller.Create(new("Workspace", "workspace", null), handler, Ct)).Result!;
        if (kind == ResultErrorKind.Validation)
        {
            var response = result.ShouldBeOfType<ObjectResult>();
            response.StatusCode.ShouldBe(status);
            var problem = response.Value.ShouldBeOfType<ProblemDetails>();
            problem.Title.ShouldBe("Invalid workspace");
            problem.Detail.ShouldBe("Safe validation detail");
            problem.Extensions.ShouldContainKey("traceId");
        }
        else result.ShouldBeAssignableTo<StatusCodeResult>().StatusCode.ShouldBe(status);
    }
}
