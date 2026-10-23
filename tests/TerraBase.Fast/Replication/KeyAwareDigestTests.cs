using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Sqlite;
using TerraBase.Features.Replication;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

/// <summary>
/// The key-aware document digest (docs/terrabase/multi-region-replication.md section 10) on real nodes. A stored digest
/// covers the document's key as well as its state, so identical states at different (collection, id) pairs, including
/// ambiguous raw concatenations such as ('a', 'bc') and ('ab', 'c'), store different digests. A same-bucket swap of two
/// documents' states is detected by anti-entropy (the bucket digest differs), and both nodes converge to the lattice join
/// of the two states. Convergence is not restoration: a join keeps every field either side holds, so each swapped
/// document gains the other's field instead of returning to its original body.
/// </summary>
public sealed class KeyAwareDigestTests
{
    private const string Items = "items";
    private static readonly ReplicaId Origin = ReplicaId.Of(99, 1, 0);

    [Test]
    public async Task Identical_states_at_ambiguous_concatenations_store_distinct_digests()
    {
        await using var pair = await Pair.StartAsync();
        var gru = pair.Gru;
        var state = DocumentState.Bottom.Put(new EntryVersion(new Hlc(1_000), Origin), Json("""{"n":1}"""));

        // Only the key differs: independently vary collection and id, including ambiguous concatenations.
        var sink = gru.Api.Server.Services.GetRequiredService<IChangeGroupSink>();
        var keys = new[] { (Collection: "a", Id: "bc"), (Collection: "a", Id: "c"), (Collection: "ab", Id: "bc"), (Collection: "ab", Id: "c") };
        await Assert.That(await sink.RepairAsync(gru.Database, [.. keys.Select(key => new RepairState(key.Collection, key.Id, state))], CancellationToken.None))
            .IsEqualTo(keys.Length);

        var first = await gru.StoredAsync(keys[0].Collection, keys[0].Id);
        List<UInt128> digests = [];
        foreach (var (collection, id) in keys)
        {
            var stored = await gru.StoredAsync(collection, id);
            await Assert.That(stored.State).IsEqualTo(first.State);
            await Assert.That(stored.Stamp).IsEqualTo(first.Stamp);
            await Assert.That(stored.Body).IsEqualTo(first.Body);
            await Assert.That(stored.Digest).IsEqualTo(DocumentStateEncoding.Digest(collection, id, state));
            digests.Add(stored.Digest);
        }

        await Assert.That(digests.Distinct().Count()).IsEqualTo(keys.Length);
    }

