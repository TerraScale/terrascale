using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Bogus;
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
using Turso.Data.Sqlite;

namespace TerraBase.Slow.Replication;

public sealed partial class A6LocalRebuildE2ETests
{
    [Test]
    public async Task Local_capture_shape_damage_rebuilds_and_accepts_new_cross_region_writes()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        var token = deadline.Token;
        await using var pair = await Pair.StartAsync(token);
        var original = EntityIds.New().ToString();
        var body = new JsonObject { ["value"] = new Faker().Random.AlphaNumeric(16) };
        await ManagementApi.CreateCollectionAsync(pair.Sjc.Data, pair.Managed, "items", token);
        await pair.Sjc.PutAsync(original, body, token);
        await pair.Gru.UntilAsync(original, body, token);
        // Wait for durable shipment, not merely a foreground read on the receiver.
        await pair.ReplicatedAsync(token, delayAcknowledgements: true);
        var oldIncarnation = await pair.Sjc.IncarnationAsync(token);
        await pair.Sjc.StopAsync();
        await Assert.That(TerraBaseTestServer.IsDataDirectoryOwned(pair.Sjc.DirectoryPath)).IsFalse();

        var retained = await pair.Sjc.RetainedStateRowAsync(token);
        // A valid capture type with an impossible row shape. Preserve table, after-image,
        // _tb_state (including A5 keyed hashes), materialized documents and commit_offset.
        // Replay from just before this retained row, under its EXISTING A4 source incarnation.
        var reset = await ManagementApi.ExecuteStoredAsync(pair.Sjc.DirectoryPath, pair.Managed, """
            UPDATE _tb_peers SET acked = $change_id - 1,
                incarnation = (SELECT incarnation FROM commit_offset WHERE id = 1);
            """, ("$change_id", retained));
        await Assert.That(reset).IsGreaterThan(0);
        var changed = await ManagementApi.ExecuteStoredAsync(pair.Sjc.DirectoryPath, pair.Managed, """
            UPDATE turso_cdc SET change_type = 2 WHERE change_id = $change_id;
            """, ("$change_id", retained));
        await Assert.That(changed).IsEqualTo(1);

        await pair.Sjc.StartAsync(register: false, token);
        // Production tail decoding records the fault; this checkpoint is AFTER its FULL ledger
        // commit and BEFORE its recovery wake. Observing it never pauses or replaces recovery.
        await pair.UntilAsync(_ => Task.FromResult(pair.Sjc.DurableFault.Task.IsCompleted), token);
        await pair.Sjc.DurableFault.Task.WaitAsync(token);
        using var metrics = new HttpClient { BaseAddress = pair.Sjc.Api.Server.MetricsAddress };
        await pair.UntilAsync(async cancellation =>
        {
            var text = await metrics.GetStringAsync(new Uri("/metrics", UriKind.Relative), cancellation);
            return text.Split('\n').Any(line =>
                line.StartsWith("terrabase_replication_rebuilds_total{", StringComparison.Ordinal)
                && line.Contains("outcome=\"completed\"", StringComparison.Ordinal)
                && double.TryParse(line[(line.LastIndexOf(' ') + 1)..], NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var count) && count >= 1);
        }, token);

        var rebuilt = await pair.Sjc.IncarnationAsync(token);
        await Assert.That(rebuilt).IsNotEqualTo(oldIncarnation);
        await Assert.That(await pair.Sjc.Service<IRebuildLedger>().GetAsync(pair.Database, token)).IsNull();
        await Assert.That(pair.Sjc.Service<IRebuildStorage>().SideExists(
            pair.Sjc.Service<IRebuildStorage>().SidePath(pair.Database, oldIncarnation))).IsFalse();
        await Assert.That(await pair.Sjc.Service<IReplicaCopyFaults>().RefusedAsync(pair.Database, oldIncarnation, token)).IsTrue();
        await Assert.That(await pair.Sjc.Service<IReplicaCopyFaults>().RefusedAsync(pair.Database, rebuilt, token)).IsFalse();
        await pair.Gru.UntilAsync(original, body, token);
        await pair.Sjc.UntilAsync(original, body, token);
        await pair.Sjc.AssertCertificateAsync(pair.Gru, rebuilt, token);

