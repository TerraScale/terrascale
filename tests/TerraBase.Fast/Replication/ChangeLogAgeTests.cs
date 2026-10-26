using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Turso;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

/// <summary>
/// No peer pins CDC rows for longer than 24 h after they committed (docs/terrabase/multi-region-replication.md section 7):
/// <c>seen</c> is refreshed by anything heard from a peer, so a peer still heard from whose ack trickles (or is stuck) stops
/// holding rows once they are that old, and refills. Continuous backup's anchor still holds them; an evicted peer
/// (<c>seen</c> 0) holds nothing, also during the 24 h after a start.
/// </summary>
public sealed class ChangeLogAgeTests
{
    private const string Items = "items";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    // The catalog replicates like any database: the backup tap's trim keeps what a catalog peer has not acknowledged.
    [Test]
    public async Task The_backup_tap_keeps_catalog_rows_a_peer_has_not_acknowledged()
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        var groups = server.Services.GetRequiredService<TursoChangeGroupSource>();
        var system = (TursoDatabaseStore)await engine.OpenSystemDatabaseAsync(CancellationToken.None);
        await using var tap = (await engine.OpenTapAsync(null, CancellationToken.None))!;
        var peer = ReplicaId.Of(2, 7, 0);
        _ = await Run(system, $"INSERT INTO _tb_peers (node, applied, acked, stable, seen, incarnation) VALUES ({unchecked((long)peer.Value)}, 0, 0, 0, {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}, '{system.Incarnation}');");
        var acked = await groups.HeadAsync(ChangeGroups.System, system.Incarnation, CancellationToken.None) ?? throw new InvalidOperationException("the catalog refused its own incarnation");
        _ = await Run(system, $"UPDATE _tb_peers SET acked = {acked} WHERE node = {unchecked((long)peer.Value)};");
        _ = await Run(system, $"UPDATE _tb_peers SET stable = 1 WHERE node = {unchecked((long)peer.Value)};");