    [Test]
    public async Task A_same_bucket_swap_is_detected_by_keyed_digests_and_both_nodes_converge_to_the_join()
    {
        await using var pair = await Pair.StartAsync();
        var (gru, sjc) = (pair.Gru, pair.Sjc);
        var (first, second) = SameBucketIds();
        var bucket = SqliteStorageTransaction.Bucket(Items, first);
        var originals = new[]
        {
            new RepairState(Items, first, DocumentState.Bottom.Set(new EntryVersion(new Hlc(1_000), Origin), "x", Json("1"))),
            new RepairState(Items, second, DocumentState.Bottom.Set(new EntryVersion(new Hlc(2_000), Origin), "y", Json("2"))),
        };
        await gru.SeedAsync(originals);
        await sjc.SeedAsync(originals);
        var expectedBucket = await gru.BucketAsync(bucket);
        await Assert.That((await sjc.BucketAsync(bucket)).SequenceEqual(expectedBucket)).IsTrue();

        var (stateFirst, stateSecond) = (await sjc.StateAsync(first), await sjc.StateAsync(second));
        var joined = stateFirst.Join(stateSecond);

        // Each row takes the other's state with a digest computed for its own key: the swap a wrong-key write would leave.
        // Without the key in the digest, the swapped digests XOR to the same bucket value, so anti-entropy would see nothing.
        await sjc.MisplaceAsync(first, stateSecond);
        await sjc.MisplaceAsync(second, stateFirst);
        await Assert.That((await sjc.BucketAsync(bucket)).SequenceEqual(expectedBucket)).IsFalse().Because("keyed digests change the bucket");

        // Anti-entropy repairs buckets only after they differ in two consecutive rounds.
        await sjc.AntiEntropy.RunRoundAsync(CancellationToken.None);
        await sjc.AntiEntropy.RunRoundAsync(CancellationToken.None);
        await Assert.That(await sjc.StateAsync(first)).IsEqualTo(joined);
        await Assert.That(await sjc.StateAsync(second)).IsEqualTo(joined);

        // gru pulls the join from sjc on its own rounds, so both nodes end on the same states.
        await gru.AntiEntropy.RunRoundAsync(CancellationToken.None);
        await gru.AntiEntropy.RunRoundAsync(CancellationToken.None);
        await Assert.That(await gru.StateAsync(first)).IsEqualTo(joined);
        await Assert.That(await gru.StateAsync(second)).IsEqualTo(joined);
        var repairedBucket = await gru.BucketAsync(bucket);
        await Assert.That((await sjc.BucketAsync(bucket)).SequenceEqual(repairedBucket)).IsTrue();
        await Assert.That(await sjc.ReadAsync(first)).IsEqualTo(Encoding.UTF8.GetString(joined.Materialize() ?? []));
        await Assert.That(await gru.ReadAsync(second)).IsEqualTo(Encoding.UTF8.GetString(joined.Materialize() ?? []));
    }

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    // 4097 unique ids guarantee a collision in 4096 buckets.
    private static (string First, string Second) SameBucketIds()
    {
        Dictionary<int, string> seen = [];
        for (var i = 0; i <= SqliteStorageTransaction.Buckets; i++)
        {
            var id = EntityIds.New().ToString();
            var bucket = SqliteStorageTransaction.Bucket(Items, id);
            if (seen.TryGetValue(bucket, out var first))
            {
                return (first, id);
            }

            seen[bucket] = id;
        }

        throw new InvalidOperationException("4097 keys must share one of 4096 buckets");
    }

    // A node of the pair: its own data directory, its own listeners, the same database as the other node.
    private sealed class Node : IAsyncDisposable
    {
        private readonly ManualRoundsTime _time;

        private Node(ManagementApi api, TenantContext database, ManualRoundsTime time)
        {
            (Api, Database, _time) = (api, database, time);
        }

        public ManagementApi Api { get; }

        public TenantContext Database { get; }

        public static async Task<Node> StartAsync(string region, FakeMemberStore members, string replica, TenantContext database, string user)
        {
            var name = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())[..14];
            var time = new ManualRoundsTime();
            var api = await ManagementApi.StartAsync(settings =>
            {
                settings["region"] = region;
                settings["node-name"] = name;
            }, services => services.AddSingleton<IMemberDirectory>(members), time: time, userId: user);
            var node = new Node(api, database, time);
            try
            {
                await node.RegisterAsync(replica);
            }
            catch
            {
                await node.DisposeAsync();
                throw;
            }

            return node;
        }

        public async Task DiscoverPeerAsync()
        {
            _time.AdvanceMembership();
            var view = await Api.Server.Services.GetRequiredService<IMembership>().RefreshAsync(CancellationToken.None);
            await Assert.That(view.Elsewhere.Length).IsEqualTo(1);
        }

        public async Task SeedAsync(RepairState[] states)
        {
            var sink = Api.Server.Services.GetRequiredService<IChangeGroupSink>();
            await Assert.That(await sink.RepairAsync(Database, states, CancellationToken.None)).IsEqualTo(states.Length);
        }

        public async Task<string?> ReadAsync(string id)
        {
            await using var transaction = await (await OpenAsync()).BeginTransactionAsync(write: false, CancellationToken.None);
            return await transaction.GetAsync(Items, id, CancellationToken.None) is { Deleted: false } document ? Encoding.UTF8.GetString(document.Json.Span) : null;
        }

