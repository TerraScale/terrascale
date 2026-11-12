using NATS.Client.Core;
using System.Net.Http.Headers;
using System.Net;
using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Engine;
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
                await StopFollowingAsync(key, follower.Database, cancellationToken).ConfigureAwait(false);
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


    private async Task SyncHomeAsync(ReplicaPeer home, CancellationToken cancellationToken)
    {
        var listed = await ListAsync(home, cancellationToken).ConfigureAwait(false);
        if (listed is not null)
        {
            foreach (var entry in listed.Values)
            {
                await RecordAsync(entry, cancellationToken).ConfigureAwait(false);
            }
        }

        TenantContext? after = null;
        do
        {
            if (await catalog.ListHomedAsync(home.Region, after, cancellationToken).ConfigureAwait(false) is not TenantDatabasePage page)
            {
                return;
            }

            foreach (var database in page.Databases)
            {
                var key = (database.OrganizationId, database.ProjectId!, database.DatabaseId!);
                var replicated = await catalog.FindAsync(database, cancellationToken).ConfigureAwait(false) is TenantDatabaseRecord { Placement: var placement }
                    && placement.Home == home.Region && (home.Region == options.Region || placement.Includes(options.Region));
                try
                {
                    if (replicated && (listed is null || listed.ContainsKey(key)))
                    {
                        await FollowAsync(key, database, home, cancellationToken).ConfigureAwait(false);
                    }
                    else if (replicated)
                    {
                        await ResolveMissingAsync(home, database, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await StopFollowingAsync(key, database, cancellationToken).ConfigureAwait(false);
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
    }


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
                var address = new Uri(home.Internal, $"/v1/sync/regions/{options.Region}/databases" + (after is null ? "" : $"?after={Uri.EscapeDataString(after)}"));
                var page = await _http.GetFromJsonAsync(address, ReplicasJsonContext.Default.ReplicaCatalogPage, cancellationToken).ConfigureAwait(false);
                if (page is null)
                {
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
        catch (Exception error) when (error is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.ZLogInformation($"Region {home.Region}'s home is unreachable; its replicas keep serving what they hold: {error.GetType().Name:@Error}");
            return null;
        }

        return listed;
    }

    private async Task RecordAsync(ReplicaCatalogEntry entry, CancellationToken cancellationToken)
    {
        if (!await catalog.RecordAsync(entry, cancellationToken).ConfigureAwait(false))
        {
            logger.ZLogWarning($"A replicated database could not be recorded here (its project belongs to another organization, its name is taken or it was deleted); it is not replicated");
        }
    }

    private async Task ResolveMissingAsync(ReplicaPeer home, TenantContext database, CancellationToken cancellationToken)
    {
        var key = (database.OrganizationId, database.ProjectId!, database.DatabaseId!);
        var address = new Uri(home.Internal, ReplicaRoutes.DatabasePath(database, "placement"));
        using var response = await _http.GetAsync(address, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            await StopFollowingAsync(key, database, cancellationToken).ConfigureAwait(false);
            _ = await catalog.ForgetAsync(database, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!response.IsSuccessStatusCode
            || await response.Content.ReadFromJsonAsync(ReplicasJsonContext.Default.ReplicaCatalogEntry, cancellationToken).ConfigureAwait(false) is not { } entry)
        {
            return;
        }

        _ = await catalog.RecordAsync(entry, cancellationToken).ConfigureAwait(false);
        if (!entry.AllRegions && !entry.ReplicaRegions.Contains(options.Region))
        {
            await StopFollowingAsync(key, database, cancellationToken).ConfigureAwait(false);
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
