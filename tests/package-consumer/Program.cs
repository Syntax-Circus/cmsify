using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml.Linq;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Core.Workspaces;
using Cmsify.Infrastructure.Extensions;
using Cmsify.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using SyntaxCircus.Common;

// This executable is copied outside the checkout and built exclusively from candidate .nupkg references.
foreach (var assembly in new[] { "Core", "Infrastructure" })
{
    var id = $"SyntaxCircus.Cmsify.{assembly}";
    using var package = ZipFile.OpenRead(Path.Combine(args[0], $"{id}.{args[1]}.nupkg"));
    Require(package.GetEntry($"lib/net10.0/Cmsify.{assembly}.dll") is not null, $"{id} lacks its existing assembly.");
    Require(package.GetEntry("README.md") is not null && package.GetEntry("LICENSE") is not null, $"{id} lacks readme/license content.");
    using (var licenseFile = package.GetEntry("LICENSE")!.Open())
        Require(Convert.ToHexString(SHA256.HashData(licenseFile)).Equals(args[3], StringComparison.OrdinalIgnoreCase), "Packed LICENSE bytes differ from the repository's AGPL license.");
    using var nuspec = package.Entries.Single(entry => entry.FullName.EndsWith(".nuspec")).Open();
    var metadata = XDocument.Load(nuspec).Descendants().Single(element => element.Name.LocalName == "metadata");
    string? Value(string name) => metadata.Elements().SingleOrDefault(element => element.Name.LocalName == name)?.Value;
    Require(Value("id") == id && Value("version") == args[1], "Candidate package identity/version differs.");
    var license = metadata.Elements().Single(element => element.Name.LocalName == "license");
    Require(license.Value == "AGPL-3.0-or-later" && (string?)license.Attribute("type") == "expression", "Engine license is not AGPL-3.0-or-later.");
    Require(Value("readme") == "README.md", "Package readme metadata is missing.");
    var repository = metadata.Elements().Single(element => element.Name.LocalName == "repository");
    Require((string?)repository.Attribute("url") == "https://github.com/Syntax-Circus/cmsify", "Source repository metadata differs.");
    if (args.Length > 2 && args[2].Length > 0)
        Require((string?)repository.Attribute("commit") == args[2], "Source commit metadata differs.");
    if (assembly == "Infrastructure")
    {
        var core = metadata.Descendants().Single(element => element.Name.LocalName == "dependency" && (string?)element.Attribute("id") == "SyntaxCircus.Cmsify.Core");
        Require((string?)core.Attribute("version") == args[1], "Infrastructure Core dependency is not the candidate version.");
        Require(!metadata.Descendants().Any(element => element.Name.LocalName == "dependency" && (string?)element.Attribute("id") == "Microsoft.EntityFrameworkCore.Design"), "Design-only EF dependency leaked into consumers.");
    }
}
Console.WriteLine("PASS packed engine assemblies, AGPL license/readmes, source metadata and Core dependency version.");

