using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraBase.Replication;

namespace TerraBase.Transport;

/// <summary>
/// Bounded, checksum-protected replication chunks. A receiver acknowledges a
/// chunk sequence only after applying all records in it; a reconnect can then
/// request the following sequence without guessing a causal prefix.
/// </summary>
public sealed class ResumableReplicationStore : IReplicationTransport
{
    private sealed class StreamState
    {
        public required string StreamId { get; init; }
        public required IReadOnlyList<ReplicationStreamChunk> Chunks { get; init; }
        public required long FirstAvailableSequence { get; init; }
    }

    private readonly object gate = new();
    private readonly Dictionary<string, StreamState> streams = new(StringComparer.Ordinal);
    private readonly int maximumRetainedChunks;

    public ResumableReplicationStore(int maximumRetainedChunks = 1_024)
    {
        if (maximumRetainedChunks <= 0) throw new ArgumentOutOfRangeException(nameof(maximumRetainedChunks));
        this.maximumRetainedChunks = maximumRetainedChunks;
    }

    /// <summary>The maximum chunks retained for any stream.</summary>
    public int MaximumRetainedChunks => maximumRetainedChunks;

    /// <summary>
    /// Creates a stream from an immutable source snapshot. The stream id must be
    /// reused by reconnecting callers; creating a second stream with the same id
    /// is rejected to prevent a resume token from selecting another payload.
    /// </summary>
    public string CreateStream(
        ReplicationStreamRequest request,
        IEnumerable<ReplicationRecord> records)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(records);
        var source = records
            .Where(record => !request.Cursor.Contains(record.Dot))
            .ToArray();
        var chunks = Chunk(request.StreamId, request.Cursor, source, request.MaxRecords, request.MaxChunkBytes);
        lock (gate)
        {
            if (streams.ContainsKey(request.StreamId))
                throw new ArgumentException($"Replication stream '{request.StreamId}' already exists.", nameof(request));
            streams.Add(request.StreamId, new StreamState
            {
                StreamId = request.StreamId,
                Chunks = chunks,
                FirstAvailableSequence = chunks.Count == 0 ? 0 : chunks[0].Sequence,
            });
        }

