using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Features.Replicas;

internal sealed class ReplicaNameLookups(CancellationToken cancellationToken) : IDisposable
{
    private CancellationTokenSource? _timeout;

    public CancellationToken Token
    {
        get
        {
            if (_timeout is null)
            {
                _timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _timeout.CancelAfter(TimeSpan.FromSeconds(3));
            }

            return _timeout.Token;
        }
    }

    public void Dispose() => _timeout?.Dispose();
}

internal sealed class ReplicaNames(ITenantDirectory tenants, IReplicaPeers peers, InternalCredential credential) : IDisposable
{
    private readonly HttpClient _http = PeerHttp.Create(credential);

    public async Task<bool> ApplyAsync(ReplicaCatalogEntry entry,
        IReadOnlyDictionary<(string, string, string), ReplicaCatalogEntry>? listed, ReplicaNameLookups lookup, CancellationToken cancellationToken)
    {
        if (DatabaseIdentity.Reconcile(entry.Identity) is null)
        {
            return false;
        }

        var database = new TenantContext(entry.OrganizationId, PlanTier.Hobby, entry.ProjectId, entry.DatabaseId);
        return await tenants.ReconcileDatabaseNameAsync(database, entry.Name, entry.HomeRegion, entry.PlacementVersion, async (holder, token) =>
        {
            ReplicaCatalogEntry? found;
            if (listed?.TryGetValue((holder.OrganizationId, holder.ProjectId!, holder.DatabaseId!), out found) != true)
            {
                if (lookup.Token.IsCancellationRequested)
                {
                    return null;
                }

                try
                {
                    var home = (await peers.ListAsync(lookup.Token).ConfigureAwait(false)).FirstOrDefault(peer => peer.Region == entry.HomeRegion);
                    if (home is null)
                    {
                        return null;
                    }

                    using var response = await _http.GetAsync(new Uri(home.Internal, ReplicaRoutes.DatabasePath(holder, "placement")), lookup.Token).ConfigureAwait(false);
                    found = response.IsSuccessStatusCode
                        ? await response.Content.ReadFromJsonAsync(ReplicasJsonContext.Default.ReplicaCatalogEntry, lookup.Token).ConfigureAwait(false)
                        : null;
                }
                catch (Exception error) when (error is HttpRequestException or JsonException or TerraBaseConfigurationException or OperationCanceledException && !token.IsCancellationRequested)
                {
                    return null;
                }
            }

            return found is not null && found.OrganizationId == holder.OrganizationId && found.ProjectId == holder.ProjectId
                && found.DatabaseId == holder.DatabaseId && found.HomeRegion == entry.HomeRegion && DatabaseIdentity.Reconcile(found.Identity) is not null
                    ? found.Name : null;
        }, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _http.Dispose();
}
