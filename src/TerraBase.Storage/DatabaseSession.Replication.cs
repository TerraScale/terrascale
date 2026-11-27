using System.Globalization;

namespace TerraBase.Storage;

internal sealed record SnapshotCapture(CausalBookmark Bookmark, ulong SchemaEpoch);

internal readonly union SnapshotOutcome(SnapshotCapture, StorageBusy);

internal readonly record struct PendingApply(CommitMetadata Commit, DeltaBatch Batch, bool Quarantined, ulong SavedEpoch);

internal sealed partial class DatabaseSession
{
    private static readonly DeltaBatch EmptyBatch = new([], []);
    private static readonly byte[] MaxHlcCeiling = CreateMaxHlcCeiling();
    private static readonly TimeSpan AppliedPruneCooldown = TimeSpan.FromMinutes(1);

    [ThreadStatic]
    private static StatementInfo? _readerStatementInfo;

    private DateTimeOffset _lastEmptyAppliedPrune = DateTimeOffset.MinValue;

    private static byte[] CreateMaxHlcCeiling()
    {
        var ceiling = new byte[HlcTimestamp.EncodedSize];
        HlcCodec.Write(ceiling, new HlcTimestamp(HlcTimestamp.MaxWallMs, ushort.MaxValue, ushort.MaxValue));
        return ceiling;
    }

    public ValueTask<SqlReadOutcome> ExecuteReadAsync(SqlStatementRequest request, CancellationToken cancellationToken) =>
        ExecuteReadAsync(request, limitResults: true, cancellationToken);

    internal async ValueTask<SqlReadOutcome> ExecuteReadAsync(
        SqlStatementRequest request, bool limitResults, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var waited = await WaitForBookmarkAsync(request.After, Options.BookmarkWaitTimeout, cancellationToken);
        if (waited switch { BookmarkReached => false, StorageTimeout => true })
        {
            return new StorageTimeout("causal bookmark was not reached before the read");
        }

        var observed = _versions.Snapshot();
        if (await RentReaderAsync(cancellationToken) is not { } reader)
        {
            return Closing();
        }

        var info = _readerStatementInfo ??= new StatementInfo();
        try
        {
            var singleStatement = request.Statements.Count == 1
                && request.Statements[0].Scope is null
                && !HasMultipleStatements(request.Statements[0].Sql);
            if (singleStatement)
            {
                if (reader.InTransaction)
                {
                    var cleared = reader.RollbackOpenTransaction();
                    if (!cleared.IsOk)
                    {
                        return cleared.ToBusy();
                    }
                }
            }
            else
            {
                var begun = reader.Begin("BEGIN;");
                if (!begun.IsOk)
                {
                    return begun.ToBusy();
                }
            }

            var results = request.Statements.Count == 0 ? [] : new SqlResultSet[request.Statements.Count];
            for (var index = 0; index < request.Statements.Count; index++)
            {
                var executed = ExecuteOnReader(reader, request.Statements[index], index, info, null, limitResults, cancellationToken);
                SqlError? failure = executed switch
                {
                    SqlResultSet result => Assign(results, index, result),
                    SqlError error => error,
                };
                if (failure is not null)
                {
                    if (!singleStatement)
                    {
                        _ = reader.Exec("ROLLBACK;");
                    }

                    return failure;
                }
            }

            if (!singleStatement)
            {
                _ = reader.Exec("COMMIT;");
            }

            return new SqlReadResult(results, observed);
        }
        finally
        {
            info.Reset();
            ReturnReader(reader);
        }
    }

    private static bool HasMultipleStatements(string sql)
    {
        var semicolon = sql.IndexOf(';');
        if (semicolon < 0)
        {
            return false;
        }

        for (var index = semicolon + 1; index < sql.Length; index++)
        {
            if (!char.IsWhiteSpace(sql[index]))
            {
                return true;
            }
        }

        return false;
    }

    public async ValueTask<ValidateReadSetOutcome> ValidateReadSetAsync(
        HlcTimestamp readSnapshot,
        IReadOnlyList<ReadKey> readSet,
        IReadOnlyList<string> scannedTables,
        ulong schemaEpoch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readSet);
        ArgumentNullException.ThrowIfNull(scannedTables);
        if (schemaEpoch != SchemaEpoch)
        {
            return new SchemaMismatch(DatabaseId, schemaEpoch, SchemaEpoch);
        }

        if (await RentReaderAsync(cancellationToken) is not { } reader)
        {
            return Closing();
        }

