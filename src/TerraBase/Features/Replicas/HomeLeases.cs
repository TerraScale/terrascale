using NATS.Client.Core;
using NATS.Client.JetStream.Models;
using NATS.Client.JetStream;
using NATS.Client.KeyValueStore;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Engine.Revisions;
using TerraBase.Engine;
using TerraBase.Features.Management;
using TerraBase.Features.Platform;
using TerraBase.Features.Replicas;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Replicas;

internal sealed record NodeLease(string Node, string Region, string Session);

internal sealed record HomeLease(string OrganizationId, string ProjectId, string DatabaseId, string Home, string Node, long Epoch, string Session, string? Adopted = null);

internal sealed record HomeLeaseTaken(HomeLease? Previous, HomeLease Taken);

internal sealed record HeldRevisionAnswer(string Region, string? Revision);

internal enum HomeRefusal
{
    NotWriter,
    LeaseChanged,
    PlacementRefused,
    PrepareFailed,
    ReplicaNewer,
    PrimaryMissing,
    Unproven,
    DrainFailed,
    PositionUnknown,
}

internal interface IHomeWriteLease
{
    ValueTask<bool> PrepareAsync(TenantContext database, CancellationToken cancellationToken);
}

internal sealed partial class HomeLeases(
    TerraBaseOptions options, IReplicaCatalog catalog, DatabaseFences fences, HomeLeaseGate leaseGate, RegionReplication replication, IStorageEngine engine,
    IDatabaseRevisions revisions, IDatabaseReplicas replicas, IHomeMoveCatalog moves, KeptCopies kept, IMeterFactory meters, ILogger<HomeLeases> logger, RegionalOwners owners)
    : BackgroundService, IHomeWriteLease
{
    internal const string Bucket = "terrabase-homes";
    internal static readonly TimeSpan Ttl = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RenewEvery = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Margin = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ScanEvery = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Backoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StartScan = TimeSpan.FromSeconds(10);
    private const int WrongLastSequence = 10071;

    private readonly ConcurrentDictionary<string, (HomeLease Lease, ulong Revision)> _homes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ulong> _isolated = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, NodeLease> _nodes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _lapsed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, UnservedDatabase> _unserved = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ulong> _foreign = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _inPlace = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _confirming = new(1, 1);
    private readonly Meter _meter = meters.Create(Telemetry.MeterName);
    private NatsConnection? _nats;
    private NatsJSContext? _js;
    private INatsKVStore? _kv;
    private string _session = Ulid.NewUlid().ToString();
    private ulong? _nodeRevision;
    private Counter<long>? _lapses;
    private Counter<long>? _refusals;
    private Counter<long>? _demotions;

    private string NodeKey => "node." + options.MachineId;

    private static string HomeKey(string database) => "home." + database;

    private static bool RegionOnly(DatabasePlacement placement) => !placement.AllRegions && placement.Replicas.Count == 0;

    private static bool PlatformDatabase(string database) => database is PlatformDatabases.AuthId or PlatformDatabases.DashboardId;

    internal static string Reason(HomeRefusal refusal) => refusal switch
    {
        HomeRefusal.NotWriter => "not_writer",
        HomeRefusal.LeaseChanged => "lease_changed",
        HomeRefusal.PlacementRefused => "placement_refused",
        HomeRefusal.PrepareFailed => "prepare_failed",
        HomeRefusal.ReplicaNewer => "replica_newer",
        HomeRefusal.PrimaryMissing => "primary_missing",
        HomeRefusal.Unproven => "unproven",
        HomeRefusal.DrainFailed => "drain_failed",
        HomeRefusal.PositionUnknown => "position_unknown",
        _ => throw new UnreachableException(),
    };

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (options is { NatsUrl: { } natsUrl, NatsToken: { } natsToken })
        {
            _nats = new NatsConnection(new NatsOpts
            {
                Url = natsUrl.ToString(),
                Name = $"terrabase-lease-{options.Region}-{options.MachineId}",
                AuthOpts = new NatsAuthOpts { Token = natsToken },
                MaxReconnectRetry = -1,
                ReconnectWaitMin = TimeSpan.FromMilliseconds(100),
                ReconnectWaitMax = TimeSpan.FromSeconds(1),
            });
            _js = new NatsJSContext(_nats);
            _lapses = _meter.CreateCounter<long>("terrabase.home.lapses", "{lapse}", "Home leases whose node lapsed while this node watched; the database's home stayed where it was.");
            _refusals = _meter.CreateCounter<long>("terrabase.home.refusals", "{refusal}", "Home or regional writer changes this node refused because their proof was missing.");
            _demotions = _meter.CreateCounter<long>("terrabase.home.demotions", "{demotion}", "Databases this node stopped homing after their new home proved it holds their content.");
            _ = _meter.CreateObservableGauge("terrabase.home.leased", () => leaseGate.Leased.Count, "{database}", "Databases whose writes on this node need its home lease.");
            _ = _meter.CreateObservableGauge("terrabase.home.unserved", () => _unserved.Values.GroupBy(database => database.Region, StringComparer.Ordinal)
                .Select(region => new Measurement<long>(region.Count(), new KeyValuePair<string, object?>("region", region.Key))), "{database}",
                "Databases whose home lease names a node that cannot serve them, by the lease's region; nothing serves them until an operator acts.");
            using var scan = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            scan.CancelAfter(StartScan);
            try
            {
                await FenceHomedAsync(scan.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger.ZLogWarning($"The catalog scan at start did not finish; databases with replicas are fenced on the first lease scan");
            }
        }

        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        if (_nats is { } nats)
        {
            await nats.DisposeAsync().ConfigureAwait(false);
        }
    }

    public override void Dispose()
    {
        _confirming.Dispose();
        _meter.Dispose();
        base.Dispose();
    }

    internal bool Ready => _kv is not null && _nodeRevision is not null;

    internal bool Active => _js is not null;

    internal IReadOnlyList<UnservedDatabase> Unserved => [.. _unserved.Values.OrderBy(database => database.DatabaseId, StringComparer.Ordinal)];

    internal void Refused(HomeRefusal refusal, string scope)
    {
        _refusals?.Add(1, new KeyValuePair<string, object?>("reason", Reason(refusal)), new KeyValuePair<string, object?>("scope", scope));
        logger.ZLogWarning($"Refused a {scope:@Scope} change: {Reason(refusal):@Reason}");
    }

    public async ValueTask<bool> PrepareAsync(TenantContext database, CancellationToken cancellationToken)
    {
        if (_js is null)
        {
            return true;
        }

        if (await catalog.FindAsync(database, cancellationToken).ConfigureAwait(false) is not TenantDatabaseRecord { Placement: var placement }
            || placement.Home != options.Region)
        {
            return false;
        }

        if (placement is { AllRegions: false, Replicas.Count: 0 })
        {
            return true;
        }

        var id = database.DatabaseId!;
        var kv = _kv;
        await _confirming.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!leaseGate.Leased.Contains(id))
            {
                leaseGate.Lease(id, held: false);
            }

            if (kv is null || !Ready)
            {
                return false;
            }

            await ConfirmAsync(kv, database, placement, await MovingAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            return leaseGate.Holds(id);
        }
        finally
        {
            _confirming.Release();
        }
    }

    internal async ValueTask<string?> RestoreNodeAsync(TenantContext database, string fromRegion, string? fromNode, CancellationToken cancellationToken)
    {
        if (_js is null)
        {
            return fromNode ?? "";
        }

        if (_kv is not { } kv || !Ready)
        {
            return null;
        }

        return await ReadAsync(kv, database.DatabaseId!, cancellationToken).ConfigureAwait(false) is { Value: { } bytes } && Decode(bytes) is { } current
            && current.Home == fromRegion ? current.Node : fromNode;
    }

    internal async ValueTask<HomeLeaseTaken?> TakeAsync(TenantContext database, string fromRegion, long epoch, CancellationToken cancellationToken)
    {
        var id = database.DatabaseId!;
        var taken = new HomeLease(database.OrganizationId, database.ProjectId!, id, options.Region, options.MachineId, epoch, _session);
        if (fromRegion == options.Region || PlatformDatabase(id)
            || await catalog.FindAsync(database, cancellationToken).ConfigureAwait(false) is not TenantDatabaseRecord { Placement: var placement }
            || RegionOnly(placement))
        {
            return null;
        }

        if (_js is null)
        {
            return new HomeLeaseTaken(null, taken);
        }

        if (!owners.Writer)
        {
            Refused(HomeRefusal.NotWriter, "home");
            return null;
        }

        if (_kv is not { } kv || !Ready)
        {
            return null;
        }

        var current = await ReadAsync(kv, id, cancellationToken).ConfigureAwait(false);
        var existing = current?.Value is { } bytes ? Decode(bytes) : null;
        if (existing is not null && (existing.Home != fromRegion || existing.Epoch >= epoch))
        {
            Refused(HomeRefusal.LeaseChanged, "home");
            return null;
        }

        leaseGate.Lease(id, held: false);
        return new HomeLeaseTaken(existing, taken);
    }

    internal async ValueTask<bool> CommitAsync(TenantContext database, HomeLeaseTaken taken, DatabaseRevision adopted, CancellationToken cancellationToken)
    {
        var id = database.DatabaseId!;
        var committed = taken.Taken with { Adopted = adopted.ToString() };
        if (_js is null)
        {
            return true;
        }

        if (_kv is not { } kv)
        {
            return false;
        }

        while (true)
        {
            var current = await ReadAsync(kv, id, cancellationToken).ConfigureAwait(false);
            var existing = current?.Value is { } bytes ? Decode(bytes) : null;
            if (existing?.Home != taken.Previous?.Home || existing?.Node != taken.Previous?.Node || existing is not null && existing.Epoch >= committed.Epoch)
            {
                Refused(HomeRefusal.LeaseChanged, "home");
                return false;
            }

            try
            {
                var revision = current is null
                    ? await kv.CreateAsync(HomeKey(id), Encode(committed), cancellationToken: cancellationToken).ConfigureAwait(false)
                    : await kv.UpdateAsync(HomeKey(id), Encode(committed), current.Value.Revision, cancellationToken: cancellationToken).ConfigureAwait(false);
                _homes[id] = (committed, revision);
                leaseGate.Lease(id, held: true);
                return true;
            }
            catch (Exception error) when (error is NatsKVWrongLastRevisionException or NatsKVCreateException)
            {
                logger.ZLogInformation($"The previous home's lease changed while a home move committed; it is read again");
            }
        }
    }

    internal async ValueTask<bool?> CommittedAsync(TenantContext database, long version, CancellationToken cancellationToken)
    {
        if (_js is null)
        {
            return await catalog.FindAsync(database, cancellationToken).ConfigureAwait(false) is TenantDatabaseRecord { Placement: var placement }
                && placement.Home == options.Region && placement.Version >= version;
        }

        if (_kv is not { } kv || !Ready)
        {
            return null;
        }

        return await ReadAsync(kv, database.DatabaseId!, cancellationToken).ConfigureAwait(false) is { Value: { } bytes } && Decode(bytes) is { } lease
            && lease.Node == options.MachineId && lease.Epoch >= version && lease.Adopted is not null;
    }

    internal async ValueTask<long?> RestoreAsync(TenantContext database, PromotionSource source, long version, CancellationToken cancellationToken)
    {
        var id = database.DatabaseId!;
        leaseGate.Lease(id, held: false);
        if (_js is null)
        {
            return 0;
        }

        if (_kv is not { } kv || !Ready)
        {
            throw new IOException("the home lease store is not ready; the rollback is settled again");
        }

        var entry = await ReadAsync(kv, id, cancellationToken).ConfigureAwait(false);
        var current = entry?.Value is { } bytes ? Decode(bytes) : null;
        if (current is null || current.Home != source.Region || current.Node != source.Node)
        {
            logger.ZLogWarning($"A home change is not rolled back: the lease names {current?.Node ?? "no node":@Node} in {current?.Home ?? "no region":@Home} at epoch {current?.Epoch ?? 0:@Epoch}, not the previous home {source.Node:@Previous}; this node keeps its copy fenced and the previous home keeps its file");
            return null;
        }

        if (current.Epoch > version)
        {
            logger.ZLogWarning($"A home change was rolled back without rewriting the lease: it already names {current.Node:@Node} in {current.Home:@Home} at epoch {current.Epoch:@Epoch}");
            return current.Epoch;
        }

        var restored = current with { Epoch = version + 1 };
        try
        {
            var revision = await kv.UpdateAsync(HomeKey(id), Encode(restored), entry!.Value.Revision, cancellationToken: cancellationToken).ConfigureAwait(false);
            _homes[id] = (restored, revision);
        }
        catch (NatsKVWrongLastRevisionException error)
        {
            Refused(HomeRefusal.LeaseChanged, "home");
            throw new IOException("the home lease changed during the rollback; it is settled again", error);
        }

        logger.ZLogWarning($"A home change was rolled back: {source.Node:@Node} in {source.Region:@Home} is the home again at epoch {restored.Epoch:@Epoch}");
        return restored.Epoch;
    }

    internal async Task<bool> RemoveIfProvenAsync(TenantContext database, KeepReason unproven, CancellationToken cancellationToken)
    {
        var id = database.DatabaseId!;
        while (engine.HasDatabaseFiles(database, cancellationToken))
        {
            NatsKVEntry<byte[]>? proof = null;
            HomeLease? lease = null;
            DatabaseRevision? mine = null;
            if (_js is not null)
            {
                proof = _kv is { } kv ? await ReadAsync(kv, id, cancellationToken).ConfigureAwait(false) : null;
                lease = proof?.Value is { } bytes ? Decode(bytes) : null;
                mine = await revisions.CurrentRevisionAsync(database, cancellationToken).ConfigureAwait(false);
                if (lease is null || lease.Node == options.MachineId || lease.Adopted is null)
                {
                    leaseGate.Lease(id, held: false);
                    KeptInPlace(id, lease);
                    return false;
                }

                if (!Proves(lease, mine))
                {
                    if (await kept.KeepAsync(database, unproven, cancellationToken).ConfigureAwait(false))
                    {
                        fences.Release(id);
                        leaseGate.Unlease(id);
                    }

                    return false;
                }

                if (await ReadAsync(_kv!, id, cancellationToken).ConfigureAwait(false) is not { } again || again.Revision != proof!.Value.Revision)
                {
                    continue;
                }
            }

            try
            {
                fences.Release(id);
                replicas.ArchiveCompletedImport(database);
                _ = await engine.DeleteDatabaseAsync(database, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or InvalidOperationException)
            {
                logger.ZLogWarning($"A former home's proven file was not deleted; it stays on disk: {error.GetType().Name:@Error}");
                return false;
            }

            leaseGate.Unlease(id);
            _demotions?.Add(1);
            logger.ZLogWarning($"This node no longer homes a database: {lease?.Node ?? "the new home":@Node} in {lease?.Home ?? "its region":@Home} proved at lease revision {proof?.Revision ?? 0:@Revision} that it holds {mine?.ToString() ?? "this node's final position":@Position}; the file is deleted");
            return true;
        }

        return false;
    }

    private void KeptInPlace(string id, HomeLease? lease)
    {
        if (_inPlace.TryGetValue(id, out var epoch) && epoch == (lease?.Epoch ?? 0))
        {
            return;
        }

        _inPlace[id] = lease?.Epoch ?? 0;
        logger.ZLogWarning($"Database {id:@Database} keeps its file in place and fenced: the lease names {lease?.Node ?? "no node":@Node} in {lease?.Home ?? "no region":@Home} at epoch {lease?.Epoch ?? 0:@Epoch} with no adopted position, so no other copy is proven to hold this node's writes");
    }

    private static bool Proves(HomeLease lease, DatabaseRevision? mine) =>
        mine is { } final && DatabaseRevision.TryParse(lease.Adopted, out var adopted)
            && adopted.Incarnation == final.Incarnation && DatabaseRevision.Compare(adopted, final) >= 0;

    private async Task<IReadOnlySet<string>> MovingAsync(CancellationToken cancellationToken) =>
        (await moves.PendingHomeMovesAsync(cancellationToken).ConfigureAwait(false)).Select(marker => marker.DatabaseId).ToHashSet(StringComparer.Ordinal);

    internal HomeLease? Find(string database) => _homes.TryGetValue(database, out var entry) ? entry.Lease : null;

    internal async Task<bool> TakeRegionalAsync(TenantContext database, string source, CancellationToken cancellationToken)
    {
        if (await catalog.FindAsync(database, cancellationToken).ConfigureAwait(false) is not TenantDatabaseRecord { Placement: var placement }
            || placement.Home != options.Region)
        {
            leaseGate.Lease(database.DatabaseId!, held: false);
            return true;
        }
        return await TakeRegionalCoreAsync(database, placement, source, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<bool> TakeRetainedRegionalAsync(DeletedDatabaseRecord record, string source, CancellationToken cancellationToken)
    {
        var store = await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        var current = await SystemCatalog.GetAsync(transaction, DeletedDatabases.Kind, record.Database.Id,
            ManagementJsonContext.Default.DeletedDatabaseRecord, cancellationToken).ConfigureAwait(false);
        if (current is null || current.DeletedAt != record.DeletedAt || !DatabasePurge.SameDatabase(current.Storage, record.Storage)
            || current.Database.Home(options.Region) != options.Region
            || await transaction.GetAsync(DatabasePurge.Kind, record.Database.Id, cancellationToken).ConfigureAwait(false) is not null)
        {
            return false;
        }
        return await TakeRegionalCoreAsync(current.Storage, current.Database.Placement(options.Region), source, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TakeRegionalCoreAsync(TenantContext database, DatabasePlacement placement, string source, CancellationToken cancellationToken)
    {
        if (placement is { AllRegions: false, Replicas.Count: 0 })
        {
            leaseGate.Unlease(database.DatabaseId!);
            return true;
        }

        if (_kv is not { } kv || !Ready)
        {
            return false;
        }

        await _confirming.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            NatsKVEntry<byte[]>? current;
            try
            {
                current = await kv.GetEntryAsync<byte[]>(HomeKey(database.DatabaseId!), cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is NatsKVKeyNotFoundException or NatsKVKeyDeletedException)
            {
                current = null;
            }

            var previous = current?.Value is { } bytes ? Decode(bytes) : null;
            if (previous is not null && (previous.OrganizationId != database.OrganizationId || previous.ProjectId != database.ProjectId
                || previous.DatabaseId != database.DatabaseId || previous.Epoch < placement.Version))
            {
                Isolate(previous, current!.Value.Revision);
                return true;
            }
            if (previous is not null && previous.Home != options.Region)
            {
                leaseGate.Lease(database.DatabaseId!, held: false);
                fences.Moved(database.DatabaseId!, previous.Home);
                if (previous.Adopted is null && engine.HasDatabaseFiles(database, cancellationToken))
                {
                    KeptInPlace(database.DatabaseId!, previous);
                    return true;
                }

                await catalog.UpdatePlacementAsync(database, Moved(placement, previous), cancellationToken).ConfigureAwait(false);
                replication.Resume(database);
                replication.WakeCatalog(previous.Home);
                return true;
            }
            if (previous is not null && previous.Node != source && previous.Node != options.MachineId)
            {
                Isolate(previous, current!.Value.Revision);
                return true;
            }

            var next = new HomeLease(database.OrganizationId, database.ProjectId!, database.DatabaseId!, options.Region,
                options.MachineId, (previous?.Epoch ?? 0) + 1, _session, previous?.Adopted);
            var value = JsonSerializer.SerializeToUtf8Bytes(next, ReplicasJsonContext.Default.HomeLease);
            var revision = current is null
                ? await kv.CreateAsync(HomeKey(next.DatabaseId), value, cancellationToken: cancellationToken).ConfigureAwait(false)
                : await kv.UpdateAsync(HomeKey(next.DatabaseId), value, current.Value.Revision, cancellationToken: cancellationToken).ConfigureAwait(false);
            _homes[next.DatabaseId] = (next, revision);
            leaseGate.Lease(next.DatabaseId, held: true);
            return true;
        }
        catch (Exception error) when (error is NatsKVWrongLastRevisionException or NatsKVCreateException)
        {
            _homes.TryRemove(database.DatabaseId!, out _);
            leaseGate.Lease(database.DatabaseId!, held: false);
            return true;
        }
        finally
        {
            _confirming.Release();
        }

        void Isolate(HomeLease conflicting, ulong revision)
        {
            _isolated[database.DatabaseId!] = revision;
            _ = _homes.AddOrUpdate(database.DatabaseId!, (conflicting, revision),
                (_, known) => known.Revision < revision ? (conflicting, revision) : known);
            leaseGate.Lease(database.DatabaseId!, held: false);
            logger.ZLogWarning($"A conflicting database home lease is isolated; other regional databases may activate");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_js is not { } js || _nats is not { } nats)
        {
            return;
        }

        while (_kv is null)
        {
            try
            {
                await nats.ConnectAsync().ConfigureAwait(false);
                _kv = await new NatsKVContext(js).CreateOrUpdateStoreAsync(new NatsKVConfig(Bucket)
                {
                    History = 1,
                    Storage = NatsKVStorageType.File,
                    NumberOfReplicas = nats.ServerInfo?.Cluster is { Length: > 0 } ? 3 : 1,
                    LimitMarkerTTL = Ttl,
                }, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                logger.ZLogWarning($"The home lease bucket is not ready; databases with replicas keep their current home: {error.GetType().Name:@Error}");
                await Task.Delay(Backoff, leaseGate.Time, stoppingToken).ConfigureAwait(false);
            }
        }

        await Task.WhenAll(RenewAsync(js, _kv, stoppingToken), WatchAsync(_kv, stoppingToken), ScanAsync(_kv, stoppingToken)).ConfigureAwait(false);
    }

    private async Task ScanAsync(INatsKVStore kv, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(ScanEvery, leaseGate.Time);
        do
        {
            if (_nodeRevision is not null)
            {
                try
                {
                    await ConfirmAllAsync(kv, cancellationToken).ConfigureAwait(false);
                    await TrackUnservedAsync(cancellationToken).ConfigureAwait(false);
                    foreach (var home in _homes.Values.Select(entry => entry.Lease).DistinctBy(home => home.Node))
                    {
                        if (home.Node != options.MachineId && !_nodes.ContainsKey(home.Node))
                        {
                            Lapsed(home.Node);
                        }
                    }
                }
                catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    logger.ZLogInformation($"Home leases are confirmed again on the next scan: {error.GetType().Name:@Error}");
                }
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }

    private async Task FenceHomedAsync(CancellationToken cancellationToken)
    {
        TenantContext? after = null;
        do
        {
            if (await catalog.ListHomedAsync(options.Region, after, cancellationToken).ConfigureAwait(false) is not TenantDatabasePage page)
            {
                logger.ZLogWarning($"The catalog could not be read at start; databases with replicas are fenced on the first lease scan");
                return;
            }

            foreach (var database in page.Databases)
            {
                if (await catalog.FindAsync(database, cancellationToken).ConfigureAwait(false) is TenantDatabaseRecord { Placement: var placement }
                    && placement.Home == options.Region && (placement.AllRegions || placement.Replicas.Count > 0))
                {
                    leaseGate.Lease(database.DatabaseId!, held: false);
                }
            }

            after = page.Next;
        }
        while (after is not null);
    }

    private async Task ConfirmAllAsync(INatsKVStore kv, CancellationToken cancellationToken)
    {
        if (!owners.Writer)
        {
            return;
        }

        var moving = await MovingAsync(cancellationToken).ConfigureAwait(false);
        await _confirming.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TenantContext? after = null;
            do
            {
                if (await catalog.ListHomedAsync(options.Region, after, cancellationToken).ConfigureAwait(false) is not TenantDatabasePage page)
                {
                    return;
                }

                foreach (var database in page.Databases)
                {
                    if (await catalog.FindAsync(database, cancellationToken).ConfigureAwait(false) is not TenantDatabaseRecord { Placement: var placement }
                        || placement.Home != options.Region)
                    {
                        continue;
                    }

                    if (placement is { AllRegions: false, Replicas.Count: 0 })
                    {
                        leaseGate.Unlease(database.DatabaseId!);
                        continue;
                    }

                    await ConfirmAsync(kv, database, placement, moving, cancellationToken).ConfigureAwait(false);
                }

                after = page.Next;
            }
            while (after is not null);
        }
        finally
        {
            _confirming.Release();
        }
    }

    private async Task ConfirmAsync(INatsKVStore kv, TenantContext database, DatabasePlacement placement, IReadOnlySet<string> moving, CancellationToken cancellationToken)
    {
        var id = database.DatabaseId!;
        if (!owners.Writer || fences.Holds(id) || moving.Contains(id))
        {
            return;
        }

        if (!_isolated.ContainsKey(id) && _homes.TryGetValue(id, out var known) && known.Lease is { Node: var node, Session: var session } && node == options.MachineId && session == _session)
        {
            leaseGate.Lease(id, held: true);
            return;
        }

        if (!leaseGate.Leased.Contains(id))
        {
            leaseGate.Lease(id, held: false);
        }

        var current = await ReadAsync(kv, id, cancellationToken).ConfigureAwait(false);
        if (_isolated.TryGetValue(id, out var conflict))
        {
            if (current?.Revision == conflict)
            {
                leaseGate.Lease(id, held: false);
                return;
            }
            _isolated.TryRemove(id, out _);
        }

        var existing = current?.Value is { } bytes ? Decode(bytes) : null;
        if (existing is not null && existing.Node != options.MachineId)
        {
            if (existing.Epoch >= placement.Version)
            {
                await DemoteAsync(database, cancellationToken).ConfigureAwait(false);
                return;
            }

            leaseGate.Lease(id, held: false);
            if (!_foreign.TryGetValue(id, out var seen) || seen != current!.Value.Revision)
            {
                _foreign[id] = current!.Value.Revision;
                Refused(HomeRefusal.Unproven, "home");
                logger.ZLogWarning($"Home lease not taken over: database {id:@Database} is recorded here at version {placement.Version:@Version}, but its lease names {existing.Node:@Node} in {existing.Home:@Home} at epoch {existing.Epoch:@Epoch}; this node writes neither the lease nor the database without proof");
            }

            return;
        }

        _ = _foreign.TryRemove(id, out _);
        var lease = new HomeLease(database.OrganizationId, database.ProjectId!, id, options.Region, options.MachineId, Math.Max(placement.Version, existing?.Epoch ?? 0), _session, existing?.Adopted);
        try
        {
            var revision = current is null
                ? await kv.CreateAsync(HomeKey(id), Encode(lease), cancellationToken: cancellationToken).ConfigureAwait(false)
                : await kv.UpdateAsync(HomeKey(id), Encode(lease), current.Value.Revision, cancellationToken: cancellationToken).ConfigureAwait(false);
            _homes[id] = (lease, revision);
            leaseGate.Lease(id, held: true);
        }
        catch (Exception error) when (error is NatsKVWrongLastRevisionException or NatsKVCreateException)
        {
            logger.ZLogInformation($"A home lease changed while this node confirmed it; it is read again");
        }
    }

    private async Task WatchAsync(INatsKVStore kv, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var entry in kv.WatchAsync<byte[]>(cancellationToken: cancellationToken).ConfigureAwait(false))
                {
                    await ObserveAsync(entry, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.ZLogInformation($"The home lease watch restarts: {error.GetType().Name:@Error}");
                await Task.Delay(Backoff, leaseGate.Time, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ObserveAsync(NatsKVEntry<byte[]> entry, CancellationToken cancellationToken)
    {
        if (entry.Key.StartsWith("node.", StringComparison.Ordinal))
        {
            var node = entry.Key["node.".Length..];
            if (entry.Operation == NatsKVOperation.Put && entry.Value is { } value
                && JsonSerializer.Deserialize(value, ReplicasJsonContext.Default.NodeLease) is { } lease)
            {
                _nodes[node] = lease;
            }
            else if (_nodes.TryRemove(node, out _) && node != options.MachineId)
            {
                Lapsed(node);
            }

            return;
        }

        if (!entry.Key.StartsWith("home.", StringComparison.Ordinal) || entry.Operation != NatsKVOperation.Put || entry.Value is not { } bytes
            || Decode(bytes) is not { } home)
        {
            return;
        }

        if (PlatformDatabase(home.DatabaseId) || entry.Key != HomeKey(home.DatabaseId))
        {
            return;
        }
        var database = new TenantContext(home.OrganizationId, PlanTier.Hobby, home.ProjectId, home.DatabaseId);
        var recorded = await catalog.FindAsync(database, cancellationToken).ConfigureAwait(false) is TenantDatabaseRecord { Placement: var found } ? found : null;
        if (recorded is not null && RegionOnly(recorded))
        {
            return;
        }

        var observed = _homes.AddOrUpdate(home.DatabaseId, (home, entry.Revision),
            (_, known) => entry.Revision > known.Revision ? (home, entry.Revision) : known);
        if (observed.Revision != entry.Revision)
        {
            return;
        }
        if (home.Node == options.MachineId)
        {
            if (recorded is { } own && own.Home == options.Region && own.Version < home.Epoch)
            {
                _ = await moves.FollowHomeLeaseAsync(database, Moved(own, home), cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        if (leaseGate.Leased.Contains(home.DatabaseId))
        {
            if (recorded is null || recorded.Version <= home.Epoch)
            {
                await DemoteAsync(database, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (recorded is { } placement && (placement.Home != home.Home || placement.Version < home.Epoch) && placement.Version <= home.Epoch)
        {
            await catalog.UpdatePlacementAsync(database, Moved(placement, home), cancellationToken).ConfigureAwait(false);
            replication.WakeCatalog(home.Home);
        }

        if (!_nodes.ContainsKey(home.Node))
        {
            Lapsed(home.Node);
        }
    }

    private void Lapsed(string node)
    {
        foreach (var (id, (lease, _)) in _homes)
        {
            if (lease.Node != node || PlatformDatabase(id) || _lapsed.TryGetValue(id, out var session) && session == lease.Session)
            {
                continue;
            }

            _lapsed[id] = lease.Session;
            _lapses?.Add(1);
            logger.ZLogWarning($"Home lease lapsed, not moved: database {id:@Database} stays homed in {lease.Home:@Home} on {lease.Node:@Node}; writes that need its home wait for it");
        }
    }

    private async Task DemoteAsync(TenantContext database, CancellationToken cancellationToken)
    {
        var id = database.DatabaseId!;
        if (PlatformDatabase(id) || fences.Holds(id) || _kv is not { } kv
            || await catalog.FindAsync(database, cancellationToken).ConfigureAwait(false) is not TenantDatabaseRecord { Placement: var placement }
            || RegionOnly(placement)
            || (await MovingAsync(cancellationToken).ConfigureAwait(false)).Contains(id)
            || await ReadAsync(kv, id, cancellationToken).ConfigureAwait(false) is not { Value: { } bytes } || Decode(bytes) is not { } home
            || home.Node == options.MachineId || placement.Version > home.Epoch)
        {
            return;
        }

        if (home.Home == options.Region || !owners.Writer)
        {
            leaseGate.Lease(id, held: false);
            return;
        }

        fences.Moved(id, home.Home);
        leaseGate.Lease(id, held: false);
        if (home.Adopted is null)
        {
            KeptInPlace(id, home);
            return;
        }

        if (placement.Home == options.Region)
        {
            _ = await moves.FollowHomeLeaseAsync(database, Moved(placement, home), cancellationToken).ConfigureAwait(false);
            if (await catalog.FindAsync(database, cancellationToken).ConfigureAwait(false) is not TenantDatabaseRecord { Placement.Home: var recordedHome }
                || recordedHome == options.Region)
            {
                return;
            }
        }
        else
        {
            await catalog.UpdatePlacementAsync(database, Moved(placement, home), cancellationToken).ConfigureAwait(false);
        }

        _ = await RemoveIfProvenAsync(database, KeepReason.LeaseUnproven, cancellationToken).ConfigureAwait(false);
        replication.WakeCatalog(home.Home);
    }

    private async Task TrackUnservedAsync(CancellationToken cancellationToken)
    {
        foreach (var (id, (lease, _)) in _homes)
        {
            var database = new TenantContext(lease.OrganizationId, PlanTier.Hobby, lease.ProjectId, id);
            var cause = PlatformDatabase(id) || await catalog.FindAsync(database, cancellationToken).ConfigureAwait(false) is not TenantDatabaseRecord ? null
                : !_nodes.ContainsKey(lease.Node) ? "lapsed"
                : owners.Find(lease.Home) is { } owner && owner.Node != lease.Node ? "not_writer"
                : null;
            if (cause is null)
            {
                if (_unserved.TryRemove(id, out var was))
                {
                    logger.ZLogWarning($"Database {id:@Database} is served again: its lease names {lease.Node:@Node} in {lease.Home:@Home} at epoch {lease.Epoch:@Epoch} (it was unserved: {was.Cause:@Cause})");
                }

                continue;
            }

            var unserved = new UnservedDatabase(id, lease.Home, lease.Node, lease.Epoch, cause);
            if (_unserved.TryGetValue(id, out var known) && known == unserved)
            {
                continue;
            }

            _unserved[id] = unserved;
            logger.ZLogWarning($"Database {id:@Database} is unserved: its lease names {lease.Node:@Node} in {lease.Home:@Home} at epoch {lease.Epoch:@Epoch}, which cannot serve it ({cause:@Cause}); nothing else serves it as home until an operator acts");
        }
    }

    private static DatabasePlacement Moved(DatabasePlacement placement, HomeLease home) => placement.AllRegions
        ? new DatabasePlacement(home.Home, [], home.Epoch, AllRegions: true)
        : new DatabasePlacement(home.Home, [.. placement.Replicas.Append(placement.Home).Where(region => region != home.Home).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)], home.Epoch);

    private static byte[] Encode(HomeLease lease) => JsonSerializer.SerializeToUtf8Bytes(lease, ReplicasJsonContext.Default.HomeLease);

    private static async Task<NatsKVEntry<byte[]>?> ReadAsync(INatsKVStore kv, string database, CancellationToken cancellationToken)
    {
        try
        {
            return await kv.GetEntryAsync<byte[]>(HomeKey(database), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is NatsKVKeyNotFoundException or NatsKVKeyDeletedException)
        {
            return null;
        }
    }

    private static HomeLease? Decode(byte[] bytes)
    {
        try
        {
            return JsonSerializer.Deserialize(bytes, ReplicasJsonContext.Default.HomeLease);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