        return request.StreamId;
    }

    /// <summary>Returns retention metadata for a stream.</summary>
    public ReplicationResumeResult GetResumeResult(string streamId)
    {
        var state = GetState(streamId);
        var last = state.Chunks.Count == 0 ? -1 : state.Chunks[^1].Sequence;
        return new ReplicationResumeResult(state.StreamId, state.FirstAvailableSequence, last, state.Chunks.Count == 0 || state.Chunks[^1].IsFinal);
    }

    /// <summary>Removes a stream after its receiver has converged.</summary>
    public bool RemoveStream(string streamId)
    {
        if (string.IsNullOrWhiteSpace(streamId)) return false;
        lock (gate) return streams.Remove(streamId);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ReplicationStreamChunk> StreamReplicationAsync(
        ReplicationStreamRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        StreamState state;
        ReplicationStreamChunk[] selected;
        lock (gate)
        {
            state = GetStateLocked(request.StreamId);
            if (request.ResumeAfterSequence < state.FirstAvailableSequence - 1)
                throw new ReplicationResumeException(request.StreamId, state.FirstAvailableSequence);

            selected = state.Chunks
                .Where(chunk => chunk.Sequence > request.ResumeAfterSequence)
                .ToArray();
        }

        foreach (var chunk in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Verify(chunk);
            yield return chunk;
            // Yielding between chunks gives a slow receiver a cancellation point
            // without retaining a worker or allocating an unbounded buffer.
            await Task.Yield();
        }
    }

    private StreamState GetState(string streamId)
    {
        if (string.IsNullOrWhiteSpace(streamId)) throw new ArgumentException("A stream id is required.", nameof(streamId));
        lock (gate) return GetStateLocked(streamId);
    }

    private StreamState GetStateLocked(string streamId) =>
        streams.TryGetValue(streamId, out var state)
            ? state
            : throw new ReplicationResumeException(streamId, 0);

    private IReadOnlyList<ReplicationStreamChunk> Chunk(
        string streamId,
        ReplicationCursor initialCursor,
        IReadOnlyList<ReplicationRecord> records,
        int maxRecords,
        int maxChunkBytes)
    {
        var chunks = new List<ReplicationStreamChunk>();
        var current = new List<ReplicationRecord>(Math.Min(maxRecords, records.Count));
        var cursor = new Dictionary<string, ulong>(initialCursor.Frontier, StringComparer.Ordinal);
        var bytes = 0;
        long sequence = 0;

        foreach (var record in records)
        {
            var recordBytes = EstimateBytes(record);
            if (recordBytes > maxChunkBytes)
                throw new ArgumentOutOfRangeException(nameof(maxChunkBytes), "A replication record exceeds the chunk bound.");
            if (current.Count > 0 && (current.Count >= maxRecords || bytes > maxChunkBytes - recordBytes))
            {
                chunks.Add(CreateChunk(streamId, sequence++, current, cursor, false, bytes));
                current = new List<ReplicationRecord>(Math.Min(maxRecords, records.Count));
                bytes = 0;
            }

            current.Add(record);
            bytes = checked(bytes + recordBytes);
            if (!cursor.TryGetValue(record.Dot.Origin, out var seen) || record.Dot.Sequence > seen)
                cursor[record.Dot.Origin] = record.Dot.Sequence;
        }

        if (current.Count > 0)
            chunks.Add(CreateChunk(streamId, sequence++, current, cursor, true, bytes));
        else
            chunks.Add(CreateChunk(streamId, sequence, [], cursor, true, 0));

        // A completed stream retains only a bounded suffix. Sequences remain
        // absolute so a stale resume token is rejected instead of silently
        // replaying a different prefix.
        if (chunks.Count > maximumRetainedChunks)
            chunks = chunks[^maximumRetainedChunks..];
        return new ReadOnlyCollection<ReplicationStreamChunk>(chunks);
    }

    private static ReplicationStreamChunk CreateChunk(
        string streamId,
        long sequence,
        IReadOnlyList<ReplicationRecord> records,
        IReadOnlyDictionary<string, ulong> cursor,
        bool isFinal,
        int estimatedBytes)
    {
        var copy = new ReadOnlyCollection<ReplicationRecord>(records.ToArray());
        var chunkCursor = new ReplicationCursor(cursor);
        var checksum = ReplicationChunkDigest.Compute(streamId, sequence, copy, chunkCursor, isFinal);
        return new ReplicationStreamChunk(streamId, sequence, copy, chunkCursor, isFinal, estimatedBytes, checksum);
    }

    private static int EstimateBytes(ReplicationRecord record)
    {
        var bodyBytes = record.CanonicalBody?.Length
            ?? (record.Body is { } body ? JsonSerializer.SerializeToUtf8Bytes(body).Length : 0);
        var contextBytes = record.Context.Sum(pair => Encoding.UTF8.GetByteCount(pair.Key) + sizeof(ulong));
        return checked(64 + Encoding.UTF8.GetByteCount(record.Dot.Origin) + contextBytes + bodyBytes);
    }

    private static void Verify(ReplicationStreamChunk chunk)
    {
        var expected = ReplicationChunkDigest.Compute(
            chunk.StreamId,
            chunk.Sequence,
            chunk.Records,
            chunk.Cursor,
            chunk.IsFinal);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expected),
                Encoding.ASCII.GetBytes(chunk.Checksum)))
            throw new InterNodeTransportException(
                InterNodeTransportErrorCode.ProtocolViolation,
                $"Replication chunk {chunk.Sequence} failed checksum verification.");
    }
}

internal static class ReplicationChunkDigest
{
    internal static string Compute(
        string streamId,
        long sequence,
        IReadOnlyList<ReplicationRecord> records,
        ReplicationCursor cursor,
        bool isFinal)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, streamId);
        Append(hash, sequence.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(hash, isFinal ? "1" : "0");
        foreach (var frontier in cursor.Frontier.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            Append(hash, frontier.Key);
            Append(hash, frontier.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        foreach (var record in records)
        {
            Append(hash, record.Dot.Origin);
            Append(hash, record.Dot.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(hash, record.Hlc.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(hash, record.Deleted ? "1" : "0");
            foreach (var context in record.Context.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                Append(hash, context.Key);
                Append(hash, context.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            if (record.CanonicalBody is { } canonical)
                hash.AppendData(canonical.Span);
            else if (record.Body is { } body)
                hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(body));
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }
}
