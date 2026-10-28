using Grpc.Core;
using TerraBase.Configuration;
using TerraBase.Engine.Cluster;
using TerraBase.Features.Management;
using TerraBase.Features.Replication.V1;

namespace TerraBase.Features.Replication;

/// <summary>Asks other regions for units of an organization's database allowance (decision 0016).</summary>
internal sealed class EscrowPeers(IMembership membership, PeerCredential credential, TimeProvider time) : IEscrowPeers
{
    // A region that has not answered by then counts as unreachable; the create is refused rather than held. A grant is a
    // durable SYSTEM write at the granting region, which queues behind that region's writer: 2 s refused fresh-project
    // creates whenever the genesis region's writer was briefly busy (gru, 2026-10-28). Two peers asked in turn stay inside
    // the edge's 30 s request deadline.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    public async ValueTask<IReadOnlyList<string>> RegionsAsync(CancellationToken cancellationToken)
    {
        var view = await membership.RefreshAsync(cancellationToken).ConfigureAwait(false);
        return [.. (view.Elsewhere.IsDefault ? [] : view.Elsewhere).Select(member => member.Region).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    public async ValueTask<long?> TransferAsync(string region, string organizationId, long received, int want, CancellationToken cancellationToken)
    {
        var view = await membership.RefreshAsync(cancellationToken).ConfigureAwait(false);
        foreach (var peer in (view.Elsewhere.IsDefault ? [] : view.Elsewhere).Where(peer => peer.Region == region && peer.PeerAddress.Length > 0))
        {
            try
            {
                using var channel = PeerChannel.Create(new Uri(peer.PeerAddress), credential);
                var reply = await new DatabaseEscrow.DatabaseEscrowClient(channel).TransferAsync(
                    new EscrowTransferRequest { Organization = organizationId, Region = membership.Self.Region, Received = received, Want = want },
                    deadline: time.GetUtcNow().UtcDateTime + Timeout, cancellationToken: cancellationToken).ConfigureAwait(false);
                return reply.Granted;
            }
            catch (RpcException) when (!cancellationToken.IsCancellationRequested)
            {
                // Unreachable, timed out, or a node without escrow (UNIMPLEMENTED): try the region's next member.
            }
        }

        return null;
    }
}

/// <summary>The peer side of <see cref="EscrowPeers"/>: grants units this region holds.</summary>
internal sealed class EscrowReceiver(IDatabaseEscrow escrow) : DatabaseEscrow.DatabaseEscrowBase
{
    public override async Task<EscrowTransferReply> Transfer(EscrowTransferRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (!Primitives.EntityIds.IsValid(request.Organization) || !TerraBaseOptions.IsRegion(request.Region) || request.Received < 0 || request.Want is < 1 or > 1000)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "organization, region, received and want must be valid"));
        }

        return await escrow.GrantAsync(request.Organization, request.Region, request.Received, request.Want, context.CancellationToken).ConfigureAwait(false) is { } granted
            ? new EscrowTransferReply { Granted = granted }
            : throw new RpcException(new Status(StatusCode.Unavailable, "the catalog is not available"));
    }
}
