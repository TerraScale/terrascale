using System.Net;
using System.Text.Json;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class RegionCatalogTests
{
    [Test]
    public async Task The_catalog_lists_the_local_region_and_remote_placement_is_rejected()
    {
        await using var api = await ManagementApi.StartAsync();
        using var catalog = await api.Server.Client.GetAsync(ManagementApi.Uri("/v1/regions"));
        await Assert.That(catalog.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var regions = await catalog.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(regions.GetProperty("regions").EnumerateArray().Select(region => region.GetString()!)).IsEquivalentTo(["test-region"]);
        var database = await api.CreateManagedDatabaseAsync();
        var databases = ManagementApi.Uri($"/v1/management/organizations/{database.Organization}/projects/{database.Project}/databases");
        await RejectAsync(await api.Client.PostAsJsonAsync(databases, new { name = "replica", replicaRegions = new[] { "sjc" } }));
        await RejectAsync(await api.Client.PostAsJsonAsync(databases, new { name = "global", allRegions = true }));
        await RejectAsync(await api.Client.PatchAsJsonAsync(ManagementApi.Uri(database.Path), new { replicaRegions = new[] { "sjc" } }));
        await RejectAsync(await api.Client.PatchAsJsonAsync(ManagementApi.Uri(database.Path), new { allRegions = true }));
        using var local = await api.Client.GetAsync(ManagementApi.Uri(database.Path));
        local.EnsureSuccessStatusCode();
        var record = await local.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(record.GetProperty("homeRegion").GetString()).IsEqualTo("test-region");
        await Assert.That(record.GetProperty("replicaRegions").GetArrayLength()).IsEqualTo(0);
    }

    private static async Task RejectAsync(HttpResponseMessage response)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync();
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest).Because(body);
            await Assert.That(JsonElement.Parse(body).GetProperty("error").GetProperty("code").GetString()).IsEqualTo("replicas_unavailable");
        }
    }
}
