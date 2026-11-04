using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraBase.Features.Regions;
using TerraBase.Testing;

namespace TerraBase.Fast.Sql;

public sealed class CrossRegionMeteringTests
{
    private static readonly Faker Faker = new();

    [Test]
    public async Task Writes_through_every_region_are_counted_once_and_every_region_reports_them()
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
            descriptor.Claims["org_id"] = database.Organization;
            descriptor.Claims["scope"] = "databases:read databases:write sql:read sql:write";
        });
        using var viaGru = gru.WithToken(token);
        using var viaSjc = sjc.WithToken(token);
        using var viaFra = fra.WithToken(token);

        await SqlAsync(viaGru, database.Data, "CREATE TABLE notes (body TEXT)");
        await SqlAsync(viaSjc, database.Data, "INSERT INTO notes VALUES (?)", Faker.Lorem.Sentence());
        await SqlAsync(viaFra, database.Data, "INSERT INTO notes VALUES (?), (?)", Faker.Lorem.Sentence(), Faker.Lorem.Sentence());
        await SqlAsync(viaSjc, database.Data, "SELECT body FROM notes");

        using var created = await viaFra.PostAsJsonAsync(ManagementApi.Uri($"/v1/projects/{database.Project}/databases"), new { name = "frahome" });
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await created.Content.ReadAsStringAsync());
        var frahomeId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        var frahome = $"/v1/projects/{database.Project}/databases/frahome";
        await SqlAsync(viaFra, frahome, "CREATE TABLE notes (body TEXT)");
        await SqlAsync(viaFra, frahome, "INSERT INTO notes VALUES (?)", Faker.Lorem.Sentence());

        foreach (var client in new[] { viaGru, viaSjc, viaFra })
        {
            using var response = await client.GetAsync(ManagementApi.Uri($"/v1/projects/{database.Project}/usage"));
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await response.Content.ReadAsStringAsync());
            var usage = await response.Content.ReadFromJsonAsync<JsonElement>();
            var month = usage.GetProperty("month");
            await Assert.That((month.GetProperty("writeUnits").GetInt64(), month.GetProperty("organizationUnits").GetInt64()))
                .IsEqualTo((4L, 4L)).Because("three rows written through sjc and fra to the gru home and one row at the fra home bill four units, once");
            await Assert.That(month.GetProperty("readUnits").GetInt64()).IsEqualTo(3L).Because("the forwarded read counts its three rows as reads, not writes");
            await Assert.That(usage.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("databaseId").GetString()))
                .IsEquivalentTo([database.Id, frahomeId]);
        }

        using var allowances = await gru.Server.Client.GetAsync(ManagementApi.Uri("/v1/usage/allowances"));
        await Assert.That(allowances.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await allowances.Content.ReadAsStringAsync());
        var item = (await allowances.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray()
            .Single(organization => organization.GetProperty("organizationId").GetString() == database.Organization);
        await Assert.That((item.GetProperty("operations").GetInt64(), item.GetProperty("sqlReads").GetInt64())).IsEqualTo((4L, 3L));
    }

    private static async Task SqlAsync(HttpClient client, string database, string sql, params string[] args)
    {
        using var response = await client.PostAsJsonAsync(ManagementApi.Uri(database + "/sql/execute"), new
        {
            statements = new object[] { new { sql, args } },
        });
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await response.Content.ReadAsStringAsync());
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
