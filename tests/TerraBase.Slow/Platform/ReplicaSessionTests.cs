using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using TerraBase.Features.Regions;
using TerraBase.Features.Replicas;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Slow.Platform;

/// <summary>Postgres and WebSocket sessions in a replica region: local reads, writes at the home, read-your-writes.</summary>
public sealed class ReplicaSessionTests
{
    private static readonly Faker Faker = new();
    private static readonly string[] Sjc = ["sjc"];

    [Test]
    public async Task Postgres_and_websocket_sessions_in_a_replica_region_read_locally_write_at_the_home_and_read_their_writes()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        using var issuer = new TestIssuer();
        var regions = new ConcurrentQueue<RegionPeer>();
        await using var gru = await StartAsync("gru", issuer, regions);
        regions.Enqueue(new RegionPeer("gru", gru.Server.BaseAddress));
        await using var sjc = await StartAsync("sjc", issuer, regions);
        regions.Enqueue(new RegionPeer("sjc", sjc.Server.BaseAddress));

        var organization = await gru.CreateOrganizationAsync("Org " + EntityIds.New());
        await gru.SetPlanAsync(gru.User, organization, "hobby", token);
        var project = await gru.CreateProjectAsync(organization, "Project");
        using var created = await gru.ProjectClient(project).PostAsJsonAsync(
            ManagementApi.Uri($"/v1/projects/{project}/databases"), new { name = "main", replicaRegions = Sjc }, token);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await created.Content.ReadAsStringAsync(token));
        var database = new ManagedDatabaseRef(organization, project, (await created.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("id").GetString()!, "main");
        var bearer = issuer.Mint(gru.User, descriptor =>
        {
            descriptor.Claims["project_id"] = project;
            descriptor.Claims["scope"] = "databases:read sql:read sql:write";
        });
        using var viaGru = gru.WithToken(bearer);
        var first = Faker.Lorem.Sentence();
        using var seeded = await viaGru.PostAsJsonAsync(ManagementApi.Uri(database.Data + "/sql/execute"), new
        {
            statements = new object[] { new { sql = "CREATE TABLE notes (id INTEGER PRIMARY KEY, body TEXT NOT NULL)" }, new { sql = "INSERT INTO notes VALUES (1, ?)", args = new[] { first } } },
        }, token);
        await Assert.That(seeded.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await seeded.Content.ReadAsStringAsync(token));
        var replication = sjc.Server.Services.GetRequiredService<RegionReplication>();
        await replication.SyncCatalogsAsync(token);
        await replication.PullAllAsync(token);

        await using var postgres = PostgresSql.Source(sjc, DashboardKey(issuer, project), database.Name);
        await using var connection = await postgres.OpenConnectionAsync(token);
        await using var socket = await SqlSocketClient.ConnectAsync(sjc, database.Data, bearer);
        await Assert.That(await PostgresSql.ScalarAsync(connection, "SELECT body FROM notes WHERE id = 1")).IsEqualTo(first);

        // Each write runs at the home; the next read of the same session sees it, from the replica or the home.
        for (var round = 0; round < 3; round++)
        {
            var written = Faker.Lorem.Sentence();
            await Assert.That(await PostgresSql.ExecuteAsync(connection, "UPDATE notes SET body = $1 WHERE id = 1", written)).IsEqualTo(1);
            await Assert.That(await PostgresSql.ScalarAsync(connection, "SELECT body FROM notes WHERE id = 1")).IsEqualTo(written);
            var sent = Faker.Lorem.Sentence();
            var updated = await socket.ExecuteAsync(new { sql = "INSERT INTO notes VALUES (?, ?)", args = new object[] { 10 + round, sent } });
            await Assert.That(updated.GetProperty("type").GetString()).IsEqualTo("result").Because(updated.ToString());
            var read = await socket.ExecuteAsync(new { sql = "SELECT body FROM notes WHERE id = ?", args = new object[] { 10 + round } });
            await Assert.That(read.GetProperty("results")[0].GetProperty("rows")[0][0].GetString()).IsEqualTo(sent).Because(read.ToString());
        }

        await using (var transaction = await connection.BeginTransactionAsync(token))
        {
            await Assert.That(await PostgresSql.ExecuteAsync(connection, "INSERT INTO notes VALUES (20, 'in a transaction')")).IsEqualTo(1);
            await Assert.That(await PostgresSql.ScalarAsync(connection, "SELECT body FROM notes WHERE id = 20")).IsEqualTo("in a transaction");
            await transaction.CommitAsync(token);
        }

        using var atHome = await viaGru.PostAsJsonAsync(ManagementApi.Uri(database.Data + "/sql/execute"),
            new { statements = new object[] { new { sql = "SELECT count(*) FROM notes" } } }, token);
        await Assert.That((await atHome.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("results")[0].GetProperty("rows")[0][0].GetInt64()).IsEqualTo(5);

        // With the home gone, the replica region still answers reads on both surfaces, and refuses writes naming the home.
        await replication.PullAllAsync(token);
        await gru.DisposeAsync();
        await Assert.That(await PostgresSql.ScalarAsync(connection, "SELECT count(*) FROM notes")).IsEqualTo("5");
        var local = await socket.ExecuteAsync(new { sql = "SELECT count(*) FROM notes" });
        await Assert.That(local.GetProperty("results")[0].GetProperty("rows")[0][0].GetInt64()).IsEqualTo(5).Because(local.ToString());
        var refused = await Assert.ThrowsAsync<PostgresException>(() => PostgresSql.ExecuteAsync(connection, "INSERT INTO notes VALUES (30, 'home is gone')"));
        await Assert.That(refused!.MessageText).Contains("gru");
    }

    private static Task<ManagementApi> StartAsync(string region, TestIssuer issuer, ConcurrentQueue<RegionPeer> regions) => ManagementApi.StartAsync(
        settings => settings["region"] = region,
        services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IRegionPeers>(new RegionView(region, regions)));
            services.Replace(ServiceDescriptor.Singleton<IRegionCatalog>(new Serving()));
            services.Replace(ServiceDescriptor.Singleton<IReplicaPeers>(new ReplicaView(region, regions)));
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
