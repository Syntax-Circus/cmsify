using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Shouldly;

namespace Cmsify.Infrastructure.Tests;

public sealed class SqliteMigrationRecoveryTests
{
    [Fact]
    public async Task CompletedCapture_PreservesElapsedTimeThroughRepeatedFinishAndDisposal()
    {
        await using var fixture = new MigrationProcesses();
        var child = fixture.Start("fixture-init");
        (await child.Finish()).ShouldBe(0);
        var resultPath = Path.Combine(child.Checkpoints, "result.log");
        var firstResult = await File.ReadAllTextAsync(resultPath, TestContext.Current.CancellationToken);
        var firstOutput = child.Output;
        await Task.Delay(100, TestContext.Current.CancellationToken);
        (await child.Finish()).ShouldBe(0);
        await child.DisposeAsync();
        (await File.ReadAllTextAsync(resultPath, TestContext.Current.CancellationToken)).ShouldBe(firstResult);
        (await File.ReadAllTextAsync(Path.Combine(child.Checkpoints, "output.log"), TestContext.Current.CancellationToken)).ShouldBe(firstOutput);
    }

    [Fact]
    public async Task RelativeCheckpoint_IsRejectedWithoutFilesystemSideEffects()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cmsify-invalid-checkpoint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Cmsify.Sqlite.MigrationProbe.dll"));
        foreach (var argument in new[] { "migrate", "--database", Path.Combine(directory, "fixture.db"), "--checkpoint", "relative-checkpoint" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        bound.CancelAfter(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(bound.Token); }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: false);
                using var cleanupBound = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(cleanupBound.Token);
            }
        }
        process.ExitCode.ShouldNotBe(0);
        (await stdout + await stderr).ShouldContain("Checkpoint path must be absolute.");
        Directory.GetFileSystemEntries(directory).ShouldBeEmpty();
        Directory.Delete(directory);
    }

    [Fact]
    public async Task UpgradeRebuild_PreservesRowsAndConstraints()
    {
        await using var fixture = new MigrationProcesses();
        await fixture.Initialize();
        File.Copy(fixture.Database, fixture.Database + ".pre-upgrade");
        await using var child = fixture.Start("fixture-upgrade");
        (await child.Finish()).ShouldBe(0);
        await fixture.VerifyUpgraded();
        File.Copy(fixture.Database + ".pre-upgrade", fixture.Database + ".restored");
        await using var restore = fixture.Start("fixture-verify-old", fixture.Database + ".restored");
        (await restore.Finish()).ShouldBe(0);
        child.Output.ShouldContain("REBUILD");
    }

    [Fact]
    public async Task TwoMigrators_ProduceOneCompleteHistory()
    {
        await using var fixture = new MigrationProcesses();
        await using var first = fixture.Start("migrate", mode: "barrier");
        await using var second = fixture.Start("migrate", mode: "barrier");
        await first.Checkpoint("started");
        await second.Checkpoint("started");
        File.WriteAllText(Path.Combine(first.Checkpoints, "release"), "release");
        File.WriteAllText(Path.Combine(second.Checkpoints, "release"), "release");
        (await first.Finish()).ShouldBe(0);
        (await second.Finish()).ShouldBe(0);
        (await fixture.Scalar("SELECT COUNT(*) FROM __CmsifyMigrationsHistory")).ShouldBe(1L);
        (await fixture.Scalar("SELECT COUNT(*) FROM workspaces")).ShouldBe(0L);
        await using var repeat = fixture.Start("migrate");
        (await repeat.Finish()).ShouldBe(0);
        (await fixture.Scalar("SELECT COUNT(*) FROM __CmsifyMigrationsHistory")).ShouldBe(1L);
    }

    [Fact]
    public async Task CancelledWait_DoesNotReportSuccess()
    {
        await using var fixture = new MigrationProcesses();
        await fixture.Initialize();
        await using var holder = fixture.Start("fixture-upgrade", mode: "hold");
        await holder.Checkpoint("locked");
        await using var waiter = fixture.Start("fixture-upgrade");
        await waiter.Checkpoint("lock-attempt");
        File.WriteAllText(Path.Combine(waiter.Checkpoints, "cancel"), "cancel");
        (await waiter.Finish()).ShouldNotBe(0);
        waiter.Output.ShouldNotContain("SUCCESS");
        waiter.Output.ShouldContain("AcquireDatabaseLockAsync");
        (await fixture.Scalar("SELECT COUNT(*) FROM __EFMigrationsLock")).ShouldBe(1L);
        holder.Process.HasExited.ShouldBeFalse();
        File.WriteAllText(Path.Combine(holder.Checkpoints, "release"), "release");
        (await holder.Finish()).ShouldBe(0);
        await fixture.VerifyUpgraded();
    }

    [Fact]
    public async Task KilledMigrator_RequiresExplicitRecovery()
    {
        await using var fixture = new MigrationProcesses();
        await fixture.Initialize();
        File.Copy(fixture.Database, fixture.Database + ".pre-upgrade");
        await using var holder = fixture.Start("fixture-upgrade", mode: "hold");
        await holder.Checkpoint("locked");
        (await fixture.Scalar("SELECT COUNT(*) FROM __EFMigrationsLock")).ShouldBe(1L);
        await holder.KillOwned();
        holder.Process.ExitCode.ShouldNotBe(0);
        holder.Output.ShouldNotContain("SUCCESS");
        await using (var retry = fixture.Start("fixture-upgrade"))
        {
            await retry.Checkpoint("lock-attempt");
            File.WriteAllText(Path.Combine(retry.Checkpoints, "cancel"), "cancel");
            (await retry.Finish()).ShouldNotBe(0);
            retry.Output.ShouldNotContain("SUCCESS");
            retry.Output.ShouldContain("AcquireDatabaseLockAsync");
        }
        // All owned migrators have stopped. Pooling is disabled; this is a closed-file backup.
        File.Exists(fixture.Database + "-wal").ShouldBeFalse();
        File.Copy(fixture.Database, fixture.Database + ".abandoned");
        (await fixture.Scalar("SELECT COUNT(*) FROM __FixtureHistory")).ShouldBe(1L);
        (await fixture.Scalar("SELECT name FROM sqlite_schema WHERE name='fixture_children'")).ShouldBe("fixture_children");
        (await fixture.Scalar("SELECT COUNT(*) FROM __EFMigrationsLock")).ShouldBe(1L);
        (await fixture.Scalar("SELECT COUNT(*) FROM __EFMigrationsLock WHERE Id=1")).ShouldBe(1L);
        (await fixture.Scalar("SELECT COUNT(*) FROM sqlite_schema WHERE name='ef_temp_fixture_children'")).ShouldBe(0L);
        (await fixture.Scalar("SELECT \"notnull\" FROM pragma_table_info('fixture_children') WHERE name='name'")).ShouldBe(0L);
        (await fixture.Scalar("PRAGMA integrity_check")).ShouldBe("ok");
        await fixture.Execute("DELETE FROM __EFMigrationsLock WHERE Id=1");
        await using var recovered = fixture.Start("fixture-upgrade");
        (await recovered.Finish()).ShouldBe(0);
        await fixture.VerifyUpgraded();
    }

    [Theory]
    [InlineData("fail-before")]
    [InlineData("fail-after")]
    public async Task FailedStatement_DoesNotReportSuccess_AndRetainsOriginal(string mode)
    {
        await using var fixture = new MigrationProcesses();
        await fixture.Initialize();
        await using var failed = fixture.Start("fixture-upgrade", mode: mode);
        (await failed.Finish()).ShouldNotBe(0);
        failed.Output.ShouldContain("Injected fixture failure");
        failed.Output.ShouldNotContain("SUCCESS");
        (await fixture.Scalar("SELECT COUNT(*) FROM __FixtureHistory")).ShouldBe(1L);
        (await fixture.Scalar("SELECT name FROM fixture_children WHERE id=7")).ShouldBe("retained");
        (await fixture.Scalar("PRAGMA integrity_check")).ShouldBe("ok");
        await using var retry = fixture.Start("fixture-upgrade");
        (await retry.Finish()).ShouldBe(0);
        await fixture.VerifyUpgraded();
    }
}

