using System.Diagnostics;

namespace TerraBase.Storage;

internal sealed record LocalItemPrepared(CommitMetadata? Commit);

internal readonly union LocalItemOutcome(LocalItemPrepared, SqlError, StrongRequired);

internal sealed partial class DatabaseSession
{
    private const string ItemSavepoint = "SAVEPOINT _tb_item;";
    private const string ReleaseItem = "RELEASE _tb_item;";
    private const string RollbackItem = "ROLLBACK TO _tb_item; RELEASE _tb_item;";

    private closed record class LaneRequest;

    private sealed record class LocalWriteRequest(SqlStatementRequest Request, TaskCompletionSource<SqlWriteOutcome> Completion, CancellationToken CancellationToken) : LaneRequest;

    private sealed record class StageRequest(SqlStatementRequest Request, TaskCompletionSource<StageWriteOutcome> Completion, CancellationToken CancellationToken) : LaneRequest;

    private sealed record class TransactionRequest(WriteTransaction Transaction) : LaneRequest;

    private sealed record class RemoteApplyRequest(IReadOnlyList<CommitMetadata> Commits, bool AllowLocalOrigin, TaskCompletionSource<ApplyRemoteBatchOutcome> Completion, CancellationToken CancellationToken) : LaneRequest;

    private sealed record class CommitStagedRequest(StagedDescriptor Descriptor, HlcTimestamp CommitHlc, long? LeaseDeadline, TaskCompletionSource<CommitStagedOutcome> Completion) : LaneRequest;

    public async ValueTask<SqlWriteOutcome> ExecuteWriteAsync(SqlStatementRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var waited = await WaitForBookmarkAsync(request.After, Options.BookmarkWaitTimeout, cancellationToken);
        if (waited switch { BookmarkReached => false, StorageTimeout => true })
        {
            return new StorageTimeout("causal bookmark was not reached before the write");
        }

        var completion = NewCompletion<SqlWriteOutcome>();
        if (!await EnqueueAsync(new LocalWriteRequest(request, completion, cancellationToken), cancellationToken))
        {
            return Closing();
        }

        return await completion.Task;
    }

    public async ValueTask<StageWriteOutcome> StageWriteAsync(SqlStatementRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var waited = await WaitForBookmarkAsync(request.After, Options.BookmarkWaitTimeout, cancellationToken);
        if (waited switch { BookmarkReached => false, StorageTimeout => true })
        {
            return new StorageTimeout("causal bookmark was not reached before the staged write");
        }

        var completion = NewCompletion<StageWriteOutcome>();
        if (!await EnqueueAsync(new StageRequest(request, completion, cancellationToken), cancellationToken))
        {
            return Closing();
        }

        return await completion.Task;
    }

    public async ValueTask<CommitStagedOutcome> CommitStagedAsync(StagedWriteToken token, HlcTimestamp commitHlc, long? leaseDeadline, CancellationToken cancellationToken)
    {
        if (TakeStaged(token) is not { } descriptor)
        {
            return new StagedWriteMissing(token, "staged write not found or expired");
        }

        var completion = NewCompletion<CommitStagedOutcome>();
        var request = new CommitStagedRequest(descriptor, commitHlc, leaseDeadline, completion);
        var queued = leaseDeadline is null
            ? await EnqueueAsync(request, cancellationToken)
            : _requests.Writer.TryWrite(request);
        if (!queued)
        {
            return new StorageBusy("the writer lane cannot queue the staged commit", TimeSpan.Zero);
        }

        return await completion.Task;
    }

    public ValueTask<AbortStagedOutcome> AbortStagedAsync(StagedWriteToken token, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AbortStagedOutcome result = TakeStaged(token) is null ? new StagedWriteMissing(token, "staged write not found or expired") : new StagedWriteAborted();
        return ValueTask.FromResult(result);
    }

