using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Cmsify.Infrastructure.Tests;

internal sealed class CommandCountingInterceptor : DbCommandInterceptor
{
    private readonly object sync = new();
    private readonly List<string> commands = [];
    private bool isMeasuring;

    public int CommandCount
    {
        get
        {
            lock (sync)
            {
                return commands.Count;
            }
        }
    }

    public IReadOnlyList<string> Commands
    {
        get
        {
            lock (sync)
            {
                return commands.ToArray();
            }
        }
    }

    public IDisposable BeginMeasurement()
    {
        lock (sync)
        {
            if (isMeasuring)
            {
                throw new InvalidOperationException("A command measurement is already active.");
            }

            commands.Clear();
            isMeasuring = true;
        }

        return new MeasurementScope(this);
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        Record(command);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result)
    {
        Record(command);
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result)
    {
        Record(command);
        return base.ScalarExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    private void Record(DbCommand command)
    {
        lock (sync)
        {
            if (isMeasuring)
            {
                commands.Add(command.CommandText);
            }
        }
    }

    private void EndMeasurement()
    {
        lock (sync)
        {
            isMeasuring = false;
        }
    }

    private sealed class MeasurementScope(CommandCountingInterceptor owner) : IDisposable
    {
        private CommandCountingInterceptor? owner = owner;

        public void Dispose() => Interlocked.Exchange(ref owner, null)?.EndMeasurement();
    }
}
