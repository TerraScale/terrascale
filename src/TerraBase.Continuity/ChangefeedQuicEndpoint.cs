using System.Runtime.CompilerServices;
using TerraScale.Contracts.Quic;
using TerraScale.QuicRpc;
using TerraScale.QuicRpc.Messages;

namespace TerraBase.Continuity;

public sealed record ChangefeedPeerUnavailable(string Message, string Code);

public sealed record ChangefeedPeerTimeout(string Message, string Code);

public readonly union ChangefeedStreamItem(ChangefeedEntry, ChangefeedPeerUnavailable, ChangefeedPeerTimeout, QuicRemoteError);

public delegate bool ChangefeedPeerAuthorization(QuicCallContext context);

public sealed class ChangefeedQuicEndpoint(ChangefeedHub hub, ChangefeedPeerAuthorization authorize) : IQuicEndpoints
{
    public void Map(QuicRpcHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _ = host.MapServerStream<ChangefeedSubscribeMessage, ChangefeedEntryMessage>(QuicMessageType.ChangefeedSubscribe, HandleAsync);
    }

    private IAsyncEnumerable<QuicHandlerOutcome<ChangefeedEntryMessage>> HandleAsync(
        QuicCallContext context, ChangefeedSubscribeMessage request, CancellationToken cancellationToken)
    {
        if (!authorize(context))
        {
            return RejectAsync(new QuicRemoteError("forbidden", "changefeed requires an authenticated platform service or cluster peer"));
        }

        return request.ToSubscription() switch
        {
            ChangefeedSubscription subscription => StreamAsync(subscription, cancellationToken),
            QuicRemoteError rejected => RejectAsync(rejected),
        };
    }

    private async IAsyncEnumerable<QuicHandlerOutcome<ChangefeedEntryMessage>> StreamAsync(
        ChangefeedSubscription subscription,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var entry in hub.SubscribeAsync(subscription, cancellationToken))
        {
            yield return ChangefeedEntryMessage.From(entry);
        }
    }

    private static async IAsyncEnumerable<QuicHandlerOutcome<ChangefeedEntryMessage>> RejectAsync(QuicRemoteError rejected)
    {
        await Task.CompletedTask;
        yield return rejected;
    }
}

public sealed class ChangefeedQuicClient(QuicRpcClient client)
{
    public async IAsyncEnumerable<ChangefeedStreamItem> SubscribeAsync(
        string peerId,
        ChangefeedSubscription subscription,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(peerId);
        ArgumentNullException.ThrowIfNull(subscription);
        var stream = client.ServerStreamAsync<ChangefeedSubscribeMessage, ChangefeedEntryMessage>(
            peerId,
            QuicMessageType.ChangefeedSubscribe,
            ChangefeedSubscribeMessage.From(subscription),
            cancellationToken);
        await foreach (var item in stream)
        {
            var decoded = item switch
            {
                ChangefeedEntryMessage message => Decode(message),
                QuicUnavailable unavailable => new ChangefeedPeerUnavailable(unavailable.Message, unavailable.Code),
                QuicTimeout timeout => new ChangefeedPeerTimeout(timeout.Message, timeout.Code),
                QuicRemoteError error => error,
            };
            yield return decoded;
        }
    }

    private static ChangefeedStreamItem Decode(ChangefeedEntryMessage message)
    {
        var entry = message.ToEntry();
        return entry switch
        {
            ChangefeedEntry decoded => decoded,
            QuicRemoteError error => error,
        };
    }
}
