using System.Threading.Channels;

namespace TerraBase.Storage;

internal sealed class WriteTransaction(SqlTransactionOptions options, Ulid txId) : ISqlTransaction
{
    private readonly Channel<Command> _commands = Channel.CreateUnbounded<Command>(new UnboundedChannelOptions { SingleReader = true });
    private int _statements;
    private int _finished;
    private int _abandoned;

    public Ulid TxId { get; } = txId;

    public SqlTransactionOptions Options { get; } = options;

    public TaskCompletionSource<Result<Done, StorageBusy>> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsAbandoned => Volatile.Read(ref _abandoned) != 0;

    public void Abandon()
    {
        Volatile.Write(ref _abandoned, 1);
        _commands.Writer.TryComplete();
    }

    public async ValueTask<Command?> NextCommandAsync(TimeSpan idleTimeout, TimeProvider time)
    {
        using var cancellation = new CancellationTokenSource();
        var ready = _commands.Reader.WaitToReadAsync(cancellation.Token).AsTask();
        var delay = Task.Delay(idleTimeout, time, cancellation.Token);
        var winner = await Task.WhenAny(ready, delay).ConfigureAwait(false);
        await cancellation.CancelAsync().ConfigureAwait(false);
        if (winner == ready && await ready.ConfigureAwait(false) && _commands.Reader.TryRead(out var command))
        {
            return command;
        }

        Abandon();
        return null;
    }

    public async ValueTask<Result<SqlResultSet, SqlError, StorageBusy, RequestTimeout>> ExecuteAsync(SqlStatement statement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statement);
        var completion = new TaskCompletionSource<Result<SqlResultSet, SqlError, StorageBusy, RequestTimeout>>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Volatile.Read(ref _finished) != 0 || !_commands.Writer.TryWrite(new ExecuteCommand(statement, Interlocked.Increment(ref _statements) - 1, completion, cancellationToken)))
        {
            return Expired();
        }

        return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Result<CommitMetadata, StrongRequired, SchemaMismatch, StorageBusy, RequestTimeout>> CommitLocalAsync(CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<Result<CommitMetadata, StrongRequired, SchemaMismatch, StorageBusy, RequestTimeout>>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.Exchange(ref _finished, 1) != 0 || !_commands.Writer.TryWrite(new CommitCommand(completion)))
        {
            return Expired();
        }

        _commands.Writer.TryComplete();
        return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Result<StagedWrite, SchemaMismatch, StorageBusy, RequestTimeout>> StageAsync(CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<Result<StagedWrite, SchemaMismatch, StorageBusy, RequestTimeout>>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.Exchange(ref _finished, 1) != 0 || !_commands.Writer.TryWrite(new StageCommand(completion)))
        {
            return Expired();
        }

        _commands.Writer.TryComplete();
        return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Done> RollbackAsync(CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<Done>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.Exchange(ref _finished, 1) != 0 || !_commands.Writer.TryWrite(new RollbackCommand(completion)))
        {
            return new Done();
        }

        _commands.Writer.TryComplete();
        return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => _ = await RollbackAsync(CancellationToken.None).ConfigureAwait(false);

    private static RequestTimeout Expired() => new("transaction has finished or expired");

    internal closed record class Command;

    internal sealed record class ExecuteCommand(SqlStatement Statement, int Index, TaskCompletionSource<Result<SqlResultSet, SqlError, StorageBusy, RequestTimeout>> Completion, CancellationToken CancellationToken) : Command;

    internal sealed record class CommitCommand(TaskCompletionSource<Result<CommitMetadata, StrongRequired, SchemaMismatch, StorageBusy, RequestTimeout>> Completion) : Command;

    internal sealed record class StageCommand(TaskCompletionSource<Result<StagedWrite, SchemaMismatch, StorageBusy, RequestTimeout>> Completion) : Command;

    internal sealed record class RollbackCommand(TaskCompletionSource<Done> Completion) : Command;
}

internal sealed class ReadTransaction(DatabaseSession session, SqliteConnection reader, SqlTransactionOptions options, Ulid txId) : ISqlTransaction
{
    private readonly Lock _gate = new();
    private readonly StatementInfo _info = new();
    private int _statements;
    private bool _finished;

    public Ulid TxId { get; } = txId;

    public SqlTransactionOptions Options { get; } = options;

    public HashSet<string> ReadTables { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<SqlResultSet> Results { get; } = [];

    public ValueTask<Result<SqlResultSet, SqlError, StorageBusy, RequestTimeout>> ExecuteAsync(SqlStatement statement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statement);
        cancellationToken.ThrowIfCancellationRequested();
        Result<SqlResultSet, SqlError, StorageBusy, RequestTimeout> outcome;
        lock (_gate)
        {
            if (_finished)
            {
                outcome = new RequestTimeout("transaction has finished");
            }
            else
            {
                outcome = DatabaseSession.ExecuteOnReader(reader, statement, _statements++, _info, ReadTables, cancellationToken) switch
                {
                    SqlResultSet result => Record(result),
                    SqlError error => error,
                };
            }
        }

        return ValueTask.FromResult(outcome);
    }

    public ValueTask<Result<CommitMetadata, StrongRequired, SchemaMismatch, StorageBusy, RequestTimeout>> CommitLocalAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Result<CommitMetadata, StrongRequired, SchemaMismatch, StorageBusy, RequestTimeout> outcome = Finish("COMMIT;")
            ? session.NoOpCommit(Options.Mode)
            : new RequestTimeout("transaction has finished");
        return ValueTask.FromResult(outcome);
    }

    public ValueTask<Result<StagedWrite, SchemaMismatch, StorageBusy, RequestTimeout>> StageAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Result<StagedWrite, SchemaMismatch, StorageBusy, RequestTimeout> outcome;
        lock (_gate)
        {
            outcome = _finished ? new RequestTimeout("transaction has finished") : session.StageRead(this);
        }

        _ = Finish("ROLLBACK;");
        return ValueTask.FromResult(outcome);
    }

    public ValueTask<Done> RollbackAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = Finish("ROLLBACK;");
        return ValueTask.FromResult(new Done());
    }

    public ValueTask DisposeAsync()
    {
        _ = Finish("ROLLBACK;");
        return ValueTask.CompletedTask;
    }

    private SqlResultSet Record(SqlResultSet result)
    {
        Results.Add(result);
        return result;
    }

    private bool Finish(string sql)
    {
        lock (_gate)
        {
            if (_finished)
            {
                return false;
            }

            _finished = true;
            _ = reader.Exec(sql);
            session.ReleaseReader(reader);
            return true;
        }
    }
}
