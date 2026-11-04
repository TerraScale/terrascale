using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraBase.Features.Regions;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Slow.Platform;

public sealed class HomeRegionForwardingTests
{
    private static readonly Faker Faker = new();

    [Test]
    public async Task Rest_and_postgres_in_other_regions_reach_the_database_home_and_every_region_creates_lists_and_drops()
    {
        using var issuer = new TestIssuer();
        var mesh = new Mesh();
        await using var gru = await StartAsync("gru", issuer, mesh);
        await using var sjc = await StartAsync("sjc", issuer, mesh);
        await using var fra = await StartAsync("fra", issuer, mesh);
        mesh.Peers = [new("gru", gru.Server.BaseAddress), new("sjc", sjc.Server.BaseAddress), new("fra", fra.Server.BaseAddress)];
        var database = await gru.CreateManagedDatabaseAsync();
        var token = issuer.Mint(gru.User, descriptor =>
        {
            descriptor.Claims["project_id"] = database.Project;
            descriptor.Claims["scope"] = "databases:read documents:read documents:write";
        });
        var note = Faker.Lorem.Sentence();

        using var viaSjc = sjc.WithToken(token);
        using var written = await viaSjc.PostAsJsonAsync(ManagementApi.Uri(database.Data + "/sql/execute"), new
        {
            statements = new object[] { new { sql = "CREATE TABLE notes (body TEXT)" }, new { sql = "INSERT INTO notes VALUES (?)", args = new[] { note } } },
        });
        using var viaFra = fra.WithToken(token);
        using var listed = await viaFra.GetAsync(ManagementApi.Uri($"/v1/projects/{database.Project}/databases"));
        await Assert.That(written.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await written.Content.ReadAsStringAsync());
        await using var postgres = PostgresSql.Source(fra, DashboardKey(issuer, database.Project), database.Name);
        await using var connection = await postgres.OpenConnectionAsync();
        var read = await PostgresSql.ScalarAsync(connection, "SELECT body FROM notes");

        await Assert.That(written.Headers.GetValues("X-TerraBase-Region")).IsEquivalentTo(["gru"]);
        await Assert.That(listed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var items = (await listed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray();
        await Assert.That(items.Select(item => item.GetProperty("name").GetString())).Contains(database.Name);
        await Assert.That(read).IsEqualTo(note);

        using var manager = sjc.WithToken(issuer.Mint(gru.User, descriptor =>
        {
            descriptor.Claims["project_id"] = database.Project;
            descriptor.Claims["org_id"] = database.Organization;
            descriptor.Claims["scope"] = "databases:read databases:write";
        }));
        var databases = ManagementApi.Uri($"/v1/projects/{database.Project}/databases");
        using var created = await manager.PostAsJsonAsync(databases, new { name = "sjc-home" });
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await created.Content.ReadAsStringAsync());
        var homed = await created.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(homed.GetProperty("homeRegion").GetString()).IsEqualTo("sjc");
        await Assert.That(await NamesAsync(viaFra, databases)).IsEquivalentTo([database.Name, "sjc-home"]);

        using var dropped = await manager.DeleteAsync(ManagementApi.Uri($"/v1/projects/{database.Project}/databases/{homed.GetProperty("id").GetString()}"));
        await Assert.That(dropped.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        using var viaGru = gru.WithToken(token);
        await Assert.That(await NamesAsync(viaGru, databases)).IsEquivalentTo([database.Name]);
    }

    private static async Task<List<string>> NamesAsync(HttpClient client, Uri databases)
    {
        using var response = await client.GetAsync(databases);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        return [.. (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray().Select(item => item.GetProperty("name").GetString()!)];
    }

    private static Task<ManagementApi> StartAsync(string region, TestIssuer issuer, Mesh mesh) => ManagementApi.StartAsync(
        settings => settings["region"] = region,
        services => services.Replace(ServiceDescriptor.Singleton<IRegionPeers>(new RegionView(region, mesh))),
        sharedIssuer: issuer);

    private static string DashboardKey(TestIssuer issuer, string project)
    {
        var id = EntityIds.New().ToString();
        var key = id + "_" + Faker.Random.String2(52, "0123456789abcdefghjkmnpqrstvwxyz");
        issuer.GrantClient(key, () => issuer.Mint(id, token =>
        {
            token.Claims["key_id"] = id;
            token.Claims["key_gen"] = TestIssuer.KeyGeneration(key);
            token.Claims["project_id"] = project;
            token.Claims["scope"] = "documents:read documents:write";
        }));
        return key;
    }

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
