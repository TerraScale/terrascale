using System.Net;
using System.Runtime.CompilerServices;
using TerraScale.Contracts;
using TerraScale.Contracts.Continuity;
using TerraScale.Contracts.Quic;
using TerraScale.QuicRpc;

namespace TerraScale.Platform.TerraBase;

public sealed class TerraBaseQuic : IAsyncDisposable
{
    private readonly QuicLink? _link;

    internal TerraBaseQuic()
    {
    }

    internal TerraBaseQuic(
        QuicEndpointOptions endpoint,
        QuicMeshCertificates certificates,
        PlatformOptions options,
        QuicMeshMetrics metrics,
        TimeProvider timeProvider,
        ILogger<QuicRpcClient> logger)
    {
        var client = new QuicRpcClient(
            new QuicMeshOptions
            {
                NodeId = options.Node,
                Region = options.Region,
                ListenEndPoint = new IPEndPoint(IPAddress.IPv6Any, 0),
                Certificate = certificates.Certificate,
                TrustedAuthorities = certificates.TrustedAuthorities,
                IntermediateCertificates = certificates.Intermediates,
            },
            metrics,
            timeProvider,
            logger);
        client.UpsertPeer(new QuicPeer(endpoint.NodeId, endpoint.Region, endpoint.EndPoint));
        _link = new QuicLink(client, endpoint.NodeId);
    }

    public bool IsEnabled => _link is not null;

    public async ValueTask<InternalSqlOutcome> ExecuteAsync(
        Ulid database,
        SqlExecuteRequest request,
        SqlContext context,
        CancellationToken cancellationToken)
    {
        if (_link is not { Client: var client, Peer: var peer })
        {
            return new PlatformUnavailable("the QUIC TerraBase transport is not configured", "quic_disabled");
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(request, TerraScaleContractsJsonContext.Default.SqlExecuteRequest);
        var message = new InternalSqlRequest(database, context.Bookmark, context.Hint, payload);
        var reply = await client.CallAsync<InternalSqlRequest, InternalSqlReply>(peer, QuicMessageType.InternalSqlExecute, message, cancellationToken);
        return reply switch
        {
            InternalSqlReply answer => TerraBaseAnswers.Sql(answer.Status, answer.Body.Span),
            QuicUnavailable unavailable => new PlatformUnavailable(unavailable.Message, unavailable.Code),
            QuicTimeout timeout => new PlatformUnavailable(timeout.Message, "terrabase_timeout"),
            QuicRemoteError remote => new PlatformUnavailable(remote.Message, remote.Code),
        };
    }

    public async IAsyncEnumerable<ChangefeedReadOutcome> SubscribeAsync(
        Ulid database,
        string after,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_link is not { Client: var client, Peer: var peer })
        {
            yield return new PlatformUnavailable("the QUIC TerraBase transport is not configured", "quic_disabled");
            yield break;
        }

        if (!CausalBookmark.TryDecode(after, out var position))
        {
            yield return new PlatformUnavailable("the changefeed position is not a TerraScale bookmark", "invalid_bookmark");
            yield break;
        }

        var stream = client.ServerStreamAsync<ChangefeedSubscribeMessage, ChangefeedEntryMessage>(
            peer,
            QuicMessageType.ChangefeedSubscribe,
            ChangefeedSubscribeMessage.From(new ChangefeedSubscription(database, position)),
            cancellationToken);
        await foreach (var item in stream.WithCancellation(cancellationToken))
        {
            yield return item switch
            {
                ChangefeedEntryMessage message => message.ToEntry() switch
                {
                    ChangefeedEntry entry => Advance(ref position, entry),
                    QuicRemoteError invalid => new PlatformUnavailable(invalid.Message, invalid.Code),
                },
                QuicUnavailable unavailable => new PlatformUnavailable(unavailable.Message, unavailable.Code),
                QuicTimeout timeout => new PlatformUnavailable(timeout.Message, "terrabase_timeout"),
                QuicRemoteError remote => new PlatformUnavailable(remote.Message, remote.Code),
            };
        }
    }

    public ValueTask DisposeAsync() => _link is { } link ? link.Client.DisposeAsync() : ValueTask.CompletedTask;

    private static ChangefeedHttpItem Advance(ref CausalBookmark position, ChangefeedEntry entry)
    {
        position = position.Merge(CausalBookmark.ForRegion(entry.OriginRegion, entry.Sequence));
        return new ChangefeedHttpItem(entry.DatabaseId, entry.TxId, entry.OriginRegion, entry.Sequence, entry.SchemaEpoch, entry.Mode, entry.CommittedAtUtc, position.Encode());
    }

    private sealed record QuicLink(QuicRpcClient Client, string Peer);
}
