using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Features.Replication;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraBase.Testing;
using Turso.Data.Sqlite;

namespace TerraBase.Fast.Replication;

public sealed class RebuildFaultSafetyTests
{
    private const string CrashRoot = "TERRABASE_TEST_READ_FAULT_CRASH_ROOT";

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_group_commit_fault_after_the_native_commit_fails_without_losing_the_landed_document(bool systemFault)
    {
        using var directory = new TempDirectory();
        Action<string, TenantContext>? checkpoint = null;
        await using var api = await StartAsync(directory.Root, services =>
            services.AddSingleton<DatabaseLifecycleCheckpoint>((step, database) => checkpoint?.Invoke(step, database)));
        var (database, store) = await CreateAsync(api);
        var faults = api.Server.Services.GetRequiredService<IReplicaCopyFaults>();
        // Admit forensic inspection while SYSTEM is healthy; its first read follows the native commit below.
        await using var read = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        Task? recording = null;
        checkpoint = (step, marked) =>
        {
            if (step == "writer.marker" && marked == database)
            {
                recording = faults.RecordAsync(systemFault ? ChangeGroups.System : database,
                    systemFault ? Ulid.Empty : store.Incarnation, "a group commit marker fault", CancellationToken.None).AsTask();
            }
        };

        _ = await Assert.ThrowsAsync<ReplicaCopyFaultedException>(async () =>
            await store.WriteAsync((write, token) => write.PutAsync("items", "landed", "{\"n\":2}"u8.ToArray(), token),
                _ => true, CancellationToken.None));
        checkpoint = null;
        await Assert.That(recording).IsNotNull();
        await recording!;
        await Assert.That(await faults.RefusedAsync(database, store.Incarnation, CancellationToken.None)).IsTrue();
        await Assert.That(await read.GetAsync("items", "landed", CancellationToken.None)).IsNotNull()
            .Because("the native commit landed, but the damaged copy must acknowledge no success or replay");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_group_commit_refuses_a_fault_published_during_nonthrowing_disposal(bool systemFault)
    {
        using var directory = new TempDirectory();
        Action<string, TenantContext>? checkpoint = null;
        await using var api = await StartAsync(directory.Root, services =>
            services.AddSingleton<DatabaseLifecycleCheckpoint>((step, database) => checkpoint?.Invoke(step, database)));
        var (database, store) = await CreateAsync(api);
        var faults = api.Server.Services.GetRequiredService<IReplicaCopyFaults>();
        // A new operation must not write an intent to SYSTEM after the injected node fault.
        await using var read = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        var reset = false;
        Task? recording = null;
        checkpoint = (step, marked) =>
        {
            if (step == "writer.reset" && marked == database)
            {
                reset = true;
                if (systemFault)
                {
                    recording = faults.RecordAsync(ChangeGroups.System, Ulid.Empty, "a node fault during writer reset", CancellationToken.None).AsTask();
                    return;
                }
                throw new SqliteException("a damaged writer reset", 11);
            }
        };
        _ = await Assert.ThrowsAsync<ReplicaCopyFaultedException>(async () =>
            await store.WriteAsync((write, token) => write.PutAsync("items", "landed", "{\"n\":2}"u8.ToArray(), token),
                _ => true, CancellationToken.None));
        checkpoint = null;
        await Assert.That(reset).IsTrue();
        if (recording is not null)
        {
            await recording;
        }
        await Assert.That(await faults.RefusedAsync(database, store.Incarnation, CancellationToken.None)).IsTrue();
        await Assert.That(await read.GetAsync("items", "landed", CancellationToken.None)).IsNotNull();
    }

    [Test]
    public async Task A_read_fault_crash_before_disposal_without_a_tenant_writer_is_refused_at_startup()
    {
        if (Environment.GetEnvironmentVariable(CrashRoot) is { } childRoot)
        {
            await ReadCrashChildAsync(childRoot, atOnce: false);
            return;
        }

        await ReadCrashIsRefusedAsync(nameof(A_read_fault_crash_before_disposal_without_a_tenant_writer_is_refused_at_startup));
    }

    // Reads record no write intent (decision 0013d): the damage's own mark, durable before the error surfaces, is all that
    // refuses the copy after a crash the instant the read fails.
    [Test]
    public async Task A_read_that_crashes_as_soon_as_it_detects_damage_is_refused_at_startup()
    {
        if (Environment.GetEnvironmentVariable(CrashRoot) is { } childRoot)
        {
            await ReadCrashChildAsync(childRoot, atOnce: true);
            return;
        }

        await ReadCrashIsRefusedAsync(nameof(A_read_that_crashes_as_soon_as_it_detects_damage_is_refused_at_startup));
    }

    private static async Task ReadCrashIsRefusedAsync(string test)
    {
        using var directory = new TempDirectory();
        var (exit, output) = await RunChildAsync(directory.Root, test);
        await Assert.That(exit).IsNotEqualTo(0).Because(output);
        await Assert.That(output).Contains("crashed at retained read corruption");
        var crashed = JsonSerializer.Deserialize<ReadCrash>(await File.ReadAllTextAsync(Path.Combine(directory.Root, "read-crash.json")))!;
        var database = new TenantContext(crashed.Organization, PlanTier.Hobby, crashed.Project, crashed.Database);
        await using var api = await StartAsync(Path.Combine(directory.Root, "data"));
        var services = api.Server.Services;
        var row = await services.GetRequiredService<IRebuildLedger>().GetAsync(database, CancellationToken.None);
        await Assert.That(row?.Phase).IsEqualTo(RebuildPhase.Pending);
        await Assert.That(row?.OldIncarnation).IsEqualTo(Ulid.Parse(crashed.Incarnation));
        var store = (TursoDatabaseStore)(await services.GetRequiredService<TursoStorageEngine>().OpenDatabaseAsync(database, CancellationToken.None)
            ?? throw new InvalidOperationException("the crashed copy is missing"));
        await Assert.That(store.CurrentWriter()).IsNull();
        await Assert.That(await services.GetRequiredService<IReplicaCopyFaults>()
            .RefusedAsync(database, store.Incarnation, CancellationToken.None)).IsTrue();
        _ = await Assert.ThrowsAsync<ReplicaCopyFaultedException>(async () =>
        {
            await using var refused = await store.BeginDurableTransactionAsync(CancellationToken.None);
        });
    }

    [Test]
    public async Task A_healthy_read_only_open_keeps_its_copy_usable_after_clean_shutdown()
    {
        using var directory = new TempDirectory();
        TenantContext database;
        string path;
        await using (var setup = await StartAsync(directory.Root))
        {
            (database, var store) = await CreateAsync(setup);
            path = store.FilePath;
        }
        await Assert.That(!File.Exists(path + "-wal") || new FileInfo(path + "-wal").Length == 0).IsTrue()
            .Because("a healthy cached writer still checkpoints at close");
        await using (var reading = await StartAsync(directory.Root))
        {
            var store = (TursoDatabaseStore)(await reading.Server.Services.GetRequiredService<TursoStorageEngine>()
                .OpenDatabaseAsync(database, CancellationToken.None) ?? throw new InvalidOperationException("the healthy copy is missing"));
            await using var read = await store.BeginTransactionAsync(write: false, CancellationToken.None);
            await Assert.That(await read.GetAsync("damaged", "kept", CancellationToken.None)).IsNotNull();
            await Assert.That(store.CurrentWriter()).IsNull();
        }
        await using var restarted = await StartAsync(directory.Root);
        await Assert.That(await restarted.Server.Services.GetRequiredService<IRebuildLedger>()
            .GetAsync(database, CancellationToken.None)).IsNull().Because("clean shutdown retires a read-only open's intent");
        var copy = await restarted.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, CancellationToken.None)
            ?? throw new InvalidOperationException("the healthy copy is missing");
        await using var write = await copy.BeginDurableTransactionAsync(CancellationToken.None);
        _ = await write.PutAsync("items", "after-clean-read", "{\"n\":4}"u8.ToArray(), CancellationToken.None);
        _ = await write.CommitAsync(CancellationToken.None);
    }

