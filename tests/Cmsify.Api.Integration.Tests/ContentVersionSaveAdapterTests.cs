using System.Reflection;
using System.Text.Json;
using Cmsify.Api.Controllers;
using Cmsify.Core.ContentWrites;
using Cmsify.Core.Interfaces.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Shouldly;
using SyntaxCircus.Common;
using SyntaxCircus.Cmsify.Contracts;
using WriteRequest = Cmsify.Core.ContentWrites.UpdateContentVersionRequest;
using WireRequest = SyntaxCircus.Cmsify.Contracts.UpdateContentVersionRequest;
using CoreStatus = Cmsify.Core.Domain.Enums.ContentStatus;
using CoreKind = Cmsify.Core.Domain.Enums.ValueKind;

namespace Cmsify.Api.Integration.Tests;

public sealed class ContentVersionSaveAdapterTests
{
    [Fact]
    public async Task SaveDelegatesAllInputOnceAndMapsTheCompleteDetachedDetail()
    {
        var handler = Substitute.For<IUpdateContentVersionRequestHandler>();
        var authorization = Substitute.For<IWorkspaceAuthorizationService>();
        var controller = Controller(authorization);
        using var cancellation = new CancellationTokenSource();
        var workspace = Guid.NewGuid(); var item = Guid.NewGuid(); var field = Guid.NewGuid();
        var media = Guid.NewGuid(); var file = Guid.NewGuid(); var child = Guid.NewGuid();
        using var json = JsonDocument.Parse("{\"nested\":[1,true,null]}");
        var start = DateTimeOffset.Parse("2026-12-01T00:00:00Z"); var end = start.AddDays(1);
        var request = new WireRequest(start, end, [new(field, 7, ValueKind.Component, "text", true, media, file, child, json.RootElement)]);
        var output = Detail(child, []);
        var parent = Detail(item, [new(field, "key", "Label", 7, CoreKind.Component, "text", true, media, file, child, output, json.RootElement.Clone(), "snapshot label")]);
        handler.HandleAsync(Arg.Any<WriteRequest>(), Arg.Any<CancellationToken>()).Returns(Result<UpdatedContentVersionOutput>.Success(new(63902822400000000, parent)));
        controller.Request.Headers.IfMatch = "\"63800000000000000\"";

        var result = await InvokeAsync(controller, workspace, item, 9, request, handler, cancellation.Token, false);

        var call = handler.ReceivedCalls().ShouldHaveSingleItem();
        var received = (WriteRequest)call.GetArguments()[0]!;
        received.WorkspaceId.ShouldBe(workspace); received.ContentItemId.ShouldBe(item); received.VersionNumber.ShouldBe(9);
        received.Revision.Candidate.ShouldBe(63800000000000000); received.EffectiveStartAt.ShouldBe(start); received.EffectiveEndAt.ShouldBe(end);
        received.ExpandChildren.ShouldBeFalse(); call.GetArguments()[1].ShouldBe(cancellation.Token);
        var input = received.Fields.ShouldHaveSingleItem();
        input.FieldId.ShouldBe(field); input.Order.ShouldBe(7); input.ValueKind.ShouldBe(CoreKind.Component);
        input.TextValue.ShouldBe("text"); input.BoolValue.ShouldBe(true); input.MediaAssetId.ShouldBe(media); input.FileAssetId.ShouldBe(file); input.ChildContentItemId.ShouldBe(child);
        json.Dispose(); input.JsonValue!.Value.GetProperty("nested").GetArrayLength().ShouldBe(3);
        authorization.ReceivedCalls().ShouldBeEmpty();
        var response = result.Result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<ContentVersionDetailResponse>();
        controller.Response.Headers.ETag.ToString().ShouldBe("\"63902822400000000\"");
        AssertDetail(response, parent);
    }

