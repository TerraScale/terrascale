using System.Diagnostics;

namespace TerraBase.Storage;

internal sealed record LocalItemPrepared(CommitMetadata? Commit, long EncodedBytes);

internal sealed record LocalWriteGroupFull;

internal readonly union LocalItemOutcome(LocalItemPrepared, LocalWriteGroupFull, SqlError, StrongRequired);

internal sealed partial class DatabaseSession
{
    private const string ItemSavepoint = "SAVEPOINT _tb_item;";
    private const string ReleaseItem = "RELEASE _tb_item;";
    private const string RollbackToItem = "ROLLBACK TO _tb_item;";
    private const int CheckpointRowThreshold = 2000;
    private static readonly TimeSpan CheckpointMinInterval = TimeSpan.FromSeconds(10);

    private readonly MetaCache _metaCache = new();
    private readonly WriteContext _laneWriteContext = new(ConsistencyMode.Fast, analyzeReads: false, CancellationToken.None);
    private readonly List<LaneRequest> _laneDrained = [];
    private readonly List<LaneRequest> _laneGroup = [];
    private readonly List<(LocalWriteRequest Request, List<SqlResultSet> Results, CommitMetadata? Commit)> _laneCompleted = [];
    private readonly List<CommitMetadata> _laneCommits = [];
    private readonly List<NetChange> _laneChanges = [];
    private readonly List<RemoteApplyRequest> _laneRemoteGroup = [];
    private readonly Dictionary<string, HlcTimestamp> _laneItemTables = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HlcTimestamp> _laneGroupTables = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long Sequence, ulong SchemaEpoch)> _laneRemoteVersions = new(StringComparer.Ordinal);
    private WriteScope? _laneScope;
    private int _rowsSinceCheckpoint;
    private long _lastCheckpointTimestamp;

    private WriteContext ResetLaneWriteContext(ConsistencyMode mode, bool analyzeReads, CancellationToken cancellationToken)
    {
        _laneWriteContext.Reset(mode, analyzeReads, cancellationToken);
        return _laneWriteContext;
    }

    private closed record class LaneRequest;

    private sealed record class LocalWriteRequest(SqlStatementRequest Request, TaskCompletionSource<SqlWriteOutcome> Completion, CancellationToken CancellationToken) : LaneRequest;

    private sealed record class StageRequest(SqlStatementRequest Request, TaskCompletionSource<StageWriteOutcome> Completion, CancellationToken CancellationToken) : LaneRequest;

    private sealed record class TransactionRequest(WriteTransaction Transaction) : LaneRequest;

    private sealed record class RemoteApplyRequest(IReadOnlyList<CommitMetadata> Commits, bool IsReplay, TaskCompletionSource<ApplyRemoteBatchOutcome> Completion, CancellationToken CancellationToken) : LaneRequest;

    private sealed record class CommitStagedRequest(StagedDescriptor Descriptor, HlcTimestamp CommitHlc, long? LeaseDeadline, TaskCompletionSource<CommitStagedOutcome> Completion) : LaneRequest;

    private sealed record class ShrinkWriterRequest(long ExpectedActivityTimestamp) : LaneRequest;

    private WriteScope WriterScope() => _laneScope ??= new WriteScope(_writer, _metaCache);

    public ValueTask<SqlWriteOutcome> ExecuteWriteAsync(SqlStatementRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        MarkActive();
        if (!IsClosed && !IsFaulted && (request.After.IsEmpty || _versions.Satisfies(request.After)))
        {
            var completion = NewCompletion<SqlWriteOutcome>();
            if (_requests.Writer.TryWrite(new LocalWriteRequest(request, completion, cancellationToken)))
            {
                return new ValueTask<SqlWriteOutcome>(completion.Task);
            }
        }

        return ExecuteWriteSlowAsync(request, cancellationToken);
    }

    private async ValueTask<SqlWriteOutcome> ExecuteWriteSlowAsync(SqlStatementRequest request, CancellationToken cancellationToken)
    {
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
        MarkActive();
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
        MarkActive();
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
        MarkActive();
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
        MarkActive();
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
        MarkActive();
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

            var begun = reader.Begin("BEGIN;");
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

        var transaction = new WriteTransaction(this, options, Ulid.NewUlid(_context.Time.GetUtcNow()));
        if (!await EnqueueAsync(new TransactionRequest(transaction), cancellationToken))
        {
            return Closing();
        }

        var started = transaction.Started.Task;
        if (!started.IsCompleted)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var winner = await Task.WhenAny(started, Task.Delay(Options.LaneWaitTimeout, _context.Time, timeout.Token));
            await timeout.CancelAsync();
            if (cancellationToken.IsCancellationRequested)
            {
                transaction.Abandon();
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (winner != started)
            {
                transaction.Abandon();
                return new StorageTimeout("writer lane did not start the transaction in time");
            }
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
        RegisterStaged(new StagedDescriptor(staged, [], _context.Time.GetUtcNow(), DeltaBatch.Empty));
        return staged;
    }

    private async Task RunGuardedLaneAsync()
    {
        var lane = RunLaneAsync();
        await lane.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (!lane.IsCompletedSuccessfully)
        {
            FaultLane();
        }
    }

    private void FaultLane()
    {
        Volatile.Write(ref _laneFaulted, 1);
        _requests.Writer.TryComplete();
        var faulted = LaneFaulted();
        for (var index = 0; index < _laneDrained.Count; index++)
        {
            _ = Fail(_laneDrained[index], faulted);
        }

        _laneDrained.Clear();
        Volatile.Read(ref _activeTransaction)?.Fault(faulted);
        while (_requests.Reader.TryRead(out var queued))
        {
            _ = Fail(queued, faulted);
        }

        _capture.Suppressed = false;
        _capture.End();
        _metaCache.Clear();
        _ = _writer.RollbackOpenTransaction();
        _ = _writer.SetApplyMode(false);
    }

    private async Task RunLaneAsync()
    {
        var reader = _requests.Reader;
        var drained = _laneDrained;
        var group = _laneGroup;
        var remoteGroup = _laneRemoteGroup;
        while (await reader.WaitToReadAsync(CancellationToken.None))
        {
            drained.Clear();
            while (drained.Count < Options.MaxBatchSize && reader.TryRead(out var request))
            {
                drained.Add(request);
            }

            group.Clear();
            remoteGroup.Clear();
            var initialCount = drained.Count;
            for (var index = 0; index < initialCount; index++)
            {
                var request = drained[index];
                if (IsGroupable(request))
                {
                    if (remoteGroup.Count > 0)
                    {
                        ProcessRemoteGroup(remoteGroup);
                        remoteGroup.Clear();
                    }

                    group.Add(request);
                    continue;
                }

                if (request is RemoteApplyRequest { IsReplay: false } remoteRequest)
                {
                    if (group.Count > 0)
                    {
                        ProcessGroup(group, allowGreedyDrain: false);
                        group.Clear();
                    }

                    remoteGroup.Add(remoteRequest);
                    continue;
                }

                if (group.Count > 0)
                {
                    ProcessGroup(group, allowGreedyDrain: false);
                    group.Clear();
                }

                if (remoteGroup.Count > 0)
                {
                    ProcessRemoteGroup(remoteGroup);
                    remoteGroup.Clear();
                }

                await ProcessSerialAsync(request);
            }

            if (group.Count > 0)
            {
                ProcessGroup(group, allowGreedyDrain: true);
                group.Clear();
            }

            if (remoteGroup.Count > 0)
            {
                ProcessRemoteGroup(remoteGroup, allowGreedyDrain: true);
                remoteGroup.Clear();
            }

            drained.Clear();
            if (_rowsSinceCheckpoint >= CheckpointRowThreshold
                && reader.Count == 0
                && _context.Time.GetElapsedTime(_lastCheckpointTimestamp) >= CheckpointMinInterval)
            {
                MaybeCheckpointIdle();
            }
        }

        while (reader.TryRead(out var abandoned))
        {
            _ = Fail(abandoned, Closing());
        }

        _metaCache.Clear();
    }

    private void MaybeCheckpointIdle()
    {
        _rowsSinceCheckpoint = 0;
        _lastCheckpointTimestamp = _context.Time.GetTimestamp();
        if (_writer.InTransaction)
        {
            return;
        }

        _ = _writer.Exec("PRAGMA wal_checkpoint(PASSIVE);");
    }

    private bool ShrinkWriterMemory(long expectedActivityTimestamp)
    {
        Volatile.Write(ref _shrinkWriterQueued, 0);
        if (_writer.InTransaction || Volatile.Read(ref _lastActivityTimestamp) != expectedActivityTimestamp)
        {
            return false;
        }

        Volatile.Write(ref _lastShrunkActivityTimestamp, expectedActivityTimestamp);
        return _writer.Exec("PRAGMA shrink_memory;").IsOk;
    }

    private static bool IsGroupable(LaneRequest request) => request switch
    {
        LocalWriteRequest => true,
        StageRequest or TransactionRequest or RemoteApplyRequest or CommitStagedRequest or ShrinkWriterRequest => false,
    };

    private async Task ProcessSerialAsync(LaneRequest request)
    {
        _ = request switch
        {
            TransactionRequest transaction => await RunTransactionAsync(transaction.Transaction),
            RemoteApplyRequest remote => remote.Completion.TrySetResult(remote.CancellationToken.IsCancellationRequested ? Closing() : ApplyRemote(remote.Commits, remote.IsReplay)),
            CommitStagedRequest staged => staged.Completion.TrySetResult(CommitStaged(staged.Descriptor, staged.CommitHlc, staged.LeaseDeadline)),
            StageRequest stage => RunSerialStage(stage),
            LocalWriteRequest => ProcessGroupWithSavepoints([request]),
            ShrinkWriterRequest shrink => ShrinkWriterMemory(shrink.ExpectedActivityTimestamp),
        };
    }

    private bool RunSerialStage(StageRequest stage)
    {
        if (stage.CancellationToken.IsCancellationRequested)
        {
            stage.Completion.TrySetCanceled(stage.CancellationToken);
            return true;
        }

        var begun = BeginWrite(applying: false);
        if (!begun.IsOk)
        {
            stage.Completion.TrySetResult(begun.ToBusy());
            return false;
        }

        var context = ResetLaneWriteContext(ConsistencyMode.Strong, analyzeReads: true, stage.CancellationToken);
        _capture.Begin();
        for (var index = 0; index < stage.Request.Statements.Count; index++)
        {
            SqlError? failure = ExecuteStatement(context, stage.Request.Statements[index], index) switch
            {
                SqlResultSet => null,
                SqlError error => error,
                StrongRequired strong => new SqlError("strong_required", ReasonText(strong.Reason), "XX000", index, 0),
            };
            if (failure is not null)
            {
                _capture.End();
                var reverted = RollbackItemSchema(context, useSavepoint: false, clearMetaCache: context.IsSchemaChange);
                stage.Completion.TrySetResult(reverted.IsOk ? failure : reverted.ToSqlError(-1));
                return false;
            }
        }

        var finished = FinishStage(context, out var descriptor);
        var rolledBack = RollbackItemSchema(context, useSavepoint: false, clearMetaCache: context.IsSchemaChange);
        if (!rolledBack.IsOk)
        {
            stage.Completion.TrySetResult(rolledBack.ToSqlError(-1));
            return false;
        }

        if (!finished.IsOk || descriptor is null)
        {
            stage.Completion.TrySetResult(finished.ToSqlError(-1));
            return false;
        }

        RegisterStaged(descriptor);
        stage.Completion.TrySetResult(descriptor.Staged);
        return true;
    }

    private bool ProcessGroup(List<LaneRequest> group, bool allowGreedyDrain)
    {
        if (group.Count == 0)
        {
            return true;
        }

        if (group.Count == 1 && group[0] is not LocalWriteRequest)
        {
            return ProcessGroupWithSavepoints(group);
        }

        return TryProcessLocalGroupOptimistic(group, allowGreedyDrain);
    }

    private bool TryProcessLocalGroupOptimistic(List<LaneRequest> group, bool allowGreedyDrain)
    {
        var begun = BeginWrite(applying: false);
        if (!begun.IsOk)
        {
            var busy = begun.ToBusy();
            for (var index = 0; index < group.Count; index++)
            {
                _ = Fail(group[index], busy);
            }

            return false;
        }

        var nextSequence = _versions.Get(Region) + 1;
        var lastCommittedSequence = 0L;
        var epoch = SchemaEpoch;
        var groupBytes = 0L;
        var groupRows = 0;
        var completed = _laneCompleted;
        completed.Clear();
        _laneGroupTables.Clear();
        var schemaTouched = false;
        var needsReplay = false;
        var reader = _requests.Reader;

        for (var index = 0; index < group.Count; index++)
        {
            if (group[index] is not LocalWriteRequest local)
            {
                needsReplay = true;
                break;
            }

            if (local.CancellationToken.IsCancellationRequested)
            {
                local.Completion.TrySetCanceled(local.CancellationToken);
                continue;
            }

            var context = ResetLaneWriteContext(local.Request.Mode, analyzeReads: false, local.CancellationToken);
            _capture.Begin();
            var executed = ExecuteStatements(context, local.Request.Statements);
            var (outcome, itemRows) = FinishOptimistic(executed, context, ref nextSequence, ref epoch, ref lastCommittedSequence, groupBytes);
            schemaTouched |= context.IsSchemaChange;

            var canCompleteSingle = completed.Count == 0 && group.Count == 1 && !_writeGroupBroken;
            var step = outcome switch
            {
                LocalItemPrepared prepared when _writer.InTransaction && !_writeGroupBroken => AcceptOptimistic(completed, local, context, prepared, itemRows, ref groupBytes, ref groupRows),
                LocalItemPrepared => 2,
                LocalWriteGroupFull => 2,
                SqlError error when canCompleteSingle => CompleteSingleOptimistic(context, local, error),
                SqlError => 2,
                StrongRequired strong when canCompleteSingle => CompleteSingleOptimistic(context, local, strong),
                StrongRequired => 2,
            };
            if (step == 1)
            {
                completed.Clear();
                _laneGroupTables.Clear();
                return true;
            }

            if (step == 2)
            {
                needsReplay = true;
                break;
            }

            if (allowGreedyDrain && index + 1 == group.Count && group.Count < Options.MaxBatchSize)
            {
                while (group.Count < Options.MaxBatchSize
                    && reader.TryPeek(out var peeked)
                    && IsGroupable(peeked)
                    && reader.TryRead(out var extra))
                {
                    group.Add(extra);
                    _laneDrained.Add(extra);
                }
            }
        }

        if (needsReplay)
        {
            if (_writer.InTransaction)
            {
                _ = _writer.Exec("ROLLBACK;");
            }

            if (schemaTouched)
            {
                BumpSchemaGeneration();
            }

            _writeGroupBroken = false;
            completed.Clear();
            _laneGroupTables.Clear();
            _metaCache.Clear();
            _writer.InvalidateSchemaCache();
            return ProcessGroupWithSavepoints(group);
        }

        var result = CompleteWriteGroup(completed, lastCommittedSequence, epoch, schemaTouched);
        if (result)
        {
            _rowsSinceCheckpoint += groupRows;
        }

        completed.Clear();
        _laneGroupTables.Clear();
        return result;

        static int AcceptOptimistic(
            List<(LocalWriteRequest Request, List<SqlResultSet> Results, CommitMetadata? Commit)> completedList,
            LocalWriteRequest request,
            WriteContext ctx,
            LocalItemPrepared prepared,
            int rows,
            ref long bytes,
            ref int totalRows)
        {
            completedList.Add((request, ctx.DetachResults(), prepared.Commit));
            bytes += prepared.EncodedBytes;
            totalRows += rows;
            return 0;
        }

        int CompleteSingleOptimistic(WriteContext ctx, LocalWriteRequest request, SqlWriteOutcome failure)
        {
            _ = RollbackItemSchema(ctx, useSavepoint: false);
            _ = request.Completion.TrySetResult(failure);
            return 1;
        }
    }

    private (LocalItemOutcome Outcome, int ItemRows) FinishOptimistic(
        StatementOutcome executed,
        WriteContext context,
        ref long nextSequence,
        ref ulong epoch,
        ref long lastCommittedSequence,
        long groupBytes)
    {
        var itemRows = 0;
        LocalItemOutcome outcome = executed switch
        {
            SqlResultSet => FinishLocalItemOptimistic(context, ref nextSequence, ref epoch, ref lastCommittedSequence, groupBytes, out itemRows),
            SqlError error => AbortOptimisticFailure(error),
            StrongRequired strong => AbortOptimisticFailure(strong),
        };
        return (outcome, itemRows);

        LocalItemOutcome AbortOptimisticFailure(LocalItemOutcome failure)
        {
            _capture.End();
            return failure;
        }
    }

    private LocalItemOutcome FinishLocalItemOptimistic(
        WriteContext context,
        ref long nextSequence,
        ref ulong epoch,
        ref long lastCommittedSequence,
        long groupBytes,
        out int itemRows)
    {
        itemRows = 0;
        var savedSequence = nextSequence;
        var savedEpoch = epoch;
        var finished = FinishLocalFast(context, ref nextSequence, ref epoch, deferMetadata: true, out var commit, out itemRows);
        if (!finished.IsOk)
        {
            _laneItemTables.Clear();
            nextSequence = savedSequence;
            epoch = savedEpoch;
            return finished.ToSqlError(-1);
        }

        var bytes = commit is null ? 0 : CommitFrame.EncodedSize(commit);
        if (bytes > CommitFrame.MaxEncodedSize || (groupBytes > 0 && bytes > 0 && groupBytes + bytes > Options.SegmentMaxBytes))
        {
            _laneItemTables.Clear();
            nextSequence = savedSequence;
            epoch = savedEpoch;
            return bytes > CommitFrame.MaxEncodedSize
                ? new SqliteStatus(SqliteNative.TooBig, "commit log frame exceeds the maximum frame size").ToSqlError(-1)
                : new LocalWriteGroupFull();
        }

        if (commit is not null)
        {
            foreach (var (table, hlc) in _laneItemTables)
            {
                MetaStore.Raise(_laneGroupTables, table, hlc);
            }

            lastCommittedSequence = commit.Sequence;
        }

        _laneItemTables.Clear();
        return new LocalItemPrepared(commit, bytes);
    }

    private bool ProcessGroupWithSavepoints(List<LaneRequest> group)
    {
        if (group.Count == 0)
        {
            return true;
        }

        var useSavepoint = group.Count > 1;
        var nextSequence = 0L;
        var lastCommittedSequence = 0L;
        var epoch = 0UL;
        var groupBytes = 0L;
        var groupRows = 0;
        var completed = _laneCompleted;
        completed.Clear();
        _laneGroupTables.Clear();
        var schemaTouched = false;
        var active = false;
        var succeeded = true;
        for (var index = 0; index < group.Count; index++)
        {
            var request = group[index];
            if (request is LocalWriteRequest { Completion.Task.IsCompleted: true })
            {
                continue;
            }

            if (!active)
            {
                var begun = BeginWrite(applying: false);
                if (!begun.IsOk)
                {
                    for (; index < group.Count; index++)
                    {
                        _ = Fail(group[index], begun.ToBusy());
                    }

                    completed.Clear();
                    _laneGroupTables.Clear();
                    return false;
                }

                nextSequence = _versions.Get(Region) + 1;
                lastCommittedSequence = 0;
                epoch = SchemaEpoch;
                active = true;
            }

            var (touched, groupFull) = request switch
            {
                LocalWriteRequest local => RunLocalItem(local, completed, useSavepoint, ref nextSequence, ref epoch, ref lastCommittedSequence, ref groupBytes, ref groupRows),
                StageRequest or TransactionRequest or RemoteApplyRequest or CommitStagedRequest or ShrinkWriterRequest => (RejectFromGroup(request), false),
            };
            schemaTouched |= touched;

            if (!_writer.InTransaction || _writeGroupBroken)
            {
                var rolledBack = new StorageBusy("SQLite rolled back the write group; retry the request", TimeSpan.Zero);
                for (var pendingIndex = 0; pendingIndex < completed.Count; pendingIndex++)
                {
                    _ = Fail(completed[pendingIndex].Request, rolledBack);
                }

                if (_writer.InTransaction)
                {
                    _ = _writer.Exec("ROLLBACK;");
                }

                if (schemaTouched)
                {
                    BumpSchemaGeneration();
                }

                _writeGroupBroken = false;
                completed.Clear();
                _laneGroupTables.Clear();
                _metaCache.Clear();
                _writer.InvalidateSchemaCache();
                schemaTouched = false;
                active = false;
                lastCommittedSequence = 0;
                groupBytes = 0;
                groupRows = 0;
                succeeded = false;
            }

            if (groupFull)
            {
                if (active)
                {
                    var groupOk = CompleteWriteGroup(completed, lastCommittedSequence, epoch, schemaTouched);
                    if (groupOk)
                    {
                        _rowsSinceCheckpoint += groupRows;
                    }

                    succeeded &= groupOk;
                }

                completed.Clear();
                _laneGroupTables.Clear();
                schemaTouched = false;
                active = false;
                lastCommittedSequence = 0;
                groupBytes = 0;
                groupRows = 0;
                index--;
            }
        }

        var finalOk = active && CompleteWriteGroup(completed, lastCommittedSequence, epoch, schemaTouched);
        if (finalOk)
        {
            _rowsSinceCheckpoint += groupRows;
        }

        var result = finalOk && succeeded;
        completed.Clear();
        _laneGroupTables.Clear();
        return result;
    }

    private bool CompleteWriteGroup(
        List<(LocalWriteRequest Request, List<SqlResultSet> Results, CommitMetadata? Commit)> completed,
        long lastCommittedSequence,
        ulong epoch,
        bool schemaTouched)
    {
        if (lastCommittedSequence > 0 || _laneGroupTables.Count > 0)
        {
            var scope = WriterScope();
            scope.Reset();
            if (_laneGroupTables.Count > 0)
            {
                MetaStore.TouchTables(scope, _laneGroupTables);
            }

            if (lastCommittedSequence > 0)
            {
                MetaStore.SaveVersion(scope, Region, lastCommittedSequence, epoch);
            }

            if (scope.Failed)
            {
                if (_writer.InTransaction)
                {
                    _ = _writer.Exec("ROLLBACK;");
                }

                if (schemaTouched)
                {
                    BumpSchemaGeneration();
                }

                _metaCache.Clear();
                _writer.InvalidateSchemaCache();
                var error = scope.Failure.ToSqlError(-1);
                for (var index = 0; index < completed.Count; index++)
                {
                    completed[index].Request.Completion.TrySetResult(error);
                }

                return false;
            }
        }

        if (schemaTouched)
        {
            _writer.InvalidateSchemaCache();
            _metaCache.Clear();
        }

        var commits = _laneCommits;
        commits.Clear();
        for (var index = 0; index < completed.Count; index++)
        {
            if (completed[index].Commit is { } commit)
            {
                commits.Add(commit);
            }
        }

        var outcome = CommitWrite(commits, epoch, _laneGroupTables, schemaTouched);
        commits.Clear();
        for (var index = 0; index < completed.Count; index++)
        {
            var (request, results, commit) = completed[index];
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

    private (bool SchemaTouched, bool GroupFull) RunLocalItem(
        LocalWriteRequest local,
        List<(LocalWriteRequest Request, List<SqlResultSet> Results, CommitMetadata? Commit)> completed,
        bool useSavepoint,
        ref long nextSequence,
        ref ulong epoch,
        ref long lastCommittedSequence,
        ref long groupBytes,
        ref int groupRows)
    {
        if (local.CancellationToken.IsCancellationRequested)
        {
            local.Completion.TrySetCanceled(local.CancellationToken);
            return (false, false);
        }

        var (outcome, context, itemRows) = RunLocal(local.Request, useSavepoint, ref nextSequence, ref epoch, ref lastCommittedSequence, groupBytes, local.CancellationToken);
        var full = outcome switch
        {
            LocalItemPrepared prepared => Prepared(completed, local, context, prepared, itemRows, ref groupBytes, ref groupRows),
            LocalWriteGroupFull => true,
            SqlError error => Complete(local, error),
            StrongRequired strong => Complete(local, strong),
        };
        return (context.IsSchemaChange, full);

        static bool Prepared(List<(LocalWriteRequest Request, List<SqlResultSet> Results, CommitMetadata? Commit)> completed,
            LocalWriteRequest local, WriteContext context, LocalItemPrepared prepared, int itemRows, ref long groupBytes, ref int groupRows)
        {
            completed.Add((local, context.DetachResults(), prepared.Commit));
            groupBytes += prepared.EncodedBytes;
            groupRows += itemRows;
            return false;
        }

        static bool Complete(LocalWriteRequest local, SqlWriteOutcome outcome)
        {
            local.Completion.TrySetResult(outcome);
            return false;
        }
    }

    private (LocalItemOutcome Outcome, WriteContext Context, int ItemRows) RunLocal(
        SqlStatementRequest request,
        bool useSavepoint,
        ref long nextSequence,
        ref ulong epoch,
        ref long lastCommittedSequence,
        long groupBytes,
        CancellationToken cancellationToken)
    {
        var context = ResetLaneWriteContext(request.Mode, analyzeReads: false, cancellationToken);
        if (useSavepoint)
        {
            var savepoint = _writer.Exec(ItemSavepoint);
            if (!savepoint.IsOk)
            {
                return (savepoint.ToSqlError(-1), context, 0);
            }
        }

        _capture.Begin();
        var executed = ExecuteStatements(context, request.Statements);
        var itemRows = 0;
        LocalItemOutcome outcome = executed switch
        {
            SqlResultSet => FinishLocalItem(context, useSavepoint, ref nextSequence, ref epoch, ref lastCommittedSequence, groupBytes, out itemRows),
            SqlError error => AbortItem(context, useSavepoint, error),
            StrongRequired strong => AbortItem(context, useSavepoint, strong),
        };
        return (outcome, context, itemRows);
    }

    private LocalItemOutcome FinishLocalItem(
        WriteContext context,
        bool useSavepoint,
        ref long nextSequence,
        ref ulong epoch,
        ref long lastCommittedSequence,
        long groupBytes,
        out int itemRows)
    {
        itemRows = 0;
        var savedSequence = nextSequence;
        var savedEpoch = epoch;
        var finished = FinishLocalFast(context, ref nextSequence, ref epoch, deferMetadata: true, out var commit, out itemRows);
        if (!finished.IsOk)
        {
            _laneItemTables.Clear();
            nextSequence = savedSequence;
            epoch = savedEpoch;
            var rolledBack = RollbackItemSchema(context, useSavepoint);
            return (rolledBack.IsOk ? finished : rolledBack).ToSqlError(-1);
        }

        var bytes = commit is null ? 0 : CommitFrame.EncodedSize(commit);
        if (bytes > CommitFrame.MaxEncodedSize || (groupBytes > 0 && bytes > 0 && groupBytes + bytes > Options.SegmentMaxBytes))
        {
            _laneItemTables.Clear();
            nextSequence = savedSequence;
            epoch = savedEpoch;
            var rolledBack = RollbackItemSchema(context, useSavepoint);
            if (!rolledBack.IsOk)
            {
                return rolledBack.ToSqlError(-1);
            }

            return bytes > CommitFrame.MaxEncodedSize
                ? new SqliteStatus(SqliteNative.TooBig, "commit log frame exceeds the maximum frame size").ToSqlError(-1)
                : new LocalWriteGroupFull();
        }

        if (useSavepoint)
        {
            var released = _writer.Exec(ReleaseItem);
            if (!released.IsOk)
            {
                _laneItemTables.Clear();
                nextSequence = savedSequence;
                epoch = savedEpoch;
                var rolledBack = RollbackItemSchema(context, useSavepoint);
                return (rolledBack.IsOk ? released : rolledBack).ToSqlError(-1);
            }
        }

        if (commit is not null)
        {
            foreach (var (table, hlc) in _laneItemTables)
            {
                MetaStore.Raise(_laneGroupTables, table, hlc);
            }

            lastCommittedSequence = commit.Sequence;
        }

        _laneItemTables.Clear();
        return new LocalItemPrepared(commit, bytes);
    }

    private SqliteStatus FinishLocalFast(
        WriteContext context,
        ref long nextSequence,
        ref ulong epoch,
        bool deferMetadata,
        out CommitMetadata? commit,
        out int itemRows)
    {
        commit = null;
        itemRows = 0;
        if (!context.DdlFault.IsOk)
        {
            _capture.End();
            return context.DdlFault;
        }

        var changes = _laneChanges;
        changes.Clear();
        var status = _capture.TakeNetChanges(changes);
        if (!status.IsOk)
        {
            changes.Clear();
            return status;
        }

        if (changes.Count == 0 && !context.IsSchemaChange)
        {
            return SqliteStatus.Success;
        }

        itemRows = changes.Count;
        var hlc = _context.Clock.Tick();
        var scope = WriterScope();
        scope.Reset();
        var tables = _laneItemTables;
        tables.Clear();
        var payload = LocalStamper.StampAndEncode(scope, changes, context.DdlView, ref hlc, tables);
        changes.Clear();
        _context.Clock.Restore(hlc);
        var sequence = nextSequence;
        var nextEpoch = context.IsSchemaChange ? epoch + 1 : epoch;
        if (!deferMetadata)
        {
            MetaStore.TouchTables(scope, tables);
            MetaStore.SaveVersion(scope, Region, sequence, nextEpoch);
        }

        if (scope.Failed)
        {
            return scope.Failure;
        }

        commit = new CommitMetadata(Ulid.NewUlid(_context.Time.GetUtcNow()), Region, sequence, hlc, nextEpoch, context.Mode, BookmarkWith(Region, sequence), payload);
        nextSequence = sequence + 1;
        epoch = nextEpoch;
        return SqliteStatus.Success;
    }

    private LocalItemOutcome AbortItem(WriteContext context, bool useSavepoint, LocalItemOutcome failure)
    {
        _capture.End();
        var rolledBack = RollbackItemSchema(context, useSavepoint);
        return rolledBack.IsOk ? failure : rolledBack.ToSqlError(-1);
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

    private async Task<bool> RunTransactionAsync(WriteTransaction transaction)
    {
        if (transaction.IsAbandoned || IsClosed)
        {
            transaction.DisposeIdleTimer();
            return transaction.Started.TrySetResult(Closing());
        }

        var begun = BeginWrite(applying: false);
        if (!begun.IsOk)
        {
            transaction.DisposeIdleTimer();
            return transaction.Started.TrySetResult(begun.ToBusy());
        }

        var context = ResetLaneWriteContext(transaction.Options.Mode, analyzeReads: transaction.Options.Mode == ConsistencyMode.Strong, CancellationToken.None);
        _capture.Begin();
        _ = Interlocked.Exchange(ref _activeTransaction, transaction);
        try
        {
            if (IsClosed)
            {
                _ = EndTransaction(context);
                transaction.Abandon();
                return transaction.Started.TrySetResult(Closing());
            }

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

            return true;
        }
        finally
        {
            transaction.DisposeIdleTimer();
            Volatile.Write(ref _activeTransaction, null);
        }
    }

    private bool ExecuteInTransaction(WriteContext context, WriteTransaction.ExecuteCommand execute)
    {
        context.CancellationToken = execute.CancellationToken;
        var executed = ExecuteStatement(context, execute.Statement, execute.Index);
        TransactionExecuteResult outcome = executed switch
        {
            SqlResultSet result => result,
            SqlError error => MarkReselectError(error),
            StrongRequired strong => MarkReselectStrong(context, strong, execute.Index),
        };
        if (_capture.Fault.Primary == SqliteNative.TooBig && _writer.InTransaction)
        {
            _ = _writer.Exec("ROLLBACK;");
            _metaCache.Clear();
        }

        execute.Completion.TrySetResult(outcome);
        return false;
    }

    private SqlError MarkReselectError(SqlError error)
    {
        _capture.MarkNeedsReselect();
        return error;
    }

    private SqlError MarkReselectStrong(WriteContext context, StrongRequired strong, int index)
    {
        _capture.MarkNeedsReselect();
        return TransactionStrong(context, strong, index);
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
        var finished = FinishLocalFast(context, ref nextSequence, ref epoch, deferMetadata: false, out var metadata, out var itemRows);
        if (!finished.IsOk)
        {
            _laneItemTables.Clear();
            _ = EndTransaction(context);
            commit.Completion.TrySetResult(finished.ToBusy());
            return true;
        }

        var commits = _laneCommits;
        commits.Clear();
        if (metadata is not null)
        {
            commits.Add(metadata);
        }

        var committed = CommitWrite(commits, epoch, _laneItemTables, context.IsSchemaChange);
        commits.Clear();
        _laneItemTables.Clear();
        if (committed.IsOk)
        {
            _rowsSinceCheckpoint += itemRows;
        }
        if (context.IsSchemaChange)
        {
            _writer.InvalidateSchemaCache();
            _metaCache.Clear();
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
            _metaCache.Clear();
        }

        if (context.IsSchemaChange)
        {
            BumpSchemaGeneration();
            _writer.InvalidateSchemaCache();
            _metaCache.Clear();
        }

        return true;
    }

    private SqliteStatus RollbackItemSchema(WriteContext context, bool useSavepoint, bool clearMetaCache = true)
    {
        SqliteStatus rolledBack;
        if (!_writer.InTransaction)
        {
            rolledBack = SqliteStatus.Success;
        }
        else if (useSavepoint)
        {
            rolledBack = _writer.Exec(RollbackToItem);
            if (rolledBack.IsOk)
            {
                rolledBack = _writer.Exec(ReleaseItem);
            }

            if (!rolledBack.IsOk && _writer.InTransaction)
            {
                _ = _writer.Exec("ROLLBACK;");
                _writeGroupBroken |= _writer.InTransaction;
            }
        }
        else
        {
            rolledBack = _writer.Exec("ROLLBACK;");
            _writeGroupBroken |= _writer.InTransaction;
        }

        if (clearMetaCache || !rolledBack.IsOk)
        {
            _metaCache.Clear();
        }

        if (context.IsSchemaChange)
        {
            BumpSchemaGeneration();
            _writer.InvalidateSchemaCache();
        }

        return rolledBack;
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
        _writeGroupBroken = false;
        var status = _writer.RollbackOpenTransaction();
        if (status.IsOk)
        {
            status = _writer.SetApplyMode(applying);
        }

        if (status.IsOk)
        {
            status = _writer.Begin("BEGIN IMMEDIATE;");
        }

        if (status.IsOk)
        {
            status = _writer.ValidateSchemaCache();
            if (!status.IsOk)
            {
                _ = _writer.Exec("ROLLBACK;");
                _metaCache.Clear();
            }
        }

        if (!status.IsOk && applying)
        {
            _ = _writer.SetApplyMode(false);
        }

        return status;
    }

    private SqliteStatus CommitWrite(
        List<CommitMetadata> commits,
        ulong epoch,
        Dictionary<string, HlcTimestamp> touchedTables,
        bool schemaTouched)
    {
        if (commits.Count == 0)
        {
            var empty = _writer.Exec("COMMIT;");
            if (!empty.IsOk && _writer.InTransaction)
            {
                _ = _writer.Exec("ROLLBACK;");
                _metaCache.Clear();
            }

            return empty;
        }

        if (EnsureLog(Region, _versions.Get(Region)) is not { } log)
        {
            _ = _writer.Exec("ROLLBACK;");
            _metaCache.Clear();
            return SqliteStatus.Fail("local commit log unavailable");
        }

        var previousLast = log.LastSequence;
        var appended = log.Append(commits, out var previousLength);
        if (!appended.IsOk)
        {
            _ = _writer.Exec("ROLLBACK;");
            _metaCache.Clear();
            return appended;
        }

        var committed = _writer.Exec("COMMIT;");
        if (!committed.IsOk)
        {
            var truncated = log.Truncate(previousLength, previousLast);
            if (_writer.InTransaction)
            {
                _ = _writer.Exec("ROLLBACK;");
            }

            _metaCache.Clear();
            _writer.InvalidateSchemaCache();
            return truncated ? committed : UntruncatableLog(committed);
        }

        if (schemaTouched)
        {
            BumpSchemaGeneration();
        }

        log.MarkCommitted();
        RecordFilteredChange(touchedTables, schemaTouched);
        Interlocked.Exchange(ref _schemaEpoch, (long)epoch);
        var lastCommit = commits[^1];
        _versions.AdvanceSingle(Region, lastCommit.Sequence, lastCommit.Bookmark);
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
        ShrinkWriterRequest => true,
    };

    private static TaskCompletionSource<T> NewCompletion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
