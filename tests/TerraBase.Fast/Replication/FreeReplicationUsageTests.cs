using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Features.Management;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Replication;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

/// <summary>
/// Free replication: a client operation is counted once, in the region that served it. Peer stream applies, the refill of a
/// region that joins later and anti-entropy rounds count nothing, and every region's flush adds to the replicated catalog
/// without overwriting another region's counts. Real machines in three regions, discovered through one member directory.
/// </summary>
public sealed class FreeReplicationUsageTests
{
    private const string Collection = "items";

    [Test]
    public async Task Each_client_operation_counts_once_in_its_region_and_replication_counts_nothing()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        var members = new FakeMemberStore();
        await using var gru = await Machine.StartAsync(members, "gru");
        await using var sjc = await Machine.StartAsync(members, "sjc");
        var organization = await gru.Api.CreateOrganizationAsync("Free replication");
        var project = await gru.Api.CreateProjectAsync(organization, "Project");
        using var created = await gru.Api.Client.PostAsJsonAsync(
            ManagementApi.Uri($"/v1/management/organizations/{organization}/projects/{project}/databases"), new { name = "main", allRegions = true }, token);
        var body = JsonElement.Parse(await created.Content.ReadAsStringAsync(token));
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(body.GetRawText());
        var managed = new ManagedDatabaseRef(organization, project, body.GetProperty("id").GetString()!, "main");
        var tenant = new TenantContext(organization, PlanTier.Hobby, project, managed.Id);
        await gru.ConnectedAsync("sjc");
        await sjc.ConnectedAsync("gru");
        using var gruData = await gru.Api.CreateClientAsync(organization, "documents:read", "documents:write");
        using var sjcData = await sjc.Api.CreateClientAsync(organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(gruData, managed, Collection);
        var before = await SettledAsync(tenant, [gru, sjc]);

        // Both regions take client writes before either flushes; each also reads one of the other's documents. One at a
        // time: a Hobby organization's request budget refuses a burst.
        string[] gruIds = ["g0", "g1", "g2", "g3", "g4"];
        string[] sjcIds = ["s0", "s1", "s2"];
        foreach (var id in gruIds)
        {
            await PutAsync(gruData, managed, id);
        }

        foreach (var id in sjcIds)
        {
            await PutAsync(sjcData, managed, id);
        }

        foreach (var id in gruIds.Concat(sjcIds))
        {
            await Assert.That(await gru.HoldsAsync(tenant, id) && await sjc.HoldsAsync(tenant, id)).IsTrue();
        }

        await GetAsync(gruData, managed, "s0");
        await GetAsync(sjcData, managed, "g0");

        // fra joins later and refills the whole database from its peers; then every region runs an anti-entropy round.
        await using var fra = await Machine.StartAsync(members, "fra");
        foreach (var id in gruIds.Concat(sjcIds))
        {
            await Assert.That(await fra.HoldsAsync(tenant, id)).IsTrue().Because("a region that joined later refills the database");
        }

        await Task.WhenAll(gru.AntiEntropy.RunRoundAsync(token), sjc.AntiEntropy.RunRoundAsync(token), fra.AntiEntropy.RunRoundAsync(token));
        await Assert.That(fra.Meter.PendingDatabases).IsEqualTo(0).Because("streamed applies, the refill and anti-entropy count no operation");

        // Concurrent flushes: each region adds to its own documents of the replicated catalog.
        var after = await SettledAsync(tenant, [gru, sjc, fra]);
        await Assert.That(after.Writes - before.Writes).IsEqualTo(gruIds.Length + sjcIds.Length).Because("each client write counts exactly once");
        await Assert.That(after.Reads - before.Reads).IsEqualTo(2).Because("each client read counts exactly once");
        await Assert.That(await fra.OwnUsageDocumentsAsync(managed.Id)).IsEmpty().Because("a region that served no client wrote no usage");
    }

