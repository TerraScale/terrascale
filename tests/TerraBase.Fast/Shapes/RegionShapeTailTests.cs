using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraBase.Features.Regions;
using TerraBase.Features.Replicas;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Shapes;

public sealed class RegionShapeTailTests
{
    [Test]
    public async Task A_shape_in_sjc_reads_gru_updates_and_filtered_moves_from_its_replica_even_after_home_disconnects()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        using var issuer = new TestIssuer();
        var regions = new List<RegionPeer>();
        await using var gru = await StartAsync("gru", issuer, regions);
        regions.Add(new RegionPeer("gru", gru.Server.BaseAddress));
        await using var sjc = await StartAsync("sjc", issuer, regions);
        regions.Add(new RegionPeer("sjc", sjc.Server.BaseAddress));
        var organization = await gru.CreateOrganizationAsync("Org " + EntityIds.New());
        var project = await gru.CreateProjectAsync(organization, "Project");
        using var created = await gru.Client.PostAsJsonAsync(
            ManagementApi.Uri($"/v1/management/organizations/{organization}/projects/{project}/databases"), new { name = "main", replicaRegions = new List<string> { "sjc" } }, token);
        created.EnsureSuccessStatusCode();
        var database = new ManagedDatabaseRef(organization, project, (await created.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("id").GetString()!, "main");
        var bearer = gru.ProjectToken(project, "databases:read documents:read documents:write");
        using var home = gru.DataClient(bearer);
        using var local = sjc.DataClient(bearer);
        using var seeded = await home.PostAsJsonAsync(ManagementApi.Uri(database.Data + "/sql/execute"),
            new { statements = new object[] { new { sql = "CREATE TABLE items(id INTEGER PRIMARY KEY, name TEXT, score INTEGER)" }, new { sql = "INSERT INTO items VALUES (1, 'first', 0), (2, 'second', 20)" } } }, token);
        seeded.EnsureSuccessStatusCode();
        var shape = new TableShapeClient(home, ManagementApi.Uri(database.Data).ToString(), "items", "&where=score%3E%3D10&columns=name,score");
        await shape.SyncAsync();
        var replication = sjc.Server.Services.GetRequiredService<RegionReplication>();
        await replication.SyncCatalogsAsync(token);
        await replication.PullAllAsync(token);
        var waiting = new TableShapeClient(local, ManagementApi.Uri(database.Data).ToString(), "items");
        var target = shape.Uri(shape.NextOffset, live: true);
        var parked = waiting.GetAsync(target);
        using var changed = await home.PostAsJsonAsync(ManagementApi.Uri(database.Data + "/sql/execute"),
            new { statements = new object[] { new { sql = "UPDATE items SET score = 30 WHERE id = 1" }, new { sql = "UPDATE items SET score = 0 WHERE id = 2" } } }, token);
        changed.EnsureSuccessStatusCode();
        await replication.PullAllAsync(token);
        await gru.DisposeAsync();
        var reply = await parked.WaitAsync(TimeSpan.FromSeconds(10), token);
        await Assert.That(reply.Status).IsEqualTo(HttpStatusCode.OK).Because(System.Text.Encoding.UTF8.GetString(reply.Body));
        await Assert.That(reply.Ops.Select(op => (op.GetProperty("op").GetString()!, op.GetProperty("key").GetString()!, op.GetProperty("move").GetString()!)).ToList())
            .IsEquivalentTo([("insert", "1", "in"), ("delete", "2", "out")]);
        var repeated = await waiting.GetAsync(target);
        await Assert.That(repeated.Body).IsEquivalentTo(reply.Body);
    }

    private static Task<ManagementApi> StartAsync(string region, TestIssuer issuer, List<RegionPeer> regions) => ManagementApi.StartAsync(
        settings => settings["region"] = region,
        services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IRegionPeers>(new RegionView(region, regions)));
            services.Replace(ServiceDescriptor.Singleton<IRegionCatalog>(new Serving()));
            services.Replace(ServiceDescriptor.Singleton<IReplicaPeers>(new ReplicaView(region, regions)));
        },
        sharedIssuer: issuer);

    private sealed class Serving : IRegionCatalog
    {
        public ValueTask<IReadOnlyList<string>> ServingAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<string>>(["gru", "sjc"]);
    }

    private sealed class RegionView(string region, List<RegionPeer> regions) : IRegionPeers
    {
        public ValueTask<IReadOnlyList<RegionPeer>> OtherRegionsAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<RegionPeer>>([.. regions.Where(peer => peer.Region != region)]);
    }

    private sealed class ReplicaView(string region, List<RegionPeer> regions) : IReplicaPeers
    {
        public ValueTask<IReadOnlyList<ReplicaPeer>> ListAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<ReplicaPeer>>([.. regions.Where(peer => peer.Region != region).Select(peer => new ReplicaPeer(peer.Region, peer.Region + "-1", peer.Address))]);
    }
}
