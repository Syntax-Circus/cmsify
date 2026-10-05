using Cmsify.Core.ContentWrites;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;
using NSubstitute;
using Shouldly;
using SyntaxCircus.Common;
using System.Text.Json;

#pragma warning disable xUnit1051 // Deliberate caller-token permutations.
namespace Cmsify.Core.Tests;

public sealed class ContentVersionSaveHandlerTests
{
    private static readonly DateTimeOffset _time = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    private readonly Guid _workspace = Guid.NewGuid();
    private readonly Guid _item = Guid.NewGuid();
    private readonly Guid _user = Guid.NewGuid();
    private readonly ICurrentActor _actor = Substitute.For<ICurrentActor>();
    private readonly IWorkspaceAuthorizationService _authorization = Substitute.For<IWorkspaceAuthorizationService>();
    private readonly List<string> _calls = [];
    private readonly Session _session;
    private readonly Repository _repository;
    private readonly Clock _clock;

    public ContentVersionSaveHandlerTests()
    {
        _actor.IsAuthenticated.Returns(true);
        _actor.Role.Returns(UserRole.Editor);
        _actor.UserId.Returns(_user);
        _authorization.CanWriteWorkspaceAsync(_workspace, Arg.Any<CancellationToken>())
            .Returns(_ => { _calls.Add("authorize"); return true; });
        _session = new(_calls, new(Guid.NewGuid(), _item, _workspace, 2, ContentStatus.Draft, _time));
        _repository = new(_calls, _session);
        _clock = new(_calls);
    }

    private UpdateContentVersionRequest Request() => new(_workspace, _item, 2,
        new(_time.UtcTicks / 10), null, null, []);
    private UpdateContentVersionRequestHandler Handler() => new(_repository, _actor, _authorization, _clock);

    [Theory]
    [InlineData("anonymous", "authentication-required")]
    [InlineData("reader", "forbidden")]
    [InlineData("workspace", "not-found")]
    [InlineData("missing", "not-found")]
    [InlineData("deleted", "not-found")]
    [InlineData("wrong-scope", "not-found")]
    [InlineData("hidden", "not-found")]
    [InlineData("state", "content-version-not-editable")]
    [InlineData("revision", "concurrency-mismatch")]
    [InlineData("range", "invalid-effective-range")]
    public async Task SaveOrdersAuthorizationStateRevisionAndRange(string stage, string code)
    {
        var request = Request() with { Revision = new(null), EffectiveStartAt = _time };
        if (stage == "anonymous") _actor.IsAuthenticated.Returns(false);
        if (stage == "reader") _actor.Role.Returns(UserRole.Reader);
        if (stage == "workspace") _authorization.CanWriteWorkspaceAsync(_workspace, Arg.Any<CancellationToken>()).Returns(false);
        if (stage is "missing" or "deleted" or "wrong-scope" or "hidden") _repository.Session = null;
        if (stage == "state") _session.Snapshot = _session.Snapshot with { Status = ContentStatus.Published };
        if (stage == "range") request = request with { Revision = Request().Revision };
        var result = await Handler().HandleAsync(request, TestContext.Current.CancellationToken);
        result.Errors[0].Code.ShouldBe(code);
        _session.PrepareCalls.ShouldBe(0);
        _session.CommitCalls.ShouldBe(0);
        _repository.OpenCalls.ShouldBe(stage is "anonymous" or "reader" or "workspace" ? 0 : 1);
        _session.DisposeCalls.ShouldBe(stage is "state" or "revision" or "range" ? 1 : 0);
    }

    [Theory]
    [InlineData("prepare")]
    [InlineData("commit")]
    [InlineData("success")]
    public async Task SaveDisposesSessionOnEveryExit(string stage)
    {
        var error = new ResultError("content-validation-failed", "Invalid fields.", ResultErrorKind.Validation);
        if (stage == "prepare") _session.PrepareResult = Result.Failure(error);
        if (stage == "commit") _session.CommitResult = Result.Failure(new("concurrency-mismatch", "Changed.", ResultErrorKind.Conflict));
        var result = await Handler().HandleAsync(Request(), TestContext.Current.CancellationToken);
        result.IsSuccess.ShouldBe(stage == "success");
        _session.CommitCalls.ShouldBe(stage == "prepare" ? 0 : 1);
        _session.DisposeCalls.ShouldBe(1);
    }

