using System.Buffers;
using System.Text;
using TerraScale.Contracts;

namespace TerraScale.Platform.Features.Functions;

public sealed class FunctionSql(TerraBaseClient terraBase, Ulid database, string bookmark, TimeProvider timeProvider, ILogger logger) : IAsyncDisposable
{
    public const int MaxCalls = 256;

    private Task _tail = Task.CompletedTask;
    private TimeSpan _elapsed;
    private CausalBookmark _bookmark = CausalBookmark.TryDecode(bookmark, out var decoded) ? decoded : CausalBookmark.Empty;
    private FunctionSqlSession? _transaction;

    public int SqlCalls { get; private set; }

    public long SqlMs => (long)Math.Ceiling(_elapsed.TotalMilliseconds);

    public long RowsRead { get; private set; }

    public long RowsWritten { get; private set; }

    public string Bookmark => _bookmark.Encode();

    public Task<string> CallAsync(string operation, string json, CancellationToken cancellationToken)
    {
        if (++SqlCalls > MaxCalls)
        {
            return Task.FromResult(Failure("payload_too_large", $"an invocation makes at most {MaxCalls} sql calls", null, null));
        }

        var call = AfterAsync(_tail, operation, json, cancellationToken);
        _tail = call;
        return call;
    }

    public async Task DrainAsync() => await Task.WhenAny(_tail);

    public async ValueTask DisposeAsync()
    {
        await DrainAsync();
        if (_transaction is { } open)
        {
            _transaction = null;
            await open.DisposeAsync();
        }
    }

    private async Task<string> AfterAsync(Task previous, string operation, string json, CancellationToken cancellationToken)
    {
        await Task.WhenAny(previous);
        var started = timeProvider.GetTimestamp();
        var answer = operation switch
        {
            "begin" => await BeginAsync(cancellationToken),
            "commit" => await FinishAsync(SqlWsFrameTypes.Commit, cancellationToken),
            "rollback" => await FinishAsync(SqlWsFrameTypes.Rollback, cancellationToken),
            _ => await ExecuteAsync(json, cancellationToken),
        };
        _elapsed += timeProvider.GetElapsedTime(started);
        return answer;
    }

    private async Task<string> ExecuteAsync(string json, CancellationToken cancellationToken)
    {
        if (TerraBaseAnswers.Parse(Encoding.UTF8.GetBytes(json), TerraScaleContractsJsonContext.Default.SqlExecuteRequest) is not { Statements.Count: > 0 } request)
        {
            return Failure("invalid_request", "a statement is {sql, args, named} with JSON values", null, null);
        }

        if (_transaction is { } session)
        {
            var sessionOutcome = await session.SendAsync(SqlWsFrameTypes.Batch, request.Statements, Bookmark, cancellationToken);
            return sessionOutcome switch
            {
                SqlWsServerFrame frame => Frame(frame),
                PlatformUnavailable unavailable => Failure(unavailable.Code, unavailable.Message, null, null),
            };
        }

        var outcome = await terraBase.ExecuteLowLatencyAsync(database, request, SqlContext.After(Bookmark), cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse response => Complete(response.Results, response.Bookmark),
            SqlFailure failure => Failure(failure.Error.Code, failure.Error.Message, failure.Error.Statement, failure.Error.SqlState),
            PlatformNotFound missing => Failure(missing.Code, missing.Message ?? "the database does not exist", null, null),
            PlatformUnavailable unavailable => Failure(unavailable.Code, unavailable.Message, null, null),
        };
    }

    private async Task<string> BeginAsync(CancellationToken cancellationToken)
    {
        if (_transaction is not null)
        {
            return Failure("invalid_request", "a transaction is already open", null, null);
        }

        var outcome = await FunctionSqlSession.OpenAsync(terraBase, database, logger, cancellationToken);
        return outcome switch
        {
            FunctionSqlSession session => await StartAsync(session, cancellationToken),
            PlatformUnavailable unavailable => Failure(unavailable.Code, unavailable.Message, null, null),
        };
    }

    private async Task<string> StartAsync(FunctionSqlSession session, CancellationToken cancellationToken)
    {
        _transaction = session;
        var outcome = await session.SendAsync(SqlWsFrameTypes.Begin, null, Bookmark, cancellationToken);
        return outcome switch
        {
            SqlWsServerFrame frame => Frame(frame),
            PlatformUnavailable unavailable => Failure(unavailable.Code, unavailable.Message, null, null),
        };
    }

    private async Task<string> FinishAsync(string type, CancellationToken cancellationToken)
    {
        if (_transaction is not { } session)
        {
            return Failure("invalid_request", "no transaction is open", null, null);
        }

        _transaction = null;
        await using (session)
        {
            var outcome = await session.SendAsync(type, null, Bookmark, cancellationToken);
            return outcome switch
            {
                SqlWsServerFrame frame => Frame(frame),
                PlatformUnavailable unavailable => Failure(unavailable.Code, unavailable.Message, null, null),
            };
        }
    }

    private string Frame(SqlWsServerFrame frame) => frame.Error is { } error
        ? Failure(error.Code, error.Message, error.Statement, error.SqlState)
        : Complete(frame.Results ?? [], frame.Bookmark);

    private string Complete(IReadOnlyList<SqlHttpStatementResult> results, string? answered)
    {
        RowsRead += results.Sum(static result => (long)result.Rows.Count);
        RowsWritten += results.Sum(static result => result.RowsAffected);
        if (answered is { Length: > 0 } && CausalBookmark.TryDecode(answered, out var next))
        {
            _bookmark = _bookmark.Merge(next);
        }

        var output = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("results");
            JsonSerializer.Serialize(writer, results, FunctionsJsonContext.Default.IReadOnlyListSqlHttpStatementResult);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(output.WrittenSpan);
    }

    private static string Failure(string code, string message, int? statement, string? sqlState)
    {
        var output = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("error");
            writer.WriteString("code", code);
            writer.WriteString("message", message);
            if (statement is { } index)
            {
                writer.WriteNumber("statement", index);
            }

            if (sqlState is not null)
            {
                writer.WriteString("sqlState", sqlState);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(output.WrittenSpan);
    }
}
