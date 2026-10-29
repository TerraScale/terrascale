using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Cluster;
using TerraBase.Engine.Turso;
using TerraBase.Features.Memory;
using TerraBase.Features.Replication;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

/// <summary>
/// The R7 rebuild of a faulted copy into a new incarnation (docs/terrabase/decisions/0013a, sections 1, 5 and 9), on real
/// engines: the swap, the salvage of what only the faulted copy holds, refusals that keep the record and the side copy,
/// and resumption. A test-only adapter keeps the orchestration record in memory and uses merge refill, then records
/// its source proof and runs the production tenant completion barrier. These tests cover rebuild orchestration;
/// complete certified-refill coverage belongs to the certification integration tests.
/// </summary>
public sealed class ReplicaRebuildTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    [Test]
    public async Task A_faulted_copy_is_rebuilt_into_a_new_incarnation_keeping_a_state_only_it_held()
    {
        await using var pair = await Pair.StartAsync();
        var (kept, only) = await pair.SeedAsync();
        var faulted = await pair.Sjc.IncarnationAsync();
        var adapter = new StandIn(pair);

        var outcome = await pair.Sjc.Rebuild(adapter).RebuildAsync(pair.Database, faulted, CancellationToken.None);

        await Assert.That(outcome is RebuildOutcome.Completed).IsTrue().Because(outcome.ToString());
        var rebuilt = ((RebuildOutcome.Completed)outcome).NewIncarnation;
        await Assert.That(rebuilt).IsNotEqualTo(faulted);
        await Assert.That(await pair.Sjc.IncarnationAsync()).IsEqualTo(rebuilt);
        await Assert.That(await pair.Sjc.ReadAsync(kept)).IsEqualTo("""{"n":1}""");
        await Assert.That(await pair.Sjc.ReadAsync(only)).IsEqualTo("""{"n":2}""").Because("only the faulted copy held it; the source lacks it");
        await Assert.That(pair.Sjc.SideExists(faulted)).IsFalse();
        await Assert.That(adapter.Ledger.Get(pair.Database)).IsNull();
        await Assert.That(adapter.Certified).IsTrue().Because("only the completion barrier may end a rebuild");
        await Assert.That(File.Exists(pair.Sjc.SidePath(faulted) + ".salvage")).IsFalse();
    }

    [Test]
    public async Task A_rebuild_without_an_intact_source_is_refused_keeps_its_record_and_side_copy_and_resumes_later()
    {
        await using var pair = await Pair.StartAsync();
        var (kept, only) = await pair.SeedAsync();
        var faulted = await pair.Sjc.IncarnationAsync();
        var adapter = new StandIn(pair) { RefuseRefill = true };

        var refused = await pair.Sjc.Rebuild(adapter).RebuildAsync(pair.Database, faulted, CancellationToken.None);

        await Assert.That(refused is RebuildOutcome.Refused).IsTrue().Because(refused.ToString());
        await Assert.That(adapter.Ledger.Get(pair.Database)?.Phase).IsEqualTo(RebuildPhase.Swapped);
        await Assert.That(pair.Sjc.SideExists(faulted)).IsTrue();

        adapter.RefuseRefill = false;
        var completed = await pair.Sjc.Rebuild(adapter).RebuildAsync(pair.Database, faulted, CancellationToken.None);
        await Assert.That(completed is RebuildOutcome.Completed).IsTrue().Because(completed.ToString());
        await Assert.That(await pair.Sjc.ReadAsync(kept)).IsEqualTo("""{"n":1}""");
        await Assert.That(await pair.Sjc.ReadAsync(only)).IsEqualTo("""{"n":2}""");
        await Assert.That(pair.Sjc.SideExists(faulted)).IsFalse();
    }

    [Test]
    public async Task A_rebuild_of_an_incarnation_no_longer_in_place_is_refused_and_moves_nothing()
    {
        await using var pair = await Pair.StartAsync();
        var (kept, _) = await pair.SeedAsync();
        var current = await pair.Sjc.IncarnationAsync();
        var adapter = new StandIn(pair);
        var other = Ulid.NewUlid();

        var outcome = await pair.Sjc.Rebuild(adapter).RebuildAsync(pair.Database, other, CancellationToken.None);

        await Assert.That(outcome is RebuildOutcome.Refused).IsTrue().Because(outcome.ToString());
        await Assert.That(await pair.Sjc.IncarnationAsync()).IsEqualTo(current);
        await Assert.That(pair.Sjc.SideExists(other)).IsFalse();
        await Assert.That(adapter.Ledger.Get(pair.Database)).IsNull();
        await Assert.That(await pair.Sjc.ReadAsync(kept)).IsEqualTo("""{"n":1}""");
    }

    // Only verified states join (RT2 review 2.1-6): one that does not match its digest is skipped, and the rebuild completes
    // instead of being refused on every retry.
    [Test]
    public async Task A_side_copy_state_that_does_not_match_its_digest_is_skipped_and_the_rebuild_completes()
    {
        await using var pair = await Pair.StartAsync();
        var (kept, only) = await pair.SeedAsync();
        await pair.Sjc.CorruptDigestAsync(only);
        var faulted = await pair.Sjc.IncarnationAsync();
        var adapter = new StandIn(pair);

        var outcome = await pair.Sjc.Rebuild(adapter).RebuildAsync(pair.Database, faulted, CancellationToken.None);

        await Assert.That(outcome is RebuildOutcome.Completed).IsTrue().Because(outcome.ToString());
        await Assert.That(adapter.Certified).IsTrue();
        await Assert.That(pair.Sjc.SideExists(faulted)).IsFalse();
        await Assert.That(await pair.Sjc.ReadAsync(kept)).IsEqualTo("""{"n":1}""");
        await Assert.That(await pair.Sjc.ReadAsync(only)).IsNull().Because("its state does not verify");
    }

    [Test]
    public async Task A_side_copy_state_moved_to_another_key_with_its_digest_is_skipped_and_the_rebuild_completes()
    {
        await using var pair = await Pair.StartAsync();
        var (_, only) = await pair.SeedAsync();
        var misplaced = EntityIds.New().ToString();
        await pair.Sjc.MisplaceAsync(only, misplaced);
        await Assert.That(await pair.Sjc.ReadAsync(misplaced)).IsEqualTo("""{"n":2}""");
        var faulted = await pair.Sjc.IncarnationAsync();
        var adapter = new StandIn(pair);

        var outcome = await pair.Sjc.Rebuild(adapter).RebuildAsync(pair.Database, faulted, CancellationToken.None);

        await Assert.That(outcome is RebuildOutcome.Completed).IsTrue().Because(outcome.ToString());
        await Assert.That(adapter.Certified).IsTrue();
        await Assert.That(pair.Sjc.SideExists(faulted)).IsFalse();
        await Assert.That(await pair.Sjc.ReadAsync(misplaced)).IsNull();
    }

    [Test]
    public async Task A_rebuild_stopped_after_the_swap_resumes_from_its_record_after_a_restart()
    {
        await using var pair = await Pair.StartAsync();
        var (kept, only) = await pair.SeedAsync();
        var faulted = await pair.Sjc.IncarnationAsync();
        var adapter = new StandIn(pair) { RefuseRefill = true };
        _ = await pair.Sjc.Rebuild(adapter).RebuildAsync(pair.Database, faulted, CancellationToken.None);
        var swapped = adapter.Ledger.Get(pair.Database);
        await Assert.That(swapped?.Phase).IsEqualTo(RebuildPhase.Swapped);

        // The process restarts on the same volume: the record and the side copy are all that carry over.
        await pair.Sjc.StopAsync();
        await pair.Sjc.StartAsync();
        adapter.RefuseRefill = false;
        var outcome = await pair.Sjc.Rebuild(adapter).RebuildAsync(pair.Database, faulted, CancellationToken.None);

        await Assert.That(outcome is RebuildOutcome.Completed).IsTrue().Because(outcome.ToString());
        await Assert.That(((RebuildOutcome.Completed)outcome).NewIncarnation).IsEqualTo(swapped!.NewIncarnation!.Value);
        await Assert.That(await pair.Sjc.ReadAsync(kept)).IsEqualTo("""{"n":1}""");
        await Assert.That(await pair.Sjc.ReadAsync(only)).IsEqualTo("""{"n":2}""");
        await Assert.That(pair.Sjc.SideExists(faulted)).IsFalse();
    }

    [Test]
    public async Task A_certified_rebuild_interrupted_before_deleting_its_side_copy_deletes_it_on_the_next_run_only()
    {
        await using var pair = await Pair.StartAsync();
        _ = await pair.SeedAsync();
        var faulted = await pair.Sjc.IncarnationAsync();
        var adapter = new StandIn(pair) { FailAfterCertifying = true };

        var interrupted = await pair.Sjc.Rebuild(adapter).RebuildAsync(pair.Database, faulted, CancellationToken.None);

        await Assert.That(interrupted is RebuildOutcome.Refused).IsTrue().Because(interrupted.ToString());
        await Assert.That(adapter.Ledger.Get(pair.Database)?.Phase).IsEqualTo(RebuildPhase.Certified);
        await Assert.That(pair.Sjc.SideExists(faulted)).IsTrue();

        adapter.FailAfterCertifying = false;
        var completed = await pair.Sjc.Rebuild(adapter).RebuildAsync(pair.Database, faulted, CancellationToken.None);
        await Assert.That(completed is RebuildOutcome.Completed).IsTrue().Because(completed.ToString());
        await Assert.That(pair.Sjc.SideExists(faulted)).IsFalse();
        await Assert.That(adapter.Ledger.Get(pair.Database)).IsNull();
    }

    [Test]
    public async Task Only_one_rebuild_runs_on_a_host_and_a_second_answers_busy_without_changing_anything()
    {
        await using var pair = await Pair.StartAsync();
        _ = await pair.SeedAsync();
        var faulted = await pair.Sjc.IncarnationAsync();
        var adapter = new StandIn(pair) { Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var rebuild = pair.Sjc.Rebuild(adapter);

        var first = rebuild.RebuildAsync(pair.Database, faulted, CancellationToken.None).AsTask();
        await adapter.Entered.Task.WaitAsync(Deadline);
        // A second rebuild answers at once; one that waited for the first would still be running here.
        var second = rebuild.RebuildAsync(pair.Database, faulted, CancellationToken.None).AsTask();
        bool answered;
        try
        {
            _ = await second.WaitAsync(TimeSpan.FromSeconds(5));
            answered = true;
        }
        catch (TimeoutException)
        {
            answered = false;
        }

        adapter.Hold!.SetResult();
        await Assert.That(answered).IsTrue().Because("a second rebuild on the host answers without waiting");
        await Assert.That(await second is RebuildOutcome.Busy).IsTrue();
        await Assert.That(await first is RebuildOutcome.Completed).IsTrue();
    }

    [Test]
    public async Task A_read_open_on_the_faulted_copy_during_the_swap_leaves_the_new_incarnation_intact()
    {
        await using var pair = await Pair.StartAsync();
        var (kept, only) = await pair.SeedAsync();
        var faulted = await pair.Sjc.IncarnationAsync();
        var store = await pair.Sjc.StoreAsync();
        var reading = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        _ = await reading.GetAsync("items", kept, CancellationToken.None);

        var outcome = await pair.Sjc.Rebuild(new StandIn(pair)).RebuildAsync(pair.Database, faulted, CancellationToken.None);
        await reading.DisposeAsync();

        await Assert.That(outcome is RebuildOutcome.Completed).IsTrue().Because(outcome.ToString());
        var after = EntityIds.New().ToString();
        await pair.Sjc.PutAsync(after, """{"n":3}""");
        await Assert.That(await pair.Sjc.ReadAsync(after)).IsEqualTo("""{"n":3}""");
        await Assert.That(await pair.Sjc.ReadAsync(only)).IsEqualTo("""{"n":2}""");
        await Assert.That(await pair.Sjc.IntegrityAsync()).IsEqualTo("ok");
    }

    [Test]
    public async Task A_rebuilt_copy_removed_after_certification_refuses_and_keeps_the_side_copy()
    {
        await using var pair = await Pair.StartAsync();
        _ = await pair.SeedAsync();
        var faulted = await pair.Sjc.IncarnationAsync();
        var adapter = new StandIn(pair) { FailAfterCertifying = true };
        _ = await pair.Sjc.Rebuild(adapter).RebuildAsync(pair.Database, faulted, CancellationToken.None);
        await Assert.That(adapter.Ledger.Get(pair.Database)?.Phase).IsEqualTo(RebuildPhase.Certified);

        // The certified file goes before the side copy was deleted: the side copy is the only copy left, and it stays.
        await pair.Sjc.LoseDatabaseAsync();
        adapter.FailAfterCertifying = false;
        var outcome = await pair.Sjc.Rebuild(adapter).RebuildAsync(pair.Database, faulted, CancellationToken.None);

        await Assert.That(outcome is RebuildOutcome.Refused).IsTrue().Because(outcome.ToString());
        await Assert.That(pair.Sjc.SideExists(faulted)).IsTrue();
        await Assert.That(adapter.Ledger.Get(pair.Database)?.Phase).IsEqualTo(RebuildPhase.Certified);
    }

    [Test]
    public async Task Finalization_deletes_the_side_copy_only_while_the_rebuilt_incarnation_is_in_place()
    {
        await using var pair = await Pair.StartAsync();
        _ = await pair.SeedAsync();
        var faulted = await pair.Sjc.IncarnationAsync();
        var adapter = new StandIn(pair) { FailAfterCertifying = true };
        _ = await pair.Sjc.Rebuild(adapter).RebuildAsync(pair.Database, faulted, CancellationToken.None);
        var certified = adapter.Ledger.Get(pair.Database)!;

        await Assert.That(await pair.Sjc.FinalizeAsync(Ulid.NewUlid(), certified.SidePath)).IsFalse();
        await Assert.That(pair.Sjc.SideExists(faulted)).IsTrue();
        await Assert.That(await pair.Sjc.FinalizeAsync(certified.NewIncarnation!.Value, certified.SidePath)).IsTrue();
        await Assert.That(pair.Sjc.SideExists(faulted)).IsFalse();
    }

    [Test]
    public async Task Salvage_reads_a_private_copy_and_never_writes_the_retained_side_copy()
    {
        await using var pair = await Pair.StartAsync();
        _ = await pair.SeedAsync();
        var faulted = await pair.Sjc.IncarnationAsync();
        var adapter = new StandIn(pair) { RefuseCompletion = true };

        var outcome = await pair.Sjc.Rebuild(adapter).RebuildAsync(pair.Database, faulted, CancellationToken.None);

        await Assert.That(outcome is RebuildOutcome.Refused).IsTrue().Because(outcome.ToString());
        await Assert.That(adapter.Ledger.Get(pair.Database)?.Phase).IsEqualTo(RebuildPhase.Salvaged);
        await Assert.That(adapter.SideBeforeSalvage).IsNotNull();
        await Assert.That(Hash(pair.Sjc.SidePath(faulted))).IsEqualTo(adapter.SideBeforeSalvage).Because("the side copy's file and log, byte for byte");
        await Assert.That(File.Exists(pair.Sjc.SidePath(faulted) + ".salvage")).IsFalse();
    }

    [Test]
    public async Task Salvage_refuses_when_its_page_cannot_be_reserved_and_keeps_the_side_copy()
    {
        await using var pair = await Pair.StartAsync();
        _ = await pair.SeedAsync();
        var faulted = await pair.Sjc.IncarnationAsync();
        var adapter = new StandIn(pair);

        var outcome = await pair.Sjc.Rebuild(adapter, new NoMemory()).RebuildAsync(pair.Database, faulted, CancellationToken.None);

        await Assert.That(outcome is RebuildOutcome.Refused).IsTrue().Because(outcome.ToString());
        await Assert.That(adapter.Ledger.Get(pair.Database)?.Phase).IsEqualTo(RebuildPhase.Swapped);
        await Assert.That(pair.Sjc.SideExists(faulted)).IsTrue();
        await Assert.That(adapter.Certified).IsFalse();
    }

    // SHA-256 of a file and each of its sidecars, by exact path.
    private static string Hash(string path) =>
        string.Join(",", new[] { string.Empty, "-wal", "-shm", "-journal" }.Where(sidecar => File.Exists(path + sidecar))
            .Select(sidecar => sidecar + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path + sidecar)))));

    // A memory budget with nothing left.
    private sealed class NoMemory : IMemoryBudget
    {
        public IMemoryReservation? TryReserve(MemoryLane lane, long bytes) => null;

        public IMemoryReservation? TryReserve(MemoryLane lane, long bytes, TenantContext? tenant) => null;
    }

    private sealed class BusyRebuild : IReplicaRebuild
    {
        public ValueTask<RebuildOutcome> RebuildAsync(TenantContext database, Ulid faultedIncarnation, CancellationToken cancellationToken) =>
            ValueTask.FromResult<RebuildOutcome>(new RebuildOutcome.Busy());
    }

    // The in-memory orchestration record outlives this fixture's restart; merge refill still crosses the real tenant barrier.
    private sealed class StandIn(Pair pair) : IRebuildCertification, IRebuildRepair
    {
        private (Ulid Incarnation, long Head, Hlc CollectedBefore)? _refill;

        public ValueTask<int?> RefillRebuildAsync(TenantContext database, Ulid newIncarnation, IReadOnlyList<int> buckets,
            BucketCursor? after, IReadOnlyList<RepairState> states, Hlc collectedBefore, CancellationToken cancellationToken) =>
            pair.Sjc.Api.Server.Services.GetRequiredService<IRebuildRepair>()
                .RefillRebuildAsync(database, newIncarnation, buckets, after, states, collectedBefore, cancellationToken);

        public MemoryLedger Ledger { get; } = new();

        public bool RefuseRefill { get; set; }

        public bool RefuseCompletion { get; set; }

        // The side copy's hashes as the refill began, after the swap and before the salvage.
        public string? SideBeforeSalvage { get; private set; }

        public bool FailAfterCertifying { get; set; }

        public bool Certified { get; private set; }

        public TaskCompletionSource? Hold { get; init; }

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<int?> RepairRebuildAsync(TenantContext database, Ulid newIncarnation, IReadOnlyList<RepairState> states, CancellationToken cancellationToken) =>
            await pair.Sjc.IncarnationAsync() == newIncarnation
                ? await pair.Sjc.Api.Server.Services.GetRequiredService<IChangeGroupSink>().RepairAsync(database, newIncarnation, states, cancellationToken)
                : null;

        public ValueTask<bool> RecordRefillAsync(
            TenantContext database, Ulid newIncarnation, string sourceMachine, Ulid sourceIncarnation, long head, Hlc collectedBefore, CancellationToken cancellationToken) =>
            pair.Sjc.Api.Server.Services.GetRequiredService<IRebuildRepair>()
                .RecordRefillAsync(database, newIncarnation, sourceMachine, sourceIncarnation, head, collectedBefore, cancellationToken);

        public ValueTask<bool> CompleteRebuildAsync(TenantContext database, Ulid oldIncarnation, Ulid newIncarnation, CancellationToken cancellationToken) =>
            CompleteAsync(database, oldIncarnation, newIncarnation, cancellationToken);

        public async ValueTask<bool> RefillAsync(TenantContext database, Ulid newIncarnation, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            SideBeforeSalvage = Ledger.Get(database) is { } record ? Hash(record.SidePath) : null;
            if (Hold is { } hold)
            {
                await hold.Task.WaitAsync(cancellationToken);
            }

            // The refill pins gru's copy in place, as a sender's refill names its own incarnation.
            var source = pair.Gru.Api.Server.Services.GetRequiredService<IReplicaStates>();
            if (RefuseRefill || await source.DigestAsync(database, null, cancellationToken) is not { } pinned
                || await pair.Sjc.AntiEntropy.RefillAsync(database, pair.Gru.Name, await source.HorizonAsync(database, cancellationToken), pinned.Incarnation, cancellationToken) is null)
            {
                return false;
            }

            _refill = (pinned.Incarnation, pinned.Head, pinned.CollectedBefore);
            return true;
        }

        public async ValueTask<bool> CompleteAsync(TenantContext database, Ulid oldIncarnation, Ulid newIncarnation, CancellationToken cancellationToken)
        {
            if (RefuseCompletion || Ledger.Get(database) is not { Phase: RebuildPhase.Salvaged } record || record.OldIncarnation != oldIncarnation
                || record.NewIncarnation != newIncarnation || _refill is not { } proof)
            {
                return false;
            }

            var services = pair.Sjc.Api.Server.Services;
            var durable = services.GetRequiredService<IRebuildLedger>();
            await durable.SaveAsync(record with { Phase = RebuildPhase.Pending, NewIncarnation = null }, cancellationToken);
            await durable.SaveAsync(record with { Phase = RebuildPhase.Swapped }, cancellationToken);
            if (!await RecordRefillAsync(database, newIncarnation, pair.Gru.Name, proof.Incarnation, proof.Head, proof.CollectedBefore, cancellationToken))
            {
                return false;
            }

            await durable.SaveAsync(record, cancellationToken);
            if (!await services.GetRequiredService<IRebuildRepair>().CompleteRebuildAsync(database, oldIncarnation, newIncarnation, cancellationToken))
            {
                return false;
            }

            Ledger.Set(await durable.GetAsync(database, cancellationToken)
                ?? throw new InvalidOperationException("the completion barrier did not record its certificate"));
            Certified = true;
            return FailAfterCertifying ? throw new IOException("a crash after the certificate, before the side copy went") : true;
        }
    }

    private sealed class MemoryLedger : IRebuildLedger
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<TenantContext, RebuildRecord> _records = [];

        public RebuildRecord? Get(TenantContext database)
        {
            lock (_gate)
            {
                return _records.GetValueOrDefault(database);
            }
        }

        public void Set(RebuildRecord record)
        {
            lock (_gate)
            {
                _records[record.Database] = record;
            }
        }

        public ValueTask<RebuildRecord?> GetAsync(TenantContext database, CancellationToken cancellationToken) => ValueTask.FromResult(Get(database));

        public ValueTask SaveAsync(RebuildRecord record, CancellationToken cancellationToken)
        {
            Set(record);
            return ValueTask.CompletedTask;
        }

        public ValueTask RetireAsync(TenantContext database, Ulid oldIncarnation, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_records.TryGetValue(database, out var record) && record.OldIncarnation == oldIncarnation)
                {
                    _ = _records.Remove(database);
                }
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>gru and sjc, each holding one database placed in both; sjc is the copy that gets rebuilt.</summary>
    private sealed class Pair : IAsyncDisposable
    {
        public FakeMemberStore Members { get; } = new();

        public string User { get; } = EntityIds.New().ToString();

        public TenantContext Database { get; } = new(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());

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

        // Two documents on both machines, then gru loses the second behind the streams' back: only sjc's copy holds it.
        public async Task<(string Kept, string Only)> SeedAsync()
        {
            var (kept, only) = (EntityIds.New().ToString(), EntityIds.New().ToString());
            await Gru.WriteAsync(async transaction =>
            {
                _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
                _ = await transaction.PutAsync("items", kept, Encoding.UTF8.GetBytes("""{"n":1}"""), CancellationToken.None);
                _ = await transaction.PutAsync("items", only, Encoding.UTF8.GetBytes("""{"n":2}"""), CancellationToken.None);
            });
            await Assert.That(await Sjc.UntilAsync(only, """{"n":2}""")).IsTrue();
            await Gru.LoseAsync("items", only);
            await Assert.That(await Gru.ReadAsync(only)).IsNull();
            return (kept, only);
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
        private readonly string _directory = Directory.CreateTempSubdirectory("terrabase-rebuild-").FullName;
        private ManagementApi? _api;

        public string Name { get; } = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())[..14];

        public ManagementApi Api => _api ?? throw new InvalidOperationException($"{region} is stopped");

        public AntiEntropy AntiEntropy => Api.Server.Services.GetRequiredService<AntiEntropy>();

        private TursoStorageEngine Engine => Api.Server.Services.GetRequiredService<TursoStorageEngine>();

        public async Task StartAsync()
        {
            var first = !File.Exists(Path.Combine(_directory, "_system.db"));
            _api = await ManagementApi.StartAsync(settings =>
            {
                settings["region"] = region;
                settings["node-name"] = Name;
                settings["data-directory"] = _directory;
            }, services =>
            {
                services.AddSingleton<IMemberDirectory>(pair.Members);
                // The test drives recovery; the hosted worker must preserve its deliberately retained side copy.
                services.AddSingleton<IReplicaRebuild, BusyRebuild>();
            }, userId: pair.User);
            if (first)
            {
                await RegisterAsync();
            }
            else
            {
                // The restarted fixture is listed only after startup; refill requires its real catalog verification.
                await Assert.That(await ((CatalogGate)Api.Server.Services.GetRequiredService<ICatalogGate>()).VerifyAsync(TestContext.Current!.Execution.CancellationToken)).IsTrue();
            }
        }

        public async Task StopAsync()
        {
            await Api.DisposeAsync();
            _api = null;
        }

        // The rebuild of this machine's copy, with the stand-in's seams.
        public ReplicaRebuild Rebuild(StandIn adapter, IMemoryBudget? memory = null) =>
            new(Engine, adapter.Ledger, adapter, adapter,
                memory ?? Api.Server.Services.GetRequiredService<IMemoryBudget>());

        public string SidePath(Ulid incarnation) => Engine.SidePath(pair.Database, incarnation);

        public async Task<bool> FinalizeAsync(Ulid rebuilt, string sidePath) => await Engine.FinalizeAsync(pair.Database, rebuilt, sidePath, CancellationToken.None);

        public async Task LoseDatabaseAsync()
        {
            var path = ((TursoDatabaseStore)await StoreAsync()).FilePath;
            await StopAsync();
            // Lose only the certified file: an explicit database deletion also deletes its retained side copies.
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
            {
                File.Delete(path + suffix);
            }

            await StartAsync();
        }

        public async Task<Ulid> IncarnationAsync() =>
            await Engine.IncarnationAsync(pair.Database, CancellationToken.None) ?? throw new InvalidOperationException("the database is missing");

        public bool SideExists(Ulid incarnation) => Engine.SideExists(Engine.SidePath(pair.Database, incarnation));

        public async Task<IDatabaseStore> StoreAsync() =>
            await Engine.OpenDatabaseAsync(pair.Database, CancellationToken.None) ?? throw new InvalidOperationException("the database is missing");

        public async Task WriteAsync(Func<IStorageTransaction, Task> work)
        {
            var store = await StoreAsync();
            _ = await store.WriteAsync(async (transaction, _) =>
            {
                await work(transaction);
                return true;
            }, static _ => true, CancellationToken.None);
        }

        public Task PutAsync(string id, string json) =>
            WriteAsync(async transaction => _ = await transaction.PutAsync("items", id, Encoding.UTF8.GetBytes(json), CancellationToken.None));

        public async Task<string?> ReadAsync(string id)
        {
            var store = await StoreAsync();
            await using var transaction = await store.BeginTransactionAsync(write: false, CancellationToken.None);
            return await transaction.GetAsync("items", id, CancellationToken.None) is { Deleted: false } document ? Encoding.UTF8.GetString(document.Json.Span) : null;
        }

        public async Task<bool> UntilAsync(string id, string expected)
        {
            var deadline = DateTime.UtcNow + Deadline;
            while (DateTime.UtcNow < deadline)
            {
                if (await ReadAsync(id) == expected)
                {
                    return true;
                }

                await Task.Delay(100);
            }

            return false;
        }

        // Deletes the document's rows from the file directly, as a lost apply would leave it.
        public Task LoseAsync(string collection, string id) =>
            OnWriterAsync($"DELETE FROM \"{collection}\" WHERE id = $id; DELETE FROM _tb_state WHERE collection = $collection AND id = $id;", collection, id);

        // Breaks the stored digest of the document's state row, as a torn or unsynced page could.
        public Task CorruptDigestAsync(string id) =>
            OnWriterAsync("UPDATE _tb_state SET h1 = h1 + 1 WHERE collection = $collection AND id = $id;", "items", id);

        // Moves a document and its state together, preserving the digest that belonged to the old key.
        public Task MisplaceAsync(string id, string misplaced) =>
            OnWriterAsync("UPDATE items SET id = $misplaced WHERE id = $id; UPDATE _tb_state SET id = $misplaced WHERE collection = $collection AND id = $id;",
                "items", id, misplaced);

        public async Task<string?> IntegrityAsync()
        {
            var store = (TursoDatabaseStore)await StoreAsync();
            return await store.WithWriterAsync(session =>
            {
                using var command = session.Connection.CreateCommand();
                command.CommandText = "PRAGMA integrity_check;";
                return command.ExecuteScalar() as string;
            }, "a test checking integrity", CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            if (_api is not null)
            {
                await StopAsync();
            }

            Directory.Delete(_directory, recursive: true);
        }

        private async Task OnWriterAsync(string sql, string collection, string id, string? misplaced = null)
        {
            var store = (TursoDatabaseStore)await StoreAsync();
            _ = await store.WithWriterAsync(session =>
            {
                using var transaction = session.Connection.BeginTransaction();
                using var command = session.Connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = sql;
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$collection", collection);
                if (misplaced is not null)
                {
                    command.Parameters.AddWithValue("$misplaced", misplaced);
                }

                var changed = command.ExecuteNonQuery();
                MerkleFixture.Rebuild(session, transaction);
                transaction.Commit();
                return changed;
            }, "a test editing rows directly", CancellationToken.None);
            store.ForgetCollections();
        }

        // The organization, project and database under the pair's ids, placed in both regions.
        private async Task RegisterAsync()
        {
            var database = pair.Database;
            await CatalogFixture.RegisterAsync(Api, Api.User, database, "main", replicas);
        }
    }
}
