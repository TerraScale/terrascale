using System.Text.Json;
using System.Text.Json.Serialization;
using TerraBase.Engine;
using TerraBase.Features.Memory;
using TerraBase.Hosting;
using static TerraBase.Features.Management.SystemCatalog;

namespace TerraBase.Features.Management.Queries;

internal enum QueryShapeAccept
{
    Accepted,
    Duplicate,
    Capacity,
}

// A watermark and its statistics commit together. Unknown marker formats are never overwritten.
internal sealed record QueryShapeSenderRecord(
    [property: JsonRequired] string Format,
    [property: JsonRequired] string Sender,
    [property: JsonRequired] string Epoch,
    [property: JsonRequired] long Sequence,
    [property: JsonRequired] DateTimeOffset LastSeenAt);

internal sealed partial class QueryShapeRecorder
{
    private const string Senders = "query_shape_senders";
    private const int MaxSenders = 1024;
    private const int MaxSenderEpochs = 16;
    internal static readonly TimeSpan RetryLifetime = TimeSpan.FromDays(1);
    private static readonly TimeSpan SenderRetirement = TimeSpan.FromDays(7);

    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly Queue<(DatabaseOwner Owner, (string Collection, string Shape) Key)> _turns = new();
    private Outgoing? _outgoing;
    private long _sequence;

