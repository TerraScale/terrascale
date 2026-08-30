// <copyright file="TbepClient.cs" company="TerraScale">
// TerraBase (SE-14). See LICENSE.md.
// </copyright>

namespace TerraBase.Protocol;

using System.Net.Sockets;
using System.Threading;

/// <summary>
/// The TBEP client over the required local Unix-domain-socket binding
/// (PD-2026-08-26-11 D1): the single interop artifact every C# consumer
/// (the API host SE-08, the CLI SE-13 direct mode) uses to speak the
/// TerraBase Engine Protocol to the engine process.
/// </summary>
public sealed partial class TbepClient : IAsyncDisposable
{
    private readonly Socket? socket;
    private readonly Stream stream;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object inboxLock = new();
    private readonly Queue<TbepWire.Frame> inbox = new();
    private ulong correlation;
    private string? tenant;
    private string? principal;

    private TbepClient(Socket socket)
    {
        this.socket = socket;
        this.stream = new NetworkStream(socket, ownsSocket: true);
    }

    /// <summary>Wraps the client discipline over an already-connected transport stream — the QUIC binding's bidirectional channel (P4).</summary>
    /// <param name="stream">The connected bidirectional stream carrying TBEP frames.</param>
    /// <returns>The client, unbound.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "the stream's ownership transfers to the client")]
    public static TbepClient OverStream(Stream stream) => new(stream);

    private TbepClient(Stream stream)
    {
        this.socket = null;
        this.stream = stream;
    }