        try
        {
            var begun = reader.Begin("BEGIN;");
            if (!begun.IsOk)
            {
                return begun.ToBusy();
            }

            var scope = new WriteScope(reader);
            List<RowKey> staleKeys = [];
            foreach (var key in readSet)
            {
                if (!PrimaryKeyCodec.TryDecodeKeyString(key.Key.PrimaryKey, out var encoded))
                {
                    staleKeys.Add(key.Key);
                    continue;
                }

                if (MetaStore.Load(scope, key.Key.Table, encoded).RowHlc != key.Observed)
                {
                    staleKeys.Add(key.Key);
                }
            }

            List<string> staleTables = [.. scannedTables.Where(table => TableHlc(scope, table) > readSnapshot)];
            _ = reader.Exec("COMMIT;");
            if (scope.Failed)
            {
                return scope.Failure.ToBusy();
            }

            return staleKeys.Count == 0 && staleTables.Count == 0
                ? new ReadSetValid()
                : new ReadSetConflict(DatabaseId, staleKeys, staleTables);
        }
        finally
        {
            ReturnReader(reader);
        }
    }

    internal async ValueTask<SnapshotOutcome> BackupToAsync(SqliteConnection destination, CancellationToken cancellationToken)
    {
        if (await RentReaderAsync(cancellationToken) is not { } reader)
        {
            return Closing();
        }

        try
        {
            var status = reader.Begin("BEGIN;");
            List<SqlValue[]> versions = [];
            if (status.IsOk)
            {
                status = reader.Query(MetaStore.SelectVersionsSql, versions);
            }

            if (status.IsOk)
            {
                status = reader.BackupTo(destination);
            }

            _ = reader.Exec("COMMIT;");
            if (!status.IsOk)
            {
                return status.ToBusy();
            }

            var (watermarks, epoch) = ReadVersions(versions);
            return new SnapshotCapture(CausalBookmark.Create(watermarks), epoch);
        }
        finally
        {
            ReturnReader(reader);
        }
    }

    internal static (Dictionary<string, long> Watermarks, ulong Epoch) ReadVersions(List<SqlValue[]> rows)
    {
        Dictionary<string, long> watermarks = new(StringComparer.Ordinal);
        ulong epoch = 0;
        foreach (var row in rows)
        {
            if (row[0].TryGetText(out var origin) && row[1].TryGetIntegral(out var sequence) && row[2].TryGetIntegral(out var schemaEpoch))
            {
                watermarks[origin] = sequence;
                epoch = Math.Max(epoch, (ulong)schemaEpoch);
            }
        }

        return (watermarks, epoch);
    }

    private static SqlError? Assign(SqlResultSet[] results, int index, SqlResultSet result)
    {
        results[index] = result;
        return null;
    }

    private static HlcTimestamp TableHlc(WriteScope scope, string table)
    {
        if (scope.MetaCache is { } cache && cache.TryGetTableHlc(table, out var cached))
        {
            return cached;
        }

        var hlc = scope.First(MetaStore.SelectTableHlcSql, SqlValue.FromText(table)) is { } row
            && row[0].TryGetBlob(out var blob)
            && blob.Length == HlcTimestamp.EncodedSize
            ? HlcCodec.Read(blob.Span)
            : HlcTimestamp.Zero;
        if (!scope.Failed)
        {
            scope.MetaCache?.SetTableHlc(table, hlc);
        }

        return hlc;
    }

    private RecoverOutcome Recover()
    {
        List<SqlValue[]> rows = [];
        var status = _writer.Query(MetaStore.SelectVersionsSql, rows);
        if (!status.IsOk)
        {
            return status.IsBusy ? status.ToBusy() : new StorageCorrupted(DatabaseId, status.Message);
        }

        var (watermarks, epoch) = ReadVersions(rows);
        Interlocked.Exchange(ref _schemaEpoch, (long)epoch);
        _versions.Set(watermarks);
        var discovered = OriginLog.DiscoverFiles(_context.Directory, DatabaseId);
        HashSet<string> origins = new(discovered.Keys, StringComparer.Ordinal);
        origins.UnionWith(watermarks.Keys);
        foreach (var origin in origins)
        {
            if (!StorageEngineOptions.IsValidRegion(origin))
            {
                continue;
            }

            var found = discovered.TryGetValue(origin, out var files);
            var opened = OriginLog.Open(
                _context.Directory,
                DatabaseId,
                origin,
                watermarks.GetValueOrDefault(origin),
                _cipher,
                _logOptions,
                _context.Time,
                found ? files.Sealed : [],
                activeKnownExisting: found && files.HasActive);
            var failure = opened switch
            {
                OriginLog log => Track(origin, log),
                StorageCorrupted corrupted => corrupted,
            };
            if (failure is { } corruptedLog)
            {
                return corruptedLog;
            }
        }

        foreach (var log in Logs())
        {
            var applied = watermarks.GetValueOrDefault(log.Origin);
            if (log.LastSequence < applied)
            {
                return new StorageCorrupted(DatabaseId, string.Concat("commit log for origin ", log.Origin, " is behind the database"));
            }

            if (log.LastSequence == applied)
            {
                continue;
            }

            var page = log.Read(applied + 1, int.MaxValue);
            var failure = page switch
            {
                IReadOnlyList<CommitMetadata> entries => Buffer(entries),
                CommitLogGap gap => new StorageCorrupted(DatabaseId, string.Concat("commit log gap for origin ", gap.OriginRegion)),
                StorageCorrupted corrupted => corrupted,
            };
            if (failure is { } corruptedPage)
            {
                return corruptedPage;
            }
        }

        var pending = ApplyPending([], persist: false);
        return pending switch
        {
            RemoteApplyOutcome recovered => RecoverClockWatermark(recovered),
            SchemaMismatch mismatch => new StorageCorrupted(DatabaseId, string.Concat("schema epoch mismatch during recovery: expected ", mismatch.ExpectedEpoch.ToString(CultureInfo.InvariantCulture))),
            StorageCorrupted corrupted => corrupted,
            StorageBusy busy => busy,
        };
    }

    private RecoverOutcome RecoverClockWatermark(RemoteApplyOutcome recovered)
    {
        _ = Interlocked.Add(ref _recoveredQuarantined, recovered.Quarantined);
        var status = _writer.QueryFirst(MetaStore.SelectClockWatermarkSql, out var row, SqlValue.FromBlob(MaxHlcCeiling));
        if (!status.IsOk)
        {
            return status.IsBusy ? status.ToBusy() : new StorageCorrupted(DatabaseId, status.Message);
        }

        if (row is not null && row[0].TryGetBlob(out var blob) && blob.Length == HlcTimestamp.EncodedSize)
        {
            _context.Clock.Restore(HlcCodec.Read(blob.Span));
        }

        foreach (var log in Logs())
        {
            var last = log.LastSequence;
            if (last <= 0 || log.Read(last, 1) is not IReadOnlyList<CommitMetadata> { Count: > 0 } entries || !entries[^1].Hlc.IsValid)
            {
                continue;
            }

            if (string.Equals(log.Origin, Region, StringComparison.Ordinal))
            {
                _context.Clock.Restore(entries[^1].Hlc);
            }
            else
            {
                _ = _context.Clock.Observe(entries[^1].Hlc);
            }
        }

        return new SessionRecovered();
    }

    private StorageCorrupted? Track(string origin, OriginLog log)
    {
        lock (_logsGate)
        {
            _logs[origin] = log;
        }

        return null;
    }

    private StorageCorrupted? Buffer(IReadOnlyList<CommitMetadata> entries)
    {
        foreach (var entry in entries)
        {
            if (!_pending.TryGetValue(entry.OriginRegion, out var queue))
            {
                queue = [];
                _pending[entry.OriginRegion] = queue;
            }

            queue[entry.Sequence] = entry;
        }

        return null;
    }

    private void ProcessRemoteGroup(List<RemoteApplyRequest> requests, bool allowGreedyDrain = false)
    {
        if (allowGreedyDrain)
        {
            var reader = _requests.Reader;
            var added = false;
            while (requests.Count < Options.MaxBatchSize
                && reader.TryPeek(out var peeked)
                && peeked is RemoteApplyRequest { IsReplay: false }
                && reader.TryRead(out var extra)
                && extra is RemoteApplyRequest remoteExtra)
            {
                requests.Add(remoteExtra);
                added = true;
            }

            if (added)
            {
                Volatile.Write(ref _inFlight, [.. requests]);
            }
        }

        if (requests.Count == 1)
        {
            var single = requests[0];
            _ = single.Completion.TrySetResult(single.CancellationToken.IsCancellationRequested ? Closing() : ApplyRemote(single.Commits, single.IsReplay));
            return;
        }

        List<(RemoteApplyRequest Request, int PreDuplicates, List<(string Origin, long Sequence)> Enqueued)> queued = new(requests.Count);
        for (var index = 0; index < requests.Count; index++)
        {
            var request = requests[index];
            if (request.CancellationToken.IsCancellationRequested)
            {
                _ = request.Completion.TrySetResult(Closing());
                continue;
            }

            var validationError = ValidateRemoteBatch(request.Commits, request.IsReplay);
            if (validationError is not null)
            {
                FlushRemoteGroup(queued);
                _ = request.Completion.TrySetResult(validationError);
                continue;
            }

            var pendingCount = PendingCommitCount();
            if (queued.Count > 0 && pendingCount + request.Commits.Count > Options.MaxPendingRemoteCommits)
            {
                FlushRemoteGroup(queued);
            }

            var preDuplicates = 0;
            var enqueued = new List<(string Origin, long Sequence)>(request.Commits.Count);
            for (var commitIndex = 0; commitIndex < request.Commits.Count; commitIndex++)
            {
                var commit = request.Commits[commitIndex];
                if (commit.Sequence <= _versions.Get(commit.OriginRegion))
                {
                    preDuplicates++;
                    continue;
                }

                if (!_pending.TryGetValue(commit.OriginRegion, out var queue))
                {
                    queue = [];
                    _pending[commit.OriginRegion] = queue;
                }

                if (!queue.TryAdd(commit.Sequence, commit))
                {
                    preDuplicates++;
                }
                else
                {
                    enqueued.Add((commit.OriginRegion, commit.Sequence));
                }
            }

            if (PendingCommitCount() > Options.MaxPendingRemoteCommits)
            {
                for (var removeIndex = 0; removeIndex < enqueued.Count; removeIndex++)
                {
                    var (origin, sequence) = enqueued[removeIndex];
                    _ = _pending[origin].Remove(sequence);
                }

                if (queued.Count > 0)
                {
                    FlushRemoteGroup(queued);
                }

                _pending.Clear();
                _ = request.Completion.TrySetResult(new StorageBusy("remote apply buffer overflowed; resend from the bookmark", TimeSpan.FromMilliseconds(100)));
                continue;
            }

            queued.Add((request, preDuplicates, enqueued));
        }

        FlushRemoteGroup(queued);
    }

    private void FlushRemoteGroup(List<(RemoteApplyRequest Request, int PreDuplicates, List<(string Origin, long Sequence)> Enqueued)> queued)
    {
        if (queued.Count == 0)
        {
            return;
        }

        HashSet<(string Origin, long Sequence)>? skippedKeys = null;
        HashSet<(string Origin, long Sequence)>? quarantinedKeys = null;
        var outcome = ApplyPending([], persist: true, trackOutcomeKeys: true, out skippedKeys, out quarantinedKeys);
        _ = outcome switch
        {
            RemoteApplyOutcome => CompleteRemoteGroupSuccess(queued, skippedKeys, quarantinedKeys),
            SchemaMismatch mismatch => FailRemoteGroup(queued, mismatch),
            StorageCorrupted corrupted => FailRemoteGroup(queued, corrupted),
            StorageBusy busy => FailRemoteGroup(queued, busy),
        };
        queued.Clear();
    }

    private bool CompleteRemoteGroupSuccess(
        List<(RemoteApplyRequest Request, int PreDuplicates, List<(string Origin, long Sequence)> Enqueued)> queued,
        HashSet<(string Origin, long Sequence)>? skippedKeys,
        HashSet<(string Origin, long Sequence)>? quarantinedKeys)
    {
        var snapshot = _versions.Snapshot();
        for (var index = 0; index < queued.Count; index++)
        {
            var (request, preDuplicates, enqueued) = queued[index];
            var applied = 0;
            var duplicates = preDuplicates;
            var buffered = 0;
            var quarantined = 0;
            for (var commitIndex = 0; commitIndex < enqueued.Count; commitIndex++)
            {
                var key = enqueued[commitIndex];
                if (_pending.TryGetValue(key.Origin, out var queue) && queue.ContainsKey(key.Sequence))
                {
                    buffered++;
                }
                else if (skippedKeys is not null && skippedKeys.Contains(key))
                {
                    duplicates++;
                }
                else if (quarantinedKeys is not null && quarantinedKeys.Contains(key))
                {
                    quarantined++;
                }
                else
                {
                    applied++;
                }
            }

            _ = request.Completion.TrySetResult(new RemoteApplyOutcome(applied, duplicates, buffered, snapshot, quarantined));
        }

        return true;
    }

    private static bool FailRemoteGroup(
        List<(RemoteApplyRequest Request, int PreDuplicates, List<(string Origin, long Sequence)> Enqueued)> queued,
        ApplyRemoteBatchOutcome failure)
    {
        for (var index = 0; index < queued.Count; index++)
        {
            _ = queued[index].Request.Completion.TrySetResult(failure);
        }

        return false;
    }

    private StorageCorrupted? ValidateRemoteBatch(IReadOnlyList<CommitMetadata> commits, bool isReplay)
    {
        for (var index = 0; index < commits.Count; index++)
        {
            var commit = commits[index];
            if (!StorageEngineOptions.IsValidRegion(commit.OriginRegion) || commit.Sequence <= 0)
            {
                return new StorageCorrupted(DatabaseId, "remote commit carries an invalid origin or sequence");
            }

            if (!isReplay
                && commit.Sequence > _versions.Get(commit.OriginRegion)
                && string.Equals(commit.OriginRegion, Region, StringComparison.Ordinal))
            {
                return new StorageCorrupted(DatabaseId, "remote batch carries a local-origin commit ahead of the local log");
            }
        }

        return null;
    }

    private int PendingCommitCount()
    {
        var count = 0;
        foreach (var queue in _pending.Values)
        {
            count += queue.Count;
        }

        return count;
    }

    private ApplyRemoteBatchOutcome ApplyRemote(IReadOnlyList<CommitMetadata> commits, bool isReplay)
    {
        var duplicates = 0;
        for (var index = 0; index < commits.Count; index++)
        {
            var commit = commits[index];
            if (!StorageEngineOptions.IsValidRegion(commit.OriginRegion) || commit.Sequence <= 0)
            {
                return new StorageCorrupted(DatabaseId, "remote commit carries an invalid origin or sequence");
            }

            if (commit.Sequence <= _versions.Get(commit.OriginRegion))
            {
                duplicates++;
                continue;
            }

            if (!isReplay && string.Equals(commit.OriginRegion, Region, StringComparison.Ordinal))
            {
                return new StorageCorrupted(DatabaseId, "remote batch carries a local-origin commit ahead of the local log");
            }

            if (!_pending.TryGetValue(commit.OriginRegion, out var queue))
            {
                queue = [];
                _pending[commit.OriginRegion] = queue;
            }

            if (!queue.TryAdd(commit.Sequence, commit))
            {
                duplicates++;
            }
        }

        if (!isReplay && PendingCommitCount() > Options.MaxPendingRemoteCommits)
        {
            _pending.Clear();
            return new StorageBusy("remote apply buffer overflowed; resend from the bookmark", TimeSpan.FromMilliseconds(100));
        }

        var outcome = ApplyPending(commits, persist: true);
        return outcome switch
        {
            RemoteApplyOutcome applied => applied with { Duplicates = applied.Duplicates + duplicates },
            SchemaMismatch mismatch => mismatch,
            StorageCorrupted corrupted => corrupted,
            StorageBusy busy => busy,
        };
    }

    private ApplyRemoteBatchOutcome ApplyPending(IReadOnlyList<CommitMetadata> requested, bool persist) =>
        ApplyPending(requested, persist, trackOutcomeKeys: false, out _, out _);

    private ApplyRemoteBatchOutcome ApplyPending(
        IReadOnlyList<CommitMetadata> requested,
        bool persist,
        bool trackOutcomeKeys,
        out HashSet<(string Origin, long Sequence)>? skippedKeys,
        out HashSet<(string Origin, long Sequence)>? quarantinedKeys)
    {
        skippedKeys = null;
        quarantinedKeys = null;
        var applied = 0;
        var duplicates = 0;
        var quarantined = 0;
        var isolate = false;
        HashSet<(string Origin, long Sequence)>? poisoned = null;
        var batchFull = true;
        while (batchFull)
        {
            var tentative = _versions.Copy();
            var epoch = SchemaEpoch;
            var horizon = _context.Time.GetUtcNow().ToUnixTimeMilliseconds() + HlcClock.MaxFutureSkewMs;
            List<PendingApply> order = [];
            var bytes = 0L;
            batchFull = false;
            var progressed = true;
            while (progressed && !batchFull)
            {
                progressed = false;
                foreach (var (origin, queue) in _pending)
                {
                    if (isolate && order.Count > 0)
                    {
                        batchFull = true;
                        break;
                    }

                    var next = tentative.GetValueOrDefault(origin) + 1;
                    if (!queue.TryGetValue(next, out var commit) || !Satisfied(commit, tentative))
                    {
                        continue;
                    }

                    if (persist && commit.Hlc.IsValid && commit.Hlc.WallMs > horizon && !string.Equals(origin, Region, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var decoded = MutationBatchCodec.TryDecode(commit.DeltaPayload.Span, out var batch);
                    var malformed = !commit.Hlc.IsValid || !decoded;
                    var regressed = false;
                    if (malformed)
                    {
                        batch = EmptyBatch;
                    }
                    else if (batch.Ddl.Count > 0 && commit.SchemaEpoch != epoch + 1)
                    {
                        if (commit.SchemaEpoch > epoch)
                        {
                            continue;
                        }

                        regressed = true;
                    }
                    else if (batch.Ddl.Count == 0 && commit.SchemaEpoch > epoch)
                    {
                        continue;
                    }

                    if (persist)
                    {
                        var size = CommitFrame.EncodedSize(commit);
                        if (size > CommitFrame.MaxEncodedSize)
                        {
                            _ = queue.Remove(next);
                            return new StorageCorrupted(DatabaseId, "remote commit exceeds the maximum commit log frame size");
                        }

                        if (order.Count > 0 && bytes + size > Options.SegmentMaxBytes)
                        {
                            batchFull = true;
                            break;
                        }

                        bytes += size;
                    }

                    var poison = malformed || regressed || (poisoned is not null && poisoned.Contains((origin, next)));
                    epoch = !malformed && !regressed && batch.Ddl.Count > 0 ? commit.SchemaEpoch : epoch;
                    order.Add(new PendingApply(commit, poison ? EmptyBatch : batch, poison, poison ? epoch : commit.SchemaEpoch));
                    tentative[origin] = next;
                    progressed = true;
                }
            }

            if (order.Count == 0)
            {
                break;
            }

            var status = ApplyInTransaction(order, persist, out var skipped, out var dataFault, trackOutcomeKeys ? skippedKeys ??= [] : null);
            if (dataFault && order is [{ Quarantined: false } single])
            {
                poisoned ??= [];
                _ = poisoned.Add((single.Commit.OriginRegion, single.Commit.Sequence));
                batchFull = true;
                continue;
            }

            if (dataFault && order.Count > 1)
            {
                isolate = true;
                batchFull = true;
                continue;
            }

            var isolated = 0;
            for (var index = 0; index < order.Count; index++)
            {
                var pending = order[index];
                _ = _pending[pending.Commit.OriginRegion].Remove(pending.Commit.Sequence);
                if (pending.Quarantined)
                {
                    isolated++;
                    if (trackOutcomeKeys)
                    {
                        quarantinedKeys ??= [];
                        _ = quarantinedKeys.Add((pending.Commit.OriginRegion, pending.Commit.Sequence));
                    }
                }
            }

            if (!status.IsOk)
            {
                return status.IsBusy ? status.ToBusy() : new StorageCorrupted(DatabaseId, status.Message);
            }

            isolate &= isolated == 0;
            Interlocked.Exchange(ref _schemaEpoch, (long)epoch);
            _versions.Set(tentative);
            applied += order.Count - skipped - isolated;
            duplicates += skipped;
            quarantined += isolated;
        }

        var buffered = 0;
        for (var index = 0; index < requested.Count; index++)
        {
            var commit = requested[index];
            if (_pending.TryGetValue(commit.OriginRegion, out var queue) && queue.ContainsKey(commit.Sequence))
            {
                buffered++;
            }
        }

        return new RemoteApplyOutcome(applied, duplicates, buffered, _versions.Snapshot(), quarantined);
    }

    private static bool Satisfied(CommitMetadata commit, Dictionary<string, long> tentative)
    {
        foreach (var (origin, sequence) in commit.Bookmark.Entries)
        {
            if (!string.Equals(origin, commit.OriginRegion, StringComparison.Ordinal) && tentative.GetValueOrDefault(origin) < sequence)
            {
                return false;
            }
        }

        return true;
    }

    private SqliteStatus ApplyInTransaction(
        List<PendingApply> order,
        bool persist,
        out int skipped,
        out bool dataFault,
        HashSet<(string Origin, long Sequence)>? skippedKeys = null,
        long? leaseDeadline = null)
    {
        skipped = 0;
        dataFault = false;
        var status = BeginWrite(applying: true);
        if (!status.IsOk)
        {
            return status;
        }

        _capture.Suppressed = true;
        var scope = WriterScope();
        scope.Reset();
        var batchTables = _laneGroupTables;
        batchTables.Clear();
        var versions = _laneRemoteVersions;
        versions.Clear();
        var now = _context.Time.GetUtcNow();
        var nowMs = now.ToUnixTimeMilliseconds();
        var schemaChanged = false;
        var hasRemoteOrigin = false;
        var appliedRows = 0;
        for (var index = 0; index < order.Count; index++)
        {
            var (commit, batch, quarantined, savedEpoch) = order[index];
            var receipt = quarantined ? HlcTimestamp.Zero : _context.Clock.Observe(commit.Hlc);
            var local = string.Equals(commit.OriginRegion, Region, StringComparison.Ordinal);
            hasRemoteOrigin |= !local;
            if (quarantined)
            {
                if (!local)
                {
                    MetaStore.MarkApplied(scope, commit.TxId, nowMs);
                }
            }
            else if (!local && !MetaStore.TryMarkApplied(scope, commit.TxId, nowMs))
            {
                if (!scope.Failed)
                {
                    skipped++;
                    _ = skippedKeys?.Add((commit.OriginRegion, commit.Sequence));
                }
            }
            else
            {
                for (var ddlIndex = 0; ddlIndex < batch.Ddl.Count; ddlIndex++)
                {
                    scope.Fail(_writer.Exec(batch.Ddl[ddlIndex]));
                    schemaChanged = true;
                }

                if (batch.Ddl.Count > 0 && _writer.ValidateSchemaCache() is { IsOk: false } stale)
                {
                    scope.Fail(stale.IsEnvironmental ? stale : new SqliteStatus(SqliteNative.Schema, stale.Message));
                }

                if (batch.Ddl.Count > 0)
                {
                    _metaCache.Clear();
                }

                for (var mutationIndex = 0; mutationIndex < batch.Mutations.Count; mutationIndex++)
                {
                    CrdtApplier.Apply(scope, batch.Mutations[mutationIndex], commit.Hlc, receipt, batchTables);
                }

                appliedRows += batch.Mutations.Count;
            }

            versions[commit.OriginRegion] = (commit.Sequence, savedEpoch);
            if (scope.Failed)
            {
                break;
            }
        }

        if (!scope.Failed && batchTables.Count > 0)
        {
            MetaStore.TouchTables(scope, batchTables);
        }

        if (!scope.Failed)
        {
            foreach (var (origin, version) in versions)
            {
                MetaStore.SaveVersion(scope, origin, version.Sequence, version.SchemaEpoch);
                if (scope.Failed)
                {
                    break;
                }
            }
        }

        var prunedZero = false;
        if (!scope.Failed
            && hasRemoteOrigin
            && (now - _lastEmptyAppliedPrune >= AppliedPruneCooldown || now < _lastEmptyAppliedPrune))
        {
            prunedZero = MetaStore.PruneApplied(scope, now) == 0 && !scope.Failed;
        }

        batchTables.Clear();
        dataFault = scope.Failed && !scope.Failure.IsEnvironmental;
        List<(OriginLog Log, long Length, long Last)> appended = [];
        if (!scope.Failed && persist)
        {
            var fresh = _laneCommits;
            foreach (var origin in versions.Keys)
            {
                if (EnsureLog(origin, _versions.Get(origin)) is not { } log)
                {
                    scope.Fail(new SqliteStatus(SqliteNative.CantOpen, string.Concat("commit log unavailable for origin ", origin)));
                    break;
                }

                var last = log.LastSequence;
                fresh.Clear();
                for (var index = 0; index < order.Count; index++)
                {
                    var pending = order[index];
                    if (pending.Commit.Sequence > last && string.Equals(pending.Commit.OriginRegion, origin, StringComparison.Ordinal))
                    {
                        fresh.Add(pending.Quarantined ? Preserved(pending.Commit) : pending.Commit);
                    }
                }

                if (fresh.Count == 0)
                {
                    continue;
                }

                var written = log.Append(fresh, out var length);
                scope.Fail(written);
                if (!written.IsOk)
                {
                    break;
                }

                appended.Add((log, length, last));
            }

            fresh.Clear();
        }

        versions.Clear();
        if (!scope.Failed)
        {
            scope.Fail(LeaseExpired(leaseDeadline)
                ? new SqliteStatus(SqliteNative.Interrupt, "the staged commit's participant lease expired")
                : _writer.Exec("COMMIT;"));
        }

        _capture.Suppressed = false;
        if (scope.Failed)
        {
            var truncated = true;
            foreach (var (log, length, last) in appended)
            {
                truncated &= log.Truncate(length, last);
            }

            var cleared = _writer.RollbackOpenTransaction();
            _metaCache.Clear();
            _writer.InvalidateSchemaCache();
            if (!truncated)
            {
                return UntruncatableLog(scope.Failure);
            }

            if (!cleared.IsOk)
            {
                return cleared;
            }

            var restored = _writer.SetApplyMode(false);
            return restored.IsOk
                ? scope.Failure
                : new SqliteStatus(SqliteNative.Corrupt, string.Concat("the writer could not leave apply mode after a failed apply: ", restored.Message));
        }

        if (prunedZero)
        {
            _lastEmptyAppliedPrune = now;
        }

        _rowsSinceCheckpoint += appliedRows;
        _ = _writer.SetApplyMode(false);
        if (schemaChanged)
        {
            _writer.InvalidateSchemaCache();
            _metaCache.Clear();
        }

        foreach (var (log, _, _) in appended)
        {
            log.MarkCommitted();
            log.SealIfDue();
        }

        return SqliteStatus.Success;
    }

    private static CommitMetadata Preserved(CommitMetadata commit) =>
        commit.Hlc.WallMs < 0 ? commit with { Hlc = commit.Hlc with { WallMs = HlcCodec.MaxWallMs } } : commit;

    private static SqliteStatus UntruncatableLog(SqliteStatus failure) =>
        new(SqliteNative.Corrupt, string.Concat("rolled-back commit log frames could not be truncated after: ", failure.Message));

    private bool LeaseExpired(long? leaseDeadline) => leaseDeadline is { } deadline && _context.Time.GetTimestamp() >= deadline;

    private CommitStagedOutcome CommitStaged(StagedDescriptor descriptor, HlcTimestamp commitHlc, long? leaseDeadline)
    {
        if (LeaseExpired(leaseDeadline))
        {
            return new StorageTimeout("the staged commit's participant lease expired in the writer queue");
        }

        var staged = descriptor.Staged;
        if (staged.SchemaEpoch != SchemaEpoch)
        {
            return new SchemaMismatch(DatabaseId, staged.SchemaEpoch, SchemaEpoch);
        }

        var cleared = _writer.RollbackOpenTransaction();
        if (!cleared.IsOk)
        {
            return cleared.ToBusy();
        }

        var scope = WriterScope();
        scope.Reset();
        List<RowKey> staleKeys = [];
        foreach (var observed in descriptor.Observed)
        {
            if (!PrimaryKeyCodec.TryDecodeKeyString(observed.Key.PrimaryKey, out var encoded))
            {
                staleKeys.Add(observed.Key);
                continue;
            }

            var meta = MetaStore.Load(scope, observed.Key.Table, encoded);
            if (meta.CausalLength != observed.CausalLength || meta.RowHlc != observed.RowHlc)
            {
                staleKeys.Add(observed.Key);
            }
        }

        List<string> staleTables = [.. staged.ScannedTables.Where(table => TableHlc(scope, table) > staged.ReadSnapshot)];
        if (scope.Failed)
        {
            return scope.Failure.ToBusy();
        }

        if (staleKeys.Count > 0 || staleTables.Count > 0)
        {
            return new ReadSetConflict(DatabaseId, staleKeys, staleTables);
        }

        if (staged.DeltaPayload.IsEmpty)
        {
            return NoOpCommit(ConsistencyMode.Strong);
        }

        if (!MutationBatchCodec.TryDecode(staged.DeltaPayload.Span, out var batch))
        {
            return new StorageBusy("staged payload could not be decoded", TimeSpan.Zero);
        }

        _ = _context.Clock.Observe(commitHlc);
        var sequence = _versions.Get(Region) + 1;
        var epoch = batch.Ddl.Count > 0 ? SchemaEpoch + 1 : SchemaEpoch;
        var commit = new CommitMetadata(staged.TxId, Region, sequence, commitHlc, epoch, ConsistencyMode.Strong, BookmarkWith(Region, sequence), staged.DeltaPayload);
        var status = ApplyInTransaction([new PendingApply(commit, batch, false, commit.SchemaEpoch)], persist: true, out _, out _, skippedKeys: null, leaseDeadline: leaseDeadline);
        if (!status.IsOk)
        {
            return LeaseExpired(leaseDeadline)
                ? new StorageTimeout("the staged commit's participant lease expired before commit")
                : status.ToBusy();
        }

        Interlocked.Exchange(ref _schemaEpoch, (long)epoch);
        _laneVersionUpdate[0] = new KeyValuePair<string, long>(Region, sequence);
        _versions.Set(_laneVersionUpdate);
        return commit;
    }
}
