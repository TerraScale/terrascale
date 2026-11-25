using System.Globalization;

namespace TerraBase.Storage;

internal sealed record SnapshotCapture(CausalBookmark Bookmark, ulong SchemaEpoch);

internal readonly union SnapshotOutcome(SnapshotCapture, StorageBusy);

internal sealed partial class DatabaseSession
{
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

        try
        {
            var begun = reader.Exec("BEGIN;");
            if (!begun.IsOk)
            {
                return begun.ToBusy();
            }

            var info = new StatementInfo();
            List<SqlResultSet> results = [];
            for (var index = 0; index < request.Statements.Count; index++)
            {
                var executed = ExecuteOnReader(reader, request.Statements[index], index, info, null, limitResults, cancellationToken);
                SqlError? failure = executed switch
                {
                    SqlResultSet result => Collect(results, result),
                    SqlError error => error,
                };
                if (failure is not null)
                {
                    _ = reader.Exec("ROLLBACK;");
                    return failure;
                }
            }

            _ = reader.Exec("COMMIT;");
            return new SqlReadResult(results, observed);
        }
        finally
        {
            ReturnReader(reader);
        }
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
            var status = reader.Exec("BEGIN;");
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

    private static SqlError? Collect(List<SqlResultSet> results, SqlResultSet result)
    {
        results.Add(result);
        return null;
    }

    private static HlcTimestamp TableHlc(WriteScope scope, string table) =>
        scope.First(MetaStore.SelectTableHlcSql, SqlValue.FromText(table)) is { } row && row[0].TryGetBlob(out var blob) && blob.Length == HlcTimestamp.EncodedSize
            ? HlcCodec.Read(blob.Span)
            : HlcTimestamp.Zero;

