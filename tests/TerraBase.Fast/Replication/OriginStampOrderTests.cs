using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Turso;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Replication;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

/// <summary>
/// The C7 origin stamp order (docs/terrabase/multi-region-replication.md): a member stamps its local writes after every stamp
/// it observed, and a stamp from the future moves its clock at most <see cref="MemberClock.MaxSkew"/> ahead of the physical
/// clock when it is only observed. Rewriting the future document locally mints above it, and that floor must survive a
/// restart on the same data directory with the same replica identity. gru runs ten minutes ahead; sjc receives gru's
/// documents over its peer stream. Writes and reads go through each machine's HTTP data API.
/// </summary>
public sealed class OriginStampOrderTests
{
    private const string Collection = "items";

    private static readonly long SkewMilliseconds = (long)MemberClock.MaxSkew.TotalMilliseconds;

    private static readonly TimeSpan FutureOffset = TimeSpan.FromHours(1);

    [Test]
    public async Task A_local_rewrite_of_a_future_document_mints_above_it_and_restarts_keep_every_later_mint_above_it()
    {
        await using var pair = await Pair.StartAsync();
        await pair.SeedCollectionAsync();
        var remoteId = EntityIds.New().ToString();
        await pair.Gru.PutAsync(remoteId, """{"n":1}""");
        await pair.Sjc.UntilAsync(remoteId, """{"n":1}""");
        var (remote, remoteOrigin) = await pair.Sjc.StampAsync(remoteId);
        await Assert.That(remoteOrigin).IsEqualTo(pair.Gru.Node).Because("a replicated document keeps its origin's stamp");
        await Assert.That(remote.Milliseconds > Now() + 5_000).IsTrue().Because("gru's clock runs ahead, so this stamp is from the future");

        var earlier = new List<Hlc>();

        // Rewriting the future document: its operation mints above the stamp it already has.
        await pair.Sjc.PutAsync(remoteId, """{"n":2}""");
        var (rewritten, rewrittenOrigin) = await pair.Sjc.StampAsync(remoteId);
        await Assert.That(rewrittenOrigin).IsEqualTo(pair.Sjc.Node);
        await Assert.That(rewritten.Value > remote.Value).IsTrue().Because("a local write to a future document mints above its stamp");
        earlier.Add(rewritten);

        // Other documents minted after that stay above the future stamp.
        var otherIds = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            var id = EntityIds.New().ToString();
            otherIds.Add(id);
            await pair.Sjc.PutAsync(id, """{"n":3}""");
            var (stamp, origin) = await pair.Sjc.StampAsync(id);
            await Assert.That(origin).IsEqualTo(pair.Sjc.Node);
            await Assert.That(stamp.Value > remote.Value).IsTrue().Because($"{id} was minted after a write above the future stamp");
            await Assert.That(stamp.Value > earlier.Max(before => before.Value)).IsTrue();
            earlier.Add(stamp);
        }

        // The same data directory and replica id.
        var node = pair.Sjc.Node;
        await pair.Sjc.StopAsync();
        await pair.Sjc.StartAsync();
        await Assert.That(pair.Sjc.Node).IsEqualTo(node).Because("a clean restart keeps the replica id");
        await pair.Sjc.ConnectedAsync();

