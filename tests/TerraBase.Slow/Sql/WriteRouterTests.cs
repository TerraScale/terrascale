using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using TerraBase.Configuration;
using TerraBase.Features.Clock;
using TerraBase.Features.HomePush;
using TerraBase.Features.Regions;
using TerraBase.Features.Replicas;
using TerraBase.Primitives;

namespace TerraBase.Slow.Sql;

public sealed class WriteRouterTests
{
    private static readonly Faker Faker = new();
    private static readonly string[] SjcRegion = ["sjc"];

    private const string Schema = """
        CREATE TABLE events (id TEXT PRIMARY KEY, body TEXT);
        CREATE TABLE counters (id TEXT PRIMARY KEY, n INTEGER NOT NULL, label TEXT);
        CREATE TABLE users (id TEXT PRIMARY KEY, email TEXT UNIQUE);
        CREATE TABLE logs (id INTEGER PRIMARY KEY AUTOINCREMENT, body TEXT);
        CREATE TABLE plain (id INTEGER PRIMARY KEY, body TEXT);
        CREATE TABLE parents (id TEXT PRIMARY KEY, name TEXT);
        CREATE TABLE children (id TEXT PRIMARY KEY, parent TEXT REFERENCES parents(id), body TEXT);
        INSERT INTO counters VALUES ('c1', 10, 'first');
        INSERT INTO users VALUES ('u1', 'one@example.com');
        INSERT INTO parents VALUES ('p1', 'kept'), ('p2', 'deleted');
        INSERT INTO children VALUES ('k1', 'p1', 'child')
        """;

