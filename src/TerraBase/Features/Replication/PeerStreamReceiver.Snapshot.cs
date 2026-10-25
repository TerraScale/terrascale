using Grpc.Core;
using TerraBase.Features.Memory;
using TerraBase.Features.Replication.V1;

namespace TerraBase.Features.Replication;

internal sealed partial class PeerStreamReceiver
{
    public override async Task ReadSnapshot(DigestRequest request, IServerStreamWriter<SnapshotPage> responseStream, ServerCallContext context)
    {
        var database = Tenant(request.Database);
        if (!_catalogGate.Value.Trusted)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "the source catalog is unverified"));
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        var cancellationToken = deadline.Token;
        var incarnation = Incarnation(request.Database);
        using var reservation = _memory.TryReserve(MemoryLane.Replication, BucketReadBytes, tenant: null)
            ?? throw new RpcException(new Status(StatusCode.ResourceExhausted, "no memory capacity for a snapshot"));
        var source = context.GetHttpContext().RequestServices.GetRequiredService<IReplicaSnapshotSource>();
        var complete = false;
        await foreach (var page in source.SnapshotAsync(database, incarnation, cancellationToken).ConfigureAwait(false))
        {
            var reply = new SnapshotPage { Page = page.Applied is null ? AntiEntropy.Encode(page.States, incarnation) : new StatePage { Incarnation = request.Database.Incarnation }, Complete = page.Applied is not null, CollectedBefore = page.CollectedBefore.Value };
            if (page.Applied is { } applied)
            {
                reply.Applied.AddRange(applied.Select(cut => new SnapshotCut { Replica = cut.Key.Value, Stamp = cut.Value.Value }));
                complete = true;
            }

            await responseStream.WriteAsync(reply, cancellationToken).ConfigureAwait(false);
        }

        if (!complete)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "the source snapshot could not be certified"));
        }
    }
}
