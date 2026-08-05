using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;

namespace TerraScale.DotNet;

/// <summary>
/// One explicit, resumable TerraScale Sync WebSocket session.
/// </summary>
public sealed class TerraScaleSyncSession : IAsyncDisposable
{
    internal const int MaxClientMessageBytes = 1024 * 1024;
    internal const int MaxTransitionChunkBytes = 256 * 1024;
    internal const int MaxTransitionBytes = 8 * 1024 * 1024;
    internal const int MaxTransitionChunks = MaxTransitionBytes / MaxTransitionChunkBytes;
    internal const int MaxActiveQueries = 100;
    internal const int MaxOutgoingBufferedBytes = 2 * 1024 * 1024;
    private const int OutgoingMessageCapacity = 64;
    private static readonly TimeSpan OutgoingQueueTimeout = TimeSpan.FromSeconds(5);

    private readonly object gate = new();
    private readonly TerraScaleClient client;
    private readonly ValidatedSyncSessionOptions options;
    private readonly Func<ulong, CancellationToken, ValueTask<ConnectedSyncSocket>> connect;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim querySetGate = new(1, 1);
    private readonly TaskCompletionSource started =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<QueryEntry> queries = [];
    private readonly Dictionary<TerraScaleSyncId, PendingMutation> pendingMutations = [];
    private TerraScaleSyncClientState clientState = TerraScaleSyncClientState.Empty;
    private TerraScaleSyncConnectionState connectionState = TerraScaleSyncConnectionState.Disconnected;
    private TerraScaleSyncStateVersion? lastAcknowledgedVersion;
    private TerraScaleSyncDatabaseRevision maximumObservedRevision =
        TerraScaleSyncDatabaseRevision.Zero;
    private PhysicalConnection? currentConnection;
    private Task? runTask;
    private ulong connectionSequence;
    private ulong requestedQuerySetVersion;
    private bool hasAppliedTransition;
    private bool forceResync;
    private bool startCalled;
    private bool disposed;
    private string? ingressRegion;
    private string? servingRegion;
    private string? databaseRegion;
    private TerraScaleSyncUInt64 routeGeneration;
    private int? lastRoundTripMilliseconds;
    private string? lastErrorCode;

    internal TerraScaleSyncSession(
        TerraScaleClient client,
        TerraScaleSyncSessionOptions options,
        Func<ulong, CancellationToken, ValueTask<ConnectedSyncSocket>>? connector = null)
    {
        this.client = client;
        this.options = options.Validate();
        connect = connector ?? ConnectNativeAsync;
    }

    /// <summary>
    /// Raised after the physical connection lifecycle changes.
    /// </summary>
    public event EventHandler<TerraScaleSyncConnectionStateChangedEventArgs>? ConnectionStateChanged;

    /// <summary>
    /// The last atomically applied immutable client state.
    /// </summary>
    public TerraScaleSyncClientState State
    {
        get
        {
            lock (gate)
            {
                return clientState;
            }
        }
    }

    /// <summary>
    /// Current bounded connection, region, and revision diagnostics.
    /// </summary>
    public TerraScaleSyncConnectionDiagnostics Diagnostics
    {
        get
        {
            lock (gate)
            {
                return CreateDiagnosticsLocked();
            }
        }
    }

    /// <summary>
    /// Opens the session and waits until the first authenticated WebSocket is ready.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            ThrowIfDisposedLocked();
            if (startCalled)
            {
                throw new InvalidOperationException("The Sync session has already been started.");
            }

