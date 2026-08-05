using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using TerraScale.DotNet;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleSyncTransportIntegrationTests
{
    private static readonly TerraScaleSyncStateVersion InitialVersion =
        TerraScaleSyncStateVersion.Zero;

    [Test]
    public async Task PhysicalConnectionCancellationIsSafeAfterDisposal()
    {
        var connection = new TerraScaleSyncSession.PhysicalConnection(
            new ClientWebSocket(),
            sequence: 1,
            CancellationToken.None);

        await connection.DisposeAsync();
        connection.Cancel();
    }

    [Test]
    public async Task OutOfOrderServerMessageReconnectsWithoutApplyingIt()
    {
        await using var endpoint = new LoopbackWebSocketEndpoint();
        using var httpClient = CreateHttpClient();
        await using var session = CreateSession(httpClient, endpoint);

        var start = session.StartAsync();
        var server = await endpoint.Accepted.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var handshake = await ReadHandshakeAsync(server);
        await start;

        await SendAsync(
            server,
            handshake.ConnectionSequence,
            messageSequence: 2,
            TerraScaleSyncMessageType.Ping,
            new TerraScaleSyncPingMessage(new TerraScaleSyncUInt64(1), DateTimeOffset.UtcNow));

        _ = await endpoint.Accepted.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(session.State).IsSameReferenceAs(TerraScaleSyncClientState.Empty);
        await Assert.That(session.Diagnostics.ConnectionSequence.Value).IsGreaterThanOrEqualTo(2UL);
    }

    [Test]
    public async Task ReconnectCarriesAppliedAcknowledgedAndMaximumRevision()
    {
        await using var endpoint = new LoopbackWebSocketEndpoint();
        using var httpClient = CreateHttpClient();
        await using var session = CreateSession(httpClient, endpoint);
        var start = session.StartAsync();
        var firstServer = await endpoint.Accepted.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var firstHandshake = await ReadHandshakeAsync(firstServer);
        await start;

        await using var live = await session.SubscribeAsync(Query());
        var modification = await ReadAsync<TerraScaleSyncModifyQuerySetMessage>(
            firstServer,
            TerraScaleSyncMessageType.ModifyQuerySet);
        var version = Version(1, 1);
        await SendTransitionAsync(
            firstServer,
            firstHandshake.ConnectionSequence,
            1,
            Transition(InitialVersion, version, modification.Payload.AddQuery[0].QueryId, 1));
        _ = await ReadAsync<TerraScaleSyncAcknowledgeTransitionMessage>(
            firstServer,
            TerraScaleSyncMessageType.AcknowledgeTransition);

        firstServer.Abort();
        firstServer.Dispose();
        var secondServer = await endpoint.Accepted.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var secondHandshake = await ReadHandshakeAsync(secondServer);

        await Assert.That(secondHandshake.Payload.LastAppliedVersion).IsEqualTo(version);
        await Assert.That(secondHandshake.Payload.LastAcknowledgedVersion).IsEqualTo(version);
        await Assert.That(secondHandshake.Payload.MaximumObservedRevision)
            .IsEqualTo(version.DatabaseRevision);
        await Assert.That(secondHandshake.Payload.ActiveQueries.Count).IsEqualTo(1);
        await Assert.That(secondHandshake.Payload.ActiveQueries[0].QueryId).IsEqualTo(live.QueryId);
    }

    [Test]
    public async Task SlowListenerKeepsOnlyLatestAtomicSnapshot()
    {
        await using var endpoint = new LoopbackWebSocketEndpoint();
        using var httpClient = CreateHttpClient();
        await using var session = CreateSession(httpClient, endpoint);
        var start = session.StartAsync();
        var server = await endpoint.Accepted.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var handshake = await ReadHandshakeAsync(server);
        await start;
        await using var live = await session.SubscribeAsync(Query());
        var modification = await ReadAsync<TerraScaleSyncModifyQuerySetMessage>(
            server,
            TerraScaleSyncMessageType.ModifyQuerySet);
        var queryId = modification.Payload.AddQuery[0].QueryId;

        var current = InitialVersion;
        for (var count = 1; count <= 3; count++)
        {
            var next = Version(1, (ulong)count);
            await SendTransitionAsync(
                server,
                handshake.ConnectionSequence,
                (ulong)count,
                Transition(current, next, queryId, count));
            _ = await ReadAsync<TerraScaleSyncAcknowledgeTransitionMessage>(
                server,
                TerraScaleSyncMessageType.AcknowledgeTransition);
            current = next;
        }

        await using var values = live.GetAsyncEnumerator();
        await Assert.That(await values.MoveNextAsync()).IsTrue();
        await Assert.That(values.Current.Value.GetProperty("count").GetInt32()).IsEqualTo(3);
    }

    [Test]
    public async Task IdenticalListenersShareOneNetworkRegistrationUntilLastRelease()
    {
        await using var endpoint = new LoopbackWebSocketEndpoint();
        using var httpClient = CreateHttpClient();
        await using var session = CreateSession(httpClient, endpoint);
        var start = session.StartAsync();
        var server = await endpoint.Accepted.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        _ = await ReadHandshakeAsync(server);
        await start;

        var query = Query();
        var first = await session.SubscribeAsync(query);
        var addition = await ReadAsync<TerraScaleSyncModifyQuerySetMessage>(
            server,
            TerraScaleSyncMessageType.ModifyQuerySet);
        var second = await session.SubscribeAsync(query);
        await Assert.That(second.QueryId).IsEqualTo(first.QueryId);
        await Assert.That(addition.Payload.AddQuery.Count).IsEqualTo(1);

        var removalRead = ReadAsync<TerraScaleSyncModifyQuerySetMessage>(
            server,
            TerraScaleSyncMessageType.ModifyQuerySet);
        await first.DisposeAsync();
        await Task.Delay(100);
        await Assert.That(removalRead.IsCompleted).IsFalse();
        await second.DisposeAsync();
        var removal = await removalRead.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(removal.Payload.RemoveQuery.Count).IsEqualTo(1);
        await Assert.That(removal.Payload.RemoveQuery[0].QueryId).IsEqualTo(first.QueryId);
    }

    [Test]
    public async Task DisposalCancelsConnectionAndCompletesListeners()
    {
        await using var endpoint = new LoopbackWebSocketEndpoint();
        using var httpClient = CreateHttpClient();
        var session = CreateSession(httpClient, endpoint);
        var start = session.StartAsync();
        var server = await endpoint.Accepted.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        _ = await ReadHandshakeAsync(server);
        await start;
        await using var live = await session.SubscribeAsync(Query());
        _ = await ReadAsync<TerraScaleSyncModifyQuerySetMessage>(
            server,
            TerraScaleSyncMessageType.ModifyQuerySet);
        await using var values = live.GetAsyncEnumerator();
        var pendingRead = values.MoveNextAsync().AsTask();

        await session.DisposeAsync();

        await Assert.That(await pendingRead.WaitAsync(TimeSpan.FromSeconds(5))).IsFalse();
        await Assert.That(session.Diagnostics.ConnectionState)
            .IsEqualTo(TerraScaleSyncConnectionState.Disposed);
    }

    [Test]
    public async Task MutationResultRetainsOptimismUntilAuthoritativeRevisionCatchesUp()
    {
        await using var endpoint = new LoopbackWebSocketEndpoint();
        using var httpClient = CreateHttpClient();
        await using var session = CreateSession(httpClient, endpoint);
        var start = session.StartAsync();
        var server = await endpoint.Accepted.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var handshake = await ReadHandshakeAsync(server);
        await start;
        await using var live = await session.SubscribeAsync(Query());
        var modification = await ReadAsync<TerraScaleSyncModifyQuerySetMessage>(
            server,
            TerraScaleSyncMessageType.ModifyQuerySet);
        var queryId = modification.Payload.AddQuery[0].QueryId;
        var firstVersion = Version(1, 1);
        await SendTransitionAsync(
            server,
            handshake.ConnectionSequence,
            1,
            Transition(InitialVersion, firstVersion, queryId, 1));
        _ = await ReadAsync<TerraScaleSyncAcknowledgeTransitionMessage>(
            server,
            TerraScaleSyncMessageType.AcknowledgeTransition);

        await using var values = live.GetAsyncEnumerator();
        await Assert.That(await values.MoveNextAsync()).IsTrue();
        await Assert.That(values.Current.Value.GetProperty("count").GetInt32()).IsEqualTo(1);

        var mutationTask = session.ExecuteMutationAsync(
            new TerraScaleSyncMutation(
                "mutation-1",
                "update Counter",
                Json("{}"),
                OptimisticPatches:
                [
                    new(
                        queryId,
                        current => JsonSerializer.SerializeToElement(
                            new
                            {
                                count = current!.Value.GetProperty("count").GetInt32() + 1
                            }))
                ]));
        var mutation = await ReadAsync<TerraScaleSyncExecuteMutationMessage>(
            server,
            TerraScaleSyncMessageType.ExecuteMutation);
        await Assert.That(await values.MoveNextAsync()).IsTrue();
        await Assert.That(values.Current.Optimistic).IsTrue();
        await Assert.That(values.Current.Value.GetProperty("count").GetInt32()).IsEqualTo(2);

        var committedRevision = Revision(2);
        await SendAsync(
            server,
            handshake.ConnectionSequence,
            2,
            TerraScaleSyncMessageType.MutationResult,
            new TerraScaleSyncMutationResultMessage(
                mutation.Payload.ClientRequestId,
                Succeeded: true,
                Result: Json("""{"accepted":true}"""),
                Error: null,
                committedRevision));
        var result = await mutationTask.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(result.CommittedRevision).IsEqualTo(committedRevision);
        await Assert.That(session.State.Queries[queryId].Optimistic).IsTrue();

        var caughtUp = Version(1, 2);
        await SendTransitionAsync(
            server,
            handshake.ConnectionSequence,
            3,
            Transition(firstVersion, caughtUp, queryId, 2));
        _ = await ReadAsync<TerraScaleSyncAcknowledgeTransitionMessage>(
            server,
            TerraScaleSyncMessageType.AcknowledgeTransition);
        await Assert.That(await values.MoveNextAsync()).IsTrue();
        await Assert.That(values.Current.Optimistic).IsFalse();
        await Assert.That(values.Current.Value.GetProperty("count").GetInt32()).IsEqualTo(2);
    }

    [Test]
    public async Task ConcurrentWritersEmitStrictlyOrderedConnectionMessages()
    {
        await using var endpoint = new LoopbackWebSocketEndpoint();
        using var httpClient = CreateHttpClient();
        await using var session = CreateSession(httpClient, endpoint);
        var start = session.StartAsync();
        var server = await endpoint.Accepted.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        _ = await ReadHandshakeAsync(server);
        await start;

        var acquisitions = Enumerable.Range(0, 64)
            .Select(index => Task.Run(
                () => session.SubscribeAsync(
                    new TerraScaleSyncQuery(
                        $"from Counters where Number = {index}",
                        Json("{}"),
                        TerraScaleSyncUInt64.Zero))))
            .ToArray();
        for (ulong index = 0; index < (ulong)acquisitions.Length; index++)
        {
            var message = await ReadAsync<TerraScaleSyncModifyQuerySetMessage>(
                server,
                TerraScaleSyncMessageType.ModifyQuerySet);
            await Assert.That(message.MessageSequence.Value).IsEqualTo(index + 3);
            await Assert.That(message.Payload.BaseQuerySetVersion.Value).IsEqualTo(index);
            await Assert.That(message.Payload.NewQuerySetVersion.Value).IsEqualTo(index + 1);
        }

        _ = await Task.WhenAll(acquisitions);
    }

    [Test]
    public async Task CompleteTransitionMayExceedClientMessageLimit()
    {
        await using var endpoint = new LoopbackWebSocketEndpoint();
        using var httpClient = CreateHttpClient();
        await using var session = CreateSession(httpClient, endpoint);
        var start = session.StartAsync();
        var server = await endpoint.Accepted.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var handshake = await ReadHandshakeAsync(server);
        await start;
        await using var live = await session.SubscribeAsync(Query());
        var modification = await ReadAsync<TerraScaleSyncModifyQuerySetMessage>(
            server,
            TerraScaleSyncMessageType.ModifyQuerySet);
        var version = Version(1, 1);
        var largeValue = new string('x', TerraScaleSyncSession.MaxClientMessageBytes + 64 * 1024);
        var transition = new TerraScaleSyncTransition(
            TerraScaleSyncIdGenerator.Create(),
            InitialVersion,
            version,
            [
                new(
                    modification.Payload.AddQuery[0].QueryId,
                    new string('a', 64),
                    JsonSerializer.SerializeToElement(new { data = largeValue }),
                    new string('b', 64),
                    version.DatabaseRevision,
                    version.DatabaseRevision,
                    Journal: null)
            ],
            [],
            [],
            ServerTiming: null,
            "gru",
            "sfo",
            "sfo",
            new TerraScaleSyncUInt64(1));

        await SendTransitionAsync(
            server,
            handshake.ConnectionSequence,
            1,
            transition);
        _ = await ReadAsync<TerraScaleSyncAcknowledgeTransitionMessage>(
            server,
            TerraScaleSyncMessageType.AcknowledgeTransition);
        await using var values = live.GetAsyncEnumerator();
        await Assert.That(await values.MoveNextAsync()).IsTrue();
        await Assert.That(values.Current.Value.GetProperty("data").GetString()!.Length)
            .IsEqualTo(largeValue.Length);
    }

    private static HttpClient CreateHttpClient() =>
        new()
        {
            BaseAddress = new Uri("http://localhost:5294")
        };

    private static TerraScaleSyncSession CreateSession(
        HttpClient httpClient,
        LoopbackWebSocketEndpoint endpoint)
    {
        var client = new TerraScaleClient(
            httpClient,
            new TerraScaleClientOptions
            {
                BaseAddress = httpClient.BaseAddress!,
                BearerToken = "test-token"
            });
        return new TerraScaleSyncSession(
            client,
            new TerraScaleSyncSessionOptions(
                "01ARZ3NDEKTSV4RRFFQ69G5FAV",
                "01BX5ZZKBKACTAV9WEVGEMMVRZ")
            {
                SessionId = "01HZY3NDEKTSV4RRFFQ69G5FAV",
                ReconnectDelay = TimeSpan.FromMilliseconds(1),
                MaximumReconnectDelay = TimeSpan.FromMilliseconds(10),
                HeartbeatInterval = TimeSpan.FromSeconds(30),
                HeartbeatTimeout = TimeSpan.FromMinutes(1)
            },
            endpoint.ConnectAsync);
    }

    private static TerraScaleSyncQuery Query() =>
        new("from Counters", Json("{}"), TerraScaleSyncUInt64.Zero);

    private static TerraScaleSyncTransition Transition(
        TerraScaleSyncStateVersion start,
        TerraScaleSyncStateVersion end,
        TerraScaleSyncId queryId,
        int count) =>
        new(
            TerraScaleSyncIdGenerator.Create(),
            start,
            end,
            [
                new(
                    queryId,
                    new string('a', 64),
                    Json($$"""{"count":{{count}}}"""),
                    new string((char)('a' + count), 64),
                    end.DatabaseRevision,
                    end.DatabaseRevision,
                    Journal: null)
            ],
            [],
            [],
            ServerTiming: null,
            "gru",
            "sfo",
            "sfo",
            new TerraScaleSyncUInt64(1));

    private static TerraScaleSyncStateVersion Version(ulong querySetVersion, ulong revision) =>
        new(
            new TerraScaleSyncUInt64(querySetVersion),
            Revision(revision),
            TerraScaleSyncUInt64.Zero);

    private static TerraScaleSyncDatabaseRevision Revision(ulong sequence) =>
        new(new TerraScaleSyncUInt64(1), new TerraScaleSyncUInt64(sequence));

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static async Task<TerraScaleSyncEnvelope<TerraScaleSyncConnectMessage>>
        ReadHandshakeAsync(WebSocket socket)
    {
        _ = await ReadAsync<TerraScaleSyncAuthenticateMessage>(
            socket,
            TerraScaleSyncMessageType.Authenticate);
        return await ReadAsync<TerraScaleSyncConnectMessage>(
            socket,
            TerraScaleSyncMessageType.Connect);
    }

    private static Task SendTransitionAsync(
        WebSocket socket,
        TerraScaleSyncUInt64 connectionSequence,
        ulong messageSequence,
        TerraScaleSyncTransition transition) =>
        SendAsync(
            socket,
            connectionSequence,
            messageSequence,
            TerraScaleSyncMessageType.Transition,
            (TerraScaleSyncTransitionFrame)transition);

    private static async Task SendAsync<TPayload>(
        WebSocket socket,
        TerraScaleSyncUInt64 connectionSequence,
        ulong messageSequence,
        TerraScaleSyncMessageType messageType,
        TPayload payload)
    {
        var envelope = new TerraScaleSyncEnvelope<TPayload>(
            TerraScaleSyncEnvelope<TPayload>.ProtocolVersion,
            messageType,
            connectionSequence,
            new TerraScaleSyncUInt64(messageSequence),
            payload);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            envelope,
            typeof(TerraScaleSyncEnvelope<TPayload>),
            TerraScaleSyncJsonContext.Default);
        await socket.SendAsync(
            bytes,
            WebSocketMessageType.Text,
            endOfMessage: true,
            CancellationToken.None);
    }

    private static async Task<TerraScaleSyncEnvelope<TPayload>> ReadAsync<TPayload>(
        WebSocket socket,
        TerraScaleSyncMessageType expected)
    {
        var buffer = new byte[TerraScaleSyncSession.MaxClientMessageBytes];
        var length = 0;
        while (true)
        {
            var result = await socket.ReceiveAsync(
                buffer.AsMemory(length),
                CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            length += result.Count;
            if (result.EndOfMessage)
            {
                break;
            }
        }

        var envelope = (TerraScaleSyncEnvelope<TPayload>?)JsonSerializer.Deserialize(
            buffer.AsSpan(0, length),
            typeof(TerraScaleSyncEnvelope<TPayload>),
            TerraScaleSyncJsonContext.Default);
        await Assert.That(envelope).IsNotNull();
        await Assert.That(envelope!.MessageType).IsEqualTo(expected);
        return envelope;
    }

    private sealed class LoopbackWebSocketEndpoint : IAsyncDisposable
    {
        private readonly List<WebSocket> serverSockets = [];

        public Channel<WebSocket> Accepted { get; } = Channel.CreateUnbounded<WebSocket>();

        public async ValueTask<TerraScaleSyncSession.ConnectedSyncSocket> ConnectAsync(
            ulong _,
            CancellationToken cancellationToken)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var accept = listener.AcceptTcpClientAsync(cancellationToken);
                var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
                var server = await accept;
                var clientSocket = WebSocket.CreateFromStream(
                    client.GetStream(),
                    isServer: false,
                    "terrascale-sync-v1",
                    Timeout.InfiniteTimeSpan);
                var serverSocket = WebSocket.CreateFromStream(
                    server.GetStream(),
                    isServer: true,
                    "terrascale-sync-v1",
                    Timeout.InfiniteTimeSpan);
                lock (serverSockets)
                {
                    serverSockets.Add(serverSocket);
                }

                await Accepted.Writer.WriteAsync(serverSocket, cancellationToken);
                return new(clientSocket, "test-token");
            }
            finally
            {
                listener.Stop();
            }
        }

        public ValueTask DisposeAsync()
        {
            Accepted.Writer.TryComplete();
            lock (serverSockets)
            {
                foreach (var socket in serverSockets)
                {
                    socket.Abort();
                    socket.Dispose();
                }

                serverSockets.Clear();
            }

            return ValueTask.CompletedTask;
        }
    }
}
