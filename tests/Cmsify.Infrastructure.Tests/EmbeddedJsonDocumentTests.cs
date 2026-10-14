using System.Text;
using System.Text.Json;
using Cmsify.Core.ContentWrites;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.EmbeddedContent;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Core.Services;
using Cmsify.Infrastructure.Persistence.ContentWrites;
using Cmsify.Infrastructure.Persistence.EmbeddedContent;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Shouldly;

namespace Cmsify.Infrastructure.Tests;

public sealed class EmbeddedJsonDocumentTests
{
    private const int DocumentBytes = 256 * 1024;

    [Theory]
    [InlineData("repeated")]
    [InlineData("unicode")]
    [InlineData("sections")]
    public async Task ExactSizeJsonSurvivesWritesAndReceiptsWithoutEnteringSearch(string shape)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await ContentListQueryFixtures.Create(false);
        var actor = Guid.NewGuid();
        var contract = new EmbeddedTemplateContract("site-json.v1", "Site document", "site-json-v1", "title",
        [
            new("title", "Title", 0, true, 1, 1, PrimitiveType.Text, ValueKind.Text, CompositionMode.Inline, false,
                JsonSerializer.SerializeToElement(new { maxLength = 200, formatHint = "plaintext" })),
            new("document", "Document", 1, true, 1, 1, PrimitiveType.Text, ValueKind.Text, CompositionMode.Inline, false,
                JsonSerializer.SerializeToElement(new { maxLength = DocumentBytes, formatHint = "json" }))
        ]);
        var templates = new EmbeddedTemplateRepository(fixture.Options);
        var setup = await templates.EnsureAsync(new(fixture.Workspace.Id, contract), actor, (_, _) => Task.FromResult(true), ct);
        setup.IsSuccess.ShouldBeTrue();
        var schema = setup.Value;
        var again = await templates.EnsureAsync(new(fixture.Workspace.Id, contract), actor, (_, _) => Task.FromResult(true), ct);
        again.Value.TemplateVersionId.ShouldBe(schema.TemplateVersionId);
        var document = Document(shape);
        Encoding.UTF8.GetByteCount(document).ShouldBe(DocumentBytes);
        using var valid = JsonDocument.Parse(document);
        IReadOnlyList<ContentVersionFieldInput> Fields(string value) => schema.Fields.Select(f => new ContentVersionFieldInput(
            f.FieldId, 0, ValueKind.Text, f.Schema.Key == "title" ? "Beseme" : value, null, null, null, null, null)).ToArray();
        var search = new ContentSearchVectorBuilder();
        var repository = new EmbeddedContentRepository(fixture.Options, new ContentValidator(), search, TimeProvider.System);
        var request = new CreateEmbeddedContentRequest(fixture.Workspace.Id, schema.TemplateVersionId, schema.Fingerprint,
            Guid.NewGuid(), Guid.NewGuid(), Fields(document));
        var created = await repository.CreateEmbeddedContentAsync(request, actor, Permit, ct);
        created.IsSuccess.ShouldBeTrue();
        var replay = await repository.CreateEmbeddedContentAsync(request, actor, Permit, ct);
        replay.Value.Receipt.ShouldBe(created.Value.Receipt);
        var receipt = await repository.GetEmbeddedContentOperationReceiptAsync(new(fixture.Workspace.Id, request.ContentItemId,
            EmbeddedWriteKind.ItemCreate, request.OperationKey, schema.Fingerprint), Permit, ct);
        receipt.Value.ShouldBe(created.Value.Receipt);
        var read = await repository.GetEmbeddedContentVersionAsync(new(fixture.Workspace.Id, request.ContentItemId, 1,
            schema.TemplateVersionId, schema.Fingerprint, new(created.Value.Receipt.CommittedRevision)), Permit, ct);
        read.Value.Version.Fields.Single(f => f.Key == "document").TextValue.ShouldBe(document);

