using System.Buffers;
using TerraBase.Cluster;
using TerraBase.Continuity;
using TerraBase.Hosting;
using TerraScale.Contracts.Continuity;
using TerraScale.Contracts.Quic;
using TerraScale.QuicRpc;

namespace TerraBase.Features.InternalApi;

internal sealed record RewindDatabaseRequest(Ulid DatabaseId, string Bookmark) : IBinaryMessage<RewindDatabaseRequest>
{
    public static RewindDatabaseRequest ReadFrom(ref WireReader reader) => new(reader.ReadUlid(), reader.ReadString());

    public void WriteTo(IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteUlid(DatabaseId);
        writer.WriteString(Bookmark);
    }
}

internal sealed record RewindDatabaseReply(bool Applied) : IBinaryMessage<RewindDatabaseReply>
{
    public static RewindDatabaseReply ReadFrom(ref WireReader reader) => new(reader.ReadBool());

    public void WriteTo(IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBool(Applied);
    }
}

internal sealed class RewindDatabaseQuicEndpoint(BackupContinuityService continuity, ClusterTopology topology) : IQuicEndpoints
{
    public void Map(QuicRpcHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _ = host.MapUnary<RewindDatabaseRequest, RewindDatabaseReply>(QuicMessageType.RewindDatabase, HandleAsync);
    }

    private async ValueTask<QuicHandlerOutcome<RewindDatabaseReply>> HandleAsync(QuicCallContext context, RewindDatabaseRequest request, CancellationToken cancellationToken)
    {
        if (!topology.IsPeer(context))
        {
            return new QuicRemoteError("forbidden", "rewind requires an authenticated cluster peer");
        }

        if (!CausalBookmark.TryDecode(request.Bookmark, out var bookmark))
        {
            return new QuicRemoteError("invalid_bookmark", "rewind bookmark is malformed");
        }

        var outcome = await continuity.RewindLocalAsync(request.DatabaseId, bookmark, cancellationToken);
        return outcome switch
        {
            CausalBookmark => new RewindDatabaseReply(true),
            ContinuityUnavailable unavailable => new QuicRemoteError(unavailable.Code, unavailable.Message),
        };
    }
}
