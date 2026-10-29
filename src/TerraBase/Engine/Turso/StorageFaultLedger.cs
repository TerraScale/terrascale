using System.Diagnostics;
using Turso.Data.Sqlite;
using TerraBase.Features.Memory;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Engine.Turso;

internal sealed class StorageFaultLedger(TursoStorageEngine engine, ILogger logger, IMemoryBudget? memory) : IStorageCopyFaults, IDisposable
{
    internal const string Recording = "storage.fault.recording";
    internal const string Recorded = "storage.fault.recorded";
    private const long Faulting = 0;
    private const int Page = 128;
    private const int MaxReason = 256;
    private const long RowBytes = 4096;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _generation;
    private string? _failed;

    internal string? FailedClosed => Volatile.Read(ref _failed);

    internal void FailClosed(string reason)
    {
        if (Interlocked.CompareExchange(ref _failed, reason, null) is null)
        {
            logger.ZLogCritical($"Storage failed closed: {reason:@Reason}");
        }
    }

    internal string? Refusal(TursoDatabaseStore store) =>
    FailedClosed is { } failed ? $"storage failed closed: {failed}"
    : store.CopyFault is { } fault ? $"this copy is faulted: {fault}"
    : !store.Settled && !store.IsSystem ? "this copy's fault record is not read yet"
    : store.FaultRow is { } row ? $"this database is faulted: {row.Reason}"
    : null;

    public async ValueTask<bool> RefusedAsync(TenantContext database, Ulid incarnation, CancellationToken cancellationToken)
    {
        if (database == SystemDatabase.Identity)
        {
            return FailedClosed is not null
                || (incarnation != Ulid.Empty && await SystemAsync(cancellationToken).ConfigureAwait(false) is var system
                    && system.Incarnation != incarnation && !system.IsHealthyPredecessor(incarnation));
        }

        if (await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not TursoDatabaseStore store)
        {
            return true;
        }

        await SettleAsync(store, cancellationToken).ConfigureAwait(false);
        return store.Incarnation != incarnation && !store.IsHealthyPredecessor(incarnation) || Refusal(store) is not null;
    }

    internal async ValueTask RecordAsync(TenantContext database, Ulid incarnation, string reason)
    {
        if (database == SystemDatabase.Identity)
        {
            FailClosed($"the system database faulted: {reason}");
            return;
        }

        var key = Key.Of(database);
        if (engine.OpenStore(key) is { } faulted && faulted.Incarnation == incarnation)
        {
            faulted.CopyFault = Bounded(reason);
        }

        TursoDatabaseStore system;
        try
        {
            system = await SystemAsync(CancellationToken.None).ConfigureAwait(false);
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            FailClosed($"a copy fault could not be recorded: {error.GetType().Name}");
            throw;
        }
        try
        {
            var current = await ReadRowAsync(system, key, CancellationToken.None).ConfigureAwait(false);
            if (current is null && Deleted(key))
            {

                return;
            }

            if (current is null && engine.KnownIncarnation(key) is { } replacement && replacement != incarnation)
            {

                return;
            }

            if (Faulted(key, current, incarnation, Bounded(reason)) is not { } row)
            {
                return;
            }

            if (engine.OpenStore(key) is { } store)
            {
                store.FaultRow = row;
            }

            await WriteIntentAsync(engine.IntentStore!, key, incarnation, CancellationToken.None, Faulting).ConfigureAwait(false);
            engine.Checkpoint(Recording, database);
            await WriteAsync(system, [row], [], CancellationToken.None).ConfigureAwait(false);
            Committed(key, row);

            await WriteIntentAsync(engine.IntentStore!, key, incarnation, CancellationToken.None).ConfigureAwait(false);
            engine.Checkpoint(Recorded, database);
        }
        catch (Exception error)
        {
            FailClosed($"a copy fault could not be recorded: {error.GetType().Name}");
            throw;
        }
        finally
        {
            _gate.Release();
        }

        logger.ZLogWarning($"A database copy faulted and requires recovery: {reason:@Reason}");
    }

    private static Row? Faulted(Key key, Row? current, Ulid incarnation, string reason) =>
        current is null ? new Row(key, incarnation, reason) : null;

    private bool Deleted(Key key) => engine.OpenStore(key) is null && !engine.FileExists(key.Tenant);

