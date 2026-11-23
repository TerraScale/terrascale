using System.Diagnostics;

namespace TerraBase.Storage;

internal sealed partial class DatabaseSession
{
    private const string ItemSavepoint = "SAVEPOINT _tb_item;";
    private const string ReleaseItem = "RELEASE _tb_item;";
    private const string RollbackItem = "ROLLBACK TO _tb_item; RELEASE _tb_item;";

    private closed record class LaneRequest;

    private sealed record class LocalWriteRequest(SqlStatementRequest Request, TaskCompletionSource<Result<SqlWriteResult, SqlError, StrongRequired, SchemaMismatch, StorageBusy, RequestTimeout>> Completion, CancellationToken CancellationToken) : LaneRequest;

    private sealed record class StageRequest(SqlStatementRequest Request, TaskCompletionSource<Result<StagedWrite, SqlError, SchemaMismatch, StorageBusy, RequestTimeout>> Completion, CancellationToken CancellationToken) : LaneRequest;

    private sealed record class TransactionRequest(WriteTransaction Transaction) : LaneRequest;

    private sealed record class RemoteApplyRequest(IReadOnlyList<CommitMetadata> Commits, bool AllowLocalOrigin, TaskCompletionSource<Result<RemoteApplyOutcome, SchemaMismatch, StorageCorrupted, StorageBusy>> Completion, CancellationToken CancellationToken) : LaneRequest;

    private sealed record class CommitStagedRequest(StagedDescriptor Descriptor, HlcTimestamp CommitHlc, TaskCompletionSource<Result<CommitMetadata, NotFound, ReadSetConflict, SchemaMismatch, StorageBusy>> Completion) : LaneRequest;

    public async ValueTask<Result<SqlWriteResult, SqlError, StrongRequired, SchemaMismatch, StorageBusy, RequestTimeout>> ExecuteWriteAsync(SqlStatementRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var waited = await WaitForBookmarkAsync(request.After, Options.BookmarkWaitTimeout, cancellationToken).ConfigureAwait(false);
        if (waited switch { Done => false, RequestTimeout => true })
        {
            return new RequestTimeout("causal bookmark was not reached before the write");
        }

        var completion = NewCompletion<Result<SqlWriteResult, SqlError, StrongRequired, SchemaMismatch, StorageBusy, RequestTimeout>>();
        if (!await EnqueueAsync(new LocalWriteRequest(request, completion, cancellationToken), cancellationToken).ConfigureAwait(false))
        {
            return Closing();
        }

        return await completion.Task.ConfigureAwait(false);
    }

    public async ValueTask<Result<StagedWrite, SqlError, SchemaMismatch, StorageBusy, RequestTimeout>> StageWriteAsync(SqlStatementRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var completion = NewCompletion<Result<StagedWrite, SqlError, SchemaMismatch, StorageBusy, RequestTimeout>>();
        if (!await EnqueueAsync(new StageRequest(request, completion, cancellationToken), cancellationToken).ConfigureAwait(false))
        {
            return Closing();
        }

        return await completion.Task.ConfigureAwait(false);
    }

    public async ValueTask<Result<CommitMetadata, NotFound, ReadSetConflict, SchemaMismatch, StorageBusy>> CommitStagedAsync(StagedWriteToken token, HlcTimestamp commitHlc, CancellationToken cancellationToken)
    {
        if (TakeStaged(token) is not { } descriptor)
        {
            return new NotFound("staged write not found or expired");
        }

        var completion = NewCompletion<Result<CommitMetadata, NotFound, ReadSetConflict, SchemaMismatch, StorageBusy>>();
        if (!await EnqueueAsync(new CommitStagedRequest(descriptor, commitHlc, completion), cancellationToken).ConfigureAwait(false))
        {
            return Closing();
        }

        return await completion.Task.ConfigureAwait(false);
    }

    public ValueTask<Result<Done, NotFound>> AbortStagedAsync(StagedWriteToken token, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Result<Done, NotFound> result = TakeStaged(token) is null ? new NotFound("staged write not found or expired") : new Done();
        return ValueTask.FromResult(result);
    }

