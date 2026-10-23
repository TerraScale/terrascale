using System.Net;
using System.Text.Json;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

/// <summary>
/// The region catalog (<c>GET /v1/regions</c>, TS-901) lists every region with a live member, and database placement accepts only
/// those regions (TS-885): an unknown region answers 400 naming the serving ones, and a removed region can be added back.
/// </summary>
public sealed class RegionCatalogTests
{
    private static readonly string[] Mars = ["mars"];
    private static readonly string[] SjcAndFra = ["sjc", "fra"];
    private static readonly string[] FraAndMars = ["fra", "mars"];

    [Test]
    public async Task The_catalog_lists_every_serving_region_and_placement_accepts_only_those()
    {
        await using var api = await ManagementApi.StartAsync(services: new OtherRegions("sjc", "fra").Register);

        using (var catalog = await api.Server.Client.GetAsync(ManagementApi.Uri("/v1/regions")))
        {
            var body = await catalog.Content.ReadAsStringAsync();
            await Assert.That(catalog.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(body);
            await Assert.That(body).IsEqualTo("""{"regions":["fra","sjc","test-region"]}""");
        }

        var organization = await api.CreateOrganizationAsync("Regions");
        var project = await api.CreateProjectAsync(organization, "Project");
        var databases = ManagementApi.Uri($"/v1/management/organizations/{organization}/projects/{project}/databases");

        await AssertUnservedAsync(await api.Client.PostAsJsonAsync(databases, new { name = "mars", replicaRegions = Mars }));
        await AssertUnservedAsync(await api.Client.PostAsJsonAsync(databases, new { name = "home", homeRegion = "mars" }));

        using var created = await api.Client.PostAsJsonAsync(databases, new { name = "main", replicaRegions = SjcAndFra });
        var createdBody = await created.Content.ReadAsStringAsync();
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(createdBody);
        var database = ManagementApi.Uri($"{databases}/{JsonElement.Parse(createdBody).GetProperty("id").GetString()}");

        await Assert.That(await PatchAsync(api, database, ["fra"])).IsEquivalentTo(["fra"]);
        await Assert.That(await PatchAsync(api, database, ["fra", "sjc"])).IsEquivalentTo(["fra", "sjc"]).Because("a removed region can be added back");
        await AssertUnservedAsync(await api.Client.PatchAsJsonAsync(database, new { replicaRegions = FraAndMars }));
    }

    private static async Task AssertUnservedAsync(HttpResponseMessage response)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync();
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest).Because(body);
            var error = JsonElement.Parse(body).GetProperty("error");
            await Assert.That(error.GetProperty("code").GetString()).IsEqualTo("invalid_request");
            await Assert.That(error.GetProperty("message").GetString()).Contains("mars").And.Contains("fra, sjc, test-region");
        }
    }

    // The replicaRegions of the updated database's body.
    private static async Task<string[]> PatchAsync(ManagementApi api, Uri database, string[] replicaRegions)
    {
        using var response = await api.Client.PatchAsJsonAsync(database, new { replicaRegions });
        var body = await response.Content.ReadAsStringAsync();
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(body);
        return [.. JsonElement.Parse(body).GetProperty("replicaRegions").EnumerateArray().Select(region => region.GetString()!)];
    }
}
