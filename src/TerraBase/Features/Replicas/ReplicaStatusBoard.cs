using System.Collections.Concurrent;
using System.Net.Http.Json;
using TerraBase.Configuration;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Features.Replicas;

internal interface IReplicaStatus
{
    ValueTask<bool> RecordAsync(TenantContext database, ReplicaStatusReport report, CancellationToken cancellationToken);

    IReadOnlyList<ReplicaStatusBody> Local(TenantContext database, IReadOnlyList<string> replicas);

    ValueTask<IReadOnlyList<ReplicaStatusBody>> ReadAsync(TenantContext database, string home, IReadOnlyList<string> replicas, CancellationToken cancellationToken);
}

internal sealed class ReplicaStatusBoard(
    TerraBaseOptions options, IReplicaPeers peers, IReplicaCatalog catalog, InternalCredential credential, TimeProvider time) : IReplicaStatus, IDisposable
{
    internal static readonly TimeSpan StaleAfter = RegionReplication.Sweep * 3;
    private static readonly TimeSpan RemoteTimeout = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<(string Organization, string Project, string Database, string Region), (ReplicaStatusReport Report, DateTimeOffset At)> _reports = new();
    private readonly Lazy<HttpClient> _http = new(() => PeerHttp.Create(credential));

    public async ValueTask<bool> RecordAsync(TenantContext database, ReplicaStatusReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!ReplicaStates.IsReported(report.State)
            || await catalog.FindAsync(database, cancellationToken).ConfigureAwait(false) is not TenantDatabaseRecord { Placement: var placement }
            || placement.Home != options.Region || !placement.Replicas.Contains(report.Region))
        {
            return false;
        }

        _reports[Key(database, report.Region)] = (report, time.GetUtcNow());
        foreach (var stale in _reports.Where(entry => entry.Key.Database == database.DatabaseId && !placement.Replicas.Contains(entry.Key.Region)).Select(entry => entry.Key).ToList())
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

    public async ValueTask<IReadOnlyList<ReplicaStatusBody>> ReadAsync(TenantContext database, string home, IReadOnlyList<string> replicas, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(replicas);
        if (home == options.Region)
        {
            return Local(database, replicas);
        }

        var peer = (await peers.ListAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(candidate => candidate.Region == home);
        if (peer is not null)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RemoteTimeout);
            try
            {
                var address = new Uri(peer.Internal, $"/v1/sync/organizations/{database.OrganizationId}/projects/{database.ProjectId}/databases/{database.DatabaseId}/replica-status");
                if (await _http.Value.GetFromJsonAsync(address, TerraBaseJsonContext.Default.IReadOnlyListReplicaStatusBody, timeout.Token).ConfigureAwait(false) is { } remote)
                {
                    return remote;
                }
            }
            catch (Exception error) when (error is HttpRequestException or System.Text.Json.JsonException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
            }
        }

        return [.. replicas.Select(Unknown)];
    }

    public void Dispose()
    {
        if (_http.IsValueCreated)
        {
            _http.Value.Dispose();
        }
    }

    private static ReplicaStatusBody Unknown(string region) => new(region, ReplicaStates.Unknown, null, null, null);

    private static (string, string, string, string) Key(TenantContext database, string region) =>
        (database.OrganizationId, database.ProjectId!, database.DatabaseId!, region);
}
