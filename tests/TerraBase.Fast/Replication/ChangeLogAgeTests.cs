using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Sqlite;
using TerraBase.Fast.Documents;
using TerraBase.Features.Replication;
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

    // A peer repeatedly acks c0, then advances to c1 after 25 h. Neither activity nor that slow progress renews the hold:
    // the backup anchor (c1) still keeps rows, but without it only the newest commit (c2) stays.
    [Test]
    public async Task A_peer_whose_ack_trickles_does_not_hold_rows_past_24_hours()
    {
        await using var api = await DocumentApi.StartAsync();
        var (store, c0, c1, c2) = await CommitsAsync(api);
        var peer = ReplicaId.Of(2, 7, 0);
        var start = DateTimeOffset.UtcNow;

        foreach (var at in new[] { TimeSpan.FromMinutes(2), TimeSpan.FromHours(12), TimeSpan.FromHours(23) })
        {
            await Assert.That(await AckAsync(store, peer, c0, start + at, backupAnchor: null)).IsEqualTo(c0).Because($"heard from at +{at}");
            await Assert.That(await OldestAsync(store)).IsEqualTo(c0).Because($"inside 24 h the ack holds its rows (+{at})");
        }

        await Assert.That(await AckAsync(store, peer, c0, start + TimeSpan.FromHours(25), backupAnchor: c1)).IsEqualTo(c0).Because("the peer still counts as live");
        await Assert.That(await OldestAsync(store)).IsEqualTo(c1).Because("the backup anchor still holds its rows");

        await Assert.That(await AckAsync(store, peer, c1, start + TimeSpan.FromHours(25), backupAnchor: null)).IsEqualTo(c1).Because("slow progress does not renew the hold");
        await Assert.That(await OldestAsync(store)).IsEqualTo(c2).Because("past 24 h the ack holds nothing; the newest commit stays");
        var groups = api.Server.Services.GetRequiredService<IChangeGroupSource>();
        await Assert.That(await groups.RetainsAsync(await api.TenantOfAsync(), c1, CancellationToken.None)).IsFalse().Because("the peer refills even after its ack advances");
    }

    // The far-behind eviction sets seen to 0; within the 24 h after a start (GoneBefore is long.MinValue then) that row must
    // not hold rows either.
    [Test]
    public async Task An_evicted_peer_holds_no_rows_during_the_first_24_hours_after_a_start()
    {
        await using var api = await DocumentApi.StartAsync();
        var (store, c0, _, c2) = await CommitsAsync(api);
        var now = DateTimeOffset.UtcNow.AddMinutes(2);
        var evicted = ReplicaId.Of(2, 7, 0);
        _ = await AckAsync(store, evicted, c0, now, backupAnchor: null);
        await Assert.That(await OldestAsync(store)).IsEqualTo(c0);
        _ = await Run(store, $"UPDATE _tb_peers SET seen = 0 WHERE node = {unchecked((long)evicted.Value)};");

        await Assert.That(await AckAsync(store, ReplicaId.Of(3, 7, 0), c2, now, backupAnchor: null)).IsEqualTo(c2).Because("the evicted row is no floor");
        await Assert.That(await OldestAsync(store)).IsEqualTo(c2);
    }

    // One transaction larger than any fixed scan window (70,000 captured rows, then its commit) and a newer commit, all past
    // 24 h, with a live peer at ack 0: retention reaches the newest commit, never stops before the first commit row.
    [Test]
    public async Task Rows_past_24_hours_go_whatever_the_size_of_the_transaction()
    {
        await using var api = await DocumentApi.StartAsync();
        var (store, _, _, _) = await CommitsAsync(api);
        var peer = ReplicaId.Of(2, 7, 0);
        _ = await AckAsync(store, peer, 0, DateTimeOffset.UtcNow.AddMinutes(2), backupAnchor: null);
        _ = await Run(store, """
            INSERT INTO turso_cdc (change_id, change_time, change_type, table_name, id)
            SELECT (SELECT max(change_id) FROM turso_cdc) + n, unixepoch(), -1, 'items', n
            FROM (WITH RECURSIVE r(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM r WHERE n < 70000) SELECT n FROM r);
            """);
        _ = await Run(store, "INSERT INTO turso_cdc (change_id, change_time, change_type) SELECT max(change_id) + 1, unixepoch(), 2 FROM turso_cdc;");
        _ = await Run(store, "INSERT INTO turso_cdc (change_id, change_time, change_type) SELECT max(change_id) + 1, unixepoch(), 2 FROM turso_cdc;");
        _ = await Run(store, "DELETE FROM turso_cdc WHERE change_id <= (SELECT max(change_id) - 70002 FROM turso_cdc);");
        var newest = await ScalarAsync(store, "SELECT max(change_id) FROM turso_cdc;");
        await Assert.That(await RowsAsync(store)).IsEqualTo(70_002L);

        _ = await AckAsync(store, peer, 0, DateTimeOffset.UtcNow.AddMinutes(2), backupAnchor: null);
        await Assert.That(await RowsAsync(store)).IsEqualTo(70_002L).Because("inside 24 h the ack at 0 holds every row");
        _ = await AckAsync(store, peer, 0, DateTimeOffset.UtcNow.AddHours(25), backupAnchor: null);
        await Assert.That(await OldestAsync(store)).IsEqualTo(newest);
    }

    // Neither machine streams to the other; sjc is heard from at the shifted clock (a stable report, as its stream sends
    // them) while its ack stays below gru's write, which it does not get. 25 hours later retention cuts that write's rows though sjc is live, and once gru streams
    // to it again sjc gets the write by a refill and acks gru's head.
    [Test]
    public async Task A_live_peer_whose_ack_is_stuck_past_24_hours_rejoins_by_a_refill()
    {
        var clock = new ShiftedTime();
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
        await Assert.That(await gru.Groups.RetainsAsync(pair.Database, acked, CancellationToken.None)).IsTrue();

        clock.Offset = TimeSpan.FromHours(25);
        await gru.Groups.RecordStableAsync(pair.Database, sjcNode, default, CancellationToken.None);
        await gru.TrimAsync();
        var retainedAck = await gru.Groups.AckedAsync(pair.Database, sjcNode, CancellationToken.None);
        await Assert.That(retainedAck).IsLessThan(missing).Because("quiet rows may advance the cursor, but the missing local group is never acknowledged");
        await Assert.That(await gru.FloorAsync(clock.GetUtcNow())).IsEqualTo(retainedAck).Because("sjc still counts as heard from");
        await Assert.That(await gru.Groups.RetainsAsync(pair.Database, acked, CancellationToken.None)).IsFalse().Because("the 24 h age cut passed the stuck ack");

        pair.List();
        await Assert.That(await sjc.UntilAsync("held", """{"n":1}""")).IsTrue().Because("sjc refills the cut write");
        await Assert.That(await gru.UntilAckedAsync(sjcNode, await gru.HeadAsync())).IsTrue();
    }

    private static async Task<(SqliteDatabaseStore Store, long C0, long C1, long C2)> CommitsAsync(DocumentApi api)
    {
        foreach (var n in new[] { 1, 2, 3 })
        {
            (await api.PutAsync(DocumentApi.NewId(), new { n })).Status.ShouldBe(HttpStatusCode.Created);
        }

        var store = (SqliteDatabaseStore)(await api.Server.Services.GetRequiredService<SqliteStorageEngine>().OpenDatabaseAsync(await api.TenantOfAsync(), CancellationToken.None))!;
        var commits = await store.WithWriterAsync(session =>
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = "SELECT change_id FROM turso_cdc WHERE change_type = 2 ORDER BY change_id DESC LIMIT 3;";
            using var reader = command.ExecuteReader();
            List<long> ids = [];
            while (reader.Read())
            {
                ids.Insert(0, reader.GetInt64(0));
            }

            return ids;
        }, "a test reading the change log's commits", CancellationToken.None);
        await Assert.That(commits.Count).IsEqualTo(3);
        return (store, commits[0], commits[1], commits[2]);
    }

    // Records the peer's ack as an acknowledgement on the stream does, at `now`; the floor every live peer acked.
    private static async Task<long?> AckAsync(SqliteDatabaseStore store, ReplicaId peer, long changeId, DateTimeOffset now, long? backupAnchor)
    {
        var transaction = (SqliteStorageTransaction)await store.BeginTransactionAsync(write: true, CancellationToken.None);
        await using (transaction)
        {
            var (_, floor) = await transaction.RecordAckedAsync(peer, changeId, default, backupAnchor, now, CancellationToken.None);
            _ = await transaction.CommitAsync(CancellationToken.None);
            return floor;
        }
    }

    private static Task<long> OldestAsync(SqliteDatabaseStore store) => ScalarAsync(store, "SELECT min(change_id) FROM turso_cdc;");

    private static Task<long> RowsAsync(SqliteDatabaseStore store) => ScalarAsync(store, "SELECT count(*) FROM turso_cdc;");

    private static Task<long> ScalarAsync(SqliteDatabaseStore store, string sql) =>
        store.WithWriterAsync(session =>
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = sql;
            return (long)command.ExecuteScalar()!;
        }, "a test reading the change log", CancellationToken.None);

    private static Task<int> Run(SqliteDatabaseStore store, string sql) =>
        store.WithWriterAsync(session =>
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteNonQuery();
        }, "a test editing the change log or the peers table", CancellationToken.None);

    private static ReadOnlyMemory<byte> Json(string json) => Encoding.UTF8.GetBytes(json);

    /// <summary>One machine: the real server with its CDC tailer, holding the pair's database.</summary>
    private sealed class Node(ManagementApi api, TenantContext database) : IAsyncDisposable
    {
        public ManagementApi Api { get; } = api;

        public ReplicaId Replica => Api.Server.Services.GetRequiredService<LocalReplica>().Id;

        public IChangeGroupSource Groups => Api.Server.Services.GetRequiredService<IChangeGroupSource>();

        public async Task<SqliteDatabaseStore> StoreAsync() =>
            (SqliteDatabaseStore)(await Api.Server.Services.GetRequiredService<SqliteStorageEngine>().OpenDatabaseAsync(database, CancellationToken.None)
                ?? throw new InvalidOperationException("the database is missing"));

        public async Task WriteAsync(Func<IStorageTransaction, Task> work) =>
            _ = await (await StoreAsync()).WriteAsync(async (transaction, _) =>
            {
                await work(transaction);
                return true;
            }, static _ => true, CancellationToken.None);

        public async Task<long> HeadAsync() => await Groups.HeadAsync(database, CancellationToken.None);

        // One retention pass, as the tailer's 5 s pass runs it.
        public async Task TrimAsync() => await ((TursoChangeGroupSource)Groups).TrimAsync(await StoreAsync(), CancellationToken.None);

        public async Task<long?> FloorAsync(DateTimeOffset now)
        {
            var transaction = (SqliteStorageTransaction)await (await StoreAsync()).BeginTransactionAsync(write: false, CancellationToken.None);
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
                var transaction = (SqliteStorageTransaction)await (await StoreAsync()).BeginTransactionAsync(write: false, CancellationToken.None);
                await using (transaction)
                {
                    if (await transaction.GetAckedAsync(peer, CancellationToken.None) >= changeId)
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
                pair.Gru = await pair.StartAsync("gru", "sjc", clock, user: null);
                pair.Sjc = await pair.StartAsync("sjc", "gru", clock, pair.Gru.Api.User);
                return pair;
            }
            catch
            {
                await pair.DisposeAsync();
                throw;
            }
        }

        private async Task<Node> StartAsync(string region, string replica, TimeProvider clock, string? user)
        {
            var name = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())[..14];
            var api = await ManagementApi.StartAsync(settings =>
            {
                settings["region"] = region;
                settings["node-name"] = name;
            }, services => services.AddSingleton<IMemberDirectory>(_members), time: clock, userId: user);
            var node = new Node(api, Database);
            var commit = JsonSerializer.Serialize(new
            {
                id = EntityIds.New().ToString(),
                ops = new object[]
                {
                    new { op = "create_organization", id = Database.OrganizationId, name = $"age-{Database.OrganizationId}" },
                    new { op = "create_project", organization = Database.OrganizationId, id = Database.ProjectId, name = "age" },
                    new { op = "create_database", organization = Database.OrganizationId, project = Database.ProjectId, name = "main", id = Database.DatabaseId, replica_regions = new[] { replica } },
                },
            });
            using var service = api.Service(api.User);
            using var response = await service.PostAsync(new Uri("/v1/management/commits", UriKind.Relative), new StringContent(commit, Encoding.UTF8, "application/json"));
            await Assert.That(response.IsSuccessStatusCode).IsTrue().Because(await response.Content.ReadAsStringAsync());
            return node;
        }

        // Drops both machines from the DNS and waits until neither streams to the other: from then on only the test marks
        // a peer heard from.
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

    private sealed class ShiftedTime : TimeProvider
    {
        private long _offset;

        public TimeSpan Offset
        {
            get => TimeSpan.FromTicks(Interlocked.Read(ref _offset));
            set => Interlocked.Exchange(ref _offset, value.Ticks);
        }

        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + Offset;
    }
}
