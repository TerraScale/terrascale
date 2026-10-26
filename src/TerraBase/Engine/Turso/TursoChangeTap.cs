using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Turso;

namespace TerraBase.Engine.Turso;

/// <summary>
/// The log continuous backup ships for one database file (TS-633, <c>docs/terrabase/backup.md</c>): the changes Turso
/// captured in <c>turso_cdc</c> on the file's writer connection (mode <c>after</c>), in commit order. Turso checkpoints
/// its write-ahead log on its own (it ignores <c>wal_autocheckpoint</c>), so its log file cannot be shipped as SQLite's
/// was; the captured changes are what every commit adds instead.
/// </summary>
/// <remarks>
/// <para>
/// The tap reads rows after the last one it read (its anchor) on its own <see cref="TursoConnection"/>, up to the last
/// commit row or the first commit past <see cref="ReadBudget"/>, and appends them as <see cref="ChangeLog"/> records. It
/// then deletes the rows before its anchor on the writer, always keeping the newest: an empty <c>turso_cdc</c> restarts
/// its ids at 1 after a reopen. An anchor that is gone means someone else deleted rows the tap never read: a hole, so the
/// backup starts a new generation.
/// </para>
/// <para>
/// A delete in mode <c>after</c> carries only the rowid. For <c>sqlite_schema</c> that is not enough, because Turso does
/// not capture every schema row (<c>ANALYZE</c> adds <c>sqlite_stat1</c> uncaptured), so rowids differ between the file
/// and a restore of it. The tap keeps the live schema's rowid → name map, as of its anchor, and logs a dropped object by
/// name. A schema row it cannot place (an update, an unknown rowid) is a hole too.
/// </para>
/// <para>
/// A snapshot holds the writer, so nothing commits, while it reads what is left and opens a read transaction. Then writers
/// go on, and <see cref="TursoCopy"/> copies the database as of that transaction: exactly as of the last record appended.
/// </para>
/// </remarks>
internal sealed class TursoChangeTap : IWalTap
{
    // Rows read per call, and bytes appended per call before the rest of the page is left for the next one.
    private const int PageRows = 1024;
    private const int PageBytes = 4 << 20;

    // Log bytes one ReadAsync appends before it stops at the next commit and leaves the rest to the next call: a segment
    // holds about this much, and a backlog (after a long snapshot upload) ships as several segments at upload speed.
    private const long ReadBudget = 32L << 20;

    private readonly TursoDatabaseStore _store;
    private readonly TursoIoPool _io;
    private readonly TimeProvider _time;
    // Replaced when a snapshot's read transaction may be left open (ReplaceReaderAsync).
    private TursoConnection _reader;
    private int _disposed;

    // The store's lineage this tap reads: a healthy rewrite replaces the file, so the tap ends and its owner opens another.
    private readonly int _lineage;

    // The change_id of the last row read, kept in turso_cdc; 0 before anything was captured.
    private long _anchor;

    // The file's sqlite_schema as of _anchor: rowid -> (type, name).
    private Dictionary<long, (string Type, string Name)> _schema = [];

    // The Turso times (change_time, Unix seconds) of the open transaction's first and latest rows, and whether every row
    // had one no earlier than the row before; null between transactions.
    private (long First, long Latest, bool Ordered)? _open;

    /// <summary>Runs after a snapshot is pinned and writers are released, before the file is copied; for integration tests.</summary>
    internal Func<CancellationToken, Task>? AfterSnapshotPinned { get; set; }

    /// <summary>Runs after a read, before its change-log cleanup on the writer; for integration tests.</summary>
    internal Func<CancellationToken, Task>? BeforeTrim { get; set; }

    /// <summary>Runs as a snapshot's read transaction ends, before its rollback; for integration tests.</summary>
    internal Func<CancellationToken, Task>? BeforeSnapshotEnd { get; set; }

    /// <summary>Runs after a snapshot's read transaction begins, before the read that pins it; for integration tests.</summary>
    internal Func<CancellationToken, Task>? AfterSnapshotBegin { get; set; }

    private TursoChangeTap(TursoDatabaseStore store, TursoIoPool io, TimeProvider time, TursoConnection reader, int lineage)
    {
        _store = store;
        _io = io;
        _time = time;
        _reader = reader;
        _lineage = lineage;
    }

