using Cmsify.Core.ContentQueries;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;
using NSubstitute;
using SyntaxCircus.Common;

#pragma warning disable xUnit1051 // Caller-token propagation is the behavior under test.

namespace Cmsify.Core.Tests;

public sealed class ContentListHandlerTests
{
    private static readonly DateTimeOffset _now = DateTimeOffset.Parse("2026-10-04T12:00:00Z");
    private static readonly DateTimeOffset _asOf = DateTimeOffset.Parse("2025-01-02T03:04:05Z");
    private readonly Guid _workspaceId = Guid.NewGuid();
    private readonly IContentListQueryRepository _repository = Substitute.For<IContentListQueryRepository>();
    private readonly IWorkspaceAuthorizationService _authorization = Substitute.For<IWorkspaceAuthorizationService>();
    private readonly ICurrentActor _actor = Substitute.For<ICurrentActor>();
    private readonly CountingClock _clock = new(_now);
    private readonly List<ContentListCriteria> _ordinaryCalls = [];
    private readonly List<ContentListCriteria> _resolvedCalls = [];

    public ContentListHandlerTests()
    {
        _actor.IsAuthenticated.Returns(true);
        _actor.Role.Returns(UserRole.Reader);
        _authorization.CanReadWorkspaceAsync(_workspaceId, Arg.Any<CancellationToken>()).Returns(true);
        _repository.ListItemsAsync(Arg.Any<ContentListCriteria>(), Arg.Any<CancellationToken>())
            .Returns(call => { _ordinaryCalls.Add(call.Arg<ContentListCriteria>()); return new ContentListPage([], 7); });
        _repository.ListResolvedAsync(Arg.Any<ContentListCriteria>(), Arg.Any<CancellationToken>())
            .Returns(call => { _resolvedCalls.Add(call.Arg<ContentListCriteria>()); return new ContentListPage([], 9); });
    }

    private ListContentRequestHandler Handler() => new(_repository, _actor, _authorization, _clock);
    private void AssertNoQueryOrClock()
    {
        Assert.Empty(_ordinaryCalls);
        Assert.Empty(_resolvedCalls);
        Assert.Equal(0, _clock.Reads);
    }

    [Fact]
    public async Task Anonymous_IsDeniedBeforeWorkspaceAccessAndPagination()
    {
        _actor.IsAuthenticated.Returns(false);
        var result = await Handler().HandleAsync(new(_workspaceId, Page: 0));
        Assert.True(result.IsFailure);
        Assert.Equal(ResultErrorKind.Unauthenticated, result.Errors[0].Kind);
        Assert.Equal("authentication-required", result.Errors[0].Code);
        await _authorization.DidNotReceiveWithAnyArgs().CanReadWorkspaceAsync(default);
        AssertNoQueryOrClock();
    }