        var later = EntityIds.New().ToString();
        var next = new JsonObject { ["value"] = new Faker().Random.AlphaNumeric(16) };
        await pair.Gru.PutAsync(later, next, token);
        await pair.Sjc.UntilAsync(later, next, token);
        await pair.ReplicatedAsync(token);
        // Stop cleanly again: completion, refill proof and original state survive another host.
        await pair.Sjc.StopAsync();
        await pair.Sjc.StartAsync(register: false, token);
        await Assert.That(await pair.Sjc.IncarnationAsync(token)).IsEqualTo(rebuilt);
        await Assert.That(await pair.Sjc.Service<IRebuildLedger>().GetAsync(pair.Database, token)).IsNull();
        await pair.Sjc.UntilAsync(original, body, token);
        await pair.Sjc.UntilAsync(later, next, token);
        await pair.Sjc.AssertCertificateAsync(pair.Gru, rebuilt, token);
    }

    // A drop reads every covered state in one transaction. One owner admits at most 16,384 leases, so each document's
    // admission must end with its iteration, on the dropping machine and on the peer applying the replicated drop.
    [Test]
    public async Task A_drop_of_more_documents_than_one_owner_may_lease_commits_locally_and_replicated()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(9));
        var token = deadline.Token;
        await using var pair = await Pair.StartAsync(token);
        await ManagementApi.CreateCollectionAsync(pair.Gru.Data, pair.Managed, "items", token);
        var ids = Enumerable.Range(0, 16_384 + 512).Select(_ => EntityIds.New().ToString()).ToArray();
        var store = await pair.Gru.Service<IStorageEngine>().OpenDatabaseAsync(pair.Database, token)
            ?? throw new InvalidOperationException("gru holds no copy of the database");
        // Each document's unique keys are replaced too, which reads the collection's definition.
        _ = await store.WriteAsync(async (transaction, cancellation) =>
        {
            await transaction.SetUniqueConstraintsAsync("items", [new UniqueConstraint("email", ["email"])], reindex: false, cancellation);
            return true;
        }, static _ => true, token);
        foreach (var chunk in ids.Chunk(1024))
        {
            _ = await store.WriteAsync(async (transaction, cancellation) =>
            {
                foreach (var id in chunk)
                {
                    _ = await transaction.PutAsync("items", id, Encoding.UTF8.GetBytes(new JsonObject { ["email"] = id }.ToJsonString()), cancellation);
                }

                return true;
            }, static _ => true, token);
        }

        await pair.Sjc.UntilAsync(ids[^1], new JsonObject { ["email"] = ids[^1] }, token);
        _ = await store.WriteAsync(async (transaction, cancellation) =>
            await transaction.DropCollectionAsync("items", cancellation), static _ => true, token);

        await pair.Gru.UntilAbsentAsync(ids[0], token);
        await pair.Sjc.UntilAbsentAsync(ids[^1], token);
        await pair.Sjc.UntilAbsentAsync(ids[0], token);
        // The replicated stream goes on past the drop: a later write applies and both directions acknowledge.
        await ManagementApi.CreateCollectionAsync(pair.Gru.Data, pair.Managed, "items", token);
        var later = EntityIds.New().ToString();
        var next = new JsonObject { ["value"] = new Faker().Random.AlphaNumeric(16) };
        await pair.Gru.PutAsync(later, next, token);
        await pair.Sjc.UntilAsync(later, next, token);
        await pair.ReplicatedAsync(token);
    }

    // Condition polling is supplied by the existing harness; no elapsed-time sleeps decide success.
    private static Task UntilAsync(Func<CancellationToken, Task<bool>> condition, CancellationToken token) =>
        TerraBaseTestServer.EventuallyAsync(async cancellation =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellation);
            return await condition(linked.Token);
        }, TimeSpan.FromMinutes(3)).WaitAsync(token);

    // Owned by these local rebuild tests: no Cluster, deployment branch, environment writes or shared mutable state.
    // Pin host UTC before native CDC timestamps; advance fake timers and elapsed time separately.
    private sealed class RetainedCdcTime : TimeProvider
    {
        private readonly DateTimeOffset _retainedAt = System.GetUtcNow();
        private readonly FakeTimeProvider _scheduler = new();
        public override DateTimeOffset GetUtcNow() => _retainedAt;
        public override long TimestampFrequency => _scheduler.TimestampFrequency;
        public override long GetTimestamp() => _scheduler.GetTimestamp();
        public void Advance(TimeSpan elapsed) => _scheduler.Advance(elapsed);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            _scheduler.CreateTimer(callback, state, dueTime, period);
    }

    private sealed class Pair : IAsyncDisposable
    {
        private const string HeldRegion = "a6-held";
        private int _holdTenantAcks;
        public RetainedCdcTime Clock { get; } = new();
        public FakeMemberStore Members { get; } = new();
        public string User { get; } = EntityIds.New().ToString();
        public TenantContext Database { get; } = new(EntityIds.New().ToString(), PlanTier.Hobby,
            EntityIds.New().ToString(), EntityIds.New().ToString());
        public ManagedDatabaseRef Managed => new(Database.OrganizationId, Database.ProjectId!, Database.DatabaseId!, "main");
        public Machine Gru { get; private set; } = null!;
        public Machine Sjc { get; private set; } = null!;

        public Pair()
        {
            Members.Map = (_, member) => Volatile.Read(ref _holdTenantAcks) != 0
                ? member with { Region = HeldRegion } : member;
        }

        public Task UntilAsync(Func<CancellationToken, Task<bool>> condition, CancellationToken token) =>
            A6LocalRebuildE2ETests.UntilAsync(cancellation =>
            {
                cancellation.ThrowIfCancellationRequested();
                Clock.Advance(TimeSpan.FromMilliseconds(100));
                return condition(cancellation);
            }, token);

        public static async Task<Pair> StartAsync(CancellationToken token)
        {
            var pair = new Pair();
            try
            {
                pair.Gru = new Machine(pair, "gru", ["sjc"]);
                await pair.Gru.StartAsync(register: true, token);
                pair.Sjc = new Machine(pair, "sjc", ["gru"]);
                await pair.Sjc.StartAsync(register: true, token);
                return pair;
            }
            catch
            {
                await pair.DisposeAsync();
                throw;
            }
        }

        public async Task ReplicatedAsync(CancellationToken token, bool delayAcknowledgements = false)
        {
            token.ThrowIfCancellationRequested();
            if (delayAcknowledgements)
            {
                foreach (var machine in new[] { Gru, Sjc })
                {
                    await Assert.That(machine.Service<ICatalogGate>().IsOpen).IsTrue();
                    if (await machine.Service<ITenantDirectory>().DatabaseRecordAsync(Database, token) is not TenantDatabaseRecord record)
                    {
                        throw new InvalidOperationException("the ACK barrier database has no placement");
                    }

                    await Assert.That(record.Placement.Includes(HeldRegion)).IsFalse();
                }

                _ = Interlocked.Exchange(ref _holdTenantAcks, 1);
                // A changed peer retires and joins its old sender before the replacement can welcome.
                // Keep SYSTEM connected, but place neither tenant stream in this fixture-only region.
                Clock.Advance(DirectoryMembership.Heartbeat);
                await UntilAsync(async cancellation =>
                {
                    foreach (var machine in new[] { Gru, Sjc })
                    {
                        var view = await machine.Service<IMembership>().RefreshAsync(cancellation);
                        if (view.Elsewhere.IsDefaultOrEmpty || view.Elsewhere.Any(peer => peer.Region != HeldRegion))
                        {
                            return false;
                        }

                        var status = machine.Service<IReplicationStatus>().Snapshot();
                        if (!status.Initialized || status.Peers.Count != 1 || status.Peers[0].Region != HeldRegion)
                        {
                            return false;
                        }

                        await Assert.That(status.Peers[0].Databases.Any(database =>
                            database.OrganizationId == Database.OrganizationId && database.ProjectId == Database.ProjectId
                            && database.DatabaseId == Database.DatabaseId)).IsFalse();
                    }

                    return true;
                }, token);
            }

            var directions = new[]
            {
                (Source: Gru, Receiver: Sjc, Peer: Sjc.Service<LocalReplica>().Id),
                (Source: Sjc, Receiver: Gru, Peer: Gru.Service<LocalReplica>().Id),
            };
            // A local write in each direction prevents ACK-0 initialization or remote-only quiet ranges from passing. It
            // bypasses the client's remote-ACK wait, which cannot complete while tenant ACKs are held.
            foreach (var (source, _, peer) in directions)
            {
                // Establish the peer's retention row before the probe; its zero cursor is never completion evidence.
                if (await source.Service<IChangeGroupSource>().AckedAsync(Database, peer, token) is null)
                {
                    throw new InvalidOperationException("the ACK barrier source refused its retention cursor");
                }

                await source.WriteLocalAsync(EntityIds.New().ToString(),
                    new JsonObject { ["value"] = new Faker().Random.AlphaNumeric(16) }, token);
            }

            var targets = new (Ulid Incarnation, long Head)[directions.Length];
            for (var index = 0; index < directions.Length; index++)
            {
                var digest = await directions[index].Source.Service<IReplicaStates>().DigestAsync(Database, null, token)
                    ?? throw new InvalidOperationException("the ACK barrier source refused its snapshot");
                if (digest.Head <= 0)
                {
                    throw new InvalidOperationException("the ACK barrier requires a positive captured head after its local write");
                }

                targets[index] = (digest.Incarnation, digest.Head);
            }

            if (delayAcknowledgements)
            {
                // Hold actual sender ACKs past 60 s of scheduler time without aging native CDC past retention's cutoff.
                var waitingSince = Clock.GetTimestamp();
                Clock.Advance(TimeSpan.FromSeconds(61));
                await Assert.That(Clock.GetElapsedTime(waitingSince)).IsGreaterThan(TimeSpan.FromSeconds(60));
                for (var index = 0; index < directions.Length; index++)
                {
                    var cursor = await directions[index].Source.Service<IChangeGroupSource>()
                        .AckedAsync(Database, directions[index].Peer, token)
                        ?? throw new InvalidOperationException("the delayed ACK source refused its cursor");
                    await Assert.That(cursor.Incarnation).IsEqualTo(targets[index].Incarnation);
                    await Assert.That(cursor.RequiresRefill).IsFalse();
                    await Assert.That(cursor.Acked).IsLessThan(targets[index].Head);
                }

                _ = Interlocked.Exchange(ref _holdTenantAcks, 0);
                Clock.Advance(DirectoryMembership.Heartbeat);
            }

            await UntilAsync(async cancellation =>
            {
                cancellation.ThrowIfCancellationRequested();
                var acknowledged = true;
                for (var index = 0; index < directions.Length; index++)
                {
                    var (source, receiver, peer) = directions[index];
                    var (incarnation, head) = targets[index];
                    if (receiver.Service<LocalReplica>().Id != peer)
                    {
                        throw new InvalidOperationException("the ACK barrier receiving replica changed");
                    }

                    var cursor = await source.Service<IChangeGroupSource>().AckedAsync(Database, peer, cancellation);
                    if (cursor is { } found && found.Incarnation != incarnation)
                    {
                        throw new InvalidOperationException("the ACK barrier source incarnation changed");
                    }

                    acknowledged &= cursor is { RequiresRefill: false } held && held.Acked >= head;
                }

                // Recheck every source after both ACK reads; never recapture a replacement copy as a new target.
                for (var index = 0; index < directions.Length; index++)
                {
                    var current = await directions[index].Source.Service<IReplicaStates>().DigestAsync(Database, null, cancellation);
                    if (current is null)
                    {
                        return false;
                    }

                    if (current.Incarnation != targets[index].Incarnation || current.Head < targets[index].Head)
                    {
                        throw new InvalidOperationException("the ACK barrier source snapshot changed scope or regressed");
                    }
                }

                cancellation.ThrowIfCancellationRequested();
                return acknowledged;
            }, token);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (Sjc is not null)
                {
                    await Sjc.DisposeAsync();
                }
            }
            finally
            {
                if (Gru is not null)
                {
                    await Gru.DisposeAsync();
                }
            }
        }
    }

    private sealed class Machine(Pair pair, string region, string[] replicas) : IAsyncDisposable
    {
        private ManagementApi? _api;
        private HttpClient? _data;
        private string? _key;
        public string DirectoryPath { get; } = Directory.CreateTempSubdirectory("terrabase-a6-local-rebuild-").FullName;
        public string Name { get; } = EntityIds.New().ToString().ToLowerInvariant();
        public ManagementApi Api => _api ?? throw new InvalidOperationException($"{region} is stopped");
        public HttpClient Data => _data ?? throw new InvalidOperationException($"{region} has no data client");
        public TaskCompletionSource DurableFault { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public T Service<T>() where T : notnull => Api.Server.Services.GetRequiredService<T>();

        public async Task StartAsync(bool register, CancellationToken token)
        {
            _api = await ManagementApi.StartAsync(settings =>
            {
                settings["region"] = region;
                settings["node-name"] = Name;
                settings["data-directory"] = DirectoryPath;
            }, services =>
            {
                services.AddSingleton<IMemberDirectory>(pair.Members);
                services.AddSingleton<DatabaseLifecycleCheckpoint>((step, database) =>
                {
                    if (step == RebuildLedger.Recorded && database.DatabaseId == pair.Database.DatabaseId)
                    {
                        _ = DurableFault.TrySetResult();
                    }
                });
            }, time: pair.Clock, cancellationToken: token, userId: pair.User);
            pair.Clock.Advance(DirectoryMembership.Heartbeat);
            _ = await Service<IMembership>().RefreshAsync(token);
            await pair.UntilAsync(_ => Task.FromResult(Service<ICatalogGate>().IsOpen), token);
            // Fail if integrated on PR1: never replace its always-refused certification in test DI.
            await Assert.That(Service<IRebuildCertification>() is RebuildCertification).IsTrue();
            await Assert.That(Service<IReplicaRebuild>() is ReplicaRebuild).IsTrue();
            if (register)
            {
                await RegisterAsync(token);
            }

            // The real catalog credential survives with the owned directory; only the HTTP client is replaced.
            _key ??= await Api.Server.IssueAsync(pair.Database.OrganizationId,
                ["documents:read", "documents:write"], token);
            _data = Api.DataClient(_key);
            _data.DefaultRequestHeaders.Add("TB-Durability", "remote_durable");
        }

        public async Task StopAsync()
        {
            _data?.Dispose();
            _data = null;
            if (_api is { } api)
            {
                try
                {
                    await api.DisposeAsync();
                }
                finally
                {
                    // External data directories outlive the host: join all deferred cleanup before reuse.
                    await api.CleanupCompletion;
                    _api = null;
                }
            }
        }

        // A client write waits for another placed region's durable ACK; while no such stream is connected it executes
        // nothing and answers remote_region_unavailable, which the API asks callers to retry.
        public Task PutAsync(string id, JsonObject body, CancellationToken token) =>
            pair.UntilAsync(async cancellation =>
            {
                using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                var sending = Data.PutAsync(ManagementApi.Uri(ManagementApi.DocumentPath(pair.Managed, "items", id)), content, cancellation);
                // Sender retries and the remote-ACK deadline need scheduler ticks while the HTTP request is pending.
                await pair.UntilAsync(_ => Task.FromResult(sending.IsCompleted), cancellation);
                using var response = await sending;
                var text = await response.Content.ReadAsStringAsync(cancellation);
                if (response.StatusCode == HttpStatusCode.ServiceUnavailable && text.Contains("\"remote_region_unavailable\"", StringComparison.Ordinal))
                {
                    return false;
                }

                return response.IsSuccessStatusCode
                    ? true
                    : throw new HttpRequestException($"{region} PUT returned {(int)response.StatusCode}: {text}");
            }, token);

        // A local write of this machine's copy, captured as any client write is, without the client's remote-ACK wait.
        public async Task WriteLocalAsync(string id, JsonObject body, CancellationToken token)
        {
            var store = await Service<IStorageEngine>().OpenDatabaseAsync(pair.Database, token)
                ?? throw new InvalidOperationException($"{region} holds no copy of the database");
            _ = await store.WriteAsync(async (transaction, cancellation) =>
            {
                _ = await transaction.PutAsync("items", id, Encoding.UTF8.GetBytes(body.ToJsonString()), cancellation);
                return true;
            }, static _ => true, token);
        }

        public Task UntilAsync(string id, JsonObject expected, CancellationToken token) =>
            pair.UntilAsync(async cancellation =>
            {
                using var response = await Data.GetAsync(ManagementApi.Uri(ManagementApi.DocumentPath(pair.Managed, "items", id)), cancellation);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return false;
                }

                response.EnsureSuccessStatusCode();
                var envelope = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellation));
                return JsonNode.DeepEquals(envelope?["document"], expected);
            }, token);

        public Task UntilAbsentAsync(string id, CancellationToken token) =>
            pair.UntilAsync(async cancellation =>
            {
                using var response = await Data.GetAsync(ManagementApi.Uri(ManagementApi.DocumentPath(pair.Managed, "items", id)), cancellation);
                return response.StatusCode == HttpStatusCode.NotFound;
            }, token);

        public async Task<long> RetainedStateRowAsync(CancellationToken token)
        {
            var path = Path.Combine(DirectoryPath, pair.Managed.Organization, pair.Managed.Project, pair.Managed.Id + ".db");
            await using var connection = new SqliteConnection(TestMasterKey.ConnectionString(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }));
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT change_id, change_time FROM turso_cdc
                WHERE table_name = '_tb_state' AND change_type IN (0, 1) AND after IS NOT NULL
                ORDER BY change_id LIMIT 1;
                """;
            await using var rows = await command.ExecuteReaderAsync(token);
            if (!await rows.ReadAsync(token))
            {
                throw new InvalidOperationException("the cleanly stopped copy has no retained CDC state row to damage");
            }

            await Assert.That(pair.Clock.GetUtcNow().ToUnixTimeSeconds() - rows.GetInt64(1)).IsLessThan(60);
            return rows.GetInt64(0);
        }

        public async Task<Ulid> IncarnationAsync(CancellationToken token) =>
            await Service<IRebuildStorage>().IncarnationAsync(pair.Database, token)
                ?? throw new InvalidOperationException($"{region} has no database copy");

        public async Task AssertCertificateAsync(Machine source, Ulid rebuilt, CancellationToken token)
        {
            var sourcePin = await source.IncarnationAsync(token);
            var digest = await source.Service<IReplicaStates>().DigestAsync(pair.Database, sourcePin, token)
                ?? throw new InvalidOperationException("the intact source refused its pinned digest");
            var local = await Service<IReplicaStates>().DigestAsync(pair.Database, rebuilt, token)
                ?? throw new InvalidOperationException("the rebuilt receiver refused its pinned digest");
            await Assert.That(local.Buckets.AsSpan().SequenceEqual(digest.Buckets)).IsTrue();
            var store = (TursoDatabaseStore)(await Service<TursoStorageEngine>().OpenDatabaseAsync(pair.Database, token)
                ?? throw new InvalidOperationException("the rebuilt copy is missing"));
            var (origin, incarnation, head) = await store.WithWriterAsync(session =>
            {
                using var command = session.Connection.CreateCommand();
                command.CommandText = "SELECT origin, incarnation, head FROM _tb_refill;";
                using var rows = command.ExecuteReader();
                if (!rows.Read())
                {
                    throw new InvalidDataException("the rebuilt copy has no production refill certificate");
                }

                var result = (Origin: rows.GetString(0), Incarnation: rows.GetString(1), Head: rows.GetInt64(2));
                if (rows.Read())
                {
                    throw new InvalidDataException("the rebuilt copy has more than one refill source");
                }

                return result;
            }, "A6 E2E reading the production refill certificate", token);
            await Assert.That(origin).IsEqualTo(source.Name);
            await Assert.That(incarnation).IsEqualTo(sourcePin.ToString());
            await Assert.That(head).IsGreaterThanOrEqualTo(0).And.IsLessThanOrEqualTo(digest.Head);
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            Directory.Delete(DirectoryPath, recursive: true);
        }

        private async Task RegisterAsync(CancellationToken token)
        {
            var database = pair.Database;
            await CatalogFixture.RegisterAsync(Api, Api.User, database, "main", replicas, token);
        }
    }
}
