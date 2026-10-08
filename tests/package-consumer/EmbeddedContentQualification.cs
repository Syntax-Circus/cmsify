using System.Data.Common;
using System.Text.Json;
using Cmsify.Core.ContentWrites;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.EmbeddedContent;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SyntaxCircus.Common;

internal static class EmbeddedContentQualification
{
    private static readonly Guid _subject = Guid.Parse("0af8d0c1-381c-4483-880f-a47b27d49589");
    internal static void RegisterHost(IServiceCollection services)
    {
        services.AddScoped<Scope>();
        services.AddScoped<ICurrentActor>(p => p.GetRequiredService<Scope>().Actor);
        services.AddScoped<IEmbeddedContentAuthorizationService, ContentAuthority>();
        services.AddScoped<IEmbeddedTemplateSetupAuthorizationService, SetupAuthority>();
        services.AddScoped<IContentVersionResourceGuard, UpdateGuard>();
        services.AddSingleton<LostResponse>();
        services.AddDbContext<CmsifyDbContext>((p, options) => options.AddInterceptors(p.GetRequiredService<LostResponse>()));
    }

    internal static void RegisterCapabilities(IServiceCollection services)
        => services.Replace(ServiceDescriptor.Scoped<IWorkspaceAuthorizationService, WorkspaceAuthority>());

