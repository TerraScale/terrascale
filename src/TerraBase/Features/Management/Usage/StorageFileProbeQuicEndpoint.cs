using System.Buffers;
using TerraBase.Cluster;
using TerraBase.Hosting;
using TerraScale.Contracts.Quic;
using TerraScale.QuicRpc;

namespace TerraBase.Features.Management.Usage;

internal sealed record StorageFileProbeRequest(IReadOnlyList<Ulid> Databases) : IBinaryMessage<StorageFileProbeRequest>
{
    public const int MaxDatabases = 64;

    public static StorageFileProbeRequest ReadFrom(ref WireReader reader)
    {
        var count = reader.ReadLength();
        if (count > MaxDatabases)
        {
            return new StorageFileProbeRequest([]);
        }

        List<Ulid> databases = new(count);
        for (var index = 0; index < count && !reader.IsFaulted; index++)
        {
            databases.Add(reader.ReadUlid());
        }

        return new StorageFileProbeRequest(databases);
    }

    public void WriteTo(IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteVarUInt64((ulong)Databases.Count);
        foreach (var database in Databases)
        {
            writer.WriteUlid(database);
        }
    }
}

internal sealed record StorageFileProbeReply(IReadOnlyList<long> Bytes) : IBinaryMessage<StorageFileProbeReply>
{
    public static StorageFileProbeReply ReadFrom(ref WireReader reader)
    {
        var count = reader.ReadLength();
        if (count > StorageFileProbeRequest.MaxDatabases)
        {
            return new StorageFileProbeReply([]);
        }

        List<long> bytes = new(count);
        for (var index = 0; index < count && !reader.IsFaulted; index++)
        {
            bytes.Add(reader.ReadVarInt64());
        }

        return new StorageFileProbeReply(bytes);
    }

    public void WriteTo(IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteVarUInt64((ulong)Bytes.Count);
        foreach (var length in Bytes)
        {
            writer.WriteVarInt64(length);
        }
    }
}

internal sealed class StorageFileProbeQuicEndpoint(StorageFiles files, ClusterTopology topology) : IQuicEndpoints
{
    public void Map(QuicRpcHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _ = host.MapUnary<StorageFileProbeRequest, StorageFileProbeReply>(QuicMessageType.StorageFileProbe, Handle);
    }

    private ValueTask<QuicHandlerOutcome<StorageFileProbeReply>> Handle(QuicCallContext context, StorageFileProbeRequest request, CancellationToken cancellationToken)
    {
        if (!topology.IsPeer(context))
        {
            return ValueTask.FromResult<QuicHandlerOutcome<StorageFileProbeReply>>(new QuicRemoteError("forbidden", "storage probes require an authenticated cluster peer"));
        }

        if (request.Databases.Count > StorageFileProbeRequest.MaxDatabases)
        {
            return ValueTask.FromResult<QuicHandlerOutcome<StorageFileProbeReply>>(new QuicRemoteError("too_many_databases", "a storage probe names at most one catalog page"));
        }

        return ValueTask.FromResult<QuicHandlerOutcome<StorageFileProbeReply>>(
            new StorageFileProbeReply([.. request.Databases.Select(database => files.Bytes(database) is { } bytes ? bytes : -1)]));
    }
}