        // The 16 bytes of one bucket of the database's digests, as anti-entropy compares them.
        public async Task<byte[]> BucketAsync(int bucket)
        {
            var replica = Api.Server.Services.GetRequiredService<IReplicaStates>();
            var incarnation = ((SqliteDatabaseStore)await OpenAsync()).Incarnation;
            return await replica.DigestAsync(Database, incarnation, CancellationToken.None) is { } digests
                ? digests.Buckets.AsSpan(bucket * 16, 16).ToArray()
                : throw new InvalidOperationException("the database is not held");
        }

        // The whole state of the document, read through the bucket path anti-entropy uses.
        public async Task<DocumentState> StateAsync(string id)
        {
            var replica = Api.Server.Services.GetRequiredService<IReplicaStates>();
            var incarnation = ((SqliteDatabaseStore)await OpenAsync()).Incarnation;
            var (states, _) = await replica.ReadBucketsAsync(Database, incarnation, [Bucket(id)], null, 64, ChangeGroups.MaxBatchBytes, CancellationToken.None)
                ?? throw new InvalidOperationException("the database is not held");
            return states.Single(state => state.Collection == Items && state.Id == id).State;
        }

        public AntiEntropy AntiEntropy => Api.Server.Services.GetRequiredService<AntiEntropy>();

        // The stored row and its materialized body, as the engine wrote them.
        public async Task<Stored> StoredAsync(string collection, string id)
        {
            var store = (SqliteDatabaseStore)await OpenAsync();
            return await store.WithWriterAsync(session =>
            {
                using var row = session.Connection.CreateCommand();
                row.CommandText = "SELECT state, stamp, h1, h2 FROM _tb_state WHERE collection = $collection AND id = $id;";
                row.Parameters.AddWithValue("$collection", collection);
                row.Parameters.AddWithValue("$id", id);
                using var reader = row.ExecuteReader();
                if (!reader.Read())
                {
                    throw new InvalidDataException("a test reads a stored state that is not there");
                }

                var stored = new Stored(reader.GetString(0), reader.GetInt64(1), new UInt128(unchecked((ulong)reader.GetInt64(2)), unchecked((ulong)reader.GetInt64(3))), "");
                reader.Close();
                using var body = session.Connection.CreateCommand();
                body.CommandText = $"SELECT doc FROM \"{collection}\" WHERE id = $id;";
                body.Parameters.AddWithValue("$id", id);
                return stored with { Body = (string)body.ExecuteScalar()! };
            }, "a test reading a stored state", CancellationToken.None);
        }

        // Writes `state` into the row of (Items, id) as a wrong-key write would: the state, its body, and the digest of
        // that key. It bypasses the change feed, as a damaged file would.
        public async Task MisplaceAsync(string id, DocumentState state)
        {
            var store = (SqliteDatabaseStore)await OpenAsync();
            var stored = Base64Url.EncodeToString(DocumentStateEncoding.EncodeStored(state));
            var body = Encoding.UTF8.GetString(state.Materialize() ?? []);
            var digest = DocumentStateEncoding.Digest(Items, id, state);
            var rows = await store.WithWriterAsync(session =>
            {
                using var transaction = session.Connection.BeginTransaction();
                using var row = session.Connection.CreateCommand();
                row.Transaction = transaction;
                row.CommandText = "UPDATE _tb_state SET state = $state, stamp = $stamp, h1 = $h1, h2 = $h2 WHERE collection = $collection AND id = $id;";
                row.Parameters.AddWithValue("$state", stored);
                row.Parameters.AddWithValue("$stamp", unchecked((long)state.MaxStamp.Value));
                row.Parameters.AddWithValue("$h1", unchecked((long)(ulong)(digest >> 64)));
                row.Parameters.AddWithValue("$h2", unchecked((long)(ulong)digest));
                row.Parameters.AddWithValue("$collection", Items);
                row.Parameters.AddWithValue("$id", id);
                using var document = session.Connection.CreateCommand();
                document.Transaction = transaction;
                document.CommandText = $"UPDATE \"{Items}\" SET doc = $doc WHERE id = $id;";
                document.Parameters.AddWithValue("$doc", body);
                document.Parameters.AddWithValue("$id", id);
                var changed = row.ExecuteNonQuery() + document.ExecuteNonQuery();
                MerkleFixture.Rebuild(session, transaction);
                transaction.Commit();
                return changed;
            }, "a test misplacing a state", CancellationToken.None);
            store.ForgetCollections();
            await Assert.That(rows).IsEqualTo(2);
        }

