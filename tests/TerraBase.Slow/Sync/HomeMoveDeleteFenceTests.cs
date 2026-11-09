using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraBase.Features.Regions;
using TerraBase.Features.Replicas;
using TerraBase.Hosting;
using TerraBase.Primitives;

namespace TerraBase.Slow.Sync;

public sealed class HomeMoveDeleteFenceTests
{
    private static readonly Faker Faker = new();

    [Test]
    public async Task A_delete_is_refused_while_a_home_move_holds_the_database_and_wins_at_the_new_home()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        using var issuer = new TestIssuer();
        var mesh = new Mesh();
        var park = new Park();
        await using var gru = await StartAsync("gru", issuer, mesh, checkpoint: null);
        await using var sjc = await StartAsync("sjc", issuer, mesh, checkpoint: park);
        mesh.Nodes = [("gru", gru), ("sjc", sjc)];

        var organization = await gru.CreateOrganizationAsync("Org " + EntityIds.New());
        var project = await gru.CreateProjectAsync(organization, "Project");
        await gru.SetPlanAsync(gru.User, organization, "hobby", token);
        var bearer = issuer.Mint(gru.User, descriptor =>
        {
            descriptor.Claims["project_id"] = project;
            descriptor.Claims["org_id"] = organization;
            descriptor.Claims["scope"] = "databases:read databases:write";
        });
        using var viaGru = gru.WithToken(bearer);
        using var viaSjc = sjc.WithToken(bearer);
        var name = "fenced" + Faker.Random.AlphaNumeric(6).ToLowerInvariant();
        using var created = await viaGru.PostAsJsonAsync(ManagementApi.Uri($"/v1/projects/{project}/databases"), new { name, allRegions = true }, token);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await created.Content.ReadAsStringAsync(token));
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("id").GetString()!;

        var replication = sjc.Server.Services.GetRequiredService<RegionReplication>();
        await replication.SyncCatalogsAsync(token);
        await replication.PullAllAsync(token);
        park.Promoting = id;

        var move = Task.Run(async () =>
        {
            using var sent = await viaGru.PostAsJsonAsync(ManagementApi.Uri($"/v1/projects/{project}/databases/{id}/move-home"), new { homeRegion = "sjc" }, token);
            await Assert.That(sent.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await sent.Content.ReadAsStringAsync(token));
            return (await sent.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("homeRegion").GetString()!;
        }, token);
        await park.Parked.Task.WaitAsync(TimeSpan.FromSeconds(30), token);

        try
        {
            using var refused = await viaGru.DeleteAsync(ManagementApi.Uri($"/v1/projects/{project}/databases/{name}"), token);
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Conflict).Because(await refused.Content.ReadAsStringAsync(token));
            var error = (await refused.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("error");
            await Assert.That(error.GetProperty("code").GetString()).IsEqualTo("move_in_progress");
        }
        finally
        {
            park.Resume.TrySetResult();
        }

        await Assert.That(await move.WaitAsync(TimeSpan.FromSeconds(30), token)).IsEqualTo("sjc");

        using (var deleted = await viaSjc.DeleteAsync(ManagementApi.Uri($"/v1/projects/{project}/databases/{name}"), token))
        {
            await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent).Because(await deleted.Content.ReadAsStringAsync(token));
        }

        using var read = await viaGru.GetAsync(ManagementApi.Uri($"/v1/projects/{project}/databases/{name}"), token);
        await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    private sealed class Park
    {
        public string? Promoting;
        public TaskCompletionSource Parked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static Task<ManagementApi> StartAsync(string region, TestIssuer issuer, Mesh mesh, Park? checkpoint) => ManagementApi.StartAsync(
        settings => settings["region"] = region,
        services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IRegionPeers>(new RegionView(region, mesh)));
            services.Replace(ServiceDescriptor.Singleton<IRegionCatalog>(new Serving()));
            services.Replace(ServiceDescriptor.Singleton<IReplicaPeers>(new ReplicaView(region, mesh)));
            if (checkpoint is not null)
            {
                services.Replace(ServiceDescriptor.Singleton<DatabaseLifecycleCheckpoint>((step, database) =>
                {
                    if (step == "create.published" && database.DatabaseId == checkpoint.Promoting)
                    {
                        checkpoint.Parked.TrySetResult();
                        checkpoint.Resume.Task.Wait();
                    }
                }));
            }
        },
        sharedIssuer: issuer);

    private sealed class Mesh
    {
        public (string Region, ManagementApi Api)[] Nodes { get; set; } = [];
    }

    private sealed class RegionView(string region, Mesh mesh) : IRegionPeers
    {
        public ValueTask<IReadOnlyList<RegionPeer>> OtherRegionsAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<RegionPeer>>([.. mesh.Nodes.Where(node => node.Region != region).Select(node => new RegionPeer(node.Region, node.Api.Server.BaseAddress))]);
    }

    private sealed class ReplicaView(string region, Mesh mesh) : IReplicaPeers
    {
        public ValueTask<IReadOnlyList<ReplicaPeer>> ListAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<ReplicaPeer>>([.. mesh.Nodes.Where(node => node.Region != region)
                .Select(node => new ReplicaPeer(node.Region, node.Region + "-1", node.Api.Server.BaseAddress))]);
    }

    private sealed class Serving : IRegionCatalog
    {
        public ValueTask<IReadOnlyList<string>> ServingAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<string>>(["gru", "sjc"]);
    }
}
