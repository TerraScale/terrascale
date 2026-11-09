using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraBase.Features.Regions;
using TerraBase.Features.Replicas;
using TerraBase.Primitives;

namespace TerraBase.Slow.Platform;

/// <summary>Session WebSockets in a region that does not serve their database relay the whole socket to the home.</summary>
public sealed class HomeSocketRelayTests
{
    private static readonly Faker Faker = new();

    [Test]
    public async Task Sql_websockets_in_regions_without_the_database_relay_to_the_home_whether_or_not_they_hold_its_record()
    {
        using var issuer = new TestIssuer();
        var mesh = new Mesh();
        await using var gru = await StartAsync("gru", issuer, mesh);
        await using var sjc = await StartAsync("sjc", issuer, mesh);
        await using var fra = await StartAsync("fra", issuer, mesh);
        mesh.Peers = [new("gru", gru.Server.BaseAddress), new("sjc", sjc.Server.BaseAddress), new("fra", fra.Server.BaseAddress)];
        var database = await gru.CreateManagedDatabaseAsync();
        await gru.SetPlanAsync(gru.User, database.Organization, "hobby");
        var token = issuer.Mint(gru.User, descriptor =>
        {
            descriptor.Claims["project_id"] = database.Project;
            descriptor.Claims["scope"] = "databases:read sql:read sql:write";
        });

        // fra keeps the home-only database's record without a placement here, as project enrollment leaves it;
        // sjc holds no record and locates the home by asking the mesh.
        var recorded = await fra.Server.Services.GetRequiredService<IReplicaCatalog>().RecordAsync(
            new ReplicaCatalogEntry(database.Organization, database.Project, database.Id, database.Name, "gru", [], 1), CancellationToken.None);
        await Assert.That(recorded).IsTrue();

        var note = Faker.Lorem.Sentence();
        foreach (var (region, api, id) in new[] { ("sjc", sjc, 1), ("fra", fra, 2) })
        {
            await using var socket = await SqlSocketClient.ConnectAsync(api, database.Data, token);
            var created = await socket.ExecuteAsync(new { sql = "CREATE TABLE IF NOT EXISTS notes (id INTEGER PRIMARY KEY, body TEXT NOT NULL)" });
            await Assert.That(created.GetProperty("type").GetString()).IsEqualTo("result").Because(region + ": " + created);
            var written = await socket.ExecuteAsync(new { sql = "INSERT INTO notes VALUES (?, ?)", args = new object[] { id, note } });
            await Assert.That(written.GetProperty("type").GetString()).IsEqualTo("result").Because(region + ": " + written);
            var read = await socket.ExecuteAsync(new { sql = "SELECT body FROM notes WHERE id = ?", args = new object[] { id } });
            await Assert.That(read.GetProperty("results")[0].GetProperty("rows")[0][0].GetString()).IsEqualTo(note).Because(region + ": " + read);
        }

        // A browser-style socket authenticates with a first frame instead of a header: the region resolves the home from
        // the route alone and relays, and the home runs the frame's checks itself.
        await using var frame = await SqlSocketClient.ConnectAsync(sjc, database.Data, apiKey: null);
        var admitted = await frame.AuthAsync(token);
        await Assert.That(admitted.GetProperty("type").GetString()).IsEqualTo("result").Because(admitted.ToString());
        var framed = await frame.ExecuteAsync(new { sql = "INSERT INTO notes VALUES (3, ?)", args = new object[] { note } });
        await Assert.That(framed.GetProperty("type").GetString()).IsEqualTo("result").Because(framed.ToString());

        // The sessions ran at gru: the home serves the rows, not the relaying regions.
        using var viaGru = gru.WithToken(token);
        using var atHome = await viaGru.PostAsJsonAsync(ManagementApi.Uri(database.Data + "/sql/execute"),
            new { statements = new object[] { new { sql = "SELECT count(*) FROM notes WHERE body = ?", args = new[] { note } } } });
        await Assert.That(atHome.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await atHome.Content.ReadAsStringAsync());
        await Assert.That((await atHome.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results")[0].GetProperty("rows")[0][0].GetInt64())
            .IsEqualTo(3).Because("every relayed socket wrote at the home");
    }

    private static Task<ManagementApi> StartAsync(string region, TestIssuer issuer, Mesh mesh) => ManagementApi.StartAsync(
        settings => settings["region"] = region,
        services => services.Replace(ServiceDescriptor.Singleton<IRegionPeers>(new RegionView(region, mesh))),
        sharedIssuer: issuer);

    private sealed class Mesh
    {
        public RegionPeer[] Peers { get; set; } = [];
    }

    private sealed class RegionView(string region, Mesh mesh) : IRegionPeers
    {
        public ValueTask<IReadOnlyList<RegionPeer>> OtherRegionsAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<RegionPeer>>([.. mesh.Peers.Where(peer => peer.Region != region)]);
    }
}