        public ValueTask DisposeAsync() => Api.DisposeAsync();

        private static int Bucket(string id) => SqliteStorageTransaction.Bucket(Items, id);

        private async Task<IDatabaseStore> OpenAsync() =>
            await Api.Server.Services.GetRequiredService<SqliteStorageEngine>().OpenDatabaseAsync(Database, CancellationToken.None) is { } store
                ? store
                : throw new InvalidOperationException("the database is not held");

        private async Task RegisterAsync(string replica)
        {
            var commit = JsonSerializer.Serialize(new
            {
                id = EntityIds.New().ToString(),
                ops = new object[]
                {
                    new { op = "create_organization", id = Database.OrganizationId, name = $"key-digest-{Database.OrganizationId}" },
                    new { op = "create_project", organization = Database.OrganizationId, id = Database.ProjectId, name = "key-digest" },
                    new { op = "create_database", organization = Database.OrganizationId, project = Database.ProjectId, name = "main", id = Database.DatabaseId, replica_regions = new[] { replica } },
                },
            });
            using var service = Api.Service(Api.User);
            using var response = await service.PostAsync(new Uri("/v1/management/commits", UriKind.Relative), new StringContent(commit, Encoding.UTF8, "application/json"));
            await Assert.That(response.IsSuccessStatusCode).IsTrue().Because(await response.Content.ReadAsStringAsync());
        }
    }

    private sealed record Stored(string State, long Stamp, UInt128 Digest, string Body);

    // Membership time is advanced explicitly; maintenance timers sharing the round period stand still, and deadlines use real timers.
    private sealed class ManualRoundsTime : TimeProvider
    {
        private readonly FakeTimeProvider _rounds = new();

        public override long TimestampFrequency => _rounds.TimestampFrequency;

        public override long GetTimestamp() => _rounds.GetTimestamp();

        public void AdvanceMembership() => _rounds.Advance(DirectoryMembership.Heartbeat);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            period == AntiEntropy.Round ? _rounds.CreateTimer(callback, state, dueTime, period) : base.CreateTimer(callback, state, dueTime, period);
    }

    // gru and sjc in one fake DNS, holding the same database.
    private sealed class Pair(Node gru, Node sjc) : IAsyncDisposable
    {
        public Node Gru { get; } = gru;

        public Node Sjc { get; } = sjc;

        public static async Task<Pair> StartAsync()
        {
            var members = new FakeMemberStore();
            var database = new TenantContext(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());
            var user = EntityIds.New().ToString();
            var gru = await Node.StartAsync("gru", members, "sjc", database, user);
            try
            {
                var pair = new Pair(gru, await Node.StartAsync("sjc", members, "gru", database, user));
                try
                {
                    await pair.Gru.DiscoverPeerAsync();
                    await pair.Sjc.DiscoverPeerAsync();
                    return pair;
                }
                catch
                {
                    await pair.Sjc.DisposeAsync();
                    throw;
                }
            }
            catch
            {
                await gru.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Sjc.DisposeAsync();
            await Gru.DisposeAsync();
        }
    }
}
