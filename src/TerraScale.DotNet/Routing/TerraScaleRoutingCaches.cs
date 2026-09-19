using System.Collections.Concurrent;

namespace TerraScale.DotNet;

/// <summary>
/// Per-scope routing cache with single-flight discovery, failure backoff,
/// region demotion, and corrective-stale marking (ADR 0050 §5). Entries
/// outlive their refresh policy so discovery never runs before every query.
/// </summary>
internal sealed class TerraScaleRoutingCache
{
    private sealed record Entry(TerraScaleRoutingDocument Document, long ExpiresAtMs);

    private static readonly Func<long> DefaultClock = static () => Environment.TickCount64;

    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<TerraScaleRoutingDocument>>> inflight = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> lastFailureAtMs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, HashSet<string>> demoted = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> stale = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, object> generations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> lastRevision = new(StringComparer.Ordinal);

    internal Func<long> NowMs { get; set; } = DefaultClock;

    /// <summary>
    /// The scope's current generation token. A coalesced discovery result may
    /// only be applied while the token it started under is still current:
    /// <see cref="Set"/>, <see cref="Invalidate"/>, or <see cref="DemoteRegion"/>
    /// in between means fresher authoritative routing landed mid-flight.
    /// </summary>
    internal object Generation(TerraScaleRoutingScope scope) => generations.GetOrAdd(scope.Key, _ => new object());

    private void Bump(string key) => generations[key] = new object();

    internal TerraScaleRoutingDocument? Get(TerraScaleRoutingScope scope)
    {
        if (!entries.TryGetValue(scope.Key, out var entry))
        {
            return null;
        }

        if (entry.ExpiresAtMs <= NowMs())
        {
            entries.TryRemove(scope.Key, out _);
            return null;
        }

        if (!demoted.TryGetValue(scope.Key, out var demotedRegions) || demotedRegions.Count == 0)
        {
            return entry.Document;
        }

        var endpoints = entry.Document.Endpoints.Where(endpoint => !demotedRegions.Contains(endpoint.Region)).ToList();
        if (endpoints.Count == 0)
        {
            return entry.Document;
        }

        return entry.Document with
        {
            Endpoints = endpoints.Select((endpoint, index) => endpoint with { Priority = index + 1 }).ToList()
        };
    }

    internal void Set(TerraScaleRoutingScope scope, TerraScaleRoutingDocument document)
    {
        entries[scope.Key] = new Entry(document, NowMs() + document.RefreshSeconds * 1000L);
        lastRevision[scope.Key] = document.Revision;
        demoted.TryRemove(scope.Key, out _);
        Bump(scope.Key);
    }

    /// <summary>The last observed routing revision for the scope, 0 when never seen; sent as `rev` on refreshes (ADR 0050 §2).</summary>
    internal long LastRevision(TerraScaleRoutingScope scope) =>
        lastRevision.TryGetValue(scope.Key, out var revision) ? revision : 0;

    /// <summary>Invalidates after a rejected route; the next lookup rediscovers with <c>stale=1</c>.</summary>
    internal void Invalidate(TerraScaleRoutingScope scope)
    {
        entries.TryRemove(scope.Key, out _);
        demoted.TryRemove(scope.Key, out _);
        stale[scope.Key] = 1;
        Bump(scope.Key);
    }

    /// <summary>Marks the scope for one corrective lookup; consumed by the next fetch.</summary>
    internal void MarkStale(TerraScaleRoutingScope scope) => stale[scope.Key] = 1;

    internal bool ConsumeStale(TerraScaleRoutingScope scope) => stale.TryRemove(scope.Key, out _);

    /// <summary>Records a routing failure against one region; it moves behind the alternatives.</summary>
    internal void DemoteRegion(TerraScaleRoutingScope scope, string region)
    {
        var regions = demoted.GetOrAdd(scope.Key, _ => new HashSet<string>(StringComparer.Ordinal));
        lock (regions)
        {
            regions.Add(region);
        }

        Bump(scope.Key);
    }

    internal void NoteFailure(TerraScaleRoutingScope scope) => lastFailureAtMs[scope.Key] = NowMs();

    /// <summary>Discovery backoff: a scope that just failed waits before trying again.</summary>
    internal long FailureBackoffRemainingMs(TerraScaleRoutingScope scope, long backoffMs)
    {
        return lastFailureAtMs.TryGetValue(scope.Key, out var failedAt)
            ? Math.Max(0, failedAt + backoffMs - NowMs())
            : 0;
    }

    /// <summary>Single-flight: concurrent discovery for one scope shares one request.</summary>
    internal async Task<TerraScaleRoutingDocument> CoalesceAsync(
        TerraScaleRoutingScope scope,
        Func<Task<TerraScaleRoutingDocument>> initiate)
    {
        var lazy = inflight.GetOrAdd(scope.Key, _ => new Lazy<Task<TerraScaleRoutingDocument>>(initiate));
        try
        {
            return await lazy.Value.ConfigureAwait(false);
        }
        finally
        {
            inflight.TryRemove(new KeyValuePair<string, Lazy<Task<TerraScaleRoutingDocument>>>(scope.Key, lazy));
        }
    }
}

/// <summary>
/// The region-catalog cache (ADR 0050 §5.8): five-minute TTL per discovery
/// origin, single-flight, refreshed on failure (a failed fetch caches
/// nothing, so the next request retries immediately).
/// </summary>
internal sealed class TerraScaleRegionCatalogCache
{
    internal const int CacheSeconds = 300;

    private sealed record Entry(TerraScaleRegionCatalog Catalog, long ExpiresAtMs);

    private static readonly Func<long> DefaultClock = static () => Environment.TickCount64;

    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<TerraScaleRegionCatalog>>> inflight = new(StringComparer.Ordinal);

    internal Func<long> NowMs { get; set; } = DefaultClock;

    internal TerraScaleRegionCatalog? Get(string origin)
    {
        if (!entries.TryGetValue(origin, out var entry))
        {
            return null;
        }

        if (entry.ExpiresAtMs <= NowMs())
        {
            entries.TryRemove(origin, out _);
            return null;
        }

        return entry.Catalog;
    }

    internal void Set(string origin, TerraScaleRegionCatalog catalog) =>
        entries[origin] = new Entry(catalog, NowMs() + CacheSeconds * 1000L);

    /// <summary>Single-flight: concurrent catalog fetches for one origin share one request.</summary>
    internal async Task<TerraScaleRegionCatalog> CoalesceAsync(string origin, Func<Task<TerraScaleRegionCatalog>> initiate)
    {
        var lazy = inflight.GetOrAdd(origin, _ => new Lazy<Task<TerraScaleRegionCatalog>>(initiate));
        try
        {
            return await lazy.Value.ConfigureAwait(false);
        }
        finally
        {
            inflight.TryRemove(new KeyValuePair<string, Lazy<Task<TerraScaleRegionCatalog>>>(origin, lazy));
        }
    }
}
