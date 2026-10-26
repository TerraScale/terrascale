using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
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
    private readonly FakeMemberStore _members = new();
    private Socket? _unreachablePeer;

    public IReadOnlyList<ManagementApi> Regions => _regions;

    public string Organization { get; private set; } = string.Empty;

    public string Databases { get; private set; } = string.Empty;

    public static string Limit(string plan, int count) =>
        $"the organization's {plan} plan allows {count} database{(count == 1 ? string.Empty : "s")}; delete a database or upgrade the plan at https://dash.terrascale.tech/organization?tab=billing";

    public static async Task<DatabaseEscrowCell> StartAsync(string plan = "starter")
    {
        var cell = new DatabaseEscrowCell();
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
                    services.AddSingleton<IMemberDirectory>(cell._members);
                    services.Replace(ServiceDescriptor.Singleton(new DatabaseAllowance()));
                    services.Replace(ServiceDescriptor.Singleton<IEscrowPeers>(provider =>
                        new ObservedEscrowPeers(ActivatorUtilities.CreateInstance<EscrowPeers>(provider))));
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

    // A partition keeps the donors listed. Stopping them would instead remove their placement regions.
    public async Task IsolateAsync(int region)
    {
        var membership = _regions[region].Server.Services.GetRequiredService<IMembership>();
        _unreachablePeer = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        // Own the port without listening: real donor RPCs fail, and no parallel test can take this endpoint.
        _unreachablePeer.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)_unreachablePeer.LocalEndPoint!).Port;
        _members.Map = (viewer, member) => viewer == membership.Self.Name && member.Region != membership.Self.Region
            ? member with { PeerAddress = new UriBuilder(member.PeerAddress) { Host = IPAddress.Loopback.ToString(), Port = port }.Uri.ToString() }
            : member;
        await TerraBaseTestServer.EventuallyAsync(async token =>
        {
            var view = await membership.RefreshAsync(token);
            return view.Elsewhere.Length == 2 && view.Elsewhere.All(member => new Uri(member.PeerAddress).Port == port);
        }, timeout: TimeSpan.FromSeconds(30));
    }

    public async Task<string> DonorRegionsAsync(int region) =>
        string.Join(',', await _regions[region].Server.Services.GetRequiredService<IEscrowPeers>().RegionsAsync(CancellationToken.None));

    public string FailedDonorRegions(int region) =>
        string.Join(',', ((ObservedEscrowPeers)_regions[region].Server.Services.GetRequiredService<IEscrowPeers>()).FailedRegions.Order(StringComparer.Ordinal));

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
        _members.Map = null;
        try
        {
            for (var region = _regions.Count - 1; region >= 0; region--)
            {
                await StopAsync(region);
            }
        }
        finally
        {
            _unreachablePeer?.Dispose();
        }
    }

    // Observe the production escrow client; never replace its RPCs or manufacture a grant/refusal.
    private sealed class ObservedEscrowPeers(EscrowPeers inner) : IEscrowPeers
    {
        private readonly ConcurrentDictionary<string, byte> _failed = new(StringComparer.Ordinal);

        public IEnumerable<string> FailedRegions => _failed.Keys;

        public ValueTask<IReadOnlyList<string>> RegionsAsync(CancellationToken cancellationToken) => inner.RegionsAsync(cancellationToken);

        public async ValueTask<long?> TransferAsync(string region, string organizationId, long received, int want, CancellationToken cancellationToken)
        {
            var granted = await inner.TransferAsync(region, organizationId, received, want, cancellationToken);
            if (granted is null)
            {
                _ = _failed.TryAdd(region, 0);
            }

            return granted;
        }
    }
}
