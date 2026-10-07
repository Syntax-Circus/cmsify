using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Xml.Linq;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Core.Workspaces;
using Cmsify.Infrastructure.Extensions;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Sqlite.Extensions;
using Microsoft.EntityFrameworkCore;
using SyntaxCircus.Common;

// Copied outside the checkout; every engine reference comes from candidate packages.
foreach (var assembly in new[] { "Core", "Infrastructure", "Infrastructure.Sqlite" })
{
    var id = $"SyntaxCircus.Cmsify.{assembly}";
    using var package = ZipFile.OpenRead(Path.Combine(args[0], $"{id}.{args[1]}.nupkg"));
    Require(package.GetEntry($"lib/net10.0/Cmsify.{assembly}.dll") is not null, $"{id} lacks assembly.");
    Require(package.GetEntry("README.md") is not null && package.GetEntry("LICENSE") is not null, $"{id} lacks readme/license.");
    using (var licenseFile = package.GetEntry("LICENSE")!.Open())
        Require(Convert.ToHexString(SHA256.HashData(licenseFile)).Equals(args[3], StringComparison.OrdinalIgnoreCase), "Packed LICENSE differs.");
    using var nuspec = package.Entries.Single(entry => entry.FullName.EndsWith(".nuspec")).Open();
    var metadata = XDocument.Load(nuspec).Descendants().Single(element => element.Name.LocalName == "metadata");
    string? Value(string name) => metadata.Elements().SingleOrDefault(element => element.Name.LocalName == name)?.Value;
    Require(Value("id") == id && Value("version") == args[1], "Candidate package identity/version differs.");
    var license = metadata.Elements().Single(element => element.Name.LocalName == "license");
    Require(license.Value == "AGPL-3.0-or-later" && (string?)license.Attribute("type") == "expression", "Engine license differs.");
    Require(Value("readme") == "README.md", "Readme metadata missing.");
    var repository = metadata.Elements().Single(element => element.Name.LocalName == "repository");
    Require((string?)repository.Attribute("url") == "https://github.com/Syntax-Circus/cmsify", "Source repository differs.");
    if (args[2].Length > 0) Require((string?)repository.Attribute("commit") == args[2], "Source commit metadata differs.");
    var loaded = Assembly.Load($"Cmsify.{assembly}");
    var informational = loaded.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    Require(args[2].Length > 0
        ? informational == $"{args[1]}+{args[2]}" || informational == $"{args[1]}+{args[2]}.{args[2]}"
        : informational?.Split('+')[0] == args[1], "Assembly informational version/source differs.");
    Require(loaded.GetName().Version == new Version(args[1].Split('-')[0] + ".0"), "Assembly version differs.");
    if (assembly != "Core")
    {
        var dependencyId = assembly == "Infrastructure" ? "SyntaxCircus.Cmsify.Core" : "SyntaxCircus.Cmsify.Infrastructure";
        var dependency = metadata.Descendants().Single(element => element.Name.LocalName == "dependency" && (string?)element.Attribute("id") == dependencyId);
        Require((string?)dependency.Attribute("version") == args[1], "Engine dependency not candidate version.");
        Require(!metadata.Descendants().Any(element => element.Name.LocalName == "dependency" && (string?)element.Attribute("id") == "Microsoft.EntityFrameworkCore.Design"), "Design dependency leaked.");
    }
}
Console.WriteLine("PASS SQLite candidate assembly/version/source/dependency/license/readme metadata.");
var database = Path.Combine(Directory.GetCurrentDirectory(), "cmsify-consumer.db");
var actorId = Guid.Parse("5bba16f6-f1e6-442d-a45b-eacda2d2e87e");
WebApplication BuildHost()
{
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = Environments.Development });
    builder.Configuration.Sources.Clear();
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ConnectionStrings:Cmsify"] = $"Data Source={database}",
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
    builder.Services.AddCmsifySqliteInfrastructure(builder.Configuration, options);
    var count = builder.Services.Count;
    builder.Services.AddCmsifySqliteInfrastructure(builder.Configuration, options);
    Require(builder.Services.Count == count, "Repeated registration not idempotent.");
    Require(!builder.Services.Any(service => service.ServiceType == typeof(IHttpContextAccessor) || service.ServiceType == typeof(IHostedService)), "Unexpected HTTP identity/workers.");
    return builder.Build();
}
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
var ct = deadline.Token;
Guid workspaceId;
await using (var app = BuildHost())
{
    Require(!File.Exists(database), "Registration created database.");
    await app.MigrateCmsifyDatabaseAsync(ct);
    await using var scope = app.Services.CreateAsyncScope();
    var services = scope.ServiceProvider;
    SetActor(services, actorId);
    var db = services.GetRequiredService<CmsifyDbContext>();
    Require(db.Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite", "Wrong provider.");
    Require(!db.Database.HasPendingModelChanges(), "Model differs from snapshot.");
    var migrations = db.Database.GetMigrations().ToArray();
    Require(migrations.Length > 0 && (await db.Database.GetAppliedMigrationsAsync(ct)).SequenceEqual(migrations), "Real migrations not applied.");
    Require(!await db.Workspaces.AnyAsync(ct) && !await db.Users.AnyAsync(ct), "Implicit seeding occurred.");
    await db.Database.OpenConnectionAsync(ct);
    await using (var command = db.Database.GetDbConnection().CreateCommand())
    {
        command.CommandText = "SELECT COUNT(*) FROM __CmsifyMigrationsHistory";
        Require(Convert.ToInt64(await command.ExecuteScalarAsync(ct)) == migrations.Length, "Owned history missing.");
        command.CommandText = "PRAGMA foreign_keys";
        Require(Convert.ToInt64(await command.ExecuteScalarAsync(ct)) == 1, "Foreign keys disabled.");
    }
    var created = Success(await services.GetRequiredService<IWorkspacesCreateRequestHandler>().HandleAsync(new("SQLite package", "sqlite-package", null), ct));
    workspaceId = created.Id;
    var read = Success(await services.GetRequiredService<IWorkspacesGetRequestHandler>().HandleAsync(new(created.Id), ct));
    var updated = Success(await services.GetRequiredService<IWorkspacesUpdateRequestHandler>().HandleAsync(new(created.Id, "Updated SQLite", created.Slug, null, read.Revision), ct));
    Require(updated.Revision > read.Revision, "Revision did not advance.");
    var stale = await services.GetRequiredService<IWorkspacesUpdateRequestHandler>().HandleAsync(new(created.Id, "Stale", created.Slug, null, read.Revision), ct);
    Require(stale.IsFailure && stale.Errors[0].Kind == ResultErrorKind.Conflict, "Stale revision accepted.");
}
await using (var restarted = BuildHost())
{
    await restarted.MigrateCmsifyDatabaseAsync(ct);
    await using var scope = restarted.Services.CreateAsyncScope();
    var services = scope.ServiceProvider;
    SetActor(services, actorId);
    var list = Success(await services.GetRequiredService<IWorkspacesListRequestHandler>().HandleAsync(new(), ct));
    Require(list.TotalCount == 1 && list.Items.Single().Id == workspaceId && list.Items.Single().Name == "Updated SQLite", "Restart changed data.");
    var read = Success(await services.GetRequiredService<IWorkspacesGetRequestHandler>().HandleAsync(new(workspaceId), ct));
    Require((await services.GetRequiredService<IWorkspacesDeleteRequestHandler>().HandleAsync(new(workspaceId, read.Revision), ct)).IsSuccess, "Delete failed.");
    var hidden = await services.GetRequiredService<IWorkspacesGetRequestHandler>().HandleAsync(new(workspaceId), ct);
    Require(hidden.IsFailure && hidden.Errors[0].Kind == ResultErrorKind.NotFound, "Deleted workspace visible.");
    var db = services.GetRequiredService<CmsifyDbContext>();
    Require((await db.Workspaces.IgnoreQueryFilters().SingleAsync(ct)).IsDeleted, "Soft deletion lost.");
    var audit = await db.AuditLogs.Where(log => log.EntityId == workspaceId).ToListAsync(ct);
    Require(audit.Count == 3 && audit.All(log => log.ActorUserId == actorId && log.ActorApiClientId is null), "Workspace audit invariant failed.");
    Require(!await db.Users.AnyAsync(ct) && !await db.UserSessions.AnyAsync(ct) && !await db.ApiClients.AnyAsync(ct), "Unexpected local credentials.");
    await ContentQueryQualification.RunAsync(restarted.Services, SetActor, ct);
    var humanServices = new ServiceCollection();
    humanServices.AddLogging();
    humanServices.AddSingleton<IConfiguration>(restarted.Configuration);
    humanServices.AddSingleton<IHostEnvironment>(restarted.Environment);
    WorkspaceVisibilityQualification.RegisterHost(humanServices);
    humanServices.AddCmsifySqliteInfrastructure(restarted.Configuration,
        new() { UseHostCurrentActorForAudit = true, Workers = CmsifyWorkers.None });
    WorkspaceVisibilityQualification.RegisterCapabilities(humanServices);
    await using var humanProvider = humanServices.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    await WorkspaceVisibilityQualification.RunAsync(humanProvider, supportsRevisionMutations: true, ct);
}
Console.WriteLine("PASS SQLite native migration, no seed, idempotent restart, workspace CRUD, stale rejection and host audit.");
static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static T Success<T>(Result<T> result) { Require(result.IsSuccess, "Direct handler failed."); return result.Value; }
static void SetActor(IServiceProvider services, Guid id)
    => services.GetRequiredService<ActorScope>().Actor = id == Guid.Empty ? CurrentActorInfo.Anonymous
        : new CurrentActorInfo(id, null, UserRole.Admin, null, true, true);
sealed class ActorScope { public CurrentActorInfo Actor { get; set; } = CurrentActorInfo.Anonymous; }
