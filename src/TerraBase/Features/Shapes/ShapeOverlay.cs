using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using NATS.Client.Core;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.HomePush;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Shapes;

/// <summary>One captured row change of a local transaction, in the storage engine capture image format.</summary>
/// <param name="Type">1 insert, 0 update, -1 delete.</param>
/// <param name="Table">The table name.</param>
/// <param name="RowId">The row id in the writer's replica.</param>
/// <param name="Before">The before record, or null.</param>
/// <param name="After">The after record, or null.</param>
internal sealed record ProvisionalRow(int Type, string Table, long RowId, byte[]? Before, byte[]? After);

/// <summary>A transaction of this region that the replica's pages do not hold yet.</summary>
/// <param name="Id">The RegionTransaction id, or a new id for a forwarded write.</param>
/// <param name="Origin">The machine that committed it, or that received the forwarded answer.</param>
/// <param name="Rows">Its row changes, in commit order.</param>
/// <param name="HomeAfter">0 for a local transaction. For a write forwarded to the home, the home's first captured change
/// id of the commit: the pages hold it once the replica's head is past this id, and its row ids are the home's.</param>
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

/// <summary>
/// The local overlay as shapes see it (PIVOT F7): every local transaction of this region that the replica's pages do
/// not hold yet. Commits on this node arrive through <see cref="ILocalCommitFeed"/>; the other nodes of the region
/// send theirs over core NATS. Live tails show these as <c>provisional</c> until the home's pages confirm or revert them.
/// </summary>
internal sealed class ShapeOverlay(TerraBaseOptions options, TimeProvider time, ILogger<ShapeOverlay> logger) : BackgroundService, ILocalCommitFeed
{
    internal const int MaxPerDatabase = 1024;
    internal const long MaxBytes = 64L << 20;
    internal const int MaxMessageBytes = 1 << 20;
    private static readonly TimeSpan Kept = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<(string Organization, string Project, string Database), Pending> _databases = new();
    private readonly Channel<(TenantContext Database, ProvisionalTransaction Transaction)> _outgoing =
        Channel.CreateBounded<(TenantContext, ProvisionalTransaction)>(new BoundedChannelOptions(4096) { FullMode = BoundedChannelFullMode.DropOldest });
    private long _bytes;
    private NatsConnection? _nats;

    /// <summary>The home's answer to a forwarded write carries its commits' rows here; the region strips it.</summary>
    internal const string CommitHeader = "X-TerraBase-Commit-Rows";
    internal const int MaxHeaderBytes = 48 * 1024;

    private string Subjects => $"terrabase.local.{options.Region}.>";

    /// <summary>The value of <see cref="CommitHeader"/> for a forwarded write's commits, or null when they are too large.</summary>
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

    /// <summary>Adds the commits a forwarded write made at the home, from the home's <see cref="CommitHeader"/>.</summary>
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
                && Add(database, transaction with { Origin = options.MachineId }) && _nats is not null)
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
        if (Add(database, transaction) && _nats is not null)
        {
            _ = _outgoing.Writer.TryWrite((database, transaction));
        }
    }

    /// <summary>Pending transactions of a database, in arrival order.</summary>
    public IReadOnlyList<ProvisionalTransaction> Snapshot(TenantContext database) =>
        _databases.TryGetValue(Key(database), out var pending) ? pending.Snapshot() : [];

    /// <summary>Completes when a transaction of the database is added.</summary>
    public Task Changed(TenantContext database) => _databases.GetOrAdd(Key(database), _ => new Pending()).Changed;

    /// <summary>Drops transactions the replica's pages hold (confirmed or reverted at the home).</summary>
    public void Held(TenantContext database, IReadOnlyCollection<string> ids)
    {
        if (ids.Count > 0 && _databases.TryGetValue(Key(database), out var pending))
        {
            _ = Interlocked.Add(ref _bytes, -pending.Remove(ids));
        }
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
        if (Interlocked.Read(ref _bytes) + transaction.Bytes > MaxBytes)
        {
            logger.ZLogInformation($"The shape overlay is full; a local transaction shows in shapes once the home confirms it");
            return false;
        }

        var (added, freed) = pending.Add(transaction with { AddedAt = now });
        _ = Interlocked.Add(ref _bytes, (added ? transaction.Bytes : 0) - freed);
        return added;
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
        });
        _nats = nats;
        await Task.WhenAll(PublishAsync(nats, stoppingToken), SubscribeAsync(nats, stoppingToken)).ConfigureAwait(false);
    }

    private async Task PublishAsync(NatsConnection nats, CancellationToken cancellationToken)
    {
        await foreach (var (database, transaction) in _outgoing.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(transaction, ShapeOverlayJsonContext.Default.ProvisionalTransaction);
            if (payload.Length > MaxMessageBytes)
            {
                continue;
            }

            try
            {
                await nats.PublishAsync(Subject(database), payload, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.ZLogInformation($"A provisional shape change stays on this node: {error.GetType().Name:@Error}");
            }
        }
    }

    private async Task SubscribeAsync(NatsConnection nats, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var message in nats.SubscribeAsync<byte[]>(Subjects, cancellationToken: cancellationToken).ConfigureAwait(false))
                {
                    if (Database(message.Subject) is not { } database || message.Data is not { Length: > 0 and <= MaxMessageBytes } data)
                    {
                        continue;
                    }

                    ProvisionalTransaction? transaction;
                    try
                    {
                        transaction = JsonSerializer.Deserialize(data, ShapeOverlayJsonContext.Default.ProvisionalTransaction);
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
        $"terrabase.local.{options.Region}.{database.OrganizationId}.{database.ProjectId}.{database.DatabaseId}";

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