    [Fact]
    public async Task ProjectionFailureDoesNotRepeatCommit()
    {
        _session.ThrowAt = "detail";
        await Should.ThrowAsync<InvalidOperationException>(() => Handler().HandleAsync(Request(), TestContext.Current.CancellationToken));
        _session.CommitCalls.ShouldBe(1);
        _session.DisposeCalls.ShouldBe(1);
    }

    [Fact]
    public async Task SaveClonesJsonInput()
    {
        var document = JsonDocument.Parse("{\"text\":\"owned\"}");
        var field = new ContentVersionFieldInput(Guid.NewGuid(), 3, ValueKind.Component, null, null, null, null, null, document.RootElement);
        _session.AfterPrepare = document.Dispose;
        var result = await Handler().HandleAsync(Request() with { Fields = new[] { field } }, TestContext.Current.CancellationToken);
        _session.Values!.Fields[0].JsonValue!.Value.GetProperty("text").GetString().ShouldBe("owned");
        result.Value!.Version.Fields[0].JsonValue!.Value.GetProperty("text").GetString().ShouldBe("owned");
    }

    [Theory]
    [InlineData(ContentStatus.Draft, UserRole.Editor, false)]
    [InlineData(ContentStatus.Review, UserRole.TemplateAdmin, true)]
    [InlineData(ContentStatus.Approved, UserRole.Admin, false)]
    public async Task EditableStatesAndHigherRolesSaveWithCurrentOrLegacyRevision(ContentStatus status, UserRole role, bool legacy)
    {
        _session.Snapshot = _session.Snapshot with { Status = status };
        _actor.Role.Returns(role);
        var request = Request() with { Revision = new(legacy ? _time.UtcTicks : _time.UtcTicks / 10), ExpandChildren = false };
        var result = await Handler().HandleAsync(request, TestContext.Current.CancellationToken);
        result.IsSuccess.ShouldBeTrue();
        result.Value!.Revision.ShouldBe(63926798401000000L);
        result.Value.Version.Status.ShouldBe(status);
        _session.ActorUserId.ShouldBe(_user);
        _session.ExpandChildren.ShouldBeFalse();
        _session.AsOf.ShouldBe(_time.AddSeconds(2));
        _repository.Scope.ShouldBe((_workspace, _item, 2));
        _calls.ShouldBe(new[] { "authorize", "open", "prepare", "commit", "clock", "detail", "dispose" });
    }

    [Theory]
    [InlineData(ContentStatus.Published)]
    [InlineData(ContentStatus.Archived)]
    [InlineData((ContentStatus)99)]
    public async Task NonEditableStatesWinOverInvalidRevisionAndRange(ContentStatus status)
    {
        _session.Snapshot = _session.Snapshot with { Status = status };
        var result = await Handler().HandleAsync(Request() with { Revision = new(null), EffectiveStartAt = _time }, TestContext.Current.CancellationToken);
        result.Errors[0].Code.ShouldBe("content-version-not-editable");
        _session.PrepareCalls.ShouldBe(0);
        _session.DisposeCalls.ShouldBe(1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MaxValue)]
    public async Task InvalidOrStaleRevisionWinsOverRange(long? candidate)
    {
        var result = await Handler().HandleAsync(Request() with { Revision = new(candidate), EffectiveStartAt = _time }, TestContext.Current.CancellationToken);
        result.Errors[0].Code.ShouldBe("concurrency-mismatch");
        _session.PrepareCalls.ShouldBe(0);
    }

    [Theory]
    [InlineData("start", "Provide both effectiveStartAt and effectiveEndAt, or neither.")]
    [InlineData("end", "Provide both effectiveStartAt and effectiveEndAt, or neither.")]
    [InlineData("equal", "effectiveStartAt must be before effectiveEndAt.")]
    [InlineData("reverse", "effectiveStartAt must be before effectiveEndAt.")]
    public async Task InvalidRangeIsRejectedBeforePrepare(string mode, string message)
    {
        var request = Request() with { EffectiveStartAt = mode == "end" ? null : _time,
            EffectiveEndAt = mode == "start" ? null : mode == "reverse" ? _time.AddSeconds(-1) : _time };
        var result = await Handler().HandleAsync(request, TestContext.Current.CancellationToken);
        result.Errors[0].Code.ShouldBe("invalid-effective-range");
        result.Errors[0].Message.ShouldBe(message);
        _session.PrepareCalls.ShouldBe(0);
    }

