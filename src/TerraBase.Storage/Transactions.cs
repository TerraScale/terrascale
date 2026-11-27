using System.Threading.Channels;

namespace TerraBase.Storage;

internal sealed record TransactionStarted;

internal readonly union TransactionStartOutcome(TransactionStarted, StorageBusy);

internal sealed class WriteTransaction(DatabaseSession session, SqlTransactionOptions options, Ulid txId) : ISqlTransaction
{
    private readonly Channel<Command> _commands = Channel.CreateUnbounded<Command>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Lock _timerGate = new();
    private ITimer? _idleTimer;
    private bool _timerDisposed;
    private Command? _current;
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
        DisposeIdleTimer();
        _commands.Writer.TryComplete();
    }

    public void DisposeIdleTimer()
    {
        lock (_timerGate)
        {
            if (_timerDisposed)
            {
                return;
            }

            _timerDisposed = true;
            _idleTimer?.Dispose();
            _idleTimer = null;
        }
    }

    public void Fault(StorageBusy busy)
    {
        Abandon();
        _ = Started.TrySetResult(busy);
        _ = FailCommand(Interlocked.Exchange(ref _current, null), busy);
        while (_commands.Reader.TryRead(out var queued))
        {
            _ = FailCommand(queued, busy);
        }
    }

    private static bool FailCommand(Command? command, StorageBusy busy) => command switch
    {
        null => false,
        ExecuteCommand execute => execute.Completion.TrySetResult(busy),
        CommitCommand commit => commit.Completion.TrySetResult(busy),
        StageCommand stage => stage.Completion.TrySetResult(busy),
        RollbackCommand rollback => rollback.Completion.TrySetResult(),
    };

    public async ValueTask<Command?> NextCommandAsync(TimeSpan idleTimeout, TimeProvider time)
    {
        if (_commands.Reader.TryRead(out var immediate))
        {
            Volatile.Write(ref _current, immediate);
            return immediate;
        }

        if (idleTimeout != Timeout.InfiniteTimeSpan && idleTimeout <= TimeSpan.Zero)
        {
            Volatile.Write(ref _current, null);
            Abandon();
            return null;
        }

        var armed = false;
        if (idleTimeout != Timeout.InfiniteTimeSpan)
        {
            ArmIdleTimer(idleTimeout, time);
            armed = true;
        }

        try
        {
            if (await _commands.Reader.WaitToReadAsync() && _commands.Reader.TryRead(out var next))
            {
                Volatile.Write(ref _current, next);
                return next;
            }
        }
        finally
        {
            if (armed)
            {
                DisarmIdleTimer();
            }
        }

        Volatile.Write(ref _current, null);
        Abandon();
        return null;
    }

    private void ArmIdleTimer(TimeSpan idleTimeout, TimeProvider time)
    {
        lock (_timerGate)
        {
            if (_timerDisposed)
            {
                return;
            }

            _idleTimer ??= time.CreateTimer(static state =>
            {
                if (state is WriteTransaction transaction)
                {
                    transaction.Abandon();
                }
            }, this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _ = _idleTimer.Change(idleTimeout, Timeout.InfiniteTimeSpan);
        }
    }

    private void DisarmIdleTimer()
    {
        lock (_timerGate)
        {
            if (!_timerDisposed && _idleTimer is not null)
            {
                _ = _idleTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
        }
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

    public ValueTask<SqlReadOutcome> ReadCatalogAsync(IReadOnlyList<SqlStatement> statements, CancellationToken cancellationToken) =>
        session.ExecuteReadAsync(new SqlStatementRequest(session.DatabaseId, statements, SqlAccess.Read, ConsistencyMode.Fast, CausalBookmark.Empty), cancellationToken);

    public async ValueTask<TransactionCommitLocalResult> CommitLocalAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
        cancellationToken.ThrowIfCancellationRequested();
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

internal sealed class ReadTransaction : ISqlTransaction
{
    private readonly DatabaseSession _session;
    private readonly SqliteConnection _reader;
    private readonly TimeSpan _idleTimeout;
    private readonly ITimer? _idleTimer;
    private readonly Lock _gate = new();
    private readonly Lock _interruptGate = new();
    private readonly StatementInfo _info = new();
    private SqliteConnection? _ownedReader;
    private int _statements;
    private bool _finished;

    public ReadTransaction(DatabaseSession session, SqliteConnection reader, SqlTransactionOptions options, Ulid txId, TimeSpan idleTimeout, TimeProvider time)
    {
        _session = session;
        _reader = reader;
        _ownedReader = reader;
        Options = options;
        TxId = txId;
        _idleTimeout = idleTimeout;
        if (idleTimeout != Timeout.InfiniteTimeSpan && idleTimeout > TimeSpan.Zero)
        {
            _idleTimer = time.CreateTimer(static state =>
            {
                if (state is ReadTransaction transaction)
                {
                    transaction.Abandon();
                }
            }, this, idleTimeout, Timeout.InfiniteTimeSpan);
        }
    }

    public Ulid TxId { get; }

    public SqlTransactionOptions Options { get; }

    public HashSet<string> ReadTables { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<SqlResultSet> Results { get; } = [];

    public void Abandon()
    {
        lock (_interruptGate)
        {
            _ownedReader?.Interrupt();
        }

        _ = Finish("ROLLBACK;");
    }

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
                _ = _idleTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                var executed = _session.ExecuteOnReader(_reader, statement, _statements++, _info, ReadTables, limitResults: true, cancellationToken);
                outcome = executed switch
                {
                    SqlResultSet result => Record(result),
                    SqlError error => error,
                };
                if (!_finished)
                {
                    _ = _idleTimer?.Change(_idleTimeout, Timeout.InfiniteTimeSpan);
                }
            }
        }

        return ValueTask.FromResult(outcome);
    }

    public ValueTask<SqlReadOutcome> ReadCatalogAsync(IReadOnlyList<SqlStatement> statements, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statements);
        cancellationToken.ThrowIfCancellationRequested();
        SqlReadOutcome outcome;
        lock (_gate)
        {
            if (_finished)
            {
                outcome = new StorageTimeout("transaction has finished");
            }
            else
            {
                _ = _idleTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                outcome = ReadCatalog(statements, cancellationToken);
                if (!_finished)
                {
                    _ = _idleTimer?.Change(_idleTimeout, Timeout.InfiniteTimeSpan);
                }
            }
        }

        return ValueTask.FromResult(outcome);
    }

    private SqlReadOutcome ReadCatalog(IReadOnlyList<SqlStatement> statements, CancellationToken cancellationToken)
    {
        List<SqlResultSet> results = [];
        var info = new StatementInfo();
        for (var index = 0; index < statements.Count; index++)
        {
            var executed = _session.ExecuteOnReader(_reader, statements[index], index, info, null, limitResults: true, cancellationToken);
            var failure = executed switch
            {
                SqlResultSet result => Collect(results, result),
                SqlError error => error,
            };
            if (failure is not null)
            {
                return failure;
            }
        }

        return new SqlReadResult(results, CausalBookmark.Empty);
    }

    private static SqlError? Collect(List<SqlResultSet> results, SqlResultSet result)
    {
        results.Add(result);
        return null;
    }

    public ValueTask<TransactionCommitLocalResult> CommitLocalAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TransactionCommitLocalResult outcome = Finish("COMMIT;")
            ? _session.NoOpCommit(Options.Mode)
            : new StorageTimeout("transaction has finished");
        return ValueTask.FromResult(outcome);
    }

    public ValueTask<TransactionStageResult> StageAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TransactionStageResult outcome;
        lock (_gate)
        {
            outcome = _finished ? new StorageTimeout("transaction has finished") : _session.StageRead(this);
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
            _idleTimer?.Dispose();
            lock (_interruptGate)
            {
                _ownedReader = null;
            }

            _ = _reader.Exec(sql);
            _session.UnregisterReadTransaction(this);
            _session.ReleaseReader(_reader);
            return true;
        }
    }
}
