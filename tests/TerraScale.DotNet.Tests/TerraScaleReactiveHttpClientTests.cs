using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net.WebSockets;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleReactiveHttpClientTests
{
    [Test]
    public async Task WebSocketOffersCapabilityAndSyncProtocolsAndReceivesBinaryFrame()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var serverDone = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var capability = "capability-token";
        var server = ServeWebSocketAsync(listener, capability, "terrascale.sync.v1", serverDone.Token, sendFrame: true);
        using var httpClient = new HttpClient();
        var client = new TerraScaleReactiveHttpClient(httpClient, new TerraScaleReactiveHttpClientOptions
        {
            BaseAddress = new Uri("http://127.0.0.1"),
        });

        using var socket = await client.ConnectWebSocketAsync(new Uri($"ws://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/socket"), capability, serverDone.Token);
        var buffer = new byte[1];
        var received = await socket.ReceiveAsync(buffer, serverDone.Token);
        await server;

        await Assert.That(socket.SubProtocol).IsEqualTo("terrascale.sync.v1");
        await Assert.That(received.MessageType).IsEqualTo(WebSocketMessageType.Binary);
        await Assert.That(buffer[0]).IsEqualTo((byte)0x90);
    }

    [Test]
    [Arguments(null)]
    [Arguments("terrascale.sync.v2")]
    public async Task WebSocketRejectsMissingOrWrongNegotiatedProtocol(string? selectedProtocol)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var serverDone = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var server = ServeWebSocketAsync(listener, "capability-token", selectedProtocol, serverDone.Token, sendFrame: false);
        using var httpClient = new HttpClient();
        var client = new TerraScaleReactiveHttpClient(httpClient);

        await Assert.ThrowsAsync<TerraScaleReactiveProtocolException>(() => client.ConnectWebSocketAsync(new Uri($"ws://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/socket"), "capability-token", serverDone.Token));
        await server;
    }

    [Test]
    public async Task MalformedWebSocketCapabilityDoesNotLeakItsValue()
    {
        using var httpClient = new HttpClient();
        var client = new TerraScaleReactiveHttpClient(httpClient);
        var secret = "bad\r\nX-Leak: should-not-appear";

        var exception = await Assert.ThrowsAsync<TerraScaleReactiveProtocolException>(() => client.ConnectWebSocketAsync(new Uri("ws://127.0.0.1:1/socket"), secret, CancellationToken.None));

        await Assert.That(exception!.Message.Contains(secret, StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task AttachRejectsRetiredViewCapabilityHeader()
    {
        using var httpClient = new HttpClient(new ReactiveHandler(capabilityHeader: "X-TerraScale-View-Capability"));
        var client = new TerraScaleReactiveHttpClient(httpClient, new TerraScaleReactiveHttpClientOptions { MaxRetryAttempts = 0 });

        await Assert.ThrowsAsync<TerraScaleReactiveProtocolException>(() =>
            client.SubscribeAsync("db", "sub", "SELECT * FROM docs"));
    }

    [Test]
    public async Task CanonicalAttachUsesDatabaseStatementParametersAndSafeUrls()
    {
        using var handler = new ReactiveHandler(subscription: "orders", view: "view-1");
        using var httpClient = new HttpClient(handler);
        var client = new TerraScaleReactiveHttpClient(httpClient, new TerraScaleReactiveHttpClientOptions
        {
            BaseAddress = new Uri("https://sync.api.terrascale.tech"),
            ApiBaseAddress = new Uri("https://member.terrascale.tech"),
            BearerToken = "token",
            RegionHint = "gru",
            MaxRetryAttempts = 0,
        });

        await using var subscription = await client.SubscribeAsync(
            "db-1", "orders", "SELECT * FROM orders WHERE id = ?", new byte[] { 0, 255, 1 });
        var result = await subscription.ReadNextAsync();
        var attach = JsonSerializer.Deserialize<JsonElement>(handler.AttachBody!);

        await Assert.That(attach.GetProperty("databaseId").GetString()).IsEqualTo("db-1");
        await Assert.That(attach.GetProperty("subscription").GetString()).IsEqualTo("orders");
        await Assert.That(attach.GetProperty("statement").GetString()).IsEqualTo("SELECT * FROM orders WHERE id = ?");
        await Assert.That(attach.GetProperty("parameters").GetString()).IsEqualTo("AP8B");
        await Assert.That(attach.GetProperty("checkpoint").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(attach.TryGetProperty("transport", out _)).IsFalse();
        await Assert.That(handler.Requests[0].Headers["Authorization"]).IsEqualTo("Bearer token");
        await Assert.That(handler.Requests[0].Headers["X-TerraScale-Region-Hint"]).IsEqualTo("gru");
        await Assert.That(handler.Requests[1].Uri.AbsoluteUri).IsEqualTo("https://sync.api.terrascale.tech/api/v1/reactive/views/view-1/3/snapshot/0?cut=0");
        await Assert.That(handler.Requests[1].Headers["X-TerraScale-Capability"]).IsEqualTo("capability");
        await Assert.That(handler.Requests[2].Uri.Query).IsEqualTo("?offset=0");
        await Assert.That(handler.Requests[2].Headers["X-TerraScale-Capability"]).IsEqualTo("capability");
        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Frame).IsTypeOf<TerraScaleReactiveDelta>();
        await Assert.That(subscription.Offset).IsEqualTo(1ul);
        await Assert.That(result.ETag).IsEqualTo("\"revision-1\"");
    }

    [Test]
    public async Task WebSocketTransportUsesCanonicalRouteAndSendsSubscribeBeforeEvent()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var serverDone = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var eventFrame = Envelope(4, body: Delta("view-1", 3, 0, 1, "Y3AtMQ=="));
        var server = ServeWebSocketAsync(listener, "capability", "terrascale.sync.v1", serverDone.Token, sendFrame: true, responseFrame: eventFrame, expectedPath: "/api/v1/reactive/views/view-1/3/websocket", expectSubscribe: true, expectedWireView: "view-1");
        using var handler = new ReactiveHandler(subscription: "orders", view: "view-1", websocketLog: $"http://127.0.0.1:{port}/wrong-log?offset=0");
        using var httpClient = new HttpClient(handler);
        var client = new TerraScaleReactiveHttpClient(httpClient, new TerraScaleReactiveHttpClientOptions { BaseAddress = new Uri($"http://127.0.0.1:{port}") });

        await using var subscription = await client.SubscribeAsync("db", "orders", "SELECT * FROM orders", new byte[] { 0, 255, 1 }, transport: TerraScaleReactiveTransport.WebSocket, cancellationToken: serverDone.Token);
        TerraScaleReactivePollResult? result;
        try
        {
            result = await subscription.ReadNextAsync(serverDone.Token);
        }
        catch
        {
            // Surface a server assertion instead of masking it with the resulting client EOF.
            await server;
            throw;
        }
        await server;

        await Assert.That(result!.Frame).IsTypeOf<TerraScaleReactiveDelta>();
        await Assert.That(subscription.Offset).IsEqualTo(1ul);
        await subscription.DisposeAsync();
        await Assert.That(subscription.SessionState).IsEqualTo(TerraScaleViewSessionState.Closed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => subscription.ReadNextAsync(serverDone.Token));
    }

    [Test]
    public async Task ReattachKeepsClientSubscriptionForAttachAndUsesNewCanonicalView()
    {
        using var handler = new ReactiveHandler(subscription: "orders", view: "view-1", secondView: "view-2", pollStatus: HttpStatusCode.Conflict);
        using var httpClient = new HttpClient(handler);
        var client = new TerraScaleReactiveHttpClient(httpClient, new TerraScaleReactiveHttpClientOptions { MaxRetryAttempts = 0 });

        await using var subscription = await client.SubscribeAsync("db", "orders", "SELECT * FROM orders");
        var result = await subscription.ReadNextAsync();

        await Assert.That(result!.Frame).IsTypeOf<TerraScaleReactiveReset>();
        await Assert.That(subscription.Attachment.Subscription).IsEqualTo("orders");
        await Assert.That(subscription.Attachment.View).IsEqualTo("view-2");
        await Assert.That(handler.AttachBodies).Count().IsEqualTo(2);
        var reattach = JsonSerializer.Deserialize<JsonElement>(handler.AttachBodies[1]);
        await Assert.That(reattach.GetProperty("subscription").GetString()).IsEqualTo("orders");
    }

    [Test]
    public async Task EmptyNoStorePollReturnsNullWithoutAdvancingOffset()
    {
        using var handler = new ReactiveHandler(timeout: true);
        using var httpClient = new HttpClient(handler);
        var client = new TerraScaleReactiveHttpClient(httpClient, new TerraScaleReactiveHttpClientOptions { MaxRetryAttempts = 0 });
        await using var subscription = await client.SubscribeAsync("db", "sub", "SELECT * FROM docs");
        var result = await subscription.ReadNextAsync();
        await Assert.That(result).IsNull();
        await Assert.That(subscription.Offset).IsEqualTo(0ul);
    }

    [Test]
    public async Task ReactiveFramesUseAttachedViewIdentityWhenItDiffersFromSubscription()
    {
        using var handler = new ReactiveHandler(subscription: "sub", view: "view-1", wireSubscription: "view-1");
        using var httpClient = new HttpClient(handler);
        var client = new TerraScaleReactiveHttpClient(httpClient, new TerraScaleReactiveHttpClientOptions { MaxRetryAttempts = 0 });

        await using var subscription = await client.SubscribeAsync("db", "sub", "SELECT * FROM docs");
        var result = await subscription.ReadNextAsync();

        await Assert.That(result!.Frame).IsTypeOf<TerraScaleReactiveDelta>();
        await Assert.That(subscription.Attachment.Subscription).IsEqualTo("sub");
        await Assert.That(subscription.Attachment.View).IsEqualTo("view-1");
        await Assert.That(subscription.Offset).IsEqualTo(1ul);
    }

    [Test]
    public async Task NoStoreUnauthorizedPollSurfacesTypedFailure()
    {
        using var handler = new ReactiveHandler(pollStatus: HttpStatusCode.Unauthorized, noStore: true);
        using var httpClient = new HttpClient(handler);
        var client = new TerraScaleReactiveHttpClient(httpClient, new TerraScaleReactiveHttpClientOptions { MaxRetryAttempts = 0 });
        await using var subscription = await client.SubscribeAsync("db", "sub", "SELECT * FROM docs");
        var exception = await Assert.ThrowsAsync<TerraScaleReactiveHttpException>(() => subscription.ReadNextAsync());
        await Assert.That(exception!.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task WeakDeltaEtagIsRejected()
    {
        using var handler = new ReactiveHandler(weakPollEtag: true);
        using var httpClient = new HttpClient(handler);
        var client = new TerraScaleReactiveHttpClient(httpClient, new TerraScaleReactiveHttpClientOptions { MaxRetryAttempts = 0 });
        await using var subscription = await client.SubscribeAsync("db", "sub", "SELECT * FROM docs");
        await Assert.ThrowsAsync<TerraScaleReactiveProtocolException>(() => subscription.ReadNextAsync());
        await Assert.That(subscription.Offset).IsEqualTo(0ul);
    }

    [Test]
    public async Task WeakSnapshotEtagIsRejected()
    {
        using var handler = new ReactiveHandler(weakSnapshotEtag: true);
        using var httpClient = new HttpClient(handler);
        var client = new TerraScaleReactiveHttpClient(httpClient, new TerraScaleReactiveHttpClientOptions { MaxRetryAttempts = 0 });
        await Assert.ThrowsAsync<TerraScaleReactiveProtocolException>(() => client.SubscribeAsync("db", "sub", "SELECT * FROM docs"));
    }

    [Test]
    public async Task WildcardSnapshotEtagIsRejected()
    {
        using var handler = new ReactiveHandler(wildcardSnapshotEtag: true);
        using var httpClient = new HttpClient(handler);
        var client = new TerraScaleReactiveHttpClient(httpClient, new TerraScaleReactiveHttpClientOptions { MaxRetryAttempts = 0 });
        await Assert.ThrowsAsync<TerraScaleReactiveProtocolException>(() => client.SubscribeAsync("db", "sub", "SELECT * FROM docs"));
    }

    [Test]
    public async Task WildcardDeltaEtagIsRejected()
    {
        using var handler = new ReactiveHandler(wildcardPollEtag: true);
        using var httpClient = new HttpClient(handler);
        var client = new TerraScaleReactiveHttpClient(httpClient, new TerraScaleReactiveHttpClientOptions { MaxRetryAttempts = 0 });
        await using var subscription = await client.SubscribeAsync("db", "sub", "SELECT * FROM docs");
        await Assert.ThrowsAsync<TerraScaleReactiveProtocolException>(() => subscription.ReadNextAsync());
    }

    [Test]
    public async Task LegacyMapFrameIsRejectedFailClosed()
    {
        using var handler = new ReactiveHandler(legacyFrame: true);
        using var httpClient = new HttpClient(handler);
        var client = new TerraScaleReactiveHttpClient(httpClient, new TerraScaleReactiveHttpClientOptions { MaxRetryAttempts = 0 });
        await using var subscription = await client.SubscribeAsync("db", "sub", "SELECT * FROM docs");
        await Assert.ThrowsAsync<TerraScaleReactiveProtocolException>(() => subscription.ReadNextAsync());
    }

    [Test]
    public async Task SameRevisionDigestDivergenceIsRejected()
    {
        using var handler = new ReactiveHandler(pollFrame: Envelope(4, body: Delta("sub", 3, 0, 0, "Y3AtMA==", digest: "different")));
        using var httpClient = new HttpClient(handler);
        var client = new TerraScaleReactiveHttpClient(httpClient, new TerraScaleReactiveHttpClientOptions { MaxRetryAttempts = 0 });
        await using var subscription = await client.SubscribeAsync("db", "sub", "SELECT * FROM docs");
        await Assert.ThrowsAsync<TerraScaleReactiveProtocolException>(() => subscription.ReadNextAsync());
    }

    [Test]
    public async Task AuthorizationChangeRejectsEnvelopeGenerationMismatch()
    {
        using var handler = new ReactiveHandler(pollFrame: Envelope(8, body: AuthorizationChanged(8, 7)));
        using var httpClient = new HttpClient(handler);
        var client = new TerraScaleReactiveHttpClient(httpClient, new TerraScaleReactiveHttpClientOptions { MaxRetryAttempts = 0 });
        await using var subscription = await client.SubscribeAsync("db", "sub", "SELECT * FROM docs");
        await Assert.ThrowsAsync<TerraScaleReactiveProtocolException>(() => subscription.ReadNextAsync());
    }

    [Test]
    public async Task ReattachRejectsOlderAuthorizationGeneration()
    {
        using var handler = new ReactiveHandler(pollStatus: HttpStatusCode.Conflict, staleReattachAuthorization: true);
        using var httpClient = new HttpClient(handler);
        var client = new TerraScaleReactiveHttpClient(httpClient, new TerraScaleReactiveHttpClientOptions { MaxRetryAttempts = 0 });
        await using var subscription = await client.SubscribeAsync("db", "sub", "SELECT * FROM docs");
        await Assert.ThrowsAsync<TerraScaleReactiveProtocolException>(() => subscription.ReadNextAsync());
    }

    [Test]
    public async Task MatchingButStaleAuthorizationChangeIsRejected()
    {
        using var handler = new ReactiveHandler(pollFrame: Envelope(8, body: AuthorizationChanged(0, 0)));
        using var httpClient = new HttpClient(handler);
        var client = new TerraScaleReactiveHttpClient(httpClient, new TerraScaleReactiveHttpClientOptions { MaxRetryAttempts = 0 });
        await using var subscription = await client.SubscribeAsync("db", "sub", "SELECT * FROM docs");
        await Assert.ThrowsAsync<TerraScaleReactiveProtocolException>(() => subscription.ReadNextAsync());
    }

    [Test]
    public async Task OrdinaryStaleSnapshotIsRejected()
    {
        using var handler = new ReactiveHandler(pollFrame: Envelope(3, body: SnapshotMessage("sub", Snapshot(3, 0, 0, "Y3AtMA=="))));
        using var httpClient = new HttpClient(handler);
        var client = new TerraScaleReactiveHttpClient(httpClient, new TerraScaleReactiveHttpClientOptions { MaxRetryAttempts = 0 });
        await using var subscription = await client.SubscribeAsync("db", "sub", "SELECT * FROM docs");
        await Assert.ThrowsAsync<TerraScaleReactiveProtocolException>(() => subscription.ReadNextAsync());
    }

    [Test]
    public async Task DeltaOperationsUpdateTheCurrentSnapshot()
    {
        var insert = new byte[] { 0x91, 0x94, 0x01, 0xa1, (byte)'x', 0x00, 0x90 };
        using var handler = new ReactiveHandler(pollFrame: Envelope(4, body: Delta("sub", 3, 0, 1, "Y3AtMQ==", operations: insert)));
        using var httpClient = new HttpClient(handler);
        var client = new TerraScaleReactiveHttpClient(httpClient, new TerraScaleReactiveHttpClientOptions { MaxRetryAttempts = 0 });
        await using var subscription = await client.SubscribeAsync("db", "sub", "SELECT * FROM docs");
        await subscription.ReadNextAsync();
        await Assert.That(subscription.CurrentSnapshot.Items).Count().IsEqualTo(1);
        await Assert.That(subscription.CurrentSnapshot.Items[0].Identity).IsEqualTo("x");
    }

    [Test]
    public async Task CancellationStopsAnInFlightPoll()
    {
        using var handler = new ReactiveHandler(blockPoll: true);
        using var httpClient = new HttpClient(handler);
        var client = new TerraScaleReactiveHttpClient(httpClient, new TerraScaleReactiveHttpClientOptions { MaxRetryAttempts = 0 });
        await using var subscription = await client.SubscribeAsync("db", "sub", "SELECT * FROM docs");
        using var cancellation = new CancellationTokenSource();
        var pending = subscription.ReadNextAsync(cancellation.Token);
        await handler.PollStarted.Task;
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
    }

    [Test]
    public async Task DisposalCancelsActiveAndQueuedPollsAndWaitsForCleanup()
    {
        using var handler = new ReactiveHandler(blockPoll: true);
        using var httpClient = new HttpClient(handler);
        var client = new TerraScaleReactiveHttpClient(httpClient, new TerraScaleReactiveHttpClientOptions { MaxRetryAttempts = 0 });
        var subscription = await client.SubscribeAsync("db", "sub", "SELECT * FROM docs");
        var pending = subscription.ReadNextAsync();
        await handler.PollStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = subscription.ReadNextAsync();
        var disposal = subscription.DisposeAsync().AsTask();
        var repeatedDisposal = subscription.DisposeAsync().AsTask();
        await Task.WhenAll(disposal, repeatedDisposal).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        await Assert.ThrowsAsync<OperationCanceledException>(() => queued);
        await Assert.That(subscription.SessionState).IsEqualTo(TerraScaleViewSessionState.Closed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => subscription.ReadNextAsync());
    }

    [Test]
    public async Task LongSubscriptionBoundsEtagRetentionAndRejectsExpiredReplay()
    {
        var replayOldRevision = false;
        using var handler = new ReactiveHandler(pollResponseFactory: request =>
        {
            var from = uint.Parse(request.RequestUri!.Query["?offset=".Length..], System.Globalization.CultureInfo.InvariantCulture);
            var to = replayOldRevision ? 1u : from + 1;
            var response = ReactiveHandler.Binary(Envelope(4, body: Delta("sub", 3, to - 1, to, "Y3AtMQ==")));
            response.Headers.ETag = new EntityTagHeaderValue($"\"revision-{to}\"");
            return response;
        });
        using var http = new HttpClient(handler);
        var client = new TerraScaleReactiveHttpClient(http, new TerraScaleReactiveHttpClientOptions { MaxRetryAttempts = 0 });
        await using var subscription = await client.SubscribeAsync("db", "sub", "SELECT * FROM docs");

        for (var revision = 1ul; revision <= 10_000; revision++)
        {
            await subscription.ReadNextAsync();
            await Assert.That(subscription.Offset).IsEqualTo(revision);
            await Assert.That(subscription.RetainedEtagCount).IsEqualTo(1);
        }

        replayOldRevision = true;
        await Assert.ThrowsAsync<TerraScaleReactiveProtocolException>(() => subscription.ReadNextAsync());
        await Assert.That(subscription.Offset).IsEqualTo(10_000ul);
        await Assert.That(subscription.RetainedEtagCount).IsEqualTo(1);
    }

    [Test]
    public async Task CurrentRevisionEtagSurvivesHeartbeatAndRejectedFutureFrame()
    {
        var frame = Envelope(4, body: Delta("sub", 3, 0, 1, "Y3AtMQ=="));
        var etag = "\"revision-1\"";
        using var handler = new ReactiveHandler(pollResponseFactory: _ =>
        {
            var response = ReactiveHandler.Binary(frame);
            response.Headers.ETag = new EntityTagHeaderValue(etag);
            return response;
        });
        using var http = new HttpClient(handler);
        var client = new TerraScaleReactiveHttpClient(http, new TerraScaleReactiveHttpClientOptions { MaxRetryAttempts = 0 });
        await using var subscription = await client.SubscribeAsync("db", "sub", "SELECT * FROM docs");
        await subscription.ReadNextAsync();
        var replay = await subscription.ReadNextAsync();
        await Assert.That(replay!.Frame).IsTypeOf<TerraScaleReactiveHeartbeat>();

        frame = Envelope(6, body: [0x90]);
        await subscription.ReadNextAsync();
        frame = Envelope(4, body: Delta("sub", 3, 2, 3, "Y3AtMQ=="));
        etag = "\"revision-3\"";
        await Assert.ThrowsAsync<TerraScaleReactiveProtocolException>(() => subscription.ReadNextAsync());
        frame = Envelope(4, body: Delta("sub", 3, 0, 1, "Y3AtMQ=="));
        etag = "\"changed-revision-1\"";
        await Assert.ThrowsAsync<TerraScaleReactiveProtocolException>(() => subscription.ReadNextAsync());
        await Assert.That(subscription.Offset).IsEqualTo(1ul);
        await Assert.That(subscription.RetainedEtagCount).IsEqualTo(1);
    }

    [Test]
    public async Task HostileFeatureAndMapFramesAreRejected()
    {
        foreach (var frame in new[] { Envelope(6, body: [0x90], features: 1), new byte[] { 0x81, 0xa1, (byte)'x', 0x01 } })
        {
            using var handler = new ReactiveHandler(pollFrame: frame);
            using var httpClient = new HttpClient(handler);
            var client = new TerraScaleReactiveHttpClient(httpClient, new TerraScaleReactiveHttpClientOptions { MaxRetryAttempts = 0 });
            await using var subscription = await client.SubscribeAsync("db", "sub", "SELECT * FROM docs");
            await Assert.ThrowsAsync<TerraScaleReactiveProtocolException>(() => subscription.ReadNextAsync());
        }
    }

    [Test]
    public async Task TextAndBinaryWireTypesCannotBeInterchanged()
    {
        var textField = new byte[] { 0x91, 0x94, 0x01, 0xa1, (byte)'x', 0x00, 0x91, 0x92, 0xa1, (byte)'p', 0xa1, (byte)'v' };
        foreach (var frame in new[]
        {
            Envelope(10, body: [0x92, 0x01, 0xc4, 0x01, (byte)'x']),
            Envelope(4, body: Delta("sub", 3, 0, 1, "Y3AtMQ==", operations: textField)),
        })
        {
            using var handler = new ReactiveHandler(pollFrame: frame);
            using var httpClient = new HttpClient(handler);
            var client = new TerraScaleReactiveHttpClient(httpClient, new TerraScaleReactiveHttpClientOptions { MaxRetryAttempts = 0 });
            await using var subscription = await client.SubscribeAsync("db", "sub", "SELECT * FROM docs");
            await Assert.ThrowsAsync<TerraScaleReactiveProtocolException>(() => subscription.ReadNextAsync());
            await Assert.That(subscription.Offset).IsEqualTo(0ul);
            await Assert.That(subscription.CurrentSnapshot.Items.Count).IsEqualTo(0);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task InvalidDeltaAfterValidInsertDoesNotPartiallyPublish(bool unknownDiscriminant)
    {
        var operations = new byte[] { 0x92, 0x94, 0x01, 0xa1, (byte)'x', 0x00, 0x90 }
            .Concat(unknownDiscriminant ? new byte[] { 0x91, 0x07 } : new byte[] { 0x92, 0x04, 0xa1, (byte)'y' }).ToArray();
        using var handler = new ReactiveHandler(pollFrame: Envelope(4, body: Delta("sub", 3, 0, 1, "Y3AtMQ==", operations: operations)));
        using var httpClient = new HttpClient(handler);
        var client = new TerraScaleReactiveHttpClient(httpClient, new TerraScaleReactiveHttpClientOptions { MaxRetryAttempts = 0 });
        await using var subscription = await client.SubscribeAsync("db", "sub", "SELECT * FROM docs");
        var before = subscription.CurrentSnapshot;

        await Assert.ThrowsAsync<TerraScaleReactiveProtocolException>(() => subscription.ReadNextAsync());

        await Assert.That(ReferenceEquals(subscription.CurrentSnapshot, before)).IsTrue();
        await Assert.That(subscription.Offset).IsEqualTo(0ul);
        await Assert.That(subscription.CurrentSnapshot.Items.Count).IsEqualTo(0);
    }

    [Test]
    public async Task OversizedUnsignedPositionsProduceTypedProtocolFailures()
    {
        foreach (var operations in new[]
        {
            new byte[] { 0x91, 0x94, 0x01, 0xa1, (byte)'x', 0xce, 0xff, 0xff, 0xff, 0xff, 0x90 },
            new byte[] { 0x91, 0x93, 0x05, 0xa1, (byte)'x', 0xce, 0xff, 0xff, 0xff, 0xff },
        })
        {
            using var handler = new ReactiveHandler(pollFrame: Envelope(4, body: Delta("sub", 3, 0, 1, "Y3AtMQ==", operations: operations)));
            using var httpClient = new HttpClient(handler);
            var client = new TerraScaleReactiveHttpClient(httpClient, new TerraScaleReactiveHttpClientOptions { MaxRetryAttempts = 0 });
            await using var subscription = await client.SubscribeAsync("db", "sub", "SELECT * FROM docs");
            await Assert.ThrowsAsync<TerraScaleReactiveProtocolException>(() => subscription.ReadNextAsync());
            await Assert.That(subscription.Offset).IsEqualTo(0ul);
        }
    }

    [Test]
    public async Task BackpressureAndRegionFramesChangeSessionState()
    {
        using var handler = new ReactiveHandler(pollFrame: Envelope(11, body: [0x91, 0x01]));
        using var httpClient = new HttpClient(handler);
        var client = new TerraScaleReactiveHttpClient(httpClient, new TerraScaleReactiveHttpClientOptions { MaxRetryAttempts = 0 });
        await using var subscription = await client.SubscribeAsync("db", "sub", "SELECT * FROM docs");
        var paused = await subscription.ReadNextAsync();
        await Assert.That(paused!.Frame).IsTypeOf<TerraScaleReactiveBackpressure>();
        await Assert.That(subscription.Paused).IsTrue();

        using var regionHandler = new ReactiveHandler(pollFrame: Envelope(9, body: [0x91, 0xa3, (byte)'g', (byte)'r', (byte)'u']));
        using var regionHttpClient = new HttpClient(regionHandler);
        var regionClient = new TerraScaleReactiveHttpClient(regionHttpClient, new TerraScaleReactiveHttpClientOptions { MaxRetryAttempts = 0 });
        await using var regionSubscription = await regionClient.SubscribeAsync("db", "sub", "SELECT * FROM docs");
        var route = await regionSubscription.ReadNextAsync();
        await Assert.That(route!.Frame).IsTypeOf<TerraScaleReactiveRouteChange>();
        await Assert.That(regionSubscription.SessionState).IsEqualTo(TerraScaleViewSessionState.Live);
    }

    private sealed class ReactiveHandler(bool timeout = false, bool blockPoll = false, bool legacyFrame = false, byte[]? pollFrame = null, string subscription = "sub", HttpStatusCode? pollStatus = null, bool noStore = false, bool weakPollEtag = false, bool weakSnapshotEtag = false, bool wildcardPollEtag = false, bool wildcardSnapshotEtag = false, bool staleReattachAuthorization = false, string? websocketLog = null, Func<HttpRequestMessage, HttpResponseMessage>? pollResponseFactory = null, string capabilityHeader = "X-TerraScale-Capability", string? view = null, string? wireSubscription = null, string? secondView = null) : HttpMessageHandler
    {
        public List<RequestObservation> Requests { get; } = [];
        public TaskCompletionSource PollStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? AttachBody { get; private set; }
        public List<string> AttachBodies { get; } = [];
        private int attachCount;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(pair => pair.Key, pair => pair.Value.Single());
            Requests.Add(new RequestObservation(request.Method, request.RequestUri!, headers));
            if (request.Method == HttpMethod.Post)
            {
                attachCount++;
                AttachBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                AttachBodies.Add(AttachBody);
                var attachedView = attachCount > 1 ? secondView ?? view ?? subscription : view ?? subscription;
                return Json($"{{\"view\":\"{attachedView}\",\"generation\":3,\"snapshot_chunks\":[\"/api/v1/reactive/views/{attachedView}/3/snapshot/0?cut=0\"],\"log\":\"{websocketLog ?? $"/api/v1/reactive/views/{attachedView}/3/log?offset=0"}\",\"checkpoint\":\"Y3AtMA==\",\"capability\":\"capability\",\"capability_header\":\"{capabilityHeader}\",\"offset\":0}}", HttpStatusCode.Created);
            }
            if (request.RequestUri!.AbsolutePath.EndsWith("/snapshot/0", StringComparison.Ordinal))
            {
                var auth = staleReattachAuthorization && attachCount > 1 ? 0u : 1u;
                var snapshotView = attachCount > 1 ? secondView ?? view ?? subscription : view ?? subscription;
                var response = Binary(Envelope(3, 0, SnapshotMessage(wireSubscription ?? snapshotView, Snapshot(3, 0, auth, "Y3AtMA=="))));
                if (weakSnapshotEtag) response.Headers.ETag = new EntityTagHeaderValue("\"snapshot-0\"", isWeak: true);
                else if (wildcardSnapshotEtag) response.Headers.ETag = EntityTagHeaderValue.Any;
                else response.Headers.ETag = new EntityTagHeaderValue("\"snapshot-0\"");
                return response;
            }
            PollStarted.TrySetResult();
            if (blockPoll) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (pollResponseFactory is not null) return pollResponseFactory(request);
            if (pollStatus is not null)
            {
                var response = Binary([]);
                response.StatusCode = pollStatus.Value;
                if (noStore) response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
                return response;
            }
            if (timeout)
            {
                var response = Binary([]);
                response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
                return response;
            }
            var body = pollFrame ?? (legacyFrame ? new byte[] { 0x81, 0xa1, (byte)'x', 0x01 } : Envelope(4, 0, Delta(wireSubscription ?? view ?? subscription, 3, 0, 1, "Y3AtMQ==")));
            var poll = Binary(body);
            if (weakPollEtag) poll.Headers.ETag = new EntityTagHeaderValue("\"revision-1\"", isWeak: true);
            else if (wildcardPollEtag) poll.Headers.ETag = EntityTagHeaderValue.Any;
            else poll.Headers.ETag = new EntityTagHeaderValue("\"revision-1\"");
            return poll;
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        internal static HttpResponseMessage Binary(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
    }

    private sealed record RequestObservation(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers);

    private static async Task ServeWebSocketAsync(TcpListener listener, string capability, string? selectedProtocol, CancellationToken cancellationToken, bool sendFrame, byte[]? responseFrame = null, string? expectedPath = null, bool expectSubscribe = false, string? expectedWireView = null)
    {
        using var connection = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = connection.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        string? key = null;
        string? path = null;
        string? protocols = null;
        while (await reader.ReadLineAsync(cancellationToken) is { } line && line.Length > 0)
        {
            if (line.StartsWith("GET ", StringComparison.Ordinal)) path = line[4..line.IndexOf(' ', 4)];
            if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)) key = line[(line.IndexOf(':') + 1)..].Trim();
            if (line.StartsWith("Sec-WebSocket-Protocol:", StringComparison.OrdinalIgnoreCase)) protocols = line[(line.IndexOf(':') + 1)..].Trim();
        }

        if (expectedPath is not null) await Assert.That(path).IsEqualTo(expectedPath);

        await Assert.That(protocols).IsEqualTo($"terrascale.sync.v1, terrascale.cap.{capability}");
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key! + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        var response = new StringBuilder("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n");
        if (selectedProtocol is not null) response.Append("Sec-WebSocket-Protocol: ").Append(selectedProtocol).Append("\r\n");
        response.Append("Sec-WebSocket-Accept: ").Append(accept).Append("\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response.ToString()), cancellationToken);
        if (sendFrame)
        {
            if (expectSubscribe)
            {
                var frame = await ReadWebSocketFrameAsync(stream, cancellationToken);
                await Assert.That(frame.Length > 40).IsTrue();
                await Assert.That(frame.AsSpan(0, 7).SequenceEqual(new byte[] { 0x97, 0x01, 0x00, 0x00, 0x01, 0x01, 0x00 })).IsTrue();
                if (expectedWireView is not null)
                {
                    var expected = Encoding.UTF8.GetBytes(expectedWireView);
                    await Assert.That(frame[8]).IsEqualTo((byte)(0xa0 | expected.Length));
                    await Assert.That(frame.AsSpan(9, expected.Length).SequenceEqual(expected)).IsTrue();
                }
            }
            var payload = responseFrame ?? new byte[] { 0x90 };
            if (payload.Length > 125) throw new InvalidOperationException("Test frame helper only supports short payloads.");
            await stream.WriteAsync(new[] { (byte)0x82, (byte)payload.Length }, cancellationToken);
            await stream.WriteAsync(payload, cancellationToken);
            await stream.WriteAsync(new byte[] { 0x88, 0x00 }, cancellationToken);
        }
    }

    private static async Task<byte[]> ReadWebSocketFrameAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var header = new byte[2]; await stream.ReadExactlyAsync(header, cancellationToken);
        var length = header[1] & 0x7f; var mask = new byte[4]; await stream.ReadExactlyAsync(mask, cancellationToken);
        var payload = new byte[length]; await stream.ReadExactlyAsync(payload, cancellationToken);
        for (var i = 0; i < payload.Length; i++) payload[i] ^= mask[i % 4];
        return payload;
    }

    private static byte[] Envelope(uint tag, ulong correlation = 0, byte[]? body = null, uint features = 0)
    {
        var result = new List<byte> { 0x97, 0x01, 0x00, (byte)features, (byte)tag, 0, 0 };
        result.AddRange(body ?? Array.Empty<byte>());
        return result.ToArray();
    }

    private static byte[] Snapshot(uint generation, uint revision, uint auth, string checkpoint)
    {
        var bytes = Convert.FromBase64String(checkpoint);
        var result = new List<byte> { 0x96, (byte)generation, (byte)auth, (byte)revision, 0x90, 0xa1, (byte)'d', 0xc4, (byte)bytes.Length };
        result.AddRange(bytes);
        return result.ToArray();
    }

    private static byte[] AuthorizationChanged(uint envelopeGeneration, uint snapshotAuth)
    {
        var result = new List<byte> { 0x94, 0xa3, (byte)'s', (byte)'u', (byte)'b', (byte)envelopeGeneration, 0x90 };
        result.AddRange(Snapshot(3, 0, snapshotAuth, "Y3AtMA=="));
        return result.ToArray();
    }

    private static byte[] Delta(string subscription, uint generation, uint from, uint to, string checkpoint, string digest = "d", byte[]? operations = null)
    {
        var bytes = Convert.FromBase64String(checkpoint);
        var result = new List<byte> { 0x97, (byte)(0xa0 | subscription.Length) };
        result.AddRange(Encoding.UTF8.GetBytes(subscription));
        result.AddRange(Unsigned(generation));
        result.AddRange(Unsigned(from));
        result.AddRange(Unsigned(to));
        result.AddRange(operations ?? [0x90]);
        result.Add((byte)(0xa0 | digest.Length));
        result.AddRange(Encoding.UTF8.GetBytes(digest));
        result.AddRange(new byte[] { 0xc4, (byte)bytes.Length });
        result.AddRange(bytes);
        return result.ToArray();
    }

    private static byte[] Unsigned(uint value) => value <= 0x7f
        ? [(byte)value]
        : [0xce, (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static byte[] SnapshotMessage(string subscription, byte[] snapshot)
    {
        var result = new List<byte> { 0x92, (byte)(0xa0 | subscription.Length) };
        result.AddRange(Encoding.UTF8.GetBytes(subscription));
        result.AddRange(snapshot);
        return result.ToArray();
    }
}
