using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Engine.Turso;
using TerraBase.Features.Replication;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

/// <summary>
/// The production half of the A6 rebuild on real hosts: the engine's node-local copy-fault ledger in the system database
/// (<see cref="RebuildLedger"/>), its fail-closed write intents across a real process crash, and the hosted
/// <see cref="RebuildRecovery"/> worker draining it through the ledger's capacity-1 wake and its retry tick. Every host's
/// rebuild is a test double (it records which records the worker admitted, and rebuilds nothing); where a test watches the
/// worker, a pass-through observer notes its passes and waits on the real ledger. Certified refill remains refused until
/// A6 PR2 integrates A4/A5, so nothing here claims a certified rebuild.
/// </summary>
public sealed class A6ProductionRebuildTests
{
    private const string CrashRoot = "TERRABASE_TEST_A6_CRASH_ROOT";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);
    private static readonly string[] SentinelRegions = ["gru"];

    [Test]
    public async Task A_replayed_operation_journal_preserves_the_already_committed_fault()
    {
        using var directory = new TempDirectory();
        var identity = Path.Combine(directory.Root, ReplicaIdentityFile.FileName);
        var journal = Path.Combine(directory.Root, TursoStorageEngine.IntentName + ".db");
        TenantContext database;
        Ulid incarnation;
        byte[] running;
        string path;
        await using (var first = await StartAsync(directory.Root))
        {
            (database, incarnation) = await CreateDatabaseAsync(first);
            await DurableWriteAsync(first, database);
            var firstEngine = first.Server.Services.GetRequiredService<TursoStorageEngine>();
            var store = (TursoDatabaseStore)(await firstEngine.OpenDatabaseAsync(database, CancellationToken.None))!;
            _ = await store.BeginOperationAsync(CancellationToken.None);
            path = store.FilePath;
            running = await File.ReadAllBytesAsync(identity);
        }
        var priorJournal = await File.ReadAllBytesAsync(journal);
        await File.WriteAllBytesAsync(identity, running);
        RebuildRecord? fault;
        // A page kept cached (mapped) leaves the copy unproven, so its intent faults it.
        using (var map = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read))
        using (var view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read))
        {
            _ = view.ReadByte(0);
            await using var second = await StartAsync(directory.Root);
            fault = await second.Server.Services.GetRequiredService<IRebuildLedger>().GetAsync(database, CancellationToken.None);
            await Assert.That(fault?.Phase).IsEqualTo(RebuildPhase.Pending);
            await Assert.That(fault?.OldIncarnation).IsEqualTo(incarnation);
            running = await File.ReadAllBytesAsync(identity);
        }

        // Model a crash after SYSTEM committed the fault but before the journal's DELETE became durable.
        await File.WriteAllBytesAsync(journal, priorJournal);
        await File.WriteAllBytesAsync(identity, running);
        await using var third = await StartAsync(directory.Root);
        var services = third.Server.Services;
        await Assert.That(await services.GetRequiredService<IRebuildLedger>().GetAsync(database, CancellationToken.None)).IsEqualTo(fault);
        var engine = services.GetRequiredService<TursoStorageEngine>();
        var remaining = await engine.IntentStore!.WithWriterAsync(session =>
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM _tb_write_intent;";
            return (long)command.ExecuteScalar()!;
        }, "checking journal replay", CancellationToken.None);
        await Assert.That(remaining).IsEqualTo(0);
        await Assert.That(await services.GetRequiredService<IReplicaCopyFaults>().RefusedAsync(database, incarnation, CancellationToken.None)).IsTrue();
    }

    [Test]
    public async Task A_copy_intent_does_not_wait_for_the_catalog_writer()
    {
        await using var api = await StartAsync(directory: null);
        var (database, _) = await CreateDatabaseAsync(api);
        var engine = api.Server.Services.GetRequiredService<TursoStorageEngine>();
        var store = (TursoDatabaseStore)(await engine.OpenDatabaseAsync(database, CancellationToken.None))!;
        var system = await engine.OpenSystemDatabaseAsync(CancellationToken.None);
        Task recording = Task.CompletedTask;
        try
        {
            await using var held = await system.BeginDurableTransactionAsync(CancellationToken.None);
            recording = engine.Ledger.RecordIntentAsync(store, CancellationToken.None);
            await recording.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            // Dispose the held writer before waiting for a recorder that the regression blocked.
            await recording;
        }
    }

    [Test]
    public async Task A_copy_fault_record_stays_on_its_node_and_never_reaches_a_peer()
    {
        await using var pair = await Pair.StartAsync();
        var gruIncarnation = await pair.Gru.IncarnationAsync();
        var sjcIncarnation = await pair.Sjc.IncarnationAsync();

        await pair.Sjc.Faults.RecordAsync(pair.Database, sjcIncarnation, "a test fault", CancellationToken.None);
        // A catalog write after the fault, through the same system database and peer stream: once gru has it, a captured
        // ledger row written before it would have reached gru too.
        var sentinel = await pair.Sjc.CreateSentinelAsync();
        await TerraBaseTestServer.EventuallyAsync(async token =>
            await pair.Gru.Api.Server.Services.GetRequiredService<ITenantDirectory>()
                .FindDatabaseAsync(pair.Database.OrganizationId, sentinel, "sentinel", token) is TenantContext, Deadline);

        await Assert.That(await pair.Sjc.Faults.RefusedAsync(pair.Database, sjcIncarnation, CancellationToken.None)).IsTrue();
        await Assert.That(await PendingAsync(pair.Sjc.Api, pair.Database)).IsNotNull();
        await Assert.That(await PendingAsync(pair.Gru.Api, pair.Database)).IsNull().Because("the ledger is node-local, never captured");
        await Assert.That(await pair.Gru.Faults.RefusedAsync(pair.Database, gruIncarnation, CancellationToken.None)).IsFalse();
        await DurableWriteAsync(pair.Gru.Api, pair.Database);
        _ = await Assert.ThrowsAsync<ReplicaCopyFaultedException>(async () => await DurableWriteAsync(pair.Sjc.Api, pair.Database));
    }

    [Test]
    public async Task A_persisted_copy_fault_is_resumed_at_startup_and_retried_on_each_tick_while_a_clean_restart_faults_nothing()
    {
        using var directory = new TempDirectory();
        TenantContext faulted, clean;
        Ulid incarnation;
        await using (var first = await StartAsync(directory.Root))
        {
            (faulted, incarnation) = await CreateDatabaseAsync(first);
            (clean, _) = await CreateDatabaseAsync(first);
            await DurableWriteAsync(first, faulted);
            await DurableWriteAsync(first, clean);
            await first.Server.Services.GetRequiredService<IReplicaCopyFaults>().RecordAsync(faulted, incarnation, "a test fault", CancellationToken.None);
        }

        // A clean stop and a fresh process on the same volume: the ledger row is all that carries the work over.
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var probe = new Probe();
        Observed? observed = null;
        await using var api = await StartAsync(directory.Root, probe, time, services => Observe(services, created => observed = created));

        var idle = await observed!.IdleAsync(0);
        await Assert.That(probe.Calls.Count).IsGreaterThanOrEqualTo(1).Because("startup admits the persisted record");
        await Assert.That(probe.Calls.Count).IsEqualTo(observed.Passes).Because("each pass admits the one pending record once");
        await Assert.That(probe.Calls.All(call => call == (faulted.DatabaseId!, incarnation))).IsTrue();
        var record = await api.Server.Services.GetRequiredService<IRebuildLedger>().GetAsync(faulted, CancellationToken.None);
        await Assert.That(record?.Phase).IsEqualTo(RebuildPhase.Pending);
        await Assert.That(record?.OldIncarnation).IsEqualTo(incarnation);
        _ = await Assert.ThrowsAsync<ReplicaCopyFaultedException>(async () => await DurableWriteAsync(api, faulted));
        await Assert.That(await PendingAsync(api, clean)).IsNull().Because("a clean stop turns no write intent into a fault");
        await DurableWriteAsync(api, clean);

        // A refused rebuild stays pending; each tick, and nothing else, admits it again: no fault and no wake meanwhile.
        for (var tick = 1; tick <= 2; tick++)
        {
            var (calls, passes) = (probe.Calls.Count, observed.Passes);
            time.Advance(TimeSpan.FromSeconds(61));
            var next = await observed.IdleAsync(idle + 1);
            await Assert.That(next).IsEqualTo(idle + 1).Because("a tick's pass ends at an empty wake");
            await Assert.That(observed.Passes).IsEqualTo(passes + 1);
            await Assert.That(probe.Calls.Count).IsEqualTo(calls + 1);
            idle = next;
        }

        await Assert.That(probe.Calls.All(call => call == (faulted.DatabaseId!, incarnation))).IsTrue();
        await Assert.That(await PendingAsync(api, faulted)).IsNotNull();
    }

    [Test]
    public async Task Copy_faults_recorded_while_the_worker_is_busy_coalesce_into_one_more_pass()
    {
        // The clock never moves: no tick, so every pass here is a wake the real ledger buffered.
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new Probe { Hold = hold };
        Observed? observed = null;
        await using var api = await StartAsync(directory: null, probe, time, services => Observe(services, created => observed = created));
        var faults = api.Server.Services.GetRequiredService<IReplicaCopyFaults>();
        var databases = new TenantContext[7];
        var incarnations = new Ulid[7];
        for (var index = 0; index < databases.Length; index++)
        {
            (databases[index], incarnations[index]) = await CreateDatabaseAsync(api);
        }
        var idle = await observed!.IdleAsync(0);
        var passes = observed.Passes;

        await faults.RecordAsync(databases[0], incarnations[0], "a test fault", CancellationToken.None);
        await probe.Entered.Task.WaitAsync(Deadline);
        // Five more durable faults while the worker holds the first; none waits for the worker.
        for (var index = 1; index < 6; index++)
        {
            await faults.RecordAsync(databases[index], incarnations[index], "a test fault", CancellationToken.None);
        }

        hold.SetResult();
        var drained = await observed.IdleAsync(idle + 1);
        // The held pass answered busy; the next wait found exactly one buffered wake, and the one after it none.
        await Assert.That(observed.Waits[idle + 1]).IsTrue().Because("the five faults left a wake");
        await Assert.That(drained).IsEqualTo(idle + 2).Because("five wakes during one pass coalesce into one");
        await Assert.That(observed.Passes).IsEqualTo(passes + 2);
        await Assert.That(probe.Calls.Count).IsEqualTo(1 + 6);

        await faults.RecordAsync(databases[6], incarnations[6], "a test fault", CancellationToken.None);
        await Assert.That(await observed.IdleAsync(drained + 1)).IsEqualTo(drained + 1);
        await Assert.That(observed.Passes).IsEqualTo(passes + 3);

        var counts = probe.Calls.GroupBy(call => call.Database).ToDictionary(group => group.Key, group => group.Count());
        await Assert.That(probe.Calls.Count).IsEqualTo(14);
        await Assert.That(counts[databases[0].DatabaseId!]).IsEqualTo(3);
        foreach (var database in databases[1..6])
        {
            await Assert.That(counts[database.DatabaseId!]).IsEqualTo(2);
        }

        await Assert.That(counts[databases[6].DatabaseId!]).IsEqualTo(1);
        await Assert.That(probe.Calls.ToHashSet().SetEquals(databases.Select((database, index) => (database.DatabaseId!, incarnations[index])))).IsTrue();
    }

    [Test]
    public async Task A_copy_intent_is_retired_only_after_its_last_active_operation_finishes()
    {
        await using var api = await StartAsync(directory: null);
        var (database, _) = await CreateDatabaseAsync(api);
        await DurableWriteAsync(api, database);
        var engine = api.Server.Services.GetRequiredService<TursoStorageEngine>();
        var store = await engine.OpenDatabaseAsync(database, CancellationToken.None)
            ?? throw new InvalidOperationException("the test copy is missing");
        async Task<bool> IntentAsync()
        {
            return await engine.IntentStore!.WithWriterAsync(session =>
            {
                using var command = session.Connection.CreateCommand();
                command.CommandText = "SELECT 1 FROM _tb_write_intent WHERE database = $database;";
                command.Parameters.AddWithValue("$database", database.DatabaseId);
                return command.ExecuteScalar() is not null;
            }, "checking operation protection", CancellationToken.None);
        }
        await Assert.That(await IntentAsync()).IsFalse();
        await using var tap = (TursoChangeTap)(await engine.OpenTapAsync(database, CancellationToken.None))!;
        await DurableWriteAsync(api, database);
        tap.BeforeTrim = _ => throw new IOException("backup destination failed after reading");
        _ = await Assert.ThrowsAsync<IOException>(async () => await tap.ReadAsync(Stream.Null, CancellationToken.None));
        tap.BeforeTrim = null;
        await Assert.That(await IntentAsync()).IsFalse().Because("an idle tap and a clean output failure leave no native work");
        await using (var read = await store.BeginTransactionAsync(write: false, CancellationToken.None))
        {
            await Assert.That(await IntentAsync()).IsFalse().Because("a read-only transaction cannot leave unsynced pages behind");
        }

        var sqlite = (TursoDatabaseStore)store;
        var first = await sqlite.PrepareCopyAsync(CancellationToken.None) ?? throw new InvalidOperationException("no ledger");
        var second = await sqlite.PrepareCopyAsync(CancellationToken.None) ?? throw new InvalidOperationException("no ledger");
        await Assert.That(await IntentAsync()).IsTrue();
        first.Complete();
        await first.DisposeAsync();
        await Assert.That(await IntentAsync()).IsTrue().Because("the other native operation is still active");
        second.Complete();
        await second.DisposeAsync();
        await Assert.That(await IntentAsync()).IsFalse();
    }

    // FlagAsync writes the mark without the ledger's gate, so an operation that starts later must not turn it back into an
    // ordinary intent the restart proof would clear.
    [Test]
    public async Task A_faulting_mark_outlives_a_later_operation_of_the_same_copy()
    {
        await using var api = await StartAsync(directory: null);
        var (database, _) = await CreateDatabaseAsync(api);
        var engine = api.Server.Services.GetRequiredService<TursoStorageEngine>();
        var store = (TursoDatabaseStore)(await engine.OpenDatabaseAsync(database, CancellationToken.None)
            ?? throw new InvalidOperationException("the test copy is missing"));
        Task<long?> ReplicaAsync()
        {
            return engine.IntentStore!.WithWriterAsync(session =>
            {
                using var command = session.Connection.CreateCommand();
                command.CommandText = "SELECT replica FROM _tb_write_intent WHERE database = $database;";
                command.Parameters.AddWithValue("$database", database.DatabaseId);
                return command.ExecuteScalar() as long?;
            }, "checking the faulting mark", CancellationToken.None);
        }
        store.NoteCopyFault("a test fault");
        await store.Flagged;
        await Assert.That(await ReplicaAsync()).IsEqualTo(0L);
        var operation = await store.BeginOperationAsync(CancellationToken.None) ?? throw new InvalidOperationException("no ledger");
        operation.Complete();
        await operation.DisposeAsync();
        await Assert.That(await ReplicaAsync()).IsEqualTo(0L).Because("only the durable record turns a mark ordinary");
    }

    [Test]
    public async Task A_locally_acknowledged_idle_copy_survives_a_process_crash_without_rebuild()
    {
        const string step = "acknowledged idle copy";
        if (Environment.GetEnvironmentVariable(CrashRoot) is { } childRoot)
        {
            await using var child = await StartAsync(Path.Combine(childRoot, "data"));
            var (childDatabase, incarnation) = await CreateDatabaseAsync(child);
            var document = await DurableWriteAsync(child, childDatabase);
            var engine = child.Server.Services.GetRequiredService<TursoStorageEngine>();
            await File.WriteAllTextAsync(Path.Combine(childRoot, "crashed.json"), JsonSerializer.Serialize(new Crashed(
                childDatabase.OrganizationId, childDatabase.ProjectId!, childDatabase.DatabaseId!, incarnation.ToString(), document, engine.ReplicaId.Epoch)));
            Environment.FailFast($"crashed at {step}");
            return;
        }

        using var root = new TempDirectory();
        var (exit, crashedAtStep, output) = await RunChildAsync(nameof(A_locally_acknowledged_idle_copy_survives_a_process_crash_without_rebuild), root.Root, step);
        await Assert.That(exit).IsNotEqualTo(0).Because(output);
        await Assert.That(crashedAtStep).IsTrue().Because(output);
        var crashed = JsonSerializer.Deserialize<Crashed>(await File.ReadAllTextAsync(Path.Combine(root.Root, "crashed.json")))!;
        var database = new TenantContext(crashed.Organization, PlanTier.Hobby, crashed.Project, crashed.Database);
        await using var api = await StartAsync(Path.Combine(root.Root, "data"));
        var services = api.Server.Services;
        await Assert.That(await services.GetRequiredService<IRebuildLedger>().GetAsync(database, CancellationToken.None)).IsNull()
            .Because("a completed FULL commit and cleanup leave no interrupted storage operation");
        var store = await services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, CancellationToken.None)
            ?? throw new InvalidOperationException("the acknowledged copy is missing");
        await Assert.That(store.Incarnation).IsEqualTo(Ulid.Parse(crashed.Incarnation));
        await Assert.That(await services.GetRequiredService<IReplicaCopyFaults>().RefusedAsync(database, store.Incarnation, CancellationToken.None)).IsFalse();
        await using var read = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        await Assert.That(await read.GetAsync("items", crashed.Document, CancellationToken.None)).IsNotNull();
    }

    [Test]
    public Task A_crash_before_the_fault_record_is_durable_faults_the_copy_from_its_write_intent_at_the_next_start() =>
        CrashAsync(nameof(A_crash_before_the_fault_record_is_durable_faults_the_copy_from_its_write_intent_at_the_next_start), RebuildLedger.Recording,
            reason => reason.Contains("uncleanly", StringComparison.Ordinal));

    [Test]
    public Task A_crash_after_the_fault_record_is_durable_keeps_that_record_at_the_next_start() =>
        CrashAsync(nameof(A_crash_after_the_fault_record_is_durable_keeps_that_record_at_the_next_start), RebuildLedger.Recorded,
            reason => reason == "a test fault");

    // A crash while ordinary writes are in flight (F1): the copy's FULL log is all that remains once the kernel caches none of
    // its pages, so the next start serves every acknowledged write from it, with no rebuild, and drops the uncommitted one.
    [Test]
    public async Task A_copy_written_when_the_process_crashes_serves_every_acknowledged_write_without_rebuild()
    {
        using var root = new TempDirectory();
        var (crashed, written) = await WritingCrashAsync(nameof(A_copy_written_when_the_process_crashes_serves_every_acknowledged_write_without_rebuild), root.Root);
        var data = Path.Combine(root.Root, "data");
        var database = new TenantContext(crashed.Organization, PlanTier.Hobby, crashed.Project, crashed.Database);
        await using var api = await StartAsync(data);
        var services = api.Server.Services;
        await Assert.That((int)services.GetRequiredService<TursoStorageEngine>().ReplicaId.Epoch).IsGreaterThan(crashed.Epoch).Because("the crash ended the process uncleanly");
        await Assert.That(await services.GetRequiredService<IRebuildLedger>().GetAsync(database, CancellationToken.None)).IsNull()
            .Because("an ordinary write's intent does not fault a copy its own log recovers");
        var store = await services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, CancellationToken.None)
            ?? throw new InvalidOperationException("the written copy is missing");
        await Assert.That(store.Incarnation).IsEqualTo(Ulid.Parse(crashed.Incarnation));
        await Assert.That(await services.GetRequiredService<IReplicaCopyFaults>().RefusedAsync(database, store.Incarnation, CancellationToken.None)).IsFalse();
        await using (var read = await store.BeginTransactionAsync(write: false, CancellationToken.None))
        {
            foreach (var id in written.Acknowledged)
            {
                await Assert.That(await read.GetAsync("items", id, CancellationToken.None)).IsNotNull().Because($"{id} was acknowledged");
            }
            await Assert.That(await read.GetAsync("items", written.Uncommitted, CancellationToken.None)).IsNull();
        }
        _ = await DurableWriteAsync(api, database);
    }

    // The fail-closed side of the same crash: a page of the copy the kernel keeps cached (here, mapped) could be one a failed
    // sync never wrote, so the copy faults.
    [Test]
    public async Task A_crashed_copy_with_a_page_still_cached_is_faulted()
    {
        using var root = new TempDirectory();
        var (crashed, written) = await WritingCrashAsync(nameof(A_crashed_copy_with_a_page_still_cached_is_faulted), root.Root);
        var data = Path.Combine(root.Root, "data");
        var database = new TenantContext(crashed.Organization, PlanTier.Hobby, crashed.Project, crashed.Database);
        using var map = MemoryMappedFile.CreateFromFile(written.Path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        _ = view.ReadByte(0);
        await using var api = await StartAsync(data);
        var record = await api.Server.Services.GetRequiredService<IRebuildLedger>().GetAsync(database, CancellationToken.None);
        await Assert.That(record?.Phase).IsEqualTo(RebuildPhase.Pending);
        await Assert.That(record?.OldIncarnation).IsEqualTo(Ulid.Parse(crashed.Incarnation));
        var reason = await ReasonAsync(api.Server.Services.GetRequiredService<TursoStorageEngine>(), database);
        await Assert.That(reason?.Contains("uncleanly", StringComparison.Ordinal) == true).IsTrue().Because($"the record's reason: {reason}");
        _ = await Assert.ThrowsAsync<ReplicaCopyFaultedException>(async () => await DurableWriteAsync(api, database));
    }

    // The child acknowledges durable writes, then dies with one more in flight (its transaction open, so its intent stands).
    private static async Task<(Crashed Crashed, Written Written)> WritingCrashAsync(string test, string root)
    {
        const string step = "writes in flight";
        if (Environment.GetEnvironmentVariable(CrashRoot) is { } childRoot)
        {
            await using var child = await StartAsync(Path.Combine(childRoot, "data"));
            var (database, incarnation) = await CreateDatabaseAsync(child);
            var acknowledged = new List<string>();
            for (var i = 0; i < 20; i++)
            {
                acknowledged.Add(await DurableWriteAsync(child, database));
            }
            var engine = child.Server.Services.GetRequiredService<TursoStorageEngine>();
            var store = (TursoDatabaseStore)(await engine.OpenDatabaseAsync(database, CancellationToken.None))!;
            var uncommitted = EntityIds.New().ToString();
            await using var transaction = await store.BeginDurableTransactionAsync(CancellationToken.None);
            _ = await transaction.PutAsync("items", uncommitted, Encoding.UTF8.GetBytes("""{"n":2}"""), CancellationToken.None);
            if (!await engine.IntentStore!.WithWriterAsync(session =>
            {
                using var command = session.Connection.CreateCommand();
                command.CommandText = "SELECT 1 FROM _tb_write_intent WHERE database = $database;";
                command.Parameters.AddWithValue("$database", database.DatabaseId);
                return command.ExecuteScalar() is not null;
            }, "checking the in-flight intent", CancellationToken.None))
            {
                throw new InvalidOperationException("the in-flight write left no intent");
            }
            await File.WriteAllTextAsync(Path.Combine(childRoot, "crashed.json"), JsonSerializer.Serialize(new Crashed(
                database.OrganizationId, database.ProjectId!, database.DatabaseId!, incarnation.ToString(), acknowledged[^1], engine.ReplicaId.Epoch)));
            await File.WriteAllTextAsync(Path.Combine(childRoot, "written.json"), JsonSerializer.Serialize(new Written([.. acknowledged], uncommitted, store.FilePath)));
            Environment.FailFast($"crashed at {step}");
        }

        var (exit, crashedAtStep, output) = await RunChildAsync(test, root, step);
        await Assert.That(exit).IsNotEqualTo(0).Because(output);
        await Assert.That(crashedAtStep).IsTrue().Because(output);
        return (JsonSerializer.Deserialize<Crashed>(await File.ReadAllTextAsync(Path.Combine(root, "crashed.json")))!,
            JsonSerializer.Deserialize<Written>(await File.ReadAllTextAsync(Path.Combine(root, "written.json")))!);
    }

    // The salvaged replacement's own write intent, not the original's, decides the restart: had the intent kept the first
    // incarnation written in the process, recovery would find the original already faulted and keep the replacement salvaged.
    [Test]
    public Task A_crash_before_a_replacement_fault_is_durable_faults_the_salvaged_replacement_from_its_own_write_intent() =>
        ReplacementCrashAsync(nameof(A_crash_before_a_replacement_fault_is_durable_faults_the_salvaged_replacement_from_its_own_write_intent), RebuildLedger.Recording);

    // A genuinely partial copy: the uncertified replacement keeps its ordinary intent across idle gaps, so a crash with no fault
    // being recorded still faults it, however clean its pages.
    [Test]
    public Task A_crash_during_an_uncertified_rebuild_faults_the_replacement_from_its_ordinary_write_intent() =>
        ReplacementCrashAsync(nameof(A_crash_during_an_uncertified_rebuild_faults_the_replacement_from_its_ordinary_write_intent), "salvaged replacement");

    private static async Task ReplacementCrashAsync(string test, string step)
    {
        if (Environment.GetEnvironmentVariable(CrashRoot) is { } childRoot)
        {
            await ReplacementCrashChildAsync(childRoot, step);
            return;
        }

        using var root = new TempDirectory();
        var data = Directory.CreateDirectory(Path.Combine(root.Root, "data")).FullName;
        var (exit, crashedAtStep, output) = await RunChildAsync(test, root.Root, step);
        await Assert.That(exit).IsNotEqualTo(0).Because(output);
        await Assert.That(crashedAtStep).IsTrue().Because(output);
        var crashed = JsonSerializer.Deserialize<Crashed>(await File.ReadAllTextAsync(Path.Combine(root.Root, "crashed.json")))!;
        var database = new TenantContext(crashed.Organization, PlanTier.Hobby, crashed.Project, crashed.Database);
        var (original, replacement) = (Ulid.Parse(crashed.Incarnation), Ulid.Parse(crashed.Replacement!));

        await using var api = await StartAsync(data);
        var services = api.Server.Services;
        var engine = services.GetRequiredService<TursoStorageEngine>();
        var storage = services.GetRequiredService<IRebuildStorage>();
        await Assert.That((int)engine.ReplicaId.Epoch).IsGreaterThan(crashed.Epoch).Because("the crash ended the process uncleanly");
        var record = await services.GetRequiredService<IRebuildLedger>().GetAsync(database, CancellationToken.None);
        await Assert.That(record?.Phase).IsEqualTo(RebuildPhase.Pending).Because("the replacement's intent faults it, so it is no longer salvaged");
        await Assert.That(record?.OldIncarnation).IsEqualTo(original);
        await Assert.That(record?.NewIncarnation).IsNull();
        await Assert.That(record?.AdditionalIncarnation).IsEqualTo(replacement).Because("the uncertified replacement is kept for the salvage");
        var recorded = await ReasonAsync(engine, database);
        await Assert.That(recorded?.Contains("uncleanly", StringComparison.Ordinal) == true).IsTrue().Because($"the record's reason: {recorded}");

        await Assert.That(await storage.IncarnationAsync(database, CancellationToken.None)).IsEqualTo(replacement);
        await Assert.That(await services.GetRequiredService<IReplicaCopyFaults>().RefusedAsync(database, replacement, CancellationToken.None)).IsTrue();
        _ = await Assert.ThrowsAsync<ReplicaCopyFaultedException>(async () => await DurableWriteAsync(api, database));
        var side = storage.SidePath(database, original);
        await Assert.That(storage.SideExists(side)).IsTrue();
        await Assert.That(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(side)))).IsEqualTo(crashed.Side).Because("the original copy stays aside, untouched");
        var repair = services.GetRequiredService<IRebuildRepair>();
        await Assert.That(await repair.RepairRebuildAsync(database, replacement, await SideStatesAsync(storage, side), CancellationToken.None)).IsNull()
            .Because("the faulted replacement accepts no more repairs in place");
        await Assert.That(await repair.RecordRefillAsync(database, replacement, "a6-source", Ulid.NewUlid(), 1, default, CancellationToken.None)).IsFalse();
        var store = await engine.OpenDatabaseAsync(database, CancellationToken.None) ?? throw new InvalidOperationException("the database is missing");
        await using var reading = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        await Assert.That(await reading.GetAsync("items", crashed.Document, CancellationToken.None)).IsNotNull().Because("the repair committed before the crash");
    }

    // Original written and faulted, swapped, the replacement repaired from the side copy, refilled and salvaged; then its own
    // fault dies before its FULL record, or (any other step) the process dies with the replacement salvaged.
    private static async Task ReplacementCrashChildAsync(string root, string step)
    {
        // Set only after the original's fault is recorded: the checkpoint never reads the host.
        var armed = (string?)null;
        await using var api = await StartAsync(Path.Combine(root, "data"), configure: services => services.AddSingleton<DatabaseLifecycleCheckpoint>((name, database) =>
        {
            if (name == RebuildLedger.Recording && database.DatabaseId == Volatile.Read(ref armed))
            {
                Environment.FailFast($"crashed at {RebuildLedger.Recording}");
            }
        }));
        var services = api.Server.Services;
        var storage = services.GetRequiredService<IRebuildStorage>();
        var ledger = services.GetRequiredService<IRebuildLedger>();
        var faults = services.GetRequiredService<IReplicaCopyFaults>();
        var repair = services.GetRequiredService<IRebuildRepair>();
        var (database, original) = await CreateDatabaseAsync(api);
        var document = await DurableWriteAsync(api, database);
        await faults.RecordAsync(database, original, "a test fault", CancellationToken.None);

        var pending = await ledger.GetAsync(database, CancellationToken.None) ?? throw new InvalidOperationException("the original fault is not recorded");
        var side = storage.SidePath(database, original);
        var replacement = await storage.SwapAsync(database, original, side, CancellationToken.None)
            ?? throw new InvalidOperationException("the test copy did not swap");
        var swapped = pending with { SidePath = side, Phase = RebuildPhase.Swapped, NewIncarnation = replacement };
        await ledger.SaveAsync(swapped, CancellationToken.None);
        if (await repair.RepairRebuildAsync(database, replacement, await SideStatesAsync(storage, side), CancellationToken.None) is not > 0)
        {
            throw new InvalidOperationException("the repair wrote nothing into the replacement");
        }

        // A stand-in source: the proof is what Salvaged requires, and no certificate follows.
        if (!await repair.RecordRefillAsync(database, replacement, "a6-source", Ulid.NewUlid(), 1, default, CancellationToken.None))
        {
            throw new InvalidOperationException("the refill proof was refused");
        }

        await ledger.SaveAsync(swapped with { Phase = RebuildPhase.Salvaged }, CancellationToken.None);
        var engine = services.GetRequiredService<TursoStorageEngine>();
        await File.WriteAllTextAsync(Path.Combine(root, "crashed.json"), JsonSerializer.Serialize(new Crashed(
            database.OrganizationId, database.ProjectId!, database.DatabaseId!, original.ToString(), document, engine.ReplicaId.Epoch,
            replacement.ToString(), Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(side))))));
        if (step != RebuildLedger.Recording)
        {
            Environment.FailFast($"crashed at {step}");
        }
        Volatile.Write(ref armed, database.DatabaseId);
        await faults.RecordAsync(database, replacement, "an uncertain replacement", CancellationToken.None);
        throw new InvalidOperationException($"the child outlived {RebuildLedger.Recording}");
    }

    // Every state of the side copy, read the way the salvage reads it: from a working copy, never the side copy itself.
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

    // The parent runs the test again in a child process that dies (FailFast: no disposal, no host stop) at `step` inside
    // RecordAsync after a durable write; then a fresh host on the child's volume must refuse the copy.
    private static async Task CrashAsync(string test, string step, Func<string, bool> reason)
    {
        if (Environment.GetEnvironmentVariable(CrashRoot) is { } childRoot)
        {
            await CrashChildAsync(childRoot, step);
            return;
        }

        using var root = new TempDirectory();
        var data = Directory.CreateDirectory(Path.Combine(root.Root, "data")).FullName;
        var (exit, crashedAtStep, output) = await RunChildAsync(test, root.Root, step);
        await Assert.That(exit).IsNotEqualTo(0).Because(output);
        await Assert.That(crashedAtStep).IsTrue().Because(output);
        var crashed = JsonSerializer.Deserialize<Crashed>(await File.ReadAllTextAsync(Path.Combine(root.Root, "crashed.json")))!;
        var database = new TenantContext(crashed.Organization, PlanTier.Hobby, crashed.Project, crashed.Database);

        await using var api = await StartAsync(data);
        var engine = api.Server.Services.GetRequiredService<TursoStorageEngine>();
        await Assert.That((int)engine.ReplicaId.Epoch).IsGreaterThan(crashed.Epoch).Because("the crash ended the process uncleanly");
        var record = await api.Server.Services.GetRequiredService<IRebuildLedger>().GetAsync(database, CancellationToken.None);
        await Assert.That(record?.Phase).IsEqualTo(RebuildPhase.Pending);
        await Assert.That(record?.OldIncarnation).IsEqualTo(Ulid.Parse(crashed.Incarnation));
        var recorded = await ReasonAsync(engine, database);
        await Assert.That(recorded is not null && reason(recorded)).IsTrue().Because($"the record's reason: {recorded}");
        await Assert.That(await api.Server.Services.GetRequiredService<IReplicaCopyFaults>().RefusedAsync(database, record!.OldIncarnation, CancellationToken.None)).IsTrue();
        _ = await Assert.ThrowsAsync<ReplicaCopyFaultedException>(async () => await DurableWriteAsync(api, database));
        var store = await engine.OpenDatabaseAsync(database, CancellationToken.None) ?? throw new InvalidOperationException("the database is missing");
        await using var reading = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        await Assert.That(await reading.GetAsync("items", crashed.Document, CancellationToken.None)).IsNotNull().Because("a faulted copy still serves reads");
    }

    private static async Task CrashChildAsync(string root, string step)
    {
        var target = (TenantContext?)null;
        await using var api = await StartAsync(Path.Combine(root, "data"), configure: services => services.AddSingleton<DatabaseLifecycleCheckpoint>((name, database) =>
        {
            if (name == step && database.DatabaseId == Volatile.Read(ref target)?.DatabaseId)
            {
                Environment.FailFast($"crashed at {step}");
            }
        }));
        var (database, incarnation) = await CreateDatabaseAsync(api);
        var document = await DurableWriteAsync(api, database);
        var engine = api.Server.Services.GetRequiredService<TursoStorageEngine>();
        await File.WriteAllTextAsync(Path.Combine(root, "crashed.json"), JsonSerializer.Serialize(new Crashed(
            database.OrganizationId, database.ProjectId!, database.DatabaseId!, incarnation.ToString(), document, engine.ReplicaId.Epoch)));
        Volatile.Write(ref target, database);
        await api.Server.Services.GetRequiredService<IReplicaCopyFaults>().RecordAsync(database, incarnation, "a test fault", CancellationToken.None);
        throw new InvalidOperationException($"the child outlived {step}");
    }

    // Runs `test` again in a child of the test executable with CrashRoot set; its exit code, whether it failed fast at
    // `step`, and its output tail.
    private static async Task<(int ExitCode, bool CrashedAtStep, string Output)> RunChildAsync(string test, string root, string step)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("the test executable path is unavailable");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(Path.GetFullPath(Environment.GetCommandLineArgs()[0]));
        }

        var type = typeof(A6ProductionRebuildTests);
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
        // The child's own temporary files stay under root, which the crash leaves behind and the parent deletes.
        start.Environment["TMPDIR"] = Directory.CreateDirectory(Path.Combine(root, "tmp")).FullName;
        using var child = Process.Start(start) ?? throw new InvalidOperationException("the crash child did not start");
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

        // FailFast writes its message to stderr before the stack, which alone can outgrow the tail: look for it in the whole stream.
        var (stdout, stderr) = (await output, await error);
        var text = stdout + stderr;
        return (child.ExitCode, stderr.Contains($"crashed at {step}", StringComparison.Ordinal), text.Length > 16384 ? text[^16384..] : text);
    }

    private static Task<ManagementApi> StartAsync(string? directory, Probe? probe = null, TimeProvider? time = null, Action<IServiceCollection>? configure = null) =>
        ManagementApi.StartAsync(settings =>
        {
            if (directory is not null)
            {
                settings["data-directory"] = directory;
            }
        }, services =>
        {
            // Every host's rebuild is a probe, so the production worker preserves each copy under test in place.
            services.AddSingleton<IReplicaRebuild>(probe ?? new Probe());
            configure?.Invoke(services);
        }, time);

    // Puts an observer of the real ledger's recovery seam where the worker resolves it.
    private static void Observe(IServiceCollection services, Action<Observed> created) =>
        services.AddSingleton<IRebuildRecovery>(provider =>
        {
            var observed = new Observed(provider.GetRequiredService<TursoStorageEngine>().Ledger);
            created(observed);
            return observed;
        });

    private static async Task<(TenantContext Database, Ulid Incarnation)> CreateDatabaseAsync(ManagementApi api)
    {
        var managed = await api.CreateManagedDatabaseAsync();
        var database = await api.Server.Services.GetRequiredService<ITenantDirectory>().FindDatabaseAsync(managed.Organization, managed.Project, managed.Name, CancellationToken.None)
            is TenantContext found ? found : throw new InvalidOperationException("the database is not registered");
        var store = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, CancellationToken.None)
            ?? throw new InvalidOperationException("the database is missing");
        return (database, store.Incarnation);
    }

    // A durable (replication-class) write of one new document, which records the copy's write intent first; its id.
    private static async Task<string> DurableWriteAsync(ManagementApi api, TenantContext database)
    {
        var store = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, CancellationToken.None)
            ?? throw new InvalidOperationException("the database is missing");
        var id = EntityIds.New().ToString();
        await using var transaction = await store.BeginDurableTransactionAsync(CancellationToken.None);
        _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
        _ = await transaction.PutAsync("items", id, Encoding.UTF8.GetBytes("""{"n":1}"""), CancellationToken.None);
        _ = await transaction.CommitAsync(CancellationToken.None);
        return id;
    }

    // The database's ledger row as the worker sees it, read from the system database page by page.
    private static async Task<RebuildRecord?> PendingAsync(ManagementApi api, TenantContext database)
    {
        // The engine's ledger itself, not the DI seam, which a test may observe.
        await foreach (var record in api.Server.Services.GetRequiredService<TursoStorageEngine>().Ledger.PendingAsync(CancellationToken.None))
        {
            if (record.Database.DatabaseId == database.DatabaseId)
            {
                return record;
            }
        }

        return null;
    }

    // The reason column of the database's ledger row, which RebuildRecord does not carry.
    private static async Task<string?> ReasonAsync(TursoStorageEngine engine, TenantContext database)
    {
        var system = (TursoDatabaseStore)await engine.OpenSystemDatabaseAsync(CancellationToken.None);
        return await system.WithWriterAsync(session =>
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = "SELECT reason FROM _tb_rebuild WHERE database = $database;";
            command.Parameters.AddWithValue("$database", database.DatabaseId);
            return command.ExecuteScalar() as string;
        }, "a test reading the rebuild ledger", CancellationToken.None);
    }

    private static TenantContext Tenant() => new(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());

    private sealed record Crashed(
        string Organization, string Project, string Database, string Incarnation, string Document, int Epoch, string? Replacement = null, string? Side = null);

    private sealed record Written(string[] Acknowledged, string Uncommitted, string Path);

    // Test double for the rebuild the worker admits: records each admission; the first may be held, then answers busy; every
    // other answers refused, so the ledger row stays pending exactly as a refused production rebuild leaves it.
    private sealed class Probe : IReplicaRebuild
    {
        private readonly ConcurrentQueue<(string Database, Ulid Incarnation)> _calls = new();

        public TaskCompletionSource? Hold { get; init; }

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<(string Database, Ulid Incarnation)> Calls => [.. _calls];

        public async ValueTask<RebuildOutcome> RebuildAsync(TenantContext database, Ulid faultedIncarnation, CancellationToken cancellationToken)
        {
            _calls.Enqueue((database.DatabaseId!, faultedIncarnation));
            if (Hold is { } hold && Entered.TrySetResult())
            {
                await hold.Task.WaitAsync(cancellationToken);
                return new RebuildOutcome.Busy();
            }

            return new RebuildOutcome.Refused("a test probe");
        }

        public Task UntilAsync(int count) => TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(_calls.Count >= count), Deadline);
    }

    // The real ledger's recovery seam, observed: every page and every wake still comes from the engine's ledger; this only
    // notes where the worker stands. A wait already complete when the worker asks for it found a buffered wake.
    private sealed class Observed(IRebuildRecovery ledger) : IRebuildRecovery
    {
        private readonly ConcurrentQueue<bool> _waits = new();
        private int _passes;

        public int Passes => Volatile.Read(ref _passes);

        // Per wait of the worker, in order: whether a wake was buffered.
        public IReadOnlyList<bool> Waits => [.. _waits];

        public IAsyncEnumerable<RebuildRecord> PendingAsync(CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _passes);
            return ledger.PendingAsync(cancellationToken);
        }

        public Task WaitAsync(CancellationToken cancellationToken)
        {
            var wait = ledger.WaitAsync(cancellationToken);
            _waits.Enqueue(wait.IsCompletedSuccessfully);
            return wait;
        }

        // The index of the first wait from `from` on that found no buffered wake: the worker is idle there.
        public async Task<int> IdleAsync(int from)
        {
            var found = -1;
            await TerraBaseTestServer.EventuallyAsync(_ =>
            {
                var waits = Waits;
                for (var index = from; index < waits.Count; index++)
                {
                    if (!waits[index])
                    {
                        found = index;
                        return Task.FromResult(true);
                    }
                }

                return Task.FromResult(false);
            }, Deadline);
            return found;
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("terrabase-a6-").FullName;

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    /// <summary>gru and sjc, each holding one database placed in both.</summary>
    private sealed class Pair : IAsyncDisposable
    {
        public FakeMemberStore Members { get; } = new();

        public string User { get; } = EntityIds.New().ToString();

        public TenantContext Database { get; } = Tenant();

        public Machine Gru { get; private set; } = null!;

        public Machine Sjc { get; private set; } = null!;

        public static async Task<Pair> StartAsync()
        {
            var pair = new Pair();
            try
            {
                pair.Gru = new Machine(pair, "gru", ["sjc"]);
                await pair.Gru.StartAsync();
                pair.Sjc = new Machine(pair, "sjc", ["gru"]);
                await pair.Sjc.StartAsync();
                return pair;
            }
            catch
            {
                await pair.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var machine in new[] { Sjc, Gru })
            {
                if (machine is not null)
                {
                    await machine.DisposeAsync();
                }
            }
        }
    }

    private sealed class Machine(Pair pair, string region, string[] replicas) : IAsyncDisposable
    {
        private readonly TempDirectory _directory = new();
        private ManagementApi? _api;

        public ManagementApi Api => _api ?? throw new InvalidOperationException($"{region} is stopped");

        public IReplicaCopyFaults Faults => Api.Server.Services.GetRequiredService<IReplicaCopyFaults>();

        public async Task StartAsync()
        {
            _api = await ManagementApi.StartAsync(settings =>
            {
                settings["region"] = region;
                settings["node-name"] = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())[..14];
                settings["data-directory"] = _directory.Root;
            }, services =>
            {
                services.AddSingleton<IMemberDirectory>(pair.Members);
                // No worker rebuilds the faulted copy under test.
                services.AddSingleton<IReplicaRebuild>(new Probe());
            }, userId: pair.User);
            var database = pair.Database;
            await CatalogFixture.RegisterAsync(Api, Api.User, database, "main", replicas);
        }

        public async Task<Ulid> IncarnationAsync()
        {
            return await Api.Server.Services.GetRequiredService<TursoStorageEngine>().IncarnationAsync(pair.Database, CancellationToken.None)
                ?? throw new InvalidOperationException($"the database is missing on {region}");
        }

        // A new project with one database placed on gru: a catalog write after whatever came before it on this node; its id.
        public async Task<string> CreateSentinelAsync()
        {
            var organization = pair.Database.OrganizationId;
            var project = await Api.CreateProjectAsync(organization, "sentinel-" + EntityIds.New());
            using var response = await Api.Client.PostAsJsonAsync(ManagementApi.Uri($"/v1/management/organizations/{organization}/projects/{project}/databases"),
                new { name = "sentinel", replicaRegions = SentinelRegions });
            response.EnsureSuccessStatusCode();
            return project;
        }

        public async ValueTask DisposeAsync()
        {
            if (_api is not null)
            {
                await _api.DisposeAsync();
                _api = null;
            }

            _directory.Dispose();
        }

    }
}
