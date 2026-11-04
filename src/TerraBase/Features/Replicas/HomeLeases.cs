using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.Json;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Client.KeyValueStore;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Replicas;

internal sealed record NodeLease(string Node, string Region, string Session);

internal sealed record HomeLease(string OrganizationId, string ProjectId, string DatabaseId, string Home, string Node, long Epoch, string Session);

internal sealed class HomeLeases(
    TerraBaseOptions options, IReplicaCatalog catalog, HomeLeaseGate fences, IStorageEngine engine, IMeterFactory meters, ILogger<HomeLeases> logger)
    : BackgroundService
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
    private readonly SemaphoreSlim _confirming = new(1, 1);
    private readonly Meter _meter = meters.Create(Telemetry.MeterName);
    private NatsConnection? _nats;
    private NatsJSContext? _js;
    private INatsKVStore? _kv;
    private string _session = Ulid.NewUlid().ToString();
    private ulong? _nodeRevision;
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
            _demotions = _meter.CreateCounter<long>("terrabase.home.demotions", "{demotion}", "Databases this node stopped homing because another node holds their lease.");
            _ = _meter.CreateObservableGauge("terrabase.home.leased", () => fences.Leased.Count, "{database}", "Databases whose writes on this node need its home lease.");
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

    internal HomeLease? Find(string database) => _homes.TryGetValue(database, out var entry) ? entry.Lease : null;

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
                fences.Renewed(sent + (long)((Ttl - Margin).TotalSeconds * TimeProvider.System.TimestampFrequency));
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
        foreach (var database in fences.Leased)
        {
            fences.Lease(database, held: false);
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
                    fences.Lease(database.DatabaseId!, held: false);
                }
            }

            after = page.Next;
        }
        while (after is not null);
    }

    private async Task ConfirmAllAsync(INatsKVStore kv, CancellationToken cancellationToken)
    {
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
                        fences.Unlease(database.DatabaseId!);
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
        var id = database.DatabaseId!;
        if (_homes.TryGetValue(id, out var known) && known.Lease is { Node: var node, Session: var session } && node == options.MachineId && session == _session)
        {
            fences.Lease(id, held: true);
            return;
        }

        if (!fences.Leased.Contains(id))
        {
            fences.Lease(id, held: false);
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
            fences.Lease(id, held: true);
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
        if (fences.Leased.Contains(home.DatabaseId))
        {
            if (recorded is null || recorded.Version <= home.Epoch)
            {
                await DemoteAsync(database, home, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (recorded is { } placement && (placement.Home != home.Home || placement.Version < home.Epoch) && placement.Version <= home.Epoch)
        {
            await catalog.UpdatePlacementAsync(database, Moved(placement, home), cancellationToken).ConfigureAwait(false);
            }
    }

    private async Task DemoteAsync(TenantContext database, HomeLease home, CancellationToken cancellationToken)
    {
        var id = database.DatabaseId!;
        fences.Unlease(id);
        if (await catalog.FindAsync(database, cancellationToken).ConfigureAwait(false) is TenantDatabaseRecord { Placement: var placement } && placement.Version <= home.Epoch)
        {
            await catalog.UpdatePlacementAsync(database, Moved(placement, home), cancellationToken).ConfigureAwait(false);
        }

        try
        {
            _ = await engine.DeleteDatabaseAsync(database, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            logger.ZLogWarning($"A demoted home's file was not deleted; the orphan sweep removes it: {error.GetType().Name:@Error}");
        }

        _demotions?.Add(1);
        logger.ZLogWarning($"This node no longer homes a database: {home.Home:@Home} holds its lease at epoch {home.Epoch:@Epoch}; it follows as a replica");
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