    /// <summary>Connects to a serving engine process at a UDS path.</summary>
    /// <param name="path">The socket path the engine bound.</param>
    /// <returns>The connected client, unbound.</returns>
    /// <exception cref="SocketException">The connect failed.</exception>
    public static TbepClient Connect(string path)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Connect(new UnixDomainSocketEndPoint(path));
        return new TbepClient(socket);
    }

    /// <summary>The bound tenant, or <c>null</c> before the channel binding.</summary>
    public string? Tenant => this.tenant;

    /// <summary>Binds the channel to the backend-supplied principal and tenant (PD-2026-08-26-11 D1: no frame carries a tenant).</summary>
    /// <param name="principal">The backend principal.</param>
    /// <param name="tenant">The backend-supplied tenant the engine enforces.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The bind response.</returns>
    /// <exception cref="TbepException">The engine refused the binding or the frame failed.</exception>
    public async Task<ChannelBindResponse> BindChannelAsync(string principal, string tenant, CancellationToken cancellationToken = default)
    {
        var request = new ChannelBindRequest { Principal = principal, Tenant = tenant };
        var answer = await this.CallAsync(
            "channel.bind",
            request,
            idempotency: null,
            expect: TbepRegistry.ResponseKindOf("channel.bind"),
            cancellationToken).ConfigureAwait(false);
        var response = MessagePackSerializer.Deserialize<ChannelBindResponse>(answer.Body, TbepOptions.Strict);
        this.tenant = tenant;
        this.principal = principal;
        return response;
    }

    /// <summary>
    /// Protocol-level reconnect/resume (P4): opens a fresh channel to a
    /// serving engine process and re-binds the SAME principal and tenant
    /// this channel held. The engine's durable idempotency ledger makes
    /// the resumed channel's retries safe — the same operation identity
    /// replays its recorded receipt instead of re-applying.
    /// </summary>
    /// <param name="path">The socket path the (possibly restarted) engine bound.</param>
    /// <returns>The reconnected, re-bound client.</returns>
    /// <exception cref="TbepException">The channel was never bound, or the re-bind was refused.</exception>
    public TbepClient Reconnect(string path)
    {
        if (this.tenant is null || this.principal is null)
        {
            throw new TbepException("only a bound channel can resume");
        }

        var resumed = Connect(path);
        try
        {
            resumed.BindChannelAsync(this.principal, this.tenant).GetAwaiter().GetResult();
            return resumed;
        }
        catch (Exception)
        {
            resumed.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    /// <summary>Connection-open capability and version negotiation: the client names its supported wire versions and capabilities; the peers select the highest common version, unknown capabilities are ignored.</summary>
    /// <param name="supportedVersions">The wire versions the client can speak.</param>
    /// <param name="capabilities">The capabilities the client offers.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The negotiation response with the selected version and accepted capabilities.</returns>
    /// <exception cref="TbepException">No version is common (the typed <c>ProtocolVersionMismatch</c>) or the frame failed.</exception>
    public async Task<NegotiationResponse> NegotiateAsync(ushort[] supportedVersions, string[] capabilities, CancellationToken cancellationToken = default)
    {
        var answer = await this.CallKindedAsync(
            "channel.negotiate",
            (ushort)TbepFrameKind.NegotiationRequest,
            new NegotiationRequest { SupportedVersions = supportedVersions, Capabilities = capabilities },
            idempotency: null,
            expect: (ushort)TbepFrameKind.NegotiationResponse,
            cancellationToken).ConfigureAwait(false);
        return MessagePackSerializer.Deserialize<NegotiationResponse>(answer.Body, TbepOptions.Strict);
    }

    /// <summary>Configures the Storage Tier Mover's policy (mutation): the idempotency key is the retry identity.</summary>
    /// <param name="policyId">The policy's stable identity.</param>
    /// <param name="version">The policy's version.</param>
    /// <param name="nvmeBudgetBytes">The NVMe byte budget demotion starts beyond.</param>
    /// <param name="minResidenceMoves">The minimum residence before demotion.</param>
    /// <param name="promotionThreshold">The access count that promotes a cold range back.</param>
    /// <param name="pinnedSegmentIds">The segments that never demote.</param>
    /// <param name="idempotencyKey">The envelope's idempotency key.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The configure response.</returns>
    /// <exception cref="TbepException">The operation failed.</exception>
    public async Task<TierConfigureResponse> TierConfigureAsync(string policyId, uint version, ulong nvmeBudgetBytes, uint minResidenceMoves, uint promotionThreshold, ulong[] pinnedSegmentIds, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        var answer = await this.CallAsync(
            "tier.configure",
            new TierConfigureRequest
            {
                PolicyId = policyId,
                Version = version,
                NvmeBudgetBytes = nvmeBudgetBytes,
                MinResidenceMoves = minResidenceMoves,
                PromotionThreshold = promotionThreshold,
                PinnedSegmentIds = pinnedSegmentIds,
            },
            idempotency: idempotencyKey,
            expect: TbepRegistry.ResponseKindOf("tier.configure"),
            cancellationToken).ConfigureAwait(false);
        return MessagePackSerializer.Deserialize<TierConfigureResponse>(answer.Body, TbepOptions.Strict);
    }

    /// <summary>Inspects the tier state: the control state and the backend's tier facts.</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The inspect response.</returns>
    /// <exception cref="TbepException">The operation failed.</exception>
    public async Task<TierInspectResponse> TierInspectAsync(CancellationToken cancellationToken = default)
    {
        var answer = await this.CallAsync(
            "tier.inspect",
            new TierInspectRequest(),
            idempotency: null,
            expect: TbepRegistry.ResponseKindOf("tier.inspect"),
            cancellationToken).ConfigureAwait(false);
        return MessagePackSerializer.Deserialize<TierInspectResponse>(answer.Body, TbepOptions.Strict);
    }

    /// <summary>Runs one dry mover pass: the receipt explains the next decision without transferring anything.</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The dry-run receipt.</returns>
    /// <exception cref="TbepException">The operation failed.</exception>
    public async Task<TierDryRunResponse> TierDryRunAsync(CancellationToken cancellationToken = default)
    {
        var answer = await this.CallAsync(
            "tier.dry_run",
            new TierDryRunRequest(),
            idempotency: null,
            expect: TbepRegistry.ResponseKindOf("tier.dry_run"),
            cancellationToken).ConfigureAwait(false);
        return MessagePackSerializer.Deserialize<TierDryRunResponse>(answer.Body, TbepOptions.Strict);
    }

    /// <summary>Reads one document.</summary>
    /// <param name="collection">The collection.</param>
    /// <param name="document">The document id.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The read response.</returns>
    /// <exception cref="TbepException">The operation failed.</exception>
    public async Task<DocumentReadResponse> DocumentReadAsync(string collection, string document, CancellationToken cancellationToken = default)
    {
        var answer = await this.CallAsync(
            "document.read",
            new DocumentReadRequest { Collection = collection, Document = document },
            idempotency: null,
            expect: TbepRegistry.ResponseKindOf("document.read"),
            cancellationToken).ConfigureAwait(false);
        return MessagePackSerializer.Deserialize<DocumentReadResponse>(answer.Body, TbepOptions.Strict);
    }

    /// <summary>Writes one document (mutation): the idempotency key is the retry identity the engine replays.</summary>
    /// <param name="document">The whole document body to put.</param>
    /// <param name="expected">The expected current version of the document.</param>
    /// <param name="operationId">The stable operation identity.</param>
    /// <param name="idempotencyKey">The envelope's idempotency key.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The write response with its outcome.</returns>
    /// <exception cref="TbepException">The operation failed.</exception>
    public async Task<DocumentWriteResponse> DocumentWriteAsync(DocumentPut document, ExpectedVersion expected, string operationId, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        var answer = await this.CallAsync(
            "document.write",
            new DocumentWriteRequest { Document = document, Expected = expected, OperationId = operationId },
            idempotency: idempotencyKey,
            expect: TbepRegistry.ResponseKindOf("document.write"),
            cancellationToken).ConfigureAwait(false);
        return MessagePackSerializer.Deserialize<DocumentWriteResponse>(answer.Body, TbepOptions.Strict);
    }

    /// <summary>Writes one atomic document batch (mutation): one frame, one engine transaction — every item commits together or none does. The idempotency key is the whole batch's retry identity.</summary>
    /// <param name="items">The batch items, in order.</param>
    /// <param name="ack">The acknowledgement level, or <c>null</c> for the regional default.</param>
    /// <param name="idempotencyKey">The envelope's idempotency key.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The batch outcome.</returns>
    /// <exception cref="TbepException">The operation failed.</exception>
    public async Task<DocumentBatchResponse> DocumentBatchWriteAsync(BatchWrite[] items, Acknowledgement? ack, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        var answer = await this.CallAsync(
            "document.batch",
            new DocumentBatchRequest { Items = items, Ack = ack },
            idempotency: idempotencyKey,
            expect: TbepRegistry.ResponseKindOf("document.batch"),
            cancellationToken).ConfigureAwait(false);
        return MessagePackSerializer.Deserialize<DocumentBatchResponse>(answer.Body, TbepOptions.Strict);
    }

    /// <summary>Deletes one document (mutation): the idempotency key is the retry identity the engine replays.</summary>
    /// <param name="collection">The collection.</param>
    /// <param name="document">The document id.</param>
    /// <param name="expected">The expected current version of the document.</param>
    /// <param name="operationId">The stable operation identity.</param>
    /// <param name="idempotencyKey">The envelope's idempotency key.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The delete response with its outcome.</returns>
    /// <exception cref="TbepException">The operation failed.</exception>
    public async Task<DocumentDeleteResponse> DocumentDeleteAsync(string collection, string document, ExpectedVersion expected, string operationId, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        var answer = await this.CallAsync(
            "document.delete",
            new DocumentDeleteRequest { Collection = collection, Document = document, Expected = expected, OperationId = operationId },
            idempotency: idempotencyKey,
            expect: TbepRegistry.ResponseKindOf("document.delete"),
            cancellationToken).ConfigureAwait(false);
        return MessagePackSerializer.Deserialize<DocumentDeleteResponse>(answer.Body, TbepOptions.Strict);
    }

    /// <summary>Submits the query-cancellation live signal (GAP-Q12, PB-4034).</summary>
    /// <param name="queryId">The in-flight query identity.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The cancel response.</returns>
    /// <exception cref="TbepException">The operation failed.</exception>
    public async Task<QueryCancelResponse> QueryCancelAsync(ulong queryId, CancellationToken cancellationToken = default)
    {
        var answer = await this.CallAsync(
            "query.cancel",
            new QueryCancelRequest { QueryId = queryId },
            idempotency: null,
            expect: TbepRegistry.ResponseKindOf("query.cancel"),
            cancellationToken).ConfigureAwait(false);
        return MessagePackSerializer.Deserialize<QueryCancelResponse>(answer.Body, TbepOptions.Strict);
    }

    /// <summary>Opens one sync session (the terrascale.sync.v2 serving seam, PD-2026-08-26-12).</summary>
    /// <param name="collection">The collection scope.</param>
    /// <param name="resume">The resume checkpoint bytes, or <c>null</c> for a fresh session.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The session setup response.</returns>
    /// <exception cref="TbepException">The operation failed.</exception>
    public async Task<SyncSessionSetupResponse> SyncSessionSetupAsync(string collection, byte[]? resume, CancellationToken cancellationToken = default)
    {
        var answer = await this.CallAsync(
            "sync.session_setup",
            new SyncSessionSetupRequest { Collection = collection, Resume = resume },
            idempotency: null,
            expect: TbepRegistry.ResponseKindOf("sync.session_setup"),
            cancellationToken).ConfigureAwait(false);
        return MessagePackSerializer.Deserialize<SyncSessionSetupResponse>(answer.Body, TbepOptions.Strict);
    }

    /// <summary>Submits one server-ack mutation (PD-2026-08-26-12 D2: the idempotency key is the retry identity).</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="document">The document id.</param>
    /// <param name="change">The change.</param>
    /// <param name="idempotencyKey">The stable retry identity.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The mutation outcome.</returns>
    /// <exception cref="TbepException">The operation failed.</exception>
    public async Task<SyncMutationSubmitResponse> SyncMutationSubmitAsync(
        ulong sessionId,
        string document,
        SyncChange change,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        var answer = await this.CallAsync(
            "sync.mutation_submit",
            new SyncMutationSubmitRequest { SessionId = sessionId, Document = document, Change = change },
            idempotency: idempotencyKey,
            expect: TbepRegistry.ResponseKindOf("sync.mutation_submit"),
            cancellationToken).ConfigureAwait(false);
        return MessagePackSerializer.Deserialize<SyncMutationSubmitResponse>(answer.Body, TbepOptions.Strict);
    }

    /// <summary>Subscribes one backbone stream; the returned reader yields the ordered batches, the content-free triggers, and the close frame.</summary>
    /// <param name="subscriptionId">The caller-assigned subscription identity.</param>
    /// <param name="collections">The collection filter, or <c>null</c> for every collection.</param>
    /// <param name="fromSequence">The replay start, or <c>null</c> for live-only.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The subscribe response.</returns>
    /// <exception cref="TbepException">The operation failed.</exception>
    public async Task<BackboneSubscribeResponse> BackboneSubscribeAsync(
        ulong subscriptionId,
        string[]? collections,
        ulong? fromSequence,
        CancellationToken cancellationToken = default)
    {
        var from = fromSequence is { } sequence
            ? SubscriptionStart.Sequence(sequence)
            : SubscriptionStart.Live();
        var answer = await this.CallAsync(
            "backbone.subscribe",
            new BackboneSubscribeRequest { SubscriptionId = subscriptionId, Collections = collections, From = from },
            idempotency: null,
            expect: TbepRegistry.ResponseKindOf("backbone.subscribe"),
            cancellationToken).ConfigureAwait(false);
        return MessagePackSerializer.Deserialize<BackboneSubscribeResponse>(answer.Body, TbepOptions.Strict);
    }

    /// <summary>
    /// Reads the next frame from the channel (an answer or an unsolicited
    /// notification). Frames a subscription streamed while a call was in
    /// flight were buffered by that call; they surface here first, in
    /// arrival order, before the socket is read again.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The next frame.</returns>
    /// <exception cref="TbepWire.WireException">A hostile frame arrived.</exception>
    public async Task<TbepWire.Frame> ReadFrameAsync(CancellationToken cancellationToken = default)
    {
        lock (this.inboxLock)
        {
            if (this.inbox.Count > 0)
            {
                return this.inbox.Dequeue();
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return await TbepWire.ReadFrame(this.stream).ConfigureAwait(false)
            ?? throw new TbepException("the engine closed the channel");
    }

    /// <summary>Reads one typed notification body off the channel.</summary>
    /// <typeparam name="T">The body type.</typeparam>
    /// <param name="frame">The frame to decode.</param>
    /// <returns>The typed body.</returns>
    public static T DecodeBody<T>(TbepWire.Frame frame) =>
        MessagePackSerializer.Deserialize<T>(frame.Body, TbepOptions.Strict);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await this.stream.DisposeAsync().ConfigureAwait(false);
        this.socket?.Dispose();
        this.gate.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task<TbepWire.Frame> CallAsync<T>(
        string operation,
        T request,
        string? idempotency,
        ushort expect,
        CancellationToken cancellationToken) =>
        await this.CallKindedAsync(
            operation,
            TbepRegistry.RequestKindOf(operation),
            request,
            idempotency,
            expect,
            cancellationToken).ConfigureAwait(false);

    /// <summary>The call discipline over an explicit request kind — the channel control frames (negotiation) live outside the operation registry's name table.</summary>
    /// <typeparam name="T">The request body type.</typeparam>
    /// <param name="operation">The operation name, for error text only.</param>
    /// <param name="requestKind">The request frame kind code.</param>
    /// <param name="request">The request body.</param>
    /// <param name="idempotency">The idempotency key, or <c>null</c>.</param>
    /// <param name="expect">The response frame kind code.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The answered frame.</returns>
    private async Task<TbepWire.Frame> CallKindedAsync<T>(
        string operation,
        ushort requestKind,
        T request,
        string? idempotency,
        ushort expect,
        CancellationToken cancellationToken)
    {
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var body = MessagePackSerializer.Serialize(request, TbepOptions.Strict);
            var correlation = ++this.correlation;
            var frame = TbepWire.Seal(requestKind, correlation, 0, idempotency, body);
            await TbepWire.WriteFrame(this.stream, frame).ConfigureAwait(false);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var answer = await TbepWire.ReadFrame(this.stream).ConfigureAwait(false)
                    ?? throw new TbepException("the engine closed the channel");
                if (answer.Causation == correlation && answer.Kind == expect)
                {
                    return answer;
                }

                if (answer.Causation == correlation)
                {
                    var error = MessagePackSerializer.Deserialize<Error>(answer.Body, TbepOptions.Strict);
                    throw new TbepException(operation, error.Code, error.Detail);
                }

                // An unsolicited notification (correlation 0, causation
                // naming its subscription) that races this call is
                // buffered for the client's next ReadFrameAsync — in
                // arrival order — never consumed and discarded.
                if (answer.Correlation == 0)
                {
                    lock (this.inboxLock)
                    {
                        this.inbox.Enqueue(answer);
                    }

                    continue;
                }

                throw new TbepException($"an unexpected frame answered {operation}: kind {answer.Kind}");
            }
        }
        finally
        {
            this.gate.Release();
        }
    }
}

/// <summary>The typed refusal the engine answered with.</summary>
public sealed class TbepException : Exception
{
    /// <summary>Initializes the typed refusal the engine answered with.</summary>
    /// <param name="operation">The refused operation.</param>
    /// <param name="code">The closed error code.</param>
    /// <param name="detail">The bounded machine-readable detail.</param>
    public TbepException(string operation, ErrorCode code, string detail)
        : base($"{operation} refused: {code.Variant} ({detail})")
    {
        this.Code = code;
        this.Detail = detail;
    }

    /// <summary>Initializes a plain transport failure.</summary>
    /// <param name="message">The failure text.</param>
    public TbepException(string message)
        : base(message)
    {
        this.Code = null;
        this.Detail = message;
    }

    /// <summary>The closed error code, when the engine answered an error frame.</summary>
    public ErrorCode? Code { get; }

    /// <summary>The bounded machine-readable detail.</summary>
    public string Detail { get; } = string.Empty;
}

/// <summary>The serializer options of the protocol: strict, contractful, positional — contractless/typeless never enabled (PD-2026-08-26-02 D4).</summary>
public static class TbepOptions
{
    /// <summary>The shared strict options.</summary>
    public static readonly MessagePackSerializerOptions Strict = MessagePackSerializerOptions
        .Standard
        .WithSecurity(MessagePackSecurity.UntrustedData)
        .WithCompression(MessagePackCompression.None);
}
