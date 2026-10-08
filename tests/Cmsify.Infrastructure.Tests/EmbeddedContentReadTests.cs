using Cmsify.Core.EmbeddedContent;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Services;
using Cmsify.Infrastructure.Persistence.EmbeddedContent;
using NSubstitute;
using Cmsify.Core.Interfaces.Services;
using Shouldly;
using Cmsify.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Cmsify.Core.ContentWrites;

namespace Cmsify.Infrastructure.Tests;

public sealed class EmbeddedContentReadTests
{
    [Fact]
    public async Task InvalidSavedStatusDenies()
    {
        await using var fixture = await EmbeddedContentFixture.Create();
        var initial = await fixture.Repository.CreateEmbeddedContentAsync(fixture.CreateRequest(),fixture.Actor,EmbeddedContentFixture.Permit,EmbeddedContentFixture.Ct);
        await using(var db=fixture.Fresh()) { var version=await db.ContentVersions.SingleAsync(EmbeddedContentFixture.Ct); version.Status=(ContentStatus)99; await db.SaveChangesAsync(EmbeddedContentFixture.Ct); }
        var result=await fixture.Repository.GetEmbeddedContentVersionAsync(fixture.ReadRequest(initial.Value),EmbeddedContentFixture.Permit,EmbeddedContentFixture.Ct);
        result.IsFailure.ShouldBeTrue();
    }
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

    [Fact]
    public async Task StaleConditionConflicts()
    {
        await using var fixture = await EmbeddedContentFixture.Create();
        var initial = await fixture.Repository.CreateEmbeddedContentAsync(fixture.CreateRequest(), fixture.Actor, EmbeddedContentFixture.Permit, EmbeddedContentFixture.Ct);
        var result = await fixture.Repository.GetEmbeddedContentVersionAsync(fixture.ReadRequest(initial.Value) with { Revision = new(1) }, EmbeddedContentFixture.Permit, EmbeddedContentFixture.Ct);
        result.Errors[0].Code.ShouldBe("concurrency-mismatch");
    }

    [Fact]
    public async Task ForgedTemplateScopeDenies()
    {
        await using var fixture = await EmbeddedContentFixture.Create();
        var initial = await fixture.Repository.CreateEmbeddedContentAsync(fixture.CreateRequest(), fixture.Actor, EmbeddedContentFixture.Permit, EmbeddedContentFixture.Ct);
        var foreign = await fixture.Repository.GetEmbeddedContentVersionAsync(fixture.ReadRequest(initial.Value) with { TemplateVersionId = Guid.NewGuid() }, EmbeddedContentFixture.Permit, EmbeddedContentFixture.Ct);
        foreign.Errors[0].Code.ShouldBe(EmbeddedContentErrors.TemplateMismatch);
        var wrongWorkspace = await fixture.Repository.GetEmbeddedContentVersionAsync(fixture.ReadRequest(initial.Value) with { WorkspaceId = Guid.NewGuid() }, EmbeddedContentFixture.Permit, EmbeddedContentFixture.Ct);
        wrongWorkspace.Errors[0].Code.ShouldBe("not-found");
    }

    [Fact]
    public async Task ReadCoherentDuringConcurrentEdit()
    {
        await using var fixture = await EmbeddedContentFixture.Create();
        var initial = await fixture.Repository.CreateEmbeddedContentAsync(fixture.CreateRequest(), fixture.Actor, EmbeddedContentFixture.Permit, EmbeddedContentFixture.Ct);
        var result = await fixture.Repository.GetEmbeddedContentVersionAsync(fixture.ReadRequest(initial.Value), async (operation, ct) => {
            await using var db = fixture.Fresh();
            var version = await db.ContentVersions.SingleAsync(ct);
            version.UpdatedAt = version.UpdatedAt.AddSeconds(2);
            var field = await db.ContentVersionFieldValues.SingleAsync(ct);
            field.TextValue = "Changed";
            await db.SaveChangesAsync(ct);
            return operation.TemplateVersionId == fixture.Schema.TemplateVersionId && operation.ContentItemId == initial.Value.Receipt.ContentItemId;
        }, EmbeddedContentFixture.Ct);
        result.IsSuccess.ShouldBeTrue();
        result.Value.Revision.ShouldBe(initial.Value.Receipt.CommittedRevision);
        result.Value.Version.Fields[0].TextValue.ShouldBe("Initial");
    }

    [Fact]
    public async Task DisposedScopeAndJsonCannotMutateOutput()
    {
        await using var fixture = await EmbeddedContentFixture.Create();
        var initial = await fixture.Repository.CreateEmbeddedContentAsync(fixture.CreateRequest(), fixture.Actor, EmbeddedContentFixture.Permit, EmbeddedContentFixture.Ct);
        var result = await fixture.Repository.GetEmbeddedContentVersionAsync(fixture.ReadRequest(initial.Value), EmbeddedContentFixture.Permit, EmbeddedContentFixture.Ct);
        await using (var db = fixture.Fresh()) { var field = await db.ContentVersionFieldValues.SingleAsync(EmbeddedContentFixture.Ct); field.TextValue = "Changed"; await db.SaveChangesAsync(EmbeddedContentFixture.Ct); }
        result.Value.Version.Fields[0].TextValue.ShouldBe("Initial");
        result.Value.Version.Fields.ShouldAllBe(f => f.Child == null && f.JsonValue == null);
        Should.Throw<NotSupportedException>(() => ((IList<ContentVersionFieldOutput>)result.Value.Version.Fields).Clear());
        fixture.Schema.Fields[0].Schema.FieldConfig.GetProperty("maxLength").GetInt32().ShouldBe(200);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidSavedSchemaOrFieldsDeny(bool schema)
    {
        await using var fixture = await EmbeddedContentFixture.Create();
        var initial = await fixture.Repository.CreateEmbeddedContentAsync(fixture.CreateRequest(), fixture.Actor, EmbeddedContentFixture.Permit, EmbeddedContentFixture.Ct);
        await using (var db = fixture.Fresh()) {
            if (schema) { var field = await db.TemplateFields.SingleAsync(EmbeddedContentFixture.Ct); field.Label = "Changed schema"; }
            else { var field = await db.ContentVersionFieldValues.SingleAsync(EmbeddedContentFixture.Ct); field.TextValue = new('x',201); }
            await db.SaveChangesAsync(EmbeddedContentFixture.Ct);
        }
        var result = await fixture.Repository.GetEmbeddedContentVersionAsync(fixture.ReadRequest(initial.Value), EmbeddedContentFixture.Permit, EmbeddedContentFixture.Ct);
        result.Errors[0].Code.ShouldBe(schema ? EmbeddedContentErrors.TemplateMismatch : EmbeddedContentErrors.Validation);
    }
}
