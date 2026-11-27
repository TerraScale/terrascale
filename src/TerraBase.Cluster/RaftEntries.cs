using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;
using DotNext.IO;
using DotNext.IO.Log;
using DotNext.Net.Cluster;
using DotNext.Net.Cluster.Consensus.Raft;
using TerraScale.QuicRpc;

namespace TerraBase.Cluster;

internal static class RaftIdentity
{
    public static ClusterMemberId For(string nodeId)
    {
        Span<byte> hash = stackalloc byte[32];
        var maxBytes = Encoding.UTF8.GetMaxByteCount(nodeId.Length);
        Span<byte> utf8 = maxBytes <= 256 ? stackalloc byte[maxBytes] : new byte[maxBytes];
        var written = Encoding.UTF8.GetBytes(nodeId, utf8);
        SHA256.HashData(utf8[..written], hash);
        return new ClusterMemberId(hash[..ClusterMemberId.Size]);
    }

    public static ushort ShardOf(Ulid databaseId, int shardCount)
    {
        Span<byte> bytes = stackalloc byte[16];
        _ = databaseId.TryWriteBytes(bytes);
        return (ushort)(XxHash32.HashToUInt32(bytes) % (uint)shardCount);
    }
}

internal readonly struct RaftPayloadEntry(long term, int? commandId, bool isConfiguration, bool isSnapshot, ReadOnlyMemory<byte> payload) : IRaftLogEntry
{
    public long Term => term;

    public int? CommandId => commandId;

    public bool IsConfiguration => isConfiguration;

    public bool IsSnapshot => isSnapshot;

    public bool IsReusable => true;

    public long? Length => payload.Length;

    public bool TryGetMemory(out ReadOnlyMemory<byte> memory)
    {
        memory = payload;
        return true;
    }

    public ValueTask WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
        where TWriter : notnull, IAsyncBinaryWriter =>
        writer.WriteAsync(payload, null, token);
}

internal sealed class RaftEntryProducer(IReadOnlyList<RaftWireEntry> entries) : ILogEntryProducer<RaftPayloadEntry>
{
    private int _index = -1;

    public RaftPayloadEntry Current => Wrap(entries[_index]);

    public long RemainingCount => entries.Count - _index - 1;

    public ValueTask<bool> MoveNextAsync()
    {
        if (_index + 1 >= entries.Count)
        {
            return ValueTask.FromResult(false);
        }

        _index++;
        return ValueTask.FromResult(true);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public static RaftPayloadEntry Wrap(RaftWireEntry entry) => new(entry.Term, entry.CommandId, entry.IsConfiguration, false, entry.Payload);
}

internal sealed class EmptyEntryProducer : ILogEntryProducer<RaftPayloadEntry>
{
    public static EmptyEntryProducer Instance { get; } = new();

    private EmptyEntryProducer()
    {
    }

    public RaftPayloadEntry Current => default;

    public long RemainingCount => 0;

