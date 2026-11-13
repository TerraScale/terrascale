using System.Collections.Concurrent;
using TerraBase.Configuration;
using TerraBase.Tenancy;

namespace TerraBase.Features.Replicas;

internal interface IReplicaStatus
{
    ValueTask<bool> RecordAsync(TenantContext database, ReplicaStatusReport report, CancellationToken cancellationToken);

    IReadOnlyList<ReplicaStatusBody> Local(TenantContext database, IReadOnlyList<string> replicas);
}

internal sealed class ReplicaStatusBoard(TerraBaseOptions options, IReplicaCatalog catalog, TimeProvider time) : IReplicaStatus
{
    internal static readonly TimeSpan StaleAfter = RegionReplication.Sweep * 3;

    private readonly ConcurrentDictionary<(string Organization, string Project, string Database, string Region), (ReplicaStatusReport Report, DateTimeOffset At)> _reports = new();

    public async ValueTask<bool> RecordAsync(TenantContext database, ReplicaStatusReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!ReplicaStates.IsReported(report.State)
            || await catalog.FindAsync(database, cancellationToken).ConfigureAwait(false) is not TenantDatabaseRecord { Placement: var placement }
            || placement.Home != options.Region || report.Region == options.Region || !placement.PlacedIn(report.Region))
        {
            return false;
        }

        _reports[Key(database, report.Region)] = (report, time.GetUtcNow());
        foreach (var stale in _reports.Where(entry => entry.Key.Database == database.DatabaseId && (entry.Key.Region == options.Region || !placement.PlacedIn(entry.Key.Region))).Select(entry => entry.Key).ToList())
        {
            _ = _reports.TryRemove(stale, out _);
        }

        return true;
    }

    public IReadOnlyList<ReplicaStatusBody> Local(TenantContext database, IReadOnlyList<string> replicas)
    {
        ArgumentNullException.ThrowIfNull(replicas);
        var now = time.GetUtcNow();
        return [.. replicas.Select(region => _reports.TryGetValue(Key(database, region), out var entry)
            ? new ReplicaStatusBody(region, now - entry.At > StaleAfter ? ReplicaStates.Stale : entry.Report.State, entry.Report.LagMs, entry.Report.SyncedAt,
                entry.Report.VerifiedAt, entry.At)
            : Unknown(region))];
    }

    private static ReplicaStatusBody Unknown(string region) => new(region, ReplicaStates.Unknown, null, null, null);

    private static (string, string, string, string) Key(TenantContext database, string region) =>
        (database.OrganizationId, database.ProjectId!, database.DatabaseId!, region);
}