    internal static async Task RunAsync(IServiceProvider services, Guid workspace, CancellationToken ct)
    {
        var contract = ProfileContract();
        var fingerprint = EmbeddedContractRules.Fingerprint(contract);
        EmbeddedTemplateOutput schema;
        await using (var setup = services.CreateAsyncScope())
        {
            Bind(setup.ServiceProvider, new(workspace, fingerprint, Guid.Empty, null, null, null, true));
            var actor = setup.ServiceProvider.GetRequiredService<ICurrentActor>();
            Require(actor.Role == UserRole.Editor && !actor.IsSuperAdmin && actor.UserId == _subject
                && actor.ApiClientId is null && actor.WorkspaceId is null, "Embedded identity widened.");
            schema = Success(await setup.ServiceProvider.GetRequiredService<IEnsureEmbeddedTemplateRequestHandler>().HandleAsync(new(workspace,contract),ct));
            var repeated = Success(await setup.ServiceProvider.GetRequiredService<IEnsureEmbeddedTemplateRequestHandler>().HandleAsync(new(workspace,contract),ct));
            Require(schema.TemplateVersionId == repeated.TemplateVersionId && schema.Fields.Select(f => f.FieldId).SequenceEqual(repeated.Fields.Select(f => f.FieldId)), "Ensure changed identities.");
        }
        await using (var read = services.CreateAsyncScope())
        {
            Bind(read.ServiceProvider, new(workspace,fingerprint,schema.TemplateVersionId,null,null,EmbeddedContentOperationKind.TemplateRead));
            var actual = Success(await read.ServiceProvider.GetRequiredService<IGetEmbeddedTemplateRequestHandler>().HandleAsync(new(workspace,contract.ContractKey,fingerprint),ct));
            Require(actual.TemplateVersionId == schema.TemplateVersionId && actual.Fields.Count == 5, "Exact template read differs.");
        }
        Console.WriteLine("PASS embedded ensure/get explicit pinned schema, repeated stable identities, ordinary host actor.");

        var item = Guid.NewGuid(); var operation = Guid.NewGuid();
        var fields = Fields(schema,"Marlow");
        var create = new CreateEmbeddedContentRequest(workspace,schema.TemplateVersionId,fingerprint,item,operation,fields);
        using (var canceled = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            var fault = services.GetRequiredService<LostResponse>();
            fault.CancelAfterCommit = canceled;
            await using var lost = services.CreateAsyncScope();
            Bind(lost.ServiceProvider,new(workspace,fingerprint,schema.TemplateVersionId,item,1,EmbeddedContentOperationKind.ItemCreate));
            try { await lost.ServiceProvider.GetRequiredService<ICreateEmbeddedContentRequestHandler>().HandleAsync(create,canceled.Token); throw new InvalidOperationException("Lost response did not cancel."); }
            catch(OperationCanceledException) when(canceled.IsCancellationRequested) { }
            finally { fault.CancelAfterCommit = null; }
        }
        EmbeddedContentReceiptOutput receipt;
        await using(var reconcile = services.CreateAsyncScope())
        {
            Bind(reconcile.ServiceProvider,new(workspace,fingerprint,schema.TemplateVersionId,item,1,EmbeddedContentOperationKind.ReceiptRead));
            receipt = Success(await reconcile.ServiceProvider.GetRequiredService<IGetEmbeddedContentOperationReceiptRequestHandler>()
                .HandleAsync(new(workspace,item,EmbeddedWriteKind.ItemCreate,operation,fingerprint),ct));
        }
        await using(var replay = services.CreateAsyncScope())
        {
            Bind(replay.ServiceProvider,new(workspace,fingerprint,schema.TemplateVersionId,item,1,EmbeddedContentOperationKind.ItemCreate));
            var output = Success(await replay.ServiceProvider.GetRequiredService<ICreateEmbeddedContentRequestHandler>().HandleAsync(create,ct));
            Require(output.Receipt == receipt && output.Version.Version.Fields.Single(f => f.Key == "name").TextValue == "Marlow", "Replay differs.");
        }
        Console.WriteLine("PASS embedded durable lost-response receipt reconciliation and same-key item replay.");

        EmbeddedContentVersionOutput detached;
        await using(var read = services.CreateAsyncScope())
        {
            Bind(read.ServiceProvider,new(workspace,fingerprint,schema.TemplateVersionId,item,1,EmbeddedContentOperationKind.VersionRead));
            detached = Success(await read.ServiceProvider.GetRequiredService<IGetEmbeddedContentVersionRequestHandler>()
                .HandleAsync(new(workspace,item,1,schema.TemplateVersionId,fingerprint,new(receipt.CommittedRevision)),ct));
        }
        var frozen = JsonSerializer.Serialize(detached);
        EmbeddedContentWriteOutput otherItem;
        var otherIdentity = Guid.NewGuid();
        await using(var other = services.CreateAsyncScope())
        {
            Bind(other.ServiceProvider,new(workspace,fingerprint,schema.TemplateVersionId,otherIdentity,1,EmbeddedContentOperationKind.ItemCreate));
            otherItem = Success(await other.ServiceProvider.GetRequiredService<ICreateEmbeddedContentRequestHandler>()
                .HandleAsync(create with { ContentItemId=otherIdentity, OperationKey=Guid.NewGuid() },ct));
        }
        UpdatedContentVersionOutput updated;
        await using(var edit = services.CreateAsyncScope())
        {
            Bind(edit.ServiceProvider,new(workspace,fingerprint,schema.TemplateVersionId,item,1,EmbeddedContentOperationKind.VersionUpdate));
            updated = Success(await edit.ServiceProvider.GetRequiredService<IUpdateContentVersionRequestHandler>()
                .HandleAsync(new(workspace,item,1,new(detached.Revision),null,null,Fields(schema,"Edited Marlow"),false),ct));
            var denied = await edit.ServiceProvider.GetRequiredService<IUpdateContentVersionRequestHandler>()
                .HandleAsync(new(workspace,otherIdentity,1,new(otherItem.Receipt.CommittedRevision),null,null,fields,false),ct);
            Require(denied.IsFailure && denied.Errors[0].Kind==ResultErrorKind.Forbidden,"Guard allowed another existing item.");
        }
        Require(JsonSerializer.Serialize(detached) == frozen && schema.Fields[0].Schema.FieldConfig.GetProperty("maxLength").GetInt32() == 200,
            "Detached DTO/JSON changed after scope disposal or later edit.");
        var newOperation = Guid.NewGuid();
        EmbeddedContentWriteOutput newVersion;
        await using(var next = services.CreateAsyncScope())
        {
            Bind(next.ServiceProvider,new(workspace,fingerprint,schema.TemplateVersionId,item,1,EmbeddedContentOperationKind.VersionCreate));
            newVersion = Success(await next.ServiceProvider.GetRequiredService<ICreateEmbeddedContentVersionRequestHandler>()
                .HandleAsync(new(workspace,item,1,new(updated.Revision),schema.TemplateVersionId,fingerprint,newOperation,Fields(schema,"New draft")),ct));
            Require(newVersion.Receipt.VersionNumber == 2 && newVersion.Version.Version.Status == ContentStatus.Draft,"New version identity/state differs.");
        }
        var thirdRequest = new CreateEmbeddedContentVersionRequest(workspace,item,1,new(updated.Revision),schema.TemplateVersionId,
            fingerprint,Guid.NewGuid(),Fields(schema,"Third draft"));
        await using(var next = services.CreateAsyncScope())
        {
            Bind(next.ServiceProvider,new(workspace,fingerprint,schema.TemplateVersionId,item,1,EmbeddedContentOperationKind.VersionCreate));
            var third = Success(await next.ServiceProvider.GetRequiredService<ICreateEmbeddedContentVersionRequestHandler>().HandleAsync(thirdRequest,ct));
            Require(third.Receipt.VersionNumber == 3,"Strict source authority allocation differs.");
            var replay = Success(await next.ServiceProvider.GetRequiredService<ICreateEmbeddedContentVersionRequestHandler>().HandleAsync(thirdRequest,ct));
            Require(replay.Receipt == third.Receipt,"Strict source authority replay differs.");
        }
        var allocated = await Task.WhenAll(Enumerable.Range(0,2).Select(async i =>
        {
            await using var next = services.CreateAsyncScope();
            Bind(next.ServiceProvider,new(workspace,fingerprint,schema.TemplateVersionId,item,1,EmbeddedContentOperationKind.VersionCreate));
            return Success(await next.ServiceProvider.GetRequiredService<ICreateEmbeddedContentVersionRequestHandler>().HandleAsync(
                thirdRequest with { OperationKey=Guid.NewGuid(), Fields=Fields(schema,"Concurrent draft " + i) },ct)).Receipt.VersionNumber;
        }));
        Require(allocated.Order().SequenceEqual(new[] {4,5}),"Strict source authority concurrent allocation differs.");
        await using(var source = services.CreateAsyncScope())
        {
            Bind(source.ServiceProvider,new(workspace,fingerprint,schema.TemplateVersionId,item,1,EmbeddedContentOperationKind.VersionRead));
            var unchanged = Success(await source.ServiceProvider.GetRequiredService<IGetEmbeddedContentVersionRequestHandler>()
                .HandleAsync(new(workspace,item,1,schema.TemplateVersionId,fingerprint,new(updated.Revision)),ct));
            Require(unchanged.Version.Fields.Single(f => f.Key == "name").TextValue == "Edited Marlow","Atomic new-version changed source.");
        }
        Console.WriteLine("PASS embedded selected read, guarded revision update, atomic replacement version, unchanged source and frozen detached DTO.");

        await using(var revoked = services.CreateAsyncScope())
        {
            Bind(revoked.ServiceProvider,new(workspace,fingerprint,schema.TemplateVersionId,item,1,EmbeddedContentOperationKind.ItemCreate,revoked:true));
            var denied = await revoked.ServiceProvider.GetRequiredService<ICreateEmbeddedContentRequestHandler>().HandleAsync(create,ct);
            Require(denied.IsFailure && denied.Errors[0].Kind == ResultErrorKind.Forbidden,"Revoked replay succeeded.");
        }
        await using(var hostile = services.CreateAsyncScope())
        {
            Bind(hostile.ServiceProvider,new(workspace,fingerprint,schema.TemplateVersionId,Guid.NewGuid(),1,EmbeddedContentOperationKind.VersionRead));
            var denied = await hostile.ServiceProvider.GetRequiredService<IGetEmbeddedContentVersionRequestHandler>()
                .HandleAsync(new(workspace,item,1,schema.TemplateVersionId,fingerprint),ct);
            Require(denied.IsFailure && denied.Errors[0].Kind == ResultErrorKind.Forbidden,"Forged item scope succeeded.");
        }
        await using(var oracle = services.CreateAsyncScope())
        {
            var db = oracle.ServiceProvider.GetRequiredService<CmsifyDbContext>();
            Require(await db.ContentVersions.CountAsync(v => v.ContentItemId == item,ct) == 5
                && await db.EmbeddedContentReceipts.CountAsync(r => r.ContentItemId == item,ct) == 5,"Replay/original count differs.");
            var audit = await db.AuditLogs.Where(a => a.EntityId == item).ToArrayAsync(ct);
            Require(audit.Length > 0 && audit.All(a => a.ActorUserId == _subject && a.ActorApiClientId is null),"Embedded host GUID audit differs.");
        }
        Console.WriteLine("PASS embedded hostile scope/revoked replay denial, PostgreSQL independent counts/host audit; 8 named embedded scenarios completed.");
    }

