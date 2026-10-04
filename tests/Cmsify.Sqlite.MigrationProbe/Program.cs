using System.Data.Common;
using System.Diagnostics;
using System.Security.AccessControl;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.Extensions;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Sqlite.Extensions;
using Cmsify.Sqlite.MigrationProbe.Fixtures;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

var watch = Stopwatch.StartNew();
try
{
    var operation = args[0];
    string Option(string name)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0 || index + 1 >= args.Length) throw new ArgumentException($"Missing option {name}.");
        return args[index + 1];
    }
    var database = Option("--database");
    if (!Path.IsPathFullyQualified(database)) throw new ArgumentException("Database path must be absolute.");
    var checkpoint = Array.IndexOf(args, "--checkpoint") >= 0 ? Option("--checkpoint")
        : operation == "migrate" ? Path.Combine(Path.GetTempPath(), "cmsify-probe-" + Guid.NewGuid().ToString("N"))
        : throw new ArgumentException("Fixture operations require --checkpoint.");
    if (!Path.IsPathFullyQualified(checkpoint)) throw new ArgumentException("Checkpoint path must be absolute.");
    Directory.CreateDirectory(checkpoint);
    var modePath = Path.Combine(checkpoint, "mode");
    var mode = File.Exists(modePath) ? File.ReadAllText(modePath) : "normal";
    var connection = $"Data Source={database};Pooling=False;Foreign Keys=True;Default Timeout=2";
    using var cancellation = new CancellationTokenSource();
    var monitor = Task.Run(async () =>
    {
        while (!cancellation.IsCancellationRequested)
        {
            if (File.Exists(Path.Combine(checkpoint, "cancel"))) { cancellation.Cancel(); return; }
            await Task.Delay(50);
        }
    });
    File.WriteAllText(Path.Combine(checkpoint, "started"), Environment.ProcessId.ToString());
    if (mode == "barrier")
    {
        var barrierWatch = Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(checkpoint, "release")))
        {
            if (barrierWatch.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Process start barrier expired.");
            await Task.Delay(50, cancellation.Token);
        }
    }
    if (operation == "migrate")
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor());
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Cmsify"] = connection }).Build();
        services.AddCmsifySqliteInfrastructure(configuration, new() { Workers = CmsifyWorkers.None });
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ICmsifyDatabaseMigrator>().MigrateAsync(cancellation.Token);
        await Settings(scope.ServiceProvider.GetRequiredService<CmsifyDbContext>().Database.GetDbConnection(), database);
    }
    else
    {
        var options = new DbContextOptionsBuilder<UpgradeContext>().UseSqlite(connection, b => b.MigrationsHistoryTable("__FixtureHistory"))
            .AddInterceptors(new CheckpointInterceptor(checkpoint, mode)).Options;
        await using var context = new UpgradeContext(options);
        if (operation == "fixture-init")
        {
            await context.GetService<IMigrator>().MigrateAsync("20261004022836_FixtureInitial", cancellation.Token);
            await context.Database.ExecuteSqlRawAsync("INSERT INTO fixture_parents (id) VALUES (1); INSERT INTO fixture_children (id,parent_id,name) VALUES (7,1,'retained');", cancellation.Token);
        }
        else if (operation == "fixture-verify-old")
        {
            var history = await context.Database.GetAppliedMigrationsAsync(cancellation.Token);
            if (!history.SequenceEqual(new[] { "20261004022836_FixtureInitial" })) throw new InvalidOperationException("Restore history differs from older model.");
            await using var older = new OlderUpgradeContext(new DbContextOptionsBuilder<OlderUpgradeContext>().UseSqlite(connection).Options);
            older.Add(new FixtureChild { Id = 9, ParentId = 1, Name = null });
            await older.SaveChangesAsync(cancellation.Token);
            if (await older.Set<FixtureChild>().CountAsync(cancellation.Token) != 2) throw new InvalidOperationException("Older fixture rows lost.");
        }
        else if (operation == "fixture-upgrade") await context.Database.MigrateAsync(cancellation.Token);
        else throw new ArgumentException("Unknown operation.");
        await Settings(context.Database.GetDbConnection(), database);
    }
    cancellation.Cancel();
    await monitor;
    Console.WriteLine($"SUCCESS elapsed_ms={watch.ElapsedMilliseconds}");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"FAILURE elapsed_ms={watch.ElapsedMilliseconds}: {exception}");
    return 1;
}

static async Task Settings(DbConnection connection, string database)
{
    await connection.OpenAsync();
    foreach (var setting in new[] { "journal_mode", "foreign_keys", "busy_timeout", "integrity_check" })
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA " + setting;
        Console.WriteLine($"{setting}={await command.ExecuteScalarAsync()}");
    }
    Console.WriteLine($"file_attributes={File.GetAttributes(database)}; unix_mode={(OperatingSystem.IsWindows() ? "n/a" : File.GetUnixFileMode(database).ToString())}");
    if (OperatingSystem.IsWindows()) Console.WriteLine("file_acl=" + new FileInfo(database).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
    Console.WriteLine($"managed_default_timeout={((SqliteConnection)connection).DefaultTimeout}s");
    await connection.CloseAsync();
}

internal sealed class CheckpointInterceptor(string checkpoint, string mode) : DbCommandInterceptor
{
    private async Task Before(DbCommand command, CancellationToken cancellationToken)
    {
        if (command.CommandText.Contains("__EFMigrationsLock", StringComparison.Ordinal))
        {
            Console.WriteLine("Native EF lock command: " + command.CommandText);
            if (command.CommandText.Contains("INSERT OR IGNORE", StringComparison.Ordinal))
                File.WriteAllText(Path.Combine(checkpoint, "lock-attempt"), "native EF lock acquisition attempted");
        }
        if (!IsRebuild(command)) return;
        Console.WriteLine("REBUILD statement reached under native EF migration lock");
        File.WriteAllText(Path.Combine(checkpoint, "locked"), "native EF lock acquired");
        if (mode == "fail-before") throw new InvalidOperationException("Injected fixture failure before rebuild statement.");
        if (mode == "hold")
        {
            var bound = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(checkpoint, "release")))
            {
                if (bound.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Fixture release checkpoint expired.");
                await Task.Delay(50, cancellationToken);
            }
        }
    }
    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    { await Before(command, cancellationToken); return result; }
    public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    { await Before(command, cancellationToken); return result; }
    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    { await Before(command, cancellationToken); return result; }
    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (mode == "fail-after" && IsRebuild(command)) throw new InvalidOperationException("Injected fixture failure after rebuild statement.");
        return ValueTask.FromResult(result);
    }
    private static bool IsRebuild(DbCommand command) => command.CommandText.Contains("CREATE TABLE \"ef_temp_fixture_children\"", StringComparison.Ordinal);
}