    public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(false);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal struct PooledRaftCts : IDisposable
{
    private const int MaxPoolSize = 64;

    private static readonly ConcurrentQueue<CancellationTokenSource> Pool = new();
    private static readonly Action<object?> CancelCallback = static state =>
    {
        if (state is CancellationTokenSource source)
        {
            source.Cancel();
        }
    };

    [ThreadStatic]
    private static CancellationTokenSource? ThreadLocal;

    private static int PoolCount;

    private CancellationTokenSource? _cts;
    private CancellationTokenRegistration _first;
    private CancellationTokenRegistration _second;

    private PooledRaftCts(CancellationTokenSource cts, CancellationTokenRegistration first, CancellationTokenRegistration second)
    {
        _cts = cts;
        _first = first;
        _second = second;
    }

    public readonly CancellationToken Token => _cts is { } cts ? cts.Token : CancellationToken.None;

    public readonly bool IsCancellationRequested => _cts is { IsCancellationRequested: true };

    public static PooledRaftCts CreateLinked(CancellationToken first, CancellationToken second)
    {
        var cts = Rent();
        var firstReg = first.CanBeCanceled ? first.UnsafeRegister(CancelCallback, cts) : default;
        var secondReg = !cts.IsCancellationRequested && second.CanBeCanceled ? second.UnsafeRegister(CancelCallback, cts) : default;
        return new PooledRaftCts(cts, firstReg, secondReg);
    }

    public static PooledRaftCts CreateLinkedWithTimeout(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var cts = Rent();
        var reg = cancellationToken.CanBeCanceled ? cancellationToken.UnsafeRegister(CancelCallback, cts) : default;
        if (!cts.IsCancellationRequested)
        {
            cts.CancelAfter(timeout);
        }

        return new PooledRaftCts(cts, reg, default);
    }

    public void Dispose()
    {
        var cts = _cts;
        if (cts is null)
        {
            return;
        }

        _cts = null;
        _first.Dispose();
        _first = default;
        _second.Dispose();
        _second = default;
        if (!cts.TryReset())
        {
            cts.Dispose();
            return;
        }

        if (ThreadLocal is null)
        {
            ThreadLocal = cts;
        }
        else if (Interlocked.Increment(ref PoolCount) <= MaxPoolSize)
        {
            Pool.Enqueue(cts);
        }
        else
        {
            Interlocked.Decrement(ref PoolCount);
            cts.Dispose();
        }
    }

    private static CancellationTokenSource Rent()
    {
        if (ThreadLocal is { } local)
        {
            ThreadLocal = null;
            return local;
        }

        if (Pool.TryDequeue(out var pooled))
        {
            Interlocked.Decrement(ref PoolCount);
            return pooled;
        }

        return new CancellationTokenSource();
    }
}

internal sealed record ReplicatedCommit(Ulid DatabaseId, CommitMetadata Commit)
{
    public byte[] Encode() => Encode(DatabaseId, Commit);

    public static byte[] Encode(Ulid databaseId, CommitMetadata commit)
    {
        var maxSize = 16 + ClusterCodec.EstimateCommitSize(commit);
        var rented = ArrayPool<byte>.Shared.Rent(maxSize);
        try
        {
            var span = rented.AsSpan();
            if (!databaseId.TryWriteBytes(span))
            {
                throw new InvalidOperationException("Failed to encode database id.");
            }

            var written = 16 + ClusterCodec.WriteCommitToSpan(span[16..], commit);
            return rented.AsSpan(0, written).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public static bool TryValidateAndPeek(ReadOnlySpan<byte> payload, out Ulid databaseId, out ReadOnlySpan<byte> originUtf8)
    {
        if (payload.IsEmpty)
        {
            databaseId = default;
            originUtf8 = [];
            return false;
        }

        var reader = new WireReader(payload);
        databaseId = reader.ReadUlid();
        _ = reader.ReadUlid();
        originUtf8 = reader.ReadBytesSpan();
        _ = reader.ReadVarUInt64();
        _ = reader.ReadInt64();
        _ = reader.ReadUInt16();
        _ = reader.ReadUInt16();
        _ = reader.ReadVarUInt64();
        _ = reader.ReadUInt8();
        var bookmarkCount = reader.ReadLength();
        for (var index = 0; index < bookmarkCount && !reader.IsFaulted; index++)
        {
            _ = reader.ReadBytesSpan();
            _ = reader.ReadVarUInt64();
        }

        _ = reader.ReadBytesSpan();
        if (!reader.IsComplete)
        {
            databaseId = default;
            originUtf8 = [];
            return false;
        }

        return true;
    }

    public static bool TryPeekHeader(ReadOnlySpan<byte> payload, out Ulid databaseId, out ReadOnlySpan<byte> originUtf8)
    {
        if (payload.Length < 33)
        {
            databaseId = default;
            originUtf8 = [];
            return false;
        }

        var reader = new WireReader(payload);
        databaseId = reader.ReadUlid();
        _ = reader.ReadUlid();
        originUtf8 = reader.ReadBytesSpan();
        if (reader.IsFaulted)
        {
            databaseId = default;
            originUtf8 = [];
            return false;
        }

        return true;
    }

    public static ReplicatedCommit? Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            return null;
        }

        var reader = new WireReader(payload);
        var databaseId = reader.ReadUlid();
        var commit = ClusterCodec.ReadCommit(ref reader);
        return reader.IsComplete ? new ReplicatedCommit(databaseId, commit) : null;
    }
}
