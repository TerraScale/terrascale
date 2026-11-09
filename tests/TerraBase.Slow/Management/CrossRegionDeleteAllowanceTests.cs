using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraBase.Features.Management;
using TerraBase.Features.Regions;
using TerraBase.Features.Replicas;
using TerraBase.Primitives;

namespace TerraBase.Slow.Management;

// A delete acknowledged with 204 releases the allowance in every region (GitHub #1098).
public sealed class CrossRegionDeleteAllowanceTests
{
    private const string Scopes = "databases:read databases:write";
    private static readonly string[] Sjc = ["sjc"];

    [Test]
    public async Task A_create_after_a_cross_region_delete_releases_the_allowance_and_still_enforces_it()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        using var issuer = new TestIssuer();
        var regions = new ConcurrentQueue<RegionPeer>();
        await using var gru = await StartAsync("gru", issuer, regions);
        regions.Enqueue(new RegionPeer("gru", gru.Server.BaseAddress));
        await using var sjc = await StartAsync("sjc", issuer, regions);
        regions.Enqueue(new RegionPeer("sjc", sjc.Server.BaseAddress));

        var organization = EntityIds.New().ToString();
        var project = EntityIds.New().ToString();
        using (var published = await gru.Server.Client.PutAsJsonAsync(
            ManagementApi.Uri($"/admin/organizations/{organization}/plan"), new { plan = "hobby" }, token))
        {
            await Assert.That(published.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await published.Content.ReadAsStringAsync(token));
        }

        var gruToken = gru.WithToken(Token(gru, organization, project));
        var sjcToken = sjc.WithToken(Token(sjc, organization, project));
        var path = $"/v1/projects/{project}/databases";
        using (var created = await gruToken.PostAsJsonAsync(ManagementApi.Uri(path), new { name = "first", replicaRegions = Sjc }, token))
        {
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await created.Content.ReadAsStringAsync(token));
        }

        await sjc.Server.Services.GetRequiredService<RegionReplication>().SyncCatalogsAsync(token);

        using (var deleted = await gruToken.DeleteAsync(ManagementApi.Uri($"{path}/first"), token))
        {
            await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent).Because(await deleted.Content.ReadAsStringAsync(token));
        }

        using (var created = await sjcToken.PostAsJsonAsync(ManagementApi.Uri(path), new { name = "second" }, token))
        {
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await created.Content.ReadAsStringAsync(token));
        }

        using (var refused = await gruToken.PostAsJsonAsync(ManagementApi.Uri(path), new { name = "third" }, token))
        {
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Conflict).Because(await refused.Content.ReadAsStringAsync(token));
        }
    }

    private static string Token(ManagementApi api, string organization, string project)
    {
        var bearer = api.Issuer.Mint(EntityIds.New().ToString(), descriptor =>
        {
            descriptor.Claims["project_id"] = project;
            descriptor.Claims["org_id"] = organization;
            descriptor.Claims["scope"] = Scopes;
        });
        api.Issuer.GrantProject(bearer, organization, project);
        return bearer;
    }

    private static Task<ManagementApi> StartAsync(string region, TestIssuer issuer, ConcurrentQueue<RegionPeer> regions) => ManagementApi.StartAsync(
        settings => settings["region"] = region,
        services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IRegionPeers>(new RegionView(region, regions)));
            services.Replace(ServiceDescriptor.Singleton<IReplicaPeers>(new ReplicaView(region, regions)));
            services.Replace(ServiceDescriptor.Singleton<IRegionCatalog>(new Serving()));
            services.Replace(ServiceDescriptor.Singleton(new DatabaseAllowance()));
        },
        sharedIssuer: issuer);

    private sealed class RegionView(string region, ConcurrentQueue<RegionPeer> regions) : IRegionPeers
    {
        public ValueTask<IReadOnlyList<RegionPeer>> OtherRegionsAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<RegionPeer>>([.. regions.Where(peer => peer.Region != region)]);
    }

    private sealed class ReplicaView(string region, ConcurrentQueue<RegionPeer> regions) : IReplicaPeers
    {
        public ValueTask<IReadOnlyList<ReplicaPeer>> ListAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<ReplicaPeer>>([.. regions.Where(peer => peer.Region != region)
                .Select(peer => new ReplicaPeer(peer.Region, peer.Region + "-1", peer.Address))]);
    }

    private sealed class Serving : IRegionCatalog
    {
        public ValueTask<IReadOnlyList<string>> ServingAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<string>>(["gru", "sjc"]);
    }
}
