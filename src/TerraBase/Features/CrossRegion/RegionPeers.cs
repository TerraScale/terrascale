using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Google.Protobuf;
using Grpc.Core;
using TerraBase.Engine.Cluster;
using TerraBase.Features.CrossRegion.V1;
using TerraBase.Features.Replication;

namespace TerraBase.Features.CrossRegion;

/// <summary>
/// The members of the other regions, asked directly (<c>Wan.Ask</c> on their peer listeners). Requests and replies are at
/// most <see cref="WanProtocol.MaxMessageBytes"/>.
/// </summary>
internal interface IRegionPeers
{
    /// <summary>The other regions with live members.</summary>
    ValueTask<IReadOnlyList<string>> RegionsAsync(CancellationToken cancellationToken);

    /// <summary>Sends <paramref name="request"/> to every member of <paramref name="region"/> and yields their replies as they come.</summary>
    IAsyncEnumerable<ReadOnlyMemory<byte>> RequestManyAsync(string region, ReadOnlyMemory<byte> request, CancellationToken cancellationToken);

    /// <summary>The reply of <paramref name="member"/> of <paramref name="region"/>; throws when it does not answer in time.</summary>
    ValueTask<ReadOnlyMemory<byte>> RequestAsync(string region, string member, ReadOnlyMemory<byte> request, CancellationToken cancellationToken);
}

/// <summary>The other regions' members from the membership, asked over gRPC.</summary>
internal sealed class GrpcRegionPeers(IMembership membership, PeerCredentials? credentials) : IRegionPeers
{
    // gru to sjc is about 170 ms round trip; a member that has not answered the heads within this is skipped this round.
    private static readonly TimeSpan Many = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan One = TimeSpan.FromSeconds(10);

    public async ValueTask<IReadOnlyList<string>> RegionsAsync(CancellationToken cancellationToken) => credentials is null
        ? []
        : [.. Elsewhere(await membership.RefreshAsync(cancellationToken).ConfigureAwait(false)).Select(member => member.Region).Distinct(StringComparer.Ordinal)];

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> RequestManyAsync(string region, ReadOnlyMemory<byte> request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var members = Elsewhere(await membership.RefreshAsync(cancellationToken).ConfigureAwait(false)).Where(member => member.Region == region).ToList();
        var replies = Channel.CreateBounded<ReadOnlyMemory<byte>>(Math.Max(1, members.Count));
        var asking = Task.WhenAll(members.Select(async member =>
        {
            try
            {
                await replies.Writer.WriteAsync(await AskAsync(member, request, Many, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // A member that does not answer in time is skipped this round, as one that left.
            }
        })).ContinueWith(_ => replies.Writer.Complete(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        await foreach (var reply in replies.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return reply;
        }

        await asking.ConfigureAwait(false);
    }

    public async ValueTask<ReadOnlyMemory<byte>> RequestAsync(string region, string member, ReadOnlyMemory<byte> request, CancellationToken cancellationToken) =>
        Elsewhere(await membership.RefreshAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(candidate => candidate.Region == region && candidate.Name == member) is { } found
            ? await AskAsync(found, request, One, cancellationToken).ConfigureAwait(false)
            : throw new IOException("the member is not live");

    private static IEnumerable<ClusterMember> Elsewhere(ClusterView view) =>
        view.Elsewhere.IsDefault ? [] : view.Elsewhere.Where(member => member.PeerAddress.Length > 0);

    private async Task<ReadOnlyMemory<byte>> AskAsync(ClusterMember member, ReadOnlyMemory<byte> request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var lease = credentials!.Capture();
        // ponytail: a channel (TLS handshake) per request; keep one per member if WAN rounds grow.
        using var channel = PeerChannel.Create(new Uri(member.PeerAddress), lease);
        try
        {
            // gRPC measures deadlines on the system clock.
            var reply = await new Wan.WanClient(channel).AskAsync(new WanFrame { Data = UnsafeByteOperations.UnsafeWrap(request) },
                deadline: DateTime.UtcNow.Add(timeout), cancellationToken: cancellationToken).ConfigureAwait(false);
            return reply.Data.Memory;
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.Cancelled && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("the cross-region request was canceled", error, cancellationToken);
        }
    }
}

/// <summary>Answers other regions' <c>Wan.Ask</c> requests on the peer listener.</summary>
internal sealed class WanService(WanResponder responder) : Wan.WanBase
{
    public override async Task<WanFrame> Ask(WanFrame request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        return new WanFrame { Data = UnsafeByteOperations.UnsafeWrap(await responder.AnswerAsync(request.Data.Memory, context.CancellationToken).ConfigureAwait(false)) };
    }
}
