using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
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

    private readonly long _run = Random.Shared.NextInt64(1, long.MaxValue);
    private const int Page = 128;
    private const int MaxReason = 256;
    private const long RowBytes = 4096;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _generation;
    private string? _failed;

    private readonly CancellationTokenSource _refused = new();
    internal CancellationToken Refused => _refused.Token;
    private long _faultCount;
    internal long FaultCount => Volatile.Read(ref _faultCount);
    internal int PendingCount => _pending.Count;
    internal Counter<long>? IncompleteProofs { get; set; }
    private readonly ConcurrentDictionary<Key, PendingProof> _pending = new();
    private readonly ConcurrentDictionary<Key, FaultIntent> _faulting = new();
    private readonly ConcurrentDictionary<Key, byte> _claims = new();
    private readonly ConcurrentDictionary<Key, CopyMutationBarrier> _mutations = new();

    internal CopyMutationBarrier Mutations(TenantContext database) =>
        _mutations.GetOrAdd(Key.Of(database), _ => new CopyMutationBarrier());
    private readonly CancellationTokenSource _retryStopping = new();
    private Task? _retry;

    private sealed record FaultIntent(Ulid Incarnation, Ulid Creation)
    {
        internal string Identity => (Incarnation == Ulid.Empty ? Creation : Incarnation).ToString();
    }

    private sealed record PendingProof(long Rowid, Key Key, Ulid Incarnation, Ulid Replacement, int Attempts, long At);

    internal void ThrowIfPending(TenantContext database)
    {
        if (_pending.ContainsKey(Key.Of(database)))
        {
            throw new StorageProofPendingException();
        }
    }

    private readonly Dictionary<Key, CancellationTokenSource> _readers = [];

    internal async Task<CopyRead> BeginReadAsync(TenantContext database, CancellationToken cancellationToken)
    {
        var system = await SystemAsync(cancellationToken).ConfigureAwait(false);
        var key = Key.Of(database);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfPending(database);
            if (FailedClosed is not null || _faulting.ContainsKey(key)
                || await ReadRowAsync(system, key, cancellationToken).ConfigureAwait(false) is not null)
            {
                throw new StorageCopyFaultedException("Storage requires recovery.");
            }
            var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Refused);
            if (!_readers.TryAdd(key, source))
            {
                source.Dispose();
                throw new InvalidOperationException("a direct copy reader already owns this database");
            }
            return new CopyRead(this, key, source);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal sealed class CopyRead(StorageFaultLedger ledger, Key key, CancellationTokenSource source) : IAsyncDisposable
    {
        internal CancellationToken Token => source.Token;
        public ValueTask DisposeAsync() => new(ledger.EndReadAsync(key, source));
    }

    private async Task EndReadAsync(Key key, CancellationTokenSource source)
    {
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _ = _readers.Remove(key);
            source.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task ThrowIfUnavailableAsync(TenantContext database, CancellationToken cancellationToken)
    {
        var system = await SystemAsync(cancellationToken).ConfigureAwait(false);
        ThrowIfPending(database);
        if (FailedClosed is not null || _faulting.ContainsKey(Key.Of(database))
            || await ReadRowAsync(system, Key.Of(database), cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new StorageCopyFaultedException("Storage requires recovery.");
        }
    }

    internal string? FailedClosed => Volatile.Read(ref _failed);

    internal void FailClosed(string reason)
    {
        if (Interlocked.CompareExchange(ref _failed, reason, null) is null)
        {
            _refused.Cancel();
            logger.ZLogCritical($"Storage failed closed: {reason:@Reason}");
        }
    }

    internal string? Refusal(TursoDatabaseStore store) =>
    FailedClosed is { } failed ? $"storage failed closed: {failed}"
    : !store.IsSystem && store.Tenant is { } database && _pending.ContainsKey(Key.Of(database)) ? "this copy has a pending proof"
    : !store.IsSystem && store.Tenant is { } tenant && _faulting.ContainsKey(Key.Of(tenant)) ? "this location has a durable fault intent"
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
        }
        catch (Exception error)
        {
            FailClosed($"the storage fault ledger could not be opened: {error.GetType().Name}");
            throw;
        }
        var deleted = Deleted(key);
        var binding = await BindingAsync(key, incarnation).ConfigureAwait(false);
        using var mutation = await Mutations(database).PublishFaultAsync(engine.QueryBudget).ConfigureAwait(false);

        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var current = await ReadRowAsync(system, key, CancellationToken.None).ConfigureAwait(false);
            if (current is null && deleted)
            {

                return;
            }

            if (current is null && engine.KnownIncarnation(key) is { } replacement && replacement != incarnation)
            {

                return;
            }

            if (incarnation == Ulid.Empty)
            {
                if (_readers.TryGetValue(key, out var openingReader))
                {
                    openingReader.Cancel();
                }
                binding = _faulting.GetOrAdd(key, binding);
                await WriteFaultIntentAsync(engine.IntentStore!, key, binding, CancellationToken.None).ConfigureAwait(false);
                engine.Checkpoint(Recording, database);
                return;
            }

            if (Faulted(key, current, incarnation, Bounded(reason)) is not { } row)
            {
                return;
            }

            if (_readers.TryGetValue(key, out var reader))
            {
                reader.Cancel();
            }
            if (engine.OpenStore(key) is { } store)
            {
                store.FaultRow = row;
            }

            binding = _faulting.GetOrAdd(key, binding);
            await WriteFaultIntentAsync(engine.IntentStore!, key, binding, CancellationToken.None).ConfigureAwait(false);
            engine.Checkpoint(Recording, database);
            await WriteAsync(system, [row], [], CancellationToken.None).ConfigureAwait(false);
            Committed(key, row);
            Interlocked.Increment(ref _faultCount);

            await RetireFaultIntentAsync(key, CancellationToken.None).ConfigureAwait(false);
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
            store.ThrowIfUnavailable();

            var written = await InSystemAsync(engine.IntentStore!, command =>
            {
                using var insert = command("""
                    INSERT INTO _tb_write_intent (organization, project, database, incarnation, run)
                    VALUES ($organization, $project, $database, $incarnation, $run)
                    ON CONFLICT (organization, project, database) DO UPDATE
                        SET incarnation = excluded.incarnation, run = excluded.run
                        WHERE run != $faulting;
                    """);
                key.Bind(insert);
                insert.Parameters.AddWithValue("$incarnation", store.Incarnation.ToString());
                insert.Parameters.AddWithValue("$run", _run);
                insert.Parameters.AddWithValue("$faulting", Faulting);
                return insert.ExecuteNonQuery();
            }, cancellationToken, full: false).ConfigureAwait(false);
            if (written == 0)
            {
                throw new StorageCopyFaultedException("a durable fault intent must become permanent before another write");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task FlagAsync(TursoDatabaseStore store)
    {
        using var mutation = await Mutations(store.Tenant!).PublishFaultAsync(engine.QueryBudget).ConfigureAwait(false);
        try
        {
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (store.FaultRow is null && !store.IsClosed && engine.IntentStore is { } journal)
                {
                    var key = Key.Of(store.Tenant!);
                    if (_readers.TryGetValue(key, out var reader))
                    {
                        reader.Cancel();
                    }
                    var intent = _faulting.GetOrAdd(key, new FaultIntent(store.Incarnation, store.CreationIdentity));
                    await WriteFaultIntentAsync(journal, key, intent, CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception error)
        {
            FailClosed($"a copy fault could not be marked: {error.GetType().Name}");
        }
    }

    private Task<int> WriteFaultIntentAsync(TursoDatabaseStore system, Key key, FaultIntent intent, CancellationToken cancellationToken) =>
        InSystemAsync(system, command =>
        {
            using var bind = command("""
                INSERT INTO _tb_fault_binding (organization, project, database, creation)
                VALUES ($organization, $project, $database, $creation)
                ON CONFLICT (organization, project, database) DO NOTHING;
                """);
            key.Bind(bind);
            bind.Parameters.AddWithValue("$creation", intent.Creation.ToString());
            bind.ExecuteNonQuery();
            using var insert = command("""
                INSERT INTO _tb_write_intent (organization, project, database, incarnation, run)
                VALUES ($organization, $project, $database, $incarnation, $run)
                ON CONFLICT (organization, project, database) DO UPDATE
                    SET incarnation = excluded.incarnation, run = excluded.run WHERE run != $faulting;
                """);
            key.Bind(insert);
            insert.Parameters.AddWithValue("$incarnation", intent.Identity);
            insert.Parameters.AddWithValue("$run", Faulting);
            insert.Parameters.AddWithValue("$faulting", Faulting);
            return insert.ExecuteNonQuery();
        }, cancellationToken);

    private async Task<FaultIntent> BindingAsync(Key key, Ulid incarnation)
    {
        if (_faulting.TryGetValue(key, out var binding))
        {
            return binding;
        }
        if (engine.OpenStore(key) is { CreationIdentity: var creation } && creation != Ulid.Empty)
        {
            return new FaultIntent(incarnation, creation);
        }
        try
        {
            return new FaultIntent(incarnation, await CreationIdentityAsync(key, CancellationToken.None).ConfigureAwait(false));
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            logger.ZLogWarning($"A faulted copy's creation identity is unavailable; its location remains refused: {error.GetType().Name:@Error}");
            return new FaultIntent(incarnation == Ulid.Empty ? Ulid.Parse(key.Database) : incarnation, Ulid.Empty);
        }
    }

    private async Task<Ulid> CreationIdentityAsync(Key key, CancellationToken cancellationToken) =>
        await engine.Io.RunAsync(() => new Ulid(SHA256.HashData(DatabaseKeys.ReadRecord(engine.CopyPath(key.Tenant))).AsSpan(0, 16)), cancellationToken).ConfigureAwait(false);

    private async Task<StorageProof> FaultIntentProofAsync(Key key, FaultIntent intent, CancellationToken cancellationToken)
    {
        try
        {
            if (intent.Creation == Ulid.Empty)
            {
                return new ProofIncomplete("fault_identity_unavailable");
            }
            return intent.Creation == await CreationIdentityAsync(key, cancellationToken).ConfigureAwait(false)
                ? new ProofDamaged("a durable fault recording was interrupted")
                : new ProofIncomplete("fault_identity_changed");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return new ProofIncomplete("fault_identity_unavailable");
        }
    }

    private async Task RetireFaultIntentAsync(Key key, CancellationToken cancellationToken)
    {
        _ = await InSystemAsync(engine.IntentStore!, command =>
        {
            using var delete = command("DELETE FROM _tb_write_intent WHERE organization = $organization AND project = $project AND database = $database AND run = $faulting;");
            key.Bind(delete);
            delete.Parameters.AddWithValue("$faulting", Faulting);
            var count = delete.ExecuteNonQuery();
            using var binding = command("DELETE FROM _tb_fault_binding WHERE organization = $organization AND project = $project AND database = $database;");
            key.Bind(binding);
            binding.ExecuteNonQuery();
            return count;
        }, cancellationToken).ConfigureAwait(false);
        _faulting.TryRemove(key, out _);
    }

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
                        AND incarnation = $incarnation AND run = $run;
                    """);
                Key.Of(store.Tenant!).Bind(delete);
                delete.Parameters.AddWithValue("$incarnation", store.Incarnation.ToString());
                delete.Parameters.AddWithValue("$run", _run);
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
            foreach (var (store, table) in new[] { (system, "_tb_fault"), (engine.IntentStore!, "_tb_write_intent"), (engine.IntentStore!, "_tb_fault_binding") })
            {
                _ = await InSystemAsync(store, command =>
                {
                    using var delete = command($"DELETE FROM {table} WHERE organization = $organization AND project = $project AND database = $database;");
                    key.Bind(delete);
                    return delete.ExecuteNonQuery();
                }, cancellationToken).ConfigureAwait(false);
            }
            Committed(key, null);
            _pending.TryRemove(key, out _);
            _faulting.TryRemove(key, out _);
            _mutations.TryRemove(key, out _);
            await RefreshFaultCountAsync(system, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task ClearRunAsync()
    {
        if (engine.IntentStore is not { } journal)
        {
            return;
        }

        if (!_pending.IsEmpty)
        {
            return;
        }
        while (await InSystemAsync(journal, command =>
        {
            using var clean = command("DELETE FROM _tb_write_intent WHERE rowid IN (SELECT rowid FROM _tb_write_intent WHERE run = $run LIMIT $limit);");
            clean.Parameters.AddWithValue("$run", _run);
            clean.Parameters.AddWithValue("$limit", (long)Page);
            return clean.ExecuteNonQuery();
        }, CancellationToken.None, rows: Page).ConfigureAwait(false) > 0)
        {
        }
    }

    internal async Task RecoverAsync(TursoDatabaseStore system, CancellationToken cancellationToken)
    {
        _ = await InSystemAsync(engine.IntentStore!, command =>
        {
            using var create = command("""
                CREATE TABLE IF NOT EXISTS _tb_fault_binding (
                    organization TEXT NOT NULL, project TEXT NOT NULL, database TEXT NOT NULL, creation TEXT NOT NULL,
                    UNIQUE (organization, project, database)
                );
                """);
            return create.ExecuteNonQuery();
        }, cancellationToken).ConfigureAwait(false);
        var recoveryStarted = Stopwatch.GetTimestamp();
        await RefreshFaultCountAsync(system, cancellationToken).ConfigureAwait(false);
        var before = FaultCount;
        long after = 0;
        List<double> proofs = [];
        while (true)
        {
            using var reservation = Reserve(Page);
            var intents = await InSystemAsync(engine.IntentStore!, command =>
            {
                List<(long Rowid, Key Key, Ulid Incarnation, Ulid Replacement, bool Faulting)> intents = [];
                using (var select = command("""
                    SELECT rowid, organization, project, database, incarnation,
                        length(organization) = 26 AND length(project) = 26 AND length(database) = 26
                            AND (length(incarnation) = 26 OR (length(incarnation) = 53 AND substr(incarnation, 27, 1) = ':')),
                        run = $faulting
                    FROM _tb_write_intent WHERE rowid > $after ORDER BY rowid LIMIT $limit;
                    """))
                {
                    select.Parameters.AddWithValue("$limit", (long)Page);
                    select.Parameters.AddWithValue("$after", after);
                    select.Parameters.AddWithValue("$faulting", Faulting);
                    using var reader = select.ExecuteReader();
                    while (reader.Read())
                    {

                        if (reader.GetInt64(5) == 0)
                        {
                            throw new InvalidDataException("a write intent is not one the ledger writes");
                        }

                        var identity = reader.GetString(4);
                        var incarnation = reader.GetInt64(6) != 0 && identity == Ulid.Empty.ToString() ? Ulid.Empty : Stored(identity[..26]);
                        var replacement = identity.Length == 26 ? incarnation : Stored(identity[27..]);
                        intents.Add((reader.GetInt64(0), new Key(reader.GetString(1), reader.GetString(2), reader.GetString(3)), incarnation, replacement,
                            reader.GetInt64(6) != 0));
                    }
                }

                return intents;
            }, cancellationToken, write: false, rows: 0).ConfigureAwait(false);
            if (intents.Count == 0)
            {
                break;
            }

            after = intents[^1].Rowid;
            foreach (var (rowid, key, incarnation, replacement, faulting) in intents)
            {
                var started = Stopwatch.GetTimestamp();
                var intent = new PendingProof(rowid, key, incarnation, replacement, 0, engine.Time.GetTimestamp());
                if (faulting)
                {
                    var creation = await InSystemAsync(engine.IntentStore!, command =>
                    {
                        using var select = command("SELECT creation FROM _tb_fault_binding WHERE organization = $organization AND project = $project AND database = $database;");
                        key.Bind(select);
                        return select.ExecuteScalar() is string creation && creation != Ulid.Empty.ToString() ? Stored(creation) : Ulid.Empty;
                    }, cancellationToken, write: false).ConfigureAwait(false);
                    _faulting[key] = new FaultIntent(incarnation, creation);
                }
                var result = faulting ? await FaultIntentProofAsync(key, _faulting[key], cancellationToken).ConfigureAwait(false)
                    : Deleted(key) ? new ProofPassed()
                    : await ReadRowAsync(system, key, cancellationToken).ConfigureAwait(false) is { } fault
                        ? new ProofDamaged(fault.Reason)
                        : await engine.RecoverCopyAsync(key.Tenant, incarnation, replacement, cancellationToken).ConfigureAwait(false);
                await CompleteProofAsync(system, intent, result, faulting ? _faulting[key] : null, cancellationToken).ConfigureAwait(false);
                proofs.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }

        if (proofs.Count > 0)
        {
            proofs.Sort();
            logger.ZLogInformation($"Restart proofs of {proofs.Count:@Count} copies written at an unclean exit took {Stopwatch.GetElapsedTime(recoveryStarted).TotalMilliseconds:@TotalMs} ms total, {proofs[proofs.Count / 2]:@MedianMs} ms median, {proofs[(int)Math.Ceiling(proofs.Count * 0.99) - 1]:@P99Ms} ms p99, {proofs[^1]:@MaxMs} ms max");
        }
        if (FaultCount > before)
        {
            logger.ZLogWarning($"Database copies on this node are faulted; {FaultCount - before:@Count} faulted by an unclean ending");
        }
    }

    private async Task RefreshFaultCountAsync(TursoDatabaseStore system, CancellationToken cancellationToken)
    {
        var count = await InSystemAsync(system, command =>
        {
            using var select = command("SELECT count(*) FROM _tb_fault;");
            return (long)select.ExecuteScalar()!;
        }, cancellationToken, write: false).ConfigureAwait(false);
        Volatile.Write(ref _faultCount, count);
    }

    private async Task CompleteProofAsync(TursoDatabaseStore system, PendingProof intent, StorageProof proof, FaultIntent? binding, CancellationToken cancellationToken)
    {
        var faulting = await InSystemAsync(engine.IntentStore!, command =>
        {
            using var select = command("SELECT run = $faulting FROM _tb_write_intent WHERE rowid = $rowid;");
            select.Parameters.AddWithValue("$faulting", Faulting);
            select.Parameters.AddWithValue("$rowid", intent.Rowid);
            return select.ExecuteScalar() is long value && value != 0;
        }, cancellationToken, write: false).ConfigureAwait(false);
        if (faulting && (binding is null || !_faulting.TryGetValue(intent.Key, out var currentBinding) || currentBinding != binding))
        {
            proof = new ProofIncomplete("fault_binding_changed");
        }
        if (proof is ProofIncomplete incomplete)
        {
            _pending[intent.Key] = intent with { Attempts = Math.Min(5, intent.Attempts + 1), At = engine.Time.GetTimestamp() };
            IncompleteProofs?.Add(1, new KeyValuePair<string, object?>("reason", incomplete.Reason));
            logger.ZLogWarning($"A database recovery proof is incomplete and will retry: {incomplete.Reason:@Reason}");
            return;
        }

        if (proof is ProofDamaged damaged)
        {
            var identity = intent.Incarnation == Ulid.Empty ? binding!.Creation : intent.Incarnation;
            var row = Faulted(intent.Key, await ReadRowAsync(system, intent.Key, cancellationToken).ConfigureAwait(false), identity, damaged.Reason);
            if (row is not null)
            {
                await WriteAsync(system, [row], [], cancellationToken).ConfigureAwait(false);
                Committed(intent.Key, row);
                Interlocked.Increment(ref _faultCount);
            }
        }
        _ = await InSystemAsync(engine.IntentStore!, command =>
        {
            using var delete = command("DELETE FROM _tb_write_intent WHERE rowid = $rowid AND incarnation = $incarnation;");
            delete.Parameters.AddWithValue("$rowid", intent.Rowid);
            delete.Parameters.AddWithValue("$incarnation", intent.Incarnation == intent.Replacement
                ? intent.Incarnation.ToString() : intent.Incarnation + ":" + intent.Replacement);
            var count = delete.ExecuteNonQuery();
            using var binding = command("DELETE FROM _tb_fault_binding WHERE organization = $organization AND project = $project AND database = $database;");
            intent.Key.Bind(binding);
            binding.ExecuteNonQuery();
            return count;
        }, cancellationToken).ConfigureAwait(false);
        if (faulting)
        {
            await RetireFaultIntentAsync(intent.Key, cancellationToken).ConfigureAwait(false);
        }
        if (_pending.TryRemove(intent.Key, out _))
        {
            logger.ZLogInformation($"A pending database recovery proof completed");
        }
    }

    internal void StartRetries() => _retry ??= RetryPendingAsync(_retryStopping.Token);

    internal async Task StopRetriesAsync()
    {
        await _retryStopping.CancelAsync().ConfigureAwait(false);
        if (_retry is not null)
        {
            await _retry.ConfigureAwait(false);
        }
    }

    private async Task RetryPendingAsync(CancellationToken cancellationToken)
    {
        List<Task> running = [];
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), engine.Time);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                foreach (var completed in running.Where(task => task.IsCompleted))
                {
                    await completed.ConfigureAwait(false);
                }
                running.RemoveAll(task => task.IsCompleted);
                foreach (var intent in _pending.Values)
                {
                    if (running.Count == 4)
                    {
                        break;
                    }
                    if (engine.Time.GetElapsedTime(intent.At) >= TimeSpan.FromSeconds(1 << intent.Attempts) && !_claims.ContainsKey(intent.Key))
                    {
                        running.Add(RetryProofAsync(intent, cancellationToken).AsTask());
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.ZLogInformation($"Recovery proof retries stopped with {_pending.Count:@Count} databases still pending");
        }
        catch (Exception error)
        {
            FailClosed("background recovery bookkeeping could not complete");
            logger.ZLogError(error, $"Background recovery bookkeeping failed");
        }
        finally
        {
            await Task.WhenAll(running).ConfigureAwait(false);
        }
    }

    private async ValueTask RetryProofAsync(PendingProof intent, CancellationToken cancellationToken)
    {
        if (engine.Time.GetElapsedTime(intent.At) < TimeSpan.FromSeconds(1 << intent.Attempts) || !_claims.TryAdd(intent.Key, 0))
        {
            return;
        }
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!_pending.TryGetValue(intent.Key, out var current) || current != intent)
                {
                    return;
                }
            }
            finally
            {
                _gate.Release();
            }
            _faulting.TryGetValue(intent.Key, out var fault);
            var proof = fault is not null
                ? await FaultIntentProofAsync(intent.Key, fault, cancellationToken).ConfigureAwait(false)
                : await engine.RecoverCopyAsync(intent.Key.Tenant, intent.Incarnation, intent.Replacement, cancellationToken).ConfigureAwait(false);
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_pending.TryGetValue(intent.Key, out var current) && current == intent)
                {
                    await CompleteProofAsync(engine.SystemStore!, intent, proof, fault, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.ZLogInformation($"An active recovery proof stopped with its intent retained");
        }
        finally
        {
            _claims.TryRemove(intent.Key, out _);
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
        if (store.FaultRow is not null || _faulting.ContainsKey(Key.Of(store.Tenant!)))
        {
            throw new StorageCopyFaultedException("Storage requires recovery.");
        }
        ThrowIfPending(store.Tenant!);
        store.CreationIdentity = await CreationIdentityAsync(Key.Of(store.Tenant!), cancellationToken).ConfigureAwait(false);
    }

    internal async Task SettleAsync(TursoDatabaseStore store, CancellationToken cancellationToken)
    {
        if (store.Settled)
        {
            return;
        }

        var system = await SystemAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!store.Settled && Volatile.Read(ref _generation) != store.LoadedGeneration)
            {
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

    public void Dispose()
    {
        foreach (var barrier in _mutations.Values)
        {
            barrier.Dispose();
        }
        _gate.Dispose();
        _refused.Dispose();
        _retryStopping.Dispose();
    }

    private async ValueTask<TursoDatabaseStore> SystemAsync(CancellationToken cancellationToken) =>
        engine.SystemStore ?? (TursoDatabaseStore)await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);

    private async ValueTask<Row?> ReadRowAsync(TursoDatabaseStore system, Key key, CancellationToken cancellationToken) =>
        await InSystemAsync(system, command => ReadRow(command, key), cancellationToken, write: false).ConfigureAwait(false);

    private IMemoryReservation? Reserve(int rows) =>
        memory is null ? null : memory.TryReserve(rows * RowBytes, tenant: null) ?? throw new StorageBusyException();

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
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
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
