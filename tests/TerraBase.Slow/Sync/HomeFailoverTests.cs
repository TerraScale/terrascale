using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraBase.Configuration;
using TerraBase.Features.Regions;
using TerraBase.Features.Replicas;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Slow.Sync;

public sealed class HomeFailoverTests
{
    private static readonly Faker Faker = new();
    private static readonly string[] SjcOnly = ["sjc"];
    private static readonly Uri Unreachable = new("http://127.0.0.1:9/");

    [Test]
    public async Task A_home_cut_off_from_its_lease_store_mid_write_fails_over_keeping_every_acknowledged_write_and_refuses_writes_after_the_epoch_bump()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var nats = await JetStreamServer.StartAsync(token);
        await using var gruNats = new TcpGate(nats.Url.Port);
        using var issuer = new TestIssuer();
        var mesh = new Mesh { Nodes = [("gru", Unreachable), ("sjc", Unreachable)] };
        await using var gru = await StartAsync("gru", issuer, mesh, new Uri($"nats://127.0.0.1:{gruNats.Port}"), nats.Token);
        await using var sjc = await StartAsync("sjc", issuer, mesh, nats.Url, nats.Token);
        mesh.Nodes = [("gru", gru.Server.BaseAddress), ("sjc", sjc.Server.BaseAddress)];

        var organization = await gru.CreateOrganizationAsync("Org " + EntityIds.New());
        var project = await gru.CreateProjectAsync(organization, "Project");
        var bearer = issuer.Mint(gru.User, descriptor =>
        {
            descriptor.Claims["project_id"] = project;
            descriptor.Claims["org_id"] = organization;
            descriptor.Claims["scope"] = "databases:read databases:write sql:read sql:write";
        });
        using var viaGru = gru.WithToken(bearer);
        using var viaSjc = sjc.WithToken(bearer);
        var all = await CreateAsync(viaGru, project, token);
        var local = await CreateAsync(viaGru, project, token);

