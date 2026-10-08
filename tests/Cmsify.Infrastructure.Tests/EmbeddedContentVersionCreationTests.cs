using Cmsify.Core.ContentWrites;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.EmbeddedContent;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Core.Services;
using Cmsify.Infrastructure.Persistence.EmbeddedContent;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Shouldly;

namespace Cmsify.Infrastructure.Tests;

public sealed class EmbeddedContentVersionCreationTests
{
    [Fact]
    public async Task NewVersionReplacementIsAtomic()
    {
        await using var fixture = await ContentListQueryFixtures.Create(false);
        var token = TestContext.Current.CancellationToken;
        var schema = await new EmbeddedTemplateRepository(fixture.Options).EnsureAsync(new(fixture.Workspace.Id,
            EmbeddedTemplateRepositoryTests.Contract()), Guid.NewGuid(), (_, _) => Task.FromResult(true), token);
        schema.IsSuccess.ShouldBeTrue();
        var actor = Guid.NewGuid();
        var repository = new EmbeddedContentRepository(fixture.Options, new ContentValidator(),
            Substitute.For<IContentSearchVectorBuilder>(), TimeProvider.System);
        var initial = await repository.CreateEmbeddedContentAsync(new(fixture.Workspace.Id, schema.Value.TemplateVersionId,
            schema.Value.Fingerprint, Guid.NewGuid(), Guid.NewGuid(), [new(schema.Value.Fields[0].FieldId,
                0, ValueKind.Text, "Initial", null, null, null, null, null)]), actor, (_, _) => Task.FromResult(true), token);
        initial.IsSuccess.ShouldBeTrue();
        var created = await repository.CreateEmbeddedContentVersionAsync(new(fixture.Workspace.Id, initial.Value.Receipt.ContentItemId,
            1, new(initial.Value.Receipt.CommittedRevision), schema.Value.TemplateVersionId, schema.Value.Fingerprint,
            Guid.NewGuid(), [new(schema.Value.Fields[0].FieldId, 0, ValueKind.Text, "Replacement", null,null,null,null,null)]),
            actor, (_, _) => Task.FromResult(true), token);
        created.IsSuccess.ShouldBeTrue();
        created.Value.Version.Version.VersionNumber.ShouldBe(2);
        created.Value.Version.Version.Fields[0].TextValue.ShouldBe("Replacement");
        await using var db = fixture.Context();
        (await db.ContentVersionFieldValues.SingleAsync(f => f.ContentVersionId == initial.Value.Version.Version.Id, token)).TextValue.ShouldBe("Initial");
        (await db.ContentVersions.CountAsync(token)).ShouldBe(2);
    }
}
