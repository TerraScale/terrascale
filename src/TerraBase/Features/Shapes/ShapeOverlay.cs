using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.HomePush;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Shapes;

internal sealed record ProvisionalRow(int Type, string Table, long RowId, byte[]? Before, byte[]? After);

internal sealed record ProvisionalTransaction(string Id, string Origin, IReadOnlyList<ProvisionalRow> Rows, long HomeAfter = 0)
{
    [JsonIgnore]
    internal long Bytes { get; } = 256 + Rows.Sum(row => 64L + row.Table.Length + (row.Before?.Length ?? 0) + (row.After?.Length ?? 0));

    [JsonIgnore]
    internal long AddedAt { get; init; }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ProvisionalTransaction))]
[JsonSerializable(typeof(List<ProvisionalTransaction>))]
internal sealed partial class ShapeOverlayJsonContext : JsonSerializerContext;

internal sealed class ShapeOverlay(TerraBaseOptions options, TimeProvider time, ITransactionCrypto crypto, ILogger<ShapeOverlay> logger) : BackgroundService, ILocalCommitFeed
{
    internal const int MaxPerDatabase = 1024;
    internal const long MaxBytes = 64L << 20;
    internal const int MaxMessageBytes = 1 << 20;
    private static readonly TimeSpan Kept = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<(string Organization, string Project, string Database), Pending> _databases = new();
    private readonly Channel<(TenantContext Database, ProvisionalTransaction Transaction)> _outgoing =
        Channel.CreateBounded<(TenantContext, ProvisionalTransaction)>(new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.DropOldest });
    private long _bytes;
    private NatsConnection? _nats;
    private readonly TaskCompletionSource _subscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource _streamReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task Ready => Task.WhenAll(_subscribed.Task, _streamReady.Task);

    internal const string CommitHeader = "X-TerraBase-Commit-Rows";
    internal const int MaxHeaderBytes = 48 * 1024;

    private const string Subjects = "terrabase.provisional.*.*.*.*";

    internal static string? EncodeCommits(IReadOnlyList<IReadOnlyList<CapturedRowChange>> commits)
    {
        var transactions = new List<ProvisionalTransaction>(commits.Count);
        foreach (var commit in commits)
        {
            var rows = commit.Where(change => change.Images is not null)
                .Select(change => new ProvisionalRow(change.Type, change.Table, change.Images!.RowId, change.Images.Before, change.Images.After)).ToList();
            if (rows.Count > 0)
            {
                transactions.Add(new ProvisionalTransaction(Ulid.NewUlid().ToString(), "", rows, commit.Min(change => change.Images?.ChangeId ?? long.MaxValue)));
            }
        }

        if (transactions.Count == 0)
        {
            return null;
        }

        var encoded = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(transactions, ShapeOverlayJsonContext.Default.ListProvisionalTransaction));
        return encoded.Length <= MaxHeaderBytes ? encoded : null;
    }

    public void Forwarded(TenantContext database, string header)
    {
        ArgumentNullException.ThrowIfNull(database);
        List<ProvisionalTransaction>? transactions;
        try
        {
            transactions = header.Length <= MaxHeaderBytes
                ? JsonSerializer.Deserialize(Convert.FromBase64String(header), ShapeOverlayJsonContext.Default.ListProvisionalTransaction)
                : null;
        }
        catch (Exception error) when (error is JsonException or FormatException)
        {
            return;
        }

        foreach (var transaction in transactions ?? [])
        {
            if (transaction is { Id: { Length: > 0 }, HomeAfter: > 0, Rows: { } rows } && rows.All(row => row is { Table: { Length: > 0 }, Type: >= -1 and <= 1 })
                && Add(database, transaction with { Origin = options.MachineId }) && _nats is not null && transaction.Bytes <= MaxMessageBytes)
            {
                _ = _outgoing.Writer.TryWrite((database, transaction with { Origin = options.MachineId }));
            }
        }
    }

    public void Committed(TenantContext database, string transactionId, IReadOnlyList<CapturedRowChange> captured)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(captured);
        var rows = new List<ProvisionalRow>(captured.Count);
        foreach (var change in captured)
        {
            if (change.Images is { } images)
            {
                rows.Add(new ProvisionalRow(change.Type, change.Table, images.RowId, images.Before, images.After));
            }
        }

        var transaction = new ProvisionalTransaction(transactionId, options.MachineId, rows);
        if (Add(database, transaction) && _nats is not null && transaction.Bytes <= MaxMessageBytes)
        {
            _ = _outgoing.Writer.TryWrite((database, transaction));
        }
    }

    public IReadOnlyList<ProvisionalTransaction> Snapshot(TenantContext database) =>
        _databases.TryGetValue(Key(database), out var pending) ? pending.Snapshot() : [];

    public Task Changed(TenantContext database) => _databases.GetOrAdd(Key(database), _ => new Pending()).Changed;

    public void Held(TenantContext database, IReadOnlyCollection<string> ids)
    {
        if (ids.Count > 0 && _databases.TryGetValue(Key(database), out var pending))
        {
            _ = Interlocked.Add(ref _bytes, -pending.Remove(ids));
        }
    }

    internal async Task PurgeDeletedAsync(TenantContext database, CancellationToken cancellationToken)
    {
        Held(database, Snapshot(database).Select(transaction => transaction.Id).ToArray());
        if (_nats is not { } nats)
        {
            return;
        }

        if (!_streamReady.Task.IsCompletedSuccessfully)
        {
            throw new IOException("the provisional stream is not ready; database cleanup retries");
        }

        _ = await new NatsJSContext(nats).PurgeStreamAsync(StreamName, new StreamPurgeRequest
        {
            Filter = Subject(database),
        }, cancellationToken).ConfigureAwait(false);
    }

    internal bool Add(TenantContext database, ProvisionalTransaction transaction)
    {
        if (transaction.Rows.Count == 0)
        {
            return false;
        }

        var now = time.GetTimestamp();
        var pending = _databases.GetOrAdd(Key(database), _ => new Pending());
        _ = Interlocked.Add(ref _bytes, -pending.Expire(now - (long)(Kept.TotalSeconds * time.TimestampFrequency)));
        if (!Reserve(transaction.Bytes))
        {
            logger.ZLogInformation($"The shape overlay is full; a local transaction shows in shapes once the home confirms it");
            return false;
        }

        var (added, freed) = pending.Add(transaction with { AddedAt = now });
        _ = Interlocked.Add(ref _bytes, -(added ? 0 : transaction.Bytes) - freed);
        return added;
    }

    private bool Reserve(long bytes)
    {
        while (true)
        {
            var held = Interlocked.Read(ref _bytes);
            if (bytes > MaxBytes - held)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _bytes, held + bytes, held) == held)
            {
                return true;
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options is not { NatsUrl: { } natsUrl, NatsToken: { } natsToken })
        {
            return;
        }

        await using var nats = new NatsConnection(new NatsOpts
        {
            Url = natsUrl.ToString(),
            Name = $"terrabase-overlay-{options.Region}-{options.MachineId}",
            AuthOpts = new NatsAuthOpts { Token = natsToken },
            MaxReconnectRetry = -1,
            ReconnectWaitMin = TimeSpan.FromMilliseconds(100),
            ReconnectWaitMax = TimeSpan.FromSeconds(2),
            SubPendingChannelCapacity = 8,
        });
        _nats = nats;
        await Task.WhenAll(PublishAsync(nats, stoppingToken), SubscribeAsync(nats, stoppingToken)).ConfigureAwait(false);
    }

    private async Task PublishAsync(NatsConnection nats, CancellationToken cancellationToken)
    {
        var js = new NatsJSContext(nats);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await nats.ConnectAsync().ConfigureAwait(false);
                _ = await js.CreateOrUpdateStreamAsync(new StreamConfig(StreamName,
                    [$"terrabase.provisional.{options.Region}.>"])
                {
                    Storage = StreamConfigStorage.File,
                    Retention = StreamConfigRetention.Limits,
                    Discard = StreamConfigDiscard.Old,
                    MaxAge = Kept,
                    MaxBytes = MaxBytes,
                    MaxMsgSize = MaxMessageBytes,
                    DuplicateWindow = Kept,
                    NumReplicas = 1,
                    Placement = nats.ServerInfo?.Cluster is { Length: > 0 } ? new Placement { Tags = [$"region:{options.Region}"] } : null,
                }, cancellationToken).ConfigureAwait(false);
                _streamReady.TrySetResult();
                break;
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.ZLogInformation($"The provisional stream retries; shapes still receive home confirmations: {error.GetType().Name:@Error}");
                await Task.Delay(TimeSpan.FromSeconds(1), time, cancellationToken).ConfigureAwait(false);
            }
        }

        await foreach (var (database, transaction) in _outgoing.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(transaction, ShapeOverlayJsonContext.Default.ProvisionalTransaction);
            if (payload.Length > MaxMessageBytes - TransactionCrypto.Overhead)
            {
                continue;
            }

            try
            {
                payload = await crypto.SealAsync(database, payload, cancellationToken).ConfigureAwait(false);
                var ack = await js.PublishAsync(Subject(database), payload, opts: new NatsJSPubOpts { MsgId = transaction.Id },
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                ack.EnsureSuccess();
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.ZLogInformation($"A provisional shape change falls back to the home path: {error.GetType().Name:@Error}");
            }
        }
    }

    private async Task SubscribeAsync(NatsConnection nats, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var subscription = await nats.SubscribeCoreAsync<byte[]>(Subjects, cancellationToken: cancellationToken).ConfigureAwait(false);
                _ = await nats.PingAsync(cancellationToken).ConfigureAwait(false);
                _subscribed.TrySetResult();
                await foreach (var message in subscription.Msgs.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (Database(message.Subject) is not { } database || message.Data is not { Length: > 0 and <= MaxMessageBytes } data)
                    {
                        continue;
                    }

                    ProvisionalTransaction? transaction;
                    try
                    {
                        var plain = await crypto.OpenAsync(database, data, cancellationToken).ConfigureAwait(false);
                        transaction = plain is null ? null : JsonSerializer.Deserialize(plain, ShapeOverlayJsonContext.Default.ProvisionalTransaction);
                    }
                    catch (JsonException)
                    {
                        continue;
                    }

                    if (transaction is { Id: { Length: > 0 }, Origin: { } origin, Rows: { } rows } && origin != options.MachineId
                        && rows.All(row => row is { Table: { Length: > 0 }, Type: >= -1 and <= 1 }))
                    {
                        _ = Add(database, transaction);
                    }
                }
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.ZLogInformation($"Provisional shape changes resubscribe: {error.GetType().Name:@Error}");
                await Task.Delay(TimeSpan.FromSeconds(1), time, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private string Subject(TenantContext database) =>
        $"terrabase.provisional.{options.Region}.{database.OrganizationId}.{database.ProjectId}.{database.DatabaseId}";

    private string StreamName => "TERRABASE_PROVISIONAL_" + options.Region.ToUpperInvariant();

    private static TenantContext? Database(string subject) =>
        subject.Split('.') is [_, _, _, _, _, _] ? PushNames.Database(subject) : null;

    private static (string, string, string) Key(TenantContext database) => (database.OrganizationId, database.ProjectId!, database.DatabaseId!);

    private sealed class Pending
    {
        private readonly Lock _lock = new();
        private readonly List<ProvisionalTransaction> _entries = [];
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Changed
        {
            get
            {
                lock (_lock)
                {
                    return _changed.Task;
                }
            }
        }

        public IReadOnlyList<ProvisionalTransaction> Snapshot()
        {
            lock (_lock)
            {
                return [.. _entries];
            }
        }

        public (bool Added, long Freed) Add(ProvisionalTransaction transaction)
        {
            TaskCompletionSource changed;
            long freed = 0;
            lock (_lock)
            {
                if (_entries.Exists(entry => entry.Id == transaction.Id))
                {
                    return (false, 0);
                }

                if (_entries.Count >= MaxPerDatabase)
                {
                    freed = _entries[0].Bytes;
                    _entries.RemoveAt(0);
                }

                _entries.Add(transaction);
                changed = _changed;
                _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            changed.TrySetResult();
            return (true, freed);
        }

        public long Remove(IReadOnlyCollection<string> ids)
        {
            lock (_lock)
            {
                long freed = 0;
                _ = _entries.RemoveAll(entry =>
                {
                    var held = ids.Contains(entry.Id);
                    freed += held ? entry.Bytes : 0;
                    return held;
                });
                return freed;
            }
        }

        public long Expire(long before)
        {
            lock (_lock)
            {
                long freed = 0;
                _ = _entries.RemoveAll(entry =>
                {
                    var old = entry.AddedAt < before;
                    freed += old ? entry.Bytes : 0;
                    return old;
                });
                return freed;
            }
        }
    }
}