    private static EmbeddedTemplateContract ProfileContract() => new("puppies-plus.dog-profile.v1","Dog profile","puppies-plus-dog-profile-v1","name",
        new[] { ("name","Name",200,true),("breedName","Breed",200,true),("sex","Sex",7,false),("birthDate","Birth date",10,false),("introduction","Introduction",4000,false) }
            .Select((f,i) => new EmbeddedTemplateField(f.Item1,f.Item2,i,f.Item4,f.Item4?1:0,1,PrimitiveType.Text,ValueKind.Text,
                CompositionMode.Inline,false,JsonSerializer.SerializeToElement(new { maxLength=f.Item3,formatHint="plaintext" }))).ToArray());
    private static IReadOnlyList<ContentVersionFieldInput> Fields(EmbeddedTemplateOutput schema,string name)
        => schema.Fields.Select(f => new ContentVersionFieldInput(f.FieldId,0,ValueKind.Text,f.Schema.Key switch {
            "name"=>name,"breedName"=>"Example breed","sex"=>"Female","birthDate"=>"2025-06-01",_=>"A fictional profile." },null,null,null,null,null)).ToArray();
    private static void Bind(IServiceProvider services, Binding binding) => services.GetRequiredService<Scope>().Initialize(binding);
    private static void Require(bool condition,string message) { if(!condition) throw new InvalidOperationException(message); }
    private static T Success<T>(Result<T> result) { Require(result.IsSuccess,result.IsFailure ? result.Errors[0].Code : "Failed"); return result.Value; }
    private sealed record Binding(Guid Workspace,string Fingerprint,Guid Template,Guid? Item,int? Version,EmbeddedContentOperationKind? Kind,
        bool Setup=false,bool revoked=false);
    private sealed class Scope
    {
        internal Binding? Current { get; private set; }
        internal CurrentActorInfo Actor => Current is null ? CurrentActorInfo.Anonymous : new(_subject,null,UserRole.Editor,null,true);
        internal void Initialize(Binding binding) { if(Current is not null) throw new InvalidOperationException("Operation is immutable."); Current=binding; }
    }
    private sealed class WorkspaceAuthority(Scope scope) : IWorkspaceAuthorizationService
    {
        public Task<bool> CanReadWorkspaceAsync(Guid workspaceId,CancellationToken ct=default) => Task.FromResult(scope.Current is { } b && b.Workspace==workspaceId);
        public Task<bool> CanWriteWorkspaceAsync(Guid workspaceId,CancellationToken ct=default) => CanReadWorkspaceAsync(workspaceId,ct);
    }
    private sealed class SetupAuthority(Scope scope) : IEmbeddedTemplateSetupAuthorizationService
    {
        public Task<bool> CanEnsureAsync(EmbeddedTemplateContractScope value,CancellationToken ct) => Task.FromResult(scope.Current is { Setup:true,revoked:false } b
            && b.Workspace==value.WorkspaceId && b.Fingerprint==value.Fingerprint && value.ContractKey=="puppies-plus.dog-profile.v1");
    }
    private sealed class ContentAuthority(Scope scope) : IEmbeddedContentAuthorizationService
    {
        public Task<bool> CanExecuteAsync(EmbeddedContentOperation value,CancellationToken ct) => Task.FromResult(scope.Current is { revoked:false } b
            && b.Workspace==value.WorkspaceId && b.Fingerprint==value.ContractFingerprint && b.Kind==value.Kind
            && (value.ContractKey=="" || value.ContractKey=="puppies-plus.dog-profile.v1")
            && (value.TemplateVersionId==Guid.Empty || b.Template==value.TemplateVersionId) && b.Item==value.ContentItemId
            && (value.VersionNumber==null || b.Version==value.VersionNumber));
    }
    private sealed class UpdateGuard(Scope scope) : IContentVersionResourceGuard
    {
        public Task<bool> CanEditAsync(ContentVersionEditSnapshot value,CancellationToken ct) => Task.FromResult(scope.Current is { revoked:false,Kind:EmbeddedContentOperationKind.VersionUpdate } b
            && b.Workspace==value.WorkspaceId && b.Item==value.ContentItemId && b.Version==value.VersionNumber
            && value.TemplateVersionId!=Guid.Empty && b.Template==value.TemplateVersionId);
    }
    private sealed class LostResponse : DbTransactionInterceptor
    {
        internal CancellationTokenSource? CancelAfterCommit;
        public override Task TransactionCommittedAsync(DbTransaction transaction,TransactionEndEventData data,CancellationToken ct=default)
        { CancelAfterCommit?.Cancel(); return Task.CompletedTask; }
    }
}
