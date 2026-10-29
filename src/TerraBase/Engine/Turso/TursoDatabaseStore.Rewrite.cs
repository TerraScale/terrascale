using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using TerraBase.Engine.Lattices;
using Turso;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoDatabaseStore
{
    private int _lineage;
    internal int BackupTaps;
    internal Action<int>? RewriteRoundCompleted { get; set; }
    internal Action? RewriteBeforeWriter { get; set; }
    internal Action? RewriteSessionWaiting { get; set; }
    internal Action<CancellationToken>? RewriteFinalStarting { get; set; }

    private string[] RewriteRefreshTables => IsSystem ? ["_tb_fault"] : [];

    internal Histogram<double>? RewriteSteps { get; init; }
    internal Action<string, double>? RewriteStepMeasured { get; set; }

    internal IDisposable MeasureRewrite(string step) => new RewriteMeasurement(this, step);

    private sealed class RewriteMeasurement(TursoDatabaseStore store, string step) : IDisposable
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        public void Dispose()
        {
            var seconds = Stopwatch.GetElapsedTime(_started).TotalSeconds;
            store.RewriteSteps?.Record(seconds, new KeyValuePair<string, object?>("step", step),
                new KeyValuePair<string, object?>("database", store.IsSystem ? "system" : "tenant"));
            store.RewriteStepMeasured?.Invoke(step, seconds);
        }
    }

    private const int RewriteRounds = 8;
    private static readonly TimeSpan RewriteRoundBudget = TimeSpan.FromMilliseconds(250);

    private int _rewriting;
    private long _uncapturedCommits;
    private string? _uncapturedBy;

    internal long RewriteAnchor
    {
        get => Volatile.Read(ref field);
        set => Volatile.Write(ref field, value);
    } = -1;

    internal bool Rewriting => Volatile.Read(ref _rewriting) != 0;

    internal long UncapturedCommits => Volatile.Read(ref _uncapturedCommits);

    internal Action<CancellationToken>? RewriteSnapshotPinned { get; set; }

    internal bool TryBeginRewrite()
    {
        RefuseUnhealthy();
        if (!Captures)
        {
            throw new RewriteRefusedException("a database without change capture cannot be rewritten");
        }

        if (Interlocked.CompareExchange(ref _rewriting, 1, 0) != 0)
        {
            return false;
        }

        RewriteAnchor = 0;
        return true;
    }

    internal void EndRewrite()
    {
        RewriteAnchor = -1;
        Volatile.Write(ref _rewriting, 0);
    }

    internal string? UncapturedBy => Volatile.Read(ref _uncapturedBy);

    internal void UncapturedCommit(string? caller)
    {
        Volatile.Write(ref _uncapturedBy, caller);
        Interlocked.Increment(ref _uncapturedCommits);
    }

    internal int Lineage => Volatile.Read(ref _lineage);

    private int _quiescing;

    internal bool Quiescing => Volatile.Read(ref _quiescing) != 0;

    private static readonly TimeSpan QuietBudget = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan QuietPoll = TimeSpan.FromMilliseconds(20);

    internal static readonly TimeSpan HeldDrain = RewriteRoundBudget;

    internal Action? RewriteWaiting { get; set; }

    internal async Task<IAsyncDisposable> QuiesceAsync(
    Func<CancellationToken, Task> prepare, Func<CancellationToken, Task> topUp,
    Action writerAcquired, CancellationToken finalToken, CancellationToken cancellationToken)
    {

        RefuseUnhealthy();
        Interlocked.Increment(ref _quiescing);
        var quiesced = new Quiesced(this);
        try
        {

            var quiet = Stopwatch.GetTimestamp();
            if (pool.BusyOf(this, _writerSession) > Volatile.Read(ref BackupTaps))
            {
                RewriteWaiting?.Invoke();
                while (pool.BusyOf(this, _writerSession) > Volatile.Read(ref BackupTaps))
                {
                    if (Stopwatch.GetElapsedTime(quiet) >= QuietBudget)
                    {
                        throw new RewriteRefusedException("the database's readers did not end before its writer was taken");
                    }

                    await Task.Delay(QuietPoll, cancellationToken).ConfigureAwait(false);
                }
            }

            await prepare(cancellationToken).ConfigureAwait(false);

            await HoldOperationsAsync().ConfigureAwait(false);

            await topUp(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            RefuseUnhealthy();

            if (quiesced.Operations)
            {
                quiesced.Operations = false;
                _operations.Release();
            }

            Interlocked.Increment(ref _lineage);

            pool.PauseReaders(this);
            quiesced.Paused = true;
            var taps = Stopwatch.GetTimestamp();
            while (pool.BusyOf(this, _writerSession) > 0)
            {
                RewriteWaiting?.Invoke();
                if (Stopwatch.GetElapsedTime(taps) >= QuietBudget)
                {
                    throw new RewriteRefusedException("the database's backup readers did not end before its writer was taken");
                }
                await Task.Delay(QuietPoll, cancellationToken).ConfigureAwait(false);
            }

            await PrepareRewriteLogAsync(cancellationToken).ConfigureAwait(false);
            await HoldOperationsAsync().ConfigureAwait(false);
            await topUp(cancellationToken).ConfigureAwait(false);

            RewriteBeforeWriter?.Invoke();
            await AcquireWriterAsync("a healthy rewrite", cancellationToken).ConfigureAwait(false);
            quiesced.Writer = true;
            writerAcquired();
            RefuseUnhealthy();

            pool.Pause(this);
            quiesced.Paused = true;

            if (_writerSession is { } writer)
            {
                await WriteLane.RunAsync(() => Discard(writer), finalToken).ConfigureAwait(false);
            }

            var drained = pool.DrainedAsync(this);
            if (!drained.IsCompleted)
            {
                RewriteWaiting?.Invoke();
            }

            try
            {
                await drained.WaitAsync(HeldDrain, finalToken).ConfigureAwait(false);
            }
            catch (TimeoutException error)
            {
                throw new RewriteRefusedException("the database did not drain while its writer was held", error);
            }

            if (pool.FailedClosesOf(this) is > 0 and var failed)
            {
                throw new RewriteRefusedException($"{failed} native closes of this database failed; its handles may still be open");
            }

            return quiesced;

            async Task HoldOperationsAsync()
            {
                if (Ledger is not null && !IsSystem)
                {
                    using var measured = MeasureRewrite("operations_wait");
                    using var quiet = new CancellationTokenSource(QuietBudget, TimeProvider.System);
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, quiet.Token);

                    try
                    {
                        while (true)
                        {
                            await _operations.WaitAsync(wait.Token).ConfigureAwait(false);
                            if (_activeOperations == 0)
                            {
                                quiesced.Operations = true;
                                break;
                            }

                            var ended = (_operationsEnded ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
                            _operations.Release();
                            RewriteWaiting?.Invoke();
                            await ended.WaitAsync(wait.Token).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException error) when (quiet.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    {
                        throw new RewriteRefusedException("the database's operations did not end before its writer was taken", error, RewriteRefusal.Deadline);
                    }
                }
            }
        }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested)
        {
            await quiesced.DisposeAsync().ConfigureAwait(false);
            throw new RewriteRefusedException("the database did not drain before the rewrite was cancelled", error);
        }
        catch
        {
            await quiesced.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal async Task<(long Offset, TursoChangeTap Replay, Ulid Incarnation)> WriteRewriteAsync(
    string target, long minimumOffset, TursoChangeTap? replay, Ulid targetIncarnation, bool finalPass, CancellationToken cancellationToken)
    {
        using var overall = MeasureRewrite(finalPass ? "final" : "off_writer");
        const string reason = "the source file or its log reported an I/O error or corruption while rewriting";
        var verifyReplay = replay is null;
        ExceptionDispatchInfo? sourceError = null;
        TursoRunner runner = finalPass ? WriteLane : io;
        if (!finalPass)
        {
            OpenBackupReader();
        }

        try
        {
            var sourceConnection = await ConnectionStringAsync(runner, cancellationToken).ConfigureAwait(false);
            var targetConnection = new global::Turso.Data.Sqlite.SqliteConnectionStringBuilder(sourceConnection) { DataSource = target }.ToString();
            return await runner.RunAsync(() =>
            {
                var source = new FaultAwareCopySource(sourceConnection, error =>
                {
                    NoteCopyFault(reason);
                    sourceError ??= ExceptionDispatchInfo.Capture(error);
                })
                {
                    CloseRefusal = () => CloseRefusal ?? "terminal rewrite source close",
                };
                long offset;
                var incarnation = targetIncarnation;
                try
                {
                    if (finalPass)
                    {
                        RewriteFinalStarting?.Invoke(cancellationToken);
                        if (new FileInfo(FilePath + "-wal") is { Exists: true, Length: > RewriteFinalWalBytes })
                        {
                            throw new RewriteRefusedException("the final rewrite WAL exceeds its byte budget", reason: RewriteRefusal.Budget);
                        }
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    TursoSession.OpenExisting(source, FilePath);
                    Execute(source, "BEGIN;");
                    if (Ulid.Parse((string)Scalar(source, TursoStatements.Incarnation)!) != Incarnation || Closed)
                    {
                        throw new RewriteRefusedException("the rewrite source incarnation changed");
                    }
                    offset = Convert.ToInt64(Scalar(source, TursoStatements.CurrentOffset), System.Globalization.CultureInfo.InvariantCulture);
                    if (offset < minimumOffset)
                    {
                        throw new RewriteRefusedException("the copied cut precedes the requested commit offset");
                    }
                    var stamp = !verifyReplay ? 0 : Convert.ToInt64(Scalar(source, ClockSql), System.Globalization.CultureInfo.InvariantCulture);
                    if (replay is null)
                    {
                        replay = TursoChangeTap.RewriteFrom(this, io, source);
                        RewriteSnapshotPinned?.Invoke(cancellationToken);
                        using (MeasureRewrite("copy"))
                        {
                            TursoCopy.Write(source, targetConnection, cancellationToken);
                        }
                        using (var identity = new TursoConnection(targetConnection))
                        {
                            identity.Open();
                            incarnation = Ulid.Parse((string)Scalar(identity, TursoStatements.Incarnation)!);
                        }

                        using var file = File.OpenHandle(target, FileMode.Open, FileAccess.ReadWrite);
                        RandomAccess.FlushToDisk(file);
                    }

                    Execute(source, "ROLLBACK;");
                    using (var copy = new TursoConnection(targetConnection))
                    {
                        copy.Open();
                        Execute(copy, "PRAGMA synchronous = OFF;" + TursoSession.CaptureOn);
                        var synced = false;
                        var verified = !verifyReplay;
                        for (var round = 0; ; round++)
                        {
                            var started = Stopwatch.GetTimestamp();
                            Execute(source, "BEGIN;");
                            long changes;
                            using (MeasureRewrite(finalPass ? "final_replay" : "round_replay"))
                            {
                                changes = replay!.ReplayRewrite(source, copy, finalPass, cancellationToken);
                            }
                            offset = Convert.ToInt64(Scalar(source, TursoStatements.CurrentOffset), System.Globalization.CultureInfo.InvariantCulture);
                            var converged = changes <= 1024 && replay!.RewriteBytes <= 4L * 1024 * 1024 && Stopwatch.GetElapsedTime(started) <= RewriteRoundBudget;
                            if (!verified && converged)
                            {
                                using var measured = MeasureRewrite("digest");
                                if (!RewriteDigest(source, cancellationToken).AsSpan().SequenceEqual(RewriteDigest(copy, cancellationToken)))
                                {
                                    throw new RewriteRefusedException("the replayed copy does not hold the same logical state");
                                }
                                verified = true;

                                converged = false;
                            }
                            Execute(source, "ROLLBACK;");
                            if (!finalPass)
                            {
                                RewriteRoundCompleted?.Invoke(round);
                            }
                            cancellationToken.ThrowIfCancellationRequested();
                            if (finalPass || verified && synced && converged)
                            {
                                break;
                            }

                            using (MeasureRewrite("round_sync"))
                            {
                                Execute(copy, "PRAGMA wal_checkpoint(TRUNCATE);");
                                using var file = File.OpenHandle(target, FileMode.Open, FileAccess.ReadWrite);
                                RandomAccess.FlushToDisk(file);
                                synced = true;
                            }

                            if (round + 1 == RewriteRounds)
                            {
                                throw new RewriteRefusedException("the rewrite could not catch up within its round budget", reason: RewriteRefusal.Budget);
                            }
                        }

                        foreach (var table in RewriteRefreshTables)
                        {
                            CheckRewriteTableBudget(source, table);
                            CheckRewriteTableBudget(copy, table);
                        }

                        if (finalPass)
                        {
                            Execute(copy, TursoSession.CaptureOff);
                            using var identity = copy.CreateCommand();
                            identity.CommandText = "UPDATE commit_offset SET incarnation = $incarnation;";
                            identity.Parameters.Add(new TursoParameter("$incarnation", targetIncarnation.ToString()));
                            identity.ExecuteNonQuery();
                            foreach (var table in RewriteRefreshTables)
                            {
                                using var measured = MeasureRewrite("refresh_faults");
                                RefreshRewriteTable(source, copy, table, cancellationToken);
                            }
                            if (!IsSystem)
                            {

                                Execute(copy, "DELETE FROM _tb_shape_chunk; DELETE FROM _tb_shape_snapshot;");
                            }
                            using var refreshMeasured = MeasureRewrite("refresh_digest");
                            foreach (var table in RewriteRefreshTables)
                            {
                                if (!RewriteTableDigest(source, table, cancellationToken).AsSpan().SequenceEqual(RewriteTableDigest(copy, table, cancellationToken)))
                                {
                                    throw new RewriteRefusedException("a refreshed rewrite table differs from its source");
                                }
                            }
                            if (!Equals(Scalar(source, TursoStatements.CurrentOffset), Scalar(copy, TursoStatements.CurrentOffset)))
                            {
                                throw new RewriteRefusedException("the final rewrite offset differs from its source");
                            }
                        }

                        using (MeasureRewrite(finalPass ? "scratch_checkpoint" : "round_checkpoint"))
                        {
                            Execute(copy, "PRAGMA wal_checkpoint(TRUNCATE);");
                        }
                    }

                    if (finalPass)
                    {
                        using var measured = MeasureRewrite("source_checkpoint");

                        using var checkpoint = source.CreateCommand();
                        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                        using var result = checkpoint.ExecuteReader();
                        if (result.Read() && !result.IsDBNull(0) && result.GetInt64(0) != 0)
                        {
                            throw new RewriteRefusedException("the old file's log did not checkpoint");
                        }
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    Member?.Clock.Seed(new Hlc(checked((ulong)stamp)));
                }
                catch
                {
                    Close(source);
                    throw;
                }

                Close(source);

                foreach (var path in finalPass ? [FilePath, target] : (string[])[])
                {
                    using var measured = MeasureRewrite(path == FilePath ? "source_fsync" : "scratch_fsync");
                    using var file = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite);
                    RandomAccess.FlushToDisk(file);
                }

                cancellationToken.ThrowIfCancellationRequested();
                return (offset, replay!, incarnation);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (sourceError is not null)
        {
            _ = await io.WithAdmissionAsync(async () =>
            {
                await RecordCopyFaultAsync(reason).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
            sourceError.Throw();
            throw;
        }

        finally
        {
            if (!finalPass)
            {
                CloseBackupReader();
            }
        }

        void Close(TursoConnection source)
        {
            try
            {
                source.Dispose();
            }
            catch
            {

                pool.RecordFailedClose(this);
                throw;
            }
        }
    }

    internal async Task PrepareRewriteLogAsync(CancellationToken token)
    {
        await WithWriterAsync(writer =>
        {
            using var measured = MeasureRewrite("source_pre_checkpoint");
            using var checkpoint = writer.Connection.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            checkpoint.ExecuteNonQuery();
            return true;
        }, "a rewrite's log checkpoint", token).ConfigureAwait(false);
        await io.RunAsync(() =>
        {
            using var measured = MeasureRewrite("source_pre_fsync");
            using var file = File.OpenHandle(FilePath, FileMode.Open, FileAccess.ReadWrite);
            RandomAccess.FlushToDisk(file);
        }, token).ConfigureAwait(false);
    }

    private const long RewriteFinalWalBytes = 16L * 1024 * 1024;

    private static void CheckRewriteTableBudget(TursoConnection connection, string table)
    {
        const int maxRows = 1024;
        const long maxBytes = 1024 * 1024;
        var (columns, _) = TableShape.Read(connection, table);
        var size = string.Join(" + ", columns.Select(column => $"coalesce(octet_length(CAST({TableShape.Quote(column)} AS BLOB)), 0)"));
        using var budget = connection.CreateCommand();
        budget.CommandText = $"SELECT count(*), coalesce(sum(bytes), 0) FROM (SELECT {size} AS bytes FROM {TableShape.Quote(table)} LIMIT {maxRows + 1});";
        using var rows = budget.ExecuteReader();
        if (!rows.Read() || rows.GetInt64(0) > maxRows || rows.GetInt64(1) > maxBytes)
        {
            throw new RewriteRefusedException($"the rewrite's {table} refresh exceeds its row or byte budget");
        }
    }

    private static void RefreshRewriteTable(TursoConnection source, TursoConnection copy, string table, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Execute(copy, $"DELETE FROM {TableShape.Quote(table)};");
        TursoCopy.CopyRows(source, copy, table, TursoCopy.ChunkRows, TursoCopy.ChunkBytes, token);
    }

    private void Resume() => pool.Resume(this);

    private async ValueTask<T> WhileRewritingAsync<T>(Func<T> call, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task resumed;
            try
            {
                return call();
            }
            catch (StorePausedException paused)
            {
                RewriteSessionWaiting?.Invoke();
                resumed = paused.Resumed;
            }

            await resumed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private ValueTask WhileRewritingAsync(Action call, CancellationToken cancellationToken) =>
        new(WhileRewritingAsync(() =>
        {
            call();
            return true;
        }, cancellationToken).AsTask());

    private void RefuseUnhealthy()
    {
        if ((Ledger?.FailedClosed ?? CloseRefusal) is { } refusal)
        {
            throw new RewriteRefusedException($"only a healthy copy is rewritten: {refusal}");
        }

        if (!IsSystem && (FaultRow is not null || !Settled))
        {
            throw new RewriteRefusedException("only a copy without a fault record is rewritten");
        }
    }

    internal void Rewritten(Ulid incarnation) => _incarnation = new(incarnation);

    private System.Collections.Immutable.ImmutableDictionary<Ulid, Ulid> _healthy = [];

    internal void RewroteHealthily(Ulid source, Ulid target) =>
        System.Collections.Immutable.ImmutableInterlocked.Update(ref _healthy,
            healthy => (healthy.Count >= 64 ? [] : healthy).SetItem(target, source));

    internal bool IsHealthyPredecessor(Ulid pinned)
    {
        var healthy = Volatile.Read(ref _healthy);
        var current = Incarnation;
        for (var step = 0; step < 64 && healthy.TryGetValue(current, out var source); step++)
        {
            if (source == pinned)
            {
                return true;
            }

            current = source;
        }

        return false;
    }

    private string ClockSql => "SELECT COALESCE(MAX(stamp), 0) FROM (SELECT MAX(stamp) AS stamp FROM _tb_state"
        + (IsSystem ? "" : " UNION ALL SELECT MAX(stamp) FROM _tb_feed UNION ALL SELECT MAX(stamp) FROM _tb_feed_floor") + ");";

    private byte[] RewriteDigest(TursoConnection connection, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var tables = new List<string>();
        using (var schema = connection.CreateCommand())
        {
            schema.CommandText = "SELECT type, name, tbl_name, sql FROM sqlite_schema WHERE sql IS NOT NULL ORDER BY type, name;";
            using var rows = schema.ExecuteReader();
            while (rows.Read())
            {
                if (ChangeLog.EngineTable(rows.GetString(2)))
                {
                    continue;
                }

                token.ThrowIfCancellationRequested();
                Append(hash, rows);
                if (rows.GetString(0) == "table" && rows.GetString(1) is not ("_tb_shape_chunk" or "_tb_shape_snapshot") && !RewriteRefreshTables.Contains(rows.GetString(1)))
                {
                    tables.Add(rows.GetString(1));
                }
            }
        }

        foreach (var table in tables)
        {
            HashRewriteTable(connection, table, hash, token);
        }
        return hash.GetHashAndReset();
    }

    private static byte[] RewriteTableDigest(TursoConnection connection, string table, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        HashRewriteTable(connection, table, hash, token);
        return hash.GetHashAndReset();
    }

    private static void HashRewriteTable(TursoConnection connection, string table, IncrementalHash hash, CancellationToken token)
    {
        var columns = TableShape.Read(connection, table).Columns
            .Where(column => (table, column) != ("commit_offset", "incarnation"))
            .Select(TableShape.Quote);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT rowid, {string.Join(", ", columns)} FROM {TableShape.Quote(table)} ORDER BY rowid;";
        using var rows = command.ExecuteReader();
        hash.AppendData(Encoding.UTF8.GetBytes(table + "\0"));
        while (rows.Read())
        {
            token.ThrowIfCancellationRequested();
            Append(hash, rows);
        }
    }

    private static void Append(IncrementalHash hash, System.Data.Common.DbDataReader row)
    {
        Span<byte> scalar = stackalloc byte[9];
        for (var i = 0; i < row.FieldCount; i++)
        {
            switch (row.IsDBNull(i) ? null : row.GetValue(i))
            {
                case null:
                    hash.AppendData([0]);
                    break;
                case long integer:
                    scalar[0] = 1;
                    BinaryPrimitives.WriteInt64BigEndian(scalar[1..], integer);
                    hash.AppendData(scalar);
                    break;
                case double real:
                    scalar[0] = 2;
                    BinaryPrimitives.WriteDoubleBigEndian(scalar[1..], real);
                    hash.AppendData(scalar);
                    break;
                case string text:
                    var bytes = Encoding.UTF8.GetBytes(text);
                    scalar[0] = 3;
                    BinaryPrimitives.WriteInt64BigEndian(scalar[1..], bytes.Length);
                    hash.AppendData(scalar);
                    hash.AppendData(bytes);
                    break;
                case byte[] blob:
                    scalar[0] = 4;
                    BinaryPrimitives.WriteInt64BigEndian(scalar[1..], blob.Length);
                    hash.AppendData(scalar);
                    hash.AppendData(blob);
                    break;
                default:
                    throw new InvalidDataException("a copied value has an unknown type");
            }
        }
    }

    private static void Execute(TursoConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(TursoConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private sealed class Quiesced(TursoDatabaseStore store) : IAsyncDisposable
    {
        internal bool Operations { get; set; }
        internal bool Writer { get; set; }
        internal bool Paused { get; set; }
        private bool Ended { get; set; }

        public ValueTask DisposeAsync()
        {
            try
            {
                if (Paused)
                {
                    Paused = false;
                    store.Resume();
                }
            }
            finally
            {
                try
                {
                    if (Writer)
                    {
                        Writer = false;
                        store.ReleaseWriter();
                    }
                }
                finally
                {
                    if (Operations)
                    {
                        Operations = false;
                        store._operations.Release();
                    }

                    if (!Ended)
                    {
                        Ended = true;
                        Interlocked.Decrement(ref store._quiescing);
                    }
                }
            }

            return ValueTask.CompletedTask;
        }
    }
}
