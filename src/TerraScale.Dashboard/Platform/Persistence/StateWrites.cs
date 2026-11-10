using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace TerraScale.Dashboard.Platform.Persistence;

public sealed class StateWrites : DbCommandInterceptor
{
    public static StateWrites Instance { get; } = new();

    private StateWrites()
    {
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Begin(eventData);
        return ValueTask.FromResult(result);
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        End(eventData);
        return ValueTask.FromResult(result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Begin(eventData);
        return ValueTask.FromResult(result);
    }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        End(eventData);
        return ValueTask.FromResult(result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        Begin(eventData);
        return ValueTask.FromResult(result);
    }

    public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result,
        CancellationToken cancellationToken = default)
    {
        End(eventData);
        return ValueTask.FromResult(result);
    }

    public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        End(eventData);
        return Task.CompletedTask;
    }

    public override Task CommandCanceledAsync(DbCommand command, CommandEndEventData eventData, CancellationToken cancellationToken = default)
    {
        End(eventData);
        return Task.CompletedTask;
    }

    private static void Begin(CommandEventData eventData)
    {
        if (Writes(eventData))
        {
            ServerTiming.BeginStateWrite();
        }
    }

    private static void End(CommandEventData eventData)
    {
        if (Writes(eventData))
        {
            ServerTiming.EndStateWrite();
        }
    }

    private static bool Writes(CommandEventData eventData)
    {
        return eventData.CommandSource is not CommandSource.LinqQuery;
    }
}