internal sealed class MigrationProcesses : IAsyncDisposable
{
    private readonly List<MigrationChild> _children = [];
    private readonly string _directory = Path.Combine(Environment.GetEnvironmentVariable("CMSIFY_SQLITE_RECOVERY_EVIDENCE") ?? Path.GetTempPath(), "cmsify-recovery-" + Guid.NewGuid().ToString("N"));
    public string Database => Path.Combine(_directory, "fixture.db");
    public MigrationProcesses() => Directory.CreateDirectory(_directory);
    public MigrationChild Start(string operation, string? database = null, string mode = "normal")
    {
        var child = new MigrationChild(operation, database ?? Database, Path.Combine(_directory, "process-" + _children.Count), mode);
        _children.Add(child);
        return child;
    }
    public async Task Initialize()
    {
        await using var child = Start("fixture-init");
        (await child.Finish()).ShouldBe(0);
        File.Exists(Database + "-wal").ShouldBeFalse();
    }
    public async Task<object?> Scalar(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={Database};Pooling=False;Foreign Keys=True;Default Timeout=2");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }
    public async Task Execute(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={Database};Pooling=False;Foreign Keys=True;Default Timeout=2");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
    public async Task VerifyUpgraded()
    {
        (await Scalar("SELECT COUNT(*) FROM __FixtureHistory")).ShouldBe(2L);
        (await Scalar("SELECT name FROM fixture_children WHERE id=7 AND parent_id=1")).ShouldBe("retained");
        (await Scalar("PRAGMA integrity_check")).ShouldBe("ok");
        (await Scalar("PRAGMA foreign_key_check")).ShouldBeNull();
        (await Should.ThrowAsync<SqliteException>(() => Execute("INSERT INTO fixture_children (id,parent_id,name) VALUES (8,999,'orphan')"))).SqliteExtendedErrorCode.ShouldBe(787);
        (await Should.ThrowAsync<SqliteException>(() => Execute("INSERT INTO fixture_children (id,parent_id,name) VALUES (8,1,NULL)"))).SqliteExtendedErrorCode.ShouldBe(1299);
        (await Should.ThrowAsync<SqliteException>(() => Execute("INSERT INTO fixture_children (id,parent_id,name) VALUES (8,1,'retained')"))).SqliteExtendedErrorCode.ShouldBe(2067);
    }
    public async ValueTask DisposeAsync()
    {
        foreach (var child in _children) await child.DisposeAsync();
        // Keep fixture databases and original stdout/stderr for investigation and evidence.
        Console.WriteLine($"SQLite recovery evidence: {_directory}");
    }
}

internal sealed class MigrationChild : IAsyncDisposable
{
    public Process Process { get; }
    public string Checkpoints { get; }
    private readonly Task<string> _stdout;
    private readonly Task<string> _stderr;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private bool _disposed;
    private bool _captured;
    public string Output { get; private set; } = "";
    public MigrationChild(string operation, string database, string checkpoints, string mode)
    {
        Checkpoints = checkpoints;
        Directory.CreateDirectory(checkpoints);
        File.WriteAllText(Path.Combine(checkpoints, "mode"), mode);
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Cmsify.Sqlite.MigrationProbe.dll"));
        foreach (var argument in new[] { operation, "--database", database, "--checkpoint", checkpoints }) start.ArgumentList.Add(argument);
        Process = Process.Start(start)!;
        _stdout = Process.StandardOutput.ReadToEndAsync();
        _stderr = Process.StandardError.ReadToEndAsync();
    }
    public async Task Checkpoint(string name)
    {
        var watch = Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(Checkpoints, name)))
        {
            if (Process.HasExited) throw new InvalidOperationException($"Child exited before {name}: {await Finish()}: {Output}");
            if (watch.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException($"Checkpoint {name}, owned PID {Process.Id}");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }
    public async Task<int> Finish()
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        bound.CancelAfter(TimeSpan.FromSeconds(20));
        try { await Process.WaitForExitAsync(bound.Token); }
        catch (OperationCanceledException) { await KillOwned(); throw new TimeoutException($"Owned child PID {Process.Id} exceeded wall-clock bound. {Output}"); }
        await Capture();
        return Process.ExitCode;
    }
    public async Task KillOwned()
    {
        if (!Process.HasExited) Process.Kill(entireProcessTree: false);
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Process.WaitForExitAsync(bound.Token);
        await Capture();
    }
    private async Task Capture()
    {
        _watch.Stop();
        if (_captured) return;
        Output = await _stdout + await _stderr;
        File.WriteAllText(Path.Combine(Checkpoints, "output.log"), Output);
        File.WriteAllText(Path.Combine(Checkpoints, "result.log"), $"PID={Process.Id}; exit={Process.ExitCode}; elapsed_ms={_watch.ElapsedMilliseconds}");
        _captured = true;
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        if (!Process.HasExited) await KillOwned(); else await Capture();
        Process.Dispose();
        _disposed = true;
    }
}
