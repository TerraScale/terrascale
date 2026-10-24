using System.Diagnostics.Metrics;
using System.Numerics;
using Google.Protobuf;
using TerraBase.Configuration;
using TerraBase.Features.Backup;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Replication;

/// <summary>
/// The sending side of log chunks. A chunk is a group that alone reaches the break-even, or one <em>bucket</em> of a database
/// copy's change ids, a power-of-two range sized to hold one to two times the break-even bytes, so every link maps a group
/// to the same chunk. A link that starts inside a bucket sends the rest of it inline and is aligned from the next one. A
/// chunk is put only when two or more peers still need it (their latest acks are before it) and its sealed envelope reaches
/// the break-even for that many readers; the first link to reach it decides for every link, so each chunk is put once, and
/// one already put is read whatever the count. A peer that could not read a chunk gets its batches inline for a minute.
/// Chunks are kept 10 minutes, then their objects are deleted; objects an earlier process left behind are deleted after an
/// hour.
/// </summary>
internal sealed partial class LogChunks : BackgroundService
{
    // R2 list prices (developers.cloudflare.com/r2/pricing, 2026-10-23), USD: a PUT (class A) and a GET (class B). Egress is
    // free and deletes cost nothing.
    internal const double PutCost = 4.50 / 1e6;
    internal const double GetCost = 0.36 / 1e6;

    // OCI outbound transfer past the free 10 TB a month, USD per byte: from South America, and from North America or Europe.
    internal const double SouthAmericaEgress = 0.025 / 1e9;
    internal const double Egress = 0.0085 / 1e9;

    private const int MaxChunks = 256;
    private const int MaxCopies = 4096;
    private static readonly TimeSpan Kept = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Orphaned = TimeSpan.FromHours(1);
    private static readonly TimeSpan Sweep = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Unreadable = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(1);

    private readonly TerraBaseOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<LogChunks> _logger;
    private readonly Counter<long> _bytes;
    private readonly Lock _gate = new();
    // Per database copy, bucket and bucket width (a solo group's change id and 0), the chunk decided for it.
    private readonly Dictionary<(TenantContext Database, Ulid Incarnation, long Bucket, long Width), Chunk> _chunks = [];
    // Per database copy, its bucket width and each peer machine's latest ack and when it was seen.
    private readonly Dictionary<(TenantContext Database, Ulid Incarnation), Copy> _acks = [];
    // Per peer machine, when it last could not read a chunk.
    private readonly Dictionary<string, long> _unreadable = [with(StringComparer.Ordinal)];

