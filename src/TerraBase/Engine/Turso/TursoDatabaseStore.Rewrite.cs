using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using TerraBase.Engine.Lattices;
using Turso;

namespace TerraBase.Engine.Turso;

// A healthy rewrite of this store's file in place (IHealthyRewrite): the store object, its cached references and its
// published identity stay; only the file under it and its incarnation change while every user waits.
internal sealed partial class TursoDatabaseStore
{
    private int _lineage;
    internal long RewriteAnchor
    {
        get => Volatile.Read(ref field);
        set => Volatile.Write(ref field, value);
    } = -1;

    private int _rewriting;
    internal bool Rewriting => Volatile.Read(ref _rewriting) != 0;
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
    private long _uncapturedCommits;
    internal long UncapturedCommits => Volatile.Read(ref _uncapturedCommits);
    internal void UncapturedCommit() => Interlocked.Increment(ref _uncapturedCommits);

    /// <summary>Moves as a healthy rewrite starts: a backup tap opened before ends instead of holding the old file open.</summary>
    internal int Lineage => Volatile.Read(ref _lineage);

    private int _quiescing;

    /// <summary>Whether a healthy rewrite waits for this store's handles or holds it quiesced: a backup tap does not open meanwhile.</summary>
    internal bool Quiescing => Volatile.Read(ref _quiescing) != 0;

    // How long and how often a healthy rewrite waits for this store's readers to end before it takes the writer.
    private static readonly TimeSpan QuietBudget = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan QuietPoll = TimeSpan.FromMilliseconds(20);

    // The longest a healthy rewrite holds the writer waiting for readers; then it is refused and its caller backs off.
    internal static readonly TimeSpan HeldDrain = TimeSpan.FromSeconds(2);

    /// <summary>Runs as a healthy rewrite starts waiting for operations to end or handles to drain; for integration tests.</summary>
    internal Action? RewriteWaiting { get; set; }