    [Theory]
    [InlineData("authentication-required", ResultErrorKind.Unauthenticated, 401, null, null, null)]
    [InlineData("forbidden", ResultErrorKind.Forbidden, 403, null, null, null)]
    [InlineData("not-found", ResultErrorKind.NotFound, 404, null, null, null)]
    [InlineData("content-version-not-editable", ResultErrorKind.Conflict, 409, "conflict", "Only Draft, Review, or Approved versions can be edited", null)]
    [InlineData("concurrency-mismatch", ResultErrorKind.Conflict, 412, "concurrency-mismatch", "Concurrency mismatch", null)]
    [InlineData("invalid-effective-range", ResultErrorKind.Validation, 422, "validation-failed", "Invalid effective range", "application detail")]
    [InlineData("content-validation-failed", ResultErrorKind.Validation, 422, "validation-failed", "Content validation failed", "application detail")]
    public async Task SaveMapsApplicationErrorsAtTheBoundary(string code, ResultErrorKind kind, int status, string? wireCode, string? title, string? detail)
    {
        var handler = Substitute.For<IUpdateContentVersionRequestHandler>();
        handler.HandleAsync(Arg.Any<WriteRequest>(), Arg.Any<CancellationToken>()).Returns(Result<UpdatedContentVersionOutput>.Failure(new ResultError(code, "application detail", kind)));
        var authorization = Substitute.For<IWorkspaceAuthorizationService>();
        var controller = Controller(authorization);
        var result = await InvokeAsync(controller, Guid.NewGuid(), Guid.NewGuid(), 1, new(null, null, []), handler, TestContext.Current.CancellationToken);
        handler.ReceivedCalls().Count().ShouldBe(1);
        authorization.ReceivedCalls().ShouldBeEmpty();
        if (wireCode is null) result.Result!.ShouldBeAssignableTo<StatusCodeResult>().StatusCode.ShouldBe(status);
        else
        {
            var mapped = result.Result.ShouldBeOfType<ObjectResult>();
            mapped.StatusCode.ShouldBe(status); mapped.ContentTypes.ShouldBe(["application/problem+json"]);
            var problem = mapped.Value.ShouldBeOfType<ProblemDetails>();
            problem.Type.ShouldBe("https://cmsify.dev/errors/" + wireCode); problem.Title.ShouldBe(title); problem.Detail.ShouldBe(detail);
            problem.Instance.ShouldBe("/test-save"); problem.Extensions["traceId"].ShouldBe("task5-trace"); problem.Extensions["correlationId"].ShouldBe("task5-correlation");
            controller.Response.Headers["X-Correlation-Id"].ToString().ShouldBe("task5-correlation");
        }
    }