    /// <summary>
    /// The groups of <paramref name="groups"/> (read after <paramref name="from"/>, where the batches <paramref name="peer"/> has
    /// in flight end, or its ack <paramref name="acked"/> when none is) to send it now, and either their envelope or, when
    /// <paramref name="reads"/> and a chunk is cheaper, the chunk that holds them. Only the ack places the peer as a reader:
    /// batches in flight to a peer that refuses them are sent again from its ack.
    /// </summary>
    public async ValueTask<(IReadOnlyList<OriginGroup> Groups, ByteString Envelope, V1.LogChunk? Chunk)> BatchAsync(
        string peer, bool reads, TenantContext database, Ulid incarnation, long acked, long from, IReadOnlyList<OriginGroup> groups,
        CancellationToken cancellationToken)
    {
        bool unread;
        lock (_gate)
        {
            unread = _unreadable.TryGetValue(peer, out var failed) && _time.GetElapsedTime(failed) < Unreadable;
        }

        if (_store is null || !reads || unread)
        {
            return Inline(groups);
        }

        Seen(peer, database, incarnation, acked);
        var egress = _options.Region == "gru" ? SouthAmericaEgress : Egress;
        var least = BreakEven(2, egress);
        var inline = ChangeGroups.Encode(groups);
        if (inline.Length < least)
        {
            return Inline(groups, inline);
        }

        // A group that alone reaches the break-even is its own chunk. Smaller groups go in buckets of change ids that hold
        // one to two times the break-even at this page's shipped bytes per change id; a copy keeps its width while pages stay
        // within a factor of four of it, so every link cuts the same buckets.
        var solo = (groups.Count == 1 ? inline : ChangeGroups.Encode([groups[0]])).Length >= least;
        var width = solo ? 0 : 1L << BitOperations.Log2((ulong)Math.Max(1, 2 * least * (groups[^1].ChangeId - from) / inline.Length));
        lock (_gate)
        {
            if (!solo && _acks.TryGetValue((database, incarnation), out var copy))
            {
                width = copy.Width > 0 && width / 4 < copy.Width && copy.Width / 4 < width ? copy.Width : copy.Width = width;
            }
        }

        var bucket = solo ? groups[0].ChangeId : Bucket(groups[0].ChangeId, width);
        var end = solo ? 0 : groups.Count - 1;
        while (!solo && end >= 0 && Bucket(groups[end].ChangeId, width) != bucket)
        {
            end--;
        }

        if (!solo && end == groups.Count - 1)
        {
            // The bucket may still grow (or fills the page): nothing to decide yet.
            return Inline(groups, inline);
        }

        // Started inside the bucket: the rest of it inline, so the next one can be a chunk. A chunk already put is read
        // whatever the count (a GET costs less than a send); a new one needs two machines that still read it: their latest
        // acks are before its first change id.
        var inside = !solo && Bucket(from, width) == bucket;
        var key = (database, incarnation, inside ? bucket + 1 : bucket, width);
        var before = solo ? groups[0].ChangeId : (key.Item3 * width) + 1;
        var chunk = new Chunk(_time.GetTimestamp());
        Chunk? known;
        int readers;
        var claimed = false;
        lock (_gate)
        {
            readers = Readers(database, incarnation, before);
            if (!_chunks.TryGetValue(key, out known) && !inside && readers >= 2 && _chunks.Count < MaxChunks)
            {
                _chunks[key] = chunk;
                claimed = true;
            }
        }

        if (inside)
        {
            return known is not null || readers >= 2 ? Inline(groups, end) : Inline(groups, inline);
        }

        if (known is not null)
        {
            return await known.Sealed.ConfigureAwait(false) is { } reference ? Reference(groups, end, reference) : Inline(groups, end);
        }

        if (!claimed)
        {
            return Inline(groups, inline);
        }

        V1.LogChunk? put = null;
        try
        {
            var envelope = ChangeGroups.Encode([.. groups.Take(end + 1)]);
            put = envelope.Length < BreakEven(readers, egress) ? null : await PutAsync(envelope.Memory, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Links waiting on this bucket go on whatever happened here: inline unless a chunk was put.
            chunk.Complete(put);
            if (put is null)
            {
                lock (_gate)
                {
                    _ = _chunks.Remove(key);
                }
            }
        }

        return put is null ? Inline(groups, end) : Reference(groups, end, put);
    }

    /// <summary>Notes the ack of <paramref name="peer"/>, which <paramref name="reads"/> chunks: where it is, or that it could not read one.</summary>
    public void Acked(string peer, bool reads, V1.Ack ack)
    {
        ArgumentNullException.ThrowIfNull(ack);
        if (!reads)
        {
            return;
        }

        if (ack is { Refused: true, ChunkUnread: true })
        {
            lock (_gate)
            {
                _unreadable[peer] = _time.GetTimestamp();
            }
        }
        else if (!ack.Refused && ChangeGroups.Incarnation(ack.Database) is { } incarnation)
        {
            Seen(peer, ChangeGroups.Tenant(ack.Database), incarnation, (long)ack.ChangeId);
        }
    }

    // Every minute, forgets chunks older than Kept and deletes their objects; every hour, deletes objects of this machine an
    // earlier process left behind.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_store is null)
        {
            return;
        }

        using var timer = new PeriodicTimer(Sweep, _time);
        var orphansAt = long.MinValue;
        do
        {
            try
            {
                await ForgetAsync(_store, stoppingToken).ConfigureAwait(false);
                if (orphansAt == long.MinValue || _time.GetElapsedTime(orphansAt) >= Orphaned)
                {
                    orphansAt = _time.GetTimestamp();
                    await DeleteOrphansAsync(_store, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.ZLogWarning(error, $"Cleaning up log chunks failed; the next sweep tries again");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    // The bytes past which one chunk read by `readers` machines costs less than sending it to each: the uploader's egress, a
    // PUT and a GET per reader against the egress of every send.
    internal static double BreakEven(int readers, double egress) =>
        readers < 2 ? double.PositiveInfinity : (PutCost + (readers * GetCost)) / ((readers - 1) * egress);

    // The bucket of a change id; an ack of 0 (nothing yet) is before every bucket.
    private static long Bucket(long changeId, long width) => changeId == 0 ? -1 : (changeId - 1) / width;

    // The fresh peers whose latest ack is before change id `before`: the machines that still read a chunk starting there.
    // The caller holds _gate.
    private int Readers(TenantContext database, Ulid incarnation, long before) =>
        _acks.TryGetValue((database, incarnation), out var copy)
            ? copy.Peers.Values.Count(peer => _time.GetElapsedTime(peer.At) < Fresh && peer.Acked < before) : 0;

    private void Seen(string peer, TenantContext database, Ulid incarnation, long acked)
    {
        lock (_gate)
        {
            if (!_acks.TryGetValue((database, incarnation), out var copy))
            {
                if (_acks.Count >= MaxCopies)
                {
                    return;
                }

                _acks[(database, incarnation)] = copy = new Copy();
            }

            copy.Peers[peer] = (acked, _time.GetTimestamp());
        }
    }

    private (IReadOnlyList<OriginGroup>, ByteString, V1.LogChunk?) Inline(IReadOnlyList<OriginGroup> groups, ByteString? encoded = null)
    {
        var envelope = encoded ?? ChangeGroups.Encode(groups);
        _bytes.Add(envelope.Length, new KeyValuePair<string, object?>("path", "peer"));
        return (groups, envelope, null);
    }

    private (IReadOnlyList<OriginGroup>, ByteString, V1.LogChunk?) Inline(IReadOnlyList<OriginGroup> groups, int end) =>
        Inline([.. groups.Take(end + 1)]);

    private (IReadOnlyList<OriginGroup>, ByteString, V1.LogChunk?) Reference(IReadOnlyList<OriginGroup> groups, int end, V1.LogChunk chunk)
    {
        _bytes.Add(chunk.CalculateSize(), new KeyValuePair<string, object?>("path", "peer"));
        return ([.. groups.Take(end + 1)], ByteString.Empty, chunk);
    }

    // Puts the sealed envelope under a new key of this machine; null when the store refused it (the batch goes inline).
    private async Task<V1.LogChunk?> PutAsync(ReadOnlyMemory<byte> envelope, CancellationToken cancellationToken)
    {
        var key = Prefix + _options.MachineId + "/" + Ulid.NewUlid(_time.GetUtcNow());
        var (sealedBytes, seal) = Seal(key, envelope.Span);
        try
        {
            using var content = new MemoryStream(sealedBytes, writable: false);
            _requests.Add(1, Operation("put"));
            await _store!.PutAsync(key, content, cancellationToken).ConfigureAwait(false);
            _bytes.Add(sealedBytes.Length, new KeyValuePair<string, object?>("path", "store"));
            return new V1.LogChunk
            {
                Key = key,
                Seal = ByteString.CopyFrom(seal),
                Length = (uint)sealedBytes.Length,
                Decoded = (uint)ApplicationEnvelope.DecodedLength(envelope.Span),
            };
        }
        catch (Exception error)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _logger.ZLogWarning(error, $"A log chunk could not be put in the object store; its batch goes inline");
            }

            return null;
        }
    }

    private async Task ForgetAsync(IObjectStore store, CancellationToken cancellationToken)
    {
        List<Chunk> expired;
        lock (_gate)
        {
            var keys = _chunks.Where(entry => _time.GetElapsedTime(entry.Value.At) >= Kept).Select(entry => entry.Key).ToList();
            expired = [.. keys.Select(key => _chunks[key])];
            keys.ForEach(key => _chunks.Remove(key));
            foreach (var (key, copy) in _acks.ToList())
            {
                copy.Peers.Where(peer => _time.GetElapsedTime(peer.Value.At) >= Fresh).Select(peer => peer.Key).ToList().ForEach(name => copy.Peers.Remove(name));
                if (copy.Peers.Count == 0)
                {
                    _ = _acks.Remove(key);
                }
            }
        }

        foreach (var chunk in expired)
        {
            if (await chunk.Sealed.ConfigureAwait(false) is { } sealedChunk)
            {
                _requests.Add(1, Operation("delete"));
                _ = await store.DeleteAsync(sealedChunk.Key, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task DeleteOrphansAsync(IObjectStore store, CancellationToken cancellationToken)
    {
        HashSet<string> kept;
        lock (_gate)
        {
            kept = [.. _chunks.Values.Select(chunk => chunk.Sealed.IsCompletedSuccessfully ? chunk.Sealed.Result?.Key : null).OfType<string>()];
        }

        var prefix = Prefix + _options.MachineId + "/";
        _requests.Add(1, Operation("list"));
        await foreach (var stored in store.ListAsync(prefix, startAfter: null, cancellationToken).ConfigureAwait(false))
        {
            if (stored.Key.Length > prefix.Length && Ulid.TryParse(stored.Key.AsSpan(prefix.Length), out var id) && !kept.Contains(stored.Key)
                && _time.GetUtcNow() - id.Time >= Orphaned)
            {
                _requests.Add(1, Operation("delete"));
                _ = await store.DeleteAsync(stored.Key, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private sealed class Copy
    {
        public Dictionary<string, (long Acked, long At)> Peers { get; } = [with(StringComparer.Ordinal)];

        public long Width { get; set; }
    }

    private sealed class Chunk(long at)
    {
        private readonly TaskCompletionSource<V1.LogChunk?> _sealed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public long At { get; } = at;

        // The chunk put, or null when the first link sent the bucket inline.
        public Task<V1.LogChunk?> Sealed => _sealed.Task;

        public void Complete(V1.LogChunk? chunk) => _sealed.TrySetResult(chunk);
    }
}
