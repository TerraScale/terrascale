using System.Collections.Concurrent;
using System.Diagnostics;
using System.Buffers.Binary;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using NativeCompressions;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Clock;
using TerraBase.Features.HomeApply;
using TerraBase.Features.LocalAck;
using TerraBase.Features.Regions;
using TerraBase.Features.Replicas;
using TerraBase.Features.Sql.Routing;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.HomePush;

internal interface ILocalWrites
{
    ValueTask<ILocalTransaction?> BeginAsync(TenantContext database, CancellationToken cancellationToken);

    ulong? LastPushed(TenantContext database);

    void Forwarded(TenantContext database);

    ValueTask<bool> AppliedAsync(string origin, TenantContext database, ulong sequence, CancellationToken cancellationToken);
}

internal interface ILocalTransaction : IAsyncDisposable
{
    IReadOnlyList<KeyValuePair<string, double>> Timings { get; }

    ValueTask<SqlStatementResult> ExecuteAsync(SqlStatement statement, CancellationToken cancellationToken);

    ValueTask<LocalCommit> CommitAsync(bool homeDurable, CancellationToken cancellationToken);
}

internal sealed record LocalCommit(string? Id, HomeApplyResult? Home);

internal sealed class LocalOutcomeUnknownException(string message, Exception inner) : Exception(message, inner);

