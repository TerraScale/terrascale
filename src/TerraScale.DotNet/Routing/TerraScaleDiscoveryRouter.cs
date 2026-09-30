using System.Net;

namespace TerraScale.DotNet;

/// <summary>
/// SDK-driven routing discovery (ADR 0050 §5): one metadata GET per routing
/// scope against Atlas, then direct regional connections. A healthy pinned
/// connection is kept for the document's lifetime. Routing failures move the
/// pin to the next eligible region. Discovery failures use the Atlas API proxy.
/// </summary>
public sealed class TerraScaleDiscoveryRouter
{
    internal const string DefaultDiscoveryBaseUrl = "https://atlas.terrascale.tech";
    internal const string DefaultBranch = "main";
    private const int MaxPlacementRegions = 20;

    private readonly HttpClient httpClient;
    private readonly Uri discoveryOrigin;
    private readonly string? projectId;
    private readonly string? tenantId;
    private readonly string? preferredRegion;
    private readonly TimeSpan discoveryTimeout;
    private readonly TerraScaleRoutingCache routingCache;
    private readonly TerraScaleRegionRttTracker rtt = new();
    private readonly object pinnedGate = new();
    private readonly Dictionary<string, string> pinned = new(StringComparer.Ordinal);

    /// <summary>Creates a router over the caller-owned HTTP transport.</summary>
    public TerraScaleDiscoveryRouter(
        HttpClient httpClient,
        Uri discoveryOrigin,
        TimeSpan discoveryTimeout,
        string? preferredRegion = null,
        string? projectId = null,
        string? tenantId = null)
        : this(httpClient, discoveryOrigin, discoveryTimeout, preferredRegion, projectId, tenantId, null)
    {
    }

    internal TerraScaleDiscoveryRouter(
        HttpClient httpClient,
        Uri discoveryOrigin,
        TimeSpan discoveryTimeout,
        string? preferredRegion,
        string? projectId,
        string? tenantId,
        TerraScaleRoutingCache? routingCache)
    {
        this.httpClient = httpClient;
        this.discoveryOrigin = discoveryOrigin;
        this.discoveryTimeout = discoveryTimeout;
        this.preferredRegion = preferredRegion;
        this.projectId = projectId;
        this.tenantId = tenantId;
        this.routingCache = routingCache ?? new TerraScaleRoutingCache();
    }

    /// <summary>The Atlas metadata origin serving discovery and the region catalog.</summary>
    public Uri DiscoveryOrigin => discoveryOrigin;

    /// <summary>
    /// Records one measured client-to-region round trip; measured latency
    /// outranks geography in Atlas ranking (ADR 0050 §2).
    /// </summary>
    internal void RecordLatency(string region, double rttMs) => rtt.Observe(region, rttMs);

    internal TerraScaleRoutingScope ScopeFor(string database, string branch = DefaultBranch) =>
        new(discoveryOrigin.ToString(), database, branch, projectId, tenantId);