    public async ValueTask<ApplyRemoteBatchOutcome> ApplyRemoteBatchAsync(RemoteCommitBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.DatabaseId != DatabaseId)
        {
            return new StorageCorrupted(DatabaseId, "remote batch targets a different database");
        }

        var completion = NewCompletion<ApplyRemoteBatchOutcome>();
        if (!await EnqueueAsync(new RemoteApplyRequest(batch.Commits, false, completion, cancellationToken), cancellationToken))
        {
            return Closing();
        }

        return await completion.Task;
    }

    internal async ValueTask<ApplyRemoteBatchOutcome> ReplayAsync(IReadOnlyList<CommitMetadata> commits, CancellationToken cancellationToken)
    {
        var completion = NewCompletion<ApplyRemoteBatchOutcome>();
        if (!await EnqueueAsync(new RemoteApplyRequest(commits, true, completion, cancellationToken), cancellationToken))
        {
            return Closing();
        }

        return await completion.Task;
    }

    public async ValueTask<BeginTransactionOutcome> BeginTransactionAsync(SqlTransactionOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var waited = await WaitForBookmarkAsync(options.After, Options.BookmarkWaitTimeout, cancellationToken);
        if (waited switch { BookmarkReached => false, StorageTimeout => true })
        {
            return new StorageTimeout("causal bookmark was not reached before the transaction");
        }

        if (options.Access == SqlAccess.Read)
        {
            if (await RentReaderAsync(cancellationToken) is not { } reader)
            {
                return Closing();
            }

            var begun = reader.Exec("BEGIN;");
            if (!begun.IsOk)
            {
                ReturnReader(reader);
                return begun.ToBusy();
            }

            var readTransaction = new ReadTransaction(this, reader, options, Ulid.NewUlid(_context.Time.GetUtcNow()), Options.TransactionIdleTimeout, _context.Time);
            if (!RegisterReadTransaction(readTransaction))
            {
                readTransaction.Abandon();
                return Closing();
            }

            return AsResult(readTransaction);
        }

        var transaction = new WriteTransaction(options, Ulid.NewUlid(_context.Time.GetUtcNow()));
        if (!await EnqueueAsync(new TransactionRequest(transaction), cancellationToken))
        {
            return Closing();
        }

        var started = transaction.Started.Task;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var winner = await Task.WhenAny(started, Task.Delay(Options.LaneWaitTimeout, _context.Time, timeout.Token));
        await timeout.CancelAsync();
        cancellationToken.ThrowIfCancellationRequested();
        if (winner != started)
        {
            transaction.Abandon();
            return new StorageTimeout("writer lane did not start the transaction in time");
        }

        var start = await started;
        return start switch
        {
            TransactionStarted => AsResult(transaction),
            StorageBusy busy => busy,
        };

        static BeginTransactionOutcome AsResult(ISqlTransaction value) => value;
    }

    internal StagedWrite StageRead(ReadTransaction transaction)
    {
        var snapshot = _context.Clock.Tick();
        var staged = new StagedWrite(
            new StagedWriteToken(Ulid.NewUlid(_context.Time.GetUtcNow())),
            DatabaseId,
            transaction.TxId,
            snapshot,
            SchemaEpoch,
            [],
            [.. transaction.ReadTables.Order(StringComparer.Ordinal)],
            [],
            false,
            [.. transaction.Results],
            ReadOnlyMemory<byte>.Empty);
        RegisterStaged(new StagedDescriptor(staged, [], _context.Time.GetUtcNow()));
        return staged;
    }

    private async Task RunLaneAsync()
    {
        var reader = _requests.Reader;
        List<LaneRequest> drained = new(Options.MaxBatchSize);
        List<LaneRequest> group = new(Options.MaxBatchSize);
        while (await reader.WaitToReadAsync(CancellationToken.None))
        {
            drained.Clear();
            while (drained.Count < Options.MaxBatchSize && reader.TryRead(out var request))
            {
                drained.Add(request);
            }

            group.Clear();
            foreach (var request in drained)
            {
                if (IsGroupable(request))
                {
                    group.Add(request);
                    continue;
                }

                ProcessGroup(group);
                group.Clear();
                await ProcessSerialAsync(request);
            }

            ProcessGroup(group);
        }

        while (reader.TryRead(out var abandoned))
        {
            _ = Fail(abandoned, Closing());
        }
    }

    private static bool IsGroupable(LaneRequest request) => request switch
    {
        LocalWriteRequest => true,
        StageRequest or TransactionRequest or RemoteApplyRequest or CommitStagedRequest => false,
    };

    private async Task ProcessSerialAsync(LaneRequest request)
    {
        _ = request switch
        {
            TransactionRequest transaction => await RunTransactionAsync(transaction.Transaction),
            RemoteApplyRequest remote => remote.Completion.TrySetResult(remote.CancellationToken.IsCancellationRequested ? Closing() : ApplyRemote(remote.Commits, remote.AllowLocalOrigin)),
            CommitStagedRequest staged => staged.Completion.TrySetResult(CommitStaged(staged.Descriptor, staged.CommitHlc, staged.LeaseDeadline)),
            LocalWriteRequest or StageRequest => ProcessGroup([request]),
        };
    }

    private bool ProcessGroup(List<LaneRequest> group)
    {
        if (group.Count == 0)
        {
            return true;
        }

        var nextSequence = 0L;
        var epoch = 0UL;
        List<(LocalWriteRequest Request, List<SqlResultSet> Results, CommitMetadata? Commit)> completed = [];
        var schemaTouched = false;
        var active = false;
        for (var index = 0; index < group.Count; index++)
        {
            if (!active)
            {
                var begun = BeginWrite(applying: false);
                if (!begun.IsOk)
                {
                    for (; index < group.Count; index++)
                    {
                        _ = Fail(group[index], begun.ToBusy());
                    }

                    return false;
                }

                nextSequence = _versions.Get(Region) + 1;
                epoch = SchemaEpoch;
                active = true;
            }

            var request = group[index];
            schemaTouched |= request switch
            {
                LocalWriteRequest local => RunLocalItem(local, completed, ref nextSequence, ref epoch),
                StageRequest stage => RunStageItem(stage),
                TransactionRequest or RemoteApplyRequest or CommitStagedRequest => RejectFromGroup(request),
            };

            if (!_writer.InTransaction)
            {
                var rolledBack = new StorageBusy("SQLite rolled back the write group; retry the request", TimeSpan.Zero);
                foreach (var (pending, _, _) in completed)
                {
                    _ = Fail(pending, rolledBack);
                }

                completed.Clear();
                _writer.InvalidateSchemaCache();
                schemaTouched = false;
                active = false;
            }
        }

        if (!active)
        {
            return false;
        }

        if (schemaTouched)
        {
            _writer.InvalidateSchemaCache();
        }

        var commits = completed.Select(static item => item.Commit).OfType<CommitMetadata>().ToArray();
        var outcome = CommitWrite(commits, epoch);
        foreach (var (request, results, commit) in completed)
        {
            request.Completion.TrySetResult(outcome.IsOk
                ? new SqlWriteResult(results, commit ?? NoOpCommit(request.Request.Mode))
                : outcome.ToSqlError(-1));
        }

        return outcome.IsOk;
    }

    private static bool RejectFromGroup(LaneRequest request)
    {
        _ = Fail(request, new StorageBusy("serial request cannot join a group commit", TimeSpan.Zero));
        return false;
    }

    private bool RunLocalItem(LocalWriteRequest local, List<(LocalWriteRequest Request, List<SqlResultSet> Results, CommitMetadata? Commit)> completed, ref long nextSequence, ref ulong epoch)
    {
        if (local.CancellationToken.IsCancellationRequested)
        {
            local.Completion.TrySetCanceled(local.CancellationToken);
            return false;
        }

        var (outcome, context) = RunLocal(local.Request, local.CancellationToken, ref nextSequence, ref epoch);
        _ = outcome switch
        {
            LocalItemPrepared prepared => Prepared(completed, local, context, prepared.Commit),
            SqlError error => local.Completion.TrySetResult(error),
            StrongRequired strong => local.Completion.TrySetResult(strong),
        };
        return context.IsSchemaChange;

        static bool Prepared(List<(LocalWriteRequest Request, List<SqlResultSet> Results, CommitMetadata? Commit)> completed, LocalWriteRequest local, WriteContext context, CommitMetadata? commit)
        {
            completed.Add((local, context.Results, commit));
            return true;
        }
    }

    private bool RunStageItem(StageRequest stage)
    {
        if (stage.CancellationToken.IsCancellationRequested)
        {
            stage.Completion.TrySetCanceled(stage.CancellationToken);
            return false;
        }

        var (outcome, schemaTouched) = RunStage(stage.Request, stage.CancellationToken);
        stage.Completion.TrySetResult(outcome);
        return schemaTouched;
    }

    private (LocalItemOutcome Outcome, WriteContext Context) RunLocal(SqlStatementRequest request, CancellationToken cancellationToken, ref long nextSequence, ref ulong epoch)
    {
        var context = new WriteContext(request.Mode, analyzeReads: false, cancellationToken);
        var savepoint = _writer.Exec(ItemSavepoint);
        if (!savepoint.IsOk)
        {
            return (savepoint.ToSqlError(-1), context);
        }

        _capture.Begin();
        var executed = ExecuteStatements(context, request.Statements);
        LocalItemOutcome outcome = executed switch
        {
            SqlResultSet => FinishLocalItem(context, ref nextSequence, ref epoch),
            SqlError error => AbortItem(context, error),
            StrongRequired strong => AbortItem(context, strong),
        };
        return (outcome, context);
    }

    private LocalItemOutcome FinishLocalItem(WriteContext context, ref long nextSequence, ref ulong epoch)
    {
        var savedSequence = nextSequence;
        var savedEpoch = epoch;
        var finished = FinishLocal(context, ref nextSequence, ref epoch, out var commit);
        if (!finished.IsOk)
        {
            nextSequence = savedSequence;
            epoch = savedEpoch;
            RollbackItemSchema(context);
            return finished.ToSqlError(-1);
        }

        var released = _writer.Exec(ReleaseItem);
        if (!released.IsOk)
        {
            nextSequence = savedSequence;
            epoch = savedEpoch;
            RollbackItemSchema(context);
            return released.ToSqlError(-1);
        }

        return new LocalItemPrepared(commit);
    }

    private LocalItemOutcome AbortItem(WriteContext context, LocalItemOutcome failure)
    {
        _capture.End();
        RollbackItemSchema(context);
        return failure;
    }

    private StatementOutcome ExecuteStatements(WriteContext context, IReadOnlyList<SqlStatement> statements)
    {
        StatementOutcome last = EmptyResult;
        for (var index = 0; index < statements.Count; index++)
        {
            var executed = ExecuteStatement(context, statements[index], index);
            if (executed switch { SqlResultSet => false, SqlError or StrongRequired => true })
            {
                return executed;
            }

            last = executed;
        }

        return last;
    }

    private (StageWriteOutcome Outcome, bool SchemaTouched) RunStage(SqlStatementRequest request, CancellationToken cancellationToken)
    {
        var context = new WriteContext(ConsistencyMode.Strong, analyzeReads: true, cancellationToken);
        var savepoint = _writer.Exec(ItemSavepoint);
        if (!savepoint.IsOk)
        {
            return (savepoint.ToSqlError(-1), false);
        }

        _capture.Begin();
        for (var index = 0; index < request.Statements.Count; index++)
        {
            SqlError? failure = ExecuteStatement(context, request.Statements[index], index) switch
            {
                SqlResultSet => null,
                SqlError error => error,
                StrongRequired strong => new SqlError("strong_required", ReasonText(strong.Reason), "XX000", index, 0),
            };
            if (failure is not null)
            {
                _capture.End();
                RollbackItemSchema(context);
                return (failure, context.IsSchemaChange);
            }
        }

        var finished = FinishStage(context, out var descriptor);
        RollbackItemSchema(context);
        if (!finished.IsOk || descriptor is null)
        {
            return (finished.ToSqlError(-1), context.IsSchemaChange);
        }

        RegisterStaged(descriptor);
        return (descriptor.Staged, context.IsSchemaChange);
    }

    private async Task<bool> RunTransactionAsync(WriteTransaction transaction)
    {
        if (transaction.IsAbandoned)
        {
            return false;
        }

        var begun = BeginWrite(applying: false);
        if (!begun.IsOk)
        {
            return transaction.Started.TrySetResult(begun.ToBusy());
        }

        var context = new WriteContext(transaction.Options.Mode, analyzeReads: transaction.Options.Mode == ConsistencyMode.Strong, CancellationToken.None);
        _capture.Begin();
        Volatile.Write(ref _activeTransaction, transaction);
        transaction.Started.TrySetResult(new TransactionStarted());
        var finished = false;
        while (!finished)
        {
            var command = await transaction.NextCommandAsync(Options.TransactionIdleTimeout, _context.Time);
            finished = command switch
            {
                null => EndTransaction(context),
                WriteTransaction.ExecuteCommand execute => ExecuteInTransaction(context, execute),
                WriteTransaction.CommitCommand commit => CommitTransaction(context, commit),
                WriteTransaction.StageCommand stage => StageTransaction(context, stage),
                WriteTransaction.RollbackCommand rollback => RollbackTransaction(context, rollback),
            };
        }

        Volatile.Write(ref _activeTransaction, null);
        return true;
    }

    private bool ExecuteInTransaction(WriteContext context, WriteTransaction.ExecuteCommand execute)
    {
        context.CancellationToken = execute.CancellationToken;
        var executed = ExecuteStatement(context, execute.Statement, execute.Index);
        TransactionExecuteResult outcome = executed switch
        {
            SqlResultSet result => result,
            SqlError error => error,
            StrongRequired strong => TransactionStrong(context, strong, execute.Index),
        };
        if (_capture.Fault.Primary == SqliteNative.TooBig && _writer.InTransaction)
        {
            _ = _writer.Exec("ROLLBACK;");
        }

        execute.Completion.TrySetResult(outcome);
        return false;
    }

    private bool RollbackTransaction(WriteContext context, WriteTransaction.RollbackCommand rollback)
    {
        _ = EndTransaction(context);
        rollback.Completion.TrySetResult();
        return true;
    }

    private bool CommitTransaction(WriteContext context, WriteTransaction.CommitCommand commit)
    {
        if (context.Mode == ConsistencyMode.Fast && context.StrongReason is { } reason)
        {
            _ = EndTransaction(context);
            commit.Completion.TrySetResult(new StrongRequired(DatabaseId, reason));
            return true;
        }

        var nextSequence = _versions.Get(Region) + 1;
        var epoch = SchemaEpoch;
        var finished = FinishLocal(context, ref nextSequence, ref epoch, out var metadata);
        if (!finished.IsOk)
        {
            _ = EndTransaction(context);
            commit.Completion.TrySetResult(finished.ToBusy());
            return true;
        }

        var committed = CommitWrite(metadata is null ? [] : [metadata], epoch);
        if (context.IsSchemaChange)
        {
            _writer.InvalidateSchemaCache();
        }

        commit.Completion.TrySetResult(committed.IsOk ? metadata ?? NoOpCommit(context.Mode) : committed.ToBusy());
        return true;
    }

    private bool StageTransaction(WriteContext context, WriteTransaction.StageCommand stage)
    {
        var staged = FinishStage(context, out var descriptor);
        _ = EndTransaction(context);
        if (!staged.IsOk || descriptor is null)
        {
            stage.Completion.TrySetResult(staged.ToBusy());
            return true;
        }

        RegisterStaged(descriptor);
        stage.Completion.TrySetResult(descriptor.Staged);
        return true;
    }

    private static SqlError TransactionStrong(WriteContext context, StrongRequired strong, int index)
    {
        context.StrongReason ??= strong.Reason;
        return new SqlError("strong_required", string.Concat("statement requires a strong transaction: ", ReasonText(strong.Reason)), "XX000", index, 0);
    }

    private bool EndTransaction(WriteContext context)
    {
        _capture.End();
        if (_writer.InTransaction)
        {
            _ = _writer.Exec("ROLLBACK;");
        }

        if (context.IsSchemaChange)
        {
            _writer.InvalidateSchemaCache();
        }

        return true;
    }

    private void RollbackItemSchema(WriteContext context)
    {
        if (_writer.InTransaction)
        {
            _ = _writer.Exec(RollbackItem);
        }

        if (context.IsSchemaChange)
        {
            _writer.InvalidateSchemaCache();
        }
    }

    private static string ReasonText(StrongRequiredReason reason) => reason switch
    {
        StrongRequiredReason.SchemaChange => "schema_change",
        StrongRequiredReason.SecondaryUniqueIndex => "secondary_unique_index",
        StrongRequiredReason.StrictTable => "strict_table",
        _ => throw new UnreachableException(),
    };

    private SqliteStatus BeginWrite(bool applying)
    {
        var status = _writer.SetApplyMode(applying);
        if (status.IsOk)
        {
            status = _writer.Exec("BEGIN IMMEDIATE;");
        }

        if (status.IsOk)
        {
            status = _writer.ValidateSchemaCache();
            if (!status.IsOk)
            {
                _ = _writer.Exec("ROLLBACK;");
            }
        }

        return status;
    }

    private SqliteStatus CommitWrite(CommitMetadata[] commits, ulong epoch)
    {
        if (commits.Length == 0)
        {
            var empty = _writer.Exec("COMMIT;");
            if (!empty.IsOk && _writer.InTransaction)
            {
                _ = _writer.Exec("ROLLBACK;");
            }

            return empty;
        }

        if (EnsureLog(Region, _versions.Get(Region)) is not { } log)
        {
            _ = _writer.Exec("ROLLBACK;");
            return SqliteStatus.Fail("local commit log unavailable");
        }

        var previousLast = log.LastSequence;
        var appended = log.Append(commits, out var previousLength);
        if (!appended.IsOk)
        {
            _ = _writer.Exec("ROLLBACK;");
            return appended;
        }

        var committed = _writer.Exec("COMMIT;");
        if (!committed.IsOk)
        {
            log.Truncate(previousLength, previousLast);
            if (_writer.InTransaction)
            {
                _ = _writer.Exec("ROLLBACK;");
            }

            _writer.InvalidateSchemaCache();
            return committed;
        }

        Interlocked.Exchange(ref _schemaEpoch, (long)epoch);
        _versions.Set([new(Region, commits[^1].Sequence)]);
        log.SealIfDue();
        return SqliteStatus.Success;
    }

    internal CommitMetadata NoOpCommit(ConsistencyMode mode) =>
        new(Ulid.NewUlid(_context.Time.GetUtcNow()), Region, 0, _context.Clock.Current, SchemaEpoch, mode, _versions.Snapshot(), ReadOnlyMemory<byte>.Empty);

    private static bool Fail(LaneRequest request, StorageBusy busy) => request switch
    {
        LocalWriteRequest local => local.Completion.TrySetResult(busy),
        StageRequest stage => stage.Completion.TrySetResult(busy),
        TransactionRequest transaction => transaction.Transaction.Started.TrySetResult(busy),
        RemoteApplyRequest remote => remote.Completion.TrySetResult(busy),
        CommitStagedRequest staged => staged.Completion.TrySetResult(busy),
    };

    private static TaskCompletionSource<T> NewCompletion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
