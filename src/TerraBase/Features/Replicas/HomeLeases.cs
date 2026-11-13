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

internal sealed record HomeLeaseTaken(HomeLease? Previous, HomeLease Taken, ulong Revision);

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
}

internal interface IHomeWriteLease
{
    ValueTask<bool> PrepareAsync(TenantContext database, CancellationToken cancellationToken);
}

internal sealed partial class HomeLeases(
    TerraBaseOptions options, IReplicaCatalog catalog, DatabaseFences fences, HomeLeaseGate leaseGate, RegionReplication replication, IStorageEngine engine,
    IDatabaseRevisions revisions, IHomeMoveCatalog moves, IMeterFactory meters, ILogger<HomeLeases> logger, RegionalOwners owners) : BackgroundService, IHomeWriteLease
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
    private readonly ConcurrentDictionary<string, HomeLease> _kept = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _confirming = new(1, 1);
    private readonly Meter _meter = meters.Create(Telemetry.MeterName);
    private NatsConnection? _nats;
    private NatsJSContext? _js;
    private INatsKVStore? _kv;
    private string _session = Ulid.NewUlid().ToString();
    private ulong? _nodeRevision;
    private Counter<long>? _lapses;
    private Counter<long>? _refusals;
    private Counter<long>? _reclaims;
    private Counter<long>? _keptCopies;
    private Counter<long>? _demotions;

    private string NodeKey => "node." + options.MachineId;

    private static string HomeKey(string database) => "home." + database;

    private static bool RegionOnly(DatabasePlacement placement) => !placement.AllRegions && placement.Replicas.Count == 0;

    private static bool PlatformDatabase(string database) => database is PlatformDatabases.AuthId or PlatformDatabases.DashboardId;

    private static string Reason(HomeRefusal refusal) => refusal switch
    {
        HomeRefusal.NotWriter => "not_writer",
        HomeRefusal.LeaseChanged => "lease_changed",
        HomeRefusal.PlacementRefused => "placement_refused",
        HomeRefusal.PrepareFailed => "prepare_failed",
        HomeRefusal.ReplicaNewer => "replica_newer",
        HomeRefusal.PrimaryMissing => "primary_missing",
        HomeRefusal.Unproven => "unproven",
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
            _reclaims = _meter.CreateCounter<long>("terrabase.home.reclaims", "{reclaim}", "Databases this node took back as home because the recorded home could not serve them.");
            _keptCopies = _meter.CreateCounter<long>("terrabase.home.kept", "{copy}", "Database files this node kept as a former home because the recorded home has not proven it holds them.");
            _demotions = _meter.CreateCounter<long>("terrabase.home.demotions", "{demotion}", "Databases this node stopped homing after their new home proved it holds their content.");
            _ = _meter.CreateObservableGauge("terrabase.home.leased", () => leaseGate.Leased.Count, "{database}", "Databases whose writes on this node need its home lease.");
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

    internal Action<TenantContext, long>? Reclaimed { get; set; }

    internal void Kept() => _keptCopies?.Add(1);

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

            await ConfirmAsync(kv, database, placement, cancellationToken).ConfigureAwait(false);
            return leaseGate.Holds(id);
        }
        finally
        {
            _confirming.Release();
        }
    }

    internal async ValueTask<HomeLeaseTaken?> TakeAsync(TenantContext database, string fromRegion, long epoch, DatabaseRevision adopted, CancellationToken cancellationToken)
    {
        var id = database.DatabaseId!;
        var taken = new HomeLease(database.OrganizationId, database.ProjectId!, id, options.Region, options.MachineId, epoch, _session, adopted.ToString());
        if (fromRegion == options.Region || PlatformDatabase(id)
            || await catalog.FindAsync(database, cancellationToken).ConfigureAwait(false) is not TenantDatabaseRecord { Placement: var placement }
            || RegionOnly(placement))
        {
            return null;
        }

        if (_js is null)
        {
            return new HomeLeaseTaken(null, taken, 0);
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

        try
        {
            var revision = current is null
                ? await kv.CreateAsync(HomeKey(id), Encode(taken), cancellationToken: cancellationToken).ConfigureAwait(false)
                : await kv.UpdateAsync(HomeKey(id), Encode(taken), current.Value.Revision, cancellationToken: cancellationToken).ConfigureAwait(false);
            _homes[id] = (taken, revision);
            leaseGate.Lease(id, held: true);
            return new HomeLeaseTaken(existing, taken, revision);
        }
        catch (Exception error) when (error is NatsKVWrongLastRevisionException or NatsKVCreateException)
        {
            Refused(HomeRefusal.LeaseChanged, "home");
            return null;
        }
    }

    internal async ValueTask<HomeLease?> RestoreAsync(TenantContext database, HomeLeaseTaken taken, CancellationToken cancellationToken)
    {
        var id = database.DatabaseId!;
        leaseGate.Unlease(id);
        if (taken.Previous is not { } previous)
        {
            _homes.TryRemove(id, out _);
            if (_kv is { } empty)
            {
                await empty.DeleteAsync(HomeKey(id), new NatsKVDeleteOpts { Revision = taken.Revision }, cancellationToken).ConfigureAwait(false);
            }

            return null;
        }

        var restored = previous with { Epoch = taken.Taken.Epoch + 1, Adopted = null };
        if (_kv is { } kv)
        {
            var revision = await kv.UpdateAsync(HomeKey(id), Encode(restored), taken.Revision, cancellationToken: cancellationToken).ConfigureAwait(false);
            _homes[id] = (restored, revision);
        }

        logger.ZLogWarning($"A home change was rolled back: {previous.Home:@Home} is the home again at epoch {restored.Epoch:@Epoch}");
        return restored;
    }

    internal async ValueTask<bool> ProvesAsync(TenantContext database, CancellationToken cancellationToken)
    {
        if (_js is null)
        {
            return true;
        }

        if (_kv is not { } kv || await ReadAsync(kv, database.DatabaseId!, cancellationToken).ConfigureAwait(false) is not { Value: { } bytes }
            || Decode(bytes) is not { } lease || lease.Node == options.MachineId)
        {
            return false;
        }

        return Proves(lease, await revisions.CurrentRevisionAsync(database, cancellationToken).ConfigureAwait(false));
    }

    private static bool Proves(HomeLease lease, DatabaseRevision? mine) =>
        mine is { } final && DatabaseRevision.TryParse(lease.Adopted, out var adopted)
            && adopted.Incarnation == final.Incarnation && DatabaseRevision.Compare(adopted, final) >= 0;

    internal HomeLease? Find(string database) => _homes.TryGetValue(database, out var entry) ? entry.Lease : null;

    internal async Task<bool> TakeRegionalAsync(TenantContext database, string source, CancellationToken cancellationToken)
    {
        if (await catalog.FindAsync(database, cancellationToken).ConfigureAwait(false) is not TenantDatabaseRecord { Placement: var placement }
            || placement.Home != options.Region)
        {
            leaseGate.Lease(database.DatabaseId!, held: false);
            return true;
        }
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
                options.MachineId, (previous?.Epoch ?? 0) + 1, _session);
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

                    await ConfirmAsync(kv, database, placement, cancellationToken).ConfigureAwait(false);
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

    private async Task ConfirmAsync(INatsKVStore kv, TenantContext database, DatabasePlacement placement, CancellationToken cancellationToken)
    {
        if (!owners.Writer)
        {
            return;
        }

        var id = database.DatabaseId!;
        if (!_isolated.ContainsKey(id) && _homes.TryGetValue(id, out var known) && known.Lease is { Node: var node, Session: var session } && node == options.MachineId && session == _session)
        {
            leaseGate.Lease(id, held: true);
            return;
        }

        if (!leaseGate.Leased.Contains(id))
        {
            leaseGate.Lease(id, held: false);
        }

        NatsKVEntry<byte[]>? current;
        try
        {
            current = await kv.GetEntryAsync<byte[]>(HomeKey(id), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is NatsKVKeyNotFoundException or NatsKVKeyDeletedException)
        {
            current = null;
        }

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
        if (existing is not null && existing.Node != options.MachineId && existing.Epoch >= placement.Version)
        {
            await DemoteAsync(database, existing, cancellationToken).ConfigureAwait(false);
            return;
        }

        var lease = new HomeLease(database.OrganizationId, database.ProjectId!, id, options.Region, options.MachineId, Math.Max(placement.Version, existing?.Epoch ?? 0), _session);
        var value = JsonSerializer.SerializeToUtf8Bytes(lease, ReplicasJsonContext.Default.HomeLease);
        try
        {
            var revision = current is null
                ? await kv.CreateAsync(HomeKey(id), value, cancellationToken: cancellationToken).ConfigureAwait(false)
                : await kv.UpdateAsync(HomeKey(id), value, current.Value.Revision, cancellationToken: cancellationToken).ConfigureAwait(false);
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
            if ((recorded is null || recorded.Version <= home.Epoch) && !fences.Held.Contains(home.DatabaseId))
            {
                await DemoteAsync(database, home, cancellationToken).ConfigureAwait(false);
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

    private async Task DemoteAsync(TenantContext database, HomeLease home, CancellationToken cancellationToken)
    {
        var id = database.DatabaseId!;
        if (PlatformDatabase(id)
            || await catalog.FindAsync(database, cancellationToken).ConfigureAwait(false) is not TenantDatabaseRecord { Placement: var placement }
            || RegionOnly(placement))
        {
            return;
        }
        if (home.Home == options.Region || !owners.Writer)
        {
            leaseGate.Lease(id, held: false);
            return;
        }

        var mine = placement.Home == options.Region ? await revisions.CurrentRevisionAsync(database, cancellationToken).ConfigureAwait(false) : null;
        if (mine is not null && !Proves(home, mine))
        {
            if (placement.Version <= home.Epoch && owners.Find(home.Home) is { } owner && owner.Node != home.Node
                && await ReclaimAsync(database, placement, home, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            fences.Moved(id, home.Home);
            leaseGate.Lease(id, held: false);
            if (_kept.TryGetValue(id, out var known) && known == home)
            {
                return;
            }

            _kept[id] = home;
            _keptCopies?.Add(1);
            logger.ZLogWarning($"Old home kept its copy: database {id:@Database} is recorded on {home.Node:@Node} in {home.Home:@Home} at epoch {home.Epoch:@Epoch}, which has not proven it holds {mine.Value.ToString():@Revision}; nothing is deleted and this node does not serve it as home");
            return;
        }

        fences.Moved(id, home.Home);
        leaseGate.Unlease(id);
        _kept.TryRemove(id, out _);
        if (placement.Version <= home.Epoch)
        {
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
        }

        if (mine is not null)
        {
            try
            {
                fences.Release(id);
                _ = await engine.DeleteDatabaseAsync(database, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or InvalidOperationException)
            {
                logger.ZLogWarning($"A demoted home's file was not deleted; the orphan sweep removes it: {error.GetType().Name:@Error}");
            }

            _demotions?.Add(1);
            logger.ZLogWarning($"This node no longer homes a database: {home.Node:@Node} in {home.Home:@Home} proved it holds {mine.Value.ToString():@Revision} at epoch {home.Epoch:@Epoch}; it follows as a replica");
        }

        replication.WakeCatalog(home.Home);
    }

    private async Task<bool> ReclaimAsync(TenantContext database, DatabasePlacement placement, HomeLease recorded, CancellationToken cancellationToken)
    {
        var id = database.DatabaseId!;
        if (_kv is not { } kv || !owners.Writer
            || await ReadAsync(kv, id, cancellationToken).ConfigureAwait(false) is not { Value: { } bytes } current || Decode(bytes) != recorded)
        {
            return false;
        }

        var lease = new HomeLease(database.OrganizationId, database.ProjectId!, id, options.Region, options.MachineId, recorded.Epoch + 1, _session);
        ulong revision;
        try
        {
            revision = await kv.UpdateAsync(HomeKey(id), Encode(lease), current.Revision, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (NatsKVWrongLastRevisionException)
        {
            return false;
        }

        _homes[id] = (lease, revision);
        _ = await moves.FollowHomeLeaseAsync(database, Moved(placement, lease), cancellationToken).ConfigureAwait(false);
        _kept.TryRemove(id, out _);
        fences.Lift(id);
        leaseGate.Lease(id, held: true);
        _reclaims?.Add(1);
        logger.ZLogWarning($"This node took a database back as home: {recorded.Node:@Node} held its lease at epoch {recorded.Epoch:@Epoch} but is not the writer of {recorded.Home:@Region}, so it served nothing; this node's copy is kept and served at epoch {lease.Epoch:@Epoch}");
        replication.WakeCatalog(recorded.Home);
        Reclaimed?.Invoke(database, lease.Epoch);

        return true;
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