    /// <summary>
    /// Resolves the regional base for one database-scoped operation. Returns
    /// null when discovery cannot answer (cache miss plus failed or timed-out
    /// request): the caller uses the Atlas API proxy for that request.
    /// </summary>
    public async Task<TerraScaleRoutingBase?> ResolveBaseAsync(string database, bool needsWrite, CancellationToken cancellationToken)
    {
        if (!TerraScaleRoutingValidation.IsValidDatabaseName(database))
        {
            return null;
        }

        var scope = ScopeFor(database);
        var document = routingCache.Get(scope);
        if (document is null)
        {
            if (routingCache.FailureBackoffRemainingMs(scope, 1000) > 0)
            {
                return null;
            }

            var stale = routingCache.ConsumeStale(scope);
            // The coalesced request is bounded only by its own timeout: one
            // joining caller's cancellation must never fail (or arm the
            // backoff for) the shared single-flight discovery.
            var generation = routingCache.Generation(scope);
            try
            {
                document = await routingCache.CoalesceAsync(
                    scope,
                    () => FetchRoutingDocumentAsync(database, stale, cancellationToken)).ConfigureAwait(false);
                if (!ReferenceEquals(routingCache.Generation(scope), generation) &&
                    !ReferenceEquals(routingCache.Get(scope), document))
                {
                    // A fresher 421-learned placement or demotion landed
                    // mid-flight; the fetched document is older than what the
                    // scope already holds. Drop it — the stale-marked scope
                    // refreshes correctly on the next request.
                    return null;
                }

                routingCache.Set(scope, document);
                lock (pinnedGate)
                {
                    pinned.Remove(scope.Key);
                }
            }
            catch (TerraScaleDiscoveryException)
            {
                // Any discovery failure — including a worker 404 — degrades
                // the caller to the Atlas proxy; the member's response stays
                // authoritative for existence, not the discovery cache.
                routingCache.NoteFailure(scope);
                return null;
            }
            catch (HttpRequestException)
            {
                routingCache.NoteFailure(scope);
                return null;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The discovery timeout fired; arm the backoff and degrade.
                routingCache.NoteFailure(scope);
                return null;
            }
        }

        var endpoint = SelectEndpoint(scope, document, needsWrite);
        return endpoint is null ? null : new TerraScaleRoutingBase(endpoint.Url, endpoint.Region);
    }

    /// <summary>A member 421 or placement header teaches placement without a round trip.</summary>
    public void NotePlacement(string database, IReadOnlyList<string> regions)
    {
        var scope = ScopeFor(database);
        var unique = regions
            .Where(TerraScaleRegionCodes.IsKnown)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxPlacementRegions)
            .ToList();
        if (unique.Count == 0)
        {
            InvalidateScope(scope);
            return;
        }

        // The synthesized document is provisional: the home region is first
        // and fully capable until discovery says otherwise; the next refresh
        // is corrective.
        var document = new TerraScaleRoutingDocument(
            database,
            DefaultBranch,
            Revision: 0,
            Residency: null,
            ResidencyRegions: null,
            PlacementKnown: true,
            RefreshSeconds: TerraScaleRoutingValidation.MinRefreshSeconds,
            Endpoints: unique.Select((region, index) => new TerraScaleRoutingEndpoint(
                region,
                new Uri($"https://{TerraScaleRoutingValidation.RegionalApiHostname(region)}/", UriKind.Absolute),
                index + 1,
                new[] { TerraScaleRoutingCapability.Read, TerraScaleRoutingCapability.Write, TerraScaleRoutingCapability.Sql, TerraScaleRoutingCapability.Reactive }))
                .ToList());
        routingCache.Set(scope, document);
        lock (pinnedGate)
        {
            pinned.Remove(scope.Key);
        }

