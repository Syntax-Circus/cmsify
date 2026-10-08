using Cmsify.Core.EmbeddedContent;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Services;
using Cmsify.Infrastructure.Persistence.EmbeddedContent;
using NSubstitute;
using Cmsify.Core.Interfaces.Services;
using Shouldly;

namespace Cmsify.Infrastructure.Tests;

public sealed class EmbeddedContentReadTests
{
    [Fact]
    public async Task ExplicitVersionDoesNotSelectLatest()
    {
        await using var fixture = await ContentListQueryFixtures.Create(false);
        var token = TestContext.Current.CancellationToken;
        var schema = await new EmbeddedTemplateRepository(fixture.Options).EnsureAsync(
            new(fixture.Workspace.Id, EmbeddedTemplateRepositoryTests.Contract()), Guid.NewGuid(), (_, _) => Task.FromResult(true), token);
        schema.IsSuccess.ShouldBeTrue();
        var item = new ContentItem { WorkspaceId = fixture.Workspace.Id, TemplateVersionId = schema.Value.TemplateVersionId };
        var first = new ContentVersion { WorkspaceId = item.WorkspaceId, ContentItemId = item.Id, TemplateVersionId = item.TemplateVersionId, VersionNumber = 1 };
        first.FieldValues.Add(new ContentVersionFieldValue { ContentVersionId = first.Id, FieldId = schema.Value.Fields[0].FieldId, ValueKind = ValueKind.Text, TextValue = "First" });
        var second = new ContentVersion { WorkspaceId = item.WorkspaceId, ContentItemId = item.Id, TemplateVersionId = item.TemplateVersionId, VersionNumber = 2 };
        second.FieldValues.Add(new ContentVersionFieldValue { ContentVersionId = second.Id, FieldId = schema.Value.Fields[0].FieldId, ValueKind = ValueKind.Text, TextValue = "Latest" });
        await using (var db = fixture.Context()) { db.AddRange(item, first, second); await db.SaveChangesAsync(token); }
        var repository = new EmbeddedContentRepository(fixture.Options, new ContentValidator(), Substitute.For<IContentSearchVectorBuilder>(), TimeProvider.System);
        var result = await repository.GetEmbeddedContentVersionAsync(new(item.WorkspaceId, item.Id, 1,
            item.TemplateVersionId, schema.Value.Fingerprint), (_, _) => Task.FromResult(true), token);
        result.IsSuccess.ShouldBeTrue();
        result.Value.Version.VersionNumber.ShouldBe(1);
        result.Value.Version.Fields[0].TextValue.ShouldBe("First");
    }
}
