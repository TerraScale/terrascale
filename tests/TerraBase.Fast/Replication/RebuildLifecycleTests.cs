using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TerraBase.Engine;
using TerraBase.Engine.Sqlite;
using TerraBase.Features.Replication;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;
using Turso.Data.Sqlite;

namespace TerraBase.Fast.Replication;

// Database lifecycle beside the rebuild ledger: an explicit deletion forgets a faulted copy entirely, a faulted copy is never
// copied into a new database, a fresh file never opens beside another file's log, and a certified copy that faults again
// before its side copies are finalized keeps them bounded (one salvaged again, two refuse) or fails closed for a third.
public sealed class RebuildLifecycleTests
{
    [Test]
    public async Task Deleting_a_faulted_database_removes_its_record_and_side_copies_and_no_rebuild_recreates_it()
    {
        await using var api = await StartAsync();
        var services = api.Server.Services;
        var (database, store) = await FaultedAsync(api);
        var storage = services.GetRequiredService<IRebuildStorage>();
        var ledger = services.GetRequiredService<IRebuildLedger>();
        var side = storage.SidePath(database, store.Incarnation);
        foreach (var file in (string[])[side, side + "-wal", side + ".salvage"])
        {
            await File.WriteAllBytesAsync(file, [1, 2, 3]);
        }

        var stale = await ledger.GetAsync(database, CancellationToken.None) ?? throw new InvalidOperationException("the fault was not recorded");

        _ = await services.GetRequiredService<IStorageEngine>().DeleteDatabaseAsync(database, CancellationToken.None);

        await Assert.That(await ledger.GetAsync(database, CancellationToken.None)).IsNull();
        await Assert.That(File.Exists(side) || File.Exists(side + "-wal") || File.Exists(side + ".salvage")).IsFalse();
        // A rebuild that read the record before the deletion records and publishes nothing for the deleted database.
        _ = await Assert.ThrowsAsync<ReplicaCopyFaultedException>(async () => await ledger.SaveAsync(stale, CancellationToken.None));
        await Assert.That(await storage.PublishFreshAsync(database, side, CancellationToken.None)).IsNull();
        await services.GetRequiredService<IReplicaCopyFaults>().RecordAsync(database, store.Incarnation, "late", CancellationToken.None);
        await Assert.That(await ledger.GetAsync(database, CancellationToken.None)).IsNull();
        await Assert.That(await services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, CancellationToken.None)).IsNull();