        // A new document, then a rewrite of an earlier other document, after the restart.
        var afterIds = new[] { EntityIds.New().ToString(), otherIds[0] };
        foreach (var id in afterIds)
        {
            await pair.Sjc.PutAsync(id, """{"n":4}""");
            var (stamp, origin) = await pair.Sjc.StampAsync(id);
            await Assert.That(origin).IsEqualTo(node);
            await Assert.That(stamp.Value > remote.Value).IsTrue().Because($"{id} minted after the restart must stay above the future stamp");
            await Assert.That(stamp.Value > earlier.Max(before => before.Value)).IsTrue()
                .Because($"{id} minted after the restart must sort above every local stamp issued before it");
            earlier.Add(stamp);
        }
    }

    // Twenty isolated pairs exercise the skew precondition under parallel load in local and CI runs.
    [Test]
    [Repeat(19)]
    public async Task An_unrelated_document_observing_a_future_stamp_mints_within_the_skew_bound_before_any_local_rewrite()
    {
        await using var pair = await Pair.StartAsync();
        await pair.SeedCollectionAsync();
        var remoteId = EntityIds.New().ToString();
        await pair.Gru.PutAsync(remoteId, """{"n":1}""");
        await pair.Sjc.UntilAsync(remoteId, """{"n":1}""");
        var (remote, remoteOrigin) = await pair.Sjc.StampAsync(remoteId);
        await Assert.That(remoteOrigin).IsEqualTo(pair.Gru.Node).Because("sjc observed gru's stamp through replication");
        await Assert.That(remote.Milliseconds > Now() + SkewMilliseconds).IsTrue().Because("the observed stamp exceeds the skew bound");

        // Unrelated documents on sjc, while the future document is still untouched there.
        for (var i = 0; i < 2; i++)
        {
            var id = EntityIds.New().ToString();
            await pair.Sjc.PutAsync(id, """{"n":5}""");
            var (stamp, origin) = await pair.Sjc.StampAsync(id);
            var clock = Now();
            var (stillRemote, stillRemoteOrigin) = await pair.Sjc.StampAsync(remoteId);
            await Assert.That(stillRemote).IsEqualTo(remote).Because("sjc has not rewritten the future document");
            await Assert.That(stillRemoteOrigin).IsEqualTo(pair.Gru.Node);
            await Assert.That(origin).IsEqualTo(pair.Sjc.Node);
            await Assert.That(stamp.Milliseconds < remote.Milliseconds).IsTrue()
                .Because($"{id} must not take the future stamp's time ({remote.Milliseconds} ms)");
            await Assert.That(stamp.Milliseconds <= clock + SkewMilliseconds).IsTrue()
                .Because($"{id} must stay within the 1 s skew bound of the physical clock ({clock} ms)");
        }
    }

    // One machine of a cell of one (no member directory), so no peer replicates future system or metadata stamps and masks the seed of the clock from history.
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_restart_seeds_the_clock_from_a_future_stamp_kept_only_by_the_feed_or_its_retention_floor(bool trimFeed)
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-origin-history-").FullName;
        var name = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())[..14];
        var s3 = new FakeS3();
        try
        {
            var remoteId = EntityIds.New().ToString();
            var remoteOrigin = ReplicaId.Of(1, 1, 0);
            var future = new Hlc((ulong)(TimeProvider.System.GetUtcNow() + FutureOffset).ToUnixTimeMilliseconds() << 16);
            var horizon = new Hlc(future.Value + 1);
            ReplicaId node;
            ManagedDatabaseRef managed;
            TenantContext database;
            await using (var api = await StartSoloAsync(directory, name, s3))
            {
                node = api.Server.Services.GetRequiredService<LocalReplica>().Id;
                managed = await CreateSoloDatabaseAsync(api);
                database = new TenantContext(managed.Organization, PlanTier.Hobby, managed.Project, managed.Id);
                using (var data = await api.CreateClientAsync(managed.Organization, "documents:read", "documents:write"))
                {
                    await ManagementApi.CreateCollectionAsync(data, managed, Collection);
                }

                await PutAsync(api, managed, EntityIds.New().ToString(), """{"n":0}""");

                // The future document joins as a peer's delta through the engine, with its feed row.
                var store = (TursoDatabaseStore)(await api.Server.Services.GetRequiredService<TursoStorageEngine>().OpenDatabaseAsync(database, CancellationToken.None))!;
                using (var body = JsonDocument.Parse("""{"n":1}"""))
                {
                    var state = DocumentState.Bottom.Put(new EntryVersion(future, remoteOrigin), body.RootElement);
                    await using var transaction = await store.BeginDurableTransactionAsync(CancellationToken.None);
                    _ = await transaction.JoinAsync(Collection, remoteId, state.DeltaAt(future), new EntryVersion(future, remoteOrigin), CancellationToken.None);
                    _ = await transaction.CommitAsync(CancellationToken.None);
                }

                // A copying refill forgets the document's state and body above its stamp; retention may then trim the feed.
                await using (var transaction = (TursoStorageTransaction)await store.BeginDurableTransactionAsync(CancellationToken.None))
                {
                    _ = await transaction.ForgetAsync([(Collection, remoteId)], horizon, CancellationToken.None);
                    if (trimFeed)
                    {
                        _ = await transaction.TrimFeedAsync(horizon, 1024, CancellationToken.None);
                    }

                    _ = await transaction.CommitAsync(CancellationToken.None);
                }

                // Only the branch under test holds the future stamp: the state row is gone and nothing else is in the future.
                await Assert.That(await ScalarAsync(api, database, "SELECT COUNT(*) FROM _tb_state WHERE stamp >= $stamp", ("$stamp", Stored(future)))).IsEqualTo(0);
                await Assert.That(await ScalarAsync(api, database, "SELECT COUNT(*) FROM _tb_feed WHERE stamp >= $stamp", ("$stamp", Stored(future))))
                    .IsEqualTo(trimFeed ? 0 : 1).Because("the feed row keeps the future stamp unless retention trimmed it");
                await Assert.That(await ScalarAsync(api, database, "SELECT COALESCE(MAX(stamp), 0) FROM _tb_feed_floor WHERE origin = $origin", ("$origin", (long)remoteOrigin.Value)))
                    .IsEqualTo(trimFeed ? Stored(future) : 0).Because("trimming raises the origin's retention floor to the trimmed stamp");
                await Assert.That(await api.Server.Services.GetRequiredService<ICatalogGate>().TryAnchorAsync(CancellationToken.None)).IsTrue();
            }

            await using var restarted = await StartSoloAsync(directory, name, s3);
            await Assert.That(restarted.Server.Services.GetRequiredService<LocalReplica>().Id).IsEqualTo(node).Because("a clean restart keeps the replica id");
            var id = EntityIds.New().ToString();
            await PutAsync(restarted, managed, id, """{"n":2}""");
            var stamp = await ScalarAsync(restarted, database, "SELECT stamp FROM _tb_state WHERE collection = $collection AND id = $id", ("$collection", Collection), ("$id", id));
            await Assert.That(unchecked((ulong)stamp) > future.Value).IsTrue()
                .Because($"{id} minted after the restart must exceed the retained future stamp ({future.Milliseconds} ms)");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static long Now() => TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds();

    private static async Task<ManagedDatabaseRef> CreateSoloDatabaseAsync(ManagementApi api)
    {
        var organization = await api.CreateOrganizationAsync("history");
        var project = await api.CreateProjectAsync(organization, "history");
        using var response = await api.Client.PostAsJsonAsync(
            ManagementApi.Uri($"/v1/management/organizations/{organization}/projects/{project}/databases"),
            new { name = "main" });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new ManagedDatabaseRef(organization, project, body.GetProperty("id").GetString()!, "main");
    }

    // One machine in region gru without a member directory (a cell of one), in a data directory and under a node name that a restart reuses.
    private static Task<ManagementApi> StartSoloAsync(string directory, string name, FakeS3 s3) =>
        ManagementApi.StartAsync(settings =>
        {
            s3.Configure(settings);
            settings["region"] = "gru";
            settings["node-name"] = name;
            settings["data-directory"] = directory;
        }, services =>
        {
            s3.Route(services);
            ManualBackupHosting.Configure(services);
        });

    private static async Task PutAsync(ManagementApi api, ManagedDatabaseRef managed, string id, string json)
    {
        using var data = await api.CreateClientAsync(managed.Organization, "documents:read", "documents:write");
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await data.PutAsync(ManagementApi.Uri(ManagementApi.DocumentPath(managed, Collection, id)), content);
        await Assert.That(response.IsSuccessStatusCode).IsTrue().Because(await response.Content.ReadAsStringAsync());
    }

    // One number from the database's own tables, read through its engine store.
    private static async Task<long> ScalarAsync(ManagementApi api, TenantContext database, string sql, params (string Name, object Value)[] parameters)
    {
        var store = (TursoDatabaseStore)(await api.Server.Services.GetRequiredService<TursoStorageEngine>().OpenDatabaseAsync(database, CancellationToken.None))!;
        return await store.WithWriterAsync(session =>
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            return (long)command.ExecuteScalar()!;
        }, "a test reading the clock seed rows", CancellationToken.None);
    }

    // A stamp as the tables store it.
    private static long Stored(Hlc stamp) => unchecked((long)stamp.Value);

    private sealed class Pair : IAsyncDisposable
    {
        public FakeMemberStore Members { get; } = new();

        public string User { get; } = EntityIds.New().ToString();

        public TenantContext Database { get; } = new(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());

        public Machine Gru { get; private set; } = null!;

        public Machine Sjc { get; private set; } = null!;

        public ManagedDatabaseRef Managed => new(Database.OrganizationId, Database.ProjectId!, Database.DatabaseId!, "main");

        public static async Task<Pair> StartAsync()
        {
            var pair = new Pair();
            try
            {
                pair.Gru = new Machine(pair, "gru", ["sjc"], new ShiftedTime(TimeSpan.FromMinutes(10)));
                await pair.Gru.StartAsync();
                pair.Sjc = new Machine(pair, "sjc", ["gru"], time: null);
                await pair.Sjc.StartAsync();
                await pair.Gru.ConnectedAsync();
                await pair.Sjc.ConnectedAsync();
                return pair;
            }
            catch
            {
                await pair.DisposeAsync();
                throw;
            }
        }

        // The collection is created through gru's data API; sjc gets it with the first replicated document.
        public async Task SeedCollectionAsync()
        {
            using var data = await Gru.DataAsync();
            await ManagementApi.CreateCollectionAsync(data, Managed, Collection);
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

    private sealed class Machine(Pair pair, string region, string[] replicas, TimeProvider? time) : IAsyncDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("terrabase-origin-stamps-").FullName;
        private ManagementApi? _api;

        public string Name { get; } = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())[..14];

        public ManagementApi Api => _api ?? throw new InvalidOperationException($"{region} is stopped");

        public ReplicaId Node => Api.Server.Services.GetRequiredService<LocalReplica>().Id;

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
                // Both machines flush usage into the same replicated catalog documents; sjc's flush rewriting gru's
                // future copy is a local rewrite that lawfully lifts sjc's member-wide clock past the future stamp (#262).
                services.Remove(services.Single(service => service.ServiceType == typeof(IHostedService) && service.ImplementationType == typeof(UsageFlushService)));
            }, time, userId: pair.User);
            if (first)
            {
                await RegisterAsync();
            }
            else
            {
                // Startup precedes this fixture's directory publication; finish the trusted pass before issuing keys.
                await Assert.That(await ((CatalogGate)Api.Server.Services.GetRequiredService<ICatalogGate>()).VerifyAsync(TestContext.Current!.Execution.CancellationToken)).IsTrue();
            }
        }

        public async Task StopAsync()
        {
            await Api.DisposeAsync();
            _api = null;
        }

        // A write is admitted only while a stream to another placed region is connected; wait for this machine's.
        public Task ConnectedAsync() =>
            TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(Api.Server.Services.GetRequiredService<IReplicationStatus>()
                .Snapshot().Peers.Any(peer => replicas.Contains(peer.Region))), timeout: TimeSpan.FromSeconds(30));

        // A client of this machine's data API, for the test's organization.
        public Task<HttpClient> DataAsync() =>
            Api.CreateClientAsync(pair.Database.OrganizationId, "documents:read", "documents:write");

        public async Task PutAsync(string id, string json, CancellationToken cancellationToken = default)
        {
            using var data = await DataAsync();
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await data.PutAsync(ManagementApi.Uri(ManagementApi.DocumentPath(pair.Managed, Collection, id)), content, cancellationToken);
            await Assert.That(response.IsSuccessStatusCode).IsTrue().Because(await response.Content.ReadAsStringAsync(cancellationToken));
        }

        // The document's JSON as its data API reads it, or null when absent.
        public async Task<string?> ReadAsync(string id, CancellationToken cancellationToken = default)
        {
            using var data = await DataAsync();
            using var response = await data.GetAsync(ManagementApi.Uri(ManagementApi.DocumentPath(pair.Managed, Collection, id)), cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            return JsonElement.Parse(await response.Content.ReadAsStringAsync(cancellationToken)).GetProperty("document").GetRawText();
        }

        public async Task UntilAsync(string id, string expected) =>
            await TerraBaseTestServer.EventuallyAsync(async token => await ReadAsync(id, token) == expected);

        // The stamp and origin of the document's state row, as the engine stored them.
        public async Task<(Hlc Stamp, ReplicaId Origin)> StampAsync(string id)
        {
            var store = (TursoDatabaseStore)await StoreAsync();
            return await store.WithWriterAsync(session =>
            {
                using var command = session.Connection.CreateCommand();
                command.CommandText = "SELECT stamp, origin FROM _tb_state WHERE collection = $collection AND id = $id;";
                command.Parameters.AddWithValue("$collection", Collection);
                command.Parameters.AddWithValue("$id", id);
                using var reader = command.ExecuteReader();
                if (!reader.Read())
                {
                    throw new InvalidOperationException("the document has no state row");
                }

                return (new Hlc(unchecked((ulong)reader.GetInt64(0))), new ReplicaId(unchecked((ulong)reader.GetInt64(1))));
            }, "a test reading a stamp", CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            if (_api is not null)
            {
                await StopAsync();
            }

            Directory.Delete(_directory, recursive: true);
        }

        private async Task<IDatabaseStore> StoreAsync() =>
            await Engine.OpenDatabaseAsync(pair.Database, CancellationToken.None) ?? throw new InvalidOperationException("the database is missing");

        // The organization, project and database under the pair's ids, placed in both regions.
        private async Task RegisterAsync()
        {
            var database = pair.Database;
            await CatalogFixture.RegisterAsync(Api, Api.User, database, "main", replicas);
        }
    }

    // A clock that runs `offset` ahead of the system clock, so the machine's stamps are from its future.
    private sealed class ShiftedTime(TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + offset;
    }
}
