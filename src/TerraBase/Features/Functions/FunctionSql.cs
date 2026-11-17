using System.Text.Json;
using TerraBase.Features.Sql;
using TerraBase.Hosting;

namespace TerraBase.Features.Functions;

internal sealed class FunctionSql(ISqlSession session, string? requestId, TimeProvider time)
{
    public const int MaxCalls = 256;

    private Task _tail = Task.CompletedTask;
    private int _calls;
    private TimeSpan _elapsed;

    public int SqlCalls => _calls;

    public long SqlMs => (long)Math.Ceiling(_elapsed.TotalMilliseconds);

    public long RowsRead { get; private set; }

    public long RowsWritten { get; private set; }

    public Task<string> CallAsync(string op, string json, CancellationToken cancellationToken)
    {
        if (++_calls > MaxCalls)
        {
            return Task.FromResult(Failure(SqlErrors.TooBig($"an invocation makes at most {MaxCalls} sql calls")));
        }

        var call = AfterAsync(_tail, op, json, cancellationToken);
        _tail = call;
        return call;
    }

    public async Task DrainAsync()
    {
        try
        {
            await _tail.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<string> AfterAsync(Task previous, string op, string json, CancellationToken cancellationToken)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        var started = time.GetTimestamp();
        try
        {
            return op switch
            {
                "begin" => await ControlAsync("BEGIN", cancellationToken).ConfigureAwait(false),
                "commit" => await ControlAsync("COMMIT", cancellationToken).ConfigureAwait(false),
                "rollback" => await ControlAsync("ROLLBACK", cancellationToken).ConfigureAwait(false),
                _ => await ExecuteAsync(json, cancellationToken).ConfigureAwait(false),
            };
        }
        finally
        {
            _elapsed += time.GetElapsedTime(started);
        }
    }

    private async Task<string> ExecuteAsync(string json, CancellationToken cancellationToken)
    {
        SqlExecuteRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(json, SqlJsonContext.Default.SqlExecuteRequest);
        }
        catch (JsonException)
        {
            return Failure(new SqlError(SqlErrors.InvalidRequest, "a statement is {sql, args, named} with JSON values"));
        }

        return SqlBatch.Commands(request?.Statements) switch
        {
            SqlBatchFailure invalid => Failure(invalid),
            List<SqlPlannedCommand> commands => await SqlBatch.RunAsync(session, commands, cancellationToken).ConfigureAwait(false) switch
            {
                SqlExecuteBody body => Complete(body),
                SqlBatchFailure failure => Failure(failure),
            },
        };
    }

    private string Complete(SqlExecuteBody body)
    {
        RowsRead += body.Results.Sum(result => (long)result.Rows.Count);
        RowsWritten += body.Results.Sum(result => result.RowsAffected);
        return JsonSerializer.Serialize(body, SqlJsonContext.Default.SqlExecuteBody);
    }

    private async Task<string> ControlAsync(string sql, CancellationToken cancellationToken) =>
        await session.ExecuteAsync(SqlBatch.Control(sql), cancellationToken).ConfigureAwait(false) switch
        {
            SqlError error => Failure(error),
            SqlResult => """{"results":[]}""",
        };

    private string Failure(SqlError error) => Failure(new SqlBatchFailure(error, 0));

    private string Failure(SqlBatchFailure failure) => JsonSerializer.Serialize(SqlBatch.Body(failure, requestId), SqlJsonContext.Default.SqlErrorBody);
}