    [Theory]
    [InlineData("\"123\"", 123L)]
    [InlineData("\"-123\"", -123L)]
    [InlineData("\"0\"", 0L)]
    [InlineData("\"9223372036854775807\"", long.MaxValue)]
    [InlineData("\"-9223372036854775808\"", long.MinValue)]
    [InlineData("", null)] [InlineData(" ", null)]
    [InlineData(" \"123\" ", null)]
    [InlineData("\" 123 \"", null)]
    [InlineData("\"0123\"", null)] [InlineData("\"+123\"", null)] [InlineData("\"-0\"", null)]
    [InlineData("123", null)] [InlineData("W/\"123\"", null)] [InlineData("*", null)]
    [InlineData("\"123\", \"456\"", null)] [InlineData("\"9223372036854775808\"", null)]
    public async Task SavePassesOnlyCanonicalRevisionCandidatesWithoutAnEarlyFailure(string header, long? candidate)
    {
        var handler = Substitute.For<IUpdateContentVersionRequestHandler>();
        handler.HandleAsync(Arg.Any<WriteRequest>(), Arg.Any<CancellationToken>()).Returns(Result<UpdatedContentVersionOutput>.Failure(new ResultError("not-found", "Missing", ResultErrorKind.NotFound)));
        var controller = Controller(Substitute.For<IWorkspaceAuthorizationService>());
        controller.Request.Headers.IfMatch = header;
        var result = await InvokeAsync(controller, Guid.NewGuid(), Guid.NewGuid(), 1, new(null, null, []), handler, TestContext.Current.CancellationToken);
        var call = handler.ReceivedCalls().ShouldHaveSingleItem();
        ((WriteRequest)call.GetArguments()[0]!).Revision.Candidate.ShouldBe(candidate);
        ((WriteRequest)call.GetArguments()[0]!).ExpandChildren.ShouldBeTrue();
        result.Result.ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task SavePropagatesCancellationAndUnexpectedHandlerFailures()
    {
        var handler = Substitute.For<IUpdateContentVersionRequestHandler>();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        handler.HandleAsync(Arg.Any<WriteRequest>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromCanceled<Result<UpdatedContentVersionOutput>>(cancellation.Token));
        var controller = Controller(Substitute.For<IWorkspaceAuthorizationService>());
        await Should.ThrowAsync<OperationCanceledException>(() => InvokeAsync(controller, Guid.NewGuid(), Guid.NewGuid(), 1, new(null, null, []), handler, cancellation.Token));
        handler.ReceivedCalls().Count().ShouldBe(1);
        handler.ClearReceivedCalls();
        handler.HandleAsync(Arg.Any<WriteRequest>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromException<Result<UpdatedContentVersionOutput>>(new InvalidOperationException("provider failure")));
        (await Should.ThrowAsync<InvalidOperationException>(() => InvokeAsync(controller, Guid.NewGuid(), Guid.NewGuid(), 1, new(null, null, []), handler, cancellation.Token))).Message.ShouldBe("provider failure");
        handler.ReceivedCalls().Count().ShouldBe(1);
    }

    private static ContentController Controller(IWorkspaceAuthorizationService authorization) => new(null!, Substitute.For<IContentValidator>(), Substitute.For<IContentSearchVectorBuilder>(),
        Substitute.For<IContentLifecycleService>(), Substitute.For<IContentPublishingService>(), Substitute.For<ICurrentActor>(), authorization, Substitute.For<IWebhookOutbox>())
    {
        ControllerContext = new() { HttpContext = new DefaultHttpContext { TraceIdentifier = "task5-trace", Request = { Path = "/test-save", Headers = { ["X-Correlation-Id"] = "task5-correlation" } } } }
    };

    // Reflection lets the pre-extraction action compile for meaningful RED: the old action
    // returns its workspace denial without ever receiving/calling the supplied handler.
    private static Task<ActionResult<ContentVersionDetailResponse>> InvokeAsync(ContentController controller, Guid workspace, Guid item, int number,
        WireRequest request, IUpdateContentVersionRequestHandler handler, CancellationToken ct, bool expand = true)
    {
        var method = typeof(ContentController).GetMethod(nameof(ContentController.UpdateVersion))!;
        var args = method.GetParameters().Select(parameter => parameter.Name switch
        {
            "workspaceId" => (object)workspace, "id" => item, "versionNumber" => number, "request" => request,
            "ct" => ct, "expandChildren" => expand, _ when parameter.ParameterType == typeof(IUpdateContentVersionRequestHandler) => handler,
            _ => throw new InvalidOperationException(parameter.Name)
        }).ToArray();
        var injected = method.GetParameters().SingleOrDefault(p => p.ParameterType == typeof(IUpdateContentVersionRequestHandler));
        if (injected is not null) injected.GetCustomAttribute<FromServicesAttribute>().ShouldNotBeNull();
        return (Task<ActionResult<ContentVersionDetailResponse>>)method.Invoke(controller, args)!;
    }

    private static ContentVersionDetailOutput Detail(Guid item, IReadOnlyList<ContentVersionFieldOutput> fields) => new(Guid.NewGuid(), item, 9, CoreStatus.Approved,
        Guid.NewGuid(), "Template", "slug", "en-US", Guid.NewGuid(), DateTimeOffset.Parse("2026-01-01T00:00:00Z"), DateTimeOffset.Parse("2026-02-01T00:00:00Z"),
        DateTimeOffset.Parse("2025-12-31T00:00:00Z"), DateTimeOffset.Parse("2025-12-30T00:00:00Z"), DateTimeOffset.Parse("2025-12-29T00:00:00Z"), Guid.NewGuid(), 8,
        ["one", "two"], DateTimeOffset.Parse("2025-01-01T00:00:00Z"), DateTimeOffset.Parse("2026-01-01T00:00:00Z"), fields, "template-slug");

    internal static void AssertDetail(ContentVersionDetailResponse actual, ContentVersionDetailOutput expected)
    {
        var expectedJson = JsonSerializer.SerializeToElement(expected, CmsifyJsonOptions.Create());
        var actualJson = JsonSerializer.SerializeToElement(actual, CmsifyJsonOptions.Create());
        JsonElement.DeepEquals(expectedJson, actualJson).ShouldBeTrue($"Expected {expectedJson}; actual {actualJson}");
    }
}