        var engine = services.GetRequiredService<IStorageEngine>();
        await Assert.That(await engine.CreateDatabaseAsync(database, CancellationToken.None)).IsTrue();
        var replacement = await engine.OpenDatabaseAsync(database, CancellationToken.None)
            ?? throw new InvalidOperationException("the replacement was not created");
        await Assert.That(replacement.Incarnation).IsNotEqualTo(store.Incarnation);
        await services.GetRequiredService<IReplicaCopyFaults>().RecordAsync(database, store.Incarnation, "late after recreation", CancellationToken.None);
        await Assert.That(await ledger.GetAsync(database, CancellationToken.None)).IsNull();
        await Assert.That(await RefusedWriteAsync(api, database)).IsFalse().Because("a deleted copy's late fault cannot suspend its replacement");
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task A_deleted_copy_fault_at_file_publication_cannot_suspend_its_replacement(bool copy, bool disposal)
    {
        Action<string, TenantContext>? checkpoint = null;
        await using var api = await StartAsync(configure: services =>
            services.AddSingleton<DatabaseLifecycleCheckpoint>((step, database) => checkpoint?.Invoke(step, database)));
        var services = api.Server.Services;
        var (database, original) = await FaultedAsync(api);
        var engine = services.GetRequiredService<SqliteStorageEngine>();
        var ledger = services.GetRequiredService<IRebuildLedger>();
        var faults = services.GetRequiredService<IReplicaCopyFaults>();
        var key = RebuildLedger.Key.Of(database);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        SqliteStorageTransaction? oldRead = disposal
            ? (SqliteStorageTransaction)await original.BeginTransactionAsync(write: false, deadline.Token)
            : null;
        Task? lateFault = null;
        var injectReset = 0;
        var publishedWithoutStore = false;
        Ulid? publishing = null;
        RebuildRecord? recordAtPublication = null;
        try
        {
            await Assert.That(await engine.DeleteDatabaseAsync(database, deadline.Token)).IsTrue();
            await Assert.That(await ledger.GetAsync(database, deadline.Token)).IsNull();
            var source = database with { DatabaseId = EntityIds.New().ToString() };
            if (copy)
            {
                await Assert.That(await engine.CreateDatabaseAsync(source, deadline.Token)).IsTrue();
            }

            checkpoint = (step, marked) =>
            {
                if (marked != database)
                {
                    return;
                }

                if (step == "reader.reset" && Interlocked.Exchange(ref injectReset, 0) == 1)
                {
                    throw new SqliteException("a late fault of the deleted reader", 11);
                }

                if (step == "create.published")
                {
                    publishedWithoutStore = engine.CreationHeld && engine.OpenStore(key) is null
                        && File.Exists(original.FilePath);
                    publishing = engine.KnownIncarnation(key);
                    Interlocked.Exchange(ref injectReset, disposal ? 1 : 0);
                    // Keep creation paused until the old copy's fault finishes: fault disposal must not take _creation.
                    lateFault = RecordLateFaultAsync();
                    lateFault.WaitAsync(deadline.Token).GetAwaiter().GetResult();
                    recordAtPublication = ledger.GetAsync(database, deadline.Token).AsTask().GetAwaiter().GetResult();
                }
            };

            await Assert.That(copy
                ? await engine.CopyDatabaseAsync(source, database, deadline.Token)
                : await engine.CreateDatabaseAsync(database, deadline.Token)).IsTrue();
            checkpoint = null;

            await Assert.That(lateFault).IsNotNull();
            await Assert.That(publishedWithoutStore).IsTrue().Because("the old fault must finish in the file-to-store publication gap");
            var replacement = await engine.OpenDatabaseAsync(database, deadline.Token)
                ?? throw new InvalidOperationException("the replacement was not created");
            await Assert.That(publishing).IsEqualTo(replacement.Incarnation);
            await Assert.That(replacement.Incarnation).IsNotEqualTo(original.Incarnation);
            await Assert.That(recordAtPublication).IsNull();
            await Assert.That(await ledger.GetAsync(database, deadline.Token)).IsNull();
            await Assert.That(await RefusedWriteAsync(api, database)).IsFalse();
            await Assert.That(await engine.IsReadyAsync(deadline.Token)).IsTrue();

            // The bridge rejects only the deleted incarnation: a real fault of the current file still refuses it.
            await faults.RecordAsync(database, replacement.Incarnation, "a fault of the replacement", deadline.Token);
            await Assert.That((await ledger.GetAsync(database, deadline.Token))?.OldIncarnation).IsEqualTo(replacement.Incarnation);
            await Assert.That(await RefusedWriteAsync(api, database)).IsTrue();
        }
        finally
        {
            checkpoint = null;
            if (lateFault is not null)
            {
                await lateFault;
            }
            else if (oldRead is not null)
            {
                await oldRead.DisposeAsync();
            }
        }

        async Task RecordLateFaultAsync()
        {
            if (oldRead is not null)
            {
                var error = await Assert.ThrowsAsync<SqliteException>(async () => await oldRead.DisposeAsync());
                await Assert.That(error!.SqliteErrorCode).IsEqualTo(11);
            }
            else
            {
                await faults.RecordAsync(database, original.Incarnation, "a late wrapped-source fault", deadline.Token);
            }
        }
    }