    // Network I/O holds only _send, so slow acknowledgments do not block reads or new observations.
    internal async ValueTask<bool> SendAsync(
        Func<long, QueryShapeForwardPayload, CancellationToken, Task<bool>> send, CancellationToken cancellationToken)
    {
        await _send.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_disposed || options.CatalogPrimary is null)
                {
                    return false;
                }
            }

            // Pending reservations cover retained snapshots. This covers temporary snapshots, JSON and LZ4 buffers.
            using var reservation = memory.TryReserve(MemoryLane.Replication, QueryShapeForwarding.ReservationBytes);
            if (reservation is null)
            {
                return false;
            }

            Outgoing? outgoing;
            await _flush.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_outgoing is { } expired && time.GetUtcNow() - expired.Payload.FirstSentAt >= RetryLifetime)
                {
                    DropSend(expired, "send_expired");
                }

                _outgoing ??= TakeOutgoing();
                outgoing = _outgoing;
            }
            finally
            {
                _flush.Release();
            }

            if (outgoing is null)
            {
                return false;
            }

            // Throws on ambiguous outcomes: leave the immutable sequence and snapshot intact for the next attempt.
            var accepted = await send(outgoing.Sequence, outgoing.Payload, cancellationToken).ConfigureAwait(false);
            await _flush.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (accepted)
                {
                    Complete(outgoing.Batch, committed: true);
                    _outgoing = null;
                }
                else
                {
                    // InvalidArgument is known to precede writes; retrying this immutable payload cannot help.
                    DropSend(outgoing, "send_invalid");
                }
            }
            finally
            {
                _flush.Release();
            }

            return true;
        }
        finally
        {
            _send.Release();
        }
    }

    private Outgoing? TakeOutgoing()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return null;
            }

            if (_turns.Count == 0)
            {
                foreach (var (owner, pending) in _pending)
                {
                    foreach (var key in pending.Keys)
                    {
                        _turns.Enqueue((owner, key));
                    }
                }
            }

            var items = new List<QueryShapeTransfer>();
            long bytes = 512;
            while (_turns.TryPeek(out var turn) && items.Count < QueryShapeForwarding.MaxItems)
            {
                if (!_pending.TryGetValue(turn.Owner, out var pending) || !pending.TryGetValue(turn.Key, out var entry))
                {
                    _turns.Dequeue();
                    continue;
                }

                // JSON escapes need at most six bytes per UTF-16 code unit. Include fields, numeric values and envelope.
                var size = 1024L + 6L * (turn.Key.Shape.Length + turn.Key.Collection.Length + turn.Owner.OrganizationId.Length
                    + turn.Owner.ProjectId.Length + turn.Owner.DatabaseId.Length);
                if (bytes + size > QueryShapeForwarding.MaxBytes)
                {
                    break;
                }

                _turns.Dequeue();
                bytes += size;
                items.Add(new QueryShapeTransfer(turn.Owner.OrganizationId, turn.Owner.ProjectId, turn.Owner.DatabaseId,
                    Stats(entry.Id, turn.Key.Collection, turn.Key.Shape, entry.Counts)));
                entry.Flushing = true;
                entry.AfterFlush = default;
            }

            if (items.Count == 0)
            {
                return null;
            }

            var batch = items.GroupBy(item => new DatabaseOwner(item.OrganizationId, item.ProjectId, item.DatabaseId))
                .Select(group => new Taken(group.Key, [.. group.Select(item => item.Stats)])).ToArray();
            return new Outgoing(checked(++_sequence), new QueryShapeForwardPayload(items, time.GetUtcNow()), batch);
        }
    }

    private void DropSend(Outgoing outgoing, string reason)
    {
        _dropped.Add(outgoing.Payload.Items.Aggregate(0L, (total, item) => Add(total, item.Stats.Queries)),
            new KeyValuePair<string, object?>("reason", reason));
        Complete(outgoing.Batch, committed: true);
        _outgoing = null;
    }

    // The caller validates and admits the entire decoded batch before entering this persistence operation.
    internal async ValueTask<QueryShapeAccept> AcceptAsync(
        string sender, string epoch, long sequence, QueryShapeForwardPayload payload, CancellationToken cancellationToken)
    {
        await _flush.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return QueryShapeAccept.Capacity;
                }
            }

            // A remote write cannot obscure an earlier local commit whose disposal threw.
            await SettleAsync(cancellationToken).ConfigureAwait(false);
            var (accepted, dropped, unsupported) = await catalog.WriteDurableAsync(async transaction =>
            {
                var key = SenderKey(sender, epoch);
                var document = await transaction.GetAsync(Senders, key, cancellationToken).ConfigureAwait(false);
                var previous = document is null ? null : ReadSender(document.Json, key);
                if (document is not null && previous is null)
                {
                    return (QueryShapeAccept.Capacity, 0L, 0L);
                }

                if (previous is not null && sequence <= previous.Sequence)
                {
                    return (QueryShapeAccept.Duplicate, 0L, 0L);
                }

                var now = time.GetUtcNow();
                if (previous is null && !await AdmitSenderAsync(transaction, sender, now, cancellationToken).ConfigureAwait(false))
                {
                    return (QueryShapeAccept.Capacity, 0L, 0L);
                }

                var batch = payload.Items.GroupBy(item => new DatabaseOwner(item.OrganizationId, item.ProjectId, item.DatabaseId))
                    .Select(group => new Taken(group.Key, [.. group.Select(item => item.Stats)])).ToArray();
                var (atCapacity, unreadable) = await WriteShapesAsync(transaction, batch, flushId: null, cancellationToken).ConfigureAwait(false);
                await PutAsync(transaction, Senders, key, new QueryShapeSenderRecord(Format, sender, epoch, sequence, now),
                    TerraBaseJsonContext.Default.QueryShapeSenderRecord, cancellationToken).ConfigureAwait(false);
                return (QueryShapeAccept.Accepted, atCapacity, unreadable);
            }, cancellationToken).ConfigureAwait(false);
            if (dropped > 0)
            {
                _dropped.Add(dropped, new KeyValuePair<string, object?>("reason", "persisted_limit"));
            }
            if (unsupported > 0)
            {
                _dropped.Add(unsupported, new KeyValuePair<string, object?>("reason", "unsupported_format"));
            }

            return accepted;
        }
        finally
        {
            _flush.Release();
        }
    }

    private static async ValueTask<bool> AdmitSenderAsync(
        IStorageTransaction transaction, string sender, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var count = await transaction.CountDocumentsAsync(Senders, cancellationToken).ConfigureAwait(false);
        if (count > MaxSenders)
        {
            return false;
        }

        var epochs = 0;
        string? after = null;
        DocumentPage page;
        do
        {
            page = await transaction.ListAsync(Senders, after, 128, 1024 * 1024, cancellationToken).ConfigureAwait(false);
            foreach (var document in page.Documents)
            {
                var saved = ReadSender(document.Json, document.Id);
                if (saved is not null && saved.LastSeenAt <= now - SenderRetirement)
                {
                    await transaction.DeleteAsync(Senders, document.Id, cancellationToken).ConfigureAwait(false);
                    count--;
                }
                else if (document.Id.LastIndexOf(':') == sender.Length
                    && document.Id.AsSpan(0, sender.Length).SequenceEqual(sender.AsSpan()))
                {
                    epochs++;
                }

                after = document.Id;
            }
        }
        while (page.HasMore);

        return count < MaxSenders && epochs < MaxSenderEpochs;
    }

    private static QueryShapeSenderRecord? ReadSender(ReadOnlyMemory<byte> json, string id)
    {
        if (json.Length > 2048)
        {
            return null;
        }

        try
        {
            var saved = JsonSerializer.Deserialize(json.Span, TerraBaseJsonContext.Default.QueryShapeSenderRecord);
            Span<byte> epoch = stackalloc byte[16];
            return saved is not null && saved.Format == Format && saved.Sender is { Length: > 0 and <= 128 }
                && saved.Epoch is { Length: 24 } && Convert.TryFromBase64String(saved.Epoch, epoch, out var length) && length == 16
                && saved.Sequence > 0 && saved.LastSeenAt != default && SenderKey(saved.Sender, saved.Epoch) == id
                ? saved : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string SenderKey(string sender, string epoch) => sender + ":" + epoch;
    private sealed record Outgoing(long Sequence, QueryShapeForwardPayload Payload, Taken[] Batch);
}
