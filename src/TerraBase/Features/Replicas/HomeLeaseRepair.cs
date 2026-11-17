using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net.Http.Json;
using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Replicas;

internal closed record class HomeLeaseRepairOutcome;

internal sealed record class LeaseRestamped(HomeLease? Previous, HomeLease Lease) : HomeLeaseRepairOutcome;

internal sealed record class LeaseUnchanged(HomeLease Lease) : HomeLeaseRepairOutcome;

internal sealed record class LeaseRepairRefused(string Code, string Reason) : HomeLeaseRepairOutcome;

internal sealed class HomeLeaseRepair(
    TerraBaseOptions options, IReplicaCatalog catalog, HomeLeases leases, RegionalOwners owners, RegionalHandoff handoff,
    InternalCredential credential, IMeterFactory meters, ILogger<HomeLeaseRepair> logger) : IDisposable
{
    private static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(5);
    private readonly HttpClient _http = PeerHttp.Create(credential);
    private readonly Counter<long> _repairs = meters.Create(Telemetry.MeterName)
        .CreateCounter<long>("terrabase.home.repairs", "{repair}", "Operator home lease repairs on this node, by outcome.");

    public void Dispose() => _http.Dispose();

    internal async Task<HomeLeaseRepairOutcome> RepairAsync(TenantContext database, string caller, CancellationToken cancellationToken)
    {
        var outcome = await DecideAsync(database, cancellationToken).ConfigureAwait(false);
        var (result, detail) = outcome switch
        {
            LeaseRestamped(var previous, var lease) =>
                ("restamped", $"from {previous?.Node ?? "no lease"} at epoch {previous?.Epoch ?? 0} to {lease.Node} at epoch {lease.Epoch}"),
            LeaseUnchanged(var lease) => ("unchanged", $"{lease.Node} holds it at epoch {lease.Epoch}"),
            LeaseRepairRefused(var code, var reason) => (code, reason),
        };
        _repairs.Add(1, new KeyValuePair<string, object?>("result", result));
        logger.ZLogWarning($"Home lease repair of database {database.DatabaseId:@Database} by {caller:@Caller}: {result:@Result}, {detail:@Detail}");
        return outcome;
    }

    private async Task<HomeLeaseRepairOutcome> DecideAsync(TenantContext database, CancellationToken cancellationToken)
    {
        if (await leases.RepairBlockerAsync(database.DatabaseId!, cancellationToken).ConfigureAwait(false) is { } blocker)
        {
            return new LeaseRepairRefused(blocker, Blocked(blocker));
        }

        if (await catalog.FindAsync(database, cancellationToken).ConfigureAwait(false) is not TenantDatabaseRecord { Placement: var placement }
            || placement.Home != options.Region)
        {
            return new LeaseRepairRefused("not_home", "this region is not the database's home");
        }

        if (placement is { AllRegions: false, Replicas.Count: 0 })
        {
            return new LeaseRepairRefused("not_leased", "a database without replicas has no home lease");
        }

        if (await leases.ReadLeaseAsync(database.DatabaseId!, cancellationToken).ConfigureAwait(false) is not { } read)
        {
            return new LeaseRepairRefused("lease_store_unready", "the home lease store is not ready on this node");
        }

        if (read.Lease is { } own && own.Node == options.MachineId && own.Epoch >= placement.Version)
        {
            return new LeaseUnchanged(own);
        }

        if (read.Lease is { } newer && newer.Epoch >= placement.Version)
        {
            return new LeaseRepairRefused("lease_newer", $"the lease names {newer.Node} at epoch {newer.Epoch}, not older than placement version {placement.Version}");
        }

        var nodes = await new PeersFile(options).ListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var region in nodes.Select(node => node.Region).Where(region => region != options.Region).Distinct(StringComparer.Ordinal))
        {
            if (owners.Confirmed(region) is not { Activated: true, Node: var writer } || nodes.FirstOrDefault(node => node.Name == writer) is not { } peer)
            {
                return new LeaseRepairRefused("writer_unknown", $"region {region} has no confirmed writer");
            }

            if (await PlacementAsync(peer, database, cancellationToken).ConfigureAwait(false) is not ReplicaCatalogEntry entry
                || entry.HomeRegion != placement.Home || entry.PlacementVersion != placement.Version)
            {
                return new LeaseRepairRefused("placement_disagrees", $"the writer of {region} does not record home {placement.Home} at placement version {placement.Version}");
            }
        }

        if (read.Lease is { } stale && stale.Node != options.MachineId)
        {
            if (nodes.FirstOrDefault(node => node.Name == stale.Node) is not { } named)
            {
                return new LeaseRepairRefused("lease_node_unknown", $"the lease names {stale.Node}, which is not in the peers file");
            }

            var confirmed = await PlacementAsync(named, database, cancellationToken).ConfigureAwait(false) switch
            {
                ErrorDetail { Code: "not_writer" } => true,
                ReplicaCatalogEntry entry => entry.PlacementVersion < placement.Version || entry.HomeRegion != named.Region,
                _ => false,
            };
            if (!confirmed)
            {
                return new LeaseRepairRefused("lease_node_disagrees", $"{stale.Node} did not confirm that it is not the home or records an older placement");
            }
        }

        if (await handoff.AppliedRefusalAsync(database, cancellationToken).ConfigureAwait(false) is { } refusal)
        {
            return new LeaseRepairRefused("unproven", $"the applied-position proof failed: the database {refusal}");
        }

        return await leases.RestampAsync(database, placement, read, cancellationToken).ConfigureAwait(false) is { } lease
            ? new LeaseRestamped(read.Lease, lease)
            : new LeaseRepairRefused("lease_changed", "the lease, the writer role, a fence or a home move changed while the repair checked; run it again");
    }

    private static string Blocked(string blocker) => blocker switch
    {
        "not_writer" => "this node is not its region's writer",
        "handing_off" => "this region's writer role is being handed off",
        "fenced" => "the database is fenced on this node",
        "moving" => "a home move of the database is in progress",
        _ => throw new UnreachableException(),
    };

    private async Task<PeerAnswer> PlacementAsync(ServingNode node, TenantContext database, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PeerTimeout);
        try
        {
            using var response = await _http.GetAsync(new Uri(node.InternalListener(options.InternalPort), ReplicaRoutes.DatabasePath(database, "placement")), timeout.Token)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync(ReplicasJsonContext.Default.ReplicaCatalogEntry, timeout.Token).ConfigureAwait(false) is { } entry ? entry : new Unreached()
                : await response.Content.ReadFromJsonAsync(TerraBaseJsonContext.Default.ErrorBody, timeout.Token).ConfigureAwait(false) is { Error: var error } ? error : new Unreached();
        }
        catch (Exception error) when (error is HttpRequestException or JsonException || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new Unreached();
        }
    }

    private sealed record Unreached;

    private readonly union PeerAnswer(ReplicaCatalogEntry, ErrorDetail, Unreached);
}