var actorId = Guid.Parse("5bba16f6-f1e6-442d-a45b-eacda2d2e87e");
var secondActorId = Guid.Parse("87493ad2-3055-4b38-858c-387f1285724a");
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = Environments.Development });
builder.Configuration.Sources.Clear();
builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
{
    ["ConnectionStrings:Cmsify"] = Environment.GetEnvironmentVariable("CMSIFY_CONSUMER_POSTGRES") ?? throw new InvalidOperationException("Disposable PostgreSQL connection required."),
    ["Storage:Local:BasePath"] = Path.Combine(Directory.GetCurrentDirectory(), "storage"),
    ["Secrets:ActiveKeyId"] = "package-test",
    ["Secrets:EncryptionKeys:package-test"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
});
builder.Logging.ClearProviders();
builder.Host.UseDefaultServiceProvider(options => { options.ValidateOnBuild = true; options.ValidateScopes = true; });
builder.Services.AddScoped<ActorScope>();
builder.Services.AddScoped<ICurrentActor>(services => services.GetRequiredService<ActorScope>().Actor);
builder.Services.AddSingleton<TimeProvider>(new QualificationClock());
var options = new CmsifyInfrastructureOptions { UseHostCurrentActorForAudit = true, Workers = CmsifyWorkers.None };
builder.Services.AddCmsifyInfrastructure(builder.Configuration, options);
var registrationCount = builder.Services.Count;
builder.Services.AddCmsifyInfrastructure(builder.Configuration, options);
Require(builder.Services.Count == registrationCount, "Repeated registration was not idempotent.");
Require(!builder.Services.Any(service => service.ServiceType == typeof(IHttpContextAccessor)), "Host audit mode registered HTTP identity.");
Require(!builder.Services.Any(service => service.ServiceType == typeof(IHostedService)), "Workers.None registered hosted workers.");
await using var app = builder.Build();
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
var ct = deadline.Token;
await using (var setup = app.Services.CreateAsyncScope())
{
    var db = setup.ServiceProvider.GetRequiredService<CmsifyDbContext>();
    Require(db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL", "Default registration changed from PostgreSQL.");
    await db.Database.MigrateAsync(ct);
    var migrations = db.Database.GetMigrations().ToArray();
    Require(migrations.Length > 0 && (await db.Database.GetAppliedMigrationsAsync(ct)).SequenceEqual(migrations), "Packaged PostgreSQL migrations did not all apply.");
    Console.WriteLine($"PASS default PostgreSQL registration and {migrations.Length} packaged migrations.");
}

WorkspaceOutput created;
await using (var scope = app.Services.CreateAsyncScope())
{
    SetActor(scope.ServiceProvider, actorId);
    created = Success(await scope.ServiceProvider.GetRequiredService<IWorkspacesCreateRequestHandler>().HandleAsync(new("Package workspace", "package-workspace", null), ct));
    Require(created.CanWrite, "Host admin could not write workspace.");
}
await using (var scope = app.Services.CreateAsyncScope())
{
    var services = scope.ServiceProvider;
    SetActor(services, secondActorId);
    var read = Success(await services.GetRequiredService<IWorkspacesGetRequestHandler>().HandleAsync(new(created.Id), ct));
    Require(read.Name == "Package workspace" && read.Revision == created.Revision, "Create did not persist across scopes.");
    var list = Success(await services.GetRequiredService<IWorkspacesListRequestHandler>().HandleAsync(new(), ct));
    Require(list.TotalCount == 1 && list.Items.Single().Id == created.Id, "Direct list did not return persisted workspace.");
    var updated = Success(await services.GetRequiredService<IWorkspacesUpdateRequestHandler>().HandleAsync(new(created.Id, "Updated package", created.Slug, null, read.Revision), ct));
    Require(updated.Name == "Updated package" && updated.Revision > read.Revision, "Direct update did not advance revision.");
    var stale = await services.GetRequiredService<IWorkspacesUpdateRequestHandler>().HandleAsync(new(created.Id, "Stale", created.Slug, null, read.Revision), ct);
    Require(stale.IsFailure && stale.Errors[0].Kind == ResultErrorKind.Conflict, "Stale revision was accepted.");
    var deleted = await services.GetRequiredService<IWorkspacesDeleteRequestHandler>().HandleAsync(new(created.Id, updated.Revision), ct);
    Require(deleted.IsSuccess, "Direct delete failed.");
    var hidden = await services.GetRequiredService<IWorkspacesGetRequestHandler>().HandleAsync(new(created.Id), ct);
    Require(hidden.IsFailure && hidden.Errors[0].Kind == ResultErrorKind.NotFound, "Deleted workspace remained visible.");
}
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
    var stored = await db.Workspaces.IgnoreQueryFilters().AsNoTracking().SingleAsync(ct);
    Require(stored.IsDeleted && stored.Name == "Updated package" && stored.DeletedByUserId == secondActorId, "Mutation did not persist host actor soft deletion.");
    var audit = await db.AuditLogs.AsNoTracking().Where(log => log.EntityId == created.Id).ToListAsync(ct);
    Require(audit.Count == 3, "Expected create/update/delete audit records; stale update must produce none.");
    Require(audit.Single(log => log.Action == AuditAction.Created).ActorUserId == actorId, "Create audit lost first scoped host actor.");
    Require(audit.Where(log => log.Action != AuditAction.Created).All(log => log.ActorUserId == secondActorId)
        && audit.All(log => log.ActorApiClientId is null), "Mutation audit lost second scoped host actor.");
    var outbox = await db.WebhookOutboxEvents.AsNoTracking().SingleAsync(ct);
    Require(outbox.EventType == "workspace.updated" && outbox.EntityId == created.Id && outbox.Payload.GetProperty("Name").GetString() == "Updated package", "Update outbox was not persisted atomically.");
    Require(!await db.Users.AnyAsync(ct) && !await db.UserSessions.AnyAsync(ct) && !await db.ApiClients.AnyAsync(ct), "Host workflow unexpectedly required local credentials.");
}
Console.WriteLine("PASS clean-package direct workspace create/get/list/update/delete, revision rejection, PostgreSQL persistence/outbox and isolated host audit without HTTP/local credentials.");
await ContentQueryQualification.RunAsync(app.Services, SetActor, ct);
var humanBuilder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = Environments.Development });
humanBuilder.Configuration.Sources.Clear();
humanBuilder.Configuration.AddConfiguration(builder.Configuration);
humanBuilder.Logging.ClearProviders();
humanBuilder.Host.UseDefaultServiceProvider(validation => { validation.ValidateScopes = true; validation.ValidateOnBuild = true; });
var humanServices = humanBuilder.Services;
WorkspaceVisibilityQualification.RegisterHost(humanServices);
humanServices.AddCmsifyInfrastructure(humanBuilder.Configuration, options);
WorkspaceVisibilityQualification.RegisterCapabilities(humanServices);
await using (var humanApp = humanBuilder.Build())
    await WorkspaceVisibilityQualification.RunAsync(humanApp.Services, supportsRevisionMutations: true, ct);

