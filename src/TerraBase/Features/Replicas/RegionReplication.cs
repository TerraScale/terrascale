using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using NATS.Client.Core;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Replicas;

/// <summary>
/// Region replicas (docs/terrabase/region-replicas.md). As a home, this node announces every newer revision of a database with
/// replica regions on NATS. As a replica region, it records the databases other regions' homes place here, keeps a replica of
/// each and pulls it whenever its home announces a revision, after a NATS reconnect, after a failed pull and every
/// <see cref="Sweep"/>. Star topology: every replica pulls from its home, page mode only, never pushing.
/// </summary>
internal sealed class RegionReplication(
    TerraBaseOptions options, IReplicaPeers peers, IReplicaCatalog catalog, IDatabaseReplicas replicas,
    IDatabaseRevisions revisions, InternalCredential credential, IMeterFactory meters, TimeProvider time, ILogger<RegionReplication> logger) : BackgroundService
{
    /// <summary>How often every replica pulls and every home's catalog is read again without a notice.</summary>
    internal static readonly TimeSpan Sweep = TimeSpan.FromSeconds(30);

    private const int MaxConcurrentPulls = 8;
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<(string Organization, string Project, string Database), Follower> _followers = new();
    private readonly ConcurrentDictionary<(string Organization, string Project, string Database), (TenantContext Database, long At)> _revised = new();
    private readonly Channel<bool> _publish = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Channel<string> _catalogWakes = Channel.CreateBounded<string>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly SemaphoreSlim _pulls = new(MaxConcurrentPulls, MaxConcurrentPulls);
    private readonly HttpClient _http = PeerHttp.Create(credential);
    private readonly Meter _meter = meters.Create(Telemetry.MeterName);
    private readonly Lock _gate = new();
    private IReadOnlyList<ReplicaPeer> _peers = [];
    private NatsConnection? _nats;
    private Histogram<double>? _lag;
    private Counter<long>? _pullCount;

    private IDatabaseReplicas Replicas => replicas;

    private ILogger Logger => logger;

    private TimeProvider Time => time;

    /// <summary>The open followers' databases, for tests and diagnostics.</summary>
    internal IReadOnlyCollection<TenantContext> Following => [.. _followers.Values.Select(follower => follower.Database)];

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _peers = await peers.ListAsync(cancellationToken).ConfigureAwait(false);
        if (_peers.Count > 0)
        {
            _lag = _meter.CreateHistogram<double>("terrabase.replica.lag", "ms",
                "Time from a home's commit, on its clock, to the replica holding it after the notice's pull.");
            _pullCount = _meter.CreateCounter<long>("terrabase.replica.pulls", "{pull}", "Replica pulls by result (changed, unchanged, failed).");
        }

        if (_peers.Count > 0 && options is { NatsUrl: { } natsUrl, NatsToken: { } natsToken })
        {
            _nats = new NatsConnection(new NatsOpts
            {
                Url = natsUrl.ToString(),
                Name = $"terrabase-{options.Region}-{options.MachineId}",
                AuthOpts = new NatsAuthOpts { Token = natsToken },
                MaxReconnectRetry = -1,
                ReconnectWaitMin = TimeSpan.FromMilliseconds(100),
                ReconnectWaitMax = TimeSpan.FromSeconds(2),
            });
            revisions.Revised += OnRevised;
        }
        else if (_peers.Count > 0)
        {
            // ts-orch does not supply NATS to every cell yet: replicas then follow their homes on the sweep alone.
            logger.ZLogWarning($"TS_NATS_URL or TERRABASE_NATS_TOKEN_FILE is missing; replicas pull every {Sweep.TotalSeconds} s instead of on each revision");
        }

        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        revisions.Revised -= OnRevised;
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        foreach (var follower in _followers.Values)
        {
            await follower.StopAsync().ConfigureAwait(false);
        }

        if (_nats is { } nats)
        {
            await nats.DisposeAsync().ConfigureAwait(false);
        }

        _http.Dispose();
    }

    public override void Dispose()
    {
        _pulls.Dispose();
        _meter.Dispose();
        base.Dispose();
    }

    /// <summary>Reads every home's catalog once and reconciles this region's replicas with it.</summary>
    internal async Task SyncCatalogsAsync(CancellationToken cancellationToken)
    {
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

    /// <summary>Has the replica of <paramref name="database"/> pull soon, as a revision notice would: a session here just wrote at its home.</summary>
    internal void PullSoon(TenantContext database)
    {
        if (database.ProjectId is { } project && database.DatabaseId is { } id
            && _followers.TryGetValue((database.OrganizationId, project, id), out var follower))
        {
            follower.Signal(0);
        }
    }

    /// <summary>Pulls every replica now, waiting for the pulls.</summary>
    internal async Task PullAllAsync(CancellationToken cancellationToken) =>
        await Task.WhenAll(_followers.Values.Select(follower => follower.PullNowAsync(cancellationToken))).ConfigureAwait(false);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_peers.Count == 0)
        {
            return;
        }

        await (_nats is { } nats
            ? Task.WhenAll(PublishAsync(nats, stoppingToken), SubscribeAsync(nats, stoppingToken), WatchConnectionAsync(nats, stoppingToken), CatalogAsync(stoppingToken))
            : CatalogAsync(stoppingToken)).ConfigureAwait(false);
    }

    // Home: called on the commit path, so it only marks the database.
    private void OnRevised(TenantContext database)
    {
        _revised[(database.OrganizationId, database.ProjectId!, database.DatabaseId!)] = (database, time.GetUtcNow().ToUnixTimeMilliseconds());
        _ = _publish.Writer.TryWrite(true);
    }

    private async Task PublishAsync(NatsConnection nats, CancellationToken cancellationToken)
    {
        await foreach (var _ in _publish.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var key in _revised.Keys)
            {
                if (!_revised.TryRemove(key, out var revised))
                {
                    continue;
                }

                try
                {
                    if (await catalog.FindAsync(revised.Database, cancellationToken).ConfigureAwait(false) is TenantDatabaseRecord { Placement: var placement }
                        && placement.Home == options.Region && placement.Replicas.Count > 0)
                    {
                        var notice = new ReplicaRevisionNotice(key.Organization, key.Project, key.Database, revised.At);
                        await nats.PublishAsync(ReplicaSubjects.Revision(options.Region, key.Database),
                            JsonSerializer.SerializeToUtf8Bytes(notice, TerraBaseJsonContext.Default.ReplicaRevisionNotice), cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    // Replicas catch up on their sweep; a notice is an accelerator, never the only path.
                    logger.ZLogInformation($"A revision notice was not published: {error.GetType().Name:@Error}");
                }
            }
        }
    }

    private async Task SubscribeAsync(NatsConnection nats, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var message in nats.SubscribeAsync<byte[]>(ReplicaSubjects.Revisions, cancellationToken: cancellationToken).ConfigureAwait(false))
                {
                    Notice(message.Subject, message.Data);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.ZLogInformation($"The revision subscription restarts: {error.GetType().Name:@Error}");
                await Task.Delay(MaxBackoff, time, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void Notice(string subject, byte[]? data)
    {
        if (subject.Split('.') is not ["terrabase", "revision", var home, _] || home == options.Region || data is null)
        {
            return;
        }

        ReplicaRevisionNotice? notice;
        try
        {
            notice = JsonSerializer.Deserialize(data, TerraBaseJsonContext.Default.ReplicaRevisionNotice);
        }
        catch (JsonException)
        {
            return;
        }

        if (notice is null)
        {
            return;
        }

        if (_followers.TryGetValue((notice.OrganizationId, notice.ProjectId, notice.DatabaseId), out var follower))
        {
            follower.Signal(notice.CommittedAt);
        }
        else
        {
            // A database new to this region, or one that is not replicated here: the home's catalog says which.
            _ = _catalogWakes.Writer.TryWrite(home);
        }
    }

    // After a reconnect every replica pulls: notices sent while disconnected are lost.
    private async Task WatchConnectionAsync(NatsConnection nats, CancellationToken cancellationToken)
    {
        var connected = false;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250), time);
        do
        {
            if (nats.ConnectionState != NatsConnectionState.Open)
            {
                try
                {
                    await nats.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2), time, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    if (connected)
                    {
                        logger.ZLogWarning($"NATS is unreachable; replicas pull on their sweep until it returns: {error.GetType().Name:@Error}");
                    }
                }
            }

            var open = nats.ConnectionState == NatsConnectionState.Open;
            if (open && !connected)
            {
                foreach (var follower in _followers.Values)
                {
                    follower.Signal(0);
                }
            }

            connected = open;
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }

    private async Task CatalogAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await SyncCatalogsAsync(cancellationToken).ConfigureAwait(false);
            foreach (var follower in _followers.Values)
            {
                follower.Signal(0);
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
                    && placement.Home == home.Region && placement.Replicas.Contains(options.Region);
                try
                {
                    if (replicated && (listed is null || listed.ContainsKey(key)))
                    {
                        Follow(key, database, home);
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

    private void Follow((string, string, string) key, TenantContext database, ReplicaPeer home)
    {
        lock (_gate)
        {
            if (!_followers.ContainsKey(key))
            {
                _followers[key] = Follower.Start(this, database, home);
            }
        }
    }

    // Every database the home places here, or null when the home cannot be read completely.
    private async Task<Dictionary<(string, string, string), ReplicaCatalogEntry>?> ListAsync(ReplicaPeer home, CancellationToken cancellationToken)
    {
        var listed = new Dictionary<(string, string, string), ReplicaCatalogEntry>();
        string? after = null;
        try
        {
            do
            {
                var address = new Uri(home.Internal, $"/v1/sync/regions/{options.Region}/databases" + (after is null ? "" : $"?after={Uri.EscapeDataString(after)}"));
                var page = await _http.GetFromJsonAsync(address, TerraBaseJsonContext.Default.ReplicaCatalogPage, cancellationToken).ConfigureAwait(false);
                if (page is null)
                {
                    return null;
                }

                foreach (var entry in page.Databases.Where(entry => entry.HomeRegion == home.Region && entry.ReplicaRegions.Contains(options.Region)))
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

    // A database the home no longer lists here: deleted there, or placed elsewhere now.
    private async Task ResolveMissingAsync(ReplicaPeer home, TenantContext database, CancellationToken cancellationToken)
    {
        var key = (database.OrganizationId, database.ProjectId!, database.DatabaseId!);
        var address = new Uri(home.Internal, $"/v1/sync/organizations/{database.OrganizationId}/projects/{database.ProjectId}/databases/{database.DatabaseId}/placement");
        using var response = await _http.GetAsync(address, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            await StopFollowingAsync(key, database, cancellationToken).ConfigureAwait(false);
            await catalog.ForgetAsync(database, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!response.IsSuccessStatusCode
            || await response.Content.ReadFromJsonAsync(TerraBaseJsonContext.Default.ReplicaCatalogEntry, cancellationToken).ConfigureAwait(false) is not { } entry)
        {
            return;
        }

        await catalog.UpdatePlacementAsync(database, new DatabasePlacement(entry.HomeRegion, entry.ReplicaRegions, entry.PlacementVersion), cancellationToken)
            .ConfigureAwait(false);
        if (!entry.ReplicaRegions.Contains(options.Region))
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

    /// <summary>One replica: opens it, then pulls once per batch of signals, retrying failed pulls with backoff.</summary>
    private sealed class Follower : IDisposable
    {
        private readonly RegionReplication _owner;
        private readonly Channel<bool> _signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        private readonly CancellationTokenSource _stop = new();
        private readonly ReplicaPeer _home;
        private long _noticed;
        private TaskCompletionSource _pulled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task _loop = Task.CompletedTask;

        private Follower(RegionReplication owner, TenantContext database, ReplicaPeer home)
        {
            _owner = owner;
            Database = database;
            _home = home;
        }

        public TenantContext Database { get; }

        public static Follower Start(RegionReplication owner, TenantContext database, ReplicaPeer home)
        {
            var follower = new Follower(owner, database, home);
            follower._loop = Task.Run(follower.RunAsync);
            return follower;
        }

        public void Signal(long committedAt)
        {
            if (committedAt > 0)
            {
                // The oldest unpulled commit times the lag of the pull that delivers it.
                _ = Interlocked.CompareExchange(ref _noticed, committedAt, 0);
            }

            _ = _signals.Writer.TryWrite(true);
        }

        public Task PullNowAsync(CancellationToken cancellationToken)
        {
            var pulled = Volatile.Read(ref _pulled).Task;
            Signal(0);
            return pulled.WaitAsync(cancellationToken);
        }

        public async Task StopAsync()
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            Dispose();
        }

        public void Dispose() => _stop.Dispose();

        private async Task RunAsync()
        {
            var cancellationToken = _stop.Token;
            var source = new Uri(_home.Internal,
                $"/v1/sync/organizations/{Database.OrganizationId}/projects/{Database.ProjectId}/databases/{Database.DatabaseId}");
            await RetryAsync(async () =>
            {
                await _owner.Replicas.OpenAsync(Database, source, _owner._http, cancellationToken).ConfigureAwait(false);
                return true;
            }, "open", cancellationToken).ConfigureAwait(false);
            Signal(0);
            await foreach (var _ in _signals.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var completing = Interlocked.Exchange(ref _pulled, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                var noticed = Interlocked.Exchange(ref _noticed, 0);
                var changed = await RetryAsync(async () =>
                {
                    await _owner._pulls.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        return (await _owner.Replicas.PullAsync(Database, cancellationToken).ConfigureAwait(false))?.Changed ?? false;
                    }
                    finally
                    {
                        _owner._pulls.Release();
                    }
                }, "pull", cancellationToken).ConfigureAwait(false);
                _owner._pullCount?.Add(1, new KeyValuePair<string, object?>("result", changed ? "changed" : "unchanged"));
                if (noticed > 0)
                {
                    _owner._lag?.Record(Math.Max(0, _owner.Time.GetUtcNow().ToUnixTimeMilliseconds() - noticed));
                }

                completing.TrySetResult();
            }
        }

        private async Task<bool> RetryAsync(Func<Task<bool>> attempt, string what, CancellationToken cancellationToken)
        {
            var backoff = TimeSpan.FromMilliseconds(50);
            for (var failures = 1; ; failures++)
            {
                try
                {
                    return await attempt().ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    _owner._pullCount?.Add(1, new KeyValuePair<string, object?>("result", "failed"));
                    if (failures % 20 == 1)
                    {
                        _owner.Logger.ZLogInformation($"A replica {what} failed and is retried: {error.GetType().Name:@Error}");
                        // A home that stopped answering for this database may have deleted it or moved it out of this region.
                        _ = _owner._catalogWakes.Writer.TryWrite(_home.Region);
                    }

                    await Task.Delay(backoff, _owner.Time, cancellationToken).ConfigureAwait(false);
                    backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
                }
            }
        }
    }
}