        routingCache.MarkStale(scope);
    }

    /// <summary>A rejected route: drop the cache and mark the scope for a corrective lookup.</summary>
    public void ReportRejectedRoute(string database)
    {
        var scope = ScopeFor(database);
        InvalidateScope(scope);
    }

    /// <summary>A regional transport failure demotes that region behind the alternatives.</summary>
    public void ReportRegionFailure(string database, string region)
    {
        if (TerraScaleRegionCodes.IsKnown(region))
        {
            routingCache.DemoteRegion(ScopeFor(database), region);
        }
    }

    internal bool HasAlternative(string database, string region, bool needsWrite)
    {
        var required = needsWrite ? TerraScaleRoutingCapability.Write : TerraScaleRoutingCapability.Read;
        return routingCache.Get(ScopeFor(database))?.Endpoints.Any(endpoint =>
            endpoint.Region != region && endpoint.Capabilities.Contains(required)) == true;
    }

    private void InvalidateScope(TerraScaleRoutingScope scope)
    {
        routingCache.Invalidate(scope);
        lock (pinnedGate)
        {
            pinned.Remove(scope.Key);
        }
    }

    /// <summary>
    /// Selects the endpoint for one operation: the scope's remembered region
    /// while it is still in the document and capability-eligible, otherwise
    /// the configured preference or the document's first eligible endpoint.
    /// Only a selection that actually moves rewrites the remembered region.
    /// </summary>
    private TerraScaleRoutingEndpoint? SelectEndpoint(TerraScaleRoutingScope scope, TerraScaleRoutingDocument document, bool needsWrite)
    {
        var required = needsWrite ? TerraScaleRoutingCapability.Write : TerraScaleRoutingCapability.Read;
        var eligible = document.Endpoints
            .Where(endpoint => endpoint.Capabilities.Contains(required))
            .ToList();
        if (eligible.Count == 0)
        {
            return null;
        }

        string? remembered;
        lock (pinnedGate)
        {
            remembered = pinned.GetValueOrDefault(scope.Key);
        }

        if (remembered is not null)
        {
            var kept = eligible.FirstOrDefault(endpoint => endpoint.Region == remembered);
            if (kept is not null)
            {
                return kept;
            }
        }

        var chosen = preferredRegion is null
            ? eligible[0]
            : eligible.FirstOrDefault(endpoint => endpoint.Region == preferredRegion) ?? eligible[0];
        lock (pinnedGate)
        {
            pinned[scope.Key] = chosen.Region;
        }

        return chosen;
    }

    private async Task<TerraScaleRoutingDocument> FetchRoutingDocumentAsync(string database, bool stale, CancellationToken cancellationToken)
    {
        var scope = ScopeFor(database);
        var revision = routingCache.LastRevision(scope);
        var target = new UriBuilder(new Uri(discoveryOrigin, "/api/v1/discovery"))
        {
            Query = $"database={Uri.EscapeDataString(database)}&branch={Uri.EscapeDataString(DefaultBranch)}" +
                (revision > 0 ? $"&rev={revision}" : string.Empty) +
                (stale ? "&stale=1" : string.Empty)
        }.Uri;
        var payload = await GetMetadataAsync(target, "discovery", cancellationToken, rtt.MeasuredRttHeader()).ConfigureAwait(false);
        var loopbackClient = TerraScaleRoutingValidation.IsDevOrigin(discoveryOrigin);
        var document = TerraScaleRoutingValidation.TryParseRoutingDocument(payload, database, DefaultBranch, loopbackClient);
        return document ?? throw new TerraScaleDiscoveryException("invalid_document", "Atlas returned a malformed routing document.");
    }

    /// <summary>One bounded, unauthenticated metadata GET; never cacheable, the SDK routing cache owns caching.</summary>
    private async Task<string> GetMetadataAsync(Uri target, string name, CancellationToken cancellationToken, string? regionRttHeader = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(discoveryTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, target);
        request.Headers.Accept.ParseAdd("application/json");
        if (regionRttHeader is not null)
        {
            request.Headers.TryAddWithoutValidation("X-TerraScale-Region-Rtt", regionRttHeader);
        }

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new TerraScaleDiscoveryException("not_found", $"Database is unknown to Atlas {name}.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new TerraScaleDiscoveryException("unavailable", $"Atlas {name} answered HTTP {(int)response.StatusCode}.");
        }

        var payload = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        if (payload.Length > 1_048_576)
        {
            throw new TerraScaleDiscoveryException("invalid_document", $"Atlas {name} response exceeds the size bound.");
        }

        return payload;
    }
}

/// <summary>A bounded Atlas metadata failure. Routing degrades; it never widens.</summary>
public sealed class TerraScaleDiscoveryException(string code, string message) : Exception(message)
{
    /// <summary>The failure class: <c>not_found</c>, <c>unavailable</c>, or <c>invalid_document</c>.</summary>
    public string Code { get; } = code;
}
