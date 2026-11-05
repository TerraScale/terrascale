using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Client.KeyValueStore;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Revisions;
using TerraBase.Features.Management;
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

internal sealed class HomeLeases(
    TerraBaseOptions options, IReplicaCatalog catalog, IReplicaPeers peers, IDatabaseReplicas replicas, DatabaseFences fences, HomeLeaseGate leaseGate,
    RegionReplication replication, IStorageEngine engine, IHomeMoveCatalog moves, InternalCredential credential, IMeterFactory meters, ILogger<HomeLeases> logger,
    RegionalOwners? owners = null) : BackgroundService, IHomeWriteLease
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

        // Serialize with the scan so creation cannot finish just before its first confirmation fences active writes.
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
        var value = JsonSerializer.SerializeToUtf8Bytes(taken, TerraBaseJsonContext.Default.HomeLease);
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
            if (previous is not null && (previous.Home != options.Region || previous.Node != source && previous.Node != options.MachineId))
            {
                return false;
            }

            var next = new HomeLease(database.OrganizationId, database.ProjectId!, database.DatabaseId!, options.Region,
                options.MachineId, (previous?.Epoch ?? 0) + 1, _session);
            var value = JsonSerializer.SerializeToUtf8Bytes(next, TerraBaseJsonContext.Default.HomeLease);
            var revision = current is null
                ? await kv.CreateAsync(HomeKey(next.DatabaseId), value, cancellationToken: cancellationToken).ConfigureAwait(false)
                : await kv.UpdateAsync(HomeKey(next.DatabaseId), value, current.Value.Revision, cancellationToken: cancellationToken).ConfigureAwait(false);
            _homes[next.DatabaseId] = (next, revision);
            leaseGate.Lease(next.DatabaseId, held: true);
            return true;
        }
        finally
        {
            _confirming.Release();
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
                await Task.Delay(Backoff, TimeProvider.System, stoppingToken).ConfigureAwait(false);
            }
        }

        await Task.WhenAll(RenewAsync(js, _kv, stoppingToken), WatchAsync(_kv, stoppingToken), ScanAsync(_kv, stoppingToken)).ConfigureAwait(false);
    }

    private async Task RenewAsync(NatsJSContext js, INatsKVStore kv, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(RenewEvery, TimeProvider.System);
        do
        {
            var sent = TimeProvider.System.GetTimestamp();
            try
            {
                var lease = JsonSerializer.SerializeToUtf8Bytes(new NodeLease(options.MachineId, options.Region, _session), TerraBaseJsonContext.Default.NodeLease);
                var fresh = _nodeRevision is null;
                _nodeRevision = _nodeRevision is { } revision
                    ? await PublishNodeAsync(js, lease, revision, cancellationToken).ConfigureAwait(false)
                    : await TakeNodeAsync(js, kv, lease, cancellationToken).ConfigureAwait(false);
                leaseGate.Renewed(sent + (long)((Ttl - Margin).TotalSeconds * TimeProvider.System.TimestampFrequency));
                if (fresh)
                {
                    logger.ZLogInformation($"This node holds a new home lease session");
                    await ConfirmAllAsync(kv, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (NatsJSApiException error) when (error.Error.ErrCode == WrongLastSequence)
            {
                Lapse();
                logger.ZLogWarning($"This node's home lease expired; its databases with replicas refuse writes until it confirms each lease again");
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.ZLogInformation($"The home lease renewal failed; it retries: {error.GetType().Name:@Error}");
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }

    private void Lapse()
    {
        _nodeRevision = null;
        _session = Ulid.NewUlid().ToString();
        foreach (var database in leaseGate.Leased)
        {
            leaseGate.Lease(database, held: false);
        }
    }

    private async Task<ulong> TakeNodeAsync(NatsJSContext js, INatsKVStore kv, byte[] lease, CancellationToken cancellationToken)
    {
        try
        {
            return await kv.CreateAsync(NodeKey, lease, Ttl, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (NatsKVCreateException)
        {
            var current = await kv.GetEntryAsync<byte[]>(NodeKey, cancellationToken: cancellationToken).ConfigureAwait(false);
            return await PublishNodeAsync(js, lease, current.Revision, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ulong> PublishNodeAsync(NatsJSContext js, byte[] lease, ulong revision, CancellationToken cancellationToken)
    {
        var headers = new NatsHeaders { ["Nats-TTL"] = ((long)Ttl.TotalSeconds).ToString(CultureInfo.InvariantCulture) + "s" };
        var answer = await js.PublishAsync($"$KV.{Bucket}.{NodeKey}", lease, headers: headers,
            opts: new NatsJSPubOpts { ExpectedLastSubjectSequence = revision }, cancellationToken: cancellationToken).ConfigureAwait(false);
        answer.EnsureSuccess();
        return answer.Seq;
    }

    private async Task ScanAsync(INatsKVStore kv, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(ScanEvery, TimeProvider.System);
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
                            Expired(new NodeLease(home.Node, home.Home, home.Session), cancellationToken);
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
        if (owners is { Writer: false })
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
        if (owners is { Writer: false })
        {
            return;
        }

        var id = database.DatabaseId!;
        if (_homes.TryGetValue(id, out var known) && known.Lease is { Node: var node, Session: var session } && node == options.MachineId && session == _session)
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

        var existing = current?.Value is { } bytes ? Decode(bytes) : null;
        if (existing is not null && existing.Node != options.MachineId && existing.Epoch >= placement.Version)
        {
            await DemoteAsync(database, existing, cancellationToken).ConfigureAwait(false);
            return;
        }

        var lease = new HomeLease(database.OrganizationId, database.ProjectId!, id, options.Region, options.MachineId, Math.Max(placement.Version, existing?.Epoch ?? 0), _session);
        var value = JsonSerializer.SerializeToUtf8Bytes(lease, TerraBaseJsonContext.Default.HomeLease);
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
                await Task.Delay(Backoff, TimeProvider.System, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ObserveAsync(NatsKVEntry<byte[]> entry, CancellationToken cancellationToken)
    {
        if (entry.Key.StartsWith("node.", StringComparison.Ordinal))
        {
            var node = entry.Key["node.".Length..];
            if (entry.Operation == NatsKVOperation.Put && entry.Value is { } value
                && JsonSerializer.Deserialize(value, TerraBaseJsonContext.Default.NodeLease) is { } lease)
            {
                _nodes[node] = lease;
            }
            else if (_nodes.TryRemove(node, out var gone) && node != options.MachineId)
            {
                Expired(gone, cancellationToken);
            }

            return;
        }

        if (!entry.Key.StartsWith("home.", StringComparison.Ordinal) || entry.Operation != NatsKVOperation.Put || entry.Value is not { } bytes
            || Decode(bytes) is not { } home)
        {
            return;
        }

        _homes[home.DatabaseId] = (home, entry.Revision);
        var database = new TenantContext(home.OrganizationId, PlanTier.Hobby, home.ProjectId, home.DatabaseId);
        if (home.Node == options.MachineId)
        {
            return;
        }

        var recorded = await catalog.FindAsync(database, cancellationToken).ConfigureAwait(false) is TenantDatabaseRecord { Placement: var found } ? found : null;
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
            Expired(new NodeLease(home.Node, home.Home, home.Session), cancellationToken);
        }
    }

    private void Expired(NodeLease gone, CancellationToken cancellationToken)
    {
        foreach (var (id, (lease, _)) in _homes)
        {
            if (lease.Node == gone.Node && replicas.Holds(new TenantContext(lease.OrganizationId, PlanTier.Hobby, lease.ProjectId, id)) && _claiming.TryAdd(id, true))
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
                await Task.Delay(Grace, TimeProvider.System, cancellationToken).ConfigureAwait(false);
                if (_kv is null || !_homes.TryGetValue(lease.DatabaseId, out var known) || known.Lease != lease || _nodes.ContainsKey(lease.Node) || !Ready)
                {
                    return;
                }

                if (await catalog.FindAsync(database, cancellationToken).ConfigureAwait(false) is not TenantDatabaseRecord { Placement: var placement }
                    || replicas.HeldRevision(database) is not { } mine)
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
                && (Compare(theirs, mine) > 0 || Compare(theirs, mine) == 0 && string.CompareOrdinal(answer.Region, options.Region) < 0))
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
            using var response = await _http.GetAsync(new Uri(peer.Internal,
                $"/v1/sync/organizations/{database.OrganizationId}/projects/{database.ProjectId}/databases/{database.DatabaseId}/held"), timeout.Token).ConfigureAwait(false);
            return response.StatusCode == HttpStatusCode.OK
                ? await response.Content.ReadFromJsonAsync(TerraBaseJsonContext.Default.HeldRevisionAnswer, timeout.Token).ConfigureAwait(false)
                : null;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    internal static int Compare(DatabaseRevision left, DatabaseRevision right)
    {
        var incarnation = left.Incarnation.CompareTo(right.Incarnation);
        return incarnation != 0 ? incarnation : left.Generation != right.Generation ? left.Generation.CompareTo(right.Generation) : left.Frame.CompareTo(right.Frame);
    }

    private async Task DemoteAsync(TenantContext database, HomeLease home, CancellationToken cancellationToken)
    {
        var id = database.DatabaseId!;
        if (home.Home == options.Region)
        {
            // Same-region promotion changes the writer, never the database's placement.
            // Keep the old primary readable until its peer proves the final position.
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
            return JsonSerializer.Deserialize(bytes, TerraBaseJsonContext.Default.HomeLease);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
