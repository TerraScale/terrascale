using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraBase.Features.Regions;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class RegionalPlanTests
{
    [Test]
    public async Task A_published_plan_and_its_downgrade_apply_in_another_region()
    {
        await using var away = await ManagementApi.StartAsync(settings => settings["region"] = "sjc");
        await using var home = await StartHomeAsync(away.Server.BaseAddress);
        var organization = EntityIds.New().ToString();
        using var client = ProjectClient(away, organization, out var project);
        var path = ManagementApi.Uri($"/v1/projects/{project}/databases");

        using var published = await home.Server.Client.PutAsJsonAsync(ManagementApi.Uri($"/admin/organizations/{organization}/plan"), new { plan = "pro" });
        await Assert.That(published.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var first = await client.PostAsJsonAsync(path, new { name = "first" });
        using var second = await client.PostAsJsonAsync(path, new { name = "second" });
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.Created);

        using var downgraded = await home.Server.Client.PutAsJsonAsync(ManagementApi.Uri($"/admin/organizations/{organization}/plan"), new { plan = "hobby" });
        await Assert.That(downgraded.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var refused = await client.PostAsJsonAsync(path, new { name = "third" });
        await ErrorAssert.ErrorAsync(refused, HttpStatusCode.Conflict, "database_limit", "the organization's hobby plan allows 1 database; delete a database or upgrade the plan at https://dash.terrascale.tech/organization?tab=billing");
    }

    [Test]
    public async Task An_unknown_plan_and_an_unaccepted_publication_fail_closed()
    {
        await using var away = await ManagementApi.StartAsync();
        await using var home = await StartHomeAsync(away.Server.BaseAddress);
        var organization = EntityIds.New().ToString();
        using var client = ProjectClient(home, organization, out var project);
        using var unknown = await client.PostAsJsonAsync(ManagementApi.Uri($"/v1/projects/{project}/databases"), new { name = "unknown" });
        await ErrorAssert.ErrorAsync(unknown, HttpStatusCode.ServiceUnavailable, "plan_unknown", "the organization's plan has not been published in this region; retry shortly");

        var ownedElsewhere = await away.CreateOrganizationAsync("Owned elsewhere");

        using var unpublished = await home.Server.Client.PutAsJsonAsync(ManagementApi.Uri($"/admin/organizations/{ownedElsewhere}/plan"), new { plan = "pro" });
        await ErrorAssert.ErrorAsync(unpublished, HttpStatusCode.ServiceUnavailable, "plan_unpublished", "the plan is not published in every region yet; retry");
    }

    private static Task<ManagementApi> StartHomeAsync(Uri peer) => ManagementApi.StartAsync(settings => settings["region"] = "gru", services =>
        services.Replace(ServiceDescriptor.Singleton<IRegionPeers>(new OtherRegion(peer))));

    private static HttpClient ProjectClient(ManagementApi api, string organization, out string project)
    {
        var projectId = EntityIds.New().ToString();
        project = projectId;
        var token = api.Issuer.Mint(EntityIds.New().ToString(), descriptor =>
        {
            descriptor.Claims["project_id"] = projectId;
            descriptor.Claims["org_id"] = organization;
            descriptor.Claims["scope"] = "databases:read databases:write sql:read sql:write";
        });
        api.Issuer.GrantProject(token, organization, projectId);
        return api.WithToken(token);
    }

    private sealed class OtherRegion(Uri address) : IRegionPeers
    {
        public ValueTask<IReadOnlyList<RegionPeer>> OtherRegionsAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<RegionPeer>>([new RegionPeer("sjc", address)]);
    }
}
