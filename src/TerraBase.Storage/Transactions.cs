using System.Threading.Channels;

namespace TerraBase.Storage;

internal sealed record TransactionStarted;

internal readonly union TransactionStartOutcome(TransactionStarted, StorageBusy);

internal sealed class WriteTransaction(SqlTransactionOptions options, Ulid txId) : ISqlTransaction
{
    private readonly Channel<Command> _commands = Channel.CreateUnbounded<Command>(new UnboundedChannelOptions { SingleReader = true });
    private int _statements;
    private int _finished;
    private int _abandoned;

    public Ulid TxId { get; } = txId;

    public SqlTransactionOptions Options { get; } = options;

    public TaskCompletionSource<TransactionStartOutcome> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
        var winner = await Task.WhenAny(ready, delay);
        await cancellation.CancelAsync();
        if (winner == ready && await ready && _commands.Reader.TryRead(out var command))
        {
            return command;
        }

        Abandon();
        return null;
    }

    public async ValueTask<TransactionExecuteResult> ExecuteAsync(SqlStatement statement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statement);
        var completion = new TaskCompletionSource<TransactionExecuteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Volatile.Read(ref _finished) != 0 || !_commands.Writer.TryWrite(new ExecuteCommand(statement, Interlocked.Increment(ref _statements) - 1, completion, cancellationToken)))
        {
            return Expired();
        }

        return await completion.Task.WaitAsync(cancellationToken);
    }

    public async ValueTask<TransactionCommitLocalResult> CommitLocalAsync(CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<TransactionCommitLocalResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.Exchange(ref _finished, 1) != 0 || !_commands.Writer.TryWrite(new CommitCommand(completion)))
        {
            return Expired();
        }

        _commands.Writer.TryComplete();
        return await completion.Task.WaitAsync(cancellationToken);
    }

    public async ValueTask<TransactionStageResult> StageAsync(CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<TransactionStageResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.Exchange(ref _finished, 1) != 0 || !_commands.Writer.TryWrite(new StageCommand(completion)))
        {
            return Expired();
        }

        _commands.Writer.TryComplete();
        return await completion.Task.WaitAsync(cancellationToken);
    }

    public async ValueTask RollbackAsync(CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.Exchange(ref _finished, 1) != 0 || !_commands.Writer.TryWrite(new RollbackCommand(completion)))
        {
            return;
        }

        _commands.Writer.TryComplete();
        await completion.Task.WaitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync() => await RollbackAsync(CancellationToken.None);

    private static StorageTimeout Expired() => new("transaction has finished or expired");

    internal closed record class Command;

    internal sealed record class ExecuteCommand(SqlStatement Statement, int Index, TaskCompletionSource<TransactionExecuteResult> Completion, CancellationToken CancellationToken) : Command;

    internal sealed record class CommitCommand(TaskCompletionSource<TransactionCommitLocalResult> Completion) : Command;

    internal sealed record class StageCommand(TaskCompletionSource<TransactionStageResult> Completion) : Command;

    internal sealed record class RollbackCommand(TaskCompletionSource Completion) : Command;
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

    public ValueTask<TransactionExecuteResult> ExecuteAsync(SqlStatement statement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statement);
        cancellationToken.ThrowIfCancellationRequested();
        TransactionExecuteResult outcome;
        lock (_gate)
        {
            if (_finished)
            {
                outcome = new StorageTimeout("transaction has finished");
            }
            else
            {
                var executed = DatabaseSession.ExecuteOnReader(reader, statement, _statements++, _info, ReadTables, limitResults: true, cancellationToken);
                outcome = executed switch
                {
                    SqlResultSet result => Record(result),
                    SqlError error => error,
                };
            }
        }

        return ValueTask.FromResult(outcome);
    }

    public ValueTask<TransactionCommitLocalResult> CommitLocalAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TransactionCommitLocalResult outcome = Finish("COMMIT;")
            ? session.NoOpCommit(Options.Mode)
            : new StorageTimeout("transaction has finished");
        return ValueTask.FromResult(outcome);
    }

    public ValueTask<TransactionStageResult> StageAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TransactionStageResult outcome;
        lock (_gate)
        {
            outcome = _finished ? new StorageTimeout("transaction has finished") : session.StageRead(this);
        }

        _ = Finish("ROLLBACK;");
        return ValueTask.FromResult(outcome);
    }

    public ValueTask RollbackAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = Finish("ROLLBACK;");
        return ValueTask.CompletedTask;
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