        // The tap read past the writes, so it trims up to its new anchor.
        await Assert.That((await tap.ReadAsync(Stream.Null, CancellationToken.None)).Bytes).IsGreaterThan(0);
        await Assert.That(await groups.RetainsAsync(ChangeGroups.System, system.Incarnation, acked, CancellationToken.None)).IsTrue()
            .Because("a trim past a heard-from catalog peer's ack would make it refill the catalog");
    }

    // Inbound frontiers keep the peer live while its next batch stays in flight; a late ACK must not renew the age cap.
    [Test]
    public async Task A_peer_whose_ack_trickles_does_not_hold_rows_past_24_hours()
    {
        await using var peer = await ScriptedPeer.StartAsync();
        var gru = peer.Sender;
        var store = await gru.StoreAsync();
        var c0 = await gru.HeadAsync();
        var first = await peer.NextAsync();
        store.BackupAnchor = null;
        await peer.AckAsync(first);
        await Assert.That(await gru.UntilAckedAsync(peer.Replica, c0)).IsTrue();
        await gru.WriteAsync(async transaction => _ = await transaction.PutAsync(Items, "slow", Json("""{"n":1}"""), CancellationToken.None));
        var c1 = await gru.HeadAsync();
        var pending = await peer.NextAsync();
        await Assert.That(pending.Batch?.LastChangeId).IsEqualTo((ulong)c1);
        await gru.WriteAsync(async transaction => _ = await transaction.PutAsync(Items, "newest", Json("""{"n":2}"""), CancellationToken.None));
        var c2 = await gru.HeadAsync();

        foreach (var at in new[] { TimeSpan.FromMinutes(2), TimeSpan.FromHours(12), TimeSpan.FromHours(23) })
        {
            await AgePrefixAsync(store, c2, at);
            await peer.ReportFrontierAsync();
            await gru.TrimAsync();
            await Assert.That(await gru.FloorAsync(peer.Clock.GetUtcNow())).IsEqualTo(c0).Because($"heard from at +{at}");
            await Assert.That(await OldestAsync(store)).IsEqualTo(c0).Because($"inside 24 h the ack holds its rows (+{at})");
        }

        store.BackupAnchor = c1;
        await AgePrefixAsync(store, c2, TimeSpan.FromHours(25));
        await peer.ReportFrontierAsync();
        await gru.TrimAsync();
        await Assert.That(await gru.FloorAsync(peer.Clock.GetUtcNow())).IsEqualTo(c0).Because("the peer still counts as live");
        await Assert.That(await OldestAsync(store)).IsEqualTo(c1).Because("the backup anchor still holds its rows");

        store.BackupAnchor = null;
        // Cut before releasing the late ACK so the next sender pass must refill, even when bookkeeping trails it.
        await gru.TrimAsync();
        await Assert.That(await OldestAsync(store)).IsEqualTo(c2);
        await peer.AckAsync(pending);
        await Assert.That(await gru.UntilAckedAsync(peer.Replica, c1)).IsTrue();
        await Assert.That(await gru.FloorAsync(peer.Clock.GetUtcNow())).IsEqualTo(c1).Because("slow progress does not renew the hold");
        await Assert.That(await OldestAsync(store)).IsEqualTo(c2).Because("past 24 h the ack holds nothing; the newest commit stays");
        var head = await gru.HeadAsync();
        await Assert.That((await peer.NextAsync()).Refill?.ChangeId).IsEqualTo((ulong)head).Because("the live sender refills after its late ack");
    }

    [Test]
    public async Task An_evicted_peer_holds_no_rows_during_the_first_24_hours_after_a_start()
    {
        await using var peer = await ScriptedPeer.StartAsync();
        var gru = peer.Sender;
        var store = await gru.StoreAsync();
        var c0 = await gru.HeadAsync();
        var first = await peer.NextAsync();
        store.BackupAnchor = null;
        await peer.AckAsync(first);
        await Assert.That(await gru.UntilAckedAsync(peer.Replica, c0)).IsTrue();
        var evicted = ReplicaId.Of(2, 7, 0);
        _ = await Run(store, $"INSERT INTO _tb_peers (node, applied, acked, stable, seen, incarnation) VALUES ({unchecked((long)evicted.Value)}, 0, {c0}, 0, 0, '{store.Incarnation}');");
        await gru.WriteAsync(async transaction => _ = await transaction.PutAsync(Items, "newest", Json("""{"n":1}"""), CancellationToken.None));
        var newest = await gru.HeadAsync();
        var pending = await peer.NextAsync();
        peer.Clock.Offset = TimeSpan.FromMinutes(2);
        await peer.AckAsync(pending);
        await Assert.That(await gru.UntilAckedAsync(peer.Replica, newest)).IsTrue();
        await Assert.That(await gru.FloorAsync(peer.Clock.GetUtcNow())).IsEqualTo(newest).Because("seen 0 is excluded even during startup's grace period");
        await Assert.That(await OldestAsync(store)).IsEqualTo(newest);
    }

    // The listener holds the first real batch at ack 0 while a fixture appends a transaction beyond the old scan window.
    [Test]
    public async Task Rows_past_24_hours_go_whatever_the_size_of_the_transaction()
    {
        await using var peer = await ScriptedPeer.StartAsync();
        var gru = peer.Sender;
        var store = await gru.StoreAsync();
        var pending = await peer.NextAsync();
        await Assert.That(pending.Batch).IsNotNull().Because("a real unacknowledged local group must hold the oversized fixture");
        store.BackupAnchor = null;
        _ = await Run(store, """
            INSERT INTO turso_cdc (change_id, change_time, change_type, table_name, id)
            SELECT (SELECT max(change_id) FROM turso_cdc) + n, unixepoch(), -1, 'items', n
            FROM (WITH RECURSIVE r(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM r WHERE n < 70000) SELECT n FROM r);
            """);
        _ = await Run(store, "INSERT INTO turso_cdc (change_id, change_time, change_type) SELECT max(change_id) + 1, unixepoch(), 2 FROM turso_cdc;");
        _ = await Run(store, "INSERT INTO turso_cdc (change_id, change_time, change_type) SELECT max(change_id) + 1, unixepoch(), 2 FROM turso_cdc;");
        var newest = await ScalarAsync(store, "SELECT max(change_id) FROM turso_cdc;");
        var rows = await RowsAsync(store);
        await Assert.That(rows).IsGreaterThan(70_002L);

        await AgePrefixAsync(store, newest, TimeSpan.FromMinutes(2));
        await peer.ReportFrontierAsync();
        await gru.TrimAsync();
        await Assert.That(await gru.FloorAsync(peer.Clock.GetUtcNow())).IsEqualTo(0L);
        await Assert.That(await ScalarAsync(store, $"SELECT count(*) FROM turso_cdc WHERE change_id <= {newest};")).IsEqualTo(rows)
            .Because("inside 24 h the ack at 0 holds every fixture row; inbound frontier bookkeeping appends newer rows");
        await AgePrefixAsync(store, newest, TimeSpan.FromHours(25));
        await peer.ReportFrontierAsync();
        await peer.AckAsync(pending);
        await Assert.That(await gru.UntilAckedAsync(peer.Replica, ScriptedPeer.ChangeId(pending))).IsTrue();
        // An ack or a pass deletes a bounded batch; the passes that follow take the rest.
        for (var pass = 0; pass < 10 && await OldestAsync(store) != newest; pass++)
        {
            await gru.TrimAsync();
        }

        await Assert.That(await OldestAsync(store)).IsEqualTo(newest);
        await Assert.That(await ScalarAsync(store, $"SELECT count(*) FROM turso_cdc WHERE change_id <= {newest};")).IsEqualTo(1L)
            .Because("the entire oversized transaction went; only its final commit anchor stays below newer frontier bookkeeping");
    }

    // A machine absent for longer than the retention bound gets its missing write by a real refill on return.
    [Test]
    public async Task A_peer_gone_past_24_hours_rejoins_by_a_refill()
    {
        var clock = TimeProvider.System;
        await using var pair = await Pair.StartAsync(clock);
        var (gru, sjc) = (pair.Gru, pair.Sjc);
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo(Items, 1), CancellationToken.None);
            _ = await transaction.PutAsync(Items, "warm", Json("""{"n":0}"""), CancellationToken.None);
        });
        await Assert.That(await sjc.UntilAsync("warm", """{"n":0}""")).IsTrue();
        var sjcNode = sjc.Replica;
        var acked = await gru.HeadAsync();
        await Assert.That(await gru.UntilAckedAsync(sjcNode, acked)).IsTrue();

        await pair.UnlistAsync();
        await gru.WriteAsync(async transaction => _ = await transaction.PutAsync(Items, "held", Json("""{"n":1}"""), CancellationToken.None));
        var missing = await gru.HeadAsync();
        await Assert.That(await gru.RetainsAsync(acked)).IsTrue();

        // Turso timestamps CDC with real UTC: age only the disconnected prefix, never fresh refill bookkeeping.
        await AgePrefixAsync(await gru.StoreAsync(), missing, TimeSpan.FromHours(25));
        await gru.TrimAsync();
        var retainedAck = await gru.AckedAsync(sjcNode);
        await Assert.That(retainedAck).IsLessThan(missing).Because("quiet rows may advance the cursor, but the missing local group is never acknowledged");
        await Assert.That(await gru.FloorAsync(clock.GetUtcNow() + TimeSpan.FromHours(25))).IsNull().Because("sjc is excluded once its last contact is past the bound");
        await Assert.That(await gru.RetainsAsync(acked)).IsFalse().Because("the 24 h age cut passed the stuck ack");

        pair.List();
        await Assert.That(await sjc.UntilAsync("held", """{"n":1}""")).IsTrue().Because("sjc refills the cut write");
        var resumed = await gru.HeadAsync();
        await gru.WriteAsync(async transaction => _ = await transaction.PutAsync(Items, "fresh", Json("""{"n":2}"""), CancellationToken.None));
        await gru.TrimAsync();
        await Assert.That(await gru.RetainsAsync(resumed)).IsTrue().Because("fresh post-refill changes retain their incremental resume frontier");
        await Assert.That(await sjc.UntilAsync("fresh", """{"n":2}""")).IsTrue();
        await Assert.That(await gru.UntilAckedAsync(sjcNode, await gru.HeadAsync())).IsTrue();
    }

    // Age the persisted CDC fixture, keeping the live listener and its bookkeeping on the real host clock.
    private static Task<int> AgePrefixAsync(TursoDatabaseStore store, long through, TimeSpan age) =>
        Run(store, $"UPDATE turso_cdc SET change_time = unixepoch() - {(long)age.TotalSeconds} WHERE change_id <= {through};");

    private static Task<long> OldestAsync(TursoDatabaseStore store) => ScalarAsync(store, "SELECT min(change_id) FROM turso_cdc;");

    private static Task<long> RowsAsync(TursoDatabaseStore store) => ScalarAsync(store, "SELECT count(*) FROM turso_cdc;");

    private static Task<long> ScalarAsync(TursoDatabaseStore store, string sql) =>
        store.WithWriterAsync(session =>
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = sql;
            return (long)command.ExecuteScalar()!;
        }, "a test reading the change log", CancellationToken.None);

    private static Task<int> Run(TursoDatabaseStore store, string sql) =>
        store.WithWriterAsync(session =>
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteNonQuery();
        }, "a test editing the change log or the peers table", CancellationToken.None);

    private static ReadOnlyMemory<byte> Json(string json) => Encoding.UTF8.GetBytes(json);

    /// <summary>A real sender with a real gRPC listener that holds its batches until the test acknowledges them.</summary>
    private sealed class ScriptedPeer(FakeTimeProvider timers) : Features.Replication.V1.Replication.ReplicationBase, IAsyncDisposable
    {
        private readonly Channel<PushFrame> _pending = Channel.CreateBounded<PushFrame>(16);
        private readonly Channel<AckFrame> _answers = Channel.CreateBounded<AckFrame>(16);
        private WebApplication? _app;

        public TenantContext Database { get; } = new(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());

        public ReplicaId Replica { get; } = ReplicaId.Of(1, 9, 0);

        public ShiftedTime Clock { get; } = new(timers);

        public Node Sender { get; private set; } = null!;

        public static async Task<ScriptedPeer> StartAsync()
        {
            var timers = new FakeTimeProvider();
            var peer = new ScriptedPeer(timers);
            try
            {
                var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
                builder.Configuration.Sources.Clear();
                builder.Logging.ClearProviders();
                builder.WebHost.ConfigureKestrel(kestrel =>
                    kestrel.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));
                builder.Services.AddGrpc();
                builder.Services.AddSingleton(peer);
                peer._app = builder.Build();
                peer._app.MapGrpcService<ScriptedPeer>();
                await peer._app.StartAsync();
                var address = peer._app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                var members = new FakeMemberStore();
                peer.Sender = await Pair.StartNodeAsync(peer.Database, members, "gru", "sjc", peer.Clock, user: null);
                // Keep the first local group intact until the sender reaches it, even if bootstrap takes a retention tick.
                (await peer.Sender.StoreAsync()).BackupAnchor = 0;
                await peer.Sender.WriteAsync(async transaction =>
                {
                    _ = await transaction.CreateCollectionAsync(new CollectionInfo(Items, 1), CancellationToken.None);
                    _ = await transaction.PutAsync(Items, "warm", Json("""{"n":0}"""), CancellationToken.None);
                });
                members.Join(new ClusterMember("zzzz-peer", address, "sjc", ""));
                // Discover the peer, then freeze deadlines while the test holds its original batch across retention work.
                timers.Advance(DirectoryMembership.Heartbeat);
                return peer;
            }
            catch
            {
                await peer.DisposeAsync();
                throw;
            }
        }

        public Task<PushFrame> NextAsync() => _pending.Reader.ReadAsync().AsTask().WaitAsync(Deadline);

        public static long ChangeId(PushFrame frame) => checked((long)(frame.Batch?.LastChangeId ?? frame.Refill!.ChangeId));

        public async Task AckAsync(PushFrame frame) => await _answers.Writer.WriteAsync(new AckFrame
        {
            Ack = new Ack { Database = frame.Batch?.Database ?? frame.Refill!.Database, ChangeId = checked((ulong)ChangeId(frame)) },
        }).AsTask().WaitAsync(Deadline);

        public async Task ReportFrontierAsync()
        {
            using var channel = PeerChannel.Create(Sender.Api.Server.BaseAddress, Sender.Api.Server.Credential);
            using var deadline = new CancellationTokenSource(Deadline);
            using var call = new Features.Replication.V1.Replication.ReplicationClient(channel).Push(cancellationToken: deadline.Token);
            await call.RequestStream.WriteAsync(new PushFrame
            {
                Hello = new Hello { Node = Replica.Value, Region = "sjc", Machine = "zzzz-peer" },
            }, deadline.Token);
            await Assert.That(await call.ResponseStream.MoveNext(deadline.Token)).IsTrue();
            await Assert.That(call.ResponseStream.Current.Welcome).IsNotNull();
            await call.RequestStream.WriteAsync(new PushFrame
            {
                Frontier = new Frontier { Database = ChangeGroups.Wire(Database, (await Sender.StoreAsync()).Incarnation) },
            }, deadline.Token);
            await Assert.That(await call.ResponseStream.MoveNext(deadline.Token)).IsTrue();
            await Assert.That(call.ResponseStream.Current.Stable).IsNotNull();
            await call.RequestStream.CompleteAsync();
            while (await call.ResponseStream.MoveNext(deadline.Token))
            {
            }
        }

        public override async Task Push(IAsyncStreamReader<PushFrame> requestStream, IServerStreamWriter<AckFrame> responseStream, ServerCallContext context)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            var writing = WriteAnswersAsync(responseStream, stop.Token);
            try
            {
                while (await requestStream.MoveNext(stop.Token))
                {
                    var frame = requestStream.Current;
                    if (frame.Hello is not null)
                    {
                        await _answers.Writer.WriteAsync(new AckFrame { Welcome = new Welcome { Node = Replica.Value } }, stop.Token);
                    }
                    else if (frame.Heartbeat is not null)
                    {
                        await _answers.Writer.WriteAsync(new AckFrame(), stop.Token);
                    }
                    else if (frame.Frontier is { } frontier)
                    {
                        await _answers.Writer.WriteAsync(new AckFrame { Stable = new StableReport { Database = frontier.Database } }, stop.Token);
                    }
                    else if ((frame.Batch?.Database ?? frame.Refill?.Database) is { } database)
                    {
                        if (ChangeGroups.Tenant(database) == Database)
                        {
                            await _pending.Writer.WriteAsync(frame, stop.Token);
                        }
                        else
                        {
                            await _answers.Writer.WriteAsync(new AckFrame { Ack = new Ack { Database = database, Refused = true } }, stop.Token);
                        }
                    }
                }
            }
            finally
            {
                await stop.CancelAsync();
                await writing.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }

        private async Task WriteAnswersAsync(IServerStreamWriter<AckFrame> responseStream, CancellationToken cancellationToken)
        {
            await foreach (var answer in _answers.Reader.ReadAllAsync(cancellationToken))
            {
                await responseStream.WriteAsync(answer, cancellationToken);
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (Sender is not null)
                {
                    await Sender.DisposeAsync();
                }
            }
            finally
            {
                if (_app is not null)
                {
                    await _app.DisposeAsync();
                }
            }
        }
    }

    /// <summary>One machine: the real server with its CDC tailer, holding the pair's database.</summary>
    private sealed class Node(ManagementApi api, TenantContext database) : IAsyncDisposable
    {
        public ManagementApi Api { get; } = api;

        public ReplicaId Replica => Api.Server.Services.GetRequiredService<LocalReplica>().Id;

        public IChangeGroupSource Groups => Api.Server.Services.GetRequiredService<IChangeGroupSource>();

        public async Task<TursoDatabaseStore> StoreAsync() =>
            (TursoDatabaseStore)(await Api.Server.Services.GetRequiredService<TursoStorageEngine>().OpenDatabaseAsync(database, CancellationToken.None)
                ?? throw new InvalidOperationException("the database is missing"));

        public async Task WriteAsync(Func<IStorageTransaction, Task> work) =>
            _ = await (await StoreAsync()).WriteAsync(async (transaction, _) =>
            {
                await work(transaction);
                return true;
            }, static _ => true, CancellationToken.None);

        public async Task<long> HeadAsync() =>
            await Groups.HeadAsync(database, (await StoreAsync()).Incarnation, CancellationToken.None) ?? throw new InvalidOperationException("the copy refused its own incarnation");

        public async Task<bool> RetainsAsync(long after) =>
            await Groups.RetainsAsync(database, (await StoreAsync()).Incarnation, after, CancellationToken.None) ?? throw new InvalidOperationException("the copy refused its own incarnation");

        public async Task<long> AckedAsync(ReplicaId peer)
        {
            var cursor = await Groups.AckedAsync(database, peer, CancellationToken.None) ?? throw new InvalidOperationException("the database is missing");
            return cursor.Incarnation == (await StoreAsync()).Incarnation ? cursor.Acked : throw new InvalidOperationException("the cursor names another incarnation");
        }

        // One retention pass, as the tailer's 5 s pass runs it.
        public async Task TrimAsync() => await Api.Server.Services.GetRequiredService<TursoChangeGroupSource>().TrimAsync(await StoreAsync(), CancellationToken.None);

        public async Task<long?> FloorAsync(DateTimeOffset now)
        {
            var transaction = (TursoStorageTransaction)await (await StoreAsync()).BeginTransactionAsync(write: false, CancellationToken.None);
            await using (transaction)
            {
                return await transaction.GetFloorAsync(now, CancellationToken.None);
            }
        }

        public async Task<bool> UntilAckedAsync(ReplicaId peer, long changeId)
        {
            var deadline = DateTime.UtcNow + Deadline;
            while (DateTime.UtcNow < deadline)
            {
                var store = await StoreAsync();
                var transaction = (TursoStorageTransaction)await store.BeginTransactionAsync(write: false, CancellationToken.None);
                await using (transaction)
                {
                    if (await transaction.GetAckedAsync(peer, store.Incarnation, CancellationToken.None) is { RequiresRefill: false } cursor
                        && cursor.Acked >= changeId)
                    {
                        return true;
                    }
                }

                await Task.Delay(100);
            }

            return false;
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

        private async Task<string?> ReadAsync(string id)
        {
            if (await Api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, CancellationToken.None) is not { } store)
            {
                return null;
            }

            await using var transaction = await store.BeginTransactionAsync(write: false, CancellationToken.None);
            try
            {
                return await transaction.GetAsync(Items, id, CancellationToken.None) is { Deleted: false } document ? Encoding.UTF8.GetString(document.Json.Span) : null;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        public ValueTask DisposeAsync() => Api.DisposeAsync();
    }

    /// <summary>gru and sjc in one fake DNS on one shifted host clock, both holding one database.</summary>
    private sealed class Pair : IAsyncDisposable
    {
        private readonly FakeMemberStore _members = new();
        private readonly List<ClusterMember> _unlisted = [];

        public TenantContext Database { get; } = new(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());

        public Node Gru { get; private set; } = null!;

        public Node Sjc { get; private set; } = null!;

        public static async Task<Pair> StartAsync(TimeProvider clock)
        {
            var pair = new Pair();
            try
            {
                pair.Gru = await StartNodeAsync(pair.Database, pair._members, "gru", "sjc", clock, user: null);
                pair.Sjc = await StartNodeAsync(pair.Database, pair._members, "sjc", "gru", clock, pair.Gru.Api.User);
                return pair;
            }
            catch
            {
                await pair.DisposeAsync();
                throw;
            }
        }

        public static async Task<Node> StartNodeAsync(TenantContext database, FakeMemberStore members, string region, string replica, TimeProvider clock, string? user)
        {
            var name = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())[..14];
            var api = await ManagementApi.StartAsync(settings =>
            {
                settings["region"] = region;
                settings["node-name"] = name;
            }, services => services.AddSingleton<IMemberDirectory>(members), time: clock, userId: user);
            try
            {
                var commit = JsonSerializer.Serialize(new
                {
                    id = EntityIds.New().ToString(),
                    ops = new object[]
                    {
                        new { op = "create_organization", id = database.OrganizationId, name = $"age-{database.OrganizationId}" },
                        new { op = "create_project", organization = database.OrganizationId, id = database.ProjectId, name = "age" },
                        new { op = "create_database", organization = database.OrganizationId, project = database.ProjectId, name = "main", id = database.DatabaseId, replica_regions = new[] { replica } },
                    },
                });
                using var service = api.Service(api.User);
                using var response = await service.PostAsync(new Uri("/v1/management/commits", UriKind.Relative), new StringContent(commit, Encoding.UTF8, "application/json"));
                await Assert.That(response.IsSuccessStatusCode).IsTrue().Because(await response.Content.ReadAsStringAsync());
                return new Node(api, database);
            }
            catch
            {
                await api.DisposeAsync();
                throw;
            }
        }

        // Drops both machines from DNS and waits until neither streams to the other.
        public async Task UnlistAsync()
        {
            _unlisted.AddRange(await _members.ListAsync(CancellationToken.None));
            foreach (var member in _unlisted)
            {
                _members.Leave(member.Name);
            }

            foreach (var node in new[] { Gru, Sjc })
            {
                var deadline = DateTime.UtcNow + Deadline;
                while (!await StreamsNoneAsync(node))
                {
                    if (DateTime.UtcNow >= deadline)
                    {
                        throw new TimeoutException("a machine kept its stream to an unlisted machine");
                    }

                    await Task.Delay(100);
                }
            }
        }

        public void List()
        {
            foreach (var member in _unlisted)
            {
                _members.Join(member);
            }

            _unlisted.Clear();
        }

        private static async Task<bool> StreamsNoneAsync(Node node)
        {
            using var response = await node.Api.Server.Client.GetAsync(new Uri("/replication", UriKind.Relative));
            return JsonElement.Parse(await response.Content.ReadAsStringAsync()).GetProperty("peers").GetArrayLength() == 0;
        }

        public async ValueTask DisposeAsync()
        {
            if (Sjc is not null)
            {
                await Sjc.DisposeAsync();
            }

            if (Gru is not null)
            {
                await Gru.DisposeAsync();
            }
        }
    }

    private sealed class ShiftedTime(TimeProvider timers) : TimeProvider
    {
        private long _offset;

        public TimeSpan Offset
        {
            get => TimeSpan.FromTicks(Interlocked.Read(ref _offset));
            set => Interlocked.Exchange(ref _offset, value.Ticks);
        }

        public override long TimestampFrequency => timers.TimestampFrequency;

        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + Offset;

        public override long GetTimestamp() => timers.GetTimestamp();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            timers.CreateTimer(callback, state, dueTime, period);
    }
}
