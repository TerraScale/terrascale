using NATS.Client.Core;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net.Http.Headers;
using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using TerraBase.Configuration;
using TerraBase.Engine.Revisions;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.HomePush;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Memory;
using TerraBase.Features.Replicas;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Replicas;

internal sealed partial class RegionReplication(
    TerraBaseOptions options, IReplicaPeers peers, IReplicaCatalog catalog, IDatabaseReplicas replicas,
    IDatabaseRevisions revisions, IMemoryBudget memory, ReplicaHolds holds, InternalCredential credential, IMeterFactory meters, TimeProvider time, ILogger<RegionReplication> logger,
    ITransactionCrypto crypto, IOperationAllowance allowance, RegionalOwners owners, RemovedReplicaFaults faults) : BackgroundService
{
    internal static readonly TimeSpan Sweep = TimeSpan.FromSeconds(30);

    internal static readonly TimeSpan VerifyEvery = TimeSpan.FromMinutes(10);

    private const int MaxConcurrentPulls = 8;
    private const int MaxRefreshes = 64;
    private const int MaxRecentRefreshes = 4096;
    private static readonly TimeSpan PlacementTimeout = TimeSpan.FromSeconds(5);
    private static readonly Unavailable PlacementUnanswered = new("the region did not answer for the database's placement");
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RoundGap = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryGap = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HealGap = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ReportEvery = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReportTimeout = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<(string Organization, string Project, string Database), Follower> _followers = new();
    private readonly ConcurrentDictionary<(string Organization, string Project, string Database), (TenantContext Database, long At)> _revised = new();
    private readonly ConcurrentDictionary<(string Organization, string Project, string Database), (string Revision, TenantContext Database)> _published = new();
    private readonly ConcurrentDictionary<(string Organization, string Project, string Database), bool> _suspended = new();
    private readonly ConcurrentDictionary<string, (string Project, string Database)> _refreshAfter = new();
    private readonly Dictionary<(string, string, string), DateTimeOffset> _refreshed = [];
    private readonly Channel<bool> _publish = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Channel<string> _catalogWakes = Channel.CreateBounded<string>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly SemaphoreSlim[] _lifecycle = [.. Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1))];
    private readonly SemaphoreSlim _pulls = new(MaxConcurrentPulls, MaxConcurrentPulls);
    private readonly SemaphoreSlim _verifying = new(1, 1);
    private readonly HttpClient _http = PeerHttp.Create(credential);
    private readonly string _session = Guid.NewGuid().ToString("N");
    private readonly Meter _meter = meters.Create(Telemetry.MeterName);
    private readonly Lock _gate = new();
    private IReadOnlyList<ReplicaPeer> _peers = [];
    private NatsConnection? _nats;
    private Histogram<double>? _lag;
    private Counter<long>? _pullCount;
    private Counter<long>? _verifications;
    private Counter<long>? _rebootstraps;
    private ReplicaVerifier? _verifier;

    private IDatabaseReplicas Replicas => replicas;

    private ITransactionCrypto Crypto => crypto;

    private IOperationAllowance Allowance => allowance;

    private ILogger Logger => logger;

    private TimeProvider Time => time;

    private bool Notified => _nats is not null;

    private string Region => options.Region;

    [LoggerMessage(Message = "Replica verification skipped for database {Database} in follower region {Region}, session {Session}: {Reason}")]
    private static partial void LogVerificationSkipped(ILogger logger, LogLevel level, string database, string region, string session, string reason);

    internal IReadOnlyCollection<TenantContext> Following => [.. _followers.Values.Select(follower => follower.Database)];

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _peers = await peers.ListAsync(cancellationToken).ConfigureAwait(false);
        _lag = _meter.CreateHistogram<double>("terrabase.replica.lag", "ms",
            "Time from a home's commit, on its clock, to the replica holding it after the notice's pull.");
        _pullCount = _meter.CreateCounter<long>("terrabase.replica.pulls", "{pull}", "Replica pulls by result (pushed, changed, unchanged, failed).");
        _verifications = _meter.CreateCounter<long>("terrabase.replica.verifications", "{round}",
            "Replica checksum rounds against the home by result (equal, diverged, skipped, failed).");
        _rebootstraps = _meter.CreateCounter<long>("terrabase.replica.rebootstraps", "{rebootstrap}", "Replicas deleted and bootstrapped again after a divergence.");
        _ = _meter.CreateObservableGauge("terrabase.replica.max_lag", () => _followers.Values.Select(follower => follower.LagMs ?? 0).DefaultIfEmpty(0).Max(), "ms",
            "The largest lag of this node's replicas behind their homes.");
        _ = _meter.CreateObservableGauge("terrabase.replica.max_verification_age", () => _followers.Values.Select(follower => follower.VerificationAge.TotalSeconds)
            .DefaultIfEmpty(0).Max(), "s", "The longest time any of this node's replicas went without a completed verification against its home.");
        _ = _meter.CreateObservableGauge("terrabase.replica.tracked_followers", () => _followers.Count, "{follower}",
            "The number of followers contributing to replica verification age.");
        _verifier = new ReplicaVerifier(replicas, _http);

        if (options is { NatsUrl: { } natsUrl, NatsToken: { } natsToken })
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
            _nats.ConnectionOpened += (_, _) => Reconnected();
            revisions.Revised += OnRevised;
        }
        else if (_peers.Count > 0)
        {
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
        foreach (var gate in _lifecycle)
        {
            gate.Dispose();
        }
        _pulls.Dispose();
        _verifying.Dispose();
        _meter.Dispose();
        base.Dispose();
    }

    internal void PullSoon(TenantContext database)
    {
        if (database.ProjectId is { } project && database.DatabaseId is { } id
            && _followers.TryGetValue((database.OrganizationId, project, id), out var follower))
        {
            follower.Signal(0, fromHome: true);
        }
    }

    internal async Task PullAllAsync(CancellationToken cancellationToken) =>
        await Task.WhenAll(_followers.Values.Select(follower => follower.PullNowAsync(cancellationToken))).ConfigureAwait(false);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await (_nats is { } nats
            ? Task.WhenAll(PublishAsync(nats, stoppingToken), SubscribeAsync(nats, stoppingToken), HoldsAsync(nats, stoppingToken), WatchConnectionAsync(nats, stoppingToken),
                CatalogAsync(stoppingToken))
            : CatalogAsync(stoppingToken)).ConfigureAwait(false);
    }

    private void OnRevised(TenantContext database)
    {
        _revised[(database.OrganizationId, database.ProjectId!, database.DatabaseId!)] = (database, time.GetUtcNow().ToUnixTimeMilliseconds());
        _ = _publish.Writer.TryWrite(true);
    }

    private ValueTask Reconnected()
    {
        foreach (var key in _published.Keys)
        {
            if (_published.TryRemove(key, out var published))
            {
                _revised[key] = (published.Database, time.GetUtcNow().ToUnixTimeMilliseconds());
            }
        }

        _ = _publish.Writer.TryWrite(true);
        foreach (var follower in _followers.Values)
        {
            follower.Signal(0, fromHome: true);
        }

        return default;
    }

    private async Task PublishAsync(NatsConnection nats, CancellationToken cancellationToken)
    {
        await foreach (var wake in _publish.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var key in _revised.Keys)
            {
                if (!_revised.TryRemove(key, out var revised))
                {
                    continue;
                }

                try
                {
                    if (await catalog.FindAsync(revised.Database, cancellationToken).ConfigureAwait(false) is not TenantDatabaseRecord { Placement: var placement }
                        || placement.Home != options.Region || !owners.Writer)
                    {
                        _ = _published.TryRemove(key, out _);
                        continue;
                    }

                    using var push = await RevisionPush.BuildAsync(revisions, memory, revised.Database, _published.GetValueOrDefault(key).Revision, revised.At, cancellationToken)
                        .ConfigureAwait(false);
                    if (push is not { Notice.Revision: { } revision })
                    {
                        continue;
                    }

                    if (_published.TryGetValue(key, out var last) && last.Revision == revision)
                    {
                        continue;
                    }

                    _published[key] = (revision, revised.Database);
                    foreach (var region in (placement.AllRegions ? _peers.Select(peer => peer.Region) : placement.Replicas).Append(options.Region).Distinct(StringComparer.Ordinal))
                    {
                        await nats.PublishAsync(ReplicaSubjects.Revision(region, options.Region, key.Database), push.Payload, cancellationToken: cancellationToken)
                            .ConfigureAwait(false);
                        if (push is { HasPages: true, Result: { } offered })
                        {
                            await revisions.RecordReplicaPullAsync(revised.Database, region, offered, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    _ = _published.TryRemove(key, out _);
                    _revised[key] = revised;
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
                await foreach (var message in nats.SubscribeAsync<byte[]>(ReplicaSubjects.Revisions(options.Region), cancellationToken: cancellationToken).ConfigureAwait(false))
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

    private async Task HoldsAsync(NatsConnection nats, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var message in nats.SubscribeAsync<byte[]>(ReplicaSubjects.HeldAt(options.Region), cancellationToken: cancellationToken).ConfigureAwait(false))
                {
                    try
                    {
                        if (message.Data is { } data && JsonSerializer.Deserialize(data, ReplicasJsonContext.Default.ReplicaHeldNotice) is { } notice)
                        {
                            holds.Held(notice);
                        }
                    }
                    catch (JsonException)
                    {
                    }
                }
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.ZLogInformation($"The held-revision subscription restarts: {error.GetType().Name:@Error}");
                await Task.Delay(MaxBackoff, time, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void PublishHeld(TenantContext database, string home)
    {
        if (_nats is not { } nats || replicas.HeldRevision(database) is not { } held)
        {
            return;
        }

        var notice = new ReplicaHeldNotice(database.OrganizationId, database.ProjectId!, database.DatabaseId!, options.Region, held.ToString());
        _ = PublishHeldAsync(nats, ReplicaSubjects.Held(home, database.DatabaseId!), JsonSerializer.SerializeToUtf8Bytes(notice, ReplicasJsonContext.Default.ReplicaHeldNotice));
    }

    private async Task PublishHeldAsync(NatsConnection nats, string subject, byte[] data)
    {
        try
        {
            await nats.PublishAsync(subject, data).ConfigureAwait(false);
        }
        catch (Exception error) when (error is NatsException or OperationCanceledException)
        {
            logger.ZLogInformation($"A held-revision notice was not published: {error.GetType().Name:@Error}");
        }
    }

    private void Notice(string subject, byte[]? data)
    {
        if (subject.Split('.') is not ["terrabase", "revision", var replica, var home, _] || replica != options.Region || (home == options.Region && owners.Writer) || data is null
            || !RevisionPush.TryRead(data, out var notice, out var answer) || notice is null)
        {
            return;
        }

        if (_followers.TryGetValue((notice.OrganizationId, notice.ProjectId, notice.DatabaseId), out var follower) && follower.Home.Region == home)
        {
            var known = DatabaseRevision.TryParse(notice.Revision, out var to);
            if (known && replicas.HeldRevision(follower.Database) is { } held && held.Incarnation == to.Incarnation && DatabaseRevision.Compare(to, held) <= 0)
            {
                return;
            }

            if (known && answer.Length > 0 && DatabaseRevision.TryParse(notice.Base, out var from))
            {
                _ = replicas.Offer(follower.Database, from, to, answer, notice.CdcPosition);
            }

            follower.Signal(notice.CommittedAt, fromHome: false);
        }
        else
        {
            _ = _catalogWakes.Writer.TryWrite(home);
        }
    }

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

            connected = nats.ConnectionState == NatsConnectionState.Open;
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }

    private SemaphoreSlim Lifecycle((string, string, string) key) => _lifecycle[(int)((uint)key.GetHashCode() % (uint)_lifecycle.Length)];

    private async Task FollowAsync((string, string, string) key, TenantContext database, ReplicaPeer home, CancellationToken cancellationToken)
    {
        var gate = Lifecycle(key);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await catalog.FindAsync(database, cancellationToken).ConfigureAwait(false) is TenantDatabaseRecord { Placement: var placement }
                && placement.Home == home.Region && placement.Includes(options.Region))
            {
                await FollowRegisteredAsync(key, database, home, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task FollowRegisteredAsync((string, string, string) key, TenantContext database, ReplicaPeer home, CancellationToken cancellationToken)
    {
        Follower? stale = null;
        lock (_gate)
        {
            if (_suspended.ContainsKey(key))
            {
                return;
            }

            if (_followers.TryGetValue(key, out var current))
            {
                if (current.Home.Region == home.Region && current.Home.Internal == home.Internal)
                {
                    return;
                }

                stale = current;
                _ = _followers.TryRemove(key, out _);
            }
        }

        if (stale is not null)
        {
            await stale.StopAsync().ConfigureAwait(false);
            _ = await replicas.DeleteAsync(database, cancellationToken).ConfigureAwait(false);
        }

        lock (_gate)
        {
            if (!_followers.ContainsKey(key) && !_suspended.ContainsKey(key))
            {
                _followers[key] = Follower.Start(this, database, home);
            }
        }
    }

    internal void WakeCatalog(string region) => _catalogWakes.Writer.TryWrite(region);

    internal async Task<bool> CatchUpAsync(TenantContext database, CancellationToken cancellationToken)
    {
        if (!_followers.TryGetValue(Key(database), out var follower))
        {
            return false;
        }

        await follower.PullNowAsync(cancellationToken).ConfigureAwait(false);
        return Replicas.Holds(database);
    }

    internal async Task SuspendAsync(TenantContext database)
    {
        var key = Key(database);
        Follower? follower;
        lock (_gate)
        {
            _suspended[key] = true;
            _ = _followers.TryRemove(key, out follower);
        }

        if (follower is not null)
        {
            await follower.StopAsync().ConfigureAwait(false);
        }
    }

    internal void Resume(TenantContext database)
    {
        lock (_gate)
        {
            _ = _suspended.TryRemove(Key(database), out _);
        }

        WakeCatalogs();
    }

    private sealed class Follower : IDisposable
    {
        private readonly RegionReplication _owner;
        private readonly Channel<bool> _signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        private readonly CancellationTokenSource _stop = new();
        private readonly ReplicaPeer _home;
        private readonly Uri _source;
        private readonly DateTimeOffset _started;
        private long _noticed;
        private long _pullStartedAt;
        private DateTimeOffset? _syncedAt;
        private DateTimeOffset? _verifiedAt;
        private DateTimeOffset _nextVerify;
        private DateTimeOffset _lastHeal = DateTimeOffset.MinValue;
        private DateTimeOffset _lastReport = DateTimeOffset.MinValue;
        private string _state = ReplicaStates.Bootstrapping;
        private string? _reportedState;
        private ChecksumCursor? _cursor;
        private TaskCompletionSource _pulled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task _loop = Task.CompletedTask;
        private Task _report = Task.CompletedTask;
        private int _fromHome;
        private int _pullRequested;
        private long _lastChangedAt;
        private Task<VerificationRound>? _verification;
        private VerificationSkipReason? _lastSkipReason;

        private Follower(RegionReplication owner, TenantContext database, ReplicaPeer home)
        {
            _owner = owner;
            Database = database;
            _home = home;
            _source = new Uri(_home.Internal, ReplicaRoutes.DatabasePath(Database));
            _started = owner.Time.GetUtcNow();
            _nextVerify = _started + TimeSpan.FromSeconds(Random.Shared.Next(5, 60));
        }

        public TenantContext Database { get; }

        public long? LagMs
        {
            get
            {
                var now = _owner.Time.GetUtcNow().ToUnixTimeMilliseconds();
                return Volatile.Read(ref _noticed) is > 0 and var noticed ? Math.Max(0, now - noticed)
                    : Volatile.Read(ref _pullStartedAt) is > 0 and var started ? (_owner.Notified ? 0 : Math.Max(0, now - started))
                    : null;
            }
        }

        public TimeSpan VerificationAge => _owner.Time.GetUtcNow() - (_verifiedAt ?? _started);

        public ReplicaPeer Home => _home;

        public static Follower Start(RegionReplication owner, TenantContext database, ReplicaPeer home)
        {
            var follower = new Follower(owner, database, home);
            follower._loop = Task.Run(follower.RunAsync);
            return follower;
        }

        public void SweepIfIdle()
        {
            if (_owner.Time.GetUtcNow().ToUnixTimeMilliseconds() - Volatile.Read(ref _lastChangedAt) >= Sweep.TotalMilliseconds)
            {
                Signal(0, fromHome: true);
            }
        }

        public void Signal(long committedAt, bool fromHome)
        {
            if (fromHome)
            {
                Volatile.Write(ref _fromHome, 1);
            }

            if (committedAt > 0)
            {
                _ = Interlocked.CompareExchange(ref _noticed, committedAt, 0);
            }

            Volatile.Write(ref _pullRequested, 1);
            _ = _signals.Writer.TryWrite(true);
        }

        public Task PullNowAsync(CancellationToken cancellationToken)
        {
            var pulled = Volatile.Read(ref _pulled).Task;
            Signal(0, fromHome: true);
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

            try
            {
                await _report.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            if (_verification is { } verification)
            {
                try
                {
                    await verification.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception error)
                {
                    _owner.Logger.ZLogInformation($"A replica verification round failed during shutdown: {error.GetType().Name:@Error}");
                }
            }

            Dispose();
        }

        public void Dispose() => _stop.Dispose();

        private async Task RunAsync()
        {
            var cancellationToken = _stop.Token;
            await ReportAsync(cancellationToken).ConfigureAwait(false);
            await RetryAsync(async () =>
            {
                await _owner.Crypto.PrepareAsync(Database, cancellationToken).ConfigureAwait(false);
                _ = await _owner.Allowance.CheckAsync(Database, MeteredOperation.Write, cancellationToken).ConfigureAwait(false);
                await _owner.Replicas.OpenAsync(Database, _source, _owner._http, cancellationToken).ConfigureAwait(false);
                return true;
            }, "open", cancellationToken).ConfigureAwait(false);
            _state = ReplicaStates.Following;
            Signal(0, fromHome: true);
            await foreach (var _ in _signals.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await VerifyAsync(cancellationToken).ConfigureAwait(false);
                if (Interlocked.Exchange(ref _pullRequested, 0) == 0)
                {
                    continue;
                }

                var completing = Interlocked.Exchange(ref _pulled, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                var noticed = Interlocked.Exchange(ref _noticed, 0);
                var started = _owner.Time.GetUtcNow();
                var fromHome = Interlocked.Exchange(ref _fromHome, 0) == 1;
                var pending = false;
                var pushed = false;
                var changed = await RetryAsync(async () =>
                {
                    await _owner._pulls.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var result = await _owner.Replicas.PullAsync(Database, fromHome, cancellationToken).ConfigureAwait(false);
                        pending = result?.Pending ?? false;
                        pushed = result?.Pushed ?? false;
                        return result?.Changed ?? false;
                    }
                    finally
                    {
                        _owner._pulls.Release();
                    }
                }, "pull", cancellationToken).ConfigureAwait(false);
                _owner._pullCount?.Add(1, new KeyValuePair<string, object?>("result", pushed ? "pushed" : changed ? "changed" : "unchanged"));
                if (noticed > 0)
                {
                    _owner._lag?.Record(Math.Max(0, _owner.Time.GetUtcNow().ToUnixTimeMilliseconds() - noticed));
                }

                Volatile.Write(ref _pullStartedAt, started.ToUnixTimeMilliseconds());
                _syncedAt = _owner.Time.GetUtcNow();
                if (changed)
                {
                    Volatile.Write(ref _lastChangedAt, _syncedAt.Value.ToUnixTimeMilliseconds());
                }

                _owner.PublishHeld(Database, _home.Region);
                if (pending)
                {
                    Signal(0, fromHome: false);
                }

                await VerifyAsync(cancellationToken).ConfigureAwait(false);
                if (_report.IsCompleted)
                {
                    _report = ReportAsync(cancellationToken);
                }

                completing.TrySetResult();
            }
        }

        private async Task VerifyAsync(CancellationToken cancellationToken)
        {
            if (_owner._verifier is not { } verifier)
            {
                return;
            }

            if (_verification is null)
            {
                if (_owner.Time.GetUtcNow() >= _nextVerify && await _owner._verifying.WaitAsync(0, cancellationToken).ConfigureAwait(false))
                {
                    _verification = ObserveAsync(verifier, cancellationToken);
                }

                return;
            }

            if (!_verification.IsCompleted)
            {
                return;
            }

            try
            {
                var round = await _verification.ConfigureAwait(false);
                var now = _owner.Time.GetUtcNow();
                await (round.Verdict switch
                {
                    Equal => EqualRoundAsync(round, now, cancellationToken),
                    VerificationSkipped skipped => SkippedRoundAsync(skipped, now),
                    Diverged => DivergedRoundAsync(verifier, round, now, cancellationToken),
                });
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _lastSkipReason = null;
                Count("failed");
                _owner.Logger.ZLogInformation($"A replica verification round failed and is retried: {error.GetType().Name:@Error}");
                _nextVerify = _owner.Time.GetUtcNow() + RetryGap;
            }
            finally
            {
                _verification = null;
            }

            async Task EqualRoundAsync(VerificationRound round, DateTimeOffset now, CancellationToken cancellationToken)
            {
                _lastSkipReason = null;
                Count("equal");
                _cursor = round.Next;
                if (round.Next is null)
                {
                    _verifiedAt = now;
                    _nextVerify = now + VerifyEvery;
                }
                else
                {
                    _nextVerify = now + RoundGap;
                    WakeAfter(RoundGap, cancellationToken);
                }
            }

            async Task SkippedRoundAsync(VerificationSkipped skipped, DateTimeOffset now)
            {
                _owner._verifications?.Add(1, new KeyValuePair<string, object?>("result", "skipped"),
                    new KeyValuePair<string, object?>("reason", skipped.Label));
                LogVerificationSkipped(_owner.Logger, _lastSkipReason == skipped.Reason ? LogLevel.Debug : LogLevel.Information,
                    Database.DatabaseId!, _owner.Region, _owner._session, skipped.Label);
                _lastSkipReason = skipped.Reason;
                _nextVerify = now + RetryGap;
            }

            async Task DivergedRoundAsync(ReplicaVerifier verifier, VerificationRound round, DateTimeOffset now, CancellationToken cancellationToken)
            {
                _lastSkipReason = null;
                Count("diverged");
                _owner.Logger.ZLogError($"Replica of database {Database.DatabaseId:@Database} in project {Database.ProjectId:@Project} diverged from its home in region {_home.Region:@Home}: {round.Where:@Where}");
                await HealAsync(verifier, now, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<VerificationRound> ObserveAsync(ReplicaVerifier verifier, CancellationToken cancellationToken)
        {
            try
            {
                return await verifier.ObserveAsync(Database, _source, _cursor, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _owner._verifying.Release();
                _ = _signals.Writer.TryWrite(false);
            }
        }

        private async Task HealAsync(ReplicaVerifier verifier, DateTimeOffset now, CancellationToken cancellationToken)
        {
            _cursor = null;
            if (now - _lastHeal < HealGap)
            {
                _owner.Logger.ZLogError($"Replica of database {Database.DatabaseId:@Database} diverged again within {HealGap.TotalMinutes} minutes of its last rebootstrap; the next waits");
                _nextVerify = _lastHeal + HealGap;
                return;
            }

            _state = ReplicaStates.Healing;
            _verifiedAt = null;
            await ReportAsync(cancellationToken).ConfigureAwait(false);
            await RetryAsync(async () =>
            {
                await verifier.HealAsync(Database, _source, cancellationToken).ConfigureAwait(false);
                return true;
            }, "rebootstrap", cancellationToken).ConfigureAwait(false);
            _lastHeal = _owner.Time.GetUtcNow();
            _owner._rebootstraps?.Add(1);
            _owner.Logger.ZLogWarning($"Replica of database {Database.DatabaseId:@Database} was bootstrapped again from its home and is verified next");
            _state = ReplicaStates.Following;
            _syncedAt = _lastHeal;
            _nextVerify = _lastHeal;
            WakeAfter(RoundGap, cancellationToken);
        }

        private void Count(string result) => _owner._verifications?.Add(1, new KeyValuePair<string, object?>("result", result));

        private void WakeAfter(TimeSpan delay, CancellationToken cancellationToken) =>
            _ = Task.Delay(delay, _owner.Time, cancellationToken).ContinueWith(_ => Signal(0, fromHome: true), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);

        private async Task ReportAsync(CancellationToken cancellationToken)
        {
            var now = _owner.Time.GetUtcNow();
            if (_state == _reportedState && now - _lastReport < ReportEvery)
            {
                return;
            }

            _lastReport = now;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ReportTimeout);
            try
            {
                using var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(
                    new ReplicaStatusReport(_owner.Region, _state, LagMs, _syncedAt, _verifiedAt), ReplicasJsonContext.Default.ReplicaStatusReport));
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                using var response = await _owner._http.PostAsync(new Uri(_source + "/replica-status"), content, timeout.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    _reportedState = _state;
                }
                else
                {
                    _owner.Logger.ZLogInformation($"The home refused a replica status report with {(int)response.StatusCode:@Status}");
                }
            }
            catch (Exception error) when (error is HttpRequestException || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
            }
        }

        private async Task<bool> RetryAsync(Func<Task<bool>> attempt, string what, CancellationToken cancellationToken)
        {
            var backoff = new Backoff(TimeSpan.FromMilliseconds(50), MaxBackoff);
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
                        _owner.Logger.ZLogInformation($"A replica {what} failed and is retried: {error.GetType().Name:@Error} ({error.Message:@Detail})");
                        _ = _owner._catalogWakes.Writer.TryWrite(_home.Region);
                    }

                    await Task.Delay(backoff.Next(), _owner.Time, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }
}