    // Flushes every machine at once, then waits until each one's catalog reads the same lifetime totals with nothing pending.
    private static async Task<OpCounts> SettledAsync(TenantContext database, Machine[] machines)
    {
        await Task.WhenAll(machines.Select(machine => machine.Meter.FlushAsync(CancellationToken.None).AsTask()));
        OpCounts? settled = null;
        await TerraBaseTestServer.EventuallyAsync(async token =>
        {
            var totals = new List<OpCounts>();
            foreach (var machine in machines)
            {
                await machine.Meter.FlushAsync(token);
                var window = new UsageWindow(UsageWindow.Hour, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 3600, 1);
                if (await machine.Meter.GetAsync(database, window, token) is not UsageCounts counts || machine.Meter.PendingDatabases != 0)
                {
                    return false;
                }

                totals.Add(counts.Lifetime with { Meters = null });
            }

            settled = totals[0];
            return totals.All(total => total == settled);
        }, timeout: TimeSpan.FromSeconds(30));
        return settled!;
    }

    private static async Task PutAsync(HttpClient data, ManagedDatabaseRef database, string id)
    {
        using var content = new StringContent("""{"n":1}""", Encoding.UTF8, "application/json");
        using var response = await data.PutAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, Collection, id)), content);
        await Assert.That(response.IsSuccessStatusCode).IsTrue().Because(await response.Content.ReadAsStringAsync());
    }

    private static async Task GetAsync(HttpClient data, ManagedDatabaseRef database, string id)
    {
        using var response = await data.GetAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, Collection, id)));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    private sealed class Machine(string region) : IAsyncDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("terrabase-free-replication-").FullName;
        private ManagementApi? _api;

        public ManagementApi Api => _api ?? throw new InvalidOperationException($"{region} is stopped");

        public UsageMeter Meter => Api.Server.Services.GetRequiredService<UsageMeter>();

        public AntiEntropy AntiEntropy => Api.Server.Services.GetRequiredService<AntiEntropy>();

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
                }, services =>
                {
                    services.AddSingleton<IMemberDirectory>(members);
                    // The test flushes every region at the same moment, as two regions' timers eventually do.
                    services.Remove(services.Single(service => service.ServiceType == typeof(IHostedService) && service.ImplementationType == typeof(UsageFlushService)));
                });
                return machine;
            }
            catch
            {
                await machine.DisposeAsync();
                throw;
            }
        }

        public Task ConnectedAsync(string other) =>
            TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(Api.Server.Services.GetRequiredService<IReplicationStatus>()
                .Snapshot().Peers.Any(peer => peer.Region == other)), timeout: TimeSpan.FromSeconds(30));

        // The usage documents this machine's node wrote for the database.
        public async Task<string[]> OwnUsageDocumentsAsync(string databaseId)
        {
            var node = Api.Server.Services.GetRequiredService<TerraBaseOptions>().MachineId;
            var system = await Api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
            await using var transaction = await system.BeginTransactionAsync(write: false, CancellationToken.None);
            List<string> own = [];
            foreach (var collection in new[] { "usage", "usage-minutes", "usage-hours", "usage-days" })
            {
                var page = await transaction.ListAsync(SystemCatalog.DatabaseState(databaseId) + collection, afterId: null, limit: 1000, 1024 * 1024, CancellationToken.None);
                own.AddRange(page.Documents.Select(document => document.Id).Where(id => id.EndsWith("~" + node, StringComparison.Ordinal)));
            }

            return [.. own];
        }

        public async Task<bool> HoldsAsync(TenantContext database, string id)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                if (await Api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, CancellationToken.None) is { } store)
                {
                    await using var transaction = await store.BeginTransactionAsync(write: false, CancellationToken.None);
                    if (await transaction.GetAsync(Collection, id, CancellationToken.None) is not null)
                    {
                        return true;
                    }
                }

                await Task.Delay(100);
            }

            return false;
        }

        public async ValueTask DisposeAsync()
        {
            if (_api is not null)
            {
                await _api.DisposeAsync();
                _api = null;
            }

            Directory.Delete(_directory, recursive: true);
        }
    }
}
