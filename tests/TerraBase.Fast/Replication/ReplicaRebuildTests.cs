using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Engine.Sqlite;
using TerraBase.Features.Replication;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

/// <summary>
/// The R7 rebuild of a faulted copy into a new incarnation (docs/terrabase/decisions/0013a, sections 1, 5 and 9), on real
/// engines: the swap, the salvage of what only the faulted copy holds, refusals that keep the record and the side copy,
/// and resumption. The copy fault, its record and certified refill are R6-FIX's and not built yet: a test-only adapter
/// stands in (a ledger in memory, today's merge refill, a certificate written into that ledger). These tests prove the
/// rebuild's own steps; they claim no certification.
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

    [Test]
    public async Task A_side_copy_state_that_does_not_match_its_digest_refuses_the_rebuild_and_keeps_the_side_copy()
    {
        await using var pair = await Pair.StartAsync();
        var (_, only) = await pair.SeedAsync();
        await pair.Sjc.CorruptDigestAsync(only);
        var faulted = await pair.Sjc.IncarnationAsync();
        var adapter = new StandIn(pair);

        var outcome = await pair.Sjc.Rebuild(adapter).RebuildAsync(pair.Database, faulted, CancellationToken.None);

        await Assert.That(outcome is RebuildOutcome.Refused).IsTrue().Because(outcome.ToString());
        await Assert.That(adapter.Ledger.Get(pair.Database)?.Phase).IsEqualTo(RebuildPhase.Swapped);
        await Assert.That(pair.Sjc.SideExists(faulted)).IsTrue();
        await Assert.That(adapter.Certified).IsFalse();
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
        var second = await rebuild.RebuildAsync(pair.Database, faulted, CancellationToken.None);
        await Assert.That(second is RebuildOutcome.Busy).IsTrue().Because(second.ToString());

        adapter.Hold!.SetResult();
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

    // Test-only boundary adapter for R6-FIX's seams: the record in memory (it outlives a machine's restart because the test
    // holds it), today's merge refill from gru as the stand-in for certified refill, and a certificate written into the
    // record as the stand-in for the completion barrier.
    private sealed class StandIn(Pair pair) : IRebuildCertification
    {
        public MemoryLedger Ledger { get; } = new();

        public bool RefuseRefill { get; set; }

        public bool FailAfterCertifying { get; set; }

        public bool Certified { get; private set; }

        public TaskCompletionSource? Hold { get; init; }

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<bool> RefillAsync(TenantContext database, Ulid newIncarnation, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            if (Hold is { } hold)
            {
                await hold.Task.WaitAsync(cancellationToken);
            }

            return !RefuseRefill && await pair.Sjc.AntiEntropy.RefillAsync(database, pair.Gru.Name, cancellationToken) is not null;
        }

        public ValueTask<bool> CompleteAsync(TenantContext database, Ulid oldIncarnation, Ulid newIncarnation, CancellationToken cancellationToken)
        {
            if (Ledger.Get(database) is not { Phase: RebuildPhase.Salvaged } record || record.OldIncarnation != oldIncarnation || record.NewIncarnation != newIncarnation)
            {
                return ValueTask.FromResult(false);
            }

            Ledger.Set(record with { Phase = RebuildPhase.Certified });
            Certified = true;
            return FailAfterCertifying ? throw new IOException("a crash after the certificate, before the side copy went") : ValueTask.FromResult(true);
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

        private SqliteStorageEngine Engine => Api.Server.Services.GetRequiredService<SqliteStorageEngine>();

        public async Task StartAsync()
        {
            var first = !File.Exists(Path.Combine(_directory, "_system.db"));
            _api = await ManagementApi.StartAsync(settings =>
            {
                settings["region"] = region;
                settings["node-name"] = Name;
                settings["data-directory"] = _directory;
            }, services => services.AddSingleton<IMemberDirectory>(pair.Members));
            if (first)
            {
                await RegisterAsync();
            }
        }

        public async Task StopAsync()
        {
            await Api.DisposeAsync();
            _api = null;
        }

        // The rebuild of this machine's copy, with the stand-in's seams.
        public ReplicaRebuild Rebuild(StandIn adapter) =>
            new(Engine, adapter.Ledger, adapter, Api.Server.Services.GetRequiredService<IChangeGroupSink>());

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

        public async Task<string?> IntegrityAsync()
        {
            var store = (SqliteDatabaseStore)await StoreAsync();
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

        private async Task OnWriterAsync(string sql, string collection, string id)
        {
            var store = (SqliteDatabaseStore)await StoreAsync();
            _ = await store.WithWriterAsync(session =>
            {
                using var command = session.Connection.CreateCommand();
                command.CommandText = sql;
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$collection", collection);
                return command.ExecuteNonQuery();
            }, "a test editing rows directly", CancellationToken.None);
            store.ForgetCollections();
        }

        // The organization, project and database under the pair's ids, placed in both regions.
        private async Task RegisterAsync()
        {
            var database = pair.Database;
            var commit = JsonSerializer.Serialize(new
            {
                id = EntityIds.New().ToString(),
                ops = new object[]
                {
                    new { op = "create_organization", id = database.OrganizationId, name = $"rebuild-{database.OrganizationId}" },
                    new { op = "create_project", organization = database.OrganizationId, id = database.ProjectId, name = "rebuild" },
                    new { op = "create_database", organization = database.OrganizationId, project = database.ProjectId, name = "main", id = database.DatabaseId, replica_regions = replicas },
                },
            });
            using var service = Api.Service(Api.User);
            using var response = await service.PostAsync(new Uri("/v1/management/commits", UriKind.Relative), new StringContent(commit, Encoding.UTF8, "application/json"));
            await Assert.That(response.IsSuccessStatusCode).IsTrue().Because(await response.Content.ReadAsStringAsync());
        }
    }
}
