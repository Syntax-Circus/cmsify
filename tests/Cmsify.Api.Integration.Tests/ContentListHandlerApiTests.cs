using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cmsify.Core.ContentQueries;
using Microsoft.Extensions.DependencyInjection;
using SyntaxCircus.Common;

namespace Cmsify.Api.Integration.Tests;

public sealed class ContentListHandlerApiTests
{
    [Fact]
    public async Task ActionForwardsExactCancellationTokenAndDefaults()
    {
        var handler = new RecordingHandler(Result<ContentListOutput>.Success(new([], 0, 1, 20)));
        await using var factory = new ContentListSqliteFactory(handler);
        using var client = await factory.CreateSeededClientAsync();
        using var scope = factory.Services.CreateScope();
        var controller = Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<Cmsify.Api.Controllers.ContentController>(scope.ServiceProvider);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var query = new SyntaxCircus.Cmsify.Contracts.ContentListQuery(null, null, null, null, null, null, null, null, null, null, null, null);
        await controller.List(factory.WorkspaceId, query, handler, cancellation.Token);
        Assert.Equal(new ListContentRequest(factory.WorkspaceId), Assert.Single(handler.Requests));
        Assert.Equal(cancellation.Token, Assert.Single(handler.Tokens));
    }

    [Fact]
    public async Task AllInputsReachHandlerOnceAndSuccessMapsEveryField()
    {
        var time = ContentListSqliteFactory.Clock.GetUtcNow();
        var version = new ContentListVersionOutput(Guid.NewGuid(), Guid.NewGuid(), 7, Cmsify.Core.Domain.Enums.ContentStatus.Published,
            Guid.NewGuid(), "version-slug", "fr", time.AddDays(-1), time.AddDays(1), time.AddHours(-2), time.AddHours(-1),
            time.AddHours(1), Guid.NewGuid(), 3, ["version-tag", "second"], time.AddDays(-2), time.AddDays(-1));
        var item = new ContentListItemOutput(version.ContentItemId, Guid.NewGuid(), "Template", "item-slug", "en", Guid.NewGuid(),
            ["item-tag"], time.AddDays(-4), time.AddDays(-3), 9, version, "template-slug");
        var handler = new RecordingHandler(Result<ContentListOutput>.Success(new([item], 45, 2, 13)));
        await using var factory = new ContentListSqliteFactory(handler);
        using var client = await factory.CreateSeededClientAsync();
        var templateId = Guid.NewGuid();
        var translationId = Guid.NewGuid();
        var instant = Uri.EscapeDataString(time.ToString("O"));
        var response = await client.GetAsync(factory.Url($"q=%20needle%25_&templateVersionId={item.TemplateVersionId}&templateId={templateId}&status=Published&localeCode=en&translationGroupId={translationId}&slug=item-slug&tags=alpha,beta&createdAfter={instant}&createdBefore={instant}&publishedAfter={instant}&publishedBefore={instant}&resolve=true&asOf={instant}&sortBy=slug&sortDesc=false&page=2&pageSize=13"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new ListContentRequest(factory.WorkspaceId, " needle%_", item.TemplateVersionId, templateId,
            Cmsify.Core.Domain.Enums.ContentStatus.Published, "en", translationId, "item-slug", "alpha,beta", time, time, time, time,
            true, time, "slug", false, 2, 13), Assert.Single(handler.Requests));
        Assert.Single(handler.Tokens);
        Assert.True(handler.Tokens[0].CanBeCanceled);
        var actual = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        var expected = JsonSerializer.SerializeToElement(new
        {
            Items = new[] { new { item.Id, item.TemplateVersionId, item.TemplateName, item.Slug, item.LocaleCode, item.TranslationGroupId,
                item.Tags, item.CreatedAt, item.UpdatedAt, item.VersionCount, CurrentlyServingVersion = new
                { version.Id, version.ContentItemId, version.VersionNumber, Status = "Published", version.TemplateVersionId, version.Slug,
                    version.LocaleCode, version.EffectiveStartAt, version.EffectiveEndAt, version.PublishAt, version.PublishedAt, version.ArchivedAt,
                    version.PublishedByUserId, version.RolledBackFromVersionNumber, version.Tags, version.CreatedAt, version.UpdatedAt }, item.TemplateSlug } },
            TotalCount = 45, Page = 2, PageSize = 13, TotalPages = 4
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(JsonElement.DeepEquals(expected, actual), $"Expected {expected}; actual {actual}");
    }

    [Theory]
    [InlineData(ResultErrorKind.Unauthenticated, 401)]
    [InlineData(ResultErrorKind.Forbidden, 403)]
    [InlineData(ResultErrorKind.NotFound, 404)]
    [InlineData(ResultErrorKind.Validation, 400)]
    [InlineData(ResultErrorKind.Conflict, 409)]
    [InlineData(ResultErrorKind.Failure, 500)]
    public async Task ExpectedFailuresMapAtTransport(ResultErrorKind kind, int status)
    {
        var handler = new RecordingHandler(Result<ContentListOutput>.Failure(new("test", "Safe detail", kind)));
        await using var factory = new ContentListSqliteFactory(handler);
        using var client = await factory.CreateSeededClientAsync();
        using var response = await client.GetAsync(factory.Url(""), TestContext.Current.CancellationToken);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Single(handler.Requests);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(status, problem.GetProperty("status").GetInt32());
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(factory.Url("").Split('?')[0], problem.GetProperty("instance").GetString());
        Assert.True(problem.TryGetProperty("traceId", out _));
        Assert.True(problem.TryGetProperty("correlationId", out _));
    }

    [Theory]
    [InlineData(false, 409)]
    [InlineData(true, 500)]
    public async Task UnexpectedExceptionsUseCentralizedHandling(bool infrastructure, int status)
    {
        var handler = new RecordingHandler(null, infrastructure ? new IOException("private detail") : new InvalidOperationException("Sequence contains no elements."));
        await using var factory = new ContentListSqliteFactory(handler);
        using var client = await factory.CreateSeededClientAsync();
        using var response = await client.GetAsync(factory.Url(""), TestContext.Current.CancellationToken);
        Assert.Equal(status, (int)response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(infrastructure ? "An unexpected error occurred." : "Sequence contains no elements.", problem.GetProperty("detail").GetString());
        Assert.Single(handler.Requests);
    }

    private sealed class RecordingHandler(Result<ContentListOutput>? result, Exception? error = null) : IListContentRequestHandler
    {
        internal List<ListContentRequest> Requests { get; } = [];
        internal List<CancellationToken> Tokens { get; } = [];
        public Task<Result<ContentListOutput>> HandleAsync(ListContentRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request); Tokens.Add(cancellationToken);
            return error is null ? Task.FromResult(result!) : Task.FromException<Result<ContentListOutput>>(error);
        }
    }
}
