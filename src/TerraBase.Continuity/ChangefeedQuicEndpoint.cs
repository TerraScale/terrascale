using System.Runtime.CompilerServices;
using TerraScale.Contracts.Quic;
using TerraScale.QuicRpc;
using TerraScale.QuicRpc.Messages;

namespace TerraBase.Continuity;

public static class ChangefeedQuicEndpointExtensions
{
    extension(QuicRpcHost host)
    {
        public QuicRpcHost MapChangefeedEndpoints(IChangefeedHub hub)
        {
            ArgumentNullException.ThrowIfNull(hub);
            return host.MapServerStream<ChangefeedSubscribeMessage, ChangefeedEntryMessage>(
                QuicMessageType.ChangefeedSubscribe,
                (_, request, cancellationToken) => request.ToSubscription() switch
                {
                    ChangefeedSubscription subscription => StreamAsync(hub, subscription, cancellationToken),
                    QuicRemoteError rejected => RejectAsync(rejected),
                });
        }
    }

    private static async IAsyncEnumerable<Result<ChangefeedEntryMessage, QuicRemoteError>> StreamAsync(
        IChangefeedHub hub,
        ChangefeedSubscription subscription,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var entry in hub.SubscribeAsync(subscription, cancellationToken).ConfigureAwait(false))
        {
            yield return ChangefeedEntryMessage.From(entry);
        }
    }

    private static async IAsyncEnumerable<Result<ChangefeedEntryMessage, QuicRemoteError>> RejectAsync(QuicRemoteError rejected)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield return rejected;
    }
}

public sealed class ChangefeedQuicEndpoint(ChangefeedHub hub) : IQuicEndpoints
{
    public void Map(QuicRpcHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _ = host.MapChangefeedEndpoints(hub);
    }
}

public sealed class ChangefeedQuicClient(QuicRpcClient client)
{
    public async IAsyncEnumerable<Result<ChangefeedEntry, Unavailable, RequestTimeout, QuicRemoteError>> SubscribeAsync(
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
        await foreach (var item in stream.ConfigureAwait(false))
        {
            yield return item switch
            {
                ChangefeedEntryMessage message => Decode(message),
                Unavailable unavailable => unavailable,
                RequestTimeout timeout => timeout,
                QuicRemoteError error => error,
            };
        }
    }

    private static Result<ChangefeedEntry, Unavailable, RequestTimeout, QuicRemoteError> Decode(ChangefeedEntryMessage message) => message.ToEntry() switch
    {
        ChangefeedEntry entry => entry,
        QuicRemoteError error => error,
    };
}