    [Test]
    [Arguments("read")]
    [Arguments("rebuild")]
    [Arguments("node")]
    public async Task Draining_a_refused_cached_writer_preserves_the_main_file_and_nonempty_WAL(string refusal)
    {
        using var directory = new TempDirectory();
        TenantContext database;
        string path;
        await using (var setup = await StartAsync(directory.Root))
        {
            (database, var store) = await CreateAsync(setup);
            path = store.FilePath;
        }
        if (refusal == "read")
        {
            await CorruptTablePageAsync(path, TursoStorageTransaction.Table("damaged").Trim('"'));
        }

        await using var api = await StartAsync(directory.Root);
        var services = api.Server.Services;
        var copy = (TursoDatabaseStore)(await services.GetRequiredService<TursoStorageEngine>().OpenDatabaseAsync(database, CancellationToken.None)
            ?? throw new InvalidOperationException("the test copy is missing"));
        await using (var write = await copy.BeginDurableTransactionAsync(CancellationToken.None))
        {
            _ = await write.PutAsync("items", "in-wal", "{\"n\":3}"u8.ToArray(), CancellationToken.None);
            _ = await write.CommitAsync(CancellationToken.None);
        }
        var writer = copy.CurrentWriter();
        await Assert.That(writer).IsNotNull();
        var storage = services.GetRequiredService<IRebuildStorage>();
        var side = storage.SidePath(database, copy.Incarnation);
        if (refusal == "read")
        {
            await using var read = await copy.BeginTransactionAsync(write: false, CancellationToken.None);
            var error = await Assert.ThrowsAsync<SqliteException>(async () =>
                await read.GetAsync("damaged", "kept", CancellationToken.None));
            await Assert.That(error!.SqliteErrorCode).IsEqualTo(11);
        }
        else if (refusal == "rebuild")
        {
            var ledger = services.GetRequiredService<IRebuildLedger>();
            var pending = new RebuildRecord(database, Ulid.NewUlid(), side, RebuildPhase.Pending, null);
            await ledger.SaveAsync(pending, CancellationToken.None);
            await ledger.SaveAsync(pending with { Phase = RebuildPhase.Swapped, NewIncarnation = copy.Incarnation }, CancellationToken.None);
            await Assert.That(copy.CopyFault).IsNull().Because("the ledger alone refuses this copy under rebuild");
        }
        else
        {
            await services.GetRequiredService<IReplicaCopyFaults>()
                .RecordAsync(ChangeGroups.System, Ulid.Empty, "a failed node", CancellationToken.None);
        }

        var main = await File.ReadAllBytesAsync(path);
        var wal = await File.ReadAllBytesAsync(path + "-wal");
        await Assert.That(wal.Length).IsGreaterThan(0);
        if (refusal == "node")
        {
            // Close the failed node's copy under its held writer and inspect its retained files in place.
            _ = await copy.WithWritersHeldAsync(() =>
            {
                copy.Dispose();
                return Task.FromResult(true);
            }, CancellationToken.None);
            side = path;
        }
        else
        {
            await Assert.That(await storage.SwapAsync(database, copy.Incarnation, side, CancellationToken.None)).IsNotNull();
        }
        await Assert.That(writer!.Connection.State).IsEqualTo(System.Data.ConnectionState.Closed);
        await Assert.That((await File.ReadAllBytesAsync(side)).SequenceEqual(main)).IsTrue();
        await Assert.That((await File.ReadAllBytesAsync(side + "-wal")).SequenceEqual(wal)).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_reader_held_across_a_refused_swap_preserves_the_side_copy_after_late_disposal(bool lateReadFault)
    {
        using var directory = new TempDirectory();
        TenantContext database;
        string path;
        await using (var setup = await StartAsync(directory.Root))
        {
            (database, var store) = await CreateAsync(setup);
            path = store.FilePath;
        }
        if (lateReadFault)
        {
            await CorruptTablePageAsync(path, TursoStorageTransaction.Table("damaged").Trim('"'));
        }

        await using var api = await StartAsync(directory.Root);
        var services = api.Server.Services;
        var engine = services.GetRequiredService<TursoStorageEngine>();
        var copy = (TursoDatabaseStore)(await engine.OpenDatabaseAsync(database, CancellationToken.None)
            ?? throw new InvalidOperationException("the test copy is missing"));
        await using (var write = await copy.BeginDurableTransactionAsync(CancellationToken.None))
        {
            _ = await write.PutAsync("items", "in-wal", "{\"n\":3}"u8.ToArray(), CancellationToken.None);
            _ = await write.CommitAsync(CancellationToken.None);
        }
        var writer = copy.CurrentWriter();
        await Assert.That(writer).IsNotNull();
        // Own this read through the swap; only its owner disposes it after the replacement is certified.
        await using var held = await copy.BeginTransactionAsync(write: false, CancellationToken.None);
        var before = await held.GetAsync("items", "in-wal", CancellationToken.None);
        await Assert.That(before).IsNotNull();
        await Assert.That(before!.Json.ToArray().SequenceEqual("{\"n\":3}"u8.ToArray())).IsTrue();
        var storage = services.GetRequiredService<IRebuildStorage>();
        var ledger = services.GetRequiredService<IRebuildLedger>();
        var faults = services.GetRequiredService<IReplicaCopyFaults>();
        var side = storage.SidePath(database, copy.Incarnation);
        var pending = new RebuildRecord(database, copy.Incarnation, side, RebuildPhase.Pending, null);
        await ledger.SaveAsync(pending, CancellationToken.None);
        await Assert.That(copy.CopyFault).IsNull().Because("the ledger alone must preserve the old reader's close");
        _ = await Assert.ThrowsAsync<ReplicaCopyFaultedException>(async () =>
        {
            await using var refused = await copy.BeginDurableTransactionAsync(CancellationToken.None);
        });
        var target = database with { DatabaseId = Ulid.NewUlid().ToString() };
        _ = await Assert.ThrowsAsync<ReplicaCopyFaultedException>(async () =>
            await engine.CopyDatabaseAsync(database, target, CancellationToken.None));
        await Assert.That(await engine.OpenDatabaseAsync(target, CancellationToken.None)).IsNull();

        var main = await File.ReadAllBytesAsync(path);
        var wal = await File.ReadAllBytesAsync(path + "-wal");
        await Assert.That(wal.Length).IsGreaterThan(0);
        var replacement = await storage.SwapAsync(database, copy.Incarnation, side, CancellationToken.None)
            ?? throw new InvalidOperationException("the refused copy did not swap while its reader was held");
        await Assert.That(replacement).IsNotEqualTo(copy.Incarnation);
        await Assert.That(writer!.Connection.State).IsEqualTo(System.Data.ConnectionState.Closed);
        await Assert.That((await File.ReadAllBytesAsync(side)).SequenceEqual(main)).IsTrue();
        await Assert.That((await File.ReadAllBytesAsync(side + "-wal")).SequenceEqual(wal)).IsTrue();
        var fresh = (TursoDatabaseStore)(await engine.OpenDatabaseAsync(database, CancellationToken.None)
            ?? throw new InvalidOperationException("the replacement is missing"));
        await Assert.That(fresh.Incarnation).IsEqualTo(replacement);
        var swapped = pending with { Phase = RebuildPhase.Swapped, NewIncarnation = replacement };
        await ledger.SaveAsync(swapped, CancellationToken.None);
        _ = await Assert.ThrowsAsync<ReplicaCopyFaultedException>(async () =>
        {
            await using var refused = await fresh.BeginDurableTransactionAsync(CancellationToken.None);
        });
        _ = await Assert.ThrowsAsync<ReplicaCopyFaultedException>(async () =>
            await engine.CopyDatabaseAsync(database, target, CancellationToken.None));
        await using (var rebuilding = await fresh.BeginRebuildTransactionAsync(CancellationToken.None))
        {
            await Assert.That(await rebuilding.GetAsync("items", "in-wal", CancellationToken.None)).IsNull();
            _ = await rebuilding.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await rebuilding.PutAsync("items", "in-wal", "{\"n\":4}"u8.ToArray(), CancellationToken.None);
            _ = await rebuilding.CommitAsync(CancellationToken.None);
        }
        var repair = services.GetRequiredService<IRebuildRepair>();
        await Assert.That(await repair.RecordRefillAsync(database, replacement, "held-reader-source", Ulid.NewUlid(), 1, default, CancellationToken.None)).IsTrue();
        await ledger.SaveAsync(swapped with { Phase = RebuildPhase.Salvaged }, CancellationToken.None);
        await Assert.That(await repair.CompleteRebuildAsync(database, copy.Incarnation, replacement, CancellationToken.None)).IsTrue();
        var certified = await ledger.GetAsync(database, CancellationToken.None);
        await Assert.That(certified?.Phase).IsEqualTo(RebuildPhase.Certified);
        await using (var readFresh = await fresh.BeginTransactionAsync(write: false, CancellationToken.None))
        {
            var freshDocument = await readFresh.GetAsync("items", "in-wal", CancellationToken.None);
            await Assert.That(freshDocument?.Json.ToArray().SequenceEqual("{\"n\":4}"u8.ToArray())).IsTrue();
        }
        var retained = await held.GetAsync("items", "in-wal", CancellationToken.None);
        await Assert.That(retained?.Json.ToArray().SequenceEqual(before!.Json.ToArray())).IsTrue()
            .Because("the still-open old reader must not read the fresh incarnation at the reused path");
        if (lateReadFault)
        {
            var error = await Assert.ThrowsAsync<SqliteException>(async () =>
                await held.GetAsync("damaged", "kept", CancellationToken.None));
            await Assert.That(error!.SqliteErrorCode).IsEqualTo(11);
            await Assert.That(copy.CopyFault).IsNotNull();
        }
        await held.DisposeAsync();
        await Assert.That((await File.ReadAllBytesAsync(side)).SequenceEqual(main)).IsTrue();
        await Assert.That((await File.ReadAllBytesAsync(side + "-wal")).SequenceEqual(wal)).IsTrue();
        await Assert.That(await ledger.GetAsync(database, CancellationToken.None)).IsEqualTo(certified)
            .Because("late fault recording belongs to the old incarnation, not its certified replacement");
        await Assert.That(fresh.CopyFault).IsNull();
        await Assert.That(await faults.RefusedAsync(database, copy.Incarnation, CancellationToken.None)).IsTrue();
        await Assert.That(await faults.RefusedAsync(database, replacement, CancellationToken.None)).IsFalse();
        _ = await Assert.ThrowsAsync<ReplicaCopyFaultedException>(async () =>
        {
            await using var refused = await copy.BeginDurableTransactionAsync(CancellationToken.None);
        });
        await using (var write = await fresh.BeginDurableTransactionAsync(CancellationToken.None))
        {
            _ = await write.PutAsync("items", "in-wal", "{\"n\":5}"u8.ToArray(), CancellationToken.None);
            _ = await write.CommitAsync(CancellationToken.None);
        }
        await Assert.That(await engine.CopyDatabaseAsync(database, target, CancellationToken.None)).IsTrue();
        var copied = await engine.OpenDatabaseAsync(target, CancellationToken.None)
            ?? throw new InvalidOperationException("the healthy replacement was not copied");
        await using var read = await copied.BeginTransactionAsync(write: false, CancellationToken.None);
        var document = await read.GetAsync("items", "in-wal", CancellationToken.None);
        await Assert.That(document?.Json.ToArray().SequenceEqual("{\"n\":5}"u8.ToArray())).IsTrue();
        await Assert.That((await File.ReadAllBytesAsync(side)).SequenceEqual(main)).IsTrue();
        await Assert.That((await File.ReadAllBytesAsync(side + "-wal")).SequenceEqual(wal)).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_backup_tap_held_across_a_refused_swap_preserves_the_side_copy_after_late_disposal(bool replaceReader)
    {
        using var directory = new TempDirectory();
        await using var api = await StartAsync(directory.Root);
        var (database, copy) = await CreateAsync(api);
        var services = api.Server.Services;
        var engine = services.GetRequiredService<TursoStorageEngine>();
        var backup = services.GetRequiredService<IWalBackup>();
        await using var tap = (TursoChangeTap)(await backup.OpenTapAsync(database, CancellationToken.None)
            ?? throw new InvalidOperationException("the real backup tap is missing"));
        if (replaceReader)
        {
            // Replace the reader while healthy, before the ledger refuses the old copy.
            tap.AfterSnapshotBegin = _ => throw new IOException("replace the backup tap's unpinned reader");
            var error = await Assert.ThrowsAsync<IOException>(async () =>
                await tap.SnapshotAsync(Stream.Null, Path.Combine(directory.Root, "tap-snapshot.db"), CancellationToken.None));
            tap.AfterSnapshotBegin = null;
            await Assert.That(error!.Message).IsEqualTo("replace the backup tap's unpinned reader");
        }
        await using (var write = await copy.BeginDurableTransactionAsync(CancellationToken.None))
        {
            _ = await write.PutAsync("items", "in-wal", "{\"n\":3}"u8.ToArray(), CancellationToken.None);
            _ = await write.CommitAsync(CancellationToken.None);
        }
        var writer = copy.CurrentWriter();
        await Assert.That(writer).IsNotNull();
        // Both the initial and replacement reader must observe a real commit before outliving the swap.
        var tapped = await tap.ReadAsync(Stream.Null, CancellationToken.None);
        await Assert.That(tapped.Continuous).IsTrue();
        await Assert.That(tapped.Bytes).IsGreaterThan(9);
        await Assert.That(tapped.Mark).IsNotNull();
        var storage = services.GetRequiredService<IRebuildStorage>();
        var ledger = services.GetRequiredService<IRebuildLedger>();
        var faults = services.GetRequiredService<IReplicaCopyFaults>();
        var side = storage.SidePath(database, copy.Incarnation);
        var pending = new RebuildRecord(database, copy.Incarnation, side, RebuildPhase.Pending, null);
        await ledger.SaveAsync(pending, CancellationToken.None);
        await Assert.That(copy.CopyFault).IsNull().Because("the ledger alone must refuse the tap's old store on close");
        await Assert.That(copy.CloseRefusal).IsNotNull();
        _ = await Assert.ThrowsAsync<ReplicaCopyFaultedException>(async () =>
        {
            await using var refused = await copy.BeginDurableTransactionAsync(CancellationToken.None);
        });
        var target = database with { DatabaseId = Ulid.NewUlid().ToString() };
        _ = await Assert.ThrowsAsync<ReplicaCopyFaultedException>(async () =>
            await engine.CopyDatabaseAsync(database, target, CancellationToken.None));
        await Assert.That(await engine.OpenDatabaseAsync(target, CancellationToken.None)).IsNull();

        var main = await File.ReadAllBytesAsync(copy.FilePath);
        var wal = await File.ReadAllBytesAsync(copy.FilePath + "-wal");
        await Assert.That(wal.Length).IsGreaterThan(0);
        var replacement = await storage.SwapAsync(database, copy.Incarnation, side, CancellationToken.None)
            ?? throw new InvalidOperationException("the refused copy did not swap while its backup tap was held");
        await Assert.That(replacement).IsNotEqualTo(copy.Incarnation);
        await Assert.That(writer!.Connection.State).IsEqualTo(System.Data.ConnectionState.Closed);
        await Assert.That((await File.ReadAllBytesAsync(side)).SequenceEqual(main)).IsTrue();
        await Assert.That((await File.ReadAllBytesAsync(side + "-wal")).SequenceEqual(wal)).IsTrue();
        var fresh = (TursoDatabaseStore)(await engine.OpenDatabaseAsync(database, CancellationToken.None)
            ?? throw new InvalidOperationException("the replacement is missing"));
        await Assert.That(fresh.Incarnation).IsEqualTo(replacement);
        var swapped = pending with { Phase = RebuildPhase.Swapped, NewIncarnation = replacement };
        await ledger.SaveAsync(swapped, CancellationToken.None);
        _ = await Assert.ThrowsAsync<ReplicaCopyFaultedException>(async () =>
        {
            await using var refused = await fresh.BeginDurableTransactionAsync(CancellationToken.None);
        });
        _ = await Assert.ThrowsAsync<ReplicaCopyFaultedException>(async () =>
            await engine.CopyDatabaseAsync(database, target, CancellationToken.None));
        await using (var rebuilding = await fresh.BeginRebuildTransactionAsync(CancellationToken.None))
        {
            await Assert.That(await rebuilding.GetAsync("items", "in-wal", CancellationToken.None)).IsNull();
            _ = await rebuilding.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await rebuilding.PutAsync("items", "in-wal", "{\"n\":4}"u8.ToArray(), CancellationToken.None);
            _ = await rebuilding.CommitAsync(CancellationToken.None);
        }
        var repair = services.GetRequiredService<IRebuildRepair>();
        await Assert.That(await repair.RecordRefillAsync(database, replacement, "held-backup-tap-source", Ulid.NewUlid(), 1, default, CancellationToken.None)).IsTrue();
        await ledger.SaveAsync(swapped with { Phase = RebuildPhase.Salvaged }, CancellationToken.None);
        await Assert.That(await repair.CompleteRebuildAsync(database, copy.Incarnation, replacement, CancellationToken.None)).IsTrue();
        var certified = await ledger.GetAsync(database, CancellationToken.None);
        await Assert.That(certified?.Phase).IsEqualTo(RebuildPhase.Certified);
        await using (var readFresh = await fresh.BeginTransactionAsync(write: false, CancellationToken.None))
        {
            var freshDocument = await readFresh.GetAsync("items", "in-wal", CancellationToken.None);
            await Assert.That(freshDocument?.Json.ToArray().SequenceEqual("{\"n\":4}"u8.ToArray())).IsTrue();
        }
        await Assert.That(copy.CloseRefusal).IsNotNull();
        await Assert.That(fresh.CloseRefusal).IsNull();
        await Assert.That((await File.ReadAllBytesAsync(side)).SequenceEqual(main)).IsTrue();
        await Assert.That((await File.ReadAllBytesAsync(side + "-wal")).SequenceEqual(wal)).IsTrue();
        // Drain owns the store's sessions; this independently owned tap closes only after fresh-copy certification.
        await tap.DisposeAsync();
        await Assert.That((await File.ReadAllBytesAsync(side)).SequenceEqual(main)).IsTrue();
        await Assert.That((await File.ReadAllBytesAsync(side + "-wal")).SequenceEqual(wal)).IsTrue();
        await Assert.That(await ledger.GetAsync(database, CancellationToken.None)).IsEqualTo(certified);
        await Assert.That(fresh.CopyFault).IsNull();
        await Assert.That(await faults.RefusedAsync(database, copy.Incarnation, CancellationToken.None)).IsTrue();
        await Assert.That(await faults.RefusedAsync(database, replacement, CancellationToken.None)).IsFalse();
        _ = await Assert.ThrowsAsync<ReplicaCopyFaultedException>(async () =>
        {
            await using var refused = await copy.BeginDurableTransactionAsync(CancellationToken.None);
        });
        await using (var write = await fresh.BeginDurableTransactionAsync(CancellationToken.None))
        {
            _ = await write.PutAsync("items", "in-wal", "{\"n\":5}"u8.ToArray(), CancellationToken.None);
            _ = await write.CommitAsync(CancellationToken.None);
        }
        await Assert.That(await engine.CopyDatabaseAsync(database, target, CancellationToken.None)).IsTrue();
        var copied = await engine.OpenDatabaseAsync(target, CancellationToken.None)
            ?? throw new InvalidOperationException("the healthy replacement was not copied");
        await using var read = await copied.BeginTransactionAsync(write: false, CancellationToken.None);
        var document = await read.GetAsync("items", "in-wal", CancellationToken.None);
        await Assert.That(document?.Json.ToArray().SequenceEqual("{\"n\":5}"u8.ToArray())).IsTrue();
        await Assert.That((await File.ReadAllBytesAsync(side)).SequenceEqual(main)).IsTrue();
        await Assert.That((await File.ReadAllBytesAsync(side + "-wal")).SequenceEqual(wal)).IsTrue();
    }

    private static async Task ReadCrashChildAsync(string root, bool atOnce)
    {
        var data = Path.Combine(root, "data");
        TenantContext database;
        string path;
        Ulid incarnation;
        await using (var setup = await StartAsync(data))
        {
            (database, var store) = await CreateAsync(setup);
            path = store.FilePath;
            incarnation = store.Incarnation;
        }
        await CorruptTablePageAsync(path, TursoStorageTransaction.Table("damaged").Trim('"'));
        await File.WriteAllTextAsync(Path.Combine(root, "read-crash.json"), JsonSerializer.Serialize(new ReadCrash(
            database.OrganizationId, database.ProjectId!, database.DatabaseId!, incarnation.ToString())));

        await using var api = await StartAsync(data);
        var services = api.Server.Services;
        var copy = (TursoDatabaseStore)(await services.GetRequiredService<TursoStorageEngine>().OpenDatabaseAsync(database, CancellationToken.None)
            ?? throw new InvalidOperationException("the damaged copy is missing"));
        await Assert.That(copy.CurrentWriter()).IsNull();
        await Assert.That(await services.GetRequiredService<IRebuildLedger>().GetAsync(database, CancellationToken.None)).IsNull();
        // Retain the damaged read until FailFast: no transaction disposal or tenant writer in this process.
        await using var read = await copy.BeginTransactionAsync(write: false, CancellationToken.None);
        await Assert.That(await IntentAsync(services.GetRequiredService<TursoStorageEngine>(), database)).IsFalse()
            .Because("a read-only transaction records no write intent");
        if (atOnce)
        {
            try
            {
                _ = await read.GetAsync("damaged", "kept", CancellationToken.None);
            }
            catch (SqliteException)
            {
                Environment.FailFast("crashed at retained read corruption");
            }

            throw new InvalidOperationException("the damaged read did not fail");
        }

        var error = await Assert.ThrowsAsync<SqliteException>(async () =>
            await read.GetAsync("damaged", "kept", CancellationToken.None));
        await Assert.That(error!.SqliteErrorCode).IsEqualTo(11);
        await Assert.That(copy.CopyFault).IsNotNull();
        await Assert.That(copy.CurrentWriter()).IsNull();
        await Assert.That(await services.GetRequiredService<IRebuildLedger>().GetAsync(database, CancellationToken.None)).IsNull();
        Environment.FailFast("crashed at retained read corruption");
    }

    private static Task<ManagementApi> StartAsync(string root, Action<IServiceCollection>? configure = null) =>
        ManagementApi.StartAsync(settings => settings["data-directory"] = root, services =>
        {
            services.AddSingleton<IReplicaRebuild, BusyRebuild>();
            configure?.Invoke(services);
        }, time: new FakeTimeProvider());

    private static async Task<(TenantContext Database, TursoDatabaseStore Store)> CreateAsync(ManagementApi api)
    {
        var managed = await api.CreateManagedDatabaseAsync();
        var database = await api.Server.Services.GetRequiredService<ITenantDirectory>()
            .FindDatabaseAsync(managed.Organization, managed.Project, managed.Name, CancellationToken.None)
            is TenantContext found ? found : throw new InvalidOperationException("the test database is not registered");
        var store = (TursoDatabaseStore)(await api.Server.Services.GetRequiredService<TursoStorageEngine>()
            .OpenDatabaseAsync(database, CancellationToken.None) ?? throw new InvalidOperationException("the test copy is missing"));
        await using var write = await store.BeginDurableTransactionAsync(CancellationToken.None);
        _ = await write.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
        _ = await write.CreateCollectionAsync(new CollectionInfo("damaged", 1), CancellationToken.None);
        _ = await write.PutAsync("damaged", "kept", JsonSerializer.SerializeToUtf8Bytes(new { n = new Faker().Random.Int(1, 1000) }), CancellationToken.None);
        _ = await write.CommitAsync(CancellationToken.None);
        return (database, store);
    }

    private static async Task CorruptTablePageAsync(string path, string table)
    {
        long offset;
        using (var connection = new SqliteConnection(TestMasterKey.ConnectionString(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        })))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            _ = await command.ExecuteNonQueryAsync();
            command.CommandText = "PRAGMA page_size;";
            var size = Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            command.CommandText = "SELECT rootpage FROM sqlite_schema WHERE name = $table;";
            command.Parameters.AddWithValue("$table", table);
            var page = Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            await Assert.That(page).IsGreaterThan(1);
            offset = (page - 1) * size;
        }
        // Complement the page's first byte: a fixed 0xFF left an encrypted page unchanged whenever its ciphertext held 0xFF.
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        file.Position = offset;
        var original = file.ReadByte();
        file.Position = offset;
        file.WriteByte((byte)~original);
    }