    [Test]
    public async Task A_fault_of_the_fresh_file_before_store_publication_is_recorded_and_refused()
    {
        Action<string, TenantContext>? checkpoint = null;
        await using var api = await StartAsync(configure: services =>
            services.AddSingleton<DatabaseLifecycleCheckpoint>((step, database) => checkpoint?.Invoke(step, database)));
        var services = api.Server.Services;
        var (database, original) = await FaultedAsync(api);
        var engine = services.GetRequiredService<SqliteStorageEngine>();
        var ledger = services.GetRequiredService<IRebuildLedger>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await Assert.That(await engine.DeleteDatabaseAsync(database, deadline.Token)).IsTrue();
        var publishedWithoutStore = false;
        Ulid? publishing = null;
        checkpoint = (step, marked) =>
        {
            if (step == "create.published" && marked == database)
            {
                var key = RebuildLedger.Key.Of(database);
                publishedWithoutStore = engine.OpenStore(key) is null && File.Exists(original.FilePath);
                publishing = engine.KnownIncarnation(key) ?? throw new InvalidOperationException("the fresh file identity was not registered");
                services.GetRequiredService<IReplicaCopyFaults>().RecordAsync(database, publishing.Value, "the fresh file faulted", deadline.Token)
                    .AsTask().WaitAsync(deadline.Token).GetAwaiter().GetResult();
            }
        };
        try
        {
            await Assert.That(await engine.CreateDatabaseAsync(database, deadline.Token)).IsTrue();
        }
        finally
        {
            checkpoint = null;
        }

        await Assert.That(publishedWithoutStore).IsTrue();
        var replacement = await engine.OpenDatabaseAsync(database, deadline.Token)
            ?? throw new InvalidOperationException("the replacement was not created");
        await Assert.That(publishing).IsEqualTo(replacement.Incarnation);
        var pending = await ledger.GetAsync(database, deadline.Token);
        await Assert.That(pending?.Phase).IsEqualTo(RebuildPhase.Pending);
        await Assert.That(pending?.OldIncarnation).IsEqualTo(replacement.Incarnation);
        await Assert.That(await RefusedWriteAsync(api, database)).IsTrue();
    }

    [Test]
    public async Task A_faulted_database_is_never_copied_into_a_new_one()
    {
        await using var api = await StartAsync();
        var (database, _) = await FaultedAsync(api);
        var target = new TenantContext(database.OrganizationId, PlanTier.Hobby, database.ProjectId, EntityIds.New().ToString());
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();

        _ = await Assert.ThrowsAsync<ReplicaCopyFaultedException>(async () => await engine.CopyDatabaseAsync(database, target, CancellationToken.None));

        await Assert.That(await engine.OpenDatabaseAsync(target, CancellationToken.None)).IsNull();
    }

    [Test]
    public async Task A_fresh_file_is_never_created_beside_an_orphaned_log()
    {
        await using var api = await StartAsync();
        var (database, _) = await FaultedAsync(api);
        var orphan = new TenantContext(database.OrganizationId, PlanTier.Hobby, database.ProjectId, EntityIds.New().ToString());
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        var path = Path.Combine(
            Path.GetDirectoryName(api.Server.Services.GetRequiredService<IRebuildStorage>().SidePath(orphan, Ulid.NewUlid()))!, orphan.DatabaseId + ".db");
        await File.WriteAllBytesAsync(path + "-wal", [1, 2, 3]);

        _ = await Assert.ThrowsAsync<IOException>(async () => await engine.CreateDatabaseAsync(orphan, CancellationToken.None));

        await Assert.That(File.Exists(path)).IsFalse();
        await Assert.That(await File.ReadAllBytesAsync(path + "-wal")).IsEquivalentTo(new byte[] { 1, 2, 3 });
    }

