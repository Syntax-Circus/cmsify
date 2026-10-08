using Cmsify.Core.ContentWrites;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.EmbeddedContent;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Core.Services;
using Cmsify.Infrastructure.Auth;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Persistence.EmbeddedContent;
using Cmsify.Infrastructure.Persistence.Interceptors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cmsify.Infrastructure.Tests.Fixtures;

internal sealed class EmbeddedContentFixture : IAsyncDisposable
{
    private readonly ContentListQueryFixtures _database;
    private EmbeddedContentFixture(ContentListQueryFixtures database) => _database = database;
    public Guid Actor { get; } = Guid.NewGuid();
    public EmbeddedTemplateOutput Schema { get; private set; } = null!;
    public DbContextOptions<CmsifyDbContext> Options { get; private set; } = null!;
    public static CancellationToken Ct => TestContext.Current.CancellationToken;
    public static Task<bool> Permit(EmbeddedContentOperation _, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(true); }
    public EmbeddedContentRepository Repository => new(Options, new ContentValidator(), Substitute.For<IContentSearchVectorBuilder>(), TimeProvider.System);
    public CmsifyDbContext Fresh() => _database.Context();
    public CreateEmbeddedContentRequest CreateRequest(string text = "Initial", Guid? item = null, Guid? operation = null)
        => new(Schema.WorkspaceId, Schema.TemplateVersionId, Schema.Fingerprint, item ?? Guid.NewGuid(), operation ?? Guid.NewGuid(), Fields(text));
    public IReadOnlyList<ContentVersionFieldInput> Fields(string text) => [new(Schema.Fields[0].FieldId,0,ValueKind.Text,text,null,null,null,null,null)];
    public CreateEmbeddedContentVersionRequest VersionRequest(EmbeddedContentWriteOutput source, string text = "Replacement", Guid? operation = null)
        => new(Schema.WorkspaceId, source.Receipt.ContentItemId, source.Receipt.VersionNumber, new(source.Receipt.CommittedRevision),
            Schema.TemplateVersionId, Schema.Fingerprint, operation ?? Guid.NewGuid(), Fields(text));
    public GetEmbeddedContentVersionRequest ReadRequest(EmbeddedContentWriteOutput source)
        => new(Schema.WorkspaceId, source.Receipt.ContentItemId, source.Receipt.VersionNumber, Schema.TemplateVersionId, Schema.Fingerprint);
    public static async Task<EmbeddedContentFixture> Create(IInterceptor? observer = null)
    {
        var database = await ContentListQueryFixtures.Create(false);
        var fixture = new EmbeddedContentFixture(database);
        try
        {
            var builder = new DbContextOptionsBuilder<CmsifyDbContext>(database.Options);
            var actor = new CurrentActorInfo(fixture.Actor, null, UserRole.Editor, null, true);
            builder.AddInterceptors(new AuditInterceptor(new HostCurrentActorAuditAccessor(actor)));
            if (observer is not null) builder.AddInterceptors(observer);
            fixture.Options = builder.Options;
            var schema = await new EmbeddedTemplateRepository(fixture.Options).EnsureAsync(new(database.Workspace.Id,
                EmbeddedTemplateRepositoryTests.Contract()), fixture.Actor, (_, _) => Task.FromResult(true), Ct);
            if (schema.IsFailure) throw new InvalidOperationException(schema.Errors[0].Code);
            fixture.Schema = schema.Value;
            return fixture;
        }
        catch { await fixture.DisposeAsync(); throw; }
    }
    public ValueTask DisposeAsync() => _database.DisposeAsync();
}