    private static Task<bool> IntentAsync(TursoStorageEngine engine, TenantContext database) =>
        engine.IntentStore!.WithWriterAsync(session =>
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM _tb_write_intent WHERE database = $database;";
            command.Parameters.AddWithValue("$database", database.DatabaseId);
            return command.ExecuteScalar() is not null;
        }, "checking the read's intent", CancellationToken.None);

    private static async Task<(int ExitCode, string Output)> RunChildAsync(string root, string test)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("the test executable path is unavailable");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(Path.GetFullPath(Environment.GetCommandLineArgs()[0]));
        }
        var type = typeof(RebuildFaultSafetyTests);
        foreach (var argument in new[]
        {
            "--treenode-filter", $"/{type.Assembly.GetName().Name}/{type.Namespace}/{type.Name}/{test}",
            "--no-progress", "--disable-logo", "--exit-on-process-exit", Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            "--results-directory", Directory.CreateDirectory(Path.Combine(root, "results")).FullName,
        })
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment[CrashRoot] = root;
        start.Environment["TMPDIR"] = Directory.CreateDirectory(Path.Combine(root, "tmp")).FullName;
        using var child = Process.Start(start) ?? throw new InvalidOperationException("the read crash child did not start");
        var output = child.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var error = child.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await child.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(2));
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync(CancellationToken.None);
            }
        }
        // FailFast identifies the crash before its stack trace; retain both ends of each redirected stream.
        return (child.ExitCode, Excerpt(await output) + Excerpt(await error));

        static string Excerpt(string text)
        {
            return text.Length > 8192 ? text[..4096] + text[^4096..] : text;
        }
    }

    private sealed record ReadCrash(string Organization, string Project, string Database, string Incarnation);

    private sealed class BusyRebuild : IReplicaRebuild
    {
        public ValueTask<RebuildOutcome> RebuildAsync(TenantContext database, Ulid faultedIncarnation, CancellationToken cancellationToken) =>
            ValueTask.FromResult<RebuildOutcome>(new RebuildOutcome.Refused("a fault-safety test keeps the suspect copy"));
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("terrabase-rebuild-fault-safety-").FullName;
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