internal class HomePush(
    TerraBaseOptions options, IRegionPeers peers, IReplicaCatalog catalog, IDatabaseReplicas replicas, IHomeApply apply, IHlcClock clock,
    IInRegionAck partner, RegionalOwners owners, IServiceProvider services, IMeterFactory meters, TimeProvider time, ILogger<HomePush> logger)
    : BackgroundService, ILocalWrites, ILocalCommitPath
{
    internal const int MaxPayloadBytes = 8 << 20;
    private const int MaxParallelDatabases = 16;
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan SettleBound = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HomeWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PeerSweep = TimeSpan.FromSeconds(5);

    private readonly LocalOverlay _overlay = new();
    private readonly ConcurrentDictionary<(string Organization, string Project, string Database), ulong> _pushed = new();
    private readonly ConcurrentDictionary<(string Organization, string Project, string Database), byte> _forwarded = new();
    private readonly ConcurrentDictionary<(string Origin, string Organization, string Project, string Database), ulong> _applied = new();
    private TaskCompletionSource _progress = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lazy<ILocalCommitFeed?> _feed = new(services.GetService<ILocalCommitFeed>);
    private readonly Channel<(TenantContext Database, ulong Through)> _purges = Channel.CreateBounded<(TenantContext, ulong)>(
        new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Lock _queuesGate = new();
    private readonly Dictionary<string, Queue<INatsJSMsg<byte[]>>> _queues = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _applying = new(MaxParallelDatabases, MaxParallelDatabases);
    private readonly Meter _meter = meters.Create(Telemetry.MeterName);
    private NatsConnection? _nats;
    private NatsJSContext? _js;
    private volatile bool _ready;
    private Histogram<double>? _lag;
    private Counter<long>? _applies;
    private Counter<long>? _reverts;
    private Counter<long>? _applyRetries;
    private Histogram<double>? _applyTime;

    internal LocalOverlay Overlay => _overlay;

    private IHlcClock Clock => clock;

    private ITransactionCrypto Crypto => services.GetRequiredService<ITransactionCrypto>();

    private TimeProvider Time => time;

    private string Region => options.Region;

    internal bool Started => _ready;

    public bool Ready(TenantContext database) => _ready && _js is not null;

    public bool TwoNodeAck => LocalAckAvailable && partner.PartnerHealthy;

    private bool RegionWriter => owners.Writer;

    public bool LocalAckAvailable => RegionWriter && _ready && _js is not null && _nats?.ConnectionState == NatsConnectionState.Open
        && partner.QuorumHealthy && services.GetService<ILocalWriteClock>() is { Allowed: true };

    private IInRegionAck Partner => partner;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (options is { NatsUrl: { } natsUrl, NatsToken: { } natsToken })
        {
            if (!Crypto.Seals)
            {
                throw new TerraBaseConfigurationException("TS_NATS_URL requires backup: durable local writes seal under the database key store");
            }

            _nats = new NatsConnection(new NatsOpts
            {
                Url = natsUrl.ToString(),
                Name = $"terrabase-push-{options.Region}-{options.MachineId}",
                AuthOpts = new NatsAuthOpts { Token = natsToken },
                MaxReconnectRetry = -1,
                ReconnectWaitMin = TimeSpan.FromMilliseconds(100),
                ReconnectWaitMax = TimeSpan.FromSeconds(2),
            });
            _js = new NatsJSContext(_nats);
            _lag = _meter.CreateHistogram<double>("terrabase.push.lag", "ms", "Time from a local commit stamp to the home's applied notice reaching the origin.");
            _applies = _meter.CreateCounter<long>("terrabase.push.applies", "{transaction}", "Home applies of pushed transactions by state.");
            _reverts = _meter.CreateCounter<long>("terrabase.push.reverts", "{transaction}", "Local transactions the home reverted.");
            _applyRetries = _meter.CreateCounter<long>("terrabase.push.apply_retries", "{retry}", "Temporary home apply failures by bounded reason.");
            _applyTime = _meter.CreateHistogram<double>("terrabase.push.apply_time", "ms", "Time in each home apply attempt, excluding retry backoff and transport.");
            _ = _meter.CreateObservableGauge("terrabase.push.pending", () => _overlay.Count, "{transaction}", "Local transactions the home has not confirmed.");
            _ = _meter.CreateObservableGauge("terrabase.localwrites.available", () => LocalAckAvailable ? 1 : 0, "{node}",
                "1 while this node can acknowledge local writes with its discovered regional quorum.");
            replicas.Overlay = _overlay.RestoreAsync;
            _overlay.Held += (database, through) =>
            {
                _ = _purges.Writer.TryWrite((database, through));
                partner.Release(database, through);
            };
            _overlay.Reverted += OnReverted;
        }
        else if ((await peers.OtherRegionsAsync(cancellationToken).ConfigureAwait(false)).Count > 0)
        {
            logger.ZLogWarning($"TS_NATS_URL or TERRABASE_NATS_TOKEN_FILE is missing; writes for databases homed in other regions go home");
        }

        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        replicas.Overlay = null;
        if (_nats is { } nats)
        {
            await nats.DisposeAsync().ConfigureAwait(false);
        }
    }

    public override void Dispose()
    {
        _applying.Dispose();
        _meter.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_js is not { } js || _nats is not { } nats)
        {
            return;
        }

        await RetryAsync(token => EnsureStreamAsync(js, nats, token), "the push stream is not ready", stoppingToken).ConfigureAwait(false);
        await RetryAsync(token => RebuildAsync(js, token), "the push overlay could not be read back", stoppingToken).ConfigureAwait(false);
        _ready = true;
        await Task.WhenAll(NoticesAsync(nats, stoppingToken), PurgesAsync(js, stoppingToken), OriginsAsync(js, nats, stoppingToken)).ConfigureAwait(false);
    }

    private async Task OriginsAsync(NatsJSContext js, NatsConnection nats, CancellationToken cancellationToken)
    {
        var consuming = new Dictionary<string, Task>(StringComparer.Ordinal);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                foreach (var origin in (await peers.OtherRegionsAsync(cancellationToken).ConfigureAwait(false)).Select(peer => peer.Region).Append(options.Region).Distinct(StringComparer.Ordinal))
                {
                    if (!consuming.ContainsKey(origin))
                    {
                        consuming[origin] = ConsumeAsync(js, nats, origin, cancellationToken);
                    }
                }
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.ZLogInformation($"The region list is read again shortly: {error.GetType().Name:@Error}");
            }

            try
            {
                await Task.Delay(PeerSweep, time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        await Task.WhenAll(consuming.Values).ConfigureAwait(false);
    }

    public async ValueTask<ILocalTransaction?> BeginAsync(TenantContext database, CancellationToken cancellationToken)
    {
        if (!_ready || _js is null || services.GetService<ILocalWriteClock>() is { Allowed: false })
        {
            return null;
        }

        if (await catalog.FindAsync(database, cancellationToken).ConfigureAwait(false) is not TenantDatabaseRecord { Placement: var placement }
            || placement.Home == options.Region || !placement.Includes(options.Region))
        {
            return null;
        }

        if (_forwarded.TryRemove(Key(database), out _))
        {
            try
            {
                _ = await replicas.PullAsync(database, fromHome: true, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _forwarded[Key(database)] = 0;
                return null;
            }
        }

        return await replicas.BeginWriteAsync(database, cancellationToken).ConfigureAwait(false) is { } writer
            ? new LocalTransaction(this, database, placement.Home, placement.Version, writer)
            : null;
    }

    public ulong? LastPushed(TenantContext database) => _pushed.TryGetValue(Key(database), out var sequence) ? sequence : null;

    public void Forwarded(TenantContext database) => _forwarded[Key(database)] = 0;

    public async ValueTask<bool> AppliedAsync(string origin, TenantContext database, ulong sequence, CancellationToken cancellationToken)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bound.CancelAfter(SettleBound);
        while (true)
        {
            var progress = Volatile.Read(ref _progress).Task;
            if (_applied.TryGetValue((origin, database.OrganizationId, database.ProjectId!, database.DatabaseId!), out var applied) && applied >= sequence)
            {
                return true;
            }

            try
            {
                await progress.WaitAsync(bound.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }
    }

    private static (string, string, string) Key(TenantContext database) => (database.OrganizationId, database.ProjectId!, database.DatabaseId!);

    private void Progressed(string origin, TenantContext? database, ulong sequence)
    {
        if (database is not null)
        {
            _ = _applied.AddOrUpdate((origin, database.OrganizationId, database.ProjectId!, database.DatabaseId!), sequence, (_, known) => Math.Max(known, sequence));
        }

        Interlocked.Exchange(ref _progress, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }

    internal async Task<PubAckResponse> PublishAgainAsync(TenantContext database, string id, CancellationToken cancellationToken)
    {
        var transaction = _overlay.Find(database, id) ?? throw new InvalidOperationException("the transaction is not in the overlay");
        var placement = await catalog.FindAsync(database, cancellationToken).ConfigureAwait(false) is TenantDatabaseRecord { Placement: var placed }
            ? placed
            : throw new InvalidOperationException("the database has no placement");
        return await _js!.PublishAsync(PushNames.Subject(options.Region, placement.Home, database),
            await services.GetRequiredService<ITransactionCrypto>().SealAsync(database, Encode(transaction), cancellationToken).ConfigureAwait(false),
            opts: new NatsJSPubOpts { MsgId = id }, headers: Headers(transaction.Hlc!.Value, placement.Version), cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    internal async Task<ulong?> ReplayFromAsync(string origin, TenantContext database, string transactionId, CancellationToken cancellationToken)
    {
        var js = _js ?? throw new InvalidOperationException("this node has no push stream connection");
        var stream = PushNames.Stream(origin);
        var scan = await js.CreateOrderedConsumerAsync(stream, new NatsJSOrderedConsumerOpts
        {
            FilterSubjects = [PushNames.DatabaseFilter(origin, database)],
            DeliverPolicy = ConsumerConfigDeliverPolicy.All,
        }, cancellationToken).ConfigureAwait(false);
        ulong? found = null;
        while (found is null)
        {
            var read = 0;
            await foreach (var message in scan.FetchNoWaitAsync<byte[]>(new NatsJSFetchOpts { MaxMsgs = 256 }, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                read++;
                if (found is null && message.Headers is { } headers && headers.TryGetValue("Nats-Msg-Id", out var id) && id.ToString() == transactionId
                    && message.Metadata is { } metadata)
                {
                    found = metadata.Sequence.Stream;
                }
            }

            if (read == 0)
            {
                break;
            }
        }

        if (found is not { } sequence)
        {
            return null;
        }

        var consumer = await js.GetConsumerAsync(stream, PushNames.Consumer(options.Region), cancellationToken).ConfigureAwait(false);
        var resume = Math.Min(sequence + 1, consumer.Info.AckFloor.StreamSeq + 1);
        await consumer.ResetAsync(resume, cancellationToken).ConfigureAwait(false);
        return resume;
    }

    internal async Task<int> DrainOriginsAsync(TenantContext database, long fence, CancellationToken cancellationToken)
    {
        if (_js is not { } js || _nats is not { } nats)
        {
            return 0;
        }

        var scans = new List<INatsJSConsumer>();
        foreach (var origin in (await peers.OtherRegionsAsync(cancellationToken).ConfigureAwait(false)).Select(peer => peer.Region).Append(options.Region).Distinct(StringComparer.Ordinal))
        {
            scans.Add(await js.CreateOrderedConsumerAsync(PushNames.Stream(origin), new NatsJSOrderedConsumerOpts
            {
                FilterSubjects = [PushNames.DatabaseFilter(origin, database)],
                DeliverPolicy = ConsumerConfigDeliverPolicy.All,
            }, cancellationToken).ConfigureAwait(false));
        }

        var applied = 0;
        while (true)
        {
            var read = 0;
            foreach (var scan in scans)
            {
                await foreach (var message in scan.FetchNoWaitAsync<byte[]>(new NatsJSFetchOpts { MaxMsgs = 256 }, cancellationToken: cancellationToken).ConfigureAwait(false))
                {
                    read++;
                    if (Epoch(message.Headers) >= fence)
                    {
                        continue;
                    }

                    if (message.Data is not { } data
                        || await DecodeAsync(database, data, cancellationToken).ConfigureAwait(false) is not { } transaction)
                    {
                        throw new IOException("a retained origin push could not be decoded during promotion");
                    }

                    var result = await apply.ApplyAsync(database, transaction, cancellationToken).ConfigureAwait(false)
                        ?? throw new StorageBusyException();
                    await nats.PublishAsync(PushNames.Applied(transaction.Region!, database),
                        JsonSerializer.SerializeToUtf8Bytes(result, HomeApplyJsonContext.Default.HomeApplyResult), cancellationToken: cancellationToken).ConfigureAwait(false);
                    _applies?.Add(1, new KeyValuePair<string, object?>("state", result.State));
                    if (message.Metadata is { } metadata)
                    {
                        Progressed(transaction.Region!, database, metadata.Sequence.Stream);
                    }

                    applied++;
                }
            }

            if (read == 0)
            {
                break;
            }
        }

        return applied;
    }

    private static long Epoch(NatsHeaders? headers) =>
        headers is not null && headers.TryGetValue("Tb-Epoch", out var value) && long.TryParse(value.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var epoch)
            ? epoch
            : 0;

    private static NatsHeaders Headers(HlcTimestamp stamp, long epoch)
    {
        Span<byte> encoded = stackalloc byte[HlcTimestamp.EncodedLength];
        stamp.WriteTo(encoded);
        return new NatsHeaders { ["Tb-Hlc"] = Convert.ToHexString(encoded), ["Tb-Epoch"] = epoch.ToString(CultureInfo.InvariantCulture) };
    }

    private async Task EnsureStreamAsync(NatsJSContext js, NatsConnection nats, CancellationToken cancellationToken)
    {
        await nats.ConnectAsync().ConfigureAwait(false);
        var config = new StreamConfig(PushNames.Stream(options.Region), [PushNames.Subjects(options.Region)])
        {
            Storage = StreamConfigStorage.File,
            Retention = StreamConfigRetention.Limits,
            Discard = StreamConfigDiscard.New,
            MaxBytes = 1L << 30,
            MaxMsgSize = MaxPayloadBytes,
            DuplicateWindow = TimeSpan.FromMinutes(10),
            NumReplicas = 1,
            Placement = nats.ServerInfo?.Cluster is { Length: > 0 } ? new Placement { Tags = [$"region:{options.Region}"] } : null,
        };
        _ = await js.CreateOrUpdateStreamAsync(config, cancellationToken).ConfigureAwait(false);
    }

    internal async Task RebuildForPromotionAsync(CancellationToken cancellationToken)
    {
        if (_js is not { } js || !_ready)
        {
            throw new IOException("the candidate's push recovery is not ready");
        }

        await RebuildAsync(js, cancellationToken).ConfigureAwait(false);
    }

    private async Task RebuildAsync(NatsJSContext js, CancellationToken cancellationToken)
    {
        var consumer = await js.CreateOrderedConsumerAsync(PushNames.Stream(options.Region), new NatsJSOrderedConsumerOpts
        {
            FilterSubjects = [PushNames.Subjects(options.Region)],
            DeliverPolicy = ConsumerConfigDeliverPolicy.All,
        }, cancellationToken).ConfigureAwait(false);
        while (true)
        {
            var read = 0;
            await foreach (var message in consumer.FetchNoWaitAsync<byte[]>(new NatsJSFetchOpts { MaxMsgs = 256 }, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                read++;
                if (PushNames.Database(message.Subject) is { } database && message.Data is { } data && message.Metadata is { } metadata
                    && await DecodeAsync(database, data, cancellationToken).ConfigureAwait(false) is { } transaction)
                {
                    _ = _overlay.Add(database, metadata.Sequence.Stream, transaction);
                    _ = _pushed.AddOrUpdate(Key(database), metadata.Sequence.Stream, (_, known) => Math.Max(known, metadata.Sequence.Stream));
                }
            }

            if (read == 0)
            {
                return;
            }
        }
    }

    private async Task NoticesAsync(NatsConnection nats, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var message in nats.SubscribeAsync<byte[]>(PushNames.AppliedAll(options.Region), cancellationToken: cancellationToken).ConfigureAwait(false))
                {
                    if (PushNames.Database(message.Subject) is not { } database || message.Data is not { } data)
                    {
                        continue;
                    }

                    HomeApplyResult? result;
                    try
                    {
                        result = JsonSerializer.Deserialize(data, HomeApplyJsonContext.Default.HomeApplyResult);
                    }
                    catch (JsonException)
                    {
                        continue;
                    }

                    if (result is null)
                    {
                        continue;
                    }

                    if (_overlay.Find(database, result.Id) is { Hlc: { } stamp })
                    {
                        _lag?.Record(Math.Max(0, time.GetUtcNow().ToUnixTimeMilliseconds() - stamp.PhysicalMilliseconds));
                    }

                    _overlay.Applied(database, result);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.ZLogInformation($"Applied notices resubscribe: {error.GetType().Name:@Error}");
                await Task.Delay(TimeSpan.FromSeconds(1), time, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal async Task PurgeDeletedAsync(TenantContext database, CancellationToken cancellationToken)
    {
        _overlay.DeleteDatabase(database);
        _pushed.TryRemove(Key(database), out _);
        _forwarded.TryRemove(Key(database), out _);
        if (_js is not { } js)
        {
            return;
        }
        _ = await js.PurgeStreamAsync(PushNames.Stream(options.Region), new StreamPurgeRequest
        {
            Filter = PushNames.DatabaseFilter(options.Region, database),
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task PurgesAsync(NatsJSContext js, CancellationToken cancellationToken)
    {
        await foreach (var (database, through) in _purges.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                _ = await js.PurgeStreamAsync(PushNames.Stream(options.Region), new StreamPurgeRequest
                {
                    Filter = PushNames.DatabaseFilter(options.Region, database),
                    Seq = through + 1,
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.ZLogInformation($"A push stream purge waits for the next pull: {error.GetType().Name:@Error}");
            }
        }
    }

    private async Task ConsumeAsync(NatsJSContext js, NatsConnection nats, string origin, CancellationToken cancellationToken)
    {
        var reset = true;
        var backoff = new Backoff(TimeSpan.FromMilliseconds(200), MaxBackoff);
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!RegionWriter)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), time, cancellationToken).ConfigureAwait(false);
                continue;
            }
            try
            {
                var name = PushNames.Consumer(options.Region);
                var consumer = await js.CreateOrUpdateConsumerAsync(PushNames.Stream(origin), new ConsumerConfig(name)
                {
                    DurableName = name,
                    FilterSubject = PushNames.Subjects(origin),
                    AckPolicy = ConsumerConfigAckPolicy.Explicit,
                    DeliverPolicy = ConsumerConfigDeliverPolicy.All,
                    AckWait = TimeSpan.FromSeconds(60),
                    MaxAckPending = 4096,
                }, cancellationToken).ConfigureAwait(false);
                if (reset)
                {
                    await consumer.ResetAsync(1, cancellationToken).ConfigureAwait(false);
                    reset = false;
                }

                backoff.Reset();
                await foreach (var message in consumer.ConsumeAsync<byte[]>(opts: new NatsJSConsumeOpts { MaxMsgs = 256 }, cancellationToken: cancellationToken)
                    .ConfigureAwait(false))
                {
                    if (!RegionWriter)
                    {
                        await message.NakAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                        break;
                    }

                    Dispatch(nats, message, cancellationToken);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.ZLogInformation($"The push from {origin} is not consumed yet: {error.GetType().Name:@Error}");
                await Task.Delay(backoff.Next(), time, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void Dispatch(NatsConnection nats, INatsJSMsg<byte[]> message, CancellationToken cancellationToken)
    {
        var key = PushNames.Origin(message.Subject) is { } origin && PushNames.Database(message.Subject) is { } database
            ? PushNames.DatabaseFilter(origin, database) : message.Subject;
        var start = false;
        lock (_queuesGate)
        {
            if (!_queues.TryGetValue(key, out var queue))
            {
                _queues[key] = queue = new Queue<INatsJSMsg<byte[]>>();
                start = true;
            }

            queue.Enqueue(message);
        }

        if (start)
        {
            _ = Task.Run(() => DrainAsync(nats, key, cancellationToken), cancellationToken);
        }
    }

    private async Task DrainAsync(NatsConnection nats, string subject, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            INatsJSMsg<byte[]> message;
            lock (_queuesGate)
            {
                var queue = _queues[subject];
                if (queue.Count == 0)
                {
                    _ = _queues.Remove(subject);
                    return;
                }

                message = queue.Dequeue();
            }

            try
            {
                await ApplyAsync(nats, message, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.ZLogWarning($"A pushed transaction waits for redelivery: {error.GetType().Name:@Error}");
            }
        }
    }

    private async Task ApplyAsync(NatsConnection nats, INatsJSMsg<byte[]> message, CancellationToken cancellationToken)
    {
        if (PushNames.Database(message.Subject) is { } addressed
            && await catalog.FindAsync(addressed, cancellationToken).ConfigureAwait(false) is var record
            && (record is NotFound || record is TenantDatabaseRecord { Placement: var placed } && placed.Home != options.Region))
        {
            await message.AckAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return;
        }

        if (PushNames.Database(message.Subject) is not { } database || message.Data is not { } data)
        {
            logger.ZLogWarning($"A malformed pushed message is dropped");
            _applies?.Add(1, new KeyValuePair<string, object?>("state", "malformed"));
            await message.AckTerminateAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (PushNames.Origin(message.Subject) is { } dropped && message.Metadata is { } at)
            {
                Progressed(dropped, PushNames.Database(message.Subject), at.Sequence.Stream);
            }

            return;
        }

        if (await DecodeAsync(database, data, cancellationToken).ConfigureAwait(false) is not { } transaction)
        {
            logger.ZLogWarning($"An undecodable pushed message is parked");
            _applies?.Add(1, new KeyValuePair<string, object?>("state", "parked"));
            await ParkAsync(database, data, message, cancellationToken).ConfigureAwait(false);
            await message.AckAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return;
        }

        var backoff = new Backoff(TimeSpan.FromMilliseconds(100), MaxBackoff);
        HomeApplyResult result;
        while (true)
        {
            if (!RegionWriter)
            {
                await message.NakAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                return;
            }

            if (await catalog.FindAsync(database, cancellationToken).ConfigureAwait(false) is TenantDatabaseRecord { Placement: var placement }
                && placement.Home != options.Region)
            {
                await message.AckAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                return;
            }

            HomeApplyResult? applied;
            var started = Stopwatch.GetTimestamp();
            var reason = "missing_database";
            await _applying.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                applied = await apply.ApplyAsync(database, transaction, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is StorageBusyException or IOException or TimeoutException)
            {
                applied = null;
                reason = error.GetType().Name;
            }
            finally
            {
                _applyTime?.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                _applying.Release();
            }

            if (applied is not null)
            {
                result = applied;
                break;
            }

            _applies?.Add(1, new KeyValuePair<string, object?>("state", "unavailable"));
            _applyRetries?.Add(1, new KeyValuePair<string, object?>("reason", reason));
            var delay = backoff.Next();
            logger.ZLogWarning($"A home push apply retries after {delay.TotalMilliseconds} ms: {reason}");
            await message.AckProgressAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            await Task.Delay(delay, time, cancellationToken).ConfigureAwait(false);
        }

        await nats.PublishAsync(PushNames.Applied(transaction.Region!, database),
            JsonSerializer.SerializeToUtf8Bytes(result, HomeApplyJsonContext.Default.HomeApplyResult), cancellationToken: cancellationToken).ConfigureAwait(false);
        await message.AckAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (message.Metadata is { } metadata)
        {
            Progressed(transaction.Region!, database, metadata.Sequence.Stream);
        }

        _applies?.Add(1, new KeyValuePair<string, object?>("state", result.State));
    }

    private async Task ParkAsync(TenantContext database, byte[] data, INatsJSMsg<byte[]> message, CancellationToken cancellationToken)
    {
        var js = _js ?? throw new IOException("the push connection is not open");
        var origin = PushNames.Origin(message.Subject) ?? options.Region;
        var sequence = message.Metadata?.Sequence.Stream ?? 0UL;
        _ = await js.CreateOrUpdateStreamAsync(new StreamConfig(PushNames.ParkStream(options.Region), [PushNames.ParkSubjects(options.Region)])
        {
            Storage = StreamConfigStorage.File,
            Retention = StreamConfigRetention.Limits,
            Discard = StreamConfigDiscard.New,
            MaxBytes = 1L << 30,
            MaxMsgSize = MaxPayloadBytes,
            DuplicateWindow = TimeSpan.FromMinutes(10),
            NumReplicas = 1,
        }, cancellationToken).ConfigureAwait(false);
        var parked = await js.PublishAsync(
            PushNames.ParkSubject(options.Region, origin, database, sequence),
            data,
            opts: new NatsJSPubOpts { MsgId = "park-" + origin + "-" + sequence.ToString(CultureInfo.InvariantCulture) },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (parked.Error is { } error)
        {
            throw new IOException(error.Description);
        }
    }

    protected virtual ValueTask<PubAckResponse> PublishStoredAsync(string subject, byte[] payload, string id, NatsHeaders headers, CancellationToken cancellationToken) =>
        _js!.PublishAsync(subject, payload, opts: new NatsJSPubOpts { MsgId = id }, headers: headers, cancellationToken: cancellationToken);

    private void OnReverted(TenantContext database, HomeApplyResult result)
    {
        _reverts?.Add(1);
        logger.ZLogInformation($"The home reverted a local transaction; the replica pulls the home's rows");
        _ = Task.Run(async () =>
        {
            try
            {
                _ = await replicas.PullAsync(database, fromHome: true, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                logger.ZLogInformation($"A pull after a revert waits for the next notice: {error.GetType().Name:@Error}");
            }
        });
    }

    private async Task RetryAsync(Func<CancellationToken, Task> step, string failure, CancellationToken cancellationToken)
    {
        var backoff = new Backoff(TimeSpan.FromMilliseconds(200), MaxBackoff);
        while (true)
        {
            try
            {
                await step(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception error) when (!StreamReplicaCountExceedsCluster(error) && (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested))
            {
                logger.ZLogWarning($"{failure}: {error.GetType().Name:@Error}");
                await Task.Delay(backoff.Next(), time, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal const int StreamReplicasNotSupported = 10074;

    internal static bool StreamReplicaCountExceedsCluster(Exception error)
    {
        if (error is not NatsJSApiException { Error: { } api })
        {
            return false;
        }

        return api.ErrCode == StreamReplicasNotSupported;
    }

    private void RecordPublished(TenantContext database, string transaction, string home, long epoch, ulong sequence) =>
        logger.ZLogInformation($"Local transaction {transaction:@Transaction} for database {database.DatabaseId:@Database} stored in origin {options.Region:@Origin} at sequence {sequence:@Sequence}, addressed to {home:@Home} at epoch {epoch:@Epoch}");

    private async Task<RegionTransaction?> DecodeAsync(TenantContext database, byte[] data, CancellationToken cancellationToken)
    {
        var plain = await services.GetRequiredService<ITransactionCrypto>().OpenAsync(database, data, cancellationToken).ConfigureAwait(false);
        return plain is not null && TryDecode(plain, out var transaction) ? transaction : null;
    }

    internal static byte[] Encode(RegionTransaction transaction)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(transaction, HomeApplyJsonContext.Default.RegionTransaction);
        var packed = new byte[4 + LZ4.Block.GetMaxCompressedLength(json.Length)];
        BinaryPrimitives.WriteInt32BigEndian(packed, json.Length);
        var size = LZ4.Block.Compress(json, packed.AsSpan(4));
        return packed.AsSpan(0, 4 + size).ToArray();
    }

    internal static bool TryDecode(byte[] data, out RegionTransaction transaction)
    {
        transaction = null!;
        var length = data.Length < 4 ? 0 : BinaryPrimitives.ReadInt32BigEndian(data);
        if (length is <= 0 or > MaxPayloadBytes)
        {
            return false;
        }

        try
        {
            var json = new byte[length];
            if (LZ4.Block.Decompress(data.AsSpan(4), json, null) != length
                || JsonSerializer.Deserialize(json, HomeApplyJsonContext.Default.RegionTransaction) is not { Id: not null, Region: not null, Hlc: not null, Changes: not null } decoded)
            {
                return false;
            }

            transaction = decoded;
            return true;
        }
        catch (Exception error) when (error is JsonException or LZ4Exception)
        {
            return false;
        }
    }

    private sealed class LocalTransaction(HomePush push, TenantContext database, string home, long epoch, IReplicaWriter writer) : ILocalTransaction
    {
        private readonly List<KeyValuePair<string, double>> _timings = new(8);

        public IReadOnlyList<KeyValuePair<string, double>> Timings => _timings;

        public ValueTask<SqlStatementResult> ExecuteAsync(SqlStatement statement, CancellationToken cancellationToken) => writer.ExecuteAsync(statement, cancellationToken);

        public async ValueTask<LocalCommit> CommitAsync(bool homeDurable, CancellationToken cancellationToken)
        {
            var started = Stopwatch.GetTimestamp();
            var captured = await writer.CaptureAsync(cancellationToken).ConfigureAwait(false);
            if (captured.Count == 0)
            {
                await writer.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new LocalCommit(null, null);
            }

            if (captured.Count > HomeApplier.MaxChanges)
            {
                throw new LocalWriteRefusedException("a transaction of more than 1024 row changes commits at the database's home");
            }

            var changes = await PushChanges.FromCapturedAsync(writer, captured, cancellationToken).ConfigureAwait(false);
            Record("local_capture", ref started);
            var stamp = await push.Clock.NextAsync(cancellationToken).ConfigureAwait(false);
            Record("local_hlc", ref started);
            var transaction = new RegionTransaction(Ulid.NewUlid().ToString(), push.Region, stamp, changes);
            var payload = await push.Crypto.SealAsync(database, Encode(transaction), cancellationToken).ConfigureAwait(false);
            Record("local_seal", ref started);
            if (payload.Length > MaxPayloadBytes)
            {
                throw new LocalWriteRefusedException("a transaction larger than 8 MiB commits at the database's home");
            }

            var homeApplied = push._overlay.Add(database, 0, transaction);
            PubAckResponse acknowledged;
            try
            {
                acknowledged = await push.PublishStoredAsync(PushNames.Subject(push.Region, home, database), payload, transaction.Id!, Headers(stamp, epoch), cancellationToken).ConfigureAwait(false);
                acknowledged.EnsureSuccess();
                Record("push_publish", ref started);
            }
            catch (Exception error) when (error is NatsException or TimeoutException)
            {
                push._overlay.Drop(database, transaction.Id!);
                throw new LocalWriteRefusedException("the push stream did not store the transaction; it commits at the database's home", transaction.Id);
            }
            catch
            {
                push._overlay.Drop(database, transaction.Id!);
                throw;
            }

            push._overlay.Published(database, transaction.Id!, acknowledged.Seq);
            _ = push._pushed.AddOrUpdate(Key(database), acknowledged.Seq, (_, known) => Math.Max(known, acknowledged.Seq));
            var held = await push.Partner.HoldAsync(database, home, acknowledged.Seq, payload, cancellationToken).ConfigureAwait(false);
            Record("push_hold", ref started);
            try
            {
                await writer.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                throw new LocalOutcomeUnknownException("the transaction reached the push stream but its local commit failed; the home applies it", error);
            }

            await writer.DisposeAsync().ConfigureAwait(false);
            Record("local_commit", ref started);
            push._feed.Value?.Committed(database, transaction.Id!, captured);
            push.RecordPublished(database, transaction.Id!, home, epoch, acknowledged.Seq);
            if (!homeDurable && held)
            {
                return new LocalCommit(transaction.Id, null);
            }

            try
            {
                var result = await homeApplied.WaitAsync(HomeWait, push.Time, cancellationToken).ConfigureAwait(false);
                Record("home_wait", ref started);
                return new LocalCommit(transaction.Id, result);
            }
            catch (TimeoutException error)
            {
                throw new LocalOutcomeUnknownException("the home has not answered the transaction yet; it stays in the push stream and applies there", error);
            }
        }

        public ValueTask DisposeAsync() => writer.DisposeAsync();

        private void Record(string stage, ref long started)
        {
            var now = Stopwatch.GetTimestamp();
            _timings.Add(new(stage, Stopwatch.GetElapsedTime(started, now).TotalMilliseconds));
            started = now;
        }
    }
}