        var replication = sjc.Server.Services.GetRequiredService<RegionReplication>();
        await replication.SyncCatalogsAsync(token);
        await replication.PullAllAsync(token);
        var gruLeases = gru.Server.Services.GetRequiredService<HomeLeases>();
        var sjcLeases = sjc.Server.Services.GetRequiredService<HomeLeases>();
        await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(gruLeases.Find(all.Id)?.Home == "gru" && gruLeases.Find(local.Id)?.Home == "gru"
            && sjcLeases.Find(all.Id) is not null && sjcLeases.Find(local.Id) is not null), TimeSpan.FromSeconds(30));

        var acked = new ConcurrentDictionary<string, ConcurrentQueue<string>>(StringComparer.Ordinal)
        {
            [all.Id] = new ConcurrentQueue<string>(),
            [local.Id] = new ConcurrentQueue<string>(),
        };
        var cut = 0;
        var ackedAfterCut = 0;
        var pushed = new ConcurrentQueue<string>();
        var pushedDuringCut = 0;
        var unknown = 0;
        using var stop = new CancellationTokenSource();
        var writers = new[] { (all, "all"), (local, "local") }.Select(pair => Task.Run(async () =>
        {
            var (database, durability) = pair;
            var rows = acked[database.Id];
            while (!stop.IsCancellationRequested)
            {
                var row = EntityIds.New().ToString();
                using var response = await viaGru.PostAsJsonAsync(database.Sql, new
                {
                    statements = new[] { new { sql = "INSERT INTO acked (id) VALUES (?)", args = new[] { row } } },
                    durability,
                }, token);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    rows.Enqueue(row);
                    if (Volatile.Read(ref cut) == 1 && durability == "all")
                    {
                        Interlocked.Increment(ref ackedAfterCut);
                    }
                }
            }
        }, token)).Append(Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var row = "sjc-" + EntityIds.New();
                using var response = await viaSjc.PostAsJsonAsync(all.Sql, new
                {
                    statements = new object[]
                    {
                        new { sql = "UPDATE tally SET n = n + 1 WHERE id = 1" },
                        new { sql = "INSERT INTO acked (id) VALUES (?)", args = new[] { row } },
                    },
                }, token);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    pushed.Enqueue(row);
                    if (Volatile.Read(ref cut) == 1 && sjcLeases.Find(all.Id)?.Home != "sjc")
                    {
                        Interlocked.Increment(ref pushedDuringCut);
                    }
                }
                else if ((int)response.StatusCode >= 500)
                {
                    Interlocked.Increment(ref unknown);
                }
            }
        }, token)).ToArray();

        await TerraBaseTestServer.EventuallyAsync(async _ => acked.Values.All(rows => rows.Count >= 10)
            && (await ReplicaCountAsync(viaSjc, all, token)) >= 10 && (await ReplicaCountAsync(viaSjc, local, token)) >= 10, TimeSpan.FromSeconds(30));
        gruNats.Cut();
        Volatile.Write(ref cut, 1);

        await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(sjcLeases.Find(all.Id)?.Home == "sjc" && sjcLeases.Find(local.Id)?.Home == "sjc"),
            TimeSpan.FromSeconds(60));
        await TerraBaseTestServer.EventuallyAsync(async _ => (await WriteAsync(viaSjc, all, token)) == HttpStatusCode.OK, TimeSpan.FromSeconds(30));
        await Assert.That(await WriteAsync(viaGru, local, token)).IsNotEqualTo(HttpStatusCode.OK);

        gruNats.Heal();
        await TerraBaseTestServer.EventuallyAsync(async _ => (await WriteAsync(viaGru, local, token)) == HttpStatusCode.MisdirectedRequest, TimeSpan.FromSeconds(60));
        await stop.CancelAsync();
        await Task.WhenAll(writers);

        await TerraBaseTestServer.EventuallyAsync(async _ => (await RowsAsync(viaSjc, all, token)).IsSupersetOf(pushed), TimeSpan.FromSeconds(30));
        var allStored = await RowsAsync(viaSjc, all, token);
        await Assert.That(acked[all.Id].Where(row => !allStored.Contains(row))).IsEmpty();
        await Assert.That(ackedAfterCut).IsEqualTo(0);
        await Assert.That(pushed.Where(row => !allStored.Contains(row))).IsEmpty().Because("the new home applies the pushes its former home never consumed");
        await Assert.That(pushedDuringCut).IsGreaterThan(0);
        var tally = await TallyAsync(viaSjc, all, token);
        await Assert.That(tally).IsBetween((long)pushed.Count, (long)pushed.Count + Volatile.Read(ref unknown)).Because("_ts_applied applies each pushed transaction once");

        var localStored = await RowsAsync(viaSjc, local, token);
        var order = acked[local.Id].ToList();
        var firstMissing = order.FindIndex(row => !localStored.Contains(row));
        await Assert.That(firstMissing < 0 || order.Skip(firstMissing).All(row => !localStored.Contains(row))).IsTrue()
            .Because("the default mode loses only the tail no replica pulled");
        await Assert.That(localStored.Count).IsGreaterThanOrEqualTo(10);
    }

    private static async Task<Database> CreateAsync(HttpClient client, string project, CancellationToken token)
    {
        var name = "failover" + Faker.Random.AlphaNumeric(6).ToLowerInvariant();
        using var created = await client.PostAsJsonAsync(ManagementApi.Uri($"/v1/projects/{project}/databases"), new { name, replicaRegions = SjcOnly }, token);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await created.Content.ReadAsStringAsync(token));
        var database = new Database((await created.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("id").GetString()!,
            ManagementApi.Uri($"/v1/projects/{project}/databases/{name}/sql/execute"));
        using var table = await client.PostAsJsonAsync(database.Sql, new
        {
            statements = new[]
            {
                new { sql = "CREATE TABLE acked (id TEXT PRIMARY KEY)" },
                new { sql = "CREATE TABLE tally (id INTEGER PRIMARY KEY, n INTEGER NOT NULL)" },
                new { sql = "INSERT INTO tally (id, n) VALUES (1, 0)" },
            },
        }, token);
        await Assert.That(table.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await table.Content.ReadAsStringAsync(token));
        return database;
    }

    private static async Task<long> ReplicaCountAsync(HttpClient client, Database database, CancellationToken token)
    {
        using var read = await client.PostAsJsonAsync(database.Sql, new { statements = new[] { new { sql = "SELECT count(*) FROM acked" } } }, token);
        return read.StatusCode == HttpStatusCode.OK
            ? (await read.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("results")[0].GetProperty("rows")[0][0].GetInt64()
            : -1;
    }

    private static async Task<long> TallyAsync(HttpClient client, Database database, CancellationToken token)
    {
        using var read = await client.PostAsJsonAsync(database.Sql, new { statements = new[] { new { sql = "SELECT n FROM tally WHERE id = 1" } } }, token);
        await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await read.Content.ReadAsStringAsync(token));
        return (await read.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("results")[0].GetProperty("rows")[0][0].GetInt64();
    }

    private static async Task<HttpStatusCode> WriteAsync(HttpClient client, Database database, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(database.Sql, new { statements = new[] { new { sql = "INSERT INTO acked (id) VALUES (?)", args = new[] { "probe-" + EntityIds.New() } } } }, token);
        return response.StatusCode;
    }

    private static async Task<HashSet<string>> RowsAsync(HttpClient client, Database database, CancellationToken token)
    {
        using var read = await client.PostAsJsonAsync(database.Sql, new { statements = new[] { new { sql = "SELECT id FROM acked" } } }, token);
        await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await read.Content.ReadAsStringAsync(token));
        await Assert.That(read.Headers.GetValues("X-TerraBase-Region")).IsEquivalentTo(SjcOnly);
        return (await read.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("results")[0].GetProperty("rows").EnumerateArray()
            .Select(row => row[0].GetString()!).ToHashSet(StringComparer.Ordinal);
    }

    private static Task<ManagementApi> StartAsync(string region, TestIssuer issuer, Mesh mesh, Uri natsUrl, string natsToken) => ManagementApi.StartAsync(
        settings => settings["region"] = region,
        services =>
        {
            var options = (TerraBaseOptions)services.Last(descriptor => descriptor.ServiceType == typeof(TerraBaseOptions)).ImplementationInstance!;
            services.Replace(ServiceDescriptor.Singleton(options with { MachineId = region + "-1", NatsUrl = natsUrl, NatsToken = natsToken }));
            services.Replace(ServiceDescriptor.Singleton<IRegionPeers>(new RegionView(region, mesh)));
            services.Replace(ServiceDescriptor.Singleton<IRegionCatalog>(new Serving()));
            services.Replace(ServiceDescriptor.Singleton<IReplicaPeers>(new ReplicaView(region, mesh)));
        },
        sharedIssuer: issuer);

    private sealed record Database(string Id, Uri Sql);

    private sealed class Mesh
    {
        public (string Region, Uri Internal)[] Nodes { get; set; } = [];
    }

    private sealed class RegionView(string region, Mesh mesh) : IRegionPeers
    {
        public ValueTask<IReadOnlyList<RegionPeer>> OtherRegionsAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<RegionPeer>>([.. mesh.Nodes.Where(node => node.Region != region).Select(node => new RegionPeer(node.Region, node.Internal))]);
    }

    private sealed class ReplicaView(string region, Mesh mesh) : IReplicaPeers
    {
        public ValueTask<IReadOnlyList<ReplicaPeer>> ListAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<ReplicaPeer>>([.. mesh.Nodes.Where(node => node.Region != region)
                .Select(node => new ReplicaPeer(node.Region, node.Region + "-1", node.Internal))]);
    }

    private sealed class Serving : IRegionCatalog
    {
        public ValueTask<IReadOnlyList<string>> ServingAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<string>>(["gru", "sjc"]);
    }
}