    /// <summary>Opens a tap on <paramref name="store"/> that starts at the newest change; the caller holds an admission.</summary>
    public static async Task<TursoChangeTap> OpenAsync(TursoDatabaseStore store, TursoIoPool io, TimeProvider time, CancellationToken cancellationToken)
    {
        await using var operation = await store.BeginOperationAsync(cancellationToken).ConfigureAwait(false);
        var connectionString = await store.ConnectionStringAsync(io, cancellationToken).ConfigureAwait(false);
        TursoChangeTap? tap = null;
        try
        {
            // The reader is counted only under the writer: a healthy rewrite holds the writer while this store's handles drain,
            // so a reader counted while waiting for it would hold that drain until the rewrite gave up (TS-982). Under the
            // writer, too, the anchor and the schema map describe the same commit; this also opens the writer, whose capture
            // creates turso_cdc.
            await store.WithWriterAsync(writer =>
            {
                var lineage = store.Lineage;
                if (store.Quiescing)
                {
                    // The lineage already moved: a tap opened now would not end, and would hold the file the rewrite drains.
                    throw new InvalidOperationException("a healthy rewrite of this database waits for its handles; the tap opens after it");
                }

                store.OpenBackupReader();
                Interlocked.Increment(ref store.BackupTaps);
                var reader = new TursoConnection(connectionString) { CloseRefusal = () => store.CloseRefusal ?? "terminal backup reader close" };
                tap = new TursoChangeTap(store, io, time, reader, lineage);
                TursoSession.OpenExisting(reader, store.FilePath);
                return tap.Reset(writer);
            }, "the continuous backup's tap", cancellationToken).ConfigureAwait(false);
            operation?.Complete();
            return tap!;
        }
        catch (Exception error)
        {
            if (tap is not null)
            {
                await io.RunAsync(tap._reader.Dispose, CancellationToken.None).ConfigureAwait(false);
                store.CloseBackupReader();
                Interlocked.Decrement(ref store.BackupTaps);
            }

            await store.RecordOperationDamageAsync(error).ConfigureAwait(false);
            operation?.Complete();
            throw;
        }
    }

    /// <inheritdoc />
    public bool Ended => _lineage != _store.Lineage;