    internal async Task RecordIntentAsync(TursoDatabaseStore store, CancellationToken cancellationToken)
    {
        _ = await SystemAsync(cancellationToken).ConfigureAwait(false);
        var key = Key.Of(store.Tenant!);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {

            ObjectDisposedException.ThrowIf(store.IsClosed, store);

            _ = await InSystemAsync(engine.IntentStore!, command =>
            {
                using var insert = command("""
                    INSERT INTO _tb_write_intent (organization, project, database, incarnation, replica)
                    VALUES ($organization, $project, $database, $incarnation, $replica)
                    ON CONFLICT (organization, project, database) DO UPDATE
                        SET incarnation = excluded.incarnation, replica = excluded.replica
                        WHERE NOT (replica = $faulting AND incarnation = excluded.incarnation);
                    """);
                key.Bind(insert);
                insert.Parameters.AddWithValue("$incarnation", store.Incarnation.ToString());
                insert.Parameters.AddWithValue("$replica", unchecked((long)engine.ReplicaId.Value));
                insert.Parameters.AddWithValue("$faulting", Faulting);
                return insert.ExecuteNonQuery();
            }, cancellationToken, full: Refusal(store) is not null).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task FlagAsync(TursoDatabaseStore store)
    {
        try
        {

            if (store.FaultRow is null && !store.IsClosed && engine.IntentStore is { } journal)
            {
                await WriteIntentAsync(journal, Key.Of(store.Tenant!), store.Incarnation, CancellationToken.None, Faulting).ConfigureAwait(false);
            }
        }
        catch (Exception error)
        {
            FailClosed($"a copy fault could not be marked: {error.GetType().Name}");
        }
    }

    private Task<int> WriteIntentAsync(TursoDatabaseStore system, Key key, Ulid incarnation, CancellationToken cancellationToken, long? replica = null) =>
        InSystemAsync(system, command =>
        {
            using var insert = command("""
                INSERT INTO _tb_write_intent (organization, project, database, incarnation, replica)
                VALUES ($organization, $project, $database, $incarnation, $replica)
                ON CONFLICT (organization, project, database) DO UPDATE
                    SET incarnation = excluded.incarnation, replica = excluded.replica;
                """);
            key.Bind(insert);
            insert.Parameters.AddWithValue("$incarnation", incarnation.ToString());
            insert.Parameters.AddWithValue("$replica", replica ?? unchecked((long)engine.ReplicaId.Value));
            return insert.ExecuteNonQuery();
        }, cancellationToken);

    internal async Task RetireIntentAsync(TursoDatabaseStore store)
    {
        var system = engine.IntentStore ?? throw new InvalidOperationException("the operation intent journal is not open");
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {

            if (Refusal(store) is not null)
            {
                return;
            }
            _ = await InSystemAsync(system, command =>
            {
                using var delete = command("""
                    DELETE FROM _tb_write_intent WHERE organization = $organization AND project = $project AND database = $database
                        AND incarnation = $incarnation AND replica = $replica;
                    """);
                Key.Of(store.Tenant!).Bind(delete);
                delete.Parameters.AddWithValue("$incarnation", store.Incarnation.ToString());
                delete.Parameters.AddWithValue("$replica", unchecked((long)engine.ReplicaId.Value));
                return delete.ExecuteNonQuery();
            }, CancellationToken.None, full: false).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task ForgetAsync(TenantContext database, CancellationToken cancellationToken)
    {
        if (engine.SystemStore is not { } system)
        {
            return;
        }

        var key = Key.Of(database);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var (store, table) in new[] { (system, "_tb_fault"), (engine.IntentStore!, "_tb_write_intent") })
            {
                _ = await InSystemAsync(store, command =>
                {
                    using var delete = command($"DELETE FROM {table} WHERE organization = $organization AND project = $project AND database = $database;");
                    key.Bind(delete);
                    return delete.ExecuteNonQuery();
                }, cancellationToken).ConfigureAwait(false);
            }
            Committed(key, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task RecoverAsync(TursoDatabaseStore system, CancellationToken cancellationToken)
    {
        var replica = unchecked((long)engine.ReplicaId.Value);
        while (await InSystemAsync(engine.IntentStore!, command =>
        {
            using var clean = command("DELETE FROM _tb_write_intent WHERE rowid IN (SELECT rowid FROM _tb_write_intent WHERE replica = $replica LIMIT $limit);");
            clean.Parameters.AddWithValue("$replica", replica);
            clean.Parameters.AddWithValue("$limit", (long)Page);
            return clean.ExecuteNonQuery();
        }, cancellationToken, rows: Page).ConfigureAwait(false) > 0)
        {
        }

        var faults = 0;

        List<double> proofs = [];
        while (true)
        {

            using var reservation = Reserve(Page);
            var intents = await InSystemAsync(engine.IntentStore!, command =>
            {
                List<(long Rowid, Key Key, Ulid Incarnation, bool Faulting)> intents = [];
                using (var select = command("""
                    SELECT rowid, organization, project, database, incarnation,
                        length(organization) = 26 AND length(project) = 26 AND length(database) = 26 AND length(incarnation) = 26,
                        replica = $faulting
                    FROM _tb_write_intent ORDER BY rowid LIMIT $limit;
                    """))
                {
                    select.Parameters.AddWithValue("$limit", (long)Page);
                    select.Parameters.AddWithValue("$faulting", Faulting);
                    using var reader = select.ExecuteReader();
                    while (reader.Read())
                    {

                        if (reader.GetInt64(5) == 0)
                        {
                            throw new InvalidDataException("a write intent is not one the ledger writes");
                        }

                        intents.Add((reader.GetInt64(0), new Key(reader.GetString(1), reader.GetString(2), reader.GetString(3)), Stored(reader.GetString(4)),
                            reader.GetInt64(6) != 0));
                    }
                }

                return intents;
            }, cancellationToken, write: false, rows: 0).ConfigureAwait(false);
            if (intents.Count == 0)
            {
                break;
            }

            HashSet<long> evicted = [];
            foreach (var (rowid, key, _, _) in intents.Where(intent => !intent.Faulting))
            {
                var started = Stopwatch.GetTimestamp();
                if (engine.Evicted(key.Tenant))
                {
                    evicted.Add(rowid);
                }
                proofs.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            _ = await InSystemAsync(system, command =>
            {
                foreach (var (rowid, key, incarnation, _) in intents)
                {
                    if (ReadRow(command, key) is var current && !(current is null && (Deleted(key) || evicted.Contains(rowid)))
                        && Faulted(key, current, incarnation, "the process exited uncleanly while this copy was written") is { } row)
                    {
                        Upsert(command, row);
                        faults++;
                    }
                }
                return true;
            }, cancellationToken, rows: 0).ConfigureAwait(false);
            _ = await InSystemAsync(engine.IntentStore!, command =>
            {
                foreach (var (rowid, _, _, _) in intents)
                {
                    using var delete = command("DELETE FROM _tb_write_intent WHERE rowid = $rowid;");
                    delete.Parameters.AddWithValue("$rowid", rowid);
                    delete.ExecuteNonQuery();
                }
                return true;
            }, cancellationToken, rows: 0).ConfigureAwait(false);
        }

        if (proofs.Count > 0)
        {
            proofs.Sort();
            logger.ZLogInformation($"Restart proofs of {proofs.Count:@Count} copies written at an unclean exit took {proofs[proofs.Count / 2]:@MedianMs} ms median, {proofs[(int)Math.Ceiling(proofs.Count * 0.99) - 1]:@P99Ms} ms p99, {proofs[^1]:@MaxMs} ms max");
        }

        if (faults > 0)
        {
            logger.ZLogWarning($"Database copies on this node are faulted; {faults:@Count} faulted by an unclean ending");
        }
    }

    internal async Task LoadAsync(TursoDatabaseStore store, CancellationToken cancellationToken)
    {
        store.LoadedGeneration = Volatile.Read(ref _generation);
        if (engine.SystemDatabaseExists)
        {
            var system = await SystemAsync(cancellationToken).ConfigureAwait(false);
            store.FaultRow = await ReadRowAsync(system, Key.Of(store.Tenant!), cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task SettleAsync(TursoDatabaseStore store, CancellationToken cancellationToken)
    {
        if (store.Settled)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!store.Settled && Volatile.Read(ref _generation) != store.LoadedGeneration)
            {
                var system = await SystemAsync(cancellationToken).ConfigureAwait(false);
                store.FaultRow = await ReadRowAsync(system, Key.Of(store.Tenant!), cancellationToken).ConfigureAwait(false);
            }

            store.Settled = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Committed(Key key, Row? row)
    {
        _ = Interlocked.Increment(ref _generation);
        if (engine.OpenStore(key) is { } store)
        {
            store.FaultRow = row;
        }
    }

    public void Dispose() => _gate.Dispose();

    private async ValueTask<TursoDatabaseStore> SystemAsync(CancellationToken cancellationToken) =>
        engine.SystemStore ?? (TursoDatabaseStore)await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);

    private async ValueTask<Row?> ReadRowAsync(TursoDatabaseStore system, Key key, CancellationToken cancellationToken) =>
        await InSystemAsync(system, command => ReadRow(command, key), cancellationToken, write: false).ConfigureAwait(false);

    private IMemoryReservation? Reserve(int rows) =>
        memory is null ? null : memory.TryReserve(MemoryLane.Bulk, rows * RowBytes, tenant: null) ?? throw new StorageBusyException();

    private static string Bounded(string reason) => reason.Length <= MaxReason ? reason : reason[..MaxReason];

    private async Task<T> InSystemAsync<T>(
        TursoDatabaseStore system, Func<Func<string, SqliteCommand>, T> call, CancellationToken cancellationToken, bool write = true, int rows = 1,
        bool full = true)
    {
        using var reservation = Reserve(rows);
        try
        {
            return await RunInSystemAsync(system, call, write, full, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException error)
        {

            FailClosed($"the storage fault ledger is damaged: {error.GetType().Name}");
            throw;
        }
    }

    private static async Task<T> RunInSystemAsync<T>(
        TursoDatabaseStore system, Func<Func<string, SqliteCommand>, T> call, bool write, bool full, CancellationToken cancellationToken)
    {
        if (!write)
        {
            await using var read = (TursoStorageTransaction)await system.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
            return await read.LedgerAsync(call, cancellationToken).ConfigureAwait(false);
        }

        await using var transaction = await system.BeginMaintenanceTransactionAsync(cancellationToken, full).ConfigureAwait(false);

        await transaction.SuspendCaptureAsync(cancellationToken, rewriteRefreshOnly: true).ConfigureAwait(false);
        var result = await transaction.LedgerAsync(call, cancellationToken).ConfigureAwait(false);
        _ = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private Task<bool> WriteAsync(TursoDatabaseStore system, IReadOnlyList<Row> rows, IReadOnlyList<Key> retired, CancellationToken cancellationToken) =>
        InSystemAsync(system, command =>
        {
            foreach (var row in rows)
            {
                Upsert(command, row);
            }

            foreach (var key in retired)
            {
                using var delete = command("DELETE FROM _tb_fault WHERE organization = $organization AND project = $project AND database = $database;");
                key.Bind(delete);
                delete.ExecuteNonQuery();
            }

            return true;
        }, cancellationToken);

    private static void Upsert(Func<string, SqliteCommand> command, Row row)
    {
        using var upsert = command("""
            INSERT INTO _tb_fault (organization, project, database, incarnation, reason)
            VALUES ($organization, $project, $database, $incarnation, $reason)
            ON CONFLICT (organization, project, database) DO UPDATE SET incarnation = excluded.incarnation, reason = excluded.reason;
            """);
        row.Key.Bind(upsert);
        upsert.Parameters.AddWithValue("$incarnation", row.Incarnation.ToString());
        upsert.Parameters.AddWithValue("$reason", row.Reason);
        upsert.ExecuteNonQuery();
    }

    private static Row? ReadRow(Func<string, SqliteCommand> command, Key key)
    {
        using var select = command("""
            SELECT incarnation, reason,
                length(incarnation) = 26 AND length(reason) <= 256
            FROM _tb_fault WHERE organization = $organization AND project = $project AND database = $database;
            """);
        key.Bind(select);
        using var reader = select.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }
        if (reader.GetInt64(2) == 0)
        {
            throw new InvalidDataException("a fault record is not one the ledger writes");
        }
        return new Row(key, Stored(reader.GetString(0)), reader.GetString(1));
    }

    private static Ulid Stored(string text) =>
        Ulid.TryParse(text, out var incarnation) && incarnation != Ulid.Empty && incarnation.ToString() == text
            ? incarnation : throw new InvalidDataException("a stored incarnation is not a ULID");

    internal readonly record struct Key(string Organization, string Project, string Database)
    {
        public static Key Of(TenantContext database) => new(database.OrganizationId, database.ProjectId!, database.DatabaseId!);

        public TenantContext Tenant => new(Organization, PlanTier.Hobby, Project, Database);

        public void Bind(SqliteCommand command)
        {
            command.Parameters.AddWithValue("$organization", Organization);
            command.Parameters.AddWithValue("$project", Project);
            command.Parameters.AddWithValue("$database", Database);
        }
    }

    internal sealed record Row(Key Key, Ulid Incarnation, string Reason);
}
