using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Features.Replication;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

/// <summary>
/// The every-region placement (<c>allRegions</c>, docs/terrabase/management-api.md): a database created while one region
/// serves takes a full copy into each region that joins later, with no call, and stops streaming to a region that leaves.
/// Real machines in three regions, discovered through one shared member directory.
/// </summary>
public sealed class AllRegionsPlacementTests
{
    private const string Collection = "items";

    private static readonly string[] Sjc = ["sjc"];

    [Test]
    public async Task A_database_in_every_region_reaches_regions_that_join_later_and_stops_streaming_to_one_that_leaves()
    {
        var members = new FakeMemberStore();
        await using var gru = await Machine.StartAsync(members, "gru");
        var organization = await gru.Api.CreateOrganizationAsync("Everywhere");
        var project = await gru.Api.CreateProjectAsync(organization, "Project");
        using var created = await gru.Api.Client.PostAsJsonAsync(
            ManagementApi.Uri($"/v1/management/organizations/{organization}/projects/{project}/databases"), new { name = "main", allRegions = true });
        var body = JsonElement.Parse(await created.Content.ReadAsStringAsync());
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(body.GetRawText());
        await Assert.That(body.GetProperty("allRegions").GetBoolean()).IsTrue();
        await Assert.That(body.GetProperty("replicaRegions").GetArrayLength()).IsEqualTo(0).Because("one region serves: valid, and it gains copies as regions join");
        var managed = new ManagedDatabaseRef(organization, project, body.GetProperty("id").GetString()!, "main");
        var tenant = new TenantContext(organization, PlanTier.Hobby, project, managed.Id);

        // sjc joins; nothing is called on the database.
        await using var sjc = await Machine.StartAsync(members, "sjc");
        await gru.ConnectedAsync("sjc");
        using var data = await gru.Api.CreateClientAsync(organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, managed, Collection);
        await PutAsync(data, managed, "first");
        await Assert.That(await sjc.HoldsAsync(tenant, "first")).IsTrue().Because("a region that joined after the create holds a copy");

        // fra joins later still, and refills what was written before it existed.
        await using var fra = await Machine.StartAsync(members, "fra");
        await Assert.That(await fra.HoldsAsync(tenant, "first")).IsTrue().Because("a region that joined later refills the database");

        // sjc leaves: gru stops streaming to it, and writes keep reaching the regions that remain.
        await sjc.StopAsync();
        await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(!gru.Peers().Contains("sjc")), timeout: TimeSpan.FromSeconds(30));
        await PutAsync(data, managed, "second");
        await Assert.That(await fra.HoldsAsync(tenant, "second")).IsTrue();
    }

    [Test]
    public async Task Create_and_update_set_and_clear_every_region_and_refuse_it_with_a_region_list()
    {
        await using var api = await ManagementApi.StartAsync();
        var organization = await api.CreateOrganizationAsync("Placement");
        var project = await api.CreateProjectAsync(organization, "Project");
        var databases = ManagementApi.Uri($"/v1/management/organizations/{organization}/projects/{project}/databases");

        using var both = await api.Client.PostAsJsonAsync(databases, new { name = "both", allRegions = true, replicaRegions = Sjc });
        await Assert.That(both.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await both.Content.ReadAsStringAsync()).Contains("placement_conflict");

        var id = await api.CreateDatabaseAsync(organization, project, "main");
        var database = ManagementApi.Uri($"/v1/management/organizations/{organization}/projects/{project}/databases/{id}");
        await Assert.That(await PatchAsync(api, database, new { allRegions = true })).IsTrue();
        await Assert.That(await PatchAsync(api, database, new { replicaRegions = Array.Empty<string>() })).IsFalse().Because("a region list turns every region off");
        using var conflicting = await api.Client.PatchAsJsonAsync(database, new { allRegions = true, replicaRegions = Sjc });
        await Assert.That(conflicting.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    // The allRegions of the updated database's body.
    private static async Task<bool> PatchAsync(ManagementApi api, Uri database, object request)
    {
        using var response = await api.Client.PatchAsJsonAsync(database, request);
        var body = await response.Content.ReadAsStringAsync();
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(body);
        return JsonElement.Parse(body).GetProperty("allRegions").GetBoolean();
    }

    private static async Task PutAsync(HttpClient data, ManagedDatabaseRef database, string id)
    {
        using var content = new StringContent("""{"n":1}""", Encoding.UTF8, "application/json");
        using var response = await data.PutAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, Collection, id)), content);
        await Assert.That(response.IsSuccessStatusCode).IsTrue().Because(await response.Content.ReadAsStringAsync());
    }

    private sealed class Machine(string region) : IAsyncDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("terrabase-all-regions-").FullName;
        private ManagementApi? _api;

        public ManagementApi Api => _api ?? throw new InvalidOperationException($"{region} is stopped");

        public static async Task<Machine> StartAsync(FakeMemberStore members, string region)
        {
            var machine = new Machine(region);
            try
            {
                machine._api = await ManagementApi.StartAsync(settings =>
                {
                    settings["region"] = region;
                    settings["node-name"] = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())[..14];
                    settings["data-directory"] = machine._directory;
                }, services => services.AddSingleton<IMemberDirectory>(members));
                return machine;
            }
            catch
            {
                await machine.DisposeAsync();
                throw;
            }
        }

        // The regions this machine has a connected peer stream to.
        public IReadOnlyList<string> Peers() =>
            [.. Api.Server.Services.GetRequiredService<IReplicationStatus>().Snapshot().Peers.Select(peer => peer.Region)];

        public Task ConnectedAsync(string other) =>
            TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(Peers().Contains(other)), timeout: TimeSpan.FromSeconds(30));

        // Whether this machine's own copy of the database holds the document within the deadline.
        public async Task<bool> HoldsAsync(TenantContext database, string id)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                if (await Api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, CancellationToken.None) is { } store)
                {
                    await using var transaction = await store.BeginTransactionAsync(write: false, CancellationToken.None);
                    if (await transaction.GetAsync(Collection, ManagementApi.DocumentId(id), CancellationToken.None) is not null)
                    {
                        return true;
                    }
                }

                await Task.Delay(100);
            }

            return false;
        }

        public async Task StopAsync()
        {
            await Api.DisposeAsync();
            _api = null;
        }

        public async ValueTask DisposeAsync()
        {
            if (_api is not null)
            {
                await StopAsync();
            }

            Directory.Delete(_directory, recursive: true);
        }
    }
}