    public async ValueTask<WalRead> ReadAsync(Stream destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return await AdmittedAsync(async () =>
        {
            var (anchor, schema) = (_anchor, _schema);
            var read = await ReadCoreAsync(destination, ReadBudget, cancellationToken).ConfigureAwait(false);
            if (!read.Continuous)
            {
                await _store.WithWriterAsync(Reset, "the continuous backup's tap", cancellationToken).ConfigureAwait(false);
            }
            else if (read.Bytes > 0)
            {
                try
                {
                    if (BeforeTrim is { } trim)
                    {
                        await trim(cancellationToken).ConfigureAwait(false);
                    }

                    await _store.WithWriterAsync(Truncate, "the continuous backup's cleanup", cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // A call that throws has not moved the tap (IWalTap.ReadAsync): the next one reads the same changes
                    // again, or finds a hole if the delete ran.
                    (_anchor, _schema) = (anchor, schema);
                    throw;
                }

                // Only now may CDC retention for peer streams move past the old anchor.
                _store.BackupAnchor = _anchor;
            }

            return read;
        }).ConfigureAwait(false);
    }

    public async ValueTask<WalSnapshot> SnapshotAsync(Stream destination, string snapshotPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentException.ThrowIfNullOrEmpty(snapshotPath);
        return await AdmittedAsync(async () =>
        {
            // Inside the hold every call runs on the database's own write lane, idle while the writer is held, never on the
            // shared I/O pool: a pool starved by other databases' reads would keep this writer held (batch 8, 2026-10-25).
            var lane = _store.WriteLane;
            var snapshot = await _store.WithWritersHeldAsync(async () =>
            {
                var read = await ReadCoreAsync(destination, long.MaxValue, cancellationToken, lane).ConfigureAwait(false);
                await _store.OnWriterAsync(writer =>
                {
                    // Nothing commits meanwhile: the anchor is the newest row, and after a hole the next generation starts here.
                    Reset(writer);
                    return Truncate(writer);
                }, cancellationToken).ConfigureAwait(false);
                // The read transaction the copy is made in, pinned at the anchor.
                await lane.RunAsync(() => Execute("BEGIN;"), cancellationToken).ConfigureAwait(false);
                try
                {
                    if (AfterSnapshotBegin is { } begun)
                    {
                        await begun(cancellationToken).ConfigureAwait(false);
                    }

                    await lane.RunAsync(() => Execute("SELECT count(*) FROM sqlite_schema;"), cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // The transaction is open but not pinned: the next read would pin it, and every later read keep that state.
                    await ReplaceReaderAsync().ConfigureAwait(false);
                    throw;
                }

                return await lane.RunAsync(() =>
                {
                    using var command = _reader.CreateCommand();
                    command.CommandText = "SELECT incarnation, value FROM commit_offset WHERE id = 1;";
                    using var identity = command.ExecuteReader();
                    if (!identity.Read() || !Ulid.TryParse(identity.GetString(0), out var incarnation) || incarnation == Ulid.Empty)
                    {
                        throw new InvalidDataException("the pinned backup snapshot has no file identity");
                    }

                    return new WalSnapshot(read, _time.GetUtcNow(), incarnation, identity.GetInt64(1));
                }, cancellationToken).ConfigureAwait(false);
            }, cancellationToken, "the continuous backup's snapshot").ConfigureAwait(false);

            try
            {
                if (AfterSnapshotPinned is { } pinned)
                {
                    await pinned(cancellationToken).ConfigureAwait(false);
                }

                var targetConnection = await _store.Encryption.CreateAsync(snapshotPath, _io, cancellationToken).ConfigureAwait(false);
                await _io.RunAsync(() => TursoCopy.Write(_reader, targetConnection, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await EndSnapshotAsync().ConfigureAwait(false);
            }

            return snapshot;
        }).ConfigureAwait(false);
    }

    // Ends the snapshot's read transaction. Left open, it would serve every later read from the pinned state, and a read
    // mark would claim commits that read cannot see: when the rollback fails, the reader is replaced by a new connection.
    private async Task EndSnapshotAsync()
    {
        try
        {
            if (BeforeSnapshotEnd is { } ending)
            {
                await ending(CancellationToken.None).ConfigureAwait(false);
            }

            await _io.RunAsync(() => Execute("ROLLBACK;"), CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            await ReplaceReaderAsync().ConfigureAwait(false);
            throw;
        }
    }

    // A reader that may still be in a snapshot's read transaction is disposed and a new connection opened on the file; if
    // that open fails, later reads fail instead of reading a pinned state.
    private async Task ReplaceReaderAsync()
    {
        var stale = _reader;
        // End the old snapshot even when reading or unwrapping the replacement's key fails.
        await _io.RunAsync(stale.Dispose, CancellationToken.None).ConfigureAwait(false);
        _reader = new TursoConnection(await _store.ConnectionStringAsync(_io, CancellationToken.None).ConfigureAwait(false))
        {
            CloseRefusal = () => _store.CloseRefusal ?? "terminal backup reader close",
        };
        await _io.RunAsync(() => TursoSession.OpenExisting(_reader, _store.FilePath), CancellationToken.None).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _store.BackupAnchor = null;
        await _io.RunAsync(_reader.Dispose, CancellationToken.None).ConfigureAwait(false);
        _store.CloseBackupReader();
        Interlocked.Decrement(ref _store.BackupTaps);
    }

    private async Task<T> AdmittedAsync<T>(Func<Task<T>> call)
    {
        // Before the operation, which a rewrite holds: the owner disposes this tap and releases the old file.
        ObjectDisposedException.ThrowIf(_lineage != _store.Lineage, _store);
        _io.Admit();
        try
        {
            return await _io.WithAdmissionAsync(async () =>
            {
                await using var operation = await _store.BeginOperationAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    var result = await call().ConfigureAwait(false);
                    operation?.Complete();
                    return result;
                }
                catch (Exception error)
                {
                    await _store.RecordOperationDamageAsync(error).ConfigureAwait(false);
                    if (_store.CopyFault is null)
                    {
                        await ReplaceReaderAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        await _io.RunAsync(_reader.Dispose, CancellationToken.None).ConfigureAwait(false);
                    }
                    operation?.Complete();
                    throw;
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            _io.Release();
        }
    }

    // Appends the records committed after the anchor, up to the first commit past `budget` bytes, and moves the anchor to
    // the last of them. Its calls run on `runner`: the I/O pool, or the write lane of a caller that holds the writer.
    private async Task<WalRead> ReadCoreAsync(Stream destination, long budget, CancellationToken cancellationToken, TursoRunner? runner = null)
    {
        ObjectDisposedException.ThrowIf(_store.Closed, _store);
        runner ??= _io;
        var (end, started) = await runner.RunAsync(() =>
        {
            // Every commit that completed before this time is visible to the query below.
            var now = _time.GetUtcNow();
            if (_anchor > 0 && Scalar("SELECT 1 FROM turso_cdc WHERE change_id = $anchor;", _anchor) is null)
            {
                return (-1L, now);
            }

            return (Scalar("SELECT max(change_id) FROM turso_cdc WHERE change_id > $anchor AND change_type = 2;", _anchor) as long? ?? _anchor, now);
        }, cancellationToken).ConfigureAwait(false);
        if (end < 0)
        {
            return new(0, false, false, null);
        }

        var buffer = new ArrayBufferWriter<byte>();
        var schema = new Dictionary<long, (string Type, string Name)>(_schema);
        long bytes = 0;
        var after = _anchor;
        var spent = false;
        while (after < end && !spent)
        {
            var (next, placed, stop) = await runner.RunAsync(() => ReadPage(after, end, budget - bytes, schema, buffer), cancellationToken).ConfigureAwait(false);
            if (!placed)
            {
                return new(0, false, false, null);
            }

            await destination.WriteAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
            bytes += buffer.WrittenCount;
            buffer.ResetWrittenCount();
            (after, spent) = (next, stop);
        }

        DateTimeOffset? mark = null;
        if (after == end && bytes > 0)
        {
            // This read appended through the newest commit when it started: mark when that was.
            ChangeLog.WriteReadMark(buffer, started);
            await destination.WriteAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
            bytes += buffer.WrittenCount;
            mark = started;
        }

        // The caller publishes the anchor to CDC retention (BackupAnchor) once its cleanup is done.
        (_anchor, _schema) = (after, schema);
        return new(bytes, true, after < end, mark);
    }

    // Appends rows after `after`, up to `end`, and returns the last one read; Placed is false when a schema row cannot be
    // placed, and Spent true when the page stopped at the first commit that reached `allowance` bytes.
    private (long Next, bool Placed, bool Spent) ReadPage(
        long after, long end, long allowance, Dictionary<long, (string Type, string Name)> schema, ArrayBufferWriter<byte> buffer)
    {
        using var command = _reader.CreateCommand();
        command.CommandText = "SELECT change_id, change_type, table_name, id, after, change_time FROM turso_cdc WHERE change_id > $after AND change_id <= $end ORDER BY change_id LIMIT $limit;";
        command.Parameters.Add(new TursoParameter("$after", after));
        command.Parameters.Add(new TursoParameter("$end", end));
        command.Parameters.Add(new TursoParameter("$limit", (long)PageRows));
        // Aborted, not disposed, when the page stops early: finalize would step the SELECT through its unread rows.
        using var rows = command.ExecuteReader();
        var last = after;
        if (after == _anchor)
        {
            // A read starts between transactions.
            _open = null;
        }

        while (buffer.WrittenCount < PageBytes && rows.Read())
        {
            last = rows.GetInt64(0);
            var type = rows.GetInt64(1);
            // Turso stamps every row, the commit row too, with the wall clock in whole seconds (unixepoch()).
            long? time = rows.IsDBNull(5) ? null : rows.GetInt64(5);
            _open = _open is not { } open ? (time ?? 0, time ?? 0, time is not null)
                : (open.First, Math.Max(open.Latest, time ?? open.Latest), open.Ordered && time >= open.Latest);
            if (type == 2)
            {
                // A transaction whose times go back or are missing is logged without them: a restore cannot place a time in it.
                if (_open is (var first, var latest, true))
                {
                    ChangeLog.WriteCommit(buffer, first, latest);
                }
                else
                {
                    ChangeLog.WriteCommit(buffer);
                }

                _open = null;
                if (buffer.WrittenCount >= allowance)
                {
                    FaultAwareCopySource.Abort(rows);
                    return (last, true, true);
                }

                continue;
            }

            var table = rows.GetString(2);
            var rowid = rows.GetInt64(3);
            // IsDBNull materializes blobs in this driver; fetch the image only once.
            var value = rows.GetValue(4);
            var image = value is DBNull ? null : (byte[])value;
            if (table == "sqlite_schema")
            {
                if (!Place(type, rowid, image, schema, buffer))
                {
                    FaultAwareCopySource.Abort(rows);
                    return (last, false, false);
                }
            }
            else if (!ChangeLog.EngineTable(table))
            {
                if (type == -1)
                {
                    ChangeLog.WriteDelete(buffer, table, rowid);
                }
                else
                {
                    ChangeLog.WriteUpsert(buffer, table, rowid, image ?? throw new InvalidDataException("a captured row change has no after-image"));
                }
            }
        }

        if (buffer.WrittenCount >= PageBytes)
        {
            FaultAwareCopySource.Abort(rows);
        }

        return (last, true, false);
    }

    // Logs a schema change by name: a created object with its SQL, a dropped one by type and name.
    private static bool Place(long type, long rowid, byte[]? image, Dictionary<long, (string Type, string Name)> schema, ArrayBufferWriter<byte> buffer)
    {
        if (type == 1 && image is not null && SqliteRecord.Decode(image) is [string kind, string name, _, _, var sql, ..])
        {
            schema[rowid] = (kind, name);
            // Recreating the native index recreates its reserved backing objects too.
            if (sql is string statement && !ChangeLog.EngineTable(name))
            {
                ChangeLog.WriteCreate(buffer, statement);
            }

            return true;
        }

        if (type == -1 && schema.Remove(rowid, out var dropped))
        {
            if (!ChangeLog.EngineTable(dropped.Name))
            {
                ChangeLog.WriteDrop(buffer, dropped.Type, dropped.Name);
            }
            return true;
        }

        // An update (ALTER), or a row the map does not know.
        return false;
    }

    // On the writer, with the writer held: the anchor becomes the newest change and the schema map the file's.
    private bool Reset(TursoSession writer) => Reset(writer.Connection);

    private bool Reset(System.Data.Common.DbConnection connection, bool publish = true)
    {
        using (var newest = connection.CreateCommand())
        {
            newest.CommandText = "SELECT coalesce(max(change_id), 0) FROM turso_cdc;";
            _anchor = (long)newest.ExecuteScalar()!;
            if (publish)
            {
                _store.BackupAnchor = _anchor;
            }
        }

        var schema = new Dictionary<long, (string Type, string Name)>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT rowid, type, name FROM sqlite_schema;";
        using var rows = command.ExecuteReader();
        while (rows.Read())
        {
            schema[rows.GetInt64(0)] = (rows.GetString(1), rows.GetString(2));
        }

        _schema = schema;
        return true;
    }

    // Uses the backup encoder and restore replay, retaining the snapshot's schema map across rounds. This private cursor
    // is never disposed as a backup tap: its caller owns the reader and it neither registers nor publishes BackupAnchor.
    internal static TursoChangeTap RewriteFrom(TursoDatabaseStore store, TursoIoPool io, TursoConnection source)
    {
        var tap = new TursoChangeTap(store, io, TimeProvider.System, source, store.Lineage);
        tap.Reset(source, publish: false);
        store.RewriteAnchor = tap._anchor;
        return tap;
    }

    internal long RewriteBytes { get; private set; }

    internal long ReplayRewrite(TursoConnection source, TursoConnection target, bool final, CancellationToken token)
    {
        _reader = source;
        if (_anchor > 0 && Scalar("SELECT 1 FROM turso_cdc WHERE change_id = $anchor;", _anchor) is null)
        {
            throw new RewriteRefusedException("the rewrite's change-log anchor was trimmed");
        }

        // The final cut never scans past its bounded range, even just to find the commit marker.
        var end = _anchor;
        if (final)
        {
            using var measured = _store.MeasureRewrite("final_preflight");
            using var check = source.CreateCommand();
            check.CommandText = "SELECT change_id, change_type, length(after), table_name FROM turso_cdc WHERE change_id > $after ORDER BY change_id LIMIT 1025;";
            check.Parameters.Add(new TursoParameter("$after", _anchor));
            using var rows = check.ExecuteReader();
            long count = 0, bytes = 0, last = _anchor;
            while (rows.Read())
            {
                token.ThrowIfCancellationRequested();
                last = rows.GetInt64(0);
                bytes += rows.IsDBNull(2) ? 0 : rows.GetInt64(2);
                if (++count > 1024 || bytes > PageBytes || !rows.IsDBNull(3) && rows.GetString(3) == "sqlite_schema")
                {
                    FaultAwareCopySource.Abort(rows);
                    throw new RewriteRefusedException("the final rewrite delta exceeds its budget", reason: RewriteRefusal.Budget);
                }
                if (rows.GetInt64(1) == 2)
                {
                    end = last;
                }
            }
            if (last != end)
            {
                throw new RewriteRefusedException("the final rewrite delta ends inside a transaction");
            }
        }
        else
        {
            end = Scalar("SELECT max(change_id) FROM turso_cdc WHERE change_type = 2 AND change_id > $anchor;", _anchor) as long? ?? _anchor;
        }

        if (_anchor == 0 && Scalar("SELECT min(change_id) FROM turso_cdc WHERE change_id > $anchor;", 0) as long? is > 1)
        {
            throw new RewriteRefusedException("the rewrite's initial change-log prefix was trimmed");
        }

        var first = _anchor;
        RewriteBytes = 0;
        var replay = new ChangeLog.Replay(target);
        var buffer = new ArrayBufferWriter<byte>();
        while (_anchor < end)
        {
            token.ThrowIfCancellationRequested();
            var (next, placed, _) = ReadPage(_anchor, end, long.MaxValue, _schema, buffer);
            RewriteBytes += buffer.WrittenCount;
            using var segment = new MemoryStream(buffer.WrittenMemory.ToArray(), writable: false);
            if (!placed || next <= _anchor || !replay.Apply(segment, token))
            {
                throw new RewriteRefusedException("the rewrite delta cannot be replayed");
            }

            _anchor = next;
            buffer.ResetWrittenCount();
        }

        if (replay.Open)
        {
            throw new RewriteRefusedException("the rewrite delta ends inside a transaction");
        }

        _store.RewriteAnchor = _anchor;
        return _anchor - first;
    }

    // Deletes the rows before the anchor that the peer streams also let go of: the rows they have not acknowledged stay
    // (TursoStorageTransaction.Trim, a bounded batch a call). Turso does not capture changes to turso_cdc itself.
    private bool Truncate(TursoSession writer)
    {
        var now = _time.GetUtcNow().ToUnixTimeSeconds();
        // Nor the rows the shape frontier has not walked yet, for a bounded time (FR-25); the catalog has no shapes.
        var hold = _store.RequiresRemoteProof && !_store.IsSystem ? TursoStorageTransaction.ShapeFrontierHold(writer.Connection, null, now) : null;
        var rewrite = _store.RewriteAnchor;
        var below = rewrite < 0 ? _anchor : Math.Min(_anchor, rewrite);
        _ = TursoStorageTransaction.Trim(writer.Connection, null, Math.Min(below, hold ?? long.MaxValue), now,
            _store.Member is { } member ? member.GoneBefore(_time.GetUtcNow().ToUnixTimeMilliseconds()) : long.MinValue);
        return true;
    }

    private void Execute(params string[] statements)
    {
        foreach (var sql in statements)
        {
            using var command = _reader.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }

    private object? Scalar(string sql, long anchor)
    {
        using var command = _reader.CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add(new TursoParameter("$anchor", anchor));
        return command.ExecuteScalar();
    }
}

/// <summary>
/// The records of a change log segment, format 1. Each starts with its kind: <c>1</c> upsert (table, rowid, the row's
/// SQLite record), <c>2</c> delete (table, rowid), <c>3</c> commit, <c>4</c> create (the object's SQL), <c>5</c> drop (type,
/// name), <c>6</c> timed commit (the Turso times of the transaction's first row and of its commit row, Unix seconds),
/// <c>7</c> read mark (Unix milliseconds: every transaction committed before then comes before the mark). Strings and
/// records are length-prefixed (unsigned LEB128); rowids and times are 8 bytes, big-endian. A log written before timed
/// commits has only <c>3</c>: its transactions have no known time.
/// </summary>
internal static class ChangeLog
{
    private const byte Upsert = 1;
    private const byte Delete = 2;
    private const byte Commit = 3;
    private const byte Create = 4;
    private const byte Drop = 5;
    private const byte TimedCommit = 6;
    private const byte ReadMark = 7;

    // Bounds a single length field: no record, name or statement of a valid log is near this.
    private const int MaxLength = 1 << 30;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Tables the engine maintains itself: their changes are not logged and a restore never writes them.</summary>
    public static bool EngineTable(string table) =>
        table.StartsWith("sqlite_", StringComparison.Ordinal) || table.StartsWith("turso_", StringComparison.Ordinal)
        || table.StartsWith("__turso_", StringComparison.Ordinal)
        || table.StartsWith("fts_dir__tb_fts_index_", StringComparison.Ordinal);

    public static void WriteUpsert(IBufferWriter<byte> buffer, string table, long rowid, byte[] record)
    {
        buffer.Write([Upsert]);
        WriteString(buffer, table);
        WriteInt64(buffer, rowid);
        WriteLength(buffer, record.Length);
        buffer.Write(record);
    }

    public static void WriteDelete(IBufferWriter<byte> buffer, string table, long rowid)
    {
        buffer.Write([Delete]);
        WriteString(buffer, table);
        WriteInt64(buffer, rowid);
    }

    public static void WriteCommit(IBufferWriter<byte> buffer) => buffer.Write([Commit]);

    /// <summary>A commit whose rows Turso stamped from <paramref name="first"/> to <paramref name="last"/> (its commit row), never going back.</summary>
    public static void WriteCommit(IBufferWriter<byte> buffer, long first, long last)
    {
        buffer.Write([TimedCommit]);
        WriteInt64(buffer, first);
        WriteInt64(buffer, last);
    }

    /// <summary>A mark after the last commit of a read that reached the newest commit when it started at <paramref name="started"/>.</summary>
    public static void WriteReadMark(IBufferWriter<byte> buffer, DateTimeOffset started)
    {
        buffer.Write([ReadMark]);
        WriteInt64(buffer, started.ToUnixTimeMilliseconds());
    }

    public static void WriteCreate(IBufferWriter<byte> buffer, string sql)
    {
        buffer.Write([Create]);
        WriteString(buffer, sql);
    }

    public static void WriteDrop(IBufferWriter<byte> buffer, string type, string name)
    {
        buffer.Write([Drop]);
        WriteString(buffer, type);
        WriteString(buffer, name);
    }

    /// <summary>
    /// Replays <paramref name="segments"/> on <paramref name="file"/>, a snapshot, one transaction per commit record. Returns
    /// <see langword="false"/> for a malformed log, one that ends inside a transaction, or a change the file refuses.
    /// </summary>
    public static async Task<bool> ApplyAsync(string file, IAsyncEnumerable<Stream> segments, TursoRunner io, string connectionString, CancellationToken cancellationToken)
    {
        var connection = new TursoConnection(connectionString);
        try
        {
            await io.RunAsync(() => TursoSession.OpenExisting(connection, file), cancellationToken).ConfigureAwait(false);
            var replay = new Replay(connection);
            await foreach (var segment in segments.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                await using (segment.ConfigureAwait(false))
                {
                    if (!await io.RunAsync(() => replay.Apply(segment, cancellationToken), cancellationToken).ConfigureAwait(false))
                    {
                        return false;
                    }
                }
            }

            if (replay.Open)
            {
                return false;
            }

            // Turso leaves the log next to the file when the connection closes: copy it in, so the file alone is the result.
            await io.RunAsync(replay.Checkpoint, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            await io.RunAsync(connection.Dispose, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Places a restore at <paramref name="at"/> (to the millisecond) in <paramref name="segment"/>, whose transactions come
    /// after ones that ran before it, the last committed in second <paramref name="previous"/> when known. A transaction is
    /// before the time when its commit row's second ends by it, and after it when that second starts at or after it; a read
    /// mark at or after the time also ends the history the restore needs.
    /// </summary>
    /// <exception cref="InvalidDataException">The log is malformed.</exception>
    public static LogCut Cut(Stream segment, DateTimeOffset at, long? previous)
    {
        var time = at.ToUnixTimeMilliseconds();
        long before = 0;
        var reached = false;
        var open = false;
        try
        {
            int kind;
            while ((kind = segment.ReadByte()) >= 0)
            {
                open = kind is Upsert or Delete or Create or Drop;
                switch (kind)
                {
                    case Upsert:
                        ReadString(segment);
                        ReadInt64(segment);
                        ReadBytes(segment);
                        break;
                    case Delete:
                        ReadString(segment);
                        ReadInt64(segment);
                        break;
                    case Create:
                        ReadString(segment);
                        break;
                    case Drop:
                        ReadString(segment);
                        ReadString(segment);
                        break;
                    case Commit when !reached:
                        return new(before, false, previous, "a transaction it needs has no recorded time (its log predates them, or the clock went back within it)");
                    case Commit:
                        break;
                    case TimedCommit:
                        var (first, last) = (ReadInt64(segment), ReadInt64(segment));
                        if (first < previous)
                        {
                            return new(before, false, previous, "the database's clock went back between two of its transactions");
                        }

                        // Once reached, the rest of the segment is still read, so a later transaction stamped before it is caught.
                        previous = last;
                        if (reached || last * 1000 >= time)
                        {
                            reached = true;
                        }
                        else if ((last + 1) * 1000 <= time)
                        {
                            before = segment.Position;
                        }
                        else
                        {
                            var second = DateTimeOffset.FromUnixTimeSeconds(last);
                            return new(before, false, previous, string.Create(CultureInfo.InvariantCulture,
                                $"one committed in its second, {second:yyyy-MM-ddTHH:mm:ssZ} to {second.AddSeconds(1):yyyy-MM-ddTHH:mm:ssZ}, and transaction times are whole seconds; the nearest earlier time it can place is {second:yyyy-MM-ddTHH:mm:ssZ}"));
                        }

                        break;
                    case ReadMark:
                        reached |= ReadInt64(segment) >= time;
                        before = reached ? before : segment.Position;
                        break;
                    default:
                        throw new InvalidDataException("a change log record has an unknown kind");
                }
            }

            return open ? throw new InvalidDataException("a change log ends inside a transaction") : new(before, reached, previous, null);
        }
        catch (Exception error) when (error is EndOfStreamException or DecoderFallbackException)
        {
            throw new InvalidDataException("a change log is malformed", error);
        }
    }

    /// <summary>The read mark <paramref name="segment"/> ends with, or null when its last record is not one.</summary>
    /// <exception cref="InvalidDataException">The log is malformed.</exception>
    public static DateTimeOffset? EndMark(Stream segment)
    {
        DateTimeOffset? mark = null;
        try
        {
            int kind;
            while ((kind = segment.ReadByte()) >= 0)
            {
                mark = null;
                switch (kind)
                {
                    case Upsert:
                        ReadString(segment);
                        ReadInt64(segment);
                        ReadBytes(segment);
                        break;
                    case Delete:
                        ReadString(segment);
                        ReadInt64(segment);
                        break;
                    case Create:
                        ReadString(segment);
                        break;
                    case Drop:
                        ReadString(segment);
                        ReadString(segment);
                        break;
                    case Commit:
                        break;
                    case TimedCommit:
                        ReadInt64(segment);
                        ReadInt64(segment);
                        break;
                    case ReadMark:
                        mark = DateTimeOffset.FromUnixTimeMilliseconds(ReadInt64(segment));
                        break;
                    default:
                        throw new InvalidDataException("a change log record has an unknown kind");
                }
            }

            return mark;
        }
        catch (Exception error) when (error is EndOfStreamException or DecoderFallbackException)
        {
            throw new InvalidDataException("a change log is malformed", error);
        }
    }

    internal sealed class Replay(TursoConnection connection)
    {
        // Per table: its columns in order and the ordinal of its rowid alias (INTEGER PRIMARY KEY), or -1.
        private readonly Dictionary<string, (string[] Columns, int Alias)> _tables = [with(StringComparer.Ordinal)];

        public bool Open { get; private set; }

        public bool Apply(Stream segment, CancellationToken cancellationToken)
        {
            try
            {
                int kind;
                while ((kind = segment.ReadByte()) >= 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (kind is Commit or TimedCommit)
                    {
                        if (kind == TimedCommit)
                        {
                            ReadInt64(segment);
                            ReadInt64(segment);
                        }

                        if (Open)
                        {
                            Execute("COMMIT;");
                            Open = false;
                        }

                        continue;
                    }

                    if (kind == ReadMark)
                    {
                        ReadInt64(segment);
                        continue;
                    }

                    if (!Open)
                    {
                        Execute("BEGIN IMMEDIATE;");
                        Open = true;
                    }

                    switch (kind)
                    {
                        case Upsert:
                            ApplyUpsert(ReadString(segment), ReadInt64(segment), ReadBytes(segment));
                            break;
                        case Delete:
                            Execute($"DELETE FROM {TableShape.Quote(ReadString(segment))} WHERE rowid = $rowid;", ("$rowid", ReadInt64(segment)));
                            break;
                        case Create:
                            Execute(ReadString(segment));
                            _tables.Clear();
                            break;
                        case Drop:
                            var type = ReadString(segment);
                            var name = ReadString(segment);
                            if (type is not ("table" or "index" or "view" or "trigger"))
                            {
                                return false;
                            }

                            // A table's indexes go with it, so a later drop of one may find nothing.
                            Execute($"DROP {type.ToUpperInvariant()} IF EXISTS {TableShape.Quote(name)};");
                            _tables.Clear();
                            break;
                        default:
                            return false;
                    }
                }

                return true;
            }
            catch (Exception error) when (error is global::Turso.Raw.Public.TursoException or global::Turso.Data.Sqlite.SqliteException or InvalidDataException or EndOfStreamException
                or DecoderFallbackException)
            {
                return false;
            }
        }

        public void Checkpoint() => Execute("PRAGMA wal_checkpoint(TRUNCATE);");

        private void ApplyUpsert(string table, long rowid, byte[] record)
        {
            var values = SqliteRecord.Decode(record);
            var (columns, alias) = Shape(table);
            if (values.Length > columns.Length)
            {
                throw new InvalidDataException("a logged row has more values than its table has columns");
            }

            var names = new List<string> { "rowid" };
            var parameters = new List<(string, object?)> { ("$rowid", rowid) };
            for (var i = 0; i < values.Length; i++)
            {
                // The record stores NULL for the rowid alias; the rowid fills it.
                if (i != alias)
                {
                    names.Add(TableShape.Quote(columns[i]));
                    parameters.Add(($"$v{i}", values[i]));
                }
            }

            Execute($"INSERT OR REPLACE INTO {TableShape.Quote(table)} ({string.Join(", ", names)}) VALUES ({string.Join(", ", parameters.Select(p => p.Item1))});",
                [.. parameters]);
        }

        private (string[] Columns, int Alias) Shape(string table)
        {
            if (!_tables.TryGetValue(table, out var shape))
            {
                shape = _tables[table] = TableShape.Read(connection, table);
            }

            return shape;
        }

        private void Execute(string sql, params (string Name, object? Value)[] parameters)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.Add(new TursoParameter(name, value ?? DBNull.Value));
            }

            command.ExecuteNonQuery();
        }

    }

    private static void WriteString(IBufferWriter<byte> buffer, string value)
    {
        var length = StrictUtf8.GetByteCount(value);
        WriteLength(buffer, length);
        buffer.Advance(StrictUtf8.GetBytes(value, buffer.GetSpan(length)));
    }

    private static void WriteInt64(IBufferWriter<byte> buffer, long value)
    {
        BinaryPrimitives.WriteInt64BigEndian(buffer.GetSpan(8), value);
        buffer.Advance(8);
    }

    private static void WriteLength(IBufferWriter<byte> buffer, int length)
    {
        var value = (uint)length;
        while (value >= 0x80)
        {
            buffer.Write([(byte)(value | 0x80)]);
            value >>= 7;
        }

        buffer.Write([(byte)value]);
    }

    private static string ReadString(Stream segment) => StrictUtf8.GetString(ReadBytes(segment));

    private static long ReadInt64(Stream segment)
    {
        Span<byte> bytes = stackalloc byte[8];
        segment.ReadExactly(bytes);
        return BinaryPrimitives.ReadInt64BigEndian(bytes);
    }

    private static byte[] ReadBytes(Stream segment)
    {
        var bytes = new byte[ReadLength(segment)];
        segment.ReadExactly(bytes);
        return bytes;
    }

    private static int ReadLength(Stream segment)
    {
        long value = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            var next = segment.ReadByte();
            if (next < 0)
            {
                throw new EndOfStreamException("a change log record is truncated");
            }

            value |= (long)(next & 0x7f) << shift;
            if ((next & 0x80) == 0)
            {
                return value <= MaxLength ? (int)value : throw new InvalidDataException("a change log length is out of range");
            }
        }

        throw new InvalidDataException("a change log length is malformed");
    }
}

/// <summary>Decodes a row in SQLite's record format (<c>https://sqlite.org/fileformat.html#record_format</c>).</summary>
internal static class SqliteRecord
{
    // At most `columns` values: the rest are not decoded.
    public static object?[] Decode(ReadOnlySpan<byte> record, int columns = int.MaxValue, bool materializeContent = true)
    {
        var headerSize = Varint(record, out var used);
        if (headerSize < used || headerSize > record.Length)
        {
            throw new InvalidDataException("a record header is out of range");
        }

        var header = record[used..(int)headerSize];
        var body = record[(int)headerSize..];
        var values = new List<object?>();
        while (!header.IsEmpty && values.Count < columns)
        {
            var type = Varint(header, out var read);
            header = header[read..];
            var size = type switch
            {
                0 or 8 or 9 => 0,
                1 => 1,
                2 => 2,
                3 => 3,
                4 => 4,
                5 => 6,
                6 or 7 => 8,
                >= 12 => (int)((type - 12) / 2),
                _ => throw new InvalidDataException("a record uses a reserved serial type"),
            };
            if (size > body.Length)
            {
                throw new InvalidDataException("a record value runs past its end");
            }

            var value = body[..size];
            body = body[size..];
            // Type-only payloads let proof readers validate the record without copying unused content.
            values.Add(type switch
            {
                0 => null,
                8 => 0L,
                9 => 1L,
                7 => BinaryPrimitives.ReadDoubleBigEndian(value),
                <= 6 => Integer(value),
                _ when type % 2 == 0 => materializeContent ? value.ToArray() : [],
                _ => materializeContent ? Encoding.UTF8.GetString(value) : string.Empty,
            });
        }

        return [.. values];
    }

    // A big-endian two's-complement integer of 1 to 8 bytes.
    private static long Integer(ReadOnlySpan<byte> bytes)
    {
        long value = (sbyte)bytes[0];
        foreach (var next in bytes[1..])
        {
            value = (value << 8) | next;
        }

        return value;
    }

    // SQLite's varint: big-endian groups of 7 bits, high bit set on all but the last; a ninth byte carries 8 bits.
    private static long Varint(ReadOnlySpan<byte> bytes, out int read)
    {
        long value = 0;
        for (read = 0; read < 9; read++)
        {
            if (read >= bytes.Length)
            {
                throw new InvalidDataException("a record varint is truncated");
            }

            var next = bytes[read];
            if (read == 8)
            {
                read++;
                return (value << 8) | next;
            }

            value = (value << 7) | (long)(next & 0x7f);
            if ((next & 0x80) == 0)
            {
                read++;
                return value;
            }
        }

        throw new InvalidDataException("a record varint is malformed");
    }
}