Guid embeddedWorkspace;
await using (var provision = app.Services.CreateAsyncScope())
{
    SetActor(provision.ServiceProvider, actorId);
    embeddedWorkspace = Success(await provision.ServiceProvider.GetRequiredService<IWorkspacesCreateRequestHandler>()
        .HandleAsync(new("Embedded package workspace", "embedded-package-workspace", null), ct)).Id;
}
var embeddedBuilder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = Environments.Development });
embeddedBuilder.Configuration.Sources.Clear();
embeddedBuilder.Configuration.AddConfiguration(builder.Configuration);
embeddedBuilder.Logging.ClearProviders();
embeddedBuilder.Host.UseDefaultServiceProvider(validation => { validation.ValidateScopes = true; validation.ValidateOnBuild = true; });
EmbeddedContentQualification.RegisterHost(embeddedBuilder.Services);
embeddedBuilder.Services.AddCmsifyInfrastructure(embeddedBuilder.Configuration, options);
await using (var embeddedApp = embeddedBuilder.Build())
    await EmbeddedContentQualification.RunAsync(embeddedApp.Services, embeddedWorkspace, ct);

static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static T Success<T>(Result<T> result) { Require(result.IsSuccess, "Direct handler failed."); return result.Value; }
static void SetActor(IServiceProvider services, Guid id)
    => services.GetRequiredService<ActorScope>().Actor = id == Guid.Empty ? CurrentActorInfo.Anonymous
        : new CurrentActorInfo(id, null, UserRole.Admin, null, true, true);
sealed class ActorScope { public CurrentActorInfo Actor { get; set; } = CurrentActorInfo.Anonymous; }
