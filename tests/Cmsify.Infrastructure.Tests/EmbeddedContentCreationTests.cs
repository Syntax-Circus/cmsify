using Cmsify.Core.ContentWrites;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.EmbeddedContent;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Core.Services;
using Cmsify.Infrastructure.Persistence.EmbeddedContent;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Shouldly;
using Cmsify.Infrastructure.Tests.Fixtures;

namespace Cmsify.Infrastructure.Tests;

public sealed class EmbeddedContentCreationTests
{
    [Fact]
    public async Task InputFieldsOwnedBeforeLoadedAuthorization()
    {
        await using var fixture=await EmbeddedContentFixture.Create();
        var mutable=fixture.Fields("Original").ToList();
        var request=fixture.CreateRequest() with { Fields=mutable };
        var result=await fixture.Repository.CreateEmbeddedContentAsync(request,fixture.Actor,(_,_)=>{
            mutable[0]=mutable[0] with { TextValue="Changed during authorization" };
            return Task.FromResult(true);
        },EmbeddedContentFixture.Ct);
        result.IsSuccess.ShouldBeTrue();
        result.Value.Version.Version.Fields[0].TextValue.ShouldBe("Original");
    }
    [Fact]
    public async Task LostResponseRetryCreatesOneItemAndAudit()
    {
        await using var fixture = await ContentListQueryFixtures.Create(false);
        var token = TestContext.Current.CancellationToken;
        var schema = await new EmbeddedTemplateRepository(fixture.Options).EnsureAsync(new(fixture.Workspace.Id,
            EmbeddedTemplateRepositoryTests.Contract()), Guid.NewGuid(), (_, _) => Task.FromResult(true), token);
        schema.IsSuccess.ShouldBeTrue();
        var request = new CreateEmbeddedContentRequest(fixture.Workspace.Id, schema.Value.TemplateVersionId,
            schema.Value.Fingerprint, Guid.NewGuid(), Guid.NewGuid(), [new(schema.Value.Fields[0].FieldId,
                0, ValueKind.Text, "Initial", null, null, null, null, null)]);
        var actor = Guid.NewGuid();
        var repository = new EmbeddedContentRepository(fixture.Options, new ContentValidator(),
            Substitute.For<IContentSearchVectorBuilder>(), TimeProvider.System);
        var first = await repository.CreateEmbeddedContentAsync(request, actor, (_, _) => Task.FromResult(true), token);
        first.IsSuccess.ShouldBeTrue();
        var second = await repository.CreateEmbeddedContentAsync(request, actor, (_, _) => Task.FromResult(true), token);
        second.IsSuccess.ShouldBeTrue();
        second.Value.Receipt.ShouldBe(first.Value.Receipt);
        await using var db = fixture.Context();
        (await db.ContentItems.CountAsync(token)).ShouldBe(1);
        (await db.ContentVersions.CountAsync(token)).ShouldBe(1);
        (await db.WebhookOutboxEvents.CountAsync(token)).ShouldBe(2);
    }

    [Fact]
    public async Task MaxLengthDeniesBeforeAnyItemWrite()
    {
        await using var fixture = await ContentListQueryFixtures.Create(false);
        var token = TestContext.Current.CancellationToken;
        var schema = await new EmbeddedTemplateRepository(fixture.Options).EnsureAsync(new(fixture.Workspace.Id,
            EmbeddedTemplateRepositoryTests.Contract()), Guid.NewGuid(), (_, _) => Task.FromResult(true), token);
        var repository = new EmbeddedContentRepository(fixture.Options, new ContentValidator(), Substitute.For<IContentSearchVectorBuilder>(), TimeProvider.System);
        var result = await repository.CreateEmbeddedContentAsync(new(fixture.Workspace.Id, schema.Value.TemplateVersionId,
            schema.Value.Fingerprint, Guid.NewGuid(), Guid.NewGuid(), [new(schema.Value.Fields[0].FieldId,0,ValueKind.Text,
                new string('x',201),null,null,null,null,null)]), Guid.NewGuid(), (_, _) => Task.FromResult(true), token);
        result.IsFailure.ShouldBeTrue();
        await using var db = fixture.Context();
        (await db.ContentItems.CountAsync(token)).ShouldBe(0);
    }
}