    private StorageCorrupted? Recover()
    {
        List<SqlValue[]> rows = [];
        var status = _writer.Query(MetaStore.SelectVersionsSql, rows);
        if (!status.IsOk)
        {
            return new StorageCorrupted(DatabaseId, status.Message);
        }

        var (watermarks, epoch) = ReadVersions(rows);
        Interlocked.Exchange(ref _schemaEpoch, (long)epoch);
        _versions.Set(watermarks);
        HashSet<string> origins = new(OriginLog.DiscoverOrigins(_context.Directory, DatabaseId), StringComparer.Ordinal) { Region };
        origins.UnionWith(watermarks.Keys);
        foreach (var origin in origins)
        {
            if (!StorageEngineOptions.IsValidRegion(origin))
            {
                continue;
            }

            var opened = OriginLog.Open(_context.Directory, DatabaseId, origin, watermarks.GetValueOrDefault(origin), _cipher, _logOptions, _context.Time);
            var failure = opened switch
            {
                OriginLog log => Track(origin, log),
                StorageCorrupted corrupted => corrupted,
            };
            if (failure is not null)
            {
                return failure;
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
            if (failure is not null)
            {
                return failure;
            }
        }

        var pending = ApplyPending([], persist: false);
        var pendingFailure = pending switch
        {
            RemoteApplyOutcome => null,
            SchemaMismatch mismatch => new StorageCorrupted(DatabaseId, string.Concat("schema epoch mismatch during recovery: expected ", mismatch.ExpectedEpoch.ToString(CultureInfo.InvariantCulture))),
            StorageCorrupted corrupted => corrupted,
            StorageBusy busy => new StorageCorrupted(DatabaseId, busy.Message),
        };
        if (pendingFailure is not null)
        {
            return pendingFailure;
        }

        RecoverClockWatermark();
        return null;
    }

    private void RecoverClockWatermark()
    {
        var maxHlc = HlcTimestamp.Zero;
        List<SqlValue[]> metaRows = [];
        if (_writer.Query(MetaStore.SelectAllMetaSql, metaRows).IsOk)
        {
            foreach (var row in metaRows)
            {
                if (!row[0].TryGetBlob(out var pk) || !row[1].TryGetBlob(out var blob))
                {
                    continue;
                }

                if (pk.Length == 0 && blob.Length == HlcTimestamp.EncodedSize)
                {
                    maxHlc = HlcCodec.Max(maxHlc, HlcCodec.Read(blob.Span));
                }
                else if (RowMeta.Decode(blob.Span) is { } meta)
                {
                    maxHlc = HlcCodec.Max(maxHlc, meta.RowHlc);
                    foreach (var column in meta.Columns.Values)
                    {
                        maxHlc = HlcCodec.Max(maxHlc, column.Hlc);
                    }
                }
            }
        }

        foreach (var log in Logs())
        {
            var last = log.LastSequence;
            if (last <= 0)
            {
                continue;
            }

            if (log.Read(last, 1) is IReadOnlyList<CommitMetadata> { Count: > 0 } entries)
            {
                maxHlc = HlcCodec.Max(maxHlc, entries[^1].Hlc);
            }
        }

        if (maxHlc > HlcTimestamp.Zero)
        {
            _context.Clock.Restore(maxHlc);
        }
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

    private ApplyRemoteBatchOutcome ApplyRemote(IReadOnlyList<CommitMetadata> commits, bool allowLocalOrigin)
    {
        var duplicates = 0;
        var queued = 0;
        foreach (var commit in commits)
        {
            if (!StorageEngineOptions.IsValidRegion(commit.OriginRegion) || commit.Sequence <= 0)
            {
                return new StorageCorrupted(DatabaseId, "remote commit carries an invalid origin or sequence");
            }

            if (commit.Sequence <= _versions.Get(commit.OriginRegion))
            {
                duplicates++;
                continue;
            }

            if (!allowLocalOrigin && string.Equals(commit.OriginRegion, Region, StringComparison.Ordinal))
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
                continue;
            }

            queued++;
        }

        if (_pending.Values.Sum(static queue => queue.Count) > Options.MaxPendingRemoteCommits)
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

    private ApplyRemoteBatchOutcome ApplyPending(IReadOnlyList<CommitMetadata> requested, bool persist)
    {
        var tentative = _versions.Copy();
        var epoch = SchemaEpoch;
        List<(CommitMetadata Commit, DeltaBatch Batch)> order = [];
        var progressed = true;
        while (progressed)
        {
            progressed = false;
            foreach (var (origin, queue) in _pending)
            {
                var next = tentative.GetValueOrDefault(origin) + 1;
                if (!queue.TryGetValue(next, out var commit) || !Satisfied(commit, tentative))
                {
                    continue;
                }

                if (!MutationBatchCodec.TryDecode(commit.DeltaPayload.Span, out var batch))
                {
                    _ = queue.Remove(next);
                    return new StorageCorrupted(DatabaseId, string.Concat("undecodable delta payload from ", origin));
                }

                if (batch.Ddl.Count > 0 && commit.SchemaEpoch != epoch + 1)
                {
                    if (commit.SchemaEpoch <= epoch)
                    {
                        _ = queue.Remove(next);
                        return new SchemaMismatch(DatabaseId, epoch + 1, commit.SchemaEpoch);
                    }

                    continue;
                }

                if (batch.Ddl.Count == 0 && commit.SchemaEpoch > epoch)
                {
                    continue;
                }

                epoch = batch.Ddl.Count > 0 ? commit.SchemaEpoch : epoch;
                order.Add((commit, batch));
                tentative[origin] = next;
                progressed = true;
            }
        }

        if (order.Count > 0)
        {
            var status = ApplyInTransaction(order, persist, out var skipped);
            if (!status.IsOk)
            {
                foreach (var (commit, _) in order)
                {
                    _ = _pending[commit.OriginRegion].Remove(commit.Sequence);
                }

                return status.IsBusy ? status.ToBusy() : new StorageCorrupted(DatabaseId, status.Message);
            }

            foreach (var (commit, _) in order)
            {
                _ = _pending[commit.OriginRegion].Remove(commit.Sequence);
            }

            Interlocked.Exchange(ref _schemaEpoch, (long)epoch);
            _versions.Set(tentative);
            var buffered = requested.Count(commit => _pending.TryGetValue(commit.OriginRegion, out var queue) && queue.ContainsKey(commit.Sequence));
            return new RemoteApplyOutcome(order.Count - skipped, skipped, buffered, _versions.Snapshot());
        }

        var waiting = requested.Count(commit => _pending.TryGetValue(commit.OriginRegion, out var queue) && queue.ContainsKey(commit.Sequence));
        return new RemoteApplyOutcome(0, 0, waiting, _versions.Snapshot());
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

    private SqliteStatus ApplyInTransaction(List<(CommitMetadata Commit, DeltaBatch Batch)> order, bool persist, out int skipped, long? leaseDeadline = null)
    {
        skipped = 0;
        var status = BeginWrite(applying: true);
        if (!status.IsOk)
        {
            return status;
        }

        _capture.Suppressed = true;
        var scope = new WriteScope(_writer);
        scope.Fail(_writer.Exec("PRAGMA defer_foreign_keys = ON;"));
        var nowMs = _context.Time.GetUtcNow().ToUnixTimeMilliseconds();
        var schemaChanged = false;
        foreach (var (commit, batch) in order)
        {
            var receipt = _context.Clock.Observe(commit.Hlc);
            var local = string.Equals(commit.OriginRegion, Region, StringComparison.Ordinal);
            if (!local && MetaStore.IsApplied(scope, commit.TxId))
            {
                skipped++;
            }
            else
            {
                foreach (var ddl in batch.Ddl)
                {
                    scope.Fail(_writer.Exec(ddl));
                    schemaChanged = true;
                }

                if (batch.Ddl.Count > 0)
                {
                    scope.Fail(_writer.ValidateSchemaCache());
                }

                Dictionary<string, HlcTimestamp> tables = new(StringComparer.OrdinalIgnoreCase);
                foreach (var mutation in batch.Mutations)
                {
                    CrdtApplier.Apply(scope, mutation, commit.Hlc, tables);
                }

                foreach (var table in tables.Keys.ToArray())
                {
                    tables[table] = receipt;
                }

                MetaStore.TouchTables(scope, tables);
                if (!local)
                {
                    MetaStore.MarkApplied(scope, commit.TxId, nowMs);
                }
            }

            MetaStore.SaveVersion(scope, commit.OriginRegion, commit.Sequence, commit.SchemaEpoch);
            if (scope.Failed)
            {
                break;
            }
        }

        List<(OriginLog Log, long Length, long Last)> appended = [];
        if (!scope.Failed && persist)
        {
            foreach (var group in order.Select(static item => item.Commit).GroupBy(static commit => commit.OriginRegion, StringComparer.Ordinal))
            {
                if (EnsureLog(group.Key, _versions.Get(group.Key)) is not { } log)
                {
                    scope.Fail(SqliteStatus.Fail(string.Concat("commit log unavailable for origin ", group.Key)));
                    break;
                }

                var last = log.LastSequence;
                CommitMetadata[] fresh = [.. group.Where(commit => commit.Sequence > last)];
                scope.Fail(log.Append(fresh, out var length));
                appended.Add((log, length, last));
            }
        }

        if (!scope.Failed)
        {
            scope.Fail(LeaseExpired(leaseDeadline)
                ? new SqliteStatus(SqliteNative.Interrupt, "the staged commit's participant lease expired")
                : _writer.Exec("COMMIT;"));
        }

        _capture.Suppressed = false;
        if (scope.Failed)
        {
            foreach (var (log, length, last) in appended)
            {
                log.Truncate(length, last);
            }

            if (_writer.InTransaction)
            {
                _ = _writer.Exec("ROLLBACK;");
            }

            _writer.InvalidateSchemaCache();
            return scope.Failure;
        }

        if (schemaChanged)
        {
            _writer.InvalidateSchemaCache();
        }

        foreach (var (log, _, _) in appended)
        {
            log.SealIfDue();
        }

        return SqliteStatus.Success;
    }

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

        var scope = new WriteScope(_writer);
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
        var status = ApplyInTransaction([(commit, batch)], persist: true, out _, leaseDeadline);
        if (!status.IsOk)
        {
            return LeaseExpired(leaseDeadline)
                ? new StorageTimeout("the staged commit's participant lease expired before commit")
                : status.ToBusy();
        }

        Interlocked.Exchange(ref _schemaEpoch, (long)epoch);
        _versions.Set([new(Region, sequence)]);
        return commit;
    }
}
