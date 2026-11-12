using NATS.Client.Core;
using System.Net.Http.Headers;
using System.Net;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Management;
using TerraBase.Features.Replicas;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Replicas;

internal sealed partial class RegionReplication
{
    internal async Task SyncCatalogsAsync(CancellationToken cancellationToken)
    {
        try
        {
            _peers = await peers.ListAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is TerraBaseConfigurationException)
        {
            logger.ZLogWarning($"The peers file could not be read; replicas keep the regions they know: {error.GetType().Name:@Error}");
        }

        if (owners.Writer)
        {
            foreach (var (key, follower) in _followers.Where(entry => entry.Value.Home.Region == options.Region).ToArray())
            {
                await DeleteShreddedAsync(follower.Database, cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var peer in _peers)
        {
            try
            {
                await SyncHomeAsync(peer, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.ZLogWarning($"Region {peer.Region}'s replicated catalog could not be read; its replicas keep serving: {error.GetType().Name:@Error}");
            }
        }
    }


    private async Task CatalogAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await SyncCatalogsAsync(cancellationToken).ConfigureAwait(false);
            foreach (var follower in _followers.Values)
            {
                follower.SweepIfIdle();
            }

            using var sweep = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            sweep.CancelAfter(Sweep);
            try
            {
                _ = await _catalogWakes.Reader.ReadAsync(sweep.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }
    }


    private readonly Counter<long> _catalogErrors = meters.Create(Telemetry.MeterName).CreateCounter<long>("terrabase.replica.catalog_errors");
    private readonly ConcurrentDictionary<string, DateTimeOffset> _catalogLogged = new(StringComparer.Ordinal);

    private void CatalogFailed(string region, Exception? error)
    {
        _catalogErrors.Add(1);
        var now = time.GetUtcNow();
        if (_catalogLogged.TryGetValue(region, out var last) && now - last < TimeSpan.FromMinutes(10))
        {
            return;
        }

        _catalogLogged[region] = now;
        logger.ZLogWarning(error, $"An entry from region {region:@Region}'s catalog failed; other entries and replica reconciliation continue");
    }

    private async Task SyncHomeAsync(ReplicaPeer home, CancellationToken cancellationToken)
    {
        var listed = await ListAsync(home, cancellationToken).ConfigureAwait(false);
        using var lookups = new ReplicaNameLookups(cancellationToken);
        if (listed is not null)
        {
            foreach (var entry in listed.Values)
            {
                try
                {
                    await RecordAsync(entry, listed, lookups, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    CatalogFailed(entry.HomeRegion, error);
                }
            }
        }

        (string Project, string Database)? resumed = _refreshAfter.TryGetValue(home.Region, out var cursor) ? cursor : null;
        lock (_gate)
        {
            var now = time.GetUtcNow();
            foreach (var key in _refreshed.Where(entry => entry.Value <= now).Select(entry => entry.Key).ToArray())
            {
                _refreshed.Remove(key);
            }
        }

        var refreshes = new List<TenantContext>();
        var deferred = false;
        TenantContext? after = null;
        do
        {
            if (await catalog.ListHomedAsync(home.Region, after, cancellationToken).ConfigureAwait(false) is not TenantDatabasePage page)
            {
                return;
            }

            foreach (var database in page.Databases)
            {
                var key = (Organization: database.OrganizationId, Project: database.ProjectId!, Database: database.DatabaseId!);
                var held = await catalog.FindAsync(database, cancellationToken).ConfigureAwait(false) is TenantDatabaseRecord { Placement: var placement }
                    && placement.Home == home.Region ? placement : null;
                var replicated = held is not null && (home.Region == options.Region || held.Includes(options.Region));
                try
                {
                    if (replicated && (listed is null || listed.ContainsKey(key)))
                    {
                        await FollowAsync(key, database, home, cancellationToken).ConfigureAwait(false);
                    }
                    else if (replicated)
                    {
                        await ResolveMissingAsync(home, database, lookups, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await DeleteShreddedAsync(database, cancellationToken).ConfigureAwait(false);
                        if (held is not null && listed is not null
                            && (resumed is not { } from || Follows(key.Project, key.Database, from)) && RefreshDue(key))
                        {
                            if (refreshes.Count == MaxRefreshes)
                            {
                                deferred = true;
                            }
                            else
                            {
                                refreshes.Add(database);
                            }
                        }
                    }
                }
                catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    logger.ZLogWarning($"A replica could not be reconciled with its home; the next sweep retries: {error.GetType().Name:@Error}");
                }
            }

            after = page.Next;
        }
        while (after is not null);

        if (refreshes.Count == 0)
        {
            _ = _refreshAfter.TryRemove(home.Region, out _);
            return;
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(PlacementTimeout);
        using var refreshLookups = new ReplicaNameLookups(budget.Token);
        var reached = resumed;
        foreach (var database in refreshes)
        {
            if (budget.IsCancellationRequested)
            {
                deferred = true;
                break;
            }

            lock (_gate)
            {
                if (_refreshed.ContainsKey(Key(database)))
                {
                    reached = (database.ProjectId!, database.DatabaseId!);
                    continue;
                }
                if (_refreshed.Count == MaxRecentRefreshes)
                {
                    deferred = true;
                    break;
                }
                _refreshed[Key(database)] = time.GetUtcNow() + VerifyEvery;
            }

            reached = (database.ProjectId!, database.DatabaseId!);
            try
            {
                await ResolveMissingAsync(home, database, refreshLookups, budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && budget.IsCancellationRequested)
            {
                deferred = true;
                break;
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                faults.Failed(database, home.Region, error);
            }
        }

        if (deferred && reached is { } next)
        {
            _refreshAfter[home.Region] = next;
        }
        else
        {
            _ = _refreshAfter.TryRemove(home.Region, out _);
        }
    }

    private bool RefreshDue((string, string, string) key)
    {
        lock (_gate)
        {
            return !_refreshed.ContainsKey(key);
        }
    }

    private static bool Follows(string project, string database, (string Project, string Database) cursor) =>
        string.CompareOrdinal(project, cursor.Project) is var order && (order > 0 || order == 0 && string.CompareOrdinal(database, cursor.Database) > 0);

    private async Task<PeerPlacement> PlacementAtAsync(ReplicaPeer peer, TenantContext database, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PlacementTimeout);
        try
        {
            var observed = owners.Find(peer.Region);
            var writer = observed is { Activated: true } && observed.Node == peer.Name;
            if (options.NatsUrl is null)
            {
                var nodes = options.PeersFile is null ? [] : await new PeersFile(options).ListAsync(timeout.Token).ConfigureAwait(false);
                writer = nodes.Count(node => node.Region == peer.Region) == 1 && nodes.Any(node => node.Region == peer.Region && node.Name == peer.Name);
            }

            using var response = await _http.GetAsync(new Uri(peer.Internal, ReplicaRoutes.DatabasePath(database, "placement")), timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return writer && owners.Find(peer.Region) == observed ? new NotFound() : PlacementUnanswered;
            }

            return response.IsSuccessStatusCode
                && await response.Content.ReadFromJsonAsync(ReplicasJsonContext.Default.ReplicaCatalogEntry, timeout.Token).ConfigureAwait(false) is { } entry
                ? entry
                : PlacementUnanswered;
        }
        catch (Exception error) when (error is HttpRequestException or JsonException || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return PlacementUnanswered;
        }
    }

    private readonly union PeerPlacement(ReplicaCatalogEntry, NotFound, Unavailable);


    internal async Task DeleteShreddedAsync(TenantContext database, CancellationToken cancellationToken)
    {
        var key = Key(database);
        var gate = Lifecycle(key);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopFollowingAsync(key, database, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }


    private void WakeCatalogs()
    {
        foreach (var peer in _peers)
        {
            _ = _catalogWakes.Writer.TryWrite(peer.Region);
        }
    }

    private static (string, string, string) Key(TenantContext database) => (database.OrganizationId, database.ProjectId!, database.DatabaseId!);


    private async Task<Dictionary<(string, string, string), ReplicaCatalogEntry>?> ListAsync(ReplicaPeer home, CancellationToken cancellationToken)
    {
        var listed = new Dictionary<(string, string, string), ReplicaCatalogEntry>();
        string? after = null;
        try
        {
            do
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(PlacementTimeout);
                var address = new Uri(home.Internal, $"/v1/sync/regions/{options.Region}/databases" + (after is null ? "" : $"?after={Uri.EscapeDataString(after)}"));
                var page = await _http.GetFromJsonAsync(address, ReplicasJsonContext.Default.ReplicaCatalogPage, timeout.Token).ConfigureAwait(false);
                if (page is null)
                {
                    logger.ZLogWarning($"Region {home.Region}'s home returned no catalog page; its replicas keep serving what they hold");
                    return null;
                }

                foreach (var entry in page.Databases.Where(entry => entry.HomeRegion == home.Region && (home.Region == options.Region || entry.AllRegions || entry.ReplicaRegions.Contains(options.Region))))
                {
                    listed[(entry.OrganizationId, entry.ProjectId, entry.DatabaseId)] = entry;
                }

                after = page.Next;
            }
            while (after is not null);
        }
        catch (Exception error) when (error is HttpRequestException or JsonException || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.ZLogWarning($"Region {home.Region}'s home is unreachable; its replicas keep serving what they hold: {error.GetType().Name:@Error}");
            return null;
        }

        return listed;
    }

    private async Task RecordAsync(ReplicaCatalogEntry entry, IReadOnlyDictionary<(string, string, string), ReplicaCatalogEntry>? listed, ReplicaNameLookups lookups, CancellationToken cancellationToken)
    {
        var database = new TenantContext(entry.OrganizationId, PlanTier.Hobby, entry.ProjectId, entry.DatabaseId);
        var gate = Lifecycle(Key(database));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await catalog.FindAsync(database, cancellationToken).ConfigureAwait(false) is TenantDatabaseRecord { Placement: var held }
                && held.Version < entry.PlacementVersion && !held.Includes(options.Region)
                && (entry.AllRegions || entry.HomeRegion == options.Region || entry.ReplicaRegions.Contains(options.Region)))
            {
                await StopFollowingAsync(Key(database), database, cancellationToken).ConfigureAwait(false);
            }

            if (!await catalog.RecordAsync(entry, cancellationToken, listed, lookups).ConfigureAwait(false))
            {
                CatalogFailed(entry.HomeRegion, null);
                faults.Rejected(database, entry.HomeRegion);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task ResolveMissingAsync(ReplicaPeer home, TenantContext database, ReplicaNameLookups lookups, CancellationToken cancellationToken)
    {
        switch (await PlacementAtAsync(home, database, cancellationToken).ConfigureAwait(false))
        {
            case NotFound:
                var gate = Lifecycle(Key(database));
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await StopFollowingAsync(Key(database), database, cancellationToken).ConfigureAwait(false);
                    if (await catalog.ForgetAsync(database, cancellationToken).ConfigureAwait(false) is Unavailable error)
                    {
                        faults.Failed(database, home.Region, new InvalidOperationException(error.Message));
                    }
                }
                finally
                {
                    gate.Release();
                }
                break;
            case ReplicaCatalogEntry entry:
                if (entry.HomeRegion == home.Region)
                {
                    await RecordAsync(entry, null, lookups, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await catalog.UpdatePlacementAsync(database, new DatabasePlacement(entry.HomeRegion, entry.ReplicaRegions, entry.PlacementVersion, entry.AllRegions), cancellationToken).ConfigureAwait(false);
                }
                if (entry.HomeRegion != options.Region && !entry.AllRegions && !entry.ReplicaRegions.Contains(options.Region))
                {
                    await DeleteShreddedAsync(database, cancellationToken).ConfigureAwait(false);
                }
                break;
            case Unavailable:
                faults.Unanswered(database, home.Region);
                break;
        }
    }

    private async Task StopFollowingAsync((string, string, string) key, TenantContext database, CancellationToken cancellationToken)
    {
        Follower? follower;
        lock (_gate)
        {
            _ = _followers.TryRemove(key, out follower);
        }

        if (follower is not null)
        {
            await follower.StopAsync().ConfigureAwait(false);
        }

        _ = await replicas.DeleteAsync(database, cancellationToken).ConfigureAwait(false);
    }

}
