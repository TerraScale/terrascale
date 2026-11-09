using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Diagnostics.Metrics;
using System.Net;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Errors;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Replicas;

public sealed record HomePromoteRequest(string MoveId, string FromRegion, long Version, IReadOnlyList<string> ReplicaRegions, bool AllRegions);

public sealed record HomeAbortRequest(string MoveId, long Version);

public sealed record HomePromoteAnswer(bool Promoted);

internal readonly union MoveHomeResult(ManagedDatabase, NotFound, Validation, Conflict, Unavailable);

internal readonly union PromoteResult(HomePromoteAnswer, Conflict, Unavailable);

internal sealed class HomeMoves(
    TerraBaseOptions options, IHomeMoveCatalog moves, IReplicaCatalog catalog, IReplicaPeers peers, ITenantDirectory tenants, IDatabaseReplicas replicas, RegionReplication replication,
    IStorageEngine engine, IDatabaseRevisions revisions, IWalBackup imports, DatabaseFences fences, Regions.IRegionCatalog regions, HomeApply.IHomeApply apply, HomeLeases leases, HomeLeaseGate leaseGate, HomePush.HomePush pushes,
    InternalCredential credential, IMeterFactory meters, TimeProvider time, ILogger<HomeMoves> logger)
    : BackgroundService
{
    internal static readonly Unavailable ReplicaNotReady = new("the target region's replica could not catch up; nothing changed, retry", "replica_not_ready");
    internal static readonly Unavailable Unresolved = new("the move is being settled with the target region; writes stay paused until it is, retry", "move_unresolved");

    private static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SettleEvery = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http = PeerHttp.Create(credential);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _aborted = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _running = new(StringComparer.Ordinal);
    private readonly Meter _meter = meters.Create(Telemetry.MeterName);
    private Histogram<double>? _pause;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _pause = _meter.CreateHistogram<double>("terrabase.placement.move.pause", "ms", "Time writes of a database paused while its home moved.");
        leases.FailOver = FailOverAsync;
        return base.StartAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _http.Dispose();
        _meter.Dispose();
        foreach (var gate in _locks.Values)
        {
            gate.Dispose();
        }

        base.Dispose();
    }

    public async Task<MoveHomeResult> MoveAsync(TenantContext database, string target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        var started = Stopwatch.GetTimestamp();
        if (!_running.TryAdd(database.DatabaseId!, true))
        {
            return EngineManagementService.MoveInProgress;
        }

        try
        {
            return await moves.BeginHomeMoveAsync(database, target, cancellationToken).ConfigureAwait(false) switch
            {
                HomeMoveStart begun => await MoveAsync(begun.Marker, started, cancellationToken).ConfigureAwait(false),
                ManagedDatabase unchanged => unchanged,
                NotFound error => error,
                Validation error => error,
                Conflict error => error,
                Unavailable error => error,
            };
        }
        finally
        {
            _ = _running.TryRemove(database.DatabaseId!, out _);
        }
    }

    private async Task<MoveHomeResult> MoveAsync(HomeMoveMarker marker, long started, CancellationToken cancellationToken)
    {
        var database = marker.Database;
        var peer = await PeerAsync(marker.Target, cancellationToken).ConfigureAwait(false);
        var caughtUpAnswer = peer is null ? null : await PostAsync(peer, marker, "catch-up", null, CancellationToken.None).ConfigureAwait(false);
        using (caughtUpAnswer)
        {
            if (peer is null || caughtUpAnswer is not { IsSuccessStatusCode: true })
            {
                logger.ZLogWarning($"A home move to {marker.Target:@Target} stopped: its replica did not catch up ({(caughtUpAnswer is null ? "no answer" : ((int)caughtUpAnswer.StatusCode).ToString(CultureInfo.InvariantCulture)):@Answer})");
                await moves.AbortHomeMoveAsync(marker, CancellationToken.None).ConfigureAwait(false);
                return ReplicaNotReady;
            }
        }

        var caughtUp = Stopwatch.GetElapsedTime(started);
        var paused = Stopwatch.GetTimestamp();
        fences.Pause(marker.DatabaseId);
        bool? promoted;
        try
        {
            if (!await revisions.DrainWritersAsync(database, CancellationToken.None).ConfigureAwait(false))
            {
                fences.Lift(marker.DatabaseId);
                await moves.AbortHomeMoveAsync(marker, CancellationToken.None).ConfigureAwait(false);
                return ManagementErrors.NotFound;
            }

            var record = await catalog.FindAsync(database, CancellationToken.None).ConfigureAwait(false);
            var placement = record is TenantDatabaseRecord { Placement: var current } ? current : null;
            IReadOnlyList<string> next = placement is null ? [options.Region]
                : [.. placement.Replicas.Where(region => region != marker.Target).Append(options.Region).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
            promoted = await PromoteAsync(peer, marker, new HomePromoteRequest(marker.MoveId, options.Region, marker.Version, next, placement?.AllRegions ?? false))
                .ConfigureAwait(false) ?? await SettleAsync(peer, marker).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.ZLogWarning(error, $"A home move failed before its outcome was known");
            promoted = await SettleAsync(peer, marker).ConfigureAwait(false);
        }

        switch (promoted)
        {
            case true:
                fences.Moved(marker.DatabaseId, marker.Target);
                break;
            case false:
                fences.Lift(marker.DatabaseId);
                break;
            default:
                fences.Unresolved(marker.DatabaseId);
                break;
        }

        var pauseMs = Stopwatch.GetElapsedTime(paused).TotalMilliseconds;
        _pause?.Record(pauseMs);
        logger.ZLogInformation($"Home move to {marker.Target:@Target} {(promoted switch { true => "done", false => "refused", null => "unresolved" }):@Outcome}: catch_up_ms={caughtUp.TotalMilliseconds:F0} pause_ms={pauseMs:F0} total_ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}");
        switch (promoted)
        {
            case true:
                return await FinishAsync(marker).ConfigureAwait(false) switch
                {
                    ManagedDatabase moved => moved,
                    NotFound error => error,
                    Unavailable => Unresolved,
                };
            case false:
                await moves.AbortHomeMoveAsync(marker, CancellationToken.None).ConfigureAwait(false);
                return ReplicaNotReady;
            default:
                return Unresolved;
        }
    }

    private async Task<ReplicaPeer?> PeerAsync(string region, CancellationToken cancellationToken) =>
        (await peers.ListAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(peer => peer.Region == region);

    private async Task<CompleteHomeMoveResult> FinishAsync(HomeMoveMarker marker)
    {
        var result = await moves.CompleteHomeMoveAsync(marker, CancellationToken.None).ConfigureAwait(false);
        if (result is ManagedDatabase)
        {
            fences.Release(marker.DatabaseId);
            try
            {
                _ = await engine.DeleteDatabaseAsync(marker.Database, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or InvalidOperationException)
            {
                logger.ZLogWarning($"A moved database's old home file was not deleted; the orphan sweep removes it: {error.GetType().Name:@Error}");
            }

            replication.WakeCatalog(marker.Target);
        }

        return result;
    }

    private async Task<bool?> PromoteAsync(ReplicaPeer peer, HomeMoveMarker marker, HomePromoteRequest request)
    {
        using var response = await PostAsync(peer, marker, "promote", JsonContent.Create(request, TerraBaseJsonContext.Default.HomePromoteRequest), CancellationToken.None)
            .ConfigureAwait(false);
        if (response is { IsSuccessStatusCode: false })
        {
            var refusal = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            logger.ZLogWarning($"The target region refused promotion: {(int)response.StatusCode:@Status} {refusal:@Body}");
        }

        return response switch
        {
            { StatusCode: HttpStatusCode.OK } => (await response.Content.ReadFromJsonAsync(TerraBaseJsonContext.Default.HomePromoteAnswer).ConfigureAwait(false))?.Promoted,
            { StatusCode: HttpStatusCode.Conflict or HttpStatusCode.NotFound } => false,
            _ => null,
        };
    }

    private async Task<bool?> SettleAsync(ReplicaPeer peer, HomeMoveMarker marker)
    {
        using var response = await PostAsync(peer, marker, "promote/abort",
            JsonContent.Create(new HomeAbortRequest(marker.MoveId, marker.Version), TerraBaseJsonContext.Default.HomeAbortRequest), CancellationToken.None).ConfigureAwait(false);
        return response is { StatusCode: HttpStatusCode.OK }
            ? (await response.Content.ReadFromJsonAsync(TerraBaseJsonContext.Default.HomePromoteAnswer).ConfigureAwait(false))?.Promoted
            : null;
    }

    private async Task<HttpResponseMessage?> PostAsync(ReplicaPeer peer, HomeMoveMarker marker, string action, HttpContent? content, CancellationToken cancellationToken)
    {
        var address = new Uri(peer.Internal, ReplicaRoutes.DatabasePath(marker.Database, action));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PeerTimeout);
        try
        {
            return await _http.PostAsync(address, content ?? new ByteArrayContent([]), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.ZLogInformation($"A home move request to {peer.Region:@Region} ({action:@Action}) got no answer: {error.GetType().Name:@Error}");
            return null;
        }
        finally
        {
            content?.Dispose();
        }
    }

    public async Task<PromoteResult> PromoteAsync(TenantContext database, HomePromoteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var gate = _locks.GetOrAdd(database.DatabaseId!, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_aborted.ContainsKey(request.MoveId))
            {
                return new Conflict("this move was abandoned", "move_aborted");
            }

            if (await catalog.FindAsync(database, CancellationToken.None).ConfigureAwait(false) is not TenantDatabaseRecord { Placement: var placement })
            {
                return new Conflict("this region does not record the database", "move_aborted");
            }

            if (placement.Home == options.Region)
            {
                if (placement.Version >= request.Version)
                {
                    await DrainAsync(database, request.Version).ConfigureAwait(false);
                    return new HomePromoteAnswer(true);
                }

                return new HomePromoteAnswer(false);
            }

            if (placement.Home != request.FromRegion || placement.Version >= request.Version)
            {
                return new Conflict("this region records another placement for the database", "move_aborted");
            }

            return await PromoteHereAsync(database, request).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<PromoteResult> PromoteHereAsync(TenantContext database, HomePromoteRequest request)
    {
        await replication.SuspendAsync(database).ConfigureAwait(false);
        try
        {
            if (await replicas.PullAsync(database, fromHome: true, CancellationToken.None).ConfigureAwait(false) is null
                || await replicas.PullAsync(database, fromHome: true, CancellationToken.None).ConfigureAwait(false) is not { Changed: false })
            {
                return new Conflict("the replica is not open or its home is still writing", "move_aborted");
            }

            IReadOnlyList<string> next = [.. request.ReplicaRegions.Where(region => region != options.Region)];
            return await AdoptAsync(database, request.FromRegion, new DatabasePlacement(options.Region, next, request.Version, request.AllRegions), failedHome: null)
                .ConfigureAwait(false);
        }
        finally
        {
            replication.Resume(database);
        }
    }

    public async Task<bool> FailOverAsync(TenantContext database, HomeLease failedHome, CancellationToken cancellationToken)
    {
        var fromRegion = failedHome.Home;
        var version = failedHome.Epoch + 1;
        var gate = _locks.GetOrAdd(database.DatabaseId!, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await catalog.FindAsync(database, CancellationToken.None).ConfigureAwait(false) is not TenantDatabaseRecord { Placement: var placement })
            {
                return false;
            }

            if (placement.Home == options.Region)
            {
                return placement.Version >= version;
            }

            IReadOnlyList<string> next = placement.AllRegions ? []
                : [.. placement.Replicas.Where(region => region != options.Region).Append(fromRegion).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
            await replication.SuspendAsync(database).ConfigureAwait(false);
            try
            {
                return await AdoptAsync(database, fromRegion, new DatabasePlacement(options.Region, next, version, placement.AllRegions), failedHome)
                    .ConfigureAwait(false) is HomePromoteAnswer { Promoted: true };
            }
            finally
            {
                replication.Resume(database);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<PromoteResult> AdoptAsync(TenantContext database, string fromRegion, DatabasePlacement placement, HomeLease? failedHome)
    {
        string? detached = null;
        try
        {
            detached = await replicas.DetachAsync(database, CancellationToken.None).ConfigureAwait(false);
            if (detached is null)
            {
                return new Conflict("the replica holds no pull", "move_aborted");
            }

            fences.Lift(database.DatabaseId!);
            if (leases.Active)
            {
                leaseGate.Lease(database.DatabaseId!, held: false);
            }

            await imports.ImportDatabaseAsync(database, detached, CancellationToken.None).ConfigureAwait(false);
            detached = null;
            if (!await leases.TakeAsync(database, fromRegion, placement.Version, failedHome, CancellationToken.None).ConfigureAwait(false))
            {
                leaseGate.Unlease(database.DatabaseId!);
                _ = await engine.DeleteDatabaseAsync(database, CancellationToken.None).ConfigureAwait(false);
                return new Conflict("another region holds the database's home lease", "move_aborted");
            }

            if (await tenants.UpdatePlacementAsync(database, placement, CancellationToken.None).ConfigureAwait(false) is not PlacementUpdate { Updated: true })
            {
                if (!leases.Active)
                {
                    _ = await engine.DeleteDatabaseAsync(database, CancellationToken.None).ConfigureAwait(false);
                }

                logger.ZLogError($"A new home's catalog did not take its placement; it keeps the database file and its lease");
                return new Unavailable("the catalog did not take the new placement");
            }

            await apply.PrepareAsync(database, CancellationToken.None).ConfigureAwait(false);
            await DrainAsync(database, placement.Version).ConfigureAwait(false);
            return new HomePromoteAnswer(true);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.ZLogWarning(error, $"Promoting a replica to home failed; it follows its home again");
            return new Unavailable("the replica could not become the home");
        }
        finally
        {
            if (detached is not null)
            {
                TursoReplicas.DeleteDetached(detached);
            }
        }
    }

    private async Task DrainAsync(TenantContext database, long fence)
    {
        var backoff = TimeSpan.FromMilliseconds(200);
        for (var attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                var drained = await pushes.DrainOriginsAsync(database, fence, CancellationToken.None).ConfigureAwait(false);
                logger.ZLogInformation($"A new home applied {drained} pushed transactions addressed to its former home");
                return;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.ZLogWarning($"Pushes addressed to the former home wait to be applied: {error.GetType().Name:@Error}");
                await Task.Delay(backoff, time).ConfigureAwait(false);
                backoff = TimeSpan.FromTicks(Math.Min(TimeSpan.FromSeconds(5).Ticks, backoff.Ticks * 2));
            }
        }

        throw new IOException("the new home could not drain retained origin pushes");
    }

    public async Task<HomePromoteAnswer> AbortAsync(TenantContext database, HomeAbortRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var gate = _locks.GetOrAdd(database.DatabaseId!, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await catalog.FindAsync(database, CancellationToken.None).ConfigureAwait(false) is TenantDatabaseRecord { Placement: var placement }
                && placement.Home == options.Region && placement.Version >= request.Version)
            {
                await DrainAsync(database, request.Version).ConfigureAwait(false);
                return new HomePromoteAnswer(true);
            }

            _aborted[request.MoveId] = true;
            return new HomePromoteAnswer(false);
        }
        finally
        {
            gate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SettleEvery, time);
        var tick = 0L;
        do
        {
            try
            {
                await SettleHeldAsync(stoppingToken).ConfigureAwait(false);
                if (tick++ % 6 == 0)
                {
                    await FollowServingRegionsAsync(stoppingToken).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                logger.ZLogInformation($"Unresolved home moves are settled on the next pass: {error.GetType().Name:@Error}");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    internal async Task FollowServingRegionsAsync(CancellationToken cancellationToken)
    {
        var serving = await regions.ServingAsync(cancellationToken).ConfigureAwait(false);
        if (await moves.FollowServingRegionsAsync(serving, cancellationToken).ConfigureAwait(false) is > 0 and var changed)
        {
            logger.ZLogInformation($"All-region databases follow the serving regions ({string.Join(",", serving):@Regions}): {changed:@Changed} changed");
        }
    }

    private async Task SettleHeldAsync(CancellationToken cancellationToken)
    {
        var held = fences.Held.Where(id => !_running.ContainsKey(id)).ToHashSet(StringComparer.Ordinal);
        var markers = (await moves.PendingHomeMovesAsync(cancellationToken).ConfigureAwait(false))
            .Where(marker => !_running.ContainsKey(marker.DatabaseId)).ToDictionary(marker => marker.DatabaseId, StringComparer.Ordinal);
        foreach (var (_, unsent) in markers.Where(pair => !held.Contains(pair.Key)))
        {
            await moves.AbortHomeMoveAsync(unsent, CancellationToken.None).ConfigureAwait(false);
        }

        foreach (var id in held)
        {
            if (!markers.TryGetValue(id, out var marker))
            {
                await SettleWithoutMarkerAsync(id, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (await PeerAsync(marker.Target, cancellationToken).ConfigureAwait(false) is not { } peer || await SettleAsync(peer, marker).ConfigureAwait(false) is not { } promoted)
            {
                continue;
            }

            if (promoted)
            {
                fences.Moved(id, marker.Target);
                _ = await FinishAsync(marker).ConfigureAwait(false);
            }
            else
            {
                fences.Lift(id);
                await moves.AbortHomeMoveAsync(marker, CancellationToken.None).ConfigureAwait(false);
            }

            logger.ZLogInformation($"An unresolved home move was settled: {(promoted ? "moved" : "kept here"):@Outcome}");
        }
    }

    private async Task SettleWithoutMarkerAsync(string id, CancellationToken cancellationToken)
    {
        await foreach (var database in engine.ListDatabasesAsync(cancellationToken).ConfigureAwait(false))
        {
            if (database.DatabaseId != id)
            {
                continue;
            }

            switch (await catalog.FindAsync(database, cancellationToken).ConfigureAwait(false))
            {
                case TenantDatabaseRecord { Placement.Home: var home } when home == options.Region:
                    fences.Lift(id);
                    return;
                case TenantDatabaseRecord { Placement.Home: var home }:
                    fences.Moved(id, home);
                    fences.Release(id);
                    _ = await engine.DeleteDatabaseAsync(database, CancellationToken.None).ConfigureAwait(false);
                    return;
                default:
                    return;
            }
        }

        fences.Lift(id);
    }
}
