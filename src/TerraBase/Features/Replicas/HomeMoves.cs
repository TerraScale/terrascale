using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using NATS.Client.Core;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Management;
using TerraBase.Features.Replicas;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Replicas;

public sealed record HomePromoteRequest(string MoveId, string FromRegion, long Version, IReadOnlyList<string> ReplicaRegions, bool AllRegions,
    string? Name = null, DatabaseIdentity? Identity = null, string? FromNode = null);

public sealed record HomeAbortRequest(string MoveId, long Version);

public sealed record HomePromoteAnswer(bool Promoted);

internal readonly union MoveHomeResult(ManagedDatabase, NotFound, Validation, Conflict, Unavailable);

internal readonly union PromoteResult(HomePromoteAnswer, Conflict, Unavailable);

internal sealed class HomeMoves(
    TerraBaseOptions options, IHomeMoveCatalog moves, IReplicaCatalog catalog, IReplicaPeers peers, ITenantDirectory tenants, ReplicaNames names, IDatabaseReplicas replicas, RegionReplication replication,
    IStorageEngine engine, IDatabaseRevisions revisions, IWalBackup imports, DatabaseFences fences, Regions.IRegionCatalog regions, HomeApply.IHomeApply apply, HomeLeases leases, HomeLeaseGate leaseGate, HomePush.HomePush pushes,
    KeptCopies kept, RegionalOwners owners, InternalCredential credential, IMeterFactory meters, TimeProvider time, ILogger<HomeMoves> logger)
    : BackgroundService
{
    internal static readonly Unavailable ReplicaNotReady = new("the target region's replica could not catch up; nothing changed, retry", "replica_not_ready");
    internal static readonly Unavailable Unresolved = new("the move is being settled with the target region; writes stay paused until it is, retry", "move_unresolved");

    private static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SettleEvery = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DrainBudget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CatchUpBound = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http = PeerHttp.Create(credential);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _aborted = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, MoveRequest> _running = new(StringComparer.Ordinal);
    private readonly Meter _meter = meters.Create(Telemetry.MeterName);
    private Histogram<double>? _pause;
    private Counter<long>? _catchUpTimeouts;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _pause = _meter.CreateHistogram<double>("terrabase.placement.move.pause", "ms", "Time writes of a database paused while its home moved.");
        _catchUpTimeouts = _meter.CreateCounter<long>("terrabase.placement.move.catch_up_timeouts", "{move}",
            "Promotions that answered before the new home's sequencers reached the end of every origin stream.");
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

    private sealed record MoveRequest(IReadOnlyList<string>? Regions);

    public async Task<MoveHomeResult> MoveAsync(TenantContext database, string target, IReadOnlyList<string>? requestedRegions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        var started = Stopwatch.GetTimestamp();
        var request = new MoveRequest(requestedRegions);
        var running = _running.GetOrAdd(database.DatabaseId!, request);
        if (!ReferenceEquals(running, request))
        {
            return DatabaseRegions.SameSet(running.Regions, requestedRegions) ? RegionSetPlacement.PlacementNotReady : (MoveHomeResult)EngineManagementService.MoveInProgress;
        }

        try
        {
            return await moves.BeginHomeMoveAsync(database, target, requestedRegions, cancellationToken).ConfigureAwait(false) switch
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
        fences.Moving(marker.DatabaseId);
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
        Conflict? refusal = null;
        try
        {
            if (!await revisions.DrainWritersAsync(database, CancellationToken.None).ConfigureAwait(false))
            {
                fences.Lift(marker.DatabaseId);
                await moves.AbortHomeMoveAsync(marker, CancellationToken.None).ConfigureAwait(false);
                return ManagementErrors.NotFound;
            }

            var record = await catalog.FindAsync(database, CancellationToken.None).ConfigureAwait(false) is TenantDatabaseRecord found ? found : null;
            var placement = record?.Placement;
            IReadOnlyList<string> next = marker.RequestedRegions is { } selected
                ? [.. selected.Where(region => region != marker.Target).Order(StringComparer.Ordinal)]
                : placement is null ? [options.Region]
                : [.. placement.Replicas.Where(region => region != marker.Target).Append(options.Region).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
            var promotion = await PromoteAsync(peer, marker, new HomePromoteRequest(marker.MoveId, options.Region, marker.Version, next, marker.RequestedRegions is null && (placement?.AllRegions ?? false), record?.Name, record?.Identity, options.MachineId))
                .ConfigureAwait(false);
            promoted = promotion switch
            {
                HomePromoteAnswer answer => answer.Promoted,
                Conflict => false,
                Unavailable => await SettleAsync(peer, marker).ConfigureAwait(false),
            };
            refusal = promotion switch
            {
                Conflict { Code: "name_taken" } => ManagementErrors.NameTaken with
                {
                    Message = $"Another database in {marker.Target} already uses this name. Rename one of them, then save again.",
                },
                HomePromoteAnswer or Conflict or Unavailable => null,
            };
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
                return refusal is not null ? refusal : (MoveHomeResult)ReplicaNotReady;
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
            await RemoveProvenAsync(marker.Database).ConfigureAwait(false);
            replication.WakeCatalog(marker.Target);
        }

        return result;
    }

    private async Task RemoveProvenAsync(TenantContext database)
    {
        _ = await leases.RemoveIfProvenAsync(database, KeepReason.MoveUnproven, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<PromoteResult> PromoteAsync(ReplicaPeer peer, HomeMoveMarker marker, HomePromoteRequest request)
    {
        using var response = await PostAsync(peer, marker, "promote", JsonContent.Create(request, ReplicasJsonContext.Default.HomePromoteRequest), CancellationToken.None)
            .ConfigureAwait(false);
        string? code = null;
        if (response is { IsSuccessStatusCode: false })
        {
            var refusal = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            logger.ZLogWarning($"The target region refused promotion: {(int)response.StatusCode:@Status} {refusal:@Body}");
            code = response.StatusCode == HttpStatusCode.Conflict
                ? JsonSerializer.Deserialize(refusal, TerraBaseJsonContext.Default.ErrorBody)?.Error.Code : null;
        }

        return response switch
        {
            { StatusCode: HttpStatusCode.OK } => await response.Content.ReadFromJsonAsync(ReplicasJsonContext.Default.HomePromoteAnswer).ConfigureAwait(false) is { } answer
                ? answer : (PromoteResult)ReplicaNotReady,
            { StatusCode: HttpStatusCode.Conflict } when code == "name_taken" => ManagementErrors.NameTaken,
            { StatusCode: HttpStatusCode.Conflict or HttpStatusCode.NotFound } => new Conflict("the target refused promotion", "move_aborted"),
            _ => ReplicaNotReady,
        };
    }

    private async Task<bool?> SettleAsync(ReplicaPeer peer, HomeMoveMarker marker)
    {
        using var response = await PostAsync(peer, marker, "promote/abort",
            JsonContent.Create(new HomeAbortRequest(marker.MoveId, marker.Version), ReplicasJsonContext.Default.HomeAbortRequest), CancellationToken.None).ConfigureAwait(false);
        return response is { StatusCode: HttpStatusCode.OK }
            ? (await response.Content.ReadFromJsonAsync(ReplicasJsonContext.Default.HomePromoteAnswer).ConfigureAwait(false))?.Promoted
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

            if (fences.Promoting(database.DatabaseId!) is { } pending)
            {
                switch (await SettlePromotionAsync(database, pending).ConfigureAwait(false))
                {
                    case null:
                        return Unresolved;
                    case true when pending.Version >= request.Version:
                        return new HomePromoteAnswer(true);
                    case true or false:
                        break;
                }
            }

            if (!owners.Writer)
            {
                leases.Refused(HomeRefusal.NotWriter, "home");
                return new Conflict("this node is not its region's writer", "move_aborted");
            }

            if (await catalog.FindAsync(database, CancellationToken.None).ConfigureAwait(false) is not TenantDatabaseRecord { Placement: var placement })
            {
                return new Conflict("this region does not record the database", "move_aborted");
            }

            if (placement.Home == options.Region)
            {
                if (placement.Version >= request.Version)
                {
                    await CatchUpAsync(database).ConfigureAwait(false);
                    return new HomePromoteAnswer(true);
                }

                return new HomePromoteAnswer(false);
            }

            if (placement.Home != request.FromRegion || placement.Version >= request.Version)
            {
                return new Conflict("this region records another placement for the database", "move_aborted");
            }

            return await PromoteHereAsync(database, request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<PromoteResult> PromoteHereAsync(TenantContext database, HomePromoteRequest request, CancellationToken cancellationToken)
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
            return await AdoptAsync(database, request, new DatabasePlacement(options.Region, next, request.Version, request.AllRegions), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            replication.Resume(database);
        }
    }

    private async Task<PromoteResult> AdoptAsync(TenantContext database, HomePromoteRequest request, DatabasePlacement placement, CancellationToken cancellationToken)
    {
        var (fromRegion, name, identity) = (request.FromRegion, request.Name, request.Identity);
        if (await catalog.FindAsync(database, CancellationToken.None).ConfigureAwait(false) is not TenantDatabaseRecord)
        {
            return new Conflict("this region does not record the database", "move_aborted");
        }

        if (name is not null)
        {
            using var lookups = new ReplicaNameLookups(cancellationToken);
            var nameApplied = await names.ApplyAsync(new ReplicaCatalogEntry(database.OrganizationId, database.ProjectId!, database.DatabaseId!, name,
                fromRegion, placement.Replicas, placement.Version, placement.AllRegions, identity), listed: null, lookups, cancellationToken).ConfigureAwait(false);
            var named = await tenants.FindDatabaseAsync(database.OrganizationId, database.ProjectId!, name, CancellationToken.None).ConfigureAwait(false);
            if (named is TenantContext taken && taken.DatabaseId != database.DatabaseId)
            {
                return ManagementErrors.NameTaken;
            }

            if (named is Unavailable error)
            {
                return error;
            }

            if (!nameApplied)
            {
                return new Conflict("the catalog changed during the name check; repeat the move", "move_aborted");
            }
        }

        if (replicas.HeldRevision(database) is not { } adopted)
        {
            return new Conflict("the replica holds no pull", "move_aborted");
        }

        if (await leases.RestoreNodeAsync(database, fromRegion, request.FromNode, CancellationToken.None).ConfigureAwait(false) is not { } fromNode)
        {
            return new Conflict("the previous home's node is not known, so a rollback could not name it; nothing changed", "move_aborted");
        }

        var id = database.DatabaseId!;
        var source = new PromotionSource(fromRegion, fromNode, placement.Version);
        string? detached = null;
        try
        {
            detached = await replicas.DetachAsync(database, CancellationToken.None).ConfigureAwait(false);
            if (detached is null)
            {
                return new Conflict("the replica holds no pull", "move_aborted");
            }

            fences.Promote(id, source);
            if (leases.Active)
            {
                leaseGate.Lease(id, held: false);
            }

            _ = await kept.KeepAsync(database, KeepReason.MoveTarget, CancellationToken.None).ConfigureAwait(false);
            await imports.ImportDatabaseAsync(database, detached, CancellationToken.None).ConfigureAwait(false);
            if (await leases.TakeAsync(database, fromRegion, placement.Version, CancellationToken.None).ConfigureAwait(false) is not { } lease)
            {
                return await RollBackAsync(database, source).ConfigureAwait(false)
                    ? new Conflict("another region holds the database's home lease; the previous home is still the home", "move_aborted")
                    : Unresolved;
            }

            var completing = detached;
            detached = null;
            replicas.CompleteDetach(completing);

            if (await tenants.UpdatePlacementAsync(database, placement, CancellationToken.None, name, identity).ConfigureAwait(false) is not PlacementUpdate { Updated: true })
            {
                return await RefuseAsync(HomeRefusal.PlacementRefused, "the catalog did not take the new placement").ConfigureAwait(false);
            }

            if (!await PrepareAsync(database).ConfigureAwait(false))
            {
                return await RefuseAsync(HomeRefusal.PrepareFailed, "the new home could not prepare").ConfigureAwait(false);
            }

            if (await DrainAsync(database, placement.Version).ConfigureAwait(false) is not { } drained)
            {
                return await RefuseAsync(HomeRefusal.DrainFailed, "the new home could not apply the pushes addressed to the previous home").ConfigureAwait(false);
            }

            if (!await leases.CommitAsync(database, lease, adopted, CancellationToken.None).ConfigureAwait(false))
            {
                return await RefuseAsync(HomeRefusal.LeaseChanged, "the home lease changed before the commit").ConfigureAwait(false);
            }

            fences.Lift(id);
            logger.ZLogInformation($"A home move committed: this node is the home at epoch {placement.Version:@Epoch}, holding {adopted.ToString():@Adopted}; it serves writes from now on");
            await pushes.PublishDrainedAsync(database, drained, CancellationToken.None).ConfigureAwait(false);
            await CatchUpAsync(database).ConfigureAwait(false);
            return new HomePromoteAnswer(true);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.ZLogWarning(error, $"Promoting a replica to home failed; the move is settled from its commit point");
            await SettleAfterFailureAsync(database).ConfigureAwait(false);
            return new Unavailable("the replica could not become the home");
        }
        finally
        {
            if (detached is not null)
            {
                replicas.CompleteDetach(detached);
            }
        }

        async Task<PromoteResult> RefuseAsync(HomeRefusal refusal, string reason)
        {
            leases.Refused(refusal, "home");
            logger.ZLogWarning($"A home move stopped before its commit point: {reason:@Reason}; the previous home resumes and this node keeps its imported copy");
            return await RollBackAsync(database, source).ConfigureAwait(false)
                ? new Conflict($"{reason}; the previous home is still the home", "move_aborted")
                : Unresolved;
        }
    }

    private async Task<bool> PrepareAsync(TenantContext database)
    {
        try
        {
            await apply.PreparePromotingAsync(database, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.ZLogWarning(error, $"A new home could not prepare home apply");
            return false;
        }
    }

    private async Task SettleAfterFailureAsync(TenantContext database)
    {
        if (fences.Promoting(database.DatabaseId!) is not { } pending)
        {
            return;
        }

        try
        {
            _ = await SettlePromotionAsync(database, pending).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.ZLogWarning(error, $"A failed promotion stays fenced; it is settled when the previous home asks again");
        }
    }

    private async Task<bool?> SettlePromotionAsync(TenantContext database, PromotionSource source)
    {
        switch (await leases.CommittedAsync(database, source.Version, CancellationToken.None).ConfigureAwait(false))
        {
            case true:
                fences.Lift(database.DatabaseId!);
                logger.ZLogWarning($"A home move that reached its commit point before this node stopped is finished: this node is the home at epoch {source.Version:@Epoch}");
                return true;
            case false:
                return await RollBackAsync(database, source).ConfigureAwait(false) ? false : null;
            default:
                return null;
        }
    }

    private async Task<bool> RollBackAsync(TenantContext database, PromotionSource source)
    {
        var id = database.DatabaseId!;
        leaseGate.Lease(id, held: false);
        if (await leases.RestoreAsync(database, source, source.Version, CancellationToken.None).ConfigureAwait(false) is not { } restored)
        {
            return false;
        }

        if (await catalog.FindAsync(database, CancellationToken.None).ConfigureAwait(false) is TenantDatabaseRecord { Placement: var placement } && placement.Home == options.Region)
        {
            var version = Math.Max(restored, placement.Version + 1);
            IReadOnlyList<string> others = [.. placement.Replicas.Append(options.Region).Where(region => region != source.Region).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
            var previous = placement.AllRegions ? new DatabasePlacement(source.Region, [], version, AllRegions: true) : new DatabasePlacement(source.Region, others, version);
            if (await moves.FollowHomeLeaseAsync(database, previous, CancellationToken.None).ConfigureAwait(false) is not PlacementUpdate { Updated: true })
            {
                throw new IOException("the catalog did not take the previous home back; the rollback is settled again");
            }
        }

        _ = await kept.KeepAsync(database, KeepReason.MoveRolledBack, CancellationToken.None).ConfigureAwait(false);
        fences.Lift(id);
        if (!engine.HasDatabaseFiles(database, CancellationToken.None))
        {
            leaseGate.Unlease(id);
        }

        replication.Resume(database);
        replication.WakeCatalog(source.Region);
        logger.ZLogWarning($"A home move was rolled back before its commit point: {source.Node:@Node} in {source.Region:@Region} is the home again; this node acknowledged no write as home and kept its imported copy");
        return true;
    }

    private async Task CatchUpAsync(TenantContext database)
    {
        using var bound = new CancellationTokenSource(CatchUpBound, time);
        var backoff = new Backoff(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(5));
        try
        {
            while (true)
            {
                try
                {
                    await pushes.CatchUpAsync(database, bound.Token).ConfigureAwait(false);
                    logger.ZLogInformation($"A new home's sequencers reached the end of every origin stream for the database");
                    return;
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    logger.ZLogWarning($"A new home's sequencers wait to start: {error.GetType().Name:@Error}");
                    await Task.Delay(backoff.Next(), time, bound.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (bound.IsCancellationRequested)
        {
            _catchUpTimeouts?.Add(1);
            IReadOnlyList<(string Origin, ulong Frontier, ulong Last)> behind;
            try
            {
                behind = await pushes.BehindAsync(database, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or NatsException or StorageBusyException or SqlStatementException)
            {
                logger.ZLogWarning($"A new home of {database.DatabaseId:@Database} answers promoted before its sequencers reached the end of every origin stream; how far behind is unknown: {error.GetType().Name:@Error}");
                return;
            }

            foreach (var (origin, frontier, last) in behind)
            {
                logger.ZLogWarning($"A new home of {database.DatabaseId:@Database} answers promoted while the push from {origin:@Origin} is behind: frontier {frontier:@Frontier}, stream last {last:@Last}");
            }
        }
    }

    private async Task<IReadOnlyList<HomePush.DrainedPush>?> DrainAsync(TenantContext database, long fence)
    {
        var backoff = new Backoff(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(5));
        var started = time.GetTimestamp();
        while (time.GetElapsedTime(started) < DrainBudget)
        {
            try
            {
                var drained = await pushes.DrainOriginsAsync(database, fence, CancellationToken.None).ConfigureAwait(false);
                logger.ZLogInformation($"A new home applied {drained.Count} pushed transactions addressed to its former home before its commit point");
                return drained;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.ZLogWarning($"Pushes addressed to the former home wait to be applied: {error.GetType().Name:@Error}");
                await Task.Delay(backoff.Next(), time).ConfigureAwait(false);
            }
        }

        return null;
    }

    public async Task<PromoteResult> AbortAsync(TenantContext database, HomeAbortRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var gate = _locks.GetOrAdd(database.DatabaseId!, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (fences.Promoting(database.DatabaseId!) is { } pending)
            {
                switch (await SettlePromotionAsync(database, pending).ConfigureAwait(false))
                {
                    case null:
                        return Unresolved;
                    case true when pending.Version >= request.Version:
                        return new HomePromoteAnswer(true);
                    case true or false:
                        break;
                }
            }

            if (await catalog.FindAsync(database, CancellationToken.None).ConfigureAwait(false) is TenantDatabaseRecord { Placement: var placement }
                && placement.Home == options.Region && placement.Version >= request.Version)
            {
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
                    await RemoveProvenAsync(database).ConfigureAwait(false);
                    return;
                default:
                    return;
            }
        }

        fences.Lift(id);
    }
}