    [Test]
    public async Task A_certified_copy_that_faults_beside_one_retained_side_salvages_it_again_and_certifies()
    {
        await using var api = await StartAsync();
        var services = api.Server.Services;
        var storage = services.GetRequiredService<IRebuildStorage>();
        var ledger = services.GetRequiredService<IRebuildLedger>();
        var repair = services.GetRequiredService<IRebuildRepair>();
        var (database, original, rebuilt, _) = await CertifiedAsync(api, retainAdditional: false);
        var originalSide = storage.SidePath(database, original);
        await Assert.That(storage.SideExists(originalSide)).IsTrue();

        await services.GetRequiredService<IReplicaCopyFaults>().RecordAsync(database, rebuilt, "a certified copy faulted", CancellationToken.None);

        // The unfinalized side copy was contained only by the copy now faulted: it is the one additional copy of the new rebuild.
        var pending = await ledger.GetAsync(database, CancellationToken.None) ?? throw new InvalidOperationException("the fault was not recorded");
        await Assert.That(pending.Phase).IsEqualTo(RebuildPhase.Pending);
        await Assert.That(pending.OldIncarnation).IsEqualTo(rebuilt);
        await Assert.That(pending.NewIncarnation).IsNull();
        await Assert.That(pending.AdditionalIncarnation).IsEqualTo(original);
        await Assert.That(await RefusedWriteAsync(api, database)).IsTrue();

        var rebuiltSide = storage.SidePath(database, rebuilt);
        var replacement = await storage.SwapAsync(database, rebuilt, rebuiltSide, CancellationToken.None)
            ?? throw new InvalidOperationException("the faulted certified copy did not swap");
        var swapped = pending with { Phase = RebuildPhase.Swapped, NewIncarnation = replacement };
        await ledger.SaveAsync(swapped, CancellationToken.None);
        await Assert.That(storage.SideExists(rebuiltSide) && storage.SideExists(originalSide)).IsTrue();
        // The additional side copy salvages into the replacement beside the faulted one's own.
        await Assert.That(await repair.RepairRebuildAsync(database, replacement, await SideStatesAsync(storage, originalSide), CancellationToken.None) ?? 0)
            .IsGreaterThan(0);
        _ = await repair.RepairRebuildAsync(database, replacement, await SideStatesAsync(storage, rebuiltSide), CancellationToken.None);
        await Assert.That(await repair.RecordRefillAsync(database, replacement, "lifecycle-source", Ulid.NewUlid(), 1, default, CancellationToken.None)).IsTrue();
        await ledger.SaveAsync(swapped with { Phase = RebuildPhase.Salvaged }, CancellationToken.None);
        await Assert.That(await repair.CompleteRebuildAsync(database, rebuilt, replacement, CancellationToken.None)).IsTrue();

        await Assert.That(await storage.FinalizeAsync(database, replacement, rebuiltSide, CancellationToken.None)).IsTrue();
        await Assert.That(await storage.FinalizeAsync(database, replacement, originalSide, CancellationToken.None)).IsTrue();
        await ledger.RetireAsync(database, rebuilt, CancellationToken.None);
        await Assert.That(await ledger.GetAsync(database, CancellationToken.None)).IsNull();
        await Assert.That(storage.SideExists(rebuiltSide) || storage.SideExists(originalSide)).IsFalse();
        var store = await services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, CancellationToken.None)
            ?? throw new InvalidOperationException("the rebuilt database is missing");
        await using (var read = await store.BeginTransactionAsync(write: false, CancellationToken.None))
        {
            await Assert.That(await read.GetAsync("items", "kept", CancellationToken.None)).IsNotNull();
        }