    [Fact]
    public async Task RoleBelowReader_IsDeniedBeforeWorkspaceAccessAndPagination()
    {
        _actor.Role.Returns((UserRole)(-1));
        _actor.IsSuperAdmin.Returns(true);
        var result = await Handler().HandleAsync(new(_workspaceId, Page: 0));
        Assert.True(result.IsFailure);
        Assert.Equal(ResultErrorKind.Forbidden, result.Errors[0].Kind);
        Assert.Equal("forbidden", result.Errors[0].Code);
        await _authorization.DidNotReceiveWithAnyArgs().CanReadWorkspaceAsync(default);
        AssertNoQueryOrClock();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnreadableWorkspace_IsHiddenBeforePagination(bool resolve)
    {
        _authorization.CanReadWorkspaceAsync(_workspaceId, Arg.Any<CancellationToken>()).Returns(false);
        var result = await Handler().HandleAsync(new(_workspaceId, Resolve: resolve, Page: 0, PageSize: 101));
        Assert.True(result.IsFailure);
        Assert.Equal(ResultErrorKind.NotFound, result.Errors[0].Kind);
        Assert.Equal("not-found", result.Errors[0].Code);
        AssertNoQueryOrClock();
    }

    [Theory]
    [InlineData(UserRole.Reader)]
    [InlineData(UserRole.Editor)]
    [InlineData(UserRole.TemplateAdmin)]
    [InlineData(UserRole.Admin)]
    public async Task ReadRoles_CanListAuthorizedWorkspace(UserRole role)
    {
        _actor.Role.Returns(role);
        var result = await Handler().HandleAsync(new(_workspaceId));
        Assert.True(result.IsSuccess);
        Assert.Equal(7, result.Value.TotalCount);
        Assert.Single(_ordinaryCalls);
    }

    [Fact]
    public async Task SuperAdmin_UsesWorkspaceAuthorizationWithoutRequiringUserOrWorkspaceScope()
    {
        _actor.IsSuperAdmin.Returns(true);
        _actor.UserId.Returns((Guid?)null);
        _actor.WorkspaceId.Returns((Guid?)null);
        var result = await Handler().HandleAsync(new(_workspaceId));
        Assert.True(result.IsSuccess);
        await _authorization.Received(1).CanReadWorkspaceAsync(_workspaceId, Arg.Any<CancellationToken>());
        Assert.Single(_ordinaryCalls);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(-1, 20)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    [InlineData(1, 101)]
    public async Task Authorized_InvalidPaginationReturnsValidationWithoutQueryOrClock(int page, int size)
    {
        var result = await Handler().HandleAsync(new(_workspaceId, Page: page, PageSize: size));
        Assert.True(result.IsFailure);
        Assert.Equal(ResultErrorKind.Validation, result.Errors[0].Kind);
        Assert.Equal("validation-failed", result.Errors[0].Code);
        await _authorization.Received(1).CanReadWorkspaceAsync(_workspaceId, Arg.Any<CancellationToken>());
        AssertNoQueryOrClock();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ChoosesOneBranchAndCapturesOnlyRequiredClock(bool resolve, bool explicitAsOf)
    {
        var request = new ListContentRequest(_workspaceId, Q: " q% ", TemplateVersionId: Guid.NewGuid(),
            TemplateId: Guid.NewGuid(), Status: ContentStatus.Published, LocaleCode: "fr", TranslationGroupId: Guid.NewGuid(),
            Slug: "slug", Tags: " one,TWO ", CreatedAfter: _asOf, CreatedBefore: _now,
            PublishedAfter: _asOf, PublishedBefore: _now, Resolve: resolve, AsOf: explicitAsOf ? _asOf : null,
            SortBy: "unrecognized", SortDesc: false, Page: 3, PageSize: 10);
        var result = await Handler().HandleAsync(request);
        Assert.True(result.IsSuccess);
        var criteria = Assert.Single(resolve ? _resolvedCalls : _ordinaryCalls);
        Assert.Empty(resolve ? _ordinaryCalls : _resolvedCalls);
        Assert.Same(request, criteria.Request);
        Assert.Equal(20, criteria.Offset);
        Assert.Equal(resolve && explicitAsOf ? _asOf : _now, criteria.EvaluationTime);
        Assert.Equal(resolve && explicitAsOf ? 0 : 1, _clock.Reads);
        Assert.Equal(resolve ? 9 : 7, result.Value.TotalCount);
        Assert.Equal(3, result.Value.Page);
        Assert.Equal(10, result.Value.PageSize);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OffsetOverflow_ReturnsCountOnlyAndPreservesPagination(bool resolve)
    {
        var result = await Handler().HandleAsync(new(_workspaceId, Resolve: resolve, Page: int.MaxValue, PageSize: 100));
        Assert.True(result.IsSuccess);
        Assert.Null(Assert.Single(resolve ? _resolvedCalls : _ordinaryCalls).Offset);
        Assert.Empty(result.Value.Items);
        Assert.Equal(resolve ? 9 : 7, result.Value.TotalCount);
        Assert.Equal(int.MaxValue, result.Value.Page);
        Assert.Equal(100, result.Value.PageSize);
    }

    [Theory]
    [InlineData(1, 1, 0)]
    [InlineData(1, 100, 0)]
    [InlineData(int.MaxValue, 1, 2147483646)]
    [InlineData(1073741824, 2, 2147483646)]
    public async Task ValidOffset_NearBoundaryDoesNotOverflow(int page, int size, int expectedOffset)
    {
        Assert.True((await Handler().HandleAsync(new(_workspaceId, Page: page, PageSize: size))).IsSuccess);
        Assert.Equal(expectedOffset, Assert.Single(_ordinaryCalls).Offset);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthorizedAbsentWorkspace_ReturnsRepositoryEmptyPage(bool resolve)
    {
        _repository.ListItemsAsync(Arg.Any<ContentListCriteria>(), Arg.Any<CancellationToken>()).Returns(new ContentListPage([], 0));
        _repository.ListResolvedAsync(Arg.Any<ContentListCriteria>(), Arg.Any<CancellationToken>()).Returns(new ContentListPage([], 0));
        var result = await Handler().HandleAsync(new(_workspaceId, Resolve: resolve));
        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.Items);
        Assert.Equal(0, result.Value.TotalCount);
        Assert.Equal(1, result.Value.Page);
        Assert.Equal(20, result.Value.PageSize);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Success_CopiesItemsAndNestedTagsIntoReadOnlySnapshots(bool resolve)
    {
        var tags = new List<string> { "item-tag" };
        var versionTags = new List<string> { "version-tag" };
        var id = Guid.NewGuid();
        var version = new ContentListVersionOutput(Guid.NewGuid(), id, 4, ContentStatus.Published, Guid.NewGuid(),
            "version-slug", "en", _asOf, _now, _asOf, _asOf, null, Guid.NewGuid(), 2, versionTags, _asOf, _now);
        var item = new ContentListItemOutput(id, Guid.NewGuid(), "Template", "item-slug", "fr", Guid.NewGuid(),
            tags, _asOf, _now, 4, version, "template-slug");
        var sourceItems = new List<ContentListItemOutput> { item, item with { Id = Guid.NewGuid(), CurrentlyServingVersion = null } };
        var page = new ContentListPage(sourceItems, 23);
        _repository.ListItemsAsync(Arg.Any<ContentListCriteria>(), Arg.Any<CancellationToken>()).Returns(page);
        _repository.ListResolvedAsync(Arg.Any<ContentListCriteria>(), Arg.Any<CancellationToken>()).Returns(page);
        var output = (await Handler().HandleAsync(new(_workspaceId, Resolve: resolve))).Value;
        Assert.Equal(23, output.TotalCount);
        Assert.Equal(2, output.Items.Count);
        Assert.Equal(item with { Tags = output.Items[0].Tags, CurrentlyServingVersion = output.Items[0].CurrentlyServingVersion }, output.Items[0]);
        Assert.Equal(version with { Tags = output.Items[0].CurrentlyServingVersion!.Tags }, output.Items[0].CurrentlyServingVersion);
        Assert.Null(output.Items[1].CurrentlyServingVersion);
        Assert.NotSame(sourceItems, output.Items);
        Assert.NotSame(item, output.Items[0]);
        Assert.NotSame(version, output.Items[0].CurrentlyServingVersion);
        tags.Add("mutated");
        versionTags.Clear();
        sourceItems.Clear();
        Assert.Equal(2, output.Items.Count);
        Assert.Equal(new[] { "item-tag" }, output.Items[0].Tags);
        Assert.Equal(new[] { "version-tag" }, output.Items[0].CurrentlyServingVersion!.Tags);
        Assert.Throws<NotSupportedException>(() => ((IList<ContentListItemOutput>)output.Items).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)output.Items[0].Tags).Add("changed"));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)output.Items[0].CurrentlyServingVersion!.Tags).Clear());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PropagatesCallerTokenToAccessAndSelectedRepository(bool resolve)
    {
        using var source = new CancellationTokenSource();
        var token = source.Token;
        await Handler().HandleAsync(new(_workspaceId, Resolve: resolve), token);
        await _authorization.Received(1).CanReadWorkspaceAsync(_workspaceId, token);
        if (resolve) await _repository.Received(1).ListResolvedAsync(Arg.Any<ContentListCriteria>(), token);
        else await _repository.Received(1).ListItemsAsync(Arg.Any<ContentListCriteria>(), token);
    }

    [Fact]
    public async Task AccessCancellation_PropagatesBeforeInvalidPagination()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        _authorization.CanReadWorkspaceAsync(_workspaceId, source.Token).Returns(Task.FromCanceled<bool>(source.Token));
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Handler().HandleAsync(new(_workspaceId, Page: 0), source.Token));
        Assert.Equal(source.Token, exception.CancellationToken);
        AssertNoQueryOrClock();
    }

    [Fact]
    public async Task AccessException_PropagatesBeforeInvalidPagination()
    {
        var failure = new InvalidOperationException("access unavailable");
        _authorization.CanReadWorkspaceAsync(_workspaceId, Arg.Any<CancellationToken>()).Returns(Task.FromException<bool>(failure));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => Handler().HandleAsync(new(_workspaceId, Page: 0))));
        AssertNoQueryOrClock();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepositoryCancellation_IsNotConvertedToResult(bool resolve)
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var failure = Task.FromCanceled<ContentListPage>(source.Token);
        _repository.ListItemsAsync(Arg.Any<ContentListCriteria>(), source.Token).Returns(failure);
        _repository.ListResolvedAsync(Arg.Any<ContentListCriteria>(), source.Token).Returns(failure);
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Handler().HandleAsync(new(_workspaceId, Resolve: resolve), source.Token));
        Assert.Equal(source.Token, exception.CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepositoryException_IsNotConvertedToResult(bool resolve)
    {
        var failure = new InvalidOperationException("repository unavailable");
        _repository.ListItemsAsync(Arg.Any<ContentListCriteria>(), Arg.Any<CancellationToken>()).Returns(Task.FromException<ContentListPage>(failure));
        _repository.ListResolvedAsync(Arg.Any<ContentListCriteria>(), Arg.Any<CancellationToken>()).Returns(Task.FromException<ContentListPage>(failure));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => Handler().HandleAsync(new(_workspaceId, Resolve: resolve))));
    }

    private sealed class CountingClock(DateTimeOffset now) : TimeProvider
    {
        public int Reads { get; private set; }
        public override DateTimeOffset GetUtcNow() { Reads++; return now; }
    }
}
