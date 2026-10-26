using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Turso.Data.Sqlite;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Memory;
using TerraBase.Features.Replication;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Engine.Turso;

/// <summary>
/// This node's copy faults and rebuild records (docs/terrabase/decisions/0013a, sections 1.3 and 9), one row per database
/// in the system database's <c>_tb_rebuild</c>, written with FULL and change capture off, so no peer receives them. A
/// snapshot of the whole system database still carries them: a row names the member that wrote it, and another member's
/// row keeps its fault but loses its refill proof and any certification.
/// <para>
/// A faulted incarnation, and every incarnation of a database under rebuild, refuse ordinary writes until the completion
/// barrier certifies the new one. The refusal is the open store's copy of its row (<see cref="TursoDatabaseStore.RebuildRow"/>):
/// nothing is held for a database whose store is closed.
/// </para>
/// <para>
/// The open gap of section 1.3, a crash between a failed sync and its fault record, is closed by write intents: before an
/// incarnation's first active storage operation that can write starts, <c>_tb_write_intent</c> records it (NORMAL suffices for
/// the process crash an ordinary intent speaks for, decision 0013d) under this process's replica id in a separate node-local journal, independent of the catalog writer. A clean stop keeps the replica id; any other ending raises its epoch (<see cref="ReplicaIdentityFile"/>),
/// so at the next start every remaining intent of another replica id becomes a fault, except where the copy's own FULL log
/// is proven to be all that remains: no record names the database, no fault of it was being recorded (that intent's
/// replica is <see cref="Faulting"/>), and the kernel caches none of its pages (<see cref="PageCache"/>), so the failed sync
/// the gap is about cannot be read back. Successful cleanup retires the intent when no operation remains. An uncertified copy that faults (one under
/// rebuild) is never certified: its record goes back to pending, and the rebuild starts over from the side copy, keeping
/// that uncertified copy unwritten at its own side path (at most one) for the salvage. Nothing is deleted or overwritten
/// before a certified rebuild; a second uncertain copy refuses the rebuild and stays in place.
/// </para>
/// <para>
/// Lock order: a database's writer, then the ledger's gate, then the system database's writer. Nothing here takes a client
/// database's writer while holding the gate. A fault of the system database, or one that cannot be recorded, fails the
/// node closed: every write is refused, readiness fails and the stop is unclean.
/// </para>
/// </summary>
internal sealed class RebuildLedger(TursoStorageEngine engine, ILogger logger, IMemoryBudget? memory)
    : IRebuildLedger, IRebuildRecovery, IReplicaCopyFaults, IRebuildRepair, IDisposable
{
    /// <summary>Checkpoint after a copy fault is refused in memory, before its FULL record commits.</summary>
    internal const string Recording = "replication.fault.recording";

    /// <summary>Checkpoint after a copy fault's FULL record committed, before the recovery wake.</summary>
    internal const string Recorded = "replication.fault.recorded";

    // Host-owned integration checkpoints; production's checkpoint is a no-op.
    internal const string Publishing = "replication.rebuild.publishing";
    internal const string Published = "replication.rebuild.published";
    internal const string BarrierDisposed = "replication.rebuild.barrier_disposed";
    internal const string BarrierReadDisposed = "replication.rebuild.barrier_read_disposed";

    // The replica of an intent whose fault is being recorded: no replica id has member 0, so the next start faults it.
    private const long Faulting = 0;

    // Rows and intents per page and transaction: recovery and enumeration hold no more at once.
    private const int Page = 128;

    // The longest reason and source member name a row holds (the schema checks both), and what one row read can take: its
    // strings as .NET text, with room for the record objects.
    private const int MaxReason = 256;
    private const int MaxMachine = 255;
    private const long RowBytes = 4096;

    private const string Columns =
        "rowid, organization, project, database, member, old_incarnation, phase, new_incarnation, reason, source_machine, source_incarnation, source_head, "
        + "additional_incarnation, "
        + "length(organization) = 26 AND length(project) = 26 AND length(database) = 26 AND length(old_incarnation) = 26 "
        + "AND phase BETWEEN 0 AND 3 AND coalesce(length(new_incarnation), 26) = 26 AND coalesce(length(source_incarnation), 26) = 26 "
        + "AND coalesce(length(additional_incarnation), 26) = 26 AND length(reason) <= 256 AND coalesce(length(source_machine), 0) <= 255 "
        + "AND (source_machine IS NULL) = (source_incarnation IS NULL) AND (source_machine IS NULL) = (source_head IS NULL) "
        + "AND coalesce(source_head, 0) >= 0 AND member BETWEEN 0 AND 4294967295 AND (phase = 0 OR new_incarnation IS NOT NULL) "
        + "AND (phase < 2 OR source_machine IS NOT NULL)";

    // Serializes every change of a record with the open store's copy of it.
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    // Moves with every committed record change, so a store that read its row before it was published sees a change it missed.
    private long _generation;

    private string? _failed;

    /// <summary>Why the node failed closed, or null.</summary>
    internal string? FailedClosed => Volatile.Read(ref _failed);

    /// <summary>Fails the node closed for the rest of this process; the stop is unclean, so the next start faults intents.</summary>
    internal void FailClosed(string reason)
    {
        if (Interlocked.CompareExchange(ref _failed, reason, null) is null)
        {
            logger.ZLogCritical($"Storage failed closed: {reason:@Reason}");
        }
    }

    /// <summary>Why a write to <paramref name="store"/> is refused, or null.</summary>
    internal string? Refusal(TursoDatabaseStore store) =>
        FailedClosed is { } failed ? $"storage failed closed: {failed}"
        : store.CopyFault is { } fault ? $"this copy is faulted: {fault}"
        : !store.Settled && !store.IsSystem ? "this copy's rebuild record is not read yet"
        : store.RebuildRow is { } row ? Refusal(row, store.Incarnation)
        : null;

    // A database under rebuild accepts only the rebuild's writes; a certified rebuild accepts only its new incarnation.
    private static string? Refusal(Row row, Ulid incarnation) =>
        row.Old == incarnation ? $"this copy is faulted: {row.Reason}"
        : row.Phase < RebuildPhase.Certified ? "this database's copy is under rebuild"
        : row.New != incarnation ? "this copy is not the certified rebuild of its database"
        : null;

    public async ValueTask<bool> RefusedAsync(TenantContext database, Ulid incarnation, CancellationToken cancellationToken)
    {
        if (database == ChangeGroups.System)
        {
            // The empty incarnation asks about the node alone, before a source could read the system database's own.
            return FailedClosed is not null
                || (incarnation != Ulid.Empty && await SystemAsync(cancellationToken).ConfigureAwait(false) is var system
                    && system.Incarnation != incarnation && !system.IsHealthyPredecessor(incarnation));
        }

        if (await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not TursoDatabaseStore store)
        {
            return true;
        }

        await SettleAsync(store, cancellationToken).ConfigureAwait(false);
        // A pin a healthy rewrite replaced still names the same logical state (TS-873); faults, rebuilds and failing closed refuse.
        return store.Incarnation != incarnation && !store.IsHealthyPredecessor(incarnation) || Refusal(store) is not null;
    }

    public async ValueTask RecordAsync(TenantContext database, Ulid incarnation, string reason, CancellationToken cancellationToken)
    {
        // Once storage reports damage, caller cancellation cannot restore trust in the copy.
        cancellationToken = CancellationToken.None;
        if (database == ChangeGroups.System)
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
            system = await SystemAsync(cancellationToken).ConfigureAwait(false);
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
                // A transaction that outlived its database's deletion: no file is left to refuse, and no record may revive it.
                return;
            }

            if (current is null && engine.KnownIncarnation(key) is { } replacement && replacement != incarnation)
            {
                // A late fault from the deleted copy cannot suspend the healthy file created in its place.
                return;
            }

            if (Faulted(key, current, incarnation, Bounded(reason)) is not { } row)
            {
                return;
            }

            // Refused in memory before anything else: no writer of this process uses the copy unrefused from here on.
            if (engine.OpenStore(key) is { } store)
            {
                store.RebuildRow = row;
            }

            // Explicit fault reports may follow quiescent work: protect the fault-record crash window too.
            await WriteIntentAsync(engine.IntentStore!, key, incarnation, CancellationToken.None, Faulting).ConfigureAwait(false);
            engine.Checkpoint(Recording, database);
            await WriteAsync(system, [row], [], CancellationToken.None).ConfigureAwait(false);
            Committed(key, row);
            // Recorded: the intent is an ordinary one again, which a clean stop retires.
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

        logger.ZLogWarning($"A database copy faulted and awaits a rebuild: {reason:@Reason}");
        _ = _wake.Writer.TryWrite(true);
    }

    // The record a fault of `incarnation` leaves, or null when the record already refuses it as it must.
    private Row? Faulted(Key key, Row? current, Ulid incarnation, string reason)
    {
        var member = engine.ReplicaId.Member;
        return current switch
        {
            null => new Row(0, key, member, incarnation, RebuildPhase.Pending, null, reason, null),
            var row when row.Old == incarnation => null,
            // Pending: what is in place is a fresh file nothing wrote, or a second uncertain copy already kept in place.
            { Phase: RebuildPhase.Pending } => null,
            // Whatever is in place under rebuild is uncertified and never certifies. It may hold verified states the side copy
            // lacks, so the rebuild starts over keeping it aside as the one additional copy for the salvage.
            { Additional: null } row when row.Phase < RebuildPhase.Certified =>
                row with { Phase = RebuildPhase.Pending, New = null, Refill = null, Reason = reason, Additional = incarnation },
            // A second uncertain copy while one is kept: it stays in place, named by the pending record, and the rebuild refuses
            // from here on, deleting and overwriting nothing.
            var row when row.Phase < RebuildPhase.Certified => row with { Phase = RebuildPhase.Pending, New = incarnation, Refill = null, Reason = reason },
            // Certified: a new rebuild of this incarnation replaces the record. Side copies the finalize has not deleted yet were
            // contained only by the copy now faulted, so the new rebuild salvages them too and deletes them only once its own
            // certificate proves that: one is kept as the additional copy; two refuse the rebuild, keeping every file.
            var row when row.New == incarnation => Unfinalized(key, row) switch
            {
                [] => row with { Member = member, Old = incarnation, Phase = RebuildPhase.Pending, New = null, Reason = reason, Refill = null, Additional = null },
                [var kept] => row with { Member = member, Old = incarnation, Phase = RebuildPhase.Pending, New = null, Reason = reason, Refill = null, Additional = kept },
                _ => row with { Member = member, Old = incarnation, Phase = RebuildPhase.Pending, New = incarnation, Reason = reason, Refill = null, Additional = row.Old },
            },
            // Neither of the record's incarnations: one row cannot hold both. The certified record already refuses this copy's
            // writes and nothing is changed or deleted; the node fails closed for this process, as for any fault it cannot record.
            _ => FailClosedFor("a copy faulted beside a certified rebuild record of another incarnation"),
        };
    }

    private Row? FailClosedFor(string reason)
    {
        FailClosed(reason);
        return null;
    }

    // Neither open nor on disk: deleted (the gate orders this after a deletion's ForgetAsync, which runs once the files are gone).
    private bool Deleted(Key key) => engine.OpenStore(key) is null && !engine.FileExists(key.Tenant);

    // The side copies of a certified record still on disk (a finalize that did not finish), oldest first.
    private Ulid[] Unfinalized(Key key, Row row)
    {
        var database = key.Tenant;
        return [.. new[] { (Ulid?)row.Old, row.Additional }.OfType<Ulid>().Where(side => File.Exists(engine.SidePath(database, side)))];
    }

    /// <summary>
    /// Records that <paramref name="store"/>'s incarnation has an active storage operation. Runs before native use. NORMAL
    /// unless a record refuses the copy: an ordinary intent speaks only for a process crash, after which the unsynced commit
    /// is still read back, and after a power loss the restart proof (<see cref="PageCache"/>) passes anyway (decision 0013d).
    /// </summary>
    internal async Task RecordIntentAsync(TursoDatabaseStore store, CancellationToken cancellationToken)
    {
        _ = await SystemAsync(cancellationToken).ConfigureAwait(false);
        var key = Key.Of(store.Tenant!);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Under the gate: a deletion closes the store before it forgets the database's intents, so none outlives it.
            ObjectDisposedException.ThrowIf(store.IsClosed, store);
            // A Faulting mark of this incarnation stays: FlagAsync writes it without the gate.
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

    /// <summary>
    /// Marks <paramref name="store"/>'s intent <see cref="Faulting"/> as soon as a fault is noted in memory, so a crash before
    /// its record still faults the copy. Never throws; a mark that cannot be written fails the node closed.
    /// </summary>
    /// <remarks>
    /// Without the gate: a transaction read under it (a rebuild's barrier) waits for this mark before its error surfaces.
    /// Only <see cref="RecordAsync"/>, once the record is durable, turns a mark back into an ordinary intent.
    /// </remarks>
    internal async Task FlagAsync(TursoDatabaseStore store)
    {
        try
        {
            // A record already refuses the copy, and recovery never proves a copy its record names.
            if (store.RebuildRow is null && !store.IsClosed && engine.IntentStore is { } journal)
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
            // Keep an uncertified replacement protected across idle gaps until its rebuild is certified.
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

    /// <summary>
    /// Forgets an explicitly deleted database: its record and its write intent, with FULL, once its store is closed and its
    /// files, side copies included, are gone. The caller holds the engine's creation lock. A crash before this leaves a record
    /// with nothing to rebuild until the durable deletion runs again.
    /// </summary>
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
            foreach (var (store, table) in new[] { (system, "_tb_rebuild"), (engine.IntentStore!, "_tb_write_intent") })
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

    /// <summary>
    /// Runs once, as the system database opens, before anything else uses it, a page per FULL transaction: the intents of
    /// this replica id (a clean stop) go first; every other intent (an unclean ending) becomes a fault; then another
    /// member's records lose their refill proof and certification. A crash part way leaves intents that the next start, of
    /// another epoch, faults as well: never fewer faults.
    /// </summary>
    internal async Task RecoverAsync(TursoDatabaseStore system, CancellationToken cancellationToken)
    {
        var member = engine.ReplicaId.Member;
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
        // Milliseconds per restart proof (PageCache), one per crashed copy no fault was recorded for.
        List<double> proofs = [];
        while (true)
        {
            // Keep the page admitted through both commits. SYSTEM publication precedes journal retirement: a crash
            // between them replays the same fault, never loses an uncertain operation.
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
                        // Measured before any string is read.
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

            // Synced and dropped from the cache before the copy opens: its reads then come from its disk alone.
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

        var after = 0L;
        while (await InSystemAsync(system, command =>
        {
            var rows = ReadRows(command, "member != $member AND rowid > $after", after, parameters => parameters.AddWithValue("$member", (long)member));
            foreach (var row in rows)
            {
                // Proof recorded or certified by another member (a restored snapshot) certifies nothing here: the new copy, if
                // any, refills and salvages again.
                Upsert(command, row with
                {
                    Member = member,
                    Refill = null,
                    Phase = row.Phase is RebuildPhase.Salvaged or RebuildPhase.Certified ? RebuildPhase.Swapped : row.Phase,
                });
            }

            after = rows.Count > 0 ? rows[^1].Rowid : after;
            return rows.Count;
        }, cancellationToken, rows: Page).ConfigureAwait(false) == Page)
        {
        }

        if (faults > 0 || await InSystemAsync(system, command =>
        {
            using var any = command("SELECT 1 FROM _tb_rebuild LIMIT 1;");
            return any.ExecuteScalar() is not null;
        }, cancellationToken, write: false).ConfigureAwait(false))
        {
            logger.ZLogWarning($"Database copies on this node are faulted or under rebuild; {faults:@Count} faulted by an unclean ending");
            _ = _wake.Writer.TryWrite(true);
        }
    }

    /// <summary>
    /// Gives <paramref name="store"/> its record before it is published, and the generation it read at, for
    /// <see cref="SettleAsync"/>.
    /// </summary>
    internal async Task LoadAsync(TursoDatabaseStore store, CancellationToken cancellationToken)
    {
        store.LoadedGeneration = Volatile.Read(ref _generation);
        if (!engine.SystemDatabaseExists)
        {
            return;
        }

        var system = await SystemAsync(cancellationToken).ConfigureAwait(false);
        var key = Key.Of(store.Tenant!);
        var row = await ReadRowAsync(system, key, cancellationToken).ConfigureAwait(false);
        store.RebuildRow = row;
        if (row is { Phase: RebuildPhase.Certified } certified && certified.New == store.Incarnation
            && !await BarrierHeldAsync(store, certified, cancellationToken).ConfigureAwait(false))
        {
            // The certified copy lacks its barrier (the two files restored apart): it certifies nothing and refills and
            // salvages again, refusing ordinary writes until then.
            var swapped = certified with { Phase = RebuildPhase.Swapped, Refill = null };
            store.RebuildRow = swapped;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (await ReadRowAsync(system, key, cancellationToken).ConfigureAwait(false) == certified)
                {
                    await WriteAsync(system, [swapped], [], CancellationToken.None).ConfigureAwait(false);
                    _ = Interlocked.Increment(ref _generation);
                    logger.ZLogWarning($"A certified rebuilt copy lacks its barrier; its rebuild resumes");
                    _ = _wake.Writer.TryWrite(true);
                }
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    /// <summary>
    /// After <paramref name="store"/> was published: under the gate, so no record change is mid-way, reads its record again
    /// if one changed since <see cref="LoadAsync"/>; every later change finds the published store. Until this succeeds the
    /// store refuses ordinary writes (<see cref="Refusal(TursoDatabaseStore)"/>), and its writes run this again first, so a
    /// failure here (a busy budget, an I/O error) fails those writes alone, never the node.
    /// </summary>
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
                store.RebuildRow = await ReadRowAsync(system, Key.Of(store.Tenant!), cancellationToken).ConfigureAwait(false);
            }

            store.Settled = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<RebuildRecord?> GetAsync(TenantContext database, CancellationToken cancellationToken)
    {
        var system = await SystemAsync(cancellationToken).ConfigureAwait(false);
        return await ReadRowAsync(system, Key.Of(database), cancellationToken).ConfigureAwait(false) is { } row ? Record(database, row) : null;
    }

    public async ValueTask SaveAsync(RebuildRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Phase == RebuildPhase.Certified)
        {
            throw new InvalidOperationException("only the completion barrier certifies a rebuild");
        }

        var system = await SystemAsync(cancellationToken).ConfigureAwait(false);
        var key = Key.Of(record.Database);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await ReadRowAsync(system, key, cancellationToken).ConfigureAwait(false);
            if (Advanced(key, current, record) is not { } row)
            {
                // A stale step (a fault moved the record meanwhile) or one that skips: nothing changes, and the rebuild refuses.
                throw new ReplicaCopyFaultedException("the rebuild record does not advance from what is recorded");
            }

            if (row == current)
            {
                return;
            }

            // No phase below certified loosens a refusal, so the store's copy may lead the disk.
            if (engine.OpenStore(key) is { } store)
            {
                store.RebuildRow = row;
            }

            await WriteAsync(system, [row], [], CancellationToken.None).ConfigureAwait(false);
            Committed(key, row);
        }
        catch (Exception error) when (error is not (OperationCanceledException or InvalidOperationException or StorageBusyException))
        {
            FailClosed($"a rebuild record could not be saved: {error.GetType().Name}");
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    // The record a rebuild's step leaves, or null when it does not follow from the current one: a new rebuild records a
    // pending fault; a swap names its fresh file once; a salvage follows this member's refill of that same file; the
    // additional copy and the pending fault a fault left are never overwritten; no phase goes back.
    private Row? Advanced(Key key, Row? current, RebuildRecord record)
    {
        if (current is null)
        {
            // Never for a deleted database: a rebuild that read its record before the deletion forgot it records nothing.
            return record is { Phase: RebuildPhase.Pending, NewIncarnation: null, AdditionalIncarnation: null } && !Deleted(key)
                ? new Row(0, key, engine.ReplicaId.Member, record.OldIncarnation, RebuildPhase.Pending, null, "a rebuild was requested", null)
                : null;
        }

        if (current.Old != record.OldIncarnation || current.Additional != record.AdditionalIncarnation)
        {
            return null;
        }

        return (current.Phase, record.Phase) switch
        {
            var (from, to) when from == to && current.New == record.NewIncarnation => current,
            (RebuildPhase.Pending, RebuildPhase.Swapped) when current.New is null && record.NewIncarnation is not null =>
                current with { Phase = RebuildPhase.Swapped, New = record.NewIncarnation, Refill = null },
            (RebuildPhase.Swapped, RebuildPhase.Salvaged) when current.New == record.NewIncarnation && current.Refill is not null
                && current.Member == engine.ReplicaId.Member => current with { Phase = RebuildPhase.Salvaged },
            _ => null,
        };
    }

    public async ValueTask RetireAsync(TenantContext database, Ulid oldIncarnation, CancellationToken cancellationToken)
    {
        var system = await SystemAsync(cancellationToken).ConfigureAwait(false);
        var key = Key.Of(database);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await ReadRowAsync(system, key, cancellationToken).ConfigureAwait(false) is not { } row || row.Old != oldIncarnation)
            {
                return;
            }

            if (row.Phase != RebuildPhase.Certified)
            {
                throw new InvalidOperationException("an uncertified rebuild record cannot be retired");
            }

            await WriteAsync(system, [], [key], cancellationToken).ConfigureAwait(false);
            // After the disk: retiring loosens the refusal of the old incarnation.
            Committed(key, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async IAsyncEnumerable<RebuildRecord> PendingAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var system = await SystemAsync(cancellationToken).ConfigureAwait(false);
        var after = 0L;
        while (true)
        {
            // Held while the page is read and while its records are handed out, across every rebuild they run.
            using var lease = Reserve(Page);
            var page = await InSystemAsync(system, command => ReadRows(command, "rowid > $after", after), cancellationToken, write: false).ConfigureAwait(false);
            foreach (var row in page)
            {
                yield return Record(row.Key.Tenant, row);
            }

            if (page.Count < Page)
            {
                yield break;
            }

            after = page[^1].Rowid;
        }
    }

    public async Task WaitAsync(CancellationToken cancellationToken) => _ = await _wake.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

    public async ValueTask<int?> RefillRebuildAsync(
        TenantContext database, Ulid newIncarnation, IReadOnlyList<int> buckets, BucketCursor? after,
        IReadOnlyList<RepairState> states, Hlc collectedBefore, CancellationToken cancellationToken)
    {
        TursoStorageTransaction.ValidateRefillPage(database, buckets, after, states);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(collectedBefore.Value, (ulong)long.MaxValue, nameof(collectedBefore));
        if (database == ChangeGroups.System
            || await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not TursoDatabaseStore store
            || !UnderRebuild(store, newIncarnation) || store.RebuildRow?.Phase != RebuildPhase.Swapped)
        {
            return null;
        }

        var system = await SystemAsync(cancellationToken).ConfigureAwait(false);
        var key = Key.Of(database);
        var changed = 0;
        var cursor = after;
        if (collectedBefore.Value > 0)
        {
            while (true)
            {
                // Each local gap page releases its decoded graphs at disposal before the next FULL writer.
                await using var transaction = await store.BeginRebuildTransactionAsync(cancellationToken).ConfigureAwait(false);
                if (!await RefillHeldAsync(system, key, store, transaction, newIncarnation, collectedBefore, cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }

                if (await transaction.ReconcileRefillAsync(buckets, cursor, states, collectedBefore, cancellationToken).ConfigureAwait(false) is not { } reconciled)
                {
                    return null;
                }

                var (forgotten, next) = reconciled;
                _ = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                changed += forgotten;
                if (next is null)
                {
                    break;
                }

                cursor = next;
            }
        }

        await using var joining = await store.BeginRebuildTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (!await RefillHeldAsync(system, key, store, joining, newIncarnation, collectedBefore, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        foreach (var state in states)
        {
            changed += await joining.JoinBaselineAsync(state.Collection, state.Id, state.State,
                new EntryVersion(state.State.MaxStamp, default), cancellationToken).ConfigureAwait(false) ? 1 : 0;
        }

        _ = await joining.CommitAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }

    // Called under each receiver writer, before reconciliation or a baseline join; no source proof survives a partial pass.
    private async ValueTask<bool> RefillHeldAsync(
        TursoDatabaseStore system, Key key, TursoDatabaseStore store, TursoStorageTransaction transaction,
        Ulid incarnation, Hlc collectedBefore, CancellationToken cancellationToken)
    {
        // Read before taking the gate: corrupt storage must be able to record its fault through the ledger.
        if (!UnderRebuild(store, incarnation) || !CurrentCopy(key, store, incarnation)
            || !await InPlaceAsync(transaction, incarnation, cancellationToken).ConfigureAwait(false)
            || (await transaction.GetCollectedBeforeAsync(cancellationToken).ConfigureAwait(false)).Value > collectedBefore.Value)
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await ReadRowAsync(system, key, cancellationToken).ConfigureAwait(false) is not { Phase: RebuildPhase.Swapped } row
                || row.New != incarnation || row.Member != engine.ReplicaId.Member
                || !UnderRebuild(store, incarnation) || !CurrentCopy(key, store, incarnation))
            {
                return false;
            }

            if (row.Refill is not null)
            {
                var cleared = row with { Refill = null };
                await WriteAsync(system, [cleared], [], cancellationToken).ConfigureAwait(false);
                Committed(key, cleared);
            }

            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<int?> RepairRebuildAsync(TenantContext database, Ulid newIncarnation, IReadOnlyList<RepairState> states, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(states);
        if (await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not TursoDatabaseStore store
            || !UnderRebuild(store, newIncarnation))
        {
            return null;
        }

        var changed = 0;
        foreach (var chunk in states.Where(state => state.State.MaxStamp.Value != 0).Chunk(ChangeGroups.MaxDeltasOf(database)))
        {
            await using var transaction = await store.BeginRebuildTransactionAsync(cancellationToken).ConfigureAwait(false);
            // Under the writer: a fault or a new record since the check above refuses this repair.
            if (!UnderRebuild(store, newIncarnation) || !await InPlaceAsync(transaction, newIncarnation, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            foreach (var state in chunk)
            {
                // Keep the side copies until every live entry survives without relying on an inferred deletion.
                if (!await transaction.CanSalvageAsync(state.Collection, state.Id, state.State, cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }

                // Origin 0: a repair (section 9), so the tailer never ships it and it moves no origin's applied stamp.
                changed += await transaction.JoinAsync(state.Collection, state.Id, state.State, new EntryVersion(state.State.MaxStamp, default), cancellationToken)
                    .ConfigureAwait(false) ? 1 : 0;
            }

            _ = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return changed;
    }

    public async ValueTask<bool> RecordRefillAsync(
        TenantContext database, Ulid newIncarnation, string sourceMachine, Ulid sourceIncarnation, long head, Hlc collectedBefore, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceMachine);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sourceMachine.Length, MaxMachine, nameof(sourceMachine));
        ArgumentOutOfRangeException.ThrowIfNegative(head);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(collectedBefore.Value, (ulong)long.MaxValue, nameof(collectedBefore));
        var system = await SystemAsync(cancellationToken).ConfigureAwait(false);
        if (await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not TursoDatabaseStore store
            || !UnderRebuild(store, newIncarnation))
        {
            return false;
        }

        var key = Key.Of(database);
        if (await ReadRowAsync(system, key, cancellationToken).ConfigureAwait(false) is not { Phase: RebuildPhase.Swapped } row
            || row.New != newIncarnation || row.Member != engine.ReplicaId.Member)
        {
            return false;
        }

        // Baseline coverage is complete; inherit its absence frontier durably before proof publication or salvage.
        await using (var transaction = await store.BeginRebuildTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!UnderRebuild(store, newIncarnation) || !await InPlaceAsync(transaction, newIncarnation, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            await transaction.InheritCollectedBeforeAsync(collectedBefore, cancellationToken).ConfigureAwait(false);
            _ = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        var proven = row with { Refill = new Refill(sourceMachine, sourceIncarnation, head) };
        return await PublishRebuildAsync(system, key, store, row, proven, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> CompleteRebuildAsync(TenantContext database, Ulid oldIncarnation, Ulid newIncarnation, CancellationToken cancellationToken)
    {
        var system = await SystemAsync(cancellationToken).ConfigureAwait(false);
        var key = Key.Of(database);
        if (await ReadRowAsync(system, key, cancellationToken).ConfigureAwait(false) is not { } row
            || row.Old != oldIncarnation || row.New != newIncarnation || row.Member != engine.ReplicaId.Member)
        {
            return false;
        }

        if (row.Phase == RebuildPhase.Certified)
        {
            if (await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not TursoDatabaseStore held
                || !CurrentCopy(key, held, newIncarnation))
            {
                return false;
            }

            // Take the writer outside the gate; even an idempotent success must outlive the barrier read's disposal.
            return await held.WithWritersHeldAsync(async () =>
            {
                bool matches;
                await using (var read = (TursoStorageTransaction)await held.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false))
                {
                    await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        matches = await ReadRowAsync(system, key, cancellationToken).ConfigureAwait(false) == row
                            && await BarrierMatchesAsync(read, row, cancellationToken).ConfigureAwait(false)
                            && CurrentCopy(key, held, newIncarnation);
                    }
                    finally
                    {
                        _gate.Release();
                    }
                }

                engine.Checkpoint(BarrierReadDisposed, database);
                if (!matches)
                {
                    return false;
                }

                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (await ReadRowAsync(system, key, cancellationToken).ConfigureAwait(false) != row
                        || !CurrentCopy(key, held, newIncarnation))
                    {
                        return false;
                    }

                    // Also resumes a valid durable certificate whose first caller stopped before cache publication.
                    Committed(key, row);
                    return CurrentCopy(key, held, newIncarnation);
                }
                finally
                {
                    _gate.Release();
                }
            }, cancellationToken, "an idempotent rebuild completion").ConfigureAwait(false);
        }

        if (row.Phase != RebuildPhase.Salvaged || row.Refill is not { } refill
            || await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not TursoDatabaseStore store
            || !UnderRebuild(store, newIncarnation) || !CurrentCopy(key, store, newIncarnation))
        {
            return false;
        }

        // A real FULL tenant write, fully disposed before certification. Disposal may itself record a fault and take the gate.
        await using (var transaction = await store.BeginRebuildTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!UnderRebuild(store, newIncarnation) || !CurrentCopy(key, store, newIncarnation)
                || !await InPlaceAsync(transaction, newIncarnation, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            _ = await transaction.LedgerAsync(command =>
            {
                using var upsert = command("""
                    INSERT INTO _tb_refill (origin, incarnation, head) VALUES ($origin, $incarnation, $head)
                    ON CONFLICT (origin) DO UPDATE SET incarnation = excluded.incarnation, head = excluded.head;
                    """);
                upsert.Parameters.AddWithValue("$origin", refill.Machine);
                upsert.Parameters.AddWithValue("$incarnation", refill.Incarnation.ToString());
                upsert.Parameters.AddWithValue("$head", refill.Head);
                return upsert.ExecuteNonQuery();
            }, cancellationToken).ConfigureAwait(false);
            _ = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        engine.Checkpoint(BarrierDisposed, database);
        var certified = row with { Phase = RebuildPhase.Certified };
        return await PublishRebuildAsync(system, key, store, row, certified, cancellationToken).ConfigureAwait(false);
    }

    // The receiver writer precedes the gate. Its read is opened before, and disposed after, the gate, so a damaged read
    // can record its fault. The cache remains refusing until that disposal and a final unchanged-row/marker check.
    private async Task<bool> PublishRebuildAsync(
        TursoDatabaseStore system, Key key, TursoDatabaseStore store, Row expected, Row published, CancellationToken cancellationToken)
    {
        if (expected.New is not { } rebuilt || !UnderRebuild(store, rebuilt) || !CurrentCopy(key, store, rebuilt))
        {
            return false;
        }

        return await store.WithWritersHeldAsync(async () =>
        {
            await using (var read = (TursoStorageTransaction)await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false))
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                var written = false;
                try
                {
                    var current = await ReadRowAsync(system, key, cancellationToken).ConfigureAwait(false);
                    engine.Checkpoint(Publishing, key.Tenant);
                    if (current != expected || !await InPlaceAsync(read, rebuilt, cancellationToken).ConfigureAwait(false)
                        || !UnderRebuild(store, rebuilt) || !CurrentCopy(key, store, rebuilt))
                    {
                        return false;
                    }

                    await WriteAsync(system, [published], [], cancellationToken).ConfigureAwait(false);
                    written = true;
                    engine.Checkpoint(Published, key.Tenant);
                    if (!await InPlaceAsync(read, rebuilt, cancellationToken).ConfigureAwait(false)
                        || !UnderRebuild(store, rebuilt) || !CurrentCopy(key, store, rebuilt))
                    {
                        written = false;
                        await WithdrawPublicationAsync(system, key, store, expected).ConfigureAwait(false);
                        return false;
                    }
                }
                catch
                {
                    if (written)
                    {
                        await WithdrawPublicationAsync(system, key, store, expected).ConfigureAwait(false);
                    }

                    throw;
                }
                finally
                {
                    _gate.Release();
                }
            }

            // A durable publication must be checked even when its caller cancelled while the read disposed.
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (await ReadRowAsync(system, key, CancellationToken.None).ConfigureAwait(false) != published)
                {
                    return false;
                }

                if (!UnderRebuild(store, rebuilt) || !CurrentCopy(key, store, rebuilt))
                {
                    await WithdrawPublicationAsync(system, key, store, expected).ConfigureAwait(false);
                    return false;
                }

                // Only now may a certificate loosen the cached refusal; the read and its fault recording are finished.
                Committed(key, published);
                return CurrentCopy(key, store, rebuilt);
            }
            finally
            {
                _gate.Release();
            }
        }, cancellationToken, "a rebuild proof publication").ConfigureAwait(false);
    }

    // Called only while the gate still owns this publication. A fault leads the disk, so restore its pending refusal
    // rather than leave the newly written proof/certificate available to a later load. SYSTEM failure already blocks all writes.
    private async Task WithdrawPublicationAsync(TursoDatabaseStore system, Key key, TursoDatabaseStore store, Row expected)
    {
        if (FailedClosed is not null)
        {
            return;
        }

        try
        {
            var refused = store.CopyFault is { } fault && expected.New is { } rebuilt
                ? Faulted(key, expected, rebuilt, Bounded(fault)) ?? expected : expected;
            await WriteAsync(system, [refused], [], CancellationToken.None).ConfigureAwait(false);
            Committed(key, refused);
        }
        catch (Exception error)
        {
            FailClosed($"a rebuild publication could not be withdrawn: {error.GetType().Name}");
            throw;
        }
    }

    private bool CurrentCopy(Key key, TursoDatabaseStore store, Ulid incarnation) =>
        FailedClosed is null && store.CopyFault is null && !store.Closed && store.Settled && store.Incarnation == incarnation
        && ReferenceEquals(engine.OpenStore(key), store) && engine.FileExists(key.Tenant);

    /// <summary>
    /// Whether the side copy of <paramref name="database"/> may be deleted: its record is certified, by this member, for
    /// exactly <paramref name="rebuilt"/> with this member's refill proof, and <paramref name="store"/>, that incarnation,
    /// holds the barrier's certificate row of that proof. Each of the two files could be restored apart from the other.
    /// </summary>
    internal async Task<bool> FinalizeCertifiedAsync(
        TenantContext database, TursoDatabaseStore store, Ulid rebuilt, Func<Task<bool>> finalize, CancellationToken cancellationToken)
    {
        var system = await SystemAsync(cancellationToken).ConfigureAwait(false);
        var key = Key.Of(database);
        if (FailedClosed is not null || store.CopyFault is not null || store.Incarnation != rebuilt
            || await ReadRowAsync(system, key, cancellationToken).ConfigureAwait(false) is not { Phase: RebuildPhase.Certified, Refill: not null } row
            || row.New != rebuilt || row.Member != engine.ReplicaId.Member
            || !await BarrierHeldAsync(store, row, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        // The caller holds the receiver writer. Read its barrier before taking the gate: a damaged read must be able to
        // record its fault. The unchanged ledger row then stays held through deletion.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return FailedClosed is null && store.CopyFault is null && await ReadRowAsync(system, key, cancellationToken).ConfigureAwait(false) == row
                && await finalize().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // In one read of the rebuilt file: it is the row's new incarnation and its certificate row is the row's refill proof.
    private async ValueTask<bool> BarrierHeldAsync(TursoDatabaseStore store, Row row, CancellationToken cancellationToken)
    {
        if (FailedClosed is not null || store.CopyFault is not null || row.New is not { } rebuilt || store.Incarnation != rebuilt)
        {
            return false;
        }

        bool matches;
        await using (var read = (TursoStorageTransaction)await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false))
        {
            matches = await BarrierMatchesAsync(read, row, cancellationToken).ConfigureAwait(false);
        }

        return matches && FailedClosed is null && store.CopyFault is null && store.Incarnation == rebuilt;
    }

    private static async ValueTask<bool> BarrierMatchesAsync(TursoStorageTransaction read, Row row, CancellationToken cancellationToken)
    {
        if (row is not { New: { } rebuilt, Refill: { } refill }
            || await read.GetIncarnationAsync(cancellationToken).ConfigureAwait(false) != rebuilt)
        {
            return false;
        }

        return await read.LedgerAsync(command =>
        {
            using var certificate = command("SELECT incarnation, head FROM _tb_refill WHERE origin = $origin;");
            certificate.Parameters.AddWithValue("$origin", refill.Machine);
            using var reader = certificate.ExecuteReader();
            return reader.Read() && reader.GetString(0) == refill.Incarnation.ToString() && reader.GetInt64(1) == refill.Head;
        }, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _gate.Dispose();

    // The store's copy of its record says it is under rebuild into exactly `incarnation`, not faulted, and the node serves.
    private bool UnderRebuild(TursoDatabaseStore store, Ulid incarnation) =>
        FailedClosed is null && store.CopyFault is null && store.Settled && store.Incarnation == incarnation
        && store.RebuildRow is { Phase: RebuildPhase.Swapped or RebuildPhase.Salvaged } row && row.New == incarnation;

    // After a record change committed: the open store follows it, and a store not yet published reads it again.
    private void Committed(Key key, Row? row)
    {
        _ = Interlocked.Increment(ref _generation);
        if (engine.OpenStore(key) is { } store)
        {
            store.RebuildRow = row;
        }

        if (row is { Phase: RebuildPhase.Certified })
        {
            engine.NotifyReplicationChanged(key.Tenant);
        }
    }

    // Inside the transaction, under its writer: the file is exactly that incarnation.
    private static async ValueTask<bool> InPlaceAsync(TursoStorageTransaction transaction, Ulid incarnation, CancellationToken cancellationToken) =>
        await transaction.GetIncarnationAsync(cancellationToken).ConfigureAwait(false) == incarnation;

    private async ValueTask<TursoDatabaseStore> SystemAsync(CancellationToken cancellationToken) =>
        engine.SystemStore ?? (TursoDatabaseStore)await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);

    private async ValueTask<Row?> ReadRowAsync(TursoDatabaseStore system, Key key, CancellationToken cancellationToken) =>
        await InSystemAsync(system, command => ReadRow(command, key), cancellationToken, write: false).ConfigureAwait(false);

    // A page's memory, from the process alone: host recovery is no tenant's work. Refused, never awaited.
    private IMemoryReservation? Reserve(int rows) =>
        memory is null ? null : memory.TryReserve(MemoryLane.Bulk, rows * RowBytes, tenant: null) ?? throw new StorageBusyException();

    // A reason as long as a row holds.
    private static string Bounded(string reason) => reason.Length <= MaxReason ? reason : reason[..MaxReason];

    // One system transaction, FULL and uncaptured when it writes, running `call` and committing. Every call reads at most a
    // page of rows or intents: that much memory is reserved first.
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
            // A row the ledger never writes: its records cannot be trusted, nor any copy's state without them.
            FailClosed($"the rebuild ledger is damaged: {error.GetType().Name}");
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

        await using var transaction = await system.BeginRebuildTransactionAsync(cancellationToken, full).ConfigureAwait(false);
        // SYSTEM writes only _tb_rebuild; the rewrite refreshes it under the writer. The intent journal is never rewritten.
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
                using var delete = command("DELETE FROM _tb_rebuild WHERE organization = $organization AND project = $project AND database = $database;");
                key.Bind(delete);
                delete.ExecuteNonQuery();
            }

            return true;
        }, cancellationToken);

    private static void Upsert(Func<string, SqliteCommand> command, Row row)
    {
        using var upsert = command("""
            INSERT INTO _tb_rebuild (
                organization, project, database, member, old_incarnation, phase, new_incarnation, reason, source_machine, source_incarnation, source_head,
                additional_incarnation)
            VALUES ($organization, $project, $database, $member, $old, $phase, $new, $reason, $machine, $source, $head, $additional)
            ON CONFLICT (organization, project, database) DO UPDATE SET member = excluded.member, old_incarnation = excluded.old_incarnation,
                phase = excluded.phase, new_incarnation = excluded.new_incarnation, reason = excluded.reason,
                source_machine = excluded.source_machine, source_incarnation = excluded.source_incarnation, source_head = excluded.source_head,
                additional_incarnation = excluded.additional_incarnation;
            """);
        row.Key.Bind(upsert);
        upsert.Parameters.AddWithValue("$member", (long)row.Member);
        upsert.Parameters.AddWithValue("$old", row.Old.ToString());
        upsert.Parameters.AddWithValue("$phase", (long)row.Phase);
        upsert.Parameters.AddWithValue("$new", (object?)row.New?.ToString() ?? DBNull.Value);
        upsert.Parameters.AddWithValue("$reason", row.Reason);
        upsert.Parameters.AddWithValue("$machine", (object?)row.Refill?.Machine ?? DBNull.Value);
        upsert.Parameters.AddWithValue("$source", (object?)row.Refill?.Incarnation.ToString() ?? DBNull.Value);
        upsert.Parameters.AddWithValue("$head", (object?)row.Refill?.Head ?? DBNull.Value);
        upsert.Parameters.AddWithValue("$additional", (object?)row.Additional?.ToString() ?? DBNull.Value);
        upsert.ExecuteNonQuery();
    }

    private static Row? ReadRow(Func<string, SqliteCommand> command, Key key)
    {
        using var select = command($"SELECT {Columns} FROM _tb_rebuild WHERE organization = $organization AND project = $project AND database = $database;");
        key.Bind(select);
        using var reader = select.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    // A page of rows matching `where` after rowid `after`, in rowid order.
    private static List<Row> ReadRows(Func<string, SqliteCommand> command, string where, long after, Action<SqliteParameterCollection>? bind = null)
    {
        using var select = command($"SELECT {Columns} FROM _tb_rebuild WHERE {where} ORDER BY rowid LIMIT $limit;");
        select.Parameters.AddWithValue("$after", after);
        select.Parameters.AddWithValue("$limit", (long)Page);
        bind?.Invoke(select.Parameters);
        using var reader = select.ExecuteReader();
        List<Row> rows = [];
        while (reader.Read())
        {
            rows.Add(Read(reader));
        }

        return rows;
    }

    private static Row Read(SqliteDataReader reader)
    {
        // Measured before any string is read.
        if (reader.GetInt64(13) == 0)
        {
            throw new InvalidDataException("a rebuild record is not one the ledger writes");
        }

        var key = new Key(reader.GetString(1), reader.GetString(2), reader.GetString(3));
        var refill = reader.IsDBNull(9) ? null : new Refill(reader.GetString(9), Stored(reader.GetString(10)), reader.GetInt64(11));
        return new Row(
            reader.GetInt64(0), key, unchecked((uint)reader.GetInt64(4)), Stored(reader.GetString(5)), (RebuildPhase)reader.GetInt64(6),
            reader.IsDBNull(7) ? null : Stored(reader.GetString(7)), reader.GetString(8), refill)
        {
            Additional = reader.IsDBNull(12) ? null : Stored(reader.GetString(12)),
        };
    }

    // A stored incarnation, which the ledger only ever writes canonical.
    private static Ulid Stored(string text) =>
        Ulid.TryParse(text, out var incarnation) && incarnation.ToString() == text ? incarnation : throw new InvalidDataException("a stored incarnation is not a ULID");

    private RebuildRecord Record(TenantContext database, Row row) =>
        new(database, row.Old, engine.SidePath(database, row.Old), row.Phase, row.New, row.Additional);

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

    /// <summary>
    /// A certified refill of a rebuild's new incarnation: the listed member it came from (its membership name), that source's
    /// incarnation and its head change id.
    /// </summary>
    internal sealed record Refill(string Machine, Ulid Incarnation, long Head);

    /// <summary>A database's rebuild record; <see cref="Rowid"/> only orders pages, and is 0 for one not read from disk.</summary>
    internal sealed record Row(long Rowid, Key Key, uint Member, Ulid Old, RebuildPhase Phase, Ulid? New, string Reason, Refill? Refill)
    {
        /// <summary>
        /// An uncertified copy that faulted under rebuild, kept unwritten at its own side path for the salvage beside the
        /// faulted original: at most one.
        /// </summary>
        public Ulid? Additional { get; init; }

        // Rowid is where a row sits, not what it says.
        public bool Equals(Row? other) =>
            other is not null && Key == other.Key && Member == other.Member && Old == other.Old && Phase == other.Phase && New == other.New
            && Reason == other.Reason && Refill == other.Refill && Additional == other.Additional;

        public override int GetHashCode() => HashCode.Combine(Key, Member, Old, Phase, New, Reason, Refill, Additional);
    }
}