    [Theory]
    [InlineData("before", 0, 0)]
    [InlineData("open", 0, 1)]
    [InlineData("prepare", 0, 1)]
    [InlineData("commit", 1, 1)]
    [InlineData("detail", 1, 1)]
    public async Task CancellationDisposesAndDoesNotRepeatCommit(string stage, int commits, int disposals)
    {
        using var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        if (stage == "before") source.Cancel();
        if (stage == "open") _repository.AfterOpen = source.Cancel;
        if (stage == "prepare") _session.AfterPrepare = source.Cancel;
        if (stage == "commit") _session.AfterCommit = source.Cancel;
        if (stage == "detail") _session.AfterDetail = source.Cancel;
        await Should.ThrowAsync<OperationCanceledException>(() => Handler().HandleAsync(Request(), source.Token));
        _session.CommitCalls.ShouldBe(commits);
        _session.DisposeCalls.ShouldBe(disposals);
        _session.Tokens.ShouldAllBe(token => token == source.Token);
        _repository.Token.ShouldBe(stage == "before" ? default : source.Token);
    }

    [Theory]
    [InlineData("prepare", 0)]
    [InlineData("commit", 1)]
    public async Task UnexpectedSessionFailuresRemainExceptions(string stage, int commits)
    {
        _session.ThrowAt = stage;
        await Should.ThrowAsync<InvalidOperationException>(() => Handler().HandleAsync(Request(), TestContext.Current.CancellationToken));
        _session.CommitCalls.ShouldBe(commits);
        _session.DisposeCalls.ShouldBe(1);
    }

    [Fact]
    public async Task OutputOwnsRecursiveProjectionJsonAndCollectionsAfterSessionDisposal()
    {
        _session.ProjectionDocument = JsonDocument.Parse("{\"projection\":true}");
        var result = await Handler().HandleAsync(Request(), TestContext.Current.CancellationToken);
        var detail = result.Value!.Version;
        detail.Fields[0].JsonValue!.Value.GetProperty("projection").GetBoolean().ShouldBeTrue();
        detail.Fields[0].Child!.Fields[0].JsonValue!.Value.GetProperty("projection").GetBoolean().ShouldBeTrue();
        detail.Tags.ShouldBe(new[] { "tag" });
        detail.Fields[0].Child!.Tags.ShouldBe(new[] { "tag" });
        detail.TemplateSlug.ShouldBe("template");
        detail.Fields[0].DisplayLabel.ShouldBe("Display");
        _session.ExpandChildren.ShouldBeTrue();
        _session.DisposeCalls.ShouldBe(1);
    }

    [Fact]
    public async Task ValidRangeAndApiClientWithoutUserArePassedToPreparation()
    {
        _actor.UserId.Returns((Guid?)null);
        var result = await Handler().HandleAsync(Request() with { EffectiveStartAt = _time,
            EffectiveEndAt = _time.AddSeconds(3) }, TestContext.Current.CancellationToken);
        result.IsSuccess.ShouldBeTrue();
        _session.Values!.EffectiveStartAt.ShouldBe(_time);
        _session.Values.EffectiveEndAt.ShouldBe(_time.AddSeconds(3));
        _session.ActorUserId.ShouldBeNull();
        _session.Values.Fields.ShouldBeEmpty();
    }

    [Fact]
    public async Task PreparationPreservesFieldValuesAndAllValidationErrors()
    {
        var field = new ContentVersionFieldInput(Guid.NewGuid(), 7, ValueKind.Link, "link", true,
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null);
        var first = new ResultError("content-validation-failed", "First.", ResultErrorKind.Validation);
        var second = new ResultError("content-validation-failed", "Second.", ResultErrorKind.Validation);
        _session.PrepareResult = Result.Failure(first, second);
        var result = await Handler().HandleAsync(Request() with { Fields = new[] { field } }, TestContext.Current.CancellationToken);
        result.Errors.ShouldBe(new[] { first, second });
        _session.Values!.Fields[0].ShouldBe(field);
        _session.CommitCalls.ShouldBe(0);
        _clock.Reads.ShouldBe(0);
        _session.DisposeCalls.ShouldBe(1);
    }

