using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Features.Management;
using TerraBase.Features.Replication;
using TerraBase.Primitives;

namespace TerraBase.Testing;

// Three real regions with real plan limits; gru registers first and holds the genesis units.
internal sealed class DatabaseEscrowCell : IAsyncDisposable
{
    private readonly List<ManagementApi> _regions = [];
    private readonly HashSet<int> _stopped = [];

    public IReadOnlyList<ManagementApi> Regions => _regions;

    public string Organization { get; private set; } = string.Empty;

    public string Databases { get; private set; } = string.Empty;

    public static string Limit(string plan, int count) =>
        $"the organization's {plan} plan allows {count} database{(count == 1 ? string.Empty : "s")}; delete a database or upgrade the plan at https://dash.terrascale.tech/organization?tab=billing";

    public static async Task<DatabaseEscrowCell> StartAsync(string plan = "starter")
    {
        var cell = new DatabaseEscrowCell();
        var members = new FakeMemberStore();
        var user = EntityIds.New().ToString();
        try
        {
            foreach (var region in new[] { "gru", "sjc", "fra" })
            {
                var s3 = new FakeS3();
                cell._regions.Add(await ManagementApi.StartAsync(settings =>
                {
                    s3.Configure(settings);
                    settings["region"] = region;
                    settings["node-name"] = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())[..14];
                }, services =>
                {
                    s3.Route(services);
                    ManualBackupHosting.Configure(services);
                    services.AddSingleton<IMemberDirectory>(members);
                    services.Replace(ServiceDescriptor.Singleton(new DatabaseAllowance()));
                }, userId: user));

                // Registered in start order, so gru's registration is the earliest.
                var system = await cell._regions[^1].Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
                await TerraBaseTestServer.EventuallyAsync(async token =>
                {
                    var transaction = await system.BeginTransactionAsync(write: false, token);
                    await using (transaction)
                    {
                        return await transaction.GetAsync("quota_regions", region, token) is not null;
                    }
                }, timeout: TimeSpan.FromSeconds(30));
            }

            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(cell.Regions.All(api =>
                api.Server.Services.GetRequiredService<IReplicationStatus>().Snapshot().Peers.Select(peer => peer.Region).Distinct(StringComparer.Ordinal).Count() == 2)),
                timeout: TimeSpan.FromSeconds(30));
            var gru = cell._regions[0];
            cell.Organization = await gru.CreateOrganizationAsync(new Faker().Company.CompanyName());
            var project = await gru.CreateProjectAsync(cell.Organization, new Faker().Commerce.Department());
            await gru.SetPlanAsync(user, cell.Organization, plan);
            cell.Databases = $"/v1/management/organizations/{cell.Organization}/projects/{project}/databases";
            await TerraBaseTestServer.EventuallyAsync(async token =>
            {
                foreach (var api in cell.Regions)
                {
                    using var organization = await api.Client.GetAsync(ManagementApi.Uri($"/v1/management/organizations/{cell.Organization}"), token);
                    using var databases = await api.Client.GetAsync(ManagementApi.Uri(cell.Databases), token);
                    if (organization.StatusCode != HttpStatusCode.OK || databases.StatusCode != HttpStatusCode.OK
                        || (await organization.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("plan").GetString() != plan)
                    {
                        return false;
                    }
                }

                return true;
            }, timeout: TimeSpan.FromSeconds(30));
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(cell.Regions.All(api =>
            {
                var status = api.Server.Services.GetRequiredService<IReplicationStatus>().Snapshot();
                return status.Initialized && status.Peers.Count == 2 && status.Peers.All(peer => peer.DurableLag == 0);
            })), timeout: TimeSpan.FromSeconds(30));
            return cell;
        }
        catch
        {
            await cell.DisposeAsync();
            throw;
        }
    }

    public Task<HttpResponseMessage> CreateAsync(int region, string name) =>
        _regions[region].Client.PostAsJsonAsync(ManagementApi.Uri(Databases), new { name });

    public async Task<string[]> ListAsync(int region)
    {
        var list = await _regions[region].Client.GetFromJsonAsync<JsonElement>(ManagementApi.Uri(Databases));
        return [.. list.GetProperty("items").EnumerateArray().Select(database => database.GetProperty("id").GetString()!)];
    }

    public Task CountEverywhereAsync(int count) => TerraBaseTestServer.EventuallyAsync(async _ =>
        (await Task.WhenAll(Enumerable.Range(0, _regions.Count).Select(ListAsync))).All(ids => ids.Length == count), timeout: TimeSpan.FromSeconds(30));

    public async Task DeleteAsync(int region, string id)
    {
        using var response = await _regions[region].Client.DeleteAsync(ManagementApi.Uri($"{Databases}/{id}"));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent).Because(await response.Content.ReadAsStringAsync());
    }

    public async Task StopAsync(int region)
    {
        if (_stopped.Add(region))
        {
            await _regions[region].DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        for (var region = _regions.Count - 1; region >= 0; region--)
        {
            await StopAsync(region);
        }
    }
}