        var authority = Substitute.For<IWorkspaceAuthorizationService>();
        authority.CanWriteWorkspaceAsync(fixture.Workspace.Id, ct).Returns(true);
        var guard = Substitute.For<IContentVersionResourceGuard>();
        guard.CanEditAsync(Arg.Any<ContentVersionEditSnapshot>(), ct).Returns(true);
        var update = new UpdateContentVersionRequestHandler(new ContentVersionEditRepository(fixture.Options, new ContentValidator(), search, TimeProvider.System),
            new CurrentActorInfo(actor, null, UserRole.Editor, null, true), authority, TimeProvider.System, guard);
        var changedDocument = document.Replace("paragraphs", "paragraphx", StringComparison.Ordinal);
        var saved = await update.HandleAsync(new(fixture.Workspace.Id, request.ContentItemId, 1, new(read.Value.Revision),
            null, null, Fields(changedDocument), false), ct);
        saved.IsSuccess.ShouldBeTrue();
        saved.Value.Version.Fields.Single(f => f.Key == "document").TextValue.ShouldBe(changedDocument);

        var nextRequest = new CreateEmbeddedContentVersionRequest(fixture.Workspace.Id, request.ContentItemId, 1,
            new(saved.Value.Revision), schema.TemplateVersionId, schema.Fingerprint, Guid.NewGuid(), Fields(document));
        var next = await repository.CreateEmbeddedContentVersionAsync(nextRequest, actor, Permit, ct);
        next.IsSuccess.ShouldBeTrue();
        next.Value.Receipt.VersionNumber.ShouldBe(2);
        next.Value.Version.Version.Status.ShouldBe(ContentStatus.Draft);
        next.Value.Version.Version.Fields.Single(f => f.Key == "document").TextValue.ShouldBe(document);
        (await repository.CreateEmbeddedContentVersionAsync(nextRequest, actor, Permit, ct)).Value.Receipt.ShouldBe(next.Value.Receipt);
        (await repository.GetEmbeddedContentOperationReceiptAsync(new(fixture.Workspace.Id, request.ContentItemId,
            EmbeddedWriteKind.VersionCreate, nextRequest.OperationKey, schema.Fingerprint), Permit, ct)).Value.ShouldBe(next.Value.Receipt);

        await using var oracle = fixture.Context();
        (await oracle.ContentVersions.CountAsync(v => v.ContentItemId == request.ContentItemId, ct)).ShouldBe(2);
        var vector = (await oracle.ContentItems.SingleAsync(i => i.Id == request.ContentItemId, ct)).SearchVector;
        vector.ShouldNotBeNull();
        vector.ShouldContain("beseme");
        vector.ShouldNotContain("paragraph");
        var source = await repository.GetEmbeddedContentVersionAsync(new(fixture.Workspace.Id, request.ContentItemId, 1,
            schema.TemplateVersionId, schema.Fingerprint, new(saved.Value.Revision)), Permit, ct);
        source.Value.Version.Fields.Single(f => f.Key == "document").TextValue.ShouldBe(changedDocument);
        var oversized = await repository.CreateEmbeddedContentAsync(request with { ContentItemId = Guid.NewGuid(), OperationKey = Guid.NewGuid(),
            Fields = Fields(new string(' ', DocumentBytes + 1)) }, actor, Permit, ct);
        oversized.IsFailure.ShouldBeTrue();
    }

    private static Task<bool> Permit(EmbeddedContentOperation _, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(true); }
    private static string Document(string shape)
    {
        const string prefix = "{\"paragraphs\":[\"";
        const string suffix = "\"]}";
        var available = DocumentBytes - prefix.Length - suffix.Length;
        if (shape == "unicode") return prefix + new string('界', available / 3) + new string('a', available % 3) + suffix;
        if (shape == "repeated") return prefix + string.Concat(Enumerable.Repeat("word ", available / 5)) + new string('a', available % 5) + suffix;
        var sections = JsonSerializer.Serialize(new { paragraphs = Enumerable.Range(0, 24).Select(i => new { type = "paragraph", text = "Section " + i }).ToArray() });
        return sections.PadRight(DocumentBytes);
    }
}
