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

public sealed class HomeLeaseTests
{
    private static readonly Faker Faker = new();
    private static readonly string[] SjcOnly = ["sjc"];

    [Test]
    public async Task A_durable_write_is_on_the_replica_when_acknowledged_and_a_home_cut_off_from_its_lease_refuses_writes_until_it_returns()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var nats = await JetStreamServer.StartAsync(token);
        await using var gruNats = new TcpGate(nats.Url.Port);
        using var issuer = new TestIssuer();
        var mesh = new Mesh();
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
        var name = "leased" + Faker.Random.AlphaNumeric(6).ToLowerInvariant();
        using (var created = await viaGru.PostAsJsonAsync(ManagementApi.Uri($"/v1/projects/{project}/databases"), new { name, replicaRegions = SjcOnly }, token))
        {
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await created.Content.ReadAsStringAsync(token));
        }

        var sql = ManagementApi.Uri($"/v1/projects/{project}/databases/{name}/sql/execute");
        await Assert.That(await WriteAsync(viaGru, sql, "CREATE TABLE acked (id TEXT PRIMARY KEY)", null, token)).IsEqualTo(HttpStatusCode.OK);
        var replication = sjc.Server.Services.GetRequiredService<RegionReplication>();
        await replication.SyncCatalogsAsync(token);
        await replication.PullAllAsync(token);
        var leases = gru.Server.Services.GetRequiredService<HomeLeases>();
        await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(leases.Ready), TimeSpan.FromSeconds(30));

        var row = EntityIds.New().ToString();
        await TerraBaseTestServer.EventuallyAsync(async _ => await WriteAsync(viaGru, sql, $"INSERT OR IGNORE INTO acked (id) VALUES ('{row}')", "all", token) == HttpStatusCode.OK,
            TimeSpan.FromSeconds(30));
        long? held = null;
        await TerraBaseTestServer.EventuallyAsync(async _ => (held = await CountAsync(viaSjc, sql, row, token)) is not null, TimeSpan.FromSeconds(10));
        await Assert.That(held).IsEqualTo(1);

        gruNats.Cut();
        await TerraBaseTestServer.EventuallyAsync(async _ => await WriteAsync(viaGru, sql, $"INSERT INTO acked (id) VALUES ('{EntityIds.New()}')", null, token) != HttpStatusCode.OK,
            TimeSpan.FromSeconds(20));
        await Assert.That(await WriteAsync(viaGru, sql, $"INSERT INTO acked (id) VALUES ('{EntityIds.New()}')", null, token)).IsNotEqualTo(HttpStatusCode.OK);

        gruNats.Heal();
        await TerraBaseTestServer.EventuallyAsync(async _ => await WriteAsync(viaGru, sql, $"INSERT INTO acked (id) VALUES ('{EntityIds.New()}')", null, token) == HttpStatusCode.OK,
            TimeSpan.FromSeconds(30));
    }

    private static async Task<HttpStatusCode> WriteAsync(HttpClient client, Uri sql, string statement, string? durability, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(sql, new { statements = new[] { new { sql = statement } }, durability }, token);
        return response.StatusCode;
    }

    private static async Task<long?> CountAsync(HttpClient client, Uri sql, string row, CancellationToken token)
    {
        using var read = await client.PostAsJsonAsync(sql, new { statements = new[] { new { sql = $"SELECT count(*) FROM acked WHERE id = '{row}'" } } }, token);
        if (read.StatusCode == HttpStatusCode.InternalServerError)
        {
            return null;
        }

        await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await read.Content.ReadAsStringAsync(token));
        await Assert.That(read.Headers.GetValues("X-TerraBase-Region")).IsEquivalentTo(SjcOnly);
        return (await read.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("results")[0].GetProperty("rows")[0][0].GetInt64();
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
