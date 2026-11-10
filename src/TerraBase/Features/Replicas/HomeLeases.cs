using NATS.Client.Core;
using NATS.Client.JetStream.Models;
using NATS.Client.JetStream;
using NATS.Client.KeyValueStore;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
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

internal sealed record HomeLease(string OrganizationId, string ProjectId, string DatabaseId, string Home, string Node, long Epoch, string Session);

internal sealed record HeldRevisionAnswer(string Region, string? Revision);

internal interface IHomeWriteLease
{
    ValueTask<bool> PrepareAsync(TenantContext database, CancellationToken cancellationToken);
}

internal sealed partial class HomeLeases(
    TerraBaseOptions options, IReplicaCatalog catalog, IReplicaPeers peers, IDatabaseReplicas replicas, DatabaseFences fences, HomeLeaseGate leaseGate,
    RegionReplication replication, IStorageEngine engine, IHomeMoveCatalog moves, InternalCredential credential, IMeterFactory meters,
    ILogger<HomeLeases> logger, RegionalOwners owners) : BackgroundService, IHomeWriteLease
{
    internal const string Bucket = "terrabase-homes";
    internal static readonly TimeSpan Ttl = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan Grace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RenewEvery = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Margin = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ScanEvery = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AskTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Backoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StartScan = TimeSpan.FromSeconds(10);
    private const int ClaimRounds = 3;
    private const int WrongLastSequence = 10071;

    private readonly ConcurrentDictionary<string, (HomeLease Lease, ulong Revision)> _homes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ulong> _isolated = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, NodeLease> _nodes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _claiming = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _confirming = new(1, 1);
    private readonly HttpClient _http = PeerHttp.Create(credential);
    private readonly Meter _meter = meters.Create(Telemetry.MeterName);
    private NatsConnection? _nats;
    private NatsJSContext? _js;
    private INatsKVStore? _kv;
    private string _session = Ulid.NewUlid().ToString();
    private ulong? _nodeRevision;
    private Counter<long>? _failovers;
    private Counter<long>? _demotions;

    private string NodeKey => "node." + options.MachineId;

    private static string HomeKey(string database) => "home." + database;

    private static bool RegionOnly(DatabasePlacement placement) => !placement.AllRegions && placement.Replicas.Count == 0;

    private static bool PlatformDatabase(string database) => database is PlatformDatabases.AuthId or PlatformDatabases.DashboardId;

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
            _failovers = _meter.CreateCounter<long>("terrabase.home.failovers", "{failover}", "Databases this node took over as home after their home's lease expired.");
            _demotions = _meter.CreateCounter<long>("terrabase.home.demotions", "{demotion}", "Databases this node stopped homing because another node holds their lease.");
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
        _http.Dispose();
        _meter.Dispose();
        base.Dispose();
    }

    internal bool Ready => _kv is not null && _nodeRevision is not null;

    internal bool Active => _js is not null;

    internal Func<TenantContext, HomeLease, CancellationToken, Task<bool>>? FailOver { get; set; }

    public async ValueTask<bool> PrepareAsync(TenantContext database, CancellationToken cancellationToken)
    {
        if (_js is null)
        {
            return true;
        }

        if (_kv is not { } kv || !Ready)
        {
            return false;
        }

        await _confirming.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await catalog.FindAsync(database, cancellationToken).ConfigureAwait(false) is not TenantDatabaseRecord { Placement: var placement }
                || placement.Home != options.Region)
            {
                return false;
            }

            if (placement is { AllRegions: false, Replicas.Count: 0 })
            {
                return true;
            }

            await ConfirmAsync(kv, database, placement, cancellationToken).ConfigureAwait(false);
            return leaseGate.Holds(database.DatabaseId!);
        }
        finally
        {
            _confirming.Release();
        }
    }

    internal async ValueTask<bool> TakeAsync(TenantContext database, string fromRegion, long epoch, HomeLease? failedHome, CancellationToken cancellationToken)
    {
        if (fromRegion == options.Region || PlatformDatabase(database.DatabaseId!)
            || await catalog.FindAsync(database, cancellationToken).ConfigureAwait(false) is not TenantDatabaseRecord { Placement: var placement }
            || RegionOnly(placement))
        {
            return false;
        }

        if (_js is null)
        {
            return true;
        }

        if (_kv is not { } kv || !Ready)
        {
            return false;
        }

        var id = database.DatabaseId!;
        NatsKVEntry<byte[]>? current;
        try
        {
            current = await kv.GetEntryAsync<byte[]>(HomeKey(id), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is NatsKVKeyNotFoundException or NatsKVKeyDeletedException)
        {
            current = null;
        }

        var existing = current?.Value is { } bytes ? Decode(bytes) : null;
        if (failedHome is not null && existing != failedHome)
        {
            return false;
        }

        if (existing is not null && (existing.Home != fromRegion || existing.Epoch >= epoch || failedHome is not null && _nodes.ContainsKey(existing.Node)))
        {
            return false;
        }

        var taken = new HomeLease(database.OrganizationId, database.ProjectId!, id, options.Region, options.MachineId, epoch, _session);
        var value = JsonSerializer.SerializeToUtf8Bytes(taken, ReplicasJsonContext.Default.HomeLease);
        try
        {
            var revision = current is null
                ? await kv.CreateAsync(HomeKey(id), value, cancellationToken: cancellationToken).ConfigureAwait(false)
                : await kv.UpdateAsync(HomeKey(id), value, current.Value.Revision, cancellationToken: cancellationToken).ConfigureAwait(false);
            _homes[id] = (taken, revision);
            leaseGate.Lease(id, held: true);
            return true;
        }
        catch (Exception error) when (error is NatsKVWrongLastRevisionException or NatsKVCreateException)
        {
            logger.ZLogInformation($"Another node changed a home lease first; this node does not take it");
            return false;
        }
    }

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
                            await ExpiredAsync(new NodeLease(home.Node, home.Home, home.Session), cancellationToken).ConfigureAwait(false);
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
            else if (_nodes.TryRemove(node, out var gone) && node != options.MachineId)
            {
                await ExpiredAsync(gone, cancellationToken).ConfigureAwait(false);
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

        if (!_nodes.ContainsKey(home.Node) && home.Home != options.Region)
        {
            await ExpiredAsync(new NodeLease(home.Node, home.Home, home.Session), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ExpiredAsync(NodeLease gone, CancellationToken cancellationToken)
    {
        foreach (var (id, (lease, _)) in _homes)
        {
            if (lease.Home == options.Region || lease.Node != gone.Node || PlatformDatabase(id) || _isolated.ContainsKey(id))
            {
                continue;
            }
            var database = new TenantContext(lease.OrganizationId, PlanTier.Hobby, lease.ProjectId, id);
            if (await catalog.FindAsync(database, cancellationToken).ConfigureAwait(false) is TenantDatabaseRecord { Placement: var placement }
                && !RegionOnly(placement) && replicas.Holds(database) && _claiming.TryAdd(id, true))
            {
                _ = ClaimAsync(lease, cancellationToken);
            }
        }
    }

    private async Task ClaimAsync(HomeLease lease, CancellationToken cancellationToken)
    {
        var database = new TenantContext(lease.OrganizationId, PlanTier.Hobby, lease.ProjectId, lease.DatabaseId);
        try
        {
            for (var round = 0; round < ClaimRounds && !cancellationToken.IsCancellationRequested; round++)
            {
                await Task.Delay(Grace, leaseGate.Time, cancellationToken).ConfigureAwait(false);
                if (lease.Home == options.Region || _kv is null || !_homes.TryGetValue(lease.DatabaseId, out var known) || known.Lease != lease || _nodes.ContainsKey(lease.Node) || !Ready)
                {
                    return;
                }

                if (PlatformDatabase(database.DatabaseId!)
                    || await catalog.FindAsync(database, cancellationToken).ConfigureAwait(false) is not TenantDatabaseRecord { Placement: var placement }
                    || RegionOnly(placement) || replicas.HeldRevision(database) is not { } mine)
                {
                    return;
                }

                if (round < ClaimRounds - 1 && await AheadAsync(database, placement, lease.Home, mine, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                var epoch = lease.Epoch + 1;
                if (FailOver is { } failOver && await failOver(database, lease, cancellationToken).ConfigureAwait(false))
                {
                    _failovers?.Add(1);
                    logger.ZLogWarning($"This node took over a database from {lease.Home:@From} at epoch {epoch:@Epoch}, holding {mine.ToString():@Revision}");
                }
                else
                {
                    logger.ZLogInformation($"This replica did not take over a database; another node holds its lease or its home came back");
                }

                return;
            }
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.ZLogWarning(error, $"A failover attempt stopped");
        }
        finally
        {
            _ = _claiming.TryRemove(lease.DatabaseId, out _);
        }
    }

    private async Task<bool> AheadAsync(TenantContext database, DatabasePlacement placement, string home, DatabaseRevision mine, CancellationToken cancellationToken)
    {
        var others = placement.AllRegions ? null : placement.Replicas.ToHashSet(StringComparer.Ordinal);
        var peersList = await peers.ListAsync(cancellationToken).ConfigureAwait(false);
        var asks = peersList.Where(peer => peer.Region != home && peer.Region != options.Region && (others is null || others.Contains(peer.Region)))
            .Select(peer => HeldAsync(peer, database, cancellationToken));
        foreach (var answer in await Task.WhenAll(asks).ConfigureAwait(false))
        {
            if (answer is { Revision: { } text } && DatabaseRevision.TryParse(text, out var theirs)
                && theirs.CompareAcrossIncarnations(mine) is var order && (order > 0 || order == 0 && string.CompareOrdinal(answer.Region, options.Region) < 0))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<HeldRevisionAnswer?> HeldAsync(ReplicaPeer peer, TenantContext database, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(AskTimeout);
        try
        {
            using var response = await _http.GetAsync(new Uri(peer.Internal, ReplicaRoutes.DatabasePath(database, "held")), timeout.Token).ConfigureAwait(false);
            return response.StatusCode == HttpStatusCode.OK
                ? await response.Content.ReadFromJsonAsync(ReplicasJsonContext.Default.HeldRevisionAnswer, timeout.Token).ConfigureAwait(false)
                : null;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task DemoteAsync(TenantContext database, HomeLease home, CancellationToken cancellationToken)
    {
        var id = database.DatabaseId!;
        if (PlatformDatabase(id)
            || await catalog.FindAsync(database, cancellationToken).ConfigureAwait(false) is TenantDatabaseRecord { Placement: var local } && RegionOnly(local))
        {
            return;
        }
        if (home.Home == options.Region)
        {
            leaseGate.Lease(id, held: false);
            return;
        }

        fences.Moved(id, home.Home);
        leaseGate.Unlease(id);
        if (await catalog.FindAsync(database, cancellationToken).ConfigureAwait(false) is TenantDatabaseRecord { Placement: var placement } && placement.Version <= home.Epoch)
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
        logger.ZLogWarning($"This node no longer homes a database: {home.Home:@Home} holds its lease at epoch {home.Epoch:@Epoch}; it follows as a replica");
        replication.WakeCatalog(home.Home);
    }

    private static DatabasePlacement Moved(DatabasePlacement placement, HomeLease home) => placement.AllRegions
        ? new DatabasePlacement(home.Home, [], home.Epoch, AllRegions: true)
        : new DatabasePlacement(home.Home, [.. placement.Replicas.Append(placement.Home).Where(region => region != home.Home).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)], home.Epoch);

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