    /// <summary>
    /// Waits until no operation, write transaction or native handle of this store is left and holds it so: operations and
    /// writers queue, renters wait, external readers are refused. Disposing the result resumes the store.
    /// </summary>
    internal async Task<IAsyncDisposable> QuiesceAsync(CancellationToken cancellationToken)
    {
        // Before the lineage moves, which ends the backup tap and so restarts a generation: a refused rewrite costs no backup.
        // Checked again under the writer below.
        RefuseUnhealthy();
        Interlocked.Increment(ref _lineage);
        var quiesced = new Quiesced(this);
        Interlocked.Increment(ref _quiescing);
        try
        {
            // Readers get time to end first, holding neither the writer nor the operations gate: a reader held for seconds
            // then delays only this rewrite, never the writers that would queue behind it (TS-982: a 30 s drain under the
            // writer stalled every catalog write). A reader still held at the end refuses the rewrite; the few that start just
            // before the writer is taken drain under it.
            var quiet = Stopwatch.GetTimestamp();
            if (pool.BusyOf(this, _writerSession) > 0)
            {
                RewriteWaiting?.Invoke();
                while (pool.BusyOf(this, _writerSession) > 0)
                {
                    if (Stopwatch.GetElapsedTime(quiet) >= QuietBudget)
                    {
                        throw new RewriteRefusedException("the database's readers did not end before its writer was taken");
                    }

                    await Task.Delay(QuietPoll, cancellationToken).ConfigureAwait(false);
                }
            }

            if (Ledger is not null && !IsSystem)
            {
                // Operations record write intents for this incarnation: none may span the swap.
                while (true)
                {
                    await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
                    if (_activeOperations == 0)
                    {
                        quiesced.Operations = true;
                        break;
                    }

                    // shortcut: new operations still start while this waits, so a database never idle starves the rewrite
                    // until its drain deadline; gate new operations if busy databases need rewriting.
                    var ended = (_operationsEnded ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
                    _operations.Release();
                    RewriteWaiting?.Invoke();
                    await ended.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            // Writers next: a running write transaction may still open readers, which the pause below would hold.
            await AcquireWriterAsync("a healthy rewrite", cancellationToken).ConfigureAwait(false);
            quiesced.Writer = true;
            RefuseUnhealthy();

            pool.Pause(this);
            quiesced.Paused = true;
            // The writer is held: no transaction uses its connection.
            if (_writerSession is { } writer)
            {
                await WriteLane.RunAsync(() => Discard(writer), CancellationToken.None).ConfigureAwait(false);
            }

            // Rented readers close as they are returned and a backup tap as its owner disposes it; nothing is closed under
            // its owner.
            var drained = pool.DrainedAsync(this);
            if (!drained.IsCompleted)
            {
                RewriteWaiting?.Invoke();
            }

            // Writers queue meanwhile: a reader still held after the wait above refuses the rewrite instead of stalling them.
            try
            {
                await drained.WaitAsync(HeldDrain, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Copies this quiesced file to <paramref name="target"/>, verifies the copy holds the same logical state, then
    /// checkpoints this file's log into it and syncs it, so it stands alone. Returns the copied cut's commit offset.
    /// </summary>
    internal async Task<long> WriteRewriteAsync(string target, CancellationToken cancellationToken)
    {
        const string reason = "the source file or its log reported an I/O error or corruption while rewriting";
        ExceptionDispatchInfo? sourceError = null;
        try
        {
            var sourceConnection = await ConnectionStringAsync(io, cancellationToken).ConfigureAwait(false);
            var targetConnection = new global::Turso.Data.Sqlite.SqliteConnectionStringBuilder(sourceConnection) { DataSource = target }.ToString();
            return await io.RunAsync(() =>
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
                try
                {
                    TursoSession.OpenExisting(source, FilePath);
                    Execute(source, "BEGIN;");
                    offset = Convert.ToInt64(Scalar(source, TursoStatements.CurrentOffset), System.Globalization.CultureInfo.InvariantCulture);
                    var stamp = Convert.ToInt64(Scalar(source, ClockSql), System.Globalization.CultureInfo.InvariantCulture);
                    TursoCopy.Write(source, targetConnection, cancellationToken);
                    var expected = RewriteDigest(source);
                    Execute(source, "ROLLBACK;");
                    using (var copy = new TursoConnection(targetConnection))
                    {
                        copy.Open();
                        if (!expected.AsSpan().SequenceEqual(RewriteDigest(copy)))
                        {
                            throw new RewriteRefusedException("the rewritten copy does not hold the same logical state");
                        }
                    }

                    // Nothing else has this file open: the whole log goes into it.
                    using (var checkpoint = source.CreateCommand())
                    {
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
                using var file = File.OpenHandle(FilePath, FileMode.Open, FileAccess.ReadWrite);
                RandomAccess.FlushToDisk(file);
                return offset;
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

        void Close(TursoConnection source)
        {
            try
            {
                source.Dispose();
            }
            catch
            {
                // Possibly still open: no later rewrite of this store may claim its handles are gone.
                pool.RecordFailedClose(this);
                throw;
            }
        }
    }

    private void Resume() => pool.Resume(this);

    // Retries a pool call a healthy rewrite paused, once the store resumes.
    private static async ValueTask<T> WhileRewritingAsync<T>(Func<T> call, CancellationToken cancellationToken)
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
                resumed = paused.Resumed;
            }

            await resumed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static ValueTask WhileRewritingAsync(Action call, CancellationToken cancellationToken) =>
        new(WhileRewritingAsync(() =>
        {
            call();
            return true;
        }, cancellationToken).AsTask());

    /// <summary>The file in place is now the rewritten one; the caller still holds the quiesced store.</summary>
    private void RefuseUnhealthy()
    {
        if ((Ledger?.FailedClosed ?? CloseRefusal) is { } refusal)
        {
            throw new RewriteRefusedException($"only a healthy copy is rewritten: {refusal}");
        }

        if (!IsSystem && (RebuildRow is not null || !Settled))
        {
            throw new RewriteRefusedException("only a copy without a rebuild record is rewritten");
        }
    }

    internal void Rewritten(Ulid incarnation) => _incarnation = new(incarnation);

    // Durable healthy rewrites since this process opened the store: each target to the source it replaced. In memory only (a
    // pin never outlives the process); bounded, a cleared map refuses old pins conservatively.
    private System.Collections.Immutable.ImmutableDictionary<Ulid, Ulid> _healthy = [];

    internal void RewroteHealthily(Ulid source, Ulid target) =>
        System.Collections.Immutable.ImmutableInterlocked.Update(ref _healthy,
            healthy => (healthy.Count >= 64 ? [] : healthy).SetItem(target, source));

    /// <summary>
    /// Whether <paramref name="pinned"/> was replaced, through durable healthy rewrites only, by the file in place now. Any other
    /// replacement (a rebuild swap, a copy, a reopen after restore) leaves no chain to the current incarnation.
    /// </summary>
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

    // The highest stamp of the file, as SeedClockAsync reads it.
    private string ClockSql => "SELECT COALESCE(MAX(stamp), 0) FROM (SELECT MAX(stamp) AS stamp FROM _tb_state"
        + (IsSystem ? "" : " UNION ALL SELECT MAX(stamp) FROM _tb_feed UNION ALL SELECT MAX(stamp) FROM _tb_feed_floor") + ");";

    // Every logical row a copy keeps, by rowid: what TursoCopy deliberately renews (acked ids, the incarnation, sealed shapes)
    // and the engine's own tables are left out.
    private static byte[] RewriteDigest(TursoConnection connection)
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

                Append(hash, rows);
                if (rows.GetString(0) == "table" && rows.GetString(1) is not ("_tb_shape_chunk" or "_tb_shape_snapshot"))
                {
                    tables.Add(rows.GetString(1));
                }
            }
        }

        foreach (var table in tables)
        {
            var columns = TableShape.Read(connection, table).Columns
                .Where(column => (table, column) is not (("_tb_peers", "acked") or ("commit_offset", "incarnation")))
                .Select(TableShape.Quote);
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT rowid, {string.Join(", ", columns)} FROM {TableShape.Quote(table)} ORDER BY rowid;";
            using var rows = command.ExecuteReader();
            hash.AppendData(Encoding.UTF8.GetBytes(table + "\0"));
            while (rows.Read())
            {
                Append(hash, rows);
            }
        }

        return hash.GetHashAndReset();

        // Each value with a type tag and, for text and blobs, its length, so no two rows hash alike by concatenation.
        static void Append(IncrementalHash hash, System.Data.Common.DbDataReader row)
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

    // Resumes in reverse order; each release runs even when an earlier one throws.
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