    public async ValueTask<Result<RemoteApplyOutcome, SchemaMismatch, StorageCorrupted, StorageBusy>> ApplyRemoteBatchAsync(RemoteCommitBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.DatabaseId != DatabaseId)
        {
            return new StorageCorrupted(DatabaseId, "remote batch targets a different database");
        }

        var completion = NewCompletion<Result<RemoteApplyOutcome, SchemaMismatch, StorageCorrupted, StorageBusy>>();
        if (!await EnqueueAsync(new RemoteApplyRequest(batch.Commits, false, completion, cancellationToken), cancellationToken).ConfigureAwait(false))
        {
            return Closing();
        }

        return await completion.Task.ConfigureAwait(false);
    }

    internal async ValueTask<Result<RemoteApplyOutcome, SchemaMismatch, StorageCorrupted, StorageBusy>> ReplayAsync(IReadOnlyList<CommitMetadata> commits, CancellationToken cancellationToken)
    {
        var completion = NewCompletion<Result<RemoteApplyOutcome, SchemaMismatch, StorageCorrupted, StorageBusy>>();
        if (!await EnqueueAsync(new RemoteApplyRequest(commits, true, completion, cancellationToken), cancellationToken).ConfigureAwait(false))
        {
            return Closing();
        }

        return await completion.Task.ConfigureAwait(false);
    }

    public async ValueTask<Result<ISqlTransaction, StorageBusy, RequestTimeout>> BeginTransactionAsync(SqlTransactionOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var waited = await WaitForBookmarkAsync(options.After, Options.BookmarkWaitTimeout, cancellationToken).ConfigureAwait(false);
        if (waited switch { Done => false, RequestTimeout => true })
        {
            return new RequestTimeout("causal bookmark was not reached before the transaction");
        }

        if (options.Access == SqlAccess.Read)
        {
            if (await RentReaderAsync(cancellationToken).ConfigureAwait(false) is not { } reader)
            {
                return Closing();
            }

            var begun = reader.Exec("BEGIN;");
            if (!begun.IsOk)
            {
                ReturnReader(reader);
                return begun.ToBusy();
            }

            return AsResult(new ReadTransaction(this, reader, options, Ulid.NewUlid(_context.Time.GetUtcNow())));
        }

        var transaction = new WriteTransaction(options, Ulid.NewUlid(_context.Time.GetUtcNow()));
        if (!await EnqueueAsync(new TransactionRequest(transaction), cancellationToken).ConfigureAwait(false))
        {
            return Closing();
        }

        var started = transaction.Started.Task;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var winner = await Task.WhenAny(started, Task.Delay(Options.LaneWaitTimeout, _context.Time, timeout.Token)).ConfigureAwait(false);
        await timeout.CancelAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (winner != started)
        {
            transaction.Abandon();
            return new RequestTimeout("writer lane did not start the transaction in time");
        }

        return await started.ConfigureAwait(false) switch
        {
            Done => AsResult(transaction),
            StorageBusy busy => busy,
        };

        static Result<ISqlTransaction, StorageBusy, RequestTimeout> AsResult(ISqlTransaction value) => value;
    }

    internal Result<StagedWrite, SchemaMismatch, StorageBusy, RequestTimeout> StageRead(ReadTransaction transaction)
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
        while (await reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
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
                await ProcessSerialAsync(request).ConfigureAwait(false);
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
        LocalWriteRequest or StageRequest => true,
        TransactionRequest or RemoteApplyRequest or CommitStagedRequest => false,
    };

    private async Task ProcessSerialAsync(LaneRequest request)
    {
        _ = request switch
        {
            TransactionRequest transaction => await RunTransactionAsync(transaction.Transaction).ConfigureAwait(false),
            RemoteApplyRequest remote => remote.Completion.TrySetResult(remote.CancellationToken.IsCancellationRequested ? Closing() : ApplyRemote(remote.Commits, remote.AllowLocalOrigin)),
            CommitStagedRequest staged => staged.Completion.TrySetResult(CommitStaged(staged.Descriptor, staged.CommitHlc)),
            LocalWriteRequest or StageRequest => ProcessGroup([request]),
        };
    }

    private bool ProcessGroup(List<LaneRequest> group)
    {
        if (group.Count == 0)
        {
            return true;
        }

        var begun = BeginWrite(applying: false);
        if (!begun.IsOk)
        {
            foreach (var request in group)
            {
                _ = Fail(request, begun.ToBusy());
            }

            return false;
        }

        var nextSequence = _versions.Get(Region) + 1;
        var epoch = SchemaEpoch;
        List<(LocalWriteRequest Request, List<SqlResultSet> Results, CommitMetadata? Commit)> completed = [];
        var schemaTouched = false;
        foreach (var request in group)
        {
            schemaTouched |= request switch
            {
                LocalWriteRequest local => RunLocalItem(local, completed, ref nextSequence, ref epoch),
                StageRequest stage => RunStageItem(stage),
                TransactionRequest or RemoteApplyRequest or CommitStagedRequest => RejectFromGroup(request),
            };
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

        var (failure, context, commit) = RunLocal(local.Request, ref nextSequence, ref epoch);
        if (failure is { } outcome)
        {
            local.Completion.TrySetResult(outcome);
            return context.IsSchemaChange;
        }

        completed.Add((local, context.Results, commit));
        return context.IsSchemaChange;
    }

    private bool RunStageItem(StageRequest stage)
    {
        if (stage.CancellationToken.IsCancellationRequested)
        {
            stage.Completion.TrySetCanceled(stage.CancellationToken);
            return false;
        }

        var (outcome, schemaTouched) = RunStage(stage.Request);
        stage.Completion.TrySetResult(outcome);
        return schemaTouched;
    }

    private (Result<SqlWriteResult, SqlError, StrongRequired, SchemaMismatch, StorageBusy, RequestTimeout>? Failure, WriteContext Context, CommitMetadata? Commit) RunLocal(SqlStatementRequest request, ref long nextSequence, ref ulong epoch)
    {
        var context = new WriteContext(request.Mode, analyzeReads: false);
        var savepoint = _writer.Exec(ItemSavepoint);
        if (!savepoint.IsOk)
        {
            return (savepoint.ToSqlError(-1), context, null);
        }

        _capture.Begin();
        for (var index = 0; index < request.Statements.Count; index++)
        {
            Result<SqlWriteResult, SqlError, StrongRequired, SchemaMismatch, StorageBusy, RequestTimeout>? failure = ExecuteStatement(context, request.Statements[index], index) switch
            {
                SqlResultSet => null,
                SqlError error => error,
                StrongRequired strong => strong,
            };
            if (failure is { } statementFailure)
            {
                _capture.End();
                RollbackItemSchema(context);
                return (statementFailure, context, null);
            }
        }

        var finished = FinishLocal(context, ref nextSequence, ref epoch, out var commit);
        if (!finished.IsOk)
        {
            RollbackItemSchema(context);
            return (finished.ToSqlError(-1), context, null);
        }

        var released = _writer.Exec(ReleaseItem);
        return released.IsOk ? (null, context, commit) : (released.ToSqlError(-1), context, null);
    }

    private (Result<StagedWrite, SqlError, SchemaMismatch, StorageBusy, RequestTimeout> Outcome, bool SchemaTouched) RunStage(SqlStatementRequest request)
    {
        var context = new WriteContext(ConsistencyMode.Strong, analyzeReads: true);
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

        var context = new WriteContext(transaction.Options.Mode, analyzeReads: transaction.Options.Mode == ConsistencyMode.Strong);
        _capture.Begin();
        Volatile.Write(ref _activeTransaction, transaction);
        transaction.Started.TrySetResult(new Done());
        var finished = false;
        while (!finished)
        {
            var command = await transaction.NextCommandAsync(Options.TransactionIdleTimeout, _context.Time).ConfigureAwait(false);
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
        Result<SqlResultSet, SqlError, StorageBusy, RequestTimeout> outcome = ExecuteStatement(context, execute.Statement, execute.Index) switch
        {
            SqlResultSet result => result,
            SqlError error => error,
            StrongRequired strong => TransactionStrong(context, strong, execute.Index),
        };
        execute.Completion.TrySetResult(outcome);
        return false;
    }

    private bool RollbackTransaction(WriteContext context, WriteTransaction.RollbackCommand rollback)
    {
        _ = EndTransaction(context);
        rollback.Completion.TrySetResult(new Done());
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
        _ = _writer.Exec(RollbackItem);
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
