using System.Buffers.Binary;
using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Threading.Channels;
using NativeCompressions;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Clock;
using TerraBase.Features.HomeApply;
using TerraBase.Features.Regions;
using TerraBase.Features.Replicas;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.HomePush;

internal interface ILocalWrites
{
    ValueTask<ILocalTransaction?> BeginAsync(TenantContext database, CancellationToken cancellationToken);
}

internal interface ILocalTransaction : IAsyncDisposable
{
    ValueTask<SqlStatementResult> ExecuteAsync(SqlStatement statement, CancellationToken cancellationToken);

    ValueTask<LocalCommit> CommitAsync(bool homeDurable, CancellationToken cancellationToken);
}

internal sealed record LocalCommit(string? Id, HomeApplyResult? Home);

internal sealed class LocalOutcomeUnknownException(Exception inner)
    : Exception("the transaction reached the push stream but its local commit failed; the home applies it", inner);

internal sealed class HomePush(
    TerraBaseOptions options, IRegionPeers peers, IReplicaCatalog catalog, IDatabaseReplicas replicas, IHomeApply apply, IHlcClock clock,
    IServiceProvider services, IMeterFactory meters, TimeProvider time, ILogger<HomePush> logger) : BackgroundService, ILocalWrites
{
    internal const int MaxPayloadBytes = 8 << 20;
    private const int MaxParallelDatabases = 16;
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(5);

    private readonly LocalOverlay _overlay = new();
    private readonly Channel<(TenantContext Database, ulong Through)> _purges = Channel.CreateBounded<(TenantContext, ulong)>(
        new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Lock _queuesGate = new();
    private readonly Dictionary<string, Queue<INatsJSMsg<byte[]>>> _queues = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _applying = new(MaxParallelDatabases, MaxParallelDatabases);
    private readonly Meter _meter = meters.Create(Telemetry.MeterName);
    private IReadOnlyList<RegionPeer> _peers = [];
    private NatsConnection? _nats;
    private NatsJSContext? _js;
    private volatile bool _ready;
    private Histogram<double>? _lag;
    private Counter<long>? _applies;
    private Counter<long>? _reverts;

    internal LocalOverlay Overlay => _overlay;

    private IHlcClock Clock => clock;

    private string Region => options.Region;

    internal bool Ready => _ready;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _peers = await peers.OtherRegionsAsync(cancellationToken).ConfigureAwait(false);
        if (_peers.Count > 0 && options is { NatsUrl: { } natsUrl, NatsToken: { } natsToken })
        {
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
            _ = _meter.CreateObservableGauge("terrabase.push.pending", () => _overlay.Count, "{transaction}", "Local transactions the home has not confirmed.");
            replicas.Overlay = _overlay.RestoreAsync;
            _overlay.Held += (database, through) => _purges.Writer.TryWrite((database, through));
            _overlay.Reverted += OnReverted;
        }
        else if (_peers.Count > 0)
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
        await Task.WhenAll([
            NoticesAsync(nats, stoppingToken),
            PurgesAsync(js, stoppingToken),
            .. _peers.Select(peer => peer.Region).Distinct(StringComparer.Ordinal).Select(origin => ConsumeAsync(js, nats, origin, stoppingToken)),
        ]).ConfigureAwait(false);
    }

    public async ValueTask<ILocalTransaction?> BeginAsync(TenantContext database, CancellationToken cancellationToken)
    {
        if (!_ready || _js is null || services.GetService<ILocalWriteClock>() is { Allowed: false })
        {
            return null;
        }

        if (await catalog.FindAsync(database, cancellationToken).ConfigureAwait(false) is not TenantDatabaseRecord { Placement: var placement }
            || placement.Home == options.Region || !placement.Replicas.Contains(options.Region))
        {
            return null;
        }

        return await replicas.BeginWriteAsync(database, cancellationToken).ConfigureAwait(false) is { } writer
            ? new LocalTransaction(this, database, placement.Home, writer)
            : null;
    }

    internal async Task<PubAckResponse> PublishAgainAsync(TenantContext database, string id, CancellationToken cancellationToken)
    {
        var transaction = _overlay.Find(database, id) ?? throw new InvalidOperationException("the transaction is not in the overlay");
        var home = await catalog.FindAsync(database, cancellationToken).ConfigureAwait(false) is TenantDatabaseRecord { Placement.Home: var placed }
            ? placed
            : throw new InvalidOperationException("the database has no placement");
        return await _js!.PublishAsync(PushNames.Subject(options.Region, home, database), Encode(transaction), opts: new NatsJSPubOpts { MsgId = id },
            headers: Headers(transaction.Hlc!.Value), cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    internal async Task<ulong?> ReplayFromAsync(string origin, TenantContext database, string transactionId, CancellationToken cancellationToken)
    {
        var js = _js ?? throw new InvalidOperationException("this node has no push stream connection");
        var stream = PushNames.Stream(origin);
        var scan = await js.CreateOrderedConsumerAsync(stream, new NatsJSOrderedConsumerOpts
        {
            FilterSubjects = [PushNames.Subject(origin, options.Region, database)],
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

    internal async Task<int> DrainFormerHomeAsync(TenantContext database, string formerHome, CancellationToken cancellationToken)
    {
        if (_js is not { } js || _nats is not { } nats)
        {
            return 0;
        }

        var applied = 0;
        foreach (var origin in _peers.Select(peer => peer.Region).Append(options.Region).Where(region => region != formerHome).Distinct(StringComparer.Ordinal))
        {
            var scan = await js.CreateOrderedConsumerAsync(PushNames.Stream(origin), new NatsJSOrderedConsumerOpts
            {
                FilterSubjects = [PushNames.Subject(origin, formerHome, database)],
                DeliverPolicy = ConsumerConfigDeliverPolicy.All,
            }, cancellationToken).ConfigureAwait(false);
            while (true)
            {
                var read = 0;
                await foreach (var message in scan.FetchNoWaitAsync<byte[]>(new NatsJSFetchOpts { MaxMsgs = 256 }, cancellationToken: cancellationToken).ConfigureAwait(false))
                {
                    read++;
                    if (message.Data is not { } data || !TryDecode(data, out var transaction))
                    {
                        continue;
                    }

                    var result = await apply.ApplyAsync(database, transaction, cancellationToken).ConfigureAwait(false)
                        ?? throw new StorageBusyException();
                    await nats.PublishAsync(PushNames.Applied(transaction.Region!, database),
                        JsonSerializer.SerializeToUtf8Bytes(result, TerraBaseJsonContext.Default.HomeApplyResult), cancellationToken: cancellationToken).ConfigureAwait(false);
                    _applies?.Add(1, new KeyValuePair<string, object?>("state", result.State));
                    applied++;
                }

                if (read == 0)
                {
                    break;
                }
            }
        }

        return applied;
    }

    private static NatsHeaders Headers(HlcTimestamp stamp)
    {
        Span<byte> encoded = stackalloc byte[HlcTimestamp.EncodedLength];
        stamp.WriteTo(encoded);
        return new NatsHeaders { ["Tb-Hlc"] = Convert.ToHexString(encoded) };
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
                    && TryDecode(data, out var transaction))
                {
                    _ = _overlay.Add(database, metadata.Sequence.Stream, transaction);
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
                        result = JsonSerializer.Deserialize(data, TerraBaseJsonContext.Default.HomeApplyResult);
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

    private async Task PurgesAsync(NatsJSContext js, CancellationToken cancellationToken)
    {
        await foreach (var (database, through) in _purges.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                _ = await js.PurgeStreamAsync(PushNames.Stream(options.Region), new StreamPurgeRequest
                {
                    Filter = $"terrabase.push.{options.Region}.*.{database.OrganizationId}.{database.ProjectId}.{database.DatabaseId}",
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
        var backoff = TimeSpan.FromMilliseconds(200);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var name = PushNames.Consumer(options.Region);
                var consumer = await js.CreateOrUpdateConsumerAsync(PushNames.Stream(origin), new ConsumerConfig(name)
                {
                    DurableName = name,
                    FilterSubject = PushNames.HomeFilter(origin, options.Region),
                    AckPolicy = ConsumerConfigAckPolicy.Explicit,
                    DeliverPolicy = ConsumerConfigDeliverPolicy.All,
                    AckWait = TimeSpan.FromSeconds(60),
                    MaxAckPending = 4096,
                }, cancellationToken).ConfigureAwait(false);
                if (reset)
                {
                    await consumer.ResetAsync(consumer.Info.AckFloor.StreamSeq + 1, cancellationToken).ConfigureAwait(false);
                    reset = false;
                }

                backoff = TimeSpan.FromMilliseconds(200);
                await foreach (var message in consumer.ConsumeAsync<byte[]>(opts: new NatsJSConsumeOpts { MaxMsgs = 256 }, cancellationToken: cancellationToken)
                    .ConfigureAwait(false))
                {
                    Dispatch(nats, message, cancellationToken);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.ZLogInformation($"The push from {origin} is not consumed yet: {error.GetType().Name:@Error}");
                await Task.Delay(backoff, time, cancellationToken).ConfigureAwait(false);
                backoff = TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, backoff.Ticks * 2));
            }
        }
    }

    private void Dispatch(NatsConnection nats, INatsJSMsg<byte[]> message, CancellationToken cancellationToken)
    {
        var start = false;
        lock (_queuesGate)
        {
            if (!_queues.TryGetValue(message.Subject, out var queue))
            {
                _queues[message.Subject] = queue = new Queue<INatsJSMsg<byte[]>>();
                start = true;
            }

            queue.Enqueue(message);
        }

        if (start)
        {
            _ = Task.Run(() => DrainAsync(nats, message.Subject, cancellationToken), cancellationToken);
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

            await _applying.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await ApplyAsync(nats, message, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.ZLogWarning($"A pushed transaction waits for redelivery: {error.GetType().Name:@Error}");
            }
            finally
            {
                _applying.Release();
            }
        }
    }

    private async Task ApplyAsync(NatsConnection nats, INatsJSMsg<byte[]> message, CancellationToken cancellationToken)
    {
        if (PushNames.Database(message.Subject) is not { } database || message.Data is not { } data || !TryDecode(data, out var transaction))
        {
            logger.ZLogWarning($"A malformed pushed message is dropped");
            _applies?.Add(1, new KeyValuePair<string, object?>("state", "malformed"));
            await message.AckTerminateAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return;
        }

        var backoff = TimeSpan.FromMilliseconds(100);
        HomeApplyResult result;
        while (true)
        {
            HomeApplyResult? applied;
            try
            {
                applied = await apply.ApplyAsync(database, transaction, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is StorageBusyException or IOException or TimeoutException)
            {
                applied = null;
            }

            if (applied is not null)
            {
                result = applied;
                break;
            }

            _applies?.Add(1, new KeyValuePair<string, object?>("state", "unavailable"));
            await message.AckProgressAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            await Task.Delay(backoff, time, cancellationToken).ConfigureAwait(false);
            backoff = TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, backoff.Ticks * 2));
        }

        await nats.PublishAsync(PushNames.Applied(transaction.Region!, database),
            JsonSerializer.SerializeToUtf8Bytes(result, TerraBaseJsonContext.Default.HomeApplyResult), cancellationToken: cancellationToken).ConfigureAwait(false);
        await message.AckAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        _applies?.Add(1, new KeyValuePair<string, object?>("state", result.State));
    }

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
        var backoff = TimeSpan.FromMilliseconds(200);
        while (true)
        {
            try
            {
                await step(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.ZLogWarning($"{failure}: {error.GetType().Name:@Error}");
                await Task.Delay(backoff, time, cancellationToken).ConfigureAwait(false);
                backoff = TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, backoff.Ticks * 2));
            }
        }
    }

    internal static byte[] Encode(RegionTransaction transaction)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(transaction, TerraBaseJsonContext.Default.RegionTransaction);
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
                || JsonSerializer.Deserialize(json, TerraBaseJsonContext.Default.RegionTransaction) is not { Id: not null, Region: not null, Hlc: not null, Changes: not null } decoded)
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

    private sealed class LocalTransaction(HomePush push, TenantContext database, string home, IReplicaWriter writer) : ILocalTransaction
    {
        public ValueTask<SqlStatementResult> ExecuteAsync(SqlStatement statement, CancellationToken cancellationToken) => writer.ExecuteAsync(statement, cancellationToken);

        public async ValueTask<LocalCommit> CommitAsync(bool homeDurable, CancellationToken cancellationToken)
        {
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
            var stamp = await push.Clock.NextAsync(cancellationToken).ConfigureAwait(false);
            var transaction = new RegionTransaction(Ulid.NewUlid().ToString(), push.Region, stamp, changes);
            var payload = Encode(transaction);
            if (payload.Length > MaxPayloadBytes)
            {
                throw new LocalWriteRefusedException("a transaction larger than 8 MiB commits at the database's home");
            }

            PubAckResponse acknowledged;
            try
            {
                acknowledged = await push._js!.PublishAsync(PushNames.Subject(push.Region, home, database), payload,
                    opts: new NatsJSPubOpts { MsgId = transaction.Id }, headers: Headers(stamp), cancellationToken: cancellationToken).ConfigureAwait(false);
                acknowledged.EnsureSuccess();
            }
            catch (Exception error) when (error is NatsException or TimeoutException)
            {
                throw new LocalWriteRefusedException("the push stream did not store the transaction; it commits at the database's home");
            }

            var homeApplied = push._overlay.Add(database, acknowledged.Seq, transaction);
            try
            {
                await writer.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                throw new LocalOutcomeUnknownException(error);
            }

            await writer.DisposeAsync().ConfigureAwait(false);
            return homeDurable
                ? new LocalCommit(transaction.Id, await homeApplied.WaitAsync(cancellationToken).ConfigureAwait(false))
                : new LocalCommit(transaction.Id, null);
        }

        public ValueTask DisposeAsync() => writer.DisposeAsync();
    }
}