    private sealed class Repository(List<string> calls, Session? session) : IContentVersionEditRepository
    {
        public Session? Session = session;
        public int OpenCalls;
        public Action? AfterOpen;
        public (Guid, Guid, int) Scope;
        public CancellationToken Token;
        public Task<IContentVersionEditSession?> OpenAsync(Guid workspaceId, Guid itemId, int versionNumber, CancellationToken cancellationToken)
        { calls.Add("open"); OpenCalls++; Scope = (workspaceId, itemId, versionNumber); Token = cancellationToken; AfterOpen?.Invoke(); return Task.FromResult<IContentVersionEditSession?>(Session); }
    }

    private sealed class Session(List<string> calls, ContentVersionEditSnapshot snapshot) : IContentVersionEditSession
    {
        public ContentVersionEditSnapshot Snapshot { get; set; } = snapshot;
        public int PrepareCalls, CommitCalls, DisposeCalls;
        public ContentVersionEditValues? Values;
        public Result PrepareResult = Result.Success();
        public Result CommitResult = Result.Success();
        public string? ThrowAt;
        public Action? AfterPrepare;
        public Action? AfterCommit, AfterDetail;
        public Guid? ActorUserId;
        public DateTimeOffset AsOf;
        public bool ExpandChildren;
        public List<CancellationToken> Tokens = [];
        public JsonDocument? ProjectionDocument;
        private readonly List<string> _tags = ["tag"];
        private readonly List<ContentVersionFieldOutput> _projectionFields = [];
        public Task<Result> PrepareAsync(ContentVersionEditValues values, Guid? actorUserId, CancellationToken cancellationToken)
        { calls.Add("prepare"); PrepareCalls++; Values = values; ActorUserId = actorUserId; Tokens.Add(cancellationToken); if (ThrowAt == "prepare") throw new InvalidOperationException(); AfterPrepare?.Invoke(); return Task.FromResult(PrepareResult); }
        public Task<Result> CommitAsync(CancellationToken cancellationToken)
        { calls.Add("commit"); CommitCalls++; Tokens.Add(cancellationToken); if (ThrowAt == "commit") throw new InvalidOperationException(); AfterCommit?.Invoke(); return Task.FromResult(CommitResult); }
        public Task<ContentVersionDetailOutput> ReadDetailAsync(DateTimeOffset asOf, bool expandChildren, CancellationToken cancellationToken)
        {
            calls.Add("detail");
            Tokens.Add(cancellationToken); AsOf = asOf; ExpandChildren = expandChildren; AfterDetail?.Invoke();
            if (ThrowAt == "detail") throw new InvalidOperationException("projection failure");
            var detail = new ContentVersionDetailOutput(Snapshot.Id, Snapshot.ContentItemId, 2, Snapshot.Status,
                Guid.NewGuid(), "Template", "slug", "en", null, null, null, null, null, null, null, null,
                _tags, _time, _time.AddSeconds(1), Values!.Fields.Select(f => new ContentVersionFieldOutput(f.FieldId,
                    "key", "Label", f.Order, f.ValueKind, f.TextValue, f.BoolValue, f.MediaAssetId, f.FileAssetId,
                    f.ChildContentItemId, null, f.JsonValue, "Display")).ToArray(), "template");
            if (ProjectionDocument is { } document)
            {
                var leaf = new ContentVersionFieldOutput(Guid.NewGuid(), "key", "Label", 0, ValueKind.Component,
                    null, null, null, null, null, null, document.RootElement, "Display");
                var child = detail with { Fields = new[] { leaf } };
                _projectionFields.Add(leaf with { Child = child });
                detail = detail with { Fields = _projectionFields };
            }
            return Task.FromResult(detail);
        }
        public ValueTask DisposeAsync()
        { calls.Add("dispose"); DisposeCalls++; ProjectionDocument?.Dispose(); _tags.Clear(); _projectionFields.Clear(); return ValueTask.CompletedTask; }
    }
    private sealed class Clock(List<string> calls) : TimeProvider
    {
        public int Reads;
        public override DateTimeOffset GetUtcNow() { calls.Add("clock"); Reads++; return _time.AddSeconds(2); }
    }
}