            startCalled = true;
            runTask = RunAsync();
        }

        try
        {
            await started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Acquires a JSON live query. Identical active queries share one network registration.
    /// </summary>
    public Task<TerraScaleSyncLiveQuery<JsonElement>> SubscribeAsync(
        TerraScaleSyncQuery query,
        CancellationToken cancellationToken = default) =>
        SubscribeCoreAsync<JsonElement>(query, typeInfo: null, cancellationToken);

    /// <summary>
    /// Acquires a source-generated typed live query. Identical active queries share one network registration.
    /// </summary>
    public Task<TerraScaleSyncLiveQuery<T>> SubscribeAsync<T>(
        TerraScaleSyncQuery query,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return SubscribeCoreAsync(query, typeInfo, cancellationToken);
    }

    /// <summary>
    /// Executes an idempotent mutation and retains optimistic layers until authoritative catch-up.
    /// Cancellation stops only the caller's wait; the session still reconciles an in-flight mutation.
    /// </summary>
    public async Task<TerraScaleSyncMutationResult> ExecuteMutationAsync(
        TerraScaleSyncMutation mutation,
        CancellationToken cancellationToken = default)
    {
        var payload = ValidateMutation(mutation);
        PendingMutation pending;
        PhysicalConnection? physical;
        IReadOnlySet<TerraScaleSyncId> changed;
        lock (gate)
        {
            ThrowIfNotRunningLocked();
            if (pendingMutations.Count >= MaxActiveQueries)
            {
                throw new InvalidOperationException(
                    $"A Sync session cannot retain more than {MaxActiveQueries} pending mutations.");
            }

            var mutationId = payload.ClientRequestId;
            var patches = mutation.OptimisticPatches ?? [];
            if (patches.Count > 0)
            {
                clientState = TerraScaleSyncStateReducer.BeginOptimisticMutation(
                    clientState,
                    mutationId,
                    patches,
                    out changed);
            }
            else
            {
                changed = FrozenSet<TerraScaleSyncId>.Empty;
            }

            pending = new(payload, patches.Count > 0);
            pendingMutations.Add(mutationId, pending);
            NotifyQueriesLocked(changed);
            physical = currentConnection;
        }

        if (physical is not null)
        {
            try
            {
                await TryQueueAsync(
                    physical,
                    TerraScaleSyncMessageType.ExecuteMutation,
                    payload,
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                RollbackUnsentMutation(pending);
                throw;
            }
        }

        return await pending.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void RollbackUnsentMutation(PendingMutation pending)
    {
        lock (gate)
        {
            if (!pendingMutations.Remove(pending.Payload.ClientRequestId))
            {
                return;
            }

            if (pending.HasOptimisticLayer)
            {
                clientState = TerraScaleSyncStateReducer.RollbackMutation(
                    clientState,
                    pending.Payload.ClientRequestId,
                    out var changed);
                NotifyQueriesLocked(changed);
            }
        }

        pending.Completion.TrySetCanceled();
    }

    /// <summary>
    /// Sends the latest configured bearer token through the protocol authentication-refresh path.
    /// </summary>
    public async Task RefreshAuthenticationAsync(CancellationToken cancellationToken = default)
    {
        PhysicalConnection physical;
        lock (gate)
        {
            ThrowIfNotRunningLocked();
            physical = currentConnection
                ?? throw new InvalidOperationException("The Sync session is not connected.");
        }

        var token = await client.ResolveSyncBearerTokenAsync(cancellationToken).ConfigureAwait(false);
        await QueueAsync(
            physical,
            TerraScaleSyncMessageType.Authenticate,
            new TerraScaleSyncAuthenticateMessage(token),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<TerraScaleSyncLiveQuery<T>> SubscribeCoreAsync<T>(
        TerraScaleSyncQuery query,
        JsonTypeInfo<T>? typeInfo,
        CancellationToken cancellationToken)
    {
        var addQuery = ValidateQuery(query);
        QueryEntry entry;
        QueryListener listener;
        PhysicalConnection? physical = null;
        TerraScaleSyncAddQuery[] added = [];
        lock (gate)
        {
            ThrowIfNotRunningLocked();
            entry = queries.FirstOrDefault(candidate => SameQuery(candidate.Query, addQuery))
                ?? AddQueryLocked(addQuery);
            listener = entry.AddListener();
            if (clientState.Queries.TryGetValue(entry.Query.QueryId, out var current))
            {
                listener.Channel.Writer.TryWrite(current);
            }

            if (entry.ReferenceCount == 1 && currentConnection is { } connected)
            {
                physical = connected;
                added = [entry.Query];
            }
        }

        try
        {
            if (physical is not null)
            {
                await QueueQueryModificationAsync(
                    physical,
                    added,
                    [],
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            await ReleaseQueryAsync(entry, listener, notifyServer: false).ConfigureAwait(false);
            throw;
        }

        return new(
            entry.Query.QueryId,
            listener.Channel.Reader.ReadAllAsync(),
            typeInfo,
            () => ReleaseQueryAsync(entry, listener, notifyServer: true));
    }

    private async ValueTask ReleaseQueryAsync(
        QueryEntry entry,
        QueryListener listener,
        bool notifyServer)
    {
        PhysicalConnection? physical = null;
        TerraScaleSyncRemoveQuery[] removed = [];
        lock (gate)
        {
            if (!entry.RemoveListener(listener))
            {
                return;
            }

            listener.Channel.Writer.TryComplete();
            if (entry.ReferenceCount == 0)
            {
                queries.Remove(entry);
                if (notifyServer && !disposed && currentConnection is { } connected)
                {
                    physical = connected;
                    removed = [new TerraScaleSyncRemoveQuery(entry.Query.QueryId)];
                }
            }
        }

        if (physical is not null)
        {
            await QueueQueryModificationAsync(
                physical,
                [],
                removed,
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task RunAsync()
    {
        var reconnectAttempt = 0;
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                ChangeConnectionState(
                    reconnectAttempt == 0
                        ? TerraScaleSyncConnectionState.Connecting
                        : forceResync
                            ? TerraScaleSyncConnectionState.Resynchronizing
                            : TerraScaleSyncConnectionState.Reconnecting,
                    lastErrorCode);
                try
                {
                    await RunConnectionAsync(lifetime.Token).ConfigureAwait(false);
                    throw new SyncReconnectException("ConnectionClosed");
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                    break;
                }
                catch (TerminalSyncException exception)
                {
                    lastErrorCode = exception.Code;
                    ChangeConnectionState(TerraScaleSyncConnectionState.Faulted, exception.Code);
                    started.TrySetException(
                        new TerraScaleSyncException(exception.Code, exception.Message));
                    FailPending(exception.Code, exception.Message);
                    return;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    lastErrorCode = exception is TerraScaleSyncException sync
                        ? sync.Code
                        : exception is SyncReconnectException reconnect
                            ? reconnect.Code
                            : "ConnectionLost";
                    ChangeConnectionState(
                        forceResync
                            ? TerraScaleSyncConnectionState.Resynchronizing
                            : TerraScaleSyncConnectionState.Reconnecting,
                        lastErrorCode);
                }

                reconnectAttempt++;
                await Task.Delay(ReconnectDelay(reconnectAttempt), lifetime.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            lock (gate)
            {
                currentConnection = null;
            }
        }
    }

    private async Task RunConnectionAsync(CancellationToken cancellationToken)
    {
        var sequence = NextConnectionSequence();
        var connected = await connect(sequence, cancellationToken)
            .ConfigureAwait(false);
        await using var physical = new PhysicalConnection(connected.Socket, sequence, cancellationToken);

        if (connected.BearerToken is { } bearerToken)
        {
            await SendDirectAsync(
                physical,
                TerraScaleSyncMessageType.Authenticate,
                new TerraScaleSyncAuthenticateMessage(bearerToken),
                cancellationToken).ConfigureAwait(false);
        }

        TerraScaleSyncModifyQuerySetMessage? queryDelta;
        PendingMutation[] mutationDelta;
        Task? send = null;
        Task? receive = null;
        Task? heartbeat = null;
        await querySetGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            QuerySnapshot querySnapshot;
            PendingMutation[] mutationSnapshot;
            TerraScaleSyncConnectMessage connectMessage;
            lock (gate)
            {
                querySnapshot = new(
                    queries.Select(static entry => entry.Query).ToArray(),
                    queries.Select(static entry => entry.Query.QueryId).ToHashSet());
                mutationSnapshot = pendingMutations.Values.ToArray();
                connectMessage = CreateConnectMessageLocked(querySnapshot.Queries);
            }

            await SendDirectAsync(
                physical,
                TerraScaleSyncMessageType.Connect,
                connectMessage,
                cancellationToken).ConfigureAwait(false);
            foreach (var mutation in mutationSnapshot)
            {
                await SendDirectAsync(
                    physical,
                    TerraScaleSyncMessageType.ExecuteMutation,
                    mutation.Payload,
                    cancellationToken).ConfigureAwait(false);
            }

            lock (gate)
            {
                requestedQuerySetVersion = clientState.Version.QuerySetVersion.Value;
                currentConnection = physical;
                queryDelta = CreateConnectionQueryDeltaLocked(querySnapshot);
                mutationDelta = pendingMutations.Values
                    .Where(candidate => !mutationSnapshot.Contains(candidate))
                    .ToArray();
            }

            if (queryDelta is not null)
            {
                await QueueAsync(
                    physical,
                    TerraScaleSyncMessageType.ModifyQuerySet,
                    queryDelta,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            lock (gate)
            {
                if (ReferenceEquals(currentConnection, physical))
                {
                    currentConnection = null;
                }
            }

            throw;
        }
        finally
        {
            querySetGate.Release();
        }

        try
        {
            send = SendLoopAsync(physical);
            foreach (var mutation in mutationDelta)
            {
                await QueueAsync(
                    physical,
                    TerraScaleSyncMessageType.ExecuteMutation,
                    mutation.Payload,
                    cancellationToken).ConfigureAwait(false);
            }

            ChangeConnectionState(TerraScaleSyncConnectionState.Connected, errorCode: null);
            started.TrySetResult();
            receive = ReceiveLoopAsync(physical);
            heartbeat = HeartbeatLoopAsync(physical);
            var completed = await Task.WhenAny(send, receive, heartbeat).ConfigureAwait(false);
            await completed.ConfigureAwait(false);
        }
        finally
        {
            physical.Cancel();
            physical.Socket.Abort();
            if (send is not null)
            {
                await IgnoreConnectionEndAsync(send).ConfigureAwait(false);
            }

            if (receive is not null)
            {
                await IgnoreConnectionEndAsync(receive).ConfigureAwait(false);
            }

            if (heartbeat is not null)
            {
                await IgnoreConnectionEndAsync(heartbeat).ConfigureAwait(false);
            }

            lock (gate)
            {
                if (ReferenceEquals(currentConnection, physical))
                {
                    currentConnection = null;
                }
            }
        }
    }

    private async Task SendLoopAsync(PhysicalConnection physical)
    {
        await foreach (var frame in physical.Outgoing.Reader.ReadAllAsync(physical.Token)
                           .ConfigureAwait(false))
        {
            Interlocked.Add(ref physical.BufferedBytes, -frame.Bytes.Length);
            await physical.Socket.SendAsync(
                frame.Bytes,
                WebSocketMessageType.Text,
                endOfMessage: true,
                physical.Token).ConfigureAwait(false);
            frame.Sent?.Invoke();
        }
    }

    private async Task ReceiveLoopAsync(PhysicalConnection physical)
    {
        ulong lastMessageSequence = 0;
        ChunkAccumulator? chunks = null;
        while (!physical.Token.IsCancellationRequested)
        {
            var bytes = await ReceiveMessageAsync(physical).ConfigureAwait(false);
            Volatile.Write(ref physical.LastInboundTimestamp, Stopwatch.GetTimestamp());
            var header = DeserializeEnvelope<JsonElement>(bytes, expectedMessageType: null);
            ValidateIncomingEnvelope(physical, header, ref lastMessageSequence);

            switch (header.MessageType)
            {
                case TerraScaleSyncMessageType.Transition:
                    {
                        var envelope = DeserializeEnvelope<TerraScaleSyncTransitionFrame>(
                            bytes,
                            TerraScaleSyncMessageType.Transition);
                        if (envelope.Payload is TerraScaleSyncTransition complete)
                        {
                            if (chunks is not null)
                            {
                                throw new SyncReconnectException("IncompleteTransitionChunks");
                            }

                            await ApplyTransitionAsync(physical, complete).ConfigureAwait(false);
                        }
                        else if (envelope.Payload is TerraScaleSyncTransitionChunk chunk)
                        {
                            chunks ??= new ChunkAccumulator(chunk);
                            var completeBytes = chunks.Add(chunk);
                            if (completeBytes is not null)
                            {
                                var transition = JsonSerializer.Deserialize(
                                        completeBytes,
                                        TerraScaleSyncJsonContext.Default.TerraScaleSyncTransitionFrame)
                                    as TerraScaleSyncTransition
                                    ?? throw new SyncReconnectException("MissingTransition");
                                chunks = null;
                                await ApplyTransitionAsync(physical, transition).ConfigureAwait(false);
                            }
                        }
                        else
                        {
                            throw new SyncReconnectException("UnsupportedTransitionFrame");
                        }

                        break;
                    }
                case TerraScaleSyncMessageType.MutationResult:
                    ApplyMutationResult(
                        DeserializeEnvelope<TerraScaleSyncMutationResultMessage>(
                            bytes,
                            TerraScaleSyncMessageType.MutationResult).Payload);
                    break;
                case TerraScaleSyncMessageType.AuthenticationError:
                    {
                        var error = DeserializeEnvelope<TerraScaleSyncAuthenticationErrorMessage>(
                            bytes,
                            TerraScaleSyncMessageType.AuthenticationError).Payload.Error;
                        if (IsValidError(error) &&
                            error.Retryable &&
                            client.CanRefreshSyncBearerToken)
                        {
                            PrepareForFullResync();
                            throw new SyncReconnectException("AuthenticationRetry");
                        }

                        throw new TerminalSyncException(
                            IsValidError(error) ? error.Code : "InvalidAuthenticationError",
                            IsValidError(error) ? error.Message : "The server returned an invalid authentication error.");
                    }
                case TerraScaleSyncMessageType.ResyncRequired:
                    HandleResyncRequired(
                        DeserializeEnvelope<TerraScaleSyncResyncRequiredMessage>(
                            bytes,
                            TerraScaleSyncMessageType.ResyncRequired).Payload);
                    throw new SyncReconnectException("ResyncRequired");
                case TerraScaleSyncMessageType.RegionRouteChanged:
                    {
                        var route = DeserializeEnvelope<TerraScaleSyncRegionRouteChangedMessage>(
                            bytes,
                            TerraScaleSyncMessageType.RegionRouteChanged).Payload;
                        ApplyRouteChanged(route);
                        if (route.ReconnectRequired)
                        {
                            throw new SyncReconnectException("RegionRouteChanged");
                        }

                        break;
                    }
                case TerraScaleSyncMessageType.Ping:
                    await HandlePingAsync(
                        physical,
                        DeserializeEnvelope<TerraScaleSyncPingMessage>(
                            bytes,
                            TerraScaleSyncMessageType.Ping).Payload).ConfigureAwait(false);
                    break;
                case TerraScaleSyncMessageType.FatalProtocolError:
                    {
                        var error = DeserializeEnvelope<TerraScaleSyncFatalProtocolErrorMessage>(
                            bytes,
                            TerraScaleSyncMessageType.FatalProtocolError).Payload.Error;
                        throw new TerminalSyncException(
                            string.IsNullOrWhiteSpace(error.Code) ? "FatalProtocolError" : error.Code,
                            string.IsNullOrWhiteSpace(error.Message)
                                ? "The server terminated the Sync protocol."
                                : error.Message);
                    }
                default:
                    throw new SyncReconnectException("UnexpectedServerMessage");
            }
        }
    }

    private async Task HeartbeatLoopAsync(PhysicalConnection physical)
    {
        while (!physical.Token.IsCancellationRequested)
        {
            await Task.Delay(options.HeartbeatInterval, physical.Token).ConfigureAwait(false);
            if (Stopwatch.GetElapsedTime(
                    Volatile.Read(ref physical.LastInboundTimestamp),
                    Stopwatch.GetTimestamp()) > options.HeartbeatTimeout)
            {
                throw new SyncReconnectException("HeartbeatTimeout");
            }

            var nonce = physical.NextPingNonce();
            physical.Pings.TryAdd(nonce, Stopwatch.GetTimestamp());
            await QueueAsync(
                physical,
                TerraScaleSyncMessageType.Ping,
                new TerraScaleSyncPingMessage(
                    new TerraScaleSyncUInt64(nonce),
                    DateTimeOffset.UtcNow),
                physical.Token).ConfigureAwait(false);
        }
    }

    private async Task HandlePingAsync(
        PhysicalConnection physical,
        TerraScaleSyncPingMessage ping)
    {
        if (physical.Pings.TryRemove(ping.Nonce.Value, out var sent))
        {
            var milliseconds = (int)Math.Min(
                60_000,
                Stopwatch.GetElapsedTime(sent, Stopwatch.GetTimestamp()).TotalMilliseconds);
            lock (gate)
            {
                lastRoundTripMilliseconds = milliseconds;
            }

            return;
        }

        await QueueAsync(
            physical,
            TerraScaleSyncMessageType.Ping,
            ping,
            physical.Token).ConfigureAwait(false);
    }

    private async Task ApplyTransitionAsync(
        PhysicalConnection physical,
        TerraScaleSyncTransition transition)
    {
        IReadOnlySet<TerraScaleSyncId> changed;
        lock (gate)
        {
            if (transition.EndVersion.DatabaseRevision < maximumObservedRevision)
            {
                throw new SyncReconnectException("RevisionRegression");
            }

            TerraScaleSyncClientState next;
            TerraScaleSyncValidationError? error;
            try
            {
                if (!TerraScaleSyncStateReducer.TryApply(
                        clientState,
                        transition,
                        out next,
                        out changed,
                        out error))
                {
                    throw new SyncReconnectException(error?.Code ?? "InvalidTransition");
                }
            }
            catch (SyncReconnectException)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                throw new TerminalSyncException(
                    "OptimisticPatchFailed",
                    $"An optimistic mutation patch failed while applying authoritative state: {exception.Message}");
            }

            clientState = next;
            maximumObservedRevision = Max(
                maximumObservedRevision,
                transition.EndVersion.DatabaseRevision);
            ingressRegion = transition.IngressRegion;
            servingRegion = transition.ServingRegion;
            databaseRegion = transition.DatabaseRegion;
            routeGeneration = transition.RouteGeneration;
            hasAppliedTransition = true;
            forceResync = false;
            NotifyQueriesLocked(changed);
        }

        var acknowledgement = new TerraScaleSyncAcknowledgeTransitionMessage(
            transition.TransitionId,
            transition.EndVersion);
        await QueueAsync(
            physical,
            TerraScaleSyncMessageType.AcknowledgeTransition,
            acknowledgement,
            physical.Token,
            () => RecordAcknowledgement(acknowledgement.EndVersion)).ConfigureAwait(false);
    }

    private void ApplyMutationResult(TerraScaleSyncMutationResultMessage result)
    {
        PendingMutation? pending;
        TerraScaleSyncMutationResult? completed = null;
        IReadOnlySet<TerraScaleSyncId> changed;
        lock (gate)
        {
            if (!pendingMutations.TryGetValue(result.ClientRequestId, out pending))
            {
                return;
            }

            try
            {
                if (result.ClientRequestId.IsEmpty ||
                    result.Result is { ValueKind: JsonValueKind.Undefined })
                {
                    throw new SyncReconnectException("InvalidMutationResult");
                }

                if (result.Succeeded)
                {
                    if (result.Error is not null || result.CommittedRevision is null)
                    {
                        throw new SyncReconnectException("InvalidMutationResult");
                    }

                    maximumObservedRevision = Max(
                        maximumObservedRevision,
                        result.CommittedRevision.Value);
                    if (pending.HasOptimisticLayer)
                    {
                        clientState = TerraScaleSyncStateReducer.MarkMutationCommitted(
                            clientState,
                            result.ClientRequestId,
                            result.CommittedRevision.Value,
                            out changed);
                    }
                    else
                    {
                        changed = FrozenSet<TerraScaleSyncId>.Empty;
                    }
                }
                else
                {
                    if (!IsValidError(result.Error) || result.CommittedRevision is not null)
                    {
                        throw new SyncReconnectException("InvalidMutationResult");
                    }

                    if (pending.HasOptimisticLayer)
                    {
                        clientState = TerraScaleSyncStateReducer.RollbackMutation(
                            clientState,
                            result.ClientRequestId,
                            out changed);
                    }
                    else
                    {
                        changed = FrozenSet<TerraScaleSyncId>.Empty;
                    }
                }

                pendingMutations.Remove(result.ClientRequestId);
            }
            catch (SyncReconnectException)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                throw new TerminalSyncException(
                    "OptimisticPatchFailed",
                    $"An optimistic mutation patch failed during reconciliation: {exception.Message}");
            }

            NotifyQueriesLocked(changed);
            completed = new(
                result.ClientRequestId,
                result.Succeeded,
                result.Result is { } value ? value.Clone() : null,
                result.Error,
                result.CommittedRevision);
        }

        pending.Completion.TrySetResult(completed);
    }

    private void HandleResyncRequired(TerraScaleSyncResyncRequiredMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.Reason) ||
            message.LastAcknowledgedVersion is { } acknowledged &&
            acknowledged.DatabaseRevision > maximumObservedRevision)
        {
            throw new SyncReconnectException("InvalidResyncRequired");
        }

        PrepareForFullResync();
    }

    private void PrepareForFullResync()
    {
        lock (gate)
        {
            var activeQueryIds = queries
                .Select(static entry => entry.Query.QueryId)
                .ToHashSet();
            clientState = TerraScaleSyncStateReducer.PrepareForResync(
                clientState,
                activeQueryIds);
            var retainedOptimisticMutations = clientState.OptimisticLayers
                .Select(static layer => layer.MutationId)
                .ToHashSet();
            foreach (var (mutationId, pending) in pendingMutations)
            {
                pending.HasOptimisticLayer = retainedOptimisticMutations.Contains(mutationId);
            }

            lastAcknowledgedVersion = null;
            hasAppliedTransition = false;
            forceResync = true;
        }
    }

    private void ApplyRouteChanged(TerraScaleSyncRegionRouteChangedMessage route)
    {
        if (!IsRegion(route.IngressRegion) ||
            !IsRegion(route.ServingRegion) ||
            !IsRegion(route.DatabaseRegion))
        {
            throw new SyncReconnectException("InvalidRegionRoute");
        }

        lock (gate)
        {
            if (route.RouteGeneration < routeGeneration)
            {
                throw new SyncReconnectException("RouteGenerationRegression");
            }

            ingressRegion = route.IngressRegion;
            servingRegion = route.ServingRegion;
            databaseRegion = route.DatabaseRegion;
            routeGeneration = route.RouteGeneration;
        }
    }

    private void RecordAcknowledgement(TerraScaleSyncStateVersion version)
    {
        lock (gate)
        {
            if (lastAcknowledgedVersion is null ||
                version.IsAtLeast(lastAcknowledgedVersion.Value))
            {
                lastAcknowledgedVersion = version;
            }
        }
    }

    private async ValueTask<ConnectedSyncSocket> ConnectNativeAsync(
        ulong _,
        CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        try
        {
            socket.Options.AddSubProtocol("terrascale-sync-v1");
            socket.Options.KeepAliveInterval = Timeout.InfiniteTimeSpan;
            var bearerToken = await client.ConfigureSyncWebSocketAsync(
                socket.Options,
                options.DatabaseId,
                options.EnvironmentId,
                cancellationToken).ConfigureAwait(false);
            await socket.ConnectAsync(client.SyncEndpoint, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(
                    socket.SubProtocol,
                    "terrascale-sync-v1",
                    StringComparison.Ordinal))
            {
                throw new TerraScaleSyncException(
                    "UnsupportedProtocol",
                    "The server did not negotiate terrascale-sync-v1.");
            }

            return new(socket, bearerToken);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private TerraScaleSyncConnectMessage CreateConnectMessageLocked(
        IReadOnlyList<TerraScaleSyncAddQuery> activeQueries) =>
        new(
            options.SessionId,
            options.DatabaseId,
            options.EnvironmentId,
            hasAppliedTransition && !forceResync ? clientState.Version : null,
            forceResync ? null : lastAcknowledgedVersion,
            maximumObservedRevision,
            activeQueries,
            pendingMutations.Keys.ToArray(),
            ingressRegion,
            servingRegion,
            databaseRegion);

    private TerraScaleSyncModifyQuerySetMessage? CreateConnectionQueryDeltaLocked(
        QuerySnapshot snapshot)
    {
        var current = queries.Select(static entry => entry.Query).ToArray();
        var currentIds = current.Select(static query => query.QueryId).ToHashSet();
        var added = current.Where(query => !snapshot.QueryIds.Contains(query.QueryId)).ToArray();
        var removed = snapshot.QueryIds
            .Where(queryId => !currentIds.Contains(queryId))
            .Select(static queryId => new TerraScaleSyncRemoveQuery(queryId))
            .ToArray();
        return added.Length == 0 && removed.Length == 0
            ? null
            : CreateQueryModificationLocked(added, removed);
    }

    private TerraScaleSyncModifyQuerySetMessage CreateQueryModificationLocked(
        IReadOnlyList<TerraScaleSyncAddQuery> added,
        IReadOnlyList<TerraScaleSyncRemoveQuery> removed)
    {
        var baseVersion = requestedQuerySetVersion;
        requestedQuerySetVersion = checked(baseVersion + 1);
        return new(
            new TerraScaleSyncUInt64(baseVersion),
            new TerraScaleSyncUInt64(requestedQuerySetVersion),
            added,
            removed);
    }

    private QueryEntry AddQueryLocked(TerraScaleSyncAddQuery query)
    {
        if (queries.Count >= MaxActiveQueries)
        {
            throw new InvalidOperationException(
                $"A Sync session cannot register more than {MaxActiveQueries} active queries.");
        }

        var entry = new QueryEntry(query with { QueryId = TerraScaleSyncIdGenerator.Create() });
        queries.Add(entry);
        return entry;
    }

    private static TerraScaleSyncAddQuery ValidateQuery(TerraScaleSyncQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(query.Query) ||
            Encoding.UTF8.GetByteCount(query.Query) > 256 * 1024)
        {
            throw new ArgumentException(
                "Sync query text is required and must not exceed 256 KiB.",
                nameof(query));
        }

        if (query.Arguments.ValueKind == JsonValueKind.Undefined ||
            query.Journal is { ValueKind: JsonValueKind.Undefined })
        {
            throw new ArgumentException(
                "Sync query arguments and journal must contain defined JSON values.",
                nameof(query));
        }

        return new(
            default,
            query.Query,
            query.Arguments.Clone(),
            query.SchemaVersion,
            TerraScaleSyncConsistencyMode.AuthoritativeSnapshot,
            query.Journal is { } journal ? journal.Clone() : null);
    }

    private static TerraScaleSyncExecuteMutationMessage ValidateMutation(
        TerraScaleSyncMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        if (string.IsNullOrWhiteSpace(mutation.IdempotencyKey) ||
            Encoding.UTF8.GetByteCount(mutation.IdempotencyKey) > 256)
        {
            throw new ArgumentException(
                "Mutation IdempotencyKey is required and must not exceed 256 bytes.",
                nameof(mutation));
        }

        if (string.IsNullOrWhiteSpace(mutation.Mutation) ||
            Encoding.UTF8.GetByteCount(mutation.Mutation) > 256 * 1024)
        {
            throw new ArgumentException(
                "Mutation text is required and must not exceed 256 KiB.",
                nameof(mutation));
        }

        if (mutation.Arguments.ValueKind == JsonValueKind.Undefined ||
            mutation.Journal is { ValueKind: JsonValueKind.Undefined })
        {
            throw new ArgumentException(
                "Mutation arguments and journal must contain defined JSON values.",
                nameof(mutation));
        }

        return new(
            TerraScaleSyncIdGenerator.Create(),
            mutation.IdempotencyKey.Trim(),
            mutation.Mutation,
            mutation.Arguments.Clone(),
            mutation.Journal is { } journal ? journal.Clone() : null);
    }

    private static bool SameQuery(
        TerraScaleSyncAddQuery left,
        TerraScaleSyncAddQuery right) =>
        string.Equals(left.Query, right.Query, StringComparison.Ordinal) &&
        left.SchemaVersion == right.SchemaVersion &&
        left.ConsistencyMode == right.ConsistencyMode &&
        JsonElement.DeepEquals(left.Arguments, right.Arguments) &&
        NullableJsonEquals(left.Journal, right.Journal);

    private static bool NullableJsonEquals(JsonElement? left, JsonElement? right) =>
        left is null && right is null ||
        left is { } leftValue && right is { } rightValue &&
        JsonElement.DeepEquals(leftValue, rightValue);

    private async ValueTask QueueQueryModificationAsync(
        PhysicalConnection physical,
        IReadOnlyList<TerraScaleSyncAddQuery> added,
        IReadOnlyList<TerraScaleSyncRemoveQuery> removed,
        CancellationToken cancellationToken)
    {
        await querySetGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        TerraScaleSyncModifyQuerySetMessage? modification = null;
        try
        {
            lock (gate)
            {
                if (!ReferenceEquals(currentConnection, physical))
                {
                    return;
                }

                modification = CreateQueryModificationLocked(added, removed);
            }

            await QueueAsync(
                physical,
                TerraScaleSyncMessageType.ModifyQuerySet,
                modification,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            physical.Token.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            RewindQuerySetVersion(physical, modification);
        }
        catch (ChannelClosedException)
        {
            RewindQuerySetVersion(physical, modification);
        }
        catch
        {
            RewindQuerySetVersion(physical, modification);
            throw;
        }
        finally
        {
            querySetGate.Release();
        }
    }

    private void RewindQuerySetVersion(
        PhysicalConnection physical,
        TerraScaleSyncModifyQuerySetMessage? modification)
    {
        if (modification is null)
        {
            return;
        }

        lock (gate)
        {
            if (ReferenceEquals(currentConnection, physical) &&
                requestedQuerySetVersion == modification.NewQuerySetVersion.Value)
            {
                requestedQuerySetVersion = modification.BaseQuerySetVersion.Value;
            }
        }
    }

    private async ValueTask<bool> TryQueueAsync<TPayload>(
        PhysicalConnection physical,
        TerraScaleSyncMessageType messageType,
        TPayload payload,
        CancellationToken cancellationToken)
    {
        try
        {
            await QueueAsync(physical, messageType, payload, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (
            physical.Token.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (ChannelClosedException)
        {
            return false;
        }
    }

    private static async ValueTask QueueAsync<TPayload>(
        PhysicalConnection physical,
        TerraScaleSyncMessageType messageType,
        TPayload payload,
        CancellationToken cancellationToken,
        Action? sent = null)
    {
        using var timeout = new CancellationTokenSource(OutgoingQueueTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            physical.Token,
            cancellationToken,
            timeout.Token);
        await physical.OutboundGate.WaitAsync(linked.Token).ConfigureAwait(false);
        byte[]? bytes = null;
        ulong messageSequence = 0;
        var buffered = false;
        try
        {
            messageSequence = physical.NextMessageSequence();
            bytes = SerializeEnvelope(
                physical,
                messageSequence,
                messageType,
                payload);
            var totalBuffered = Interlocked.Add(ref physical.BufferedBytes, bytes.Length);
            buffered = true;
            if (totalBuffered > MaxOutgoingBufferedBytes)
            {
                throw new TerraScaleSyncException(
                    "SlowClient",
                    "The Sync outgoing buffer exceeded 2 MiB.");
            }

            await physical.Outgoing.Writer.WriteAsync(
                new OutboundFrame(bytes, sent),
                linked.Token).ConfigureAwait(false);
            buffered = false;
        }
        catch (OperationCanceledException) when (
            timeout.IsCancellationRequested &&
            !physical.Token.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            if (buffered)
            {
                Interlocked.Add(ref physical.BufferedBytes, -bytes!.Length);
            }

            physical.RewindMessageSequence(messageSequence);
            physical.Abort();
            throw new TerraScaleSyncException(
                "SlowClient",
                "The Sync outgoing queue remained full for five seconds.");
        }
        catch (Exception exception)
        {
            if (buffered)
            {
                Interlocked.Add(ref physical.BufferedBytes, -bytes!.Length);
            }

            physical.RewindMessageSequence(messageSequence);
            if (exception is not TerraScaleSyncException { Code: "MessageTooLarge" })
            {
                physical.Abort();
            }

            throw;
        }
        finally
        {
            physical.OutboundGate.Release();
        }
    }

    private static async ValueTask SendDirectAsync<TPayload>(
        PhysicalConnection physical,
        TerraScaleSyncMessageType messageType,
        TPayload payload,
        CancellationToken cancellationToken)
    {
        var messageSequence = physical.NextMessageSequence();
        byte[] bytes;
        try
        {
            bytes = SerializeEnvelope(
                physical,
                messageSequence,
                messageType,
                payload);
        }
        catch
        {
            physical.RewindMessageSequence(messageSequence);
            throw;
        }

        await physical.Socket.SendAsync(
            bytes,
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken).ConfigureAwait(false);
    }

    private static byte[] SerializeEnvelope<TPayload>(
        PhysicalConnection physical,
        ulong messageSequence,
        TerraScaleSyncMessageType messageType,
        TPayload payload)
    {
        var envelope = new TerraScaleSyncEnvelope<TPayload>(
            TerraScaleSyncEnvelope<TPayload>.ProtocolVersion,
            messageType,
            new TerraScaleSyncUInt64(physical.Sequence),
            new TerraScaleSyncUInt64(messageSequence),
            payload);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            envelope,
            typeof(TerraScaleSyncEnvelope<TPayload>),
            TerraScaleSyncJsonContext.Default);
        return bytes.Length <= MaxClientMessageBytes
            ? bytes
            : throw new TerraScaleSyncException(
                "MessageTooLarge",
                "A Sync client message cannot exceed 1 MiB.");
    }

    private static TerraScaleSyncEnvelope<TPayload> DeserializeEnvelope<TPayload>(
        ReadOnlySpan<byte> bytes,
        TerraScaleSyncMessageType? expectedMessageType)
    {
        TerraScaleSyncEnvelope<TPayload>? envelope;
        try
        {
            envelope = (TerraScaleSyncEnvelope<TPayload>?)JsonSerializer.Deserialize(
                bytes,
                typeof(TerraScaleSyncEnvelope<TPayload>),
                TerraScaleSyncJsonContext.Default);
        }
        catch (JsonException exception)
        {
            throw new SyncReconnectException("InvalidJson", exception);
        }

        if (envelope is null ||
            !string.Equals(
                envelope.Protocol,
                TerraScaleSyncEnvelope<TPayload>.ProtocolVersion,
                StringComparison.Ordinal) ||
            envelope.ConnectionSequence == TerraScaleSyncUInt64.Zero ||
            envelope.MessageSequence == TerraScaleSyncUInt64.Zero ||
            envelope.Payload is null ||
            expectedMessageType is { } expected && envelope.MessageType != expected)
        {
            throw new SyncReconnectException("InvalidEnvelope");
        }

        return envelope;
    }

    private static void ValidateIncomingEnvelope(
        PhysicalConnection physical,
        TerraScaleSyncEnvelope<JsonElement> envelope,
        ref ulong lastMessageSequence)
    {
        if (envelope.ConnectionSequence.Value != physical.Sequence ||
            lastMessageSequence == ulong.MaxValue ||
            envelope.MessageSequence.Value != lastMessageSequence + 1)
        {
            throw new SyncReconnectException("OutOfOrderMessage");
        }

        lastMessageSequence = envelope.MessageSequence.Value;
    }

    private static async ValueTask<byte[]> ReceiveMessageAsync(PhysicalConnection physical)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            var writer = new ArrayBufferWriter<byte>();
            while (true)
            {
                var result = await physical.Socket.ReceiveAsync(
                    buffer.AsMemory(),
                    physical.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new SyncReconnectException("ServerClosedConnection");
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    throw new SyncReconnectException("UnsupportedMessageType");
                }

                if (writer.WrittenCount + result.Count > MaxTransitionBytes)
                {
                    throw new SyncReconnectException("MessageTooLarge");
                }

                writer.Write(buffer.AsSpan(0, result.Count));
                if (result.EndOfMessage)
                {
                    return writer.WrittenSpan.ToArray();
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private ulong NextConnectionSequence()
    {
        lock (gate)
        {
            if (connectionSequence == ulong.MaxValue)
            {
                throw new TerminalSyncException(
                    "ConnectionSequenceExhausted",
                    "The Sync connection sequence was exhausted.");
            }

            return ++connectionSequence;
        }
    }

    private TimeSpan ReconnectDelay(int attempt)
    {
        var multiplier = 1L << Math.Min(attempt - 1, 20);
        var milliseconds = Math.Min(
            options.ReconnectDelay.TotalMilliseconds * multiplier,
            options.MaximumReconnectDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private void NotifyQueriesLocked(IReadOnlySet<TerraScaleSyncId> queryIds)
    {
        foreach (var queryId in queryIds)
        {
            var entry = queries.FirstOrDefault(candidate => candidate.Query.QueryId == queryId);
            if (entry is null ||
                !clientState.Queries.TryGetValue(queryId, out var snapshot))
            {
                if (entry is not null)
                {
                    entry.Notify(
                        new TerraScaleSyncQuerySnapshot(
                            queryId,
                            queryFingerprint: null,
                            result: null,
                            resultHash: null,
                            executionRevision: null,
                            clientState.Version.DatabaseRevision,
                            journal: null,
                            error: null,
                            optimistic: false));
                }

                continue;
            }

            entry.Notify(snapshot);
        }
    }

    private void ChangeConnectionState(
        TerraScaleSyncConnectionState next,
        string? errorCode)
    {
        TerraScaleSyncConnectionDiagnostics diagnostics;
        lock (gate)
        {
            if (disposed && next != TerraScaleSyncConnectionState.Disposed)
            {
                return;
            }

            connectionState = next;
            lastErrorCode = errorCode;
            diagnostics = CreateDiagnosticsLocked();
        }

        RaiseConnectionStateChanged(diagnostics);
    }

    private TerraScaleSyncConnectionDiagnostics CreateDiagnosticsLocked() =>
        new(
            connectionState,
            new TerraScaleSyncUInt64(connectionSequence),
            clientState.Version,
            lastAcknowledgedVersion,
            maximumObservedRevision,
            ingressRegion,
            servingRegion,
            databaseRegion,
            routeGeneration,
            queries.Count,
            pendingMutations.Count,
            lastRoundTripMilliseconds,
            lastErrorCode);

    private void RaiseConnectionStateChanged(TerraScaleSyncConnectionDiagnostics diagnostics)
    {
        var handler = ConnectionStateChanged;
        if (handler is null)
        {
            return;
        }

        var args = new TerraScaleSyncConnectionStateChangedEventArgs(diagnostics);
        foreach (EventHandler<TerraScaleSyncConnectionStateChangedEventArgs> callback
                 in handler.GetInvocationList())
        {
            try
            {
                callback(this, args);
            }
            catch
            {
                // Application event handlers cannot terminate the transport.
            }
        }
    }

    private void FailPending(string code, string message)
    {
        PendingMutation[] mutations;
        lock (gate)
        {
            mutations = pendingMutations.Values.ToArray();
            pendingMutations.Clear();
        }

        foreach (var mutation in mutations)
        {
            mutation.Completion.TrySetException(new TerraScaleSyncException(code, message));
        }
    }

    private void ThrowIfNotRunningLocked()
    {
        ThrowIfDisposedLocked();
        if (!startCalled)
        {
            throw new InvalidOperationException(
                "Call StartAsync before using the Sync session.");
        }

        if (connectionState == TerraScaleSyncConnectionState.Faulted)
        {
            throw new InvalidOperationException("The Sync session has faulted.");
        }
    }

    private void ThrowIfDisposedLocked() =>
        ObjectDisposedException.ThrowIf(disposed, this);

    private static TerraScaleSyncDatabaseRevision Max(
        TerraScaleSyncDatabaseRevision left,
        TerraScaleSyncDatabaseRevision right) =>
        left >= right ? left : right;

    private static bool IsValidError(TerraScaleSyncError? error) =>
        error is not null &&
        !string.IsNullOrWhiteSpace(error.Code) &&
        Encoding.UTF8.GetByteCount(error.Code) <= 128 &&
        error.Code.All(static character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-') &&
        !string.IsNullOrWhiteSpace(error.Message) &&
        Encoding.UTF8.GetByteCount(error.Message) <= 4 * 1024;

    private static bool IsRegion(string? region) =>
        region is { Length: >= 1 and <= 32 } &&
        region[0] != '-' &&
        region[^1] != '-' &&
        region.All(static character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private static async Task IgnoreConnectionEndAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or
            ChannelClosedException or
            ObjectDisposedException or
            WebSocketException or
            SyncReconnectException or
            TerraScaleSyncException)
        {
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Task? running;
        PhysicalConnection? physical;
        QueryListener[] listeners;
        PendingMutation[] mutations;
        TerraScaleSyncConnectionDiagnostics diagnostics;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            connectionState = TerraScaleSyncConnectionState.Disposed;
            physical = currentConnection;
            currentConnection = null;
            listeners = queries.SelectMany(static entry => entry.Listeners).ToArray();
            queries.Clear();
            mutations = pendingMutations.Values.ToArray();
            pendingMutations.Clear();
            running = runTask;
            diagnostics = CreateDiagnosticsLocked();
        }

        lifetime.Cancel();
        physical?.Cancel();
        physical?.Socket.Abort();
        foreach (var listener in listeners)
        {
            listener.Channel.Writer.TryComplete();
        }

        foreach (var mutation in mutations)
        {
            mutation.Completion.TrySetCanceled();
        }

        started.TrySetCanceled();
        if (running is not null)
        {
            await IgnoreConnectionEndAsync(running).ConfigureAwait(false);
        }

        lifetime.Dispose();
        RaiseConnectionStateChanged(diagnostics);
    }

    internal sealed record ConnectedSyncSocket(WebSocket Socket, string? BearerToken);

    private sealed class QueryEntry(TerraScaleSyncAddQuery query)
    {
        private readonly List<QueryListener> listeners = [];

        public TerraScaleSyncAddQuery Query { get; } = query;

        public int ReferenceCount => listeners.Count;

        public IReadOnlyList<QueryListener> Listeners => listeners;

        public QueryListener AddListener()
        {
            var listener = new QueryListener();
            listeners.Add(listener);
            return listener;
        }

        public bool RemoveListener(QueryListener listener) => listeners.Remove(listener);

        public void Notify(TerraScaleSyncQuerySnapshot snapshot)
        {
            foreach (var listener in listeners)
            {
                listener.Channel.Writer.TryWrite(snapshot);
            }
        }
    }

    private sealed class QueryListener
    {
        public Channel<TerraScaleSyncQuerySnapshot> Channel { get; } =
            System.Threading.Channels.Channel.CreateBounded<TerraScaleSyncQuerySnapshot>(
                new BoundedChannelOptions(1)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false
                });
    }

    private sealed class PendingMutation(
        TerraScaleSyncExecuteMutationMessage payload,
        bool hasOptimisticLayer)
    {
        public TerraScaleSyncExecuteMutationMessage Payload { get; } = payload;

        public bool HasOptimisticLayer { get; set; } = hasOptimisticLayer;

        public TaskCompletionSource<TerraScaleSyncMutationResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed class PhysicalConnection : IAsyncDisposable
    {
        private readonly CancellationTokenSource cancellation;
        private readonly CancellationToken token;
        private long messageSequence;
        private long pingNonce;

        public PhysicalConnection(
            WebSocket socket,
            ulong sequence,
            CancellationToken cancellationToken)
        {
            Socket = socket;
            Sequence = sequence;
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            token = cancellation.Token;
            LastInboundTimestamp = Stopwatch.GetTimestamp();
            Outgoing = Channel.CreateBounded<OutboundFrame>(
                new BoundedChannelOptions(OutgoingMessageCapacity)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false
                });
        }

        public WebSocket Socket { get; }

        public ulong Sequence { get; }

        public Channel<OutboundFrame> Outgoing { get; }

        public SemaphoreSlim OutboundGate { get; } = new(1, 1);

        public CancellationToken Token => token;

        public ConcurrentDictionary<ulong, long> Pings { get; } = [];

        public long BufferedBytes;

        public long LastInboundTimestamp;

        public ulong NextMessageSequence()
        {
            var next = Interlocked.Increment(ref messageSequence);
            return next > 0
                ? checked((ulong)next)
                : throw new TerraScaleSyncException(
                    "MessageSequenceExhausted",
                    "The Sync message sequence was exhausted.");
        }

        public void RewindMessageSequence(ulong sequence)
        {
            if (sequence > 0 && messageSequence == checked((long)sequence))
            {
                messageSequence--;
            }
        }

        public ulong NextPingNonce()
        {
            var next = Interlocked.Increment(ref pingNonce);
            return next > 0
                ? checked((ulong)next)
                : throw new TerraScaleSyncException(
                    "PingSequenceExhausted",
                    "The Sync ping sequence was exhausted.");
        }

        public void Cancel()
        {
            Outgoing.Writer.TryComplete();
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Abort()
        {
            Cancel();
            Socket.Abort();
        }

        public ValueTask DisposeAsync()
        {
            Cancel();
            cancellation.Dispose();
            Socket.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    internal sealed record OutboundFrame(byte[] Bytes, Action? Sent);

    private sealed class ChunkAccumulator
    {
        private readonly TerraScaleSyncId transitionId;
        private readonly int chunkCount;
        private readonly int uncompressedLength;
        private readonly string sha256;
        private readonly ArrayBufferWriter<byte> data;
        private int nextChunkIndex;

        public ChunkAccumulator(TerraScaleSyncTransitionChunk first)
        {
            Validate(first);
            transitionId = first.TransitionId;
            chunkCount = first.ChunkCount;
            uncompressedLength = first.UncompressedLength;
            sha256 = first.Sha256;
            data = new ArrayBufferWriter<byte>(Math.Min(uncompressedLength, MaxTransitionChunkBytes));
        }

        public byte[]? Add(TerraScaleSyncTransitionChunk chunk)
        {
            Validate(chunk);
            if (chunk.TransitionId != transitionId ||
                chunk.ChunkCount != chunkCount ||
                chunk.UncompressedLength != uncompressedLength ||
                !string.Equals(chunk.Sha256, sha256, StringComparison.Ordinal) ||
                chunk.ChunkIndex != nextChunkIndex ||
                data.WrittenCount + chunk.Data.Length > uncompressedLength)
            {
                throw new SyncReconnectException("InvalidTransitionChunk");
            }

            data.Write(chunk.Data);
            nextChunkIndex++;
            if (nextChunkIndex != chunkCount)
            {
                return null;
            }

            if (data.WrittenCount != uncompressedLength ||
                !string.Equals(
                    Convert.ToHexStringLower(SHA256.HashData(data.WrittenSpan)),
                    sha256,
                    StringComparison.Ordinal))
            {
                throw new SyncReconnectException("InvalidTransitionHash");
            }

            return data.WrittenSpan.ToArray();
        }

        private static void Validate(TerraScaleSyncTransitionChunk chunk)
        {
            var expectedChunkCount =
                (chunk.UncompressedLength + MaxTransitionChunkBytes - 1) /
                MaxTransitionChunkBytes;
            var expectedChunkLength = chunk.ChunkIndex == chunk.ChunkCount - 1
                ? chunk.UncompressedLength - chunk.ChunkIndex * MaxTransitionChunkBytes
                : MaxTransitionChunkBytes;
            if (chunk.TransitionId.IsEmpty ||
                chunk.ChunkIndex < 0 ||
                chunk.ChunkCount is <= 0 or > MaxTransitionChunks ||
                chunk.ChunkCount != expectedChunkCount ||
                chunk.ChunkIndex >= chunk.ChunkCount ||
                chunk.UncompressedLength is <= 0 or > MaxTransitionBytes ||
                chunk.Data is null ||
                chunk.Data.Length != expectedChunkLength ||
                chunk.Sha256 is not { Length: 64 } ||
                chunk.Sha256.Any(static character =>
                    character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            {
                throw new SyncReconnectException("InvalidTransitionChunk");
            }
        }
    }

    private sealed record QuerySnapshot(
        TerraScaleSyncAddQuery[] Queries,
        HashSet<TerraScaleSyncId> QueryIds);

    private sealed class SyncReconnectException : Exception
    {
        public SyncReconnectException(string code, Exception? inner = null)
            : base(code, inner) => Code = code;

        public string Code { get; }
    }

    private sealed class TerminalSyncException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }
}