        await Assert.That(await RefusedWriteAsync(api, database)).IsFalse();
    }

    [Test]
    public async Task A_certified_copy_that_faults_beside_two_retained_sides_stays_pending_and_moves_nothing()
    {
        await using var api = await StartAsync();
        var services = api.Server.Services;
        var storage = services.GetRequiredService<IRebuildStorage>();
        var ledger = services.GetRequiredService<IRebuildLedger>();
        var (database, original, rebuilt, additional) = await CertifiedAsync(api, retainAdditional: true);
        var sides = (string[])[storage.SidePath(database, original), storage.SidePath(database, additional!.Value)];
        var preserved = await Task.WhenAll(sides.Select(side => File.ReadAllBytesAsync(side)));

        await services.GetRequiredService<IReplicaCopyFaults>().RecordAsync(database, rebuilt, "a certified copy faulted", CancellationToken.None);

        // Two side copies already kept: the faulted copy stays in place, named by the pending record, and the rebuild refuses.
        var pending = await ledger.GetAsync(database, CancellationToken.None) ?? throw new InvalidOperationException("the fault was not recorded");
        await Assert.That(pending.Phase).IsEqualTo(RebuildPhase.Pending);
        await Assert.That(pending.OldIncarnation).IsEqualTo(rebuilt);
        await Assert.That(pending.NewIncarnation).IsEqualTo(rebuilt);
        await Assert.That(pending.AdditionalIncarnation).IsEqualTo(original);
        await Assert.That(await storage.SwapAsync(database, rebuilt, pending.SidePath, CancellationToken.None)).IsNull();
        await Assert.That(await storage.PublishFreshAsync(database, pending.SidePath, CancellationToken.None)).IsNull();
        _ = await Assert.ThrowsAsync<ReplicaCopyFaultedException>(async () =>
            await ledger.SaveAsync(pending with { Phase = RebuildPhase.Swapped, NewIncarnation = Ulid.NewUlid() }, CancellationToken.None));
        await Assert.That(await storage.FinalizeAsync(database, rebuilt, sides[0], CancellationToken.None)).IsFalse();

        await Assert.That(await storage.IncarnationAsync(database, CancellationToken.None)).IsEqualTo(rebuilt);
        await Assert.That(storage.SideExists(pending.SidePath)).IsFalse();
        for (var i = 0; i < sides.Length; i++)
        {
            await Assert.That((await File.ReadAllBytesAsync(sides[i])).SequenceEqual(preserved[i])).IsTrue();
        }

        await Assert.That(await RefusedWriteAsync(api, database)).IsTrue();
        await Assert.That(await ledger.GetAsync(database, CancellationToken.None)).IsEqualTo(pending);
    }

    [Test]
    public async Task A_third_incarnation_faulting_beside_a_certified_record_fails_closed_and_the_next_start_resumes()
    {
        var root = Directory.CreateTempSubdirectory("terrabase-rebuild-third-").FullName;
        try
        {
            TenantContext database;
            Ulid original;
            Ulid rebuilt;
            await using (var api = await StartAsync(root))
            {
                var services = api.Server.Services;
                var storage = services.GetRequiredService<IRebuildStorage>();
                var ledger = services.GetRequiredService<IRebuildLedger>();
                (database, original, rebuilt, _) = await CertifiedAsync(api, retainAdditional: false);
                // The certified copy serves: this write records its intent in this process.
                await Assert.That(await RefusedWriteAsync(api, database)).IsFalse();
                var certified = await ledger.GetAsync(database, CancellationToken.None);

                await services.GetRequiredService<IReplicaCopyFaults>().RecordAsync(database, Ulid.NewUlid(), "a third incarnation", CancellationToken.None);

                // One row cannot name a third copy: nothing changes or is deleted, and the node refuses every write.
                await Assert.That(await ledger.GetAsync(database, CancellationToken.None)).IsEqualTo(certified);
                await Assert.That(await services.GetRequiredService<IStorageEngine>().IsReadyAsync(CancellationToken.None)).IsFalse();
                await Assert.That(await services.GetRequiredService<IReplicaCopyFaults>().RefusedAsync(database, rebuilt, CancellationToken.None)).IsTrue();
                await Assert.That(await RefusedWriteAsync(api, database)).IsTrue();
                await Assert.That(await storage.FinalizeAsync(database, rebuilt, storage.SidePath(database, original), CancellationToken.None)).IsFalse();
                await Assert.That(storage.SideExists(storage.SidePath(database, original))).IsTrue();

                // Recovery still reads and hands out its durable work: the failed-closed node does not wedge it.
                var attempted = new RecordingRebuild();
                var recovery = new RebuildRecovery(services.GetRequiredService<IStorageEngine>(), services.GetRequiredService<IRebuildRecovery>(), attempted,
                    services.GetRequiredService<IHostApplicationLifetime>(), TimeProvider.System,
                    services.GetRequiredService<IMeterFactory>(), services.GetRequiredService<ILogger<RebuildRecovery>>());
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await recovery.RunPendingAsync(deadline.Token);
                await Assert.That(attempted.Calls).Contains((database.DatabaseId!, original));
            }

            // The fail-closed stop is unclean: the next start faults the certified copy's intent into a new pending rebuild that
            // salvages the unfinalized side copy, and serves.
            await using var restarted = await StartAsync(root);
            var next = restarted.Server.Services;
            await Assert.That(await next.GetRequiredService<IStorageEngine>().IsReadyAsync(CancellationToken.None)).IsTrue();
            var pending = await next.GetRequiredService<IRebuildLedger>().GetAsync(database, CancellationToken.None);
            await Assert.That(pending?.Phase).IsEqualTo(RebuildPhase.Pending);
            await Assert.That(pending?.OldIncarnation).IsEqualTo(rebuilt);
            await Assert.That(pending?.NewIncarnation).IsNull();
            await Assert.That(pending?.AdditionalIncarnation).IsEqualTo(original);
            await Assert.That(await RefusedWriteAsync(restarted, database)).IsTrue();
            var nextStorage = next.GetRequiredService<IRebuildStorage>();
            await Assert.That(nextStorage.SideExists(nextStorage.SidePath(database, original))).IsTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // A faulted database rebuilt through the ledger's steps up to its completion barrier, its side copies not finalized. With
    // retainAdditional, the first replacement faults under rebuild and is kept aside as the additional copy.
    private static async Task<(TenantContext Database, Ulid Original, Ulid Rebuilt, Ulid? Additional)> CertifiedAsync(ManagementApi api, bool retainAdditional)
    {
        var services = api.Server.Services;
        var storage = services.GetRequiredService<IRebuildStorage>();
        var ledger = services.GetRequiredService<IRebuildLedger>();
        var repair = services.GetRequiredService<IRebuildRepair>();
        var (database, store) = await FaultedAsync(api);
        var original = store.Incarnation;
        var side = storage.SidePath(database, original);
        var rebuilt = await storage.SwapAsync(database, original, side, CancellationToken.None)
            ?? throw new InvalidOperationException("the test copy did not swap");
        var swapped = await SwappedAsync(ledger, database, rebuilt);
        Ulid? additional = null;
        if (retainAdditional)
        {
            // A replacement under rebuild faults: kept aside unwritten, and the rebuild starts over from the side copy.
            await services.GetRequiredService<IReplicaCopyFaults>().RecordAsync(database, rebuilt, "an uncertain replacement", CancellationToken.None);
            additional = rebuilt;
            rebuilt = await storage.PublishFreshAsync(database, side, CancellationToken.None)
                ?? throw new InvalidOperationException("the second replacement was not published");
            swapped = await SwappedAsync(ledger, database, rebuilt);
        }

        if (await repair.RepairRebuildAsync(database, rebuilt, await SideStatesAsync(storage, side), CancellationToken.None) is not > 0)
        {
            throw new InvalidOperationException("the repair wrote nothing into the replacement");
        }

        // A stand-in source: the proof is what the barrier certifies.
        if (!await repair.RecordRefillAsync(database, rebuilt, "lifecycle-source", Ulid.NewUlid(), 1, default, CancellationToken.None))
        {
            throw new InvalidOperationException("the refill proof was refused");
        }

        await ledger.SaveAsync(swapped with { Phase = RebuildPhase.Salvaged }, CancellationToken.None);
        if (!await repair.CompleteRebuildAsync(database, original, rebuilt, CancellationToken.None))
        {
            throw new InvalidOperationException("the completion barrier did not certify");
        }

        return (database, original, rebuilt, additional);
    }

    private static async Task<RebuildRecord> SwappedAsync(IRebuildLedger ledger, TenantContext database, Ulid replacement)
    {
        var pending = await ledger.GetAsync(database, CancellationToken.None) ?? throw new InvalidOperationException("the fault was not recorded");
        var swapped = pending with { Phase = RebuildPhase.Swapped, NewIncarnation = replacement };
        await ledger.SaveAsync(swapped, CancellationToken.None);
        return swapped;
    }

    // Every state of a side copy, read the way the salvage reads it: from a working copy, never the side copy itself.
    private static async Task<IReadOnlyList<RepairState>> SideStatesAsync(IRebuildStorage storage, string side)
    {
        var working = await storage.CopyForSalvageAsync(side, CancellationToken.None);
        try
        {
            var rows = await storage.MeasureSideAsync(working, string.Empty, string.Empty, 1024, 2 * 1024 * 1024, CancellationToken.None);
            return await storage.ReadSideAsync(working, rows, CancellationToken.None);
        }
        finally
        {
            await storage.DiscardWorkingCopyAsync(working, CancellationToken.None);
        }
    }

    // Whether an ordinary durable write to the copy in place is refused.
    private static async Task<bool> RefusedWriteAsync(ManagementApi api, TenantContext database)
    {
        var store = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, CancellationToken.None)
            ?? throw new InvalidOperationException("the test database is missing");
        try
        {
            await using var write = await store.BeginDurableTransactionAsync(CancellationToken.None);
            _ = await write.CreateCollectionAsync(new CollectionInfo("after", 1), CancellationToken.None);
            _ = await write.CommitAsync(CancellationToken.None);
            return false;
        }
        catch (ReplicaCopyFaultedException)
        {
            return true;
        }
    }

    // A managed database written once (its intent recorded), then faulted.
    private static async Task<(TenantContext Database, SqliteDatabaseStore Store)> FaultedAsync(ManagementApi api)
    {
        var services = api.Server.Services;
        var managed = await api.CreateManagedDatabaseAsync();
        var database = await services.GetRequiredService<ITenantDirectory>()
            .FindDatabaseAsync(managed.Organization, managed.Project, managed.Name, CancellationToken.None)
            is TenantContext found ? found : throw new InvalidOperationException("the test database is not registered");
        var store = (SqliteDatabaseStore)(await services.GetRequiredService<SqliteStorageEngine>().OpenDatabaseAsync(database, CancellationToken.None)
            ?? throw new InvalidOperationException("the test database is missing"));
        await using (var write = await store.BeginDurableTransactionAsync(CancellationToken.None))
        {
            _ = await write.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await write.PutAsync("items", "kept", "{\"n\":1}"u8.ToArray(), CancellationToken.None);
            _ = await write.CommitAsync(CancellationToken.None);
        }

        await services.GetRequiredService<IReplicaCopyFaults>().RecordAsync(database, store.Incarnation, "test fault", CancellationToken.None);
        return (database, store);
    }

    private static Task<ManagementApi> StartAsync(string? root = null, Action<IServiceCollection>? configure = null) =>
        ManagementApi.StartAsync(root is null ? null : settings => settings["data-directory"] = root,
            services =>
            {
                services.AddSingleton<IReplicaRebuild, BusyRebuild>();
                configure?.Invoke(services);
            });

    private sealed class BusyRebuild : IReplicaRebuild
    {
        public ValueTask<RebuildOutcome> RebuildAsync(TenantContext database, Ulid faultedIncarnation, CancellationToken cancellationToken) =>
            ValueTask.FromResult<RebuildOutcome>(new RebuildOutcome.Busy());
    }

    private sealed class RecordingRebuild : IReplicaRebuild
    {
        public ConcurrentBag<(string Database, Ulid Incarnation)> Calls { get; } = [];

        public ValueTask<RebuildOutcome> RebuildAsync(TenantContext database, Ulid faultedIncarnation, CancellationToken cancellationToken)
        {
            Calls.Add((database.DatabaseId!, faultedIncarnation));
            return ValueTask.FromResult<RebuildOutcome>(new RebuildOutcome.Refused("recorded by the test"));
        }
    }
}