    [Test]
    public async Task A_write_sent_to_a_replica_region_is_routed_by_its_shape_and_commits_at_the_home()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var cluster = await Cluster.StartAsync(token);
        (string Route, string Reason, string? Header, string[] Sql)[] rows =
        [
            ("local", "ulid_insert", null, [$"INSERT INTO events (id, body) VALUES ('{EntityIds.New()}', 'x')"]),
            ("local", "update_by_key", null, ["UPDATE counters SET label = 'second' WHERE id = 'c1'"]),
            ("local", "delete_by_key", null, ["DELETE FROM events WHERE id = 'missing'"]),
            ("local", "fk_child_insert", null, [$"INSERT INTO children (id, parent, body) VALUES ('{EntityIds.New()}', 'p1', 'b')"]),
            ("home", "relative_update", null, ["UPDATE counters SET n = n - 1 WHERE id = 'c1'"]),
            ("home", "unique", null, [$"INSERT INTO users (id, email) VALUES ('{EntityIds.New()}', '{EntityIds.New()}@example.com')"]),
            ("home", "unique_change", null, ["UPDATE users SET email = 'changed@example.com' WHERE id = 'u1'"]),
            ("home", "autoincrement", null, ["INSERT INTO logs (body) VALUES ('x')"]),
            ("home", "rowid_key", null, ["INSERT INTO plain (body) VALUES ('x')"]),
            ("home", "fk_parent_delete", null, ["DELETE FROM parents WHERE id = 'p2'"]),
            ("home", "fk_change", null, ["UPDATE children SET parent = 'p1' WHERE id = 'k1'"]),
            ("home", "not_by_key", null, ["UPDATE counters SET label = 'all' WHERE n > 0"]),
            ("home", "ddl", null, [$"CREATE TABLE t{EntityIds.New()} (id TEXT PRIMARY KEY)"]),
            ("home", "read_then_write", null, ["SELECT count(*) FROM events", $"INSERT INTO events (id, body) VALUES ('{EntityIds.New()}', 'y')"]),
            ("home", "override", "home", [$"INSERT INTO events (id, body) VALUES ('{EntityIds.New()}', 'z')"]),
        ];
        foreach (var (route, reason, header, sql) in rows)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ManagementApi.Uri(cluster.Database.Data + "/sql/execute"))
            {
                Content = JsonContent.Create(new { statements = sql.Select(text => new { sql = text }).ToArray() }),
            };
            if (header is not null)
            {
                request.Headers.Add("TerraScale-Write", header);
            }

            using var response = await cluster.ViaSjc.SendAsync(request, token);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(sql[^1] + ": " + await response.Content.ReadAsStringAsync(token));
            await Assert.That(response.Headers.GetValues("TerraScale-Write-Route").Single()).IsEqualTo($"route={route}; reason={reason}; commit=home").Because(sql[^1]);
            var timing = string.Join(", ", response.Headers.GetValues("Server-Timing"));
            await Assert.That(timing).Contains("home;dur=");
            await Assert.That(timing).Contains("desc=\"gru\"");
        }

        await Assert.That((await cluster.HomeAsync("SELECT count(*) FROM events", token))[0].GetInt64()).IsEqualTo(3);

        await cluster.StopHomeAsync();
        using var misdirected = await cluster.ViaSjc.PostAsJsonAsync(ManagementApi.Uri(cluster.Database.Data + "/sql/execute"),
            new { statements = new[] { new { sql = $"INSERT INTO events (id, body) VALUES ('{EntityIds.New()}', 'gone')" } } }, token);
        var retry = "https://gru.api.terrascale.tech" + misdirected.RequestMessage!.RequestUri!.PathAndQuery;
        await Assert.That(misdirected.StatusCode).IsEqualTo(HttpStatusCode.MisdirectedRequest);
        await Assert.That(misdirected.Headers.Location?.ToString()).IsEqualTo(retry);
        var error = (await misdirected.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("error");
        await Assert.That(error.GetProperty("code").GetString()).IsEqualTo("wrong_region");
        await Assert.That(error.GetProperty("url").GetString()).IsEqualTo(retry);
    }

    [Test]
    public async Task A_psql_transaction_and_a_websocket_batch_in_a_replica_region_commit_once_at_the_home_and_return_their_rows()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var cluster = await Cluster.StartAsync(token);
        await using var postgres = PostgresSql.Source(cluster.Sjc, cluster.Key, cluster.Database.Name);
        await using var connection = await postgres.OpenConnectionAsync(token);
        var id = EntityIds.New().ToString();
        var body = Faker.Lorem.Sentence();
        await using (var transaction = await connection.BeginTransactionAsync(token))
        {
            await using var insert = new NpgsqlCommand("INSERT INTO events (id, body) VALUES ($1, $2) RETURNING id, body", connection);
            insert.Parameters.AddWithValue(id);
            insert.Parameters.AddWithValue(body);
            await using (var reader = await insert.ExecuteReaderAsync(token))
            {
                await Assert.That(await reader.ReadAsync(token)).IsTrue();
                await Assert.That(reader.GetString(0)).IsEqualTo(id);
                await Assert.That(reader.GetString(1)).IsEqualTo(body);
            }

            await Assert.That(await PostgresSql.ExecuteAsync(connection, "UPDATE counters SET n = n + 5 WHERE id = 'c1'")).IsEqualTo(1);
            await transaction.CommitAsync(token);
        }

        await using var socket = await SqlSocketClient.ConnectAsync(cluster.Sjc, cluster.Database.Data, cluster.Bearer);
        var second = EntityIds.New().ToString();
        var batch = await socket.ExecuteAsync(
            new { sql = "INSERT INTO events (id, body) VALUES (?, 'socket') RETURNING id", args = new object[] { second } },
            new { sql = "UPDATE counters SET n = n + 1 WHERE id = 'c1' RETURNING n" });
        await Assert.That(batch.GetProperty("type").GetString()).IsEqualTo("result").Because(batch.ToString());
        await Assert.That(batch.GetProperty("results")[0].GetProperty("rows")[0][0].GetString()).IsEqualTo(second);
        await Assert.That(batch.GetProperty("results")[1].GetProperty("rows")[0][0].GetInt64()).IsEqualTo(16);

        var home = await cluster.HomeAsync("SELECT (SELECT count(*) FROM events), (SELECT n FROM counters WHERE id = 'c1')", token);
        await Assert.That(home[0].GetInt64()).IsEqualTo(2);
        await Assert.That(home[1].GetInt64()).IsEqualTo(16);
    }

    [Test]
    public async Task A_psql_transaction_that_reads_in_a_replica_region_and_then_writes_is_refused_with_40001_once_and_its_retry_runs_at_the_home()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var cluster = await Cluster.StartAsync(token);
        await using var postgres = PostgresSql.Source(cluster.Sjc, cluster.Key, cluster.Database.Name);
        await using var connection = await postgres.OpenConnectionAsync(token);

        async Task Withdraw()
        {
            await using var transaction = await connection.BeginTransactionAsync(token);
            await using var read = new NpgsqlCommand("SELECT n FROM counters WHERE id = 'c1'", connection);
            var balance = Convert.ToInt64(await read.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
            await Assert.That(await PostgresSql.ExecuteAsync(connection, "UPDATE counters SET n = $1 WHERE id = 'c1'", balance - 3)).IsEqualTo(1);
            await transaction.CommitAsync(token);
        }

        var refused = await Assert.ThrowsAsync<PostgresException>(Withdraw);
        await Assert.That(refused!.SqlState).IsEqualTo("40001");
        await Assert.That(await PostgresSql.ScalarAsync(connection, "SHOW terrascale.write_route")).IsEqualTo("route=home; reason=read_then_write; commit=home");
        await Withdraw();
        await Assert.That((await cluster.HomeAsync("SELECT n FROM counters WHERE id = 'c1'", token))[0].GetInt64()).IsEqualTo(7);
    }

    [Test]
    public async Task A_home_durable_write_in_a_replica_region_commits_locally_and_answers_after_the_home_applied_it()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var nats = await JetStreamServer.StartAsync(token);
        await using var cluster = await Cluster.StartAsync(token, nats);
        await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(cluster.Sjc.Server.Services.GetRequiredService<HomePush>().Started), TimeSpan.FromSeconds(30));
        var id = EntityIds.New().ToString();
        using var request = new HttpRequestMessage(HttpMethod.Post, ManagementApi.Uri(cluster.Database.Data + "/sql/execute"))
        {
            Content = JsonContent.Create(new { statements = new[] { new { sql = $"INSERT INTO events (id, body) VALUES ('{id}', 'from sjc')" } } }),
        };
        request.Headers.Add("TerraScale-Durability", "home");

        using var response = await cluster.ViaSjc.SendAsync(request, token);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await response.Content.ReadAsStringAsync(token));
        await Assert.That(response.Headers.GetValues("TerraScale-Write-Route").Single()).IsEqualTo("route=local; reason=ulid_insert; commit=local");
        await Assert.That((await cluster.HomeAsync($"SELECT body FROM events WHERE id = '{id}'", token))[0].GetString()).IsEqualTo("from sjc");
    }

    private sealed class Cluster : IAsyncDisposable
    {
        private readonly TestIssuer _issuer;
        private readonly ManagementApi _gru;
        private readonly HttpClient _viaGru;

        private Cluster(TestIssuer issuer, ManagementApi gru, ManagementApi sjc, ManagedDatabaseRef database, string bearer, string key)
        {
            (_issuer, _gru, Sjc, Database, Bearer, Key) = (issuer, gru, sjc, database, bearer, key);
            _viaGru = gru.WithToken(bearer);
            ViaSjc = sjc.WithToken(bearer);
        }

        public ManagementApi Sjc { get; }

        public HttpClient ViaSjc { get; }

        public ManagedDatabaseRef Database { get; }

        public string Bearer { get; }

        public string Key { get; }

        public static async Task<Cluster> StartAsync(CancellationToken token, JetStreamServer? nats = null)
        {
            var issuer = new TestIssuer();
            var keys = new FakeS3();
            var regions = new ConcurrentQueue<RegionPeer>();
            var gru = await StartAsync("gru", issuer, regions, nats, keys);
            regions.Enqueue(new RegionPeer("gru", gru.Server.BaseAddress));
            var sjc = await StartAsync("sjc", issuer, regions, nats, keys);
            regions.Enqueue(new RegionPeer("sjc", sjc.Server.BaseAddress));
            var organization = await gru.CreateOrganizationAsync("Org " + EntityIds.New());
            await gru.SetPlanAsync(gru.User, organization, "hobby", token);
            var project = await gru.CreateProjectAsync(organization, "Project");
            using var created = await gru.ProjectClient(project).PostAsJsonAsync(
                ManagementApi.Uri($"/v1/projects/{project}/databases"), new { name = "main", replicaRegions = SjcRegion }, token);
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await created.Content.ReadAsStringAsync(token));
            var database = new ManagedDatabaseRef(organization, project, (await created.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("id").GetString()!, "main");
            var bearer = issuer.Mint(gru.User, descriptor =>
            {
                descriptor.Claims["project_id"] = project;
                descriptor.Claims["scope"] = "databases:read sql:read sql:write";
            });
            var cluster = new Cluster(issuer, gru, sjc, database, bearer, DashboardKey(issuer, project));
            using var seeded = await cluster._viaGru.PostAsJsonAsync(ManagementApi.Uri(database.Data + "/sql/execute"),
                new { statements = Schema.Split(';').Select(sql => new { sql = sql.Trim() }).ToArray() }, token);
            await Assert.That(seeded.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await seeded.Content.ReadAsStringAsync(token));
            var replication = sjc.Server.Services.GetRequiredService<RegionReplication>();
            await replication.SyncCatalogsAsync(token);
            await replication.PullAllAsync(token);
            return cluster;
        }

        public async Task StopHomeAsync()
        {
            _viaGru.Dispose();
            await _gru.DisposeAsync();
        }

        public async Task<JsonElement> HomeAsync(string sql, CancellationToken token)
        {
            using var answer = await _viaGru.PostAsJsonAsync(ManagementApi.Uri(Database.Data + "/sql/execute"), new { statements = new[] { new { sql } } }, token);
            return (await answer.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("results")[0].GetProperty("rows")[0];
        }

        public async ValueTask DisposeAsync()
        {
            _viaGru.Dispose();
            ViaSjc.Dispose();
            await Sjc.DisposeAsync();
            await _gru.DisposeAsync();
            _issuer.Dispose();
        }

        private static Task<ManagementApi> StartAsync(string region, TestIssuer issuer, ConcurrentQueue<RegionPeer> regions, JetStreamServer? nats, FakeS3 keys) => ManagementApi.StartAsync(
            settings =>
            {
                settings["region"] = region;
                keys.Configure(settings);
            },
            services =>
            {
                keys.Route(services);
                services.Replace(ServiceDescriptor.Singleton<IRegionPeers>(new RegionView(region, regions)));
                services.Replace(ServiceDescriptor.Singleton<IRegionCatalog>(new Serving()));
                services.Replace(ServiceDescriptor.Singleton<IReplicaPeers>(new ReplicaView(region, regions)));
                var registered = services.Single(service => service.ServiceType == typeof(TerraBaseOptions));
                var options = (TerraBaseOptions)registered.ImplementationInstance! with { MachineId = region + "-1" };
                services[services.IndexOf(registered)] = ServiceDescriptor.Singleton(nats is null ? options : options with { NatsUrl = nats.Url, NatsToken = nats.Token });
                if (nats is not null)
                {
                    services.AddSingleton<ILocalWriteClock>(new Admitted());
                }
            },
            sharedIssuer: issuer);

        private static string DashboardKey(TestIssuer issuer, string project)
        {
            var id = EntityIds.New().ToString();
            var key = id + "_" + Faker.Random.String2(52, "0123456789abcdefghjkmnpqrstvwxyz");
            issuer.GrantClient(key, requested => requested is null || requested == project ? issuer.Mint(id, token =>
            {
                token.Claims["key_id"] = id;
                token.Claims["key_gen"] = TestIssuer.KeyGeneration(key);
                token.Claims["project_id"] = project;
                token.Claims["scope"] = "sql:read sql:write";
            }) : null);
            return key;
        }
    }

    private sealed class Admitted : ILocalWriteClock
    {
        public bool Allowed => true;

        public double SkewMilliseconds => 0;
    }

    private sealed class Serving : IRegionCatalog
    {
        public ValueTask<IReadOnlyList<string>> ServingAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<string>>(["gru", "sjc"]);
    }

    private sealed class RegionView(string region, ConcurrentQueue<RegionPeer> regions) : IRegionPeers
    {
        public ValueTask<IReadOnlyList<RegionPeer>> OtherRegionsAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<RegionPeer>>([.. regions.Where(peer => peer.Region != region)]);
    }

    private sealed class ReplicaView(string region, ConcurrentQueue<RegionPeer> regions) : IReplicaPeers
    {
        public ValueTask<IReadOnlyList<ReplicaPeer>> ListAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<ReplicaPeer>>([.. regions.Where(peer => peer.Region != region).Select(peer => new ReplicaPeer(peer.Region, peer.Region + "-1", peer.Address))]);
    }
}
