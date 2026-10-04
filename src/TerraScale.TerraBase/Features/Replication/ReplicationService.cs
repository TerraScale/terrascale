using Grpc.Core;
using TerraScale.TerraBase.Features.Replication.V1;

namespace TerraScale.TerraBase.Features.Replication;

/// <summary>
/// Member-to-member <c>Replication</c> gRPC service. Bootstrap stage: it enforces that every
/// stream opens with <c>Hello</c> and rejects the handshake until negotiation lands.
/// </summary>
internal sealed class ReplicationService : V1.Replication.ReplicationBase
{
    public override async Task Sync(
        IAsyncStreamReader<SyncRequest> requestStream,
        IServerStreamWriter<SyncResponse> responseStream,
        ServerCallContext context)
    {
        if (!await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false)
            || requestStream.Current.FrameCase != SyncRequest.FrameOneofCase.Hello)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "the first Sync request must be Hello"));
        }

        throw new RpcException(new Status(StatusCode.Unimplemented, "handshake negotiation is not implemented yet"));
    }
}
