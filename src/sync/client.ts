import type { JsonValue } from "../models.js";
import {
  compareDatabaseRevision,
  parseCompleteTransition,
  parseSyncServerEnvelope,
  parseSyncUInt64,
  syncProtocolVersion,
  type CompleteTransition,
  type DatabaseRevision,
  type SyncError,
  type SyncPing,
  type SyncStateVersion,
  type SyncUInt64,
  type TransitionChunk,
} from "./protocol.js";
import {
  canonicalQueryKey,
  TerraScaleSyncStore,
  type OptimisticPatch,
  type SyncQueryHandle,
  type SyncQueryIdentity,
} from "./store.js";

export const syncWebSocketProtocol = "terrascale-sync-v1" as const;

const maxClientMessageBytes = 1024 * 1024;
const maxTransitionChunkBytes = 256 * 1024;
const maxTransitionBytes = 8 * 1024 * 1024;
const maxIncomingBufferedBytes = 12 * 1024 * 1024;
const maxOutgoingBufferedBytes = 2 * 1024 * 1024;
const maxActiveQueries = 100;
const textEncoder = new TextEncoder();
const textDecoder = new TextDecoder("utf-8", { fatal: true });
const zeroRevision: DatabaseRevision = Object.freeze({
  RevisionEpoch: "0",
  CommitSequence: "0",
});

export type SyncWebSocket = {
  readonly readyState: number;
  readonly bufferedAmount: number;
  readonly protocol: string;
  binaryType: BinaryType;
  onopen: ((event: Event) => void) | null;
  onmessage: ((event: MessageEvent<unknown>) => void) | null;
  onerror: ((event: Event) => void) | null;
  onclose: ((event: CloseEvent) => void) | null;
  send(data: string): void;
  close(code?: number, reason?: string): void;
};

export type SyncWebSocketFactory = (
  url: string,
  protocols: readonly string[],
) => SyncWebSocket;

export type TerraScaleSyncSessionOptions = {
  readonly Url: string;
  readonly DatabaseId: string;
  readonly EnvironmentId: string;
  readonly Tenant: string;
  readonly AuthorizationScope: string;
  readonly SessionId?: string | undefined;
  readonly TicketProvider?: (() => Promise<string>) | undefined;
  readonly BearerTokenProvider?: (() => Promise<string>) | undefined;
  readonly WebSocketFactory?: SyncWebSocketFactory | undefined;
  readonly ReconnectDelayMilliseconds?: ((attempt: number) => number) | undefined;
  readonly HeartbeatIntervalMilliseconds?: number | undefined;
  readonly HeartbeatTimeoutMilliseconds?: number | undefined;
};

export type SyncLiveQuery = {
  readonly Query: string;
  readonly Arguments: JsonValue;
  readonly SchemaVersion: SyncUInt64;
  readonly ConsistencyMode?: "AuthoritativeSnapshot" | undefined;
  readonly Journal?: JsonValue | undefined;
};

export type SyncMutation = {
  readonly ClientRequestId?: string | undefined;
  readonly IdempotencyKey: string;
  readonly Mutation: string;
  readonly Arguments: JsonValue;
  readonly Journal?: JsonValue | undefined;
  readonly OptimisticPatches?: readonly OptimisticPatch[] | undefined;
};

export type SyncConnectionStatus =
  | "disconnected"
  | "connecting"
  | "connected"
  | "reconnecting"
  | "resync-required"
  | "authentication-error"
  | "disposed";

export type SyncConnectionSnapshot = {
  readonly Status: SyncConnectionStatus;
  readonly SessionId: string;
  readonly ConnectionSequence: SyncUInt64;
  readonly ReconnectAttempt: number;
  readonly LastAppliedVersion: SyncStateVersion;
  readonly LastAcknowledgedVersion?: SyncStateVersion | undefined;
  readonly MaximumObservedRevision: DatabaseRevision;
  readonly IngressRegion?: string | undefined;
  readonly ServingRegion?: string | undefined;
  readonly DatabaseRegion?: string | undefined;
  readonly RouteGeneration?: SyncUInt64 | undefined;
  readonly LastError?: string | undefined;
};

type ActiveQuery = {
  readonly Key: string;
  readonly QueryId: string;
  readonly Identity: SyncQueryIdentity;
  readonly Wire: {
    readonly QueryId: string;
    readonly Query: string;
    readonly Arguments: JsonValue;
    readonly SchemaVersion: SyncUInt64;
    readonly ConsistencyMode: "AuthoritativeSnapshot";
    readonly Journal?: JsonValue | undefined;
  };
  References: number;
};

type PendingMutation = {
  readonly Request: {
    readonly ClientRequestId: string;
    readonly IdempotencyKey: string;
    readonly Mutation: string;
    readonly Arguments: JsonValue;
    readonly Journal?: JsonValue | undefined;
  };
  readonly Resolve: (value: JsonValue | undefined) => void;
  readonly Reject: (reason: unknown) => void;
  Optimistic: boolean;
  CommittedRevision?: DatabaseRevision | undefined;
};

type ChunkAssembly = {
  readonly TransitionId: string;
  readonly ChunkCount: number;
  readonly UncompressedLength: number;
  readonly Sha256: string;
  readonly Chunks: Array<Uint8Array | undefined>;
  ReceivedChunks: number;
  Received: number;
};

export class TerraScaleSyncSession {
  readonly store = new TerraScaleSyncStore();
  readonly #options: TerraScaleSyncSessionOptions;
  readonly #sessionId: string;
  readonly #webSocketFactory: SyncWebSocketFactory;
  readonly #connectionListeners = new Set<() => void>();
  readonly #activeByKey = new Map<string, ActiveQuery>();
  readonly #activeById = new Map<string, ActiveQuery>();
  readonly #serverQueryIds = new Set<string>();
  readonly #pendingMutations = new Map<string, PendingMutation>();
  #socket: SyncWebSocket | undefined;
  #connectionSequence = 0n;
  #outgoingMessageSequence = 0n;
  #incomingMessageSequence = 0n;
  #maximumObservedRevision: DatabaseRevision = zeroRevision;
  #lastAcknowledgedVersion: SyncStateVersion | undefined;
  #diagnostics: SyncConnectionSnapshot;
  #reconnectAttempt = 0;
  #reconnectTimer: ReturnType<typeof setTimeout> | undefined;
  #opening: Promise<void> | undefined;
  #openAttempt = 0;
  #heartbeatTimer: ReturnType<typeof setInterval> | undefined;
  #lastReceiveAt = 0;
  #lastPingNonce = 0n;
  #lastPingSentAt = 0;
  #incomingBufferedBytes = 0;
  #receiveChain: Promise<void> = Promise.resolve();
  #chunkAssembly: ChunkAssembly | undefined;
  #querySetTarget: ReadonlySet<string> | undefined;
  #resyncRequired = false;
  #resumeReset = false;
  #reconnectEnabled = false;
  #disposed = false;

  public constructor(options: TerraScaleSyncSessionOptions) {
    validateOptions(options);
    this.#options = options;
    this.#sessionId = options.SessionId ?? createUlid();
    validateUlid(this.#sessionId, "SessionId");
    this.#webSocketFactory = options.WebSocketFactory ?? defaultWebSocketFactory;
    this.#diagnostics = Object.freeze({
      Status: "disconnected",
      SessionId: this.#sessionId,
      ConnectionSequence: "0",
      ReconnectAttempt: 0,
      LastAppliedVersion: this.store.getSnapshot().Version,
      MaximumObservedRevision: zeroRevision,
    });
  }

  public subscribeConnection(listener: () => void): () => void {
    this.#connectionListeners.add(listener);
    return () => this.#connectionListeners.delete(listener);
  }

  public getConnectionSnapshot(): SyncConnectionSnapshot {
    return this.#diagnostics;
  }

  public getServerConnectionSnapshot(): SyncConnectionSnapshot {
    return this.#diagnostics;
  }

  public async connect(signal?: AbortSignal): Promise<void> {
    this.#throwIfDisposed();
    signal?.throwIfAborted();
    this.#reconnectEnabled = true;
    if (this.#socket?.readyState === 1) {
      return;
    }
    if (this.#opening !== undefined) {
      return this.#opening;
    }
    const opening = this.#open(false, signal);
    this.#opening = opening;
    try {
      await opening;
    } catch (error) {
      this.#scheduleReconnect();
      throw error;
    } finally {
      if (this.#opening === opening) {
        this.#opening = undefined;
      }
    }
  }

  public disconnect(): void {
    this.#reconnectEnabled = false;
    this.#openAttempt++;
    this.#clearReconnectTimer();
    this.#stopHeartbeat();
    this.#reconnectAttempt = 0;
    const socket = this.#socket;
    socket?.close(1000, "client disconnect");
    this.#setDiagnostics({ Status: "disconnected", ReconnectAttempt: 0 });
  }

  public subscribeQuery(query: SyncLiveQuery): SyncQueryHandle {
    this.#throwIfDisposed();
    const ownedArguments = ownJson(query.Arguments, "Arguments");
    const ownedJournal = query.Journal === undefined
      ? undefined
      : ownJson(query.Journal, "Journal");
    validateText(query.Query, 256 * 1024, "Query");
    validateUInt64(query.SchemaVersion, "SchemaVersion");
    if (query.ConsistencyMode !== undefined
      && query.ConsistencyMode !== "AuthoritativeSnapshot") {
      throw new TypeError("Sync v1 requires AuthoritativeSnapshot consistency.");
    }
    const identity: SyncQueryIdentity = Object.freeze({
      Database: this.#options.DatabaseId,
      Tenant: this.#options.Tenant,
      AuthorizationScope: this.#options.AuthorizationScope,
      Query: query.Query,
      Arguments: ownedArguments,
      SchemaVersion: query.SchemaVersion,
      ConsistencyMode: "AuthoritativeSnapshot",
    });
    const key = canonicalQueryKey(identity);
    let active = this.#activeByKey.get(key);
    if (active === undefined) {
      if (this.#activeByKey.size >= maxActiveQueries) {
        throw new RangeError("A Sync session cannot have more than 100 active queries.");
      }
      const queryId = createUlid();
      active = {
        Key: key,
        QueryId: queryId,
        Identity: identity,
        Wire: Object.freeze({
          QueryId: queryId,
          Query: query.Query,
          Arguments: ownedArguments,
          SchemaVersion: query.SchemaVersion,
          ConsistencyMode: "AuthoritativeSnapshot",
          ...(ownedJournal === undefined ? {} : { Journal: ownedJournal }),
        }),
        References: 0,
      };
      this.#activeByKey.set(key, active);
      this.#activeById.set(queryId, active);
      try {
        validateSerializedBytes(
          [...this.#activeById.values()].map(item => item.Wire),
          maxClientMessageBytes - 16 * 1024,
          "Active query set",
        );
        this.#sendQuerySetChange();
      } catch (error) {
        this.#activeByKey.delete(key);
        this.#activeById.delete(queryId);
        throw error;
      }
    }
    active.References++;
    const storeHandle = this.store.acquire(active.QueryId, active.Identity);
    let released = false;
    return Object.freeze({
      QueryId: storeHandle.QueryId,
      subscribe: storeHandle.subscribe,
      getSnapshot: storeHandle.getSnapshot,
      getServerSnapshot: storeHandle.getServerSnapshot,
      release: () => {
        if (released) {
          return;
        }
        released = true;
        storeHandle.release();
        active!.References--;
        if (active!.References === 0) {
          this.#activeByKey.delete(active!.Key);
          this.#activeById.delete(active!.QueryId);
          this.#sendQuerySetChange();
        }
      },
    });
  }

  public executeMutation(mutation: SyncMutation): Promise<JsonValue | undefined> {
    this.#throwIfDisposed();
    validateText(mutation.IdempotencyKey, 256, "IdempotencyKey");
    validateText(mutation.Mutation, 256 * 1024, "Mutation");
    const clientRequestId = mutation.ClientRequestId ?? createUlid();
    validateUlid(clientRequestId, "ClientRequestId");
    if (this.#pendingMutations.has(clientRequestId)) {
      throw new TypeError(`Mutation '${clientRequestId}' is already pending.`);
    }
    if (
      [...this.#pendingMutations.values()]
        .some(candidate => candidate.Request.IdempotencyKey === mutation.IdempotencyKey)
    ) {
      throw new TypeError("An idempotency key cannot identify two pending mutations.");
    }
    if (this.#pendingMutations.size >= maxActiveQueries) {
      throw new RangeError("A Sync session cannot have more than 100 pending mutations.");
    }
    const request = Object.freeze({
      ClientRequestId: clientRequestId,
      IdempotencyKey: mutation.IdempotencyKey,
      Mutation: mutation.Mutation,
      Arguments: ownJson(mutation.Arguments, "Arguments"),
      ...(mutation.Journal === undefined
        ? {}
        : { Journal: ownJson(mutation.Journal, "Journal") }),
    });
    validateSerializedBytes(request, maxClientMessageBytes - 256, "Mutation");
    const optimistic = (mutation.OptimisticPatches?.length ?? 0) > 0;
    if (optimistic) {
      this.store.beginOptimisticMutation(clientRequestId, mutation.OptimisticPatches!);
    }
    const promise = new Promise<JsonValue | undefined>((resolve, reject) => {
      this.#pendingMutations.set(clientRequestId, {
        Request: request,
        Resolve: resolve,
        Reject: reject,
        Optimistic: optimistic,
      });
    });
    if (this.#socket?.readyState === 1) {
      try {
        this.#sendEnvelope("ExecuteMutation", request);
      } catch (error) {
        if (!(error instanceof SyncConnectionError)) {
          throw error;
        }
      }
    }
    return promise;
  }

  public async refreshAuthentication(): Promise<void> {
    this.#throwIfDisposed();
    if (this.#options.BearerTokenProvider === undefined) {
      throw new TypeError("Only bearer-authenticated sessions can refresh authentication.");
    }
    if (this.#socket?.readyState !== 1) {
      throw new SyncConnectionError("The Sync session is not connected.");
    }
    await this.#sendAuthentication();
  }

  public dispose(): void {
    if (this.#disposed) {
      return;
    }
    this.#disposed = true;
    this.#reconnectEnabled = false;
    this.#openAttempt++;
    this.#clearReconnectTimer();
    this.#stopHeartbeat();
    const socket = this.#socket;
    socket?.close(1000, "client disposed");
    for (const [mutationId, mutation] of this.#pendingMutations) {
      this.store.failMutation(mutationId);
      mutation.Reject(new SyncConnectionError("The Sync session was disposed."));
    }
    this.#pendingMutations.clear();
    this.#setDiagnostics({ Status: "disposed", ReconnectAttempt: 0 });
    this.#connectionListeners.clear();
  }

  async #open(reconnecting: boolean, signal?: AbortSignal): Promise<void> {
    const openAttempt = ++this.#openAttempt;
    this.#clearReconnectTimer();
    const sequence = ++this.#connectionSequence;
    this.#outgoingMessageSequence = 0n;
    this.#incomingMessageSequence = 0n;
    this.#chunkAssembly = undefined;
    this.#setDiagnostics({
      Status: reconnecting ? "reconnecting" : "connecting",
      ConnectionSequence: uint64(sequence),
      ReconnectAttempt: this.#reconnectAttempt,
      LastError: undefined,
    });

    const protocols: string[] = [syncWebSocketProtocol];
    if (this.#options.TicketProvider !== undefined) {
      const ticket = await this.#options.TicketProvider();
      if (!/^[A-Za-z0-9_-]+$/.test(ticket) || textEncoder.encode(ticket).length > 16 * 1024) {
        throw new TypeError("The browser Sync ticket must be bounded base64url.");
      }
      protocols.push(`ticket.${ticket}`);
    }
    signal?.throwIfAborted();
    if (openAttempt !== this.#openAttempt || !this.#reconnectEnabled) {
      throw new SyncConnectionError("The Sync connection attempt was cancelled.");
    }
    const socket = this.#webSocketFactory(this.#options.Url, protocols);
    socket.binaryType = "arraybuffer";
    this.#socket = socket;

    await new Promise<void>((resolve, reject) => {
      let opened = false;
      const abort = () => {
        this.#reconnectEnabled = false;
        reject(signal?.reason);
        socket.close(1000, "client cancellation");
      };
      signal?.addEventListener("abort", abort, { once: true });
      socket.onopen = () => {
        try {
          if (socket !== this.#socket) {
            return;
          }
          if (socket.protocol !== syncWebSocketProtocol) {
            throw new SyncProtocolError("The server did not select terrascale-sync-v1.");
          }
          opened = true;
          void this.#initializeConnection(socket, sequence).then(
            () => {
              signal?.removeEventListener("abort", abort);
              resolve();
            },
            error => {
              signal?.removeEventListener("abort", abort);
              reject(error);
              this.#protocolFailure(error, true);
            },
          );
        } catch (error) {
          signal?.removeEventListener("abort", abort);
          reject(error);
          this.#protocolFailure(error, false);
        }
      };
      socket.onmessage = event => {
        if (socket === this.#socket) {
          this.#queueIncoming(event.data, sequence);
        }
      };
      socket.onerror = () => {
        if (!opened) {
          reject(new SyncConnectionError("The WebSocket connection failed."));
          socket.close(1011, "connection failure");
        }
      };
      socket.onclose = event => {
        if (socket !== this.#socket) {
          return;
        }
        this.#socket = undefined;
        this.#stopHeartbeat();
        if (!opened) {
          signal?.removeEventListener("abort", abort);
          reject(new SyncConnectionError(
            `The WebSocket closed before connecting (${event.code}).`,
          ));
        }
        if (this.#reconnectEnabled && !this.#disposed) {
          this.#setDiagnostics({
            Status: "reconnecting",
            LastError: cleanCloseReason(event.reason, event.code),
          });
          this.#scheduleReconnect();
        } else if (!this.#disposed) {
          this.#setDiagnostics({ Status: "disconnected" });
        }
      };
    });
  }

  async #initializeConnection(socket: SyncWebSocket, sequence: bigint): Promise<void> {
    if (this.#options.BearerTokenProvider !== undefined) {
      await this.#sendAuthentication(socket, sequence);
    }
    if (socket !== this.#socket || sequence !== this.#connectionSequence) {
      throw new SyncConnectionError("The Sync connection was replaced during authentication.");
    }
    const current = this.store.getSnapshot();
    const activeIds = new Set(this.#activeById.keys());
    this.#querySetTarget = sameSet(activeIds, this.#serverQueryIds) ? undefined : activeIds;
    const connectPayload = {
      SessionId: this.#sessionId,
      DatabaseId: this.#options.DatabaseId,
      EnvironmentId: this.#options.EnvironmentId,
      LastAppliedVersion: this.#resumeReset ? undefined : current.Version,
      LastAcknowledgedVersion: this.#resumeReset ? undefined : this.#lastAcknowledgedVersion,
      MaximumObservedRevision: this.#maximumObservedRevision,
      ActiveQueries: [...this.#activeById.values()].map(query => query.Wire),
      PendingMutationIds: [...this.#pendingMutations.keys()],
      PreviousIngressRegion: this.#diagnostics.IngressRegion,
      PreviousServingRegion: this.#diagnostics.ServingRegion,
      PreviousDatabaseRegion: this.#diagnostics.DatabaseRegion,
    };
    validateSerializedBytes(connectPayload, maxClientMessageBytes - 256, "Connect");
    this.#sendEnvelope("Connect", connectPayload);
    for (const mutation of this.#pendingMutations.values()) {
      if (mutation.CommittedRevision === undefined) {
        this.#sendEnvelope("ExecuteMutation", mutation.Request);
      }
    }
    this.#reconnectAttempt = 0;
    this.#lastReceiveAt = Date.now();
    this.#startHeartbeat();
    this.#setDiagnostics({
      Status: this.#resyncRequired ? "resync-required" : "connected",
      ReconnectAttempt: 0,
    });
  }

  async #sendAuthentication(
    expectedSocket = this.#socket,
    expectedSequence = this.#connectionSequence,
  ): Promise<void> {
    const token = await this.#options.BearerTokenProvider!();
    validateText(token, 16 * 1024, "BearerToken");
    if (
      expectedSocket === undefined
      || expectedSocket !== this.#socket
      || expectedSequence !== this.#connectionSequence
    ) {
      throw new SyncConnectionError("The Sync connection changed during authentication.");
    }
    this.#sendEnvelope("Authenticate", { BearerToken: token });
  }

  #sendQuerySetChange(): void {
    if (this.#socket?.readyState !== 1 || this.#querySetTarget !== undefined) {
      return;
    }
    const target = new Set(this.#activeById.keys());
    const additions = [...target]
      .filter(queryId => !this.#serverQueryIds.has(queryId))
      .map(queryId => this.#activeById.get(queryId)!.Wire);
    const removals = [...this.#serverQueryIds]
      .filter(queryId => !target.has(queryId))
      .map(QueryId => ({ QueryId }));
    if (additions.length === 0 && removals.length === 0) {
      return;
    }
    const base = parseSyncUInt64(this.store.getSnapshot().Version.QuerySetVersion);
    try {
      this.#sendEnvelope("ModifyQuerySet", {
        BaseQuerySetVersion: uint64(base),
        NewQuerySetVersion: uint64(base + 1n),
        AddQuery: additions,
        RemoveQuery: removals,
      });
    } catch (error) {
      if (error instanceof SyncConnectionError) {
        return;
      }
      throw error;
    }
    this.#querySetTarget = target;
  }

  #sendEnvelope(messageType: string, payload: unknown): void {
    const socket = this.#socket;
    if (socket?.readyState !== 1) {
      throw new SyncConnectionError("The Sync session is not connected.");
    }
    const messageSequence = this.#outgoingMessageSequence + 1n;
    const envelope = JSON.stringify({
      Protocol: syncProtocolVersion,
      MessageType: messageType,
      ConnectionSequence: uint64(this.#connectionSequence),
      MessageSequence: uint64(messageSequence),
      Payload: payload,
    });
    const bytes = textEncoder.encode(envelope).length;
    if (bytes > maxClientMessageBytes) {
      throw new SyncProtocolError("The client message exceeds 1 MiB.");
    }
    if (socket.bufferedAmount + bytes > maxOutgoingBufferedBytes) {
      this.#protocolFailure(new SyncBackpressureError(), true);
      throw new SyncBackpressureError();
    }
    try {
      socket.send(envelope);
      this.#outgoingMessageSequence = messageSequence;
    } catch {
      const error = new SyncConnectionError("The native WebSocket send failed.");
      this.#protocolFailure(error, true);
      throw error;
    }
  }

  #queueIncoming(data: unknown, connectionSequence: bigint): void {
    let size: number;
    if (typeof data === "string") {
      size = textEncoder.encode(data).length;
    } else if (data instanceof ArrayBuffer) {
      size = data.byteLength;
    } else {
      this.#protocolFailure(new SyncProtocolError("Sync accepts only text JSON messages."), false);
      return;
    }
    if (size <= 0 || size > maxTransitionBytes + maxTransitionChunkBytes) {
      this.#protocolFailure(new SyncProtocolError("The server message has an invalid size."), false);
      return;
    }
    if (this.#incomingBufferedBytes + size > maxIncomingBufferedBytes) {
      this.#protocolFailure(new SyncBackpressureError(), true);
      return;
    }
    this.#incomingBufferedBytes += size;
    this.#receiveChain = this.#receiveChain
      .then(() => this.#receive(data, connectionSequence))
      .catch(error => this.#protocolFailure(error, isRecoverable(error)))
      .finally(() => {
        this.#incomingBufferedBytes -= size;
      });
  }

  async #receive(data: unknown, connectionSequence: bigint): Promise<void> {
    if (connectionSequence !== this.#connectionSequence) {
      return;
    }
    const jsonText = typeof data === "string" ? data : textDecoder.decode(data as ArrayBuffer);
    const envelope = parseSyncServerEnvelope(JSON.parse(jsonText));
    if (parseSyncUInt64(envelope.ConnectionSequence) !== this.#connectionSequence) {
      throw new SyncProtocolError("The server used a stale connection sequence.");
    }
    const messageSequence = parseSyncUInt64(envelope.MessageSequence);
    if (messageSequence !== this.#incomingMessageSequence + 1n) {
      throw new SyncProtocolError("The server message sequence is not contiguous.");
    }
    this.#incomingMessageSequence = messageSequence;
    this.#lastReceiveAt = Date.now();

    switch (envelope.MessageType) {
      case "Transition":
        if (envelope.Payload.FrameType === "Complete") {
          if (this.#chunkAssembly !== undefined) {
            throw new SyncProtocolError("A complete transition interrupted chunk assembly.");
          }
          await this.#applyTransition(envelope.Payload);
        } else {
          await this.#receiveChunk(envelope.Payload, connectionSequence);
        }
        break;
      case "MutationResult":
        this.#receiveMutationResult(envelope.Payload);
        break;
      case "AuthenticationError":
        this.store.clear();
        for (const mutation of this.#pendingMutations.values()) {
          mutation.Optimistic = false;
        }
        this.#resumeReset = true;
        this.#resyncRequired = true;
        this.#lastAcknowledgedVersion = undefined;
        this.#setDiagnostics({
          Status: "authentication-error",
          LastError: envelope.Payload.Error.Message,
        });
        this.#restart("authentication refresh required");
        break;
      case "ResyncRequired":
        if (
          compareDatabaseRevision(
            envelope.Payload.EarliestRetainedRevision,
            this.#maximumObservedRevision,
          ) > 0
        ) {
          this.#maximumObservedRevision = envelope.Payload.EarliestRetainedRevision;
        }
        this.#resyncRequired = true;
        this.#resumeReset = true;
        this.#lastAcknowledgedVersion = undefined;
        this.#setDiagnostics({
          Status: "resync-required",
          MaximumObservedRevision: this.#maximumObservedRevision,
          LastError: envelope.Payload.Reason,
        });
        this.#restart("full resync required");
        break;
      case "RegionRouteChanged":
        this.#acceptRoute(
          envelope.Payload.IngressRegion,
          envelope.Payload.ServingRegion,
          envelope.Payload.DatabaseRegion,
          envelope.Payload.RouteGeneration,
        );
        if (envelope.Payload.ReconnectRequired) {
          this.#restart("regional route changed");
        }
        break;
      case "Ping":
        this.#receivePing(envelope.Payload);
        break;
      case "FatalProtocolError":
        this.#reconnectEnabled = false;
        this.#setDiagnostics({
          Status: "disconnected",
          LastError: `${envelope.Payload.Error.Code}: ${envelope.Payload.Error.Message}`,
        });
        this.#socket?.close(1002, "fatal protocol error");
        break;
    }
  }

  async #receiveChunk(chunk: TransitionChunk, connectionSequence: bigint): Promise<void> {
    let assembly = this.#chunkAssembly;
    if (assembly === undefined) {
      assembly = {
        TransitionId: chunk.TransitionId,
        ChunkCount: chunk.ChunkCount,
        UncompressedLength: chunk.UncompressedLength,
        Sha256: chunk.Sha256,
        Chunks: new Array<Uint8Array | undefined>(chunk.ChunkCount),
        ReceivedChunks: 0,
        Received: 0,
      };
      this.#chunkAssembly = assembly;
    }
    if (
      assembly.TransitionId !== chunk.TransitionId
      || assembly.ChunkCount !== chunk.ChunkCount
      || assembly.UncompressedLength !== chunk.UncompressedLength
      || assembly.Sha256 !== chunk.Sha256
      || assembly.Chunks[chunk.ChunkIndex] !== undefined
    ) {
      throw new SyncProtocolError("Transition chunks are duplicate or inconsistent.");
    }
    const bytes = decodeBase64(chunk.Data);
    assembly.Chunks[chunk.ChunkIndex] = bytes;
    assembly.ReceivedChunks++;
    assembly.Received += bytes.length;
    if (assembly.ReceivedChunks < assembly.ChunkCount) {
      return;
    }
    if (
      assembly.ReceivedChunks !== assembly.ChunkCount
      || assembly.Received !== assembly.UncompressedLength
    ) {
      throw new SyncProtocolError("The assembled transition length is invalid.");
    }
    const complete = new Uint8Array(assembly.UncompressedLength);
    let offset = 0;
    for (const item of assembly.Chunks) {
      if (item === undefined) {
        throw new SyncProtocolError("The transition is missing a declared chunk.");
      }
      complete.set(item, offset);
      offset += item.length;
    }
    const digest = await globalThis.crypto.subtle.digest("SHA-256", complete);
    if (connectionSequence !== this.#connectionSequence) {
      return;
    }
    if (hex(new Uint8Array(digest)) !== assembly.Sha256) {
      throw new SyncProtocolError("The assembled transition hash is invalid.");
    }
    const transitionId = assembly.TransitionId;
    this.#chunkAssembly = undefined;
    const transition = parseCompleteTransition(JSON.parse(textDecoder.decode(complete)));
    if (transition.TransitionId !== transitionId) {
      throw new SyncProtocolError("The chunk and transition IDs do not match.");
    }
    await this.#applyTransition(transition);
  }

  async #applyTransition(transition: CompleteTransition): Promise<void> {
    const current = this.store.getSnapshot().Version;
    if (
      compareDatabaseRevision(
        transition.EndVersion.DatabaseRevision,
        this.#maximumObservedRevision,
      ) < 0
    ) {
      throw new SyncProtocolError("The server transition regressed the observed database revision.");
    }
    if (
      this.#resyncRequired
      && (
        parseSyncUInt64(transition.EndVersion.QuerySetVersion)
          < parseSyncUInt64(current.QuerySetVersion)
        || parseSyncUInt64(transition.EndVersion.IdentityVersion)
          < parseSyncUInt64(current.IdentityVersion)
      )
    ) {
      throw new SyncProtocolError("The resync transition regressed client state.");
    }
    this.#acceptRoute(
      transition.IngressRegion,
      transition.ServingRegion,
      transition.DatabaseRegion,
      transition.RouteGeneration,
    );
    if (transition.EndVersion.IdentityVersion !== transition.StartVersion.IdentityVersion) {
      for (const mutation of this.#pendingMutations.values()) {
        mutation.Optimistic = false;
      }
    }
    if (this.#resyncRequired) {
      const completeQueryIds = new Set([
        ...transition.QueryUpdated.map(query => query.QueryId),
        ...transition.QueryFailed.map(query => query.QueryId),
      ]);
      if (
        completeQueryIds.size !== this.#activeById.size
        || [...this.#activeById.keys()].some(queryId => !completeQueryIds.has(queryId))
      ) {
        throw new SyncProtocolError("A resync transition must replace every active query.");
      }
      this.store.applyResyncTransition(transition);
      this.#resyncRequired = false;
      this.#resumeReset = false;
      this.#serverQueryIds.clear();
      for (const queryId of this.#activeById.keys()) {
        this.#serverQueryIds.add(queryId);
      }
      this.#querySetTarget = undefined;
    } else {
      this.store.applyTransition(transition);
      if (
        this.#querySetTarget !== undefined
        && transition.EndVersion.QuerySetVersion !== transition.StartVersion.QuerySetVersion
      ) {
        this.#serverQueryIds.clear();
        for (const queryId of this.#querySetTarget) {
          this.#serverQueryIds.add(queryId);
        }
        this.#querySetTarget = undefined;
      } else if (this.#querySetTarget === undefined) {
        for (const query of [...transition.QueryUpdated, ...transition.QueryFailed]) {
          this.#serverQueryIds.add(query.QueryId);
        }
        for (const query of transition.QueryRemoved) {
          this.#serverQueryIds.delete(query.QueryId);
        }
      }
    }
    this.#maximumObservedRevision = transition.EndVersion.DatabaseRevision;
    for (const [mutationId, mutation] of this.#pendingMutations) {
      if (
        mutation.CommittedRevision !== undefined
        && compareDatabaseRevision(
          transition.EndVersion.DatabaseRevision,
          mutation.CommittedRevision,
        ) >= 0
      ) {
        this.#pendingMutations.delete(mutationId);
      }
    }
    this.#sendEnvelope("AcknowledgeTransition", {
      TransitionId: transition.TransitionId,
      EndVersion: transition.EndVersion,
    });
    this.#lastAcknowledgedVersion = transition.EndVersion;
    this.#setDiagnostics({
      Status: "connected",
      LastAppliedVersion: transition.EndVersion,
      LastAcknowledgedVersion: transition.EndVersion,
      MaximumObservedRevision: this.#maximumObservedRevision,
      LastError: undefined,
    });
    this.#sendQuerySetChange();
    await Promise.resolve();
  }

  #receiveMutationResult(result: {
    readonly ClientRequestId: string;
    readonly Succeeded: boolean;
    readonly Result?: JsonValue | undefined;
    readonly Error?: SyncError | undefined;
    readonly CommittedRevision?: DatabaseRevision | undefined;
  }): void {
    const pending = this.#pendingMutations.get(result.ClientRequestId);
    if (pending === undefined) {
      return;
    }
    if (result.Succeeded) {
      const revision = result.CommittedRevision!;
      if (
        pending.CommittedRevision !== undefined
        && compareDatabaseRevision(pending.CommittedRevision, revision) !== 0
      ) {
        throw new SyncProtocolError("A mutation replay returned a different committed revision.");
      }
      if (pending.CommittedRevision === undefined) {
        pending.CommittedRevision = revision;
        if (compareDatabaseRevision(revision, this.#maximumObservedRevision) > 0) {
          this.#maximumObservedRevision = revision;
          this.#setDiagnostics({ MaximumObservedRevision: revision });
        }
        if (pending.Optimistic) {
          this.store.markMutationCommitted(result.ClientRequestId, revision);
        }
        pending.Resolve(result.Result);
      }
      if (
        compareDatabaseRevision(this.store.getSnapshot().Version.DatabaseRevision, revision) >= 0
      ) {
        this.#pendingMutations.delete(result.ClientRequestId);
      }
      return;
    }
    this.#pendingMutations.delete(result.ClientRequestId);
    this.store.failMutation(result.ClientRequestId);
    pending.Reject(new SyncMutationError(result.Error!));
  }

  #acceptRoute(
    ingressRegion: string,
    servingRegion: string,
    databaseRegion: string,
    routeGeneration: SyncUInt64,
  ): void {
    const current = this.#diagnostics.RouteGeneration;
    if (current !== undefined) {
      const comparison = parseSyncUInt64(routeGeneration) - parseSyncUInt64(current);
      if (comparison < 0n) {
        throw new SyncProtocolError("The server route generation regressed.");
      }
      if (
        comparison === 0n
        && (
          ingressRegion !== this.#diagnostics.IngressRegion
          || servingRegion !== this.#diagnostics.ServingRegion
          || databaseRegion !== this.#diagnostics.DatabaseRegion
        )
      ) {
        throw new SyncProtocolError("One route generation described conflicting regions.");
      }
    }
    this.#setDiagnostics({
      IngressRegion: ingressRegion,
      ServingRegion: servingRegion,
      DatabaseRegion: databaseRegion,
      RouteGeneration: routeGeneration,
    });
  }

  #receivePing(ping: SyncPing): void {
    if (parseSyncUInt64(ping.Nonce) === this.#lastPingNonce && this.#lastPingSentAt > 0) {
      const roundTrip = Math.min(60_000, Math.max(0, Date.now() - this.#lastPingSentAt));
      this.#lastPingSentAt = 0;
      this.#sendEnvelope("ClientTelemetry", {
        RoundTripMilliseconds: roundTrip,
        ObservedIngressRegion: this.#diagnostics.IngressRegion,
        PreviousServingRegion: this.#diagnostics.ServingRegion,
        PreviousDatabaseRegion: this.#diagnostics.DatabaseRegion,
      });
      return;
    }
    this.#sendEnvelope("Ping", ping);
  }

  #startHeartbeat(): void {
    this.#stopHeartbeat();
    const interval = this.#options.HeartbeatIntervalMilliseconds ?? 15_000;
    if (interval === 0) {
      return;
    }
    const timeout = this.#options.HeartbeatTimeoutMilliseconds ?? 45_000;
    this.#heartbeatTimer = setInterval(() => {
      if (Date.now() - this.#lastReceiveAt > timeout) {
        this.#restart("heartbeat timeout");
        return;
      }
      if (this.#lastPingSentAt > 0) {
        return;
      }
      const nonce = ++this.#lastPingNonce;
      this.#lastPingSentAt = Date.now();
      try {
        this.#sendEnvelope("Ping", {
          Nonce: uint64(nonce),
          SentAt: new Date(this.#lastPingSentAt).toISOString(),
        });
      } catch (error) {
        if (!(error instanceof SyncConnectionError)) {
          this.#protocolFailure(error, true);
        }
      }
    }, interval);
  }

  #stopHeartbeat(): void {
    if (this.#heartbeatTimer !== undefined) {
      clearInterval(this.#heartbeatTimer);
      this.#heartbeatTimer = undefined;
    }
    this.#lastPingSentAt = 0;
  }

  #restart(reason: string): void {
    const socket = this.#socket;
    if (socket === undefined) {
      this.#scheduleReconnect();
      return;
    }
    socket.close(1012, reason);
  }

  #protocolFailure(error: unknown, reconnect: boolean): void {
    const message = error instanceof Error ? error.message : "Sync protocol failure.";
    this.#reconnectEnabled = reconnect && !this.#disposed;
    this.#setDiagnostics({
      Status: reconnect ? "reconnecting" : "disconnected",
      LastError: message,
    });
    const socket = this.#socket;
    if (socket === undefined) {
      if (reconnect) {
        this.#scheduleReconnect();
      }
      return;
    }
    socket.close(reconnect ? 1012 : 1002, reconnect ? "resume required" : "protocol error");
  }

  #scheduleReconnect(): void {
    if (
      !this.#reconnectEnabled
      || this.#disposed
      || this.#reconnectTimer !== undefined
      || this.#socket !== undefined
    ) {
      return;
    }
    const attempt = ++this.#reconnectAttempt;
    const delay = this.#options.ReconnectDelayMilliseconds?.(attempt)
      ?? Math.min(10_000, 250 * 2 ** Math.min(attempt - 1, 6));
    if (!Number.isFinite(delay) || delay < 0 || delay > 60_000) {
      this.#reconnectEnabled = false;
      this.#setDiagnostics({
        Status: "disconnected",
        LastError: "ReconnectDelayMilliseconds returned an invalid delay.",
      });
      return;
    }
    this.#setDiagnostics({ Status: "reconnecting", ReconnectAttempt: attempt });
    this.#reconnectTimer = setTimeout(() => {
      this.#reconnectTimer = undefined;
      const opening = this.#open(true);
      this.#opening = opening;
      void opening.catch(error => {
        this.#setDiagnostics({
          Status: "reconnecting",
          LastError: error instanceof Error ? error.message : "Reconnect failed.",
        });
        this.#scheduleReconnect();
      }).finally(() => {
        if (this.#opening === opening) {
          this.#opening = undefined;
        }
      });
    }, delay);
  }

  #clearReconnectTimer(): void {
    if (this.#reconnectTimer !== undefined) {
      clearTimeout(this.#reconnectTimer);
      this.#reconnectTimer = undefined;
    }
  }

  #setDiagnostics(changes: Partial<SyncConnectionSnapshot>): void {
    const next = Object.freeze({ ...this.#diagnostics, ...changes });
    if (sameDiagnostics(this.#diagnostics, next)) {
      return;
    }
    this.#diagnostics = next;
    for (const listener of this.#connectionListeners) {
      listener();
    }
  }

  #throwIfDisposed(): void {
    if (this.#disposed) {
      throw new SyncConnectionError("The Sync session is disposed.");
    }
  }
}

export class SyncConnectionError extends Error {
  public constructor(message: string) {
    super(message);
    this.name = "SyncConnectionError";
  }
}

export class SyncProtocolError extends Error {
  public constructor(message: string) {
    super(message);
    this.name = "SyncProtocolError";
  }
}

export class SyncBackpressureError extends SyncConnectionError {
  public constructor() {
    super("The bounded Sync transport buffer is full.");
    this.name = "SyncBackpressureError";
  }
}

export class SyncMutationError extends Error {
  public constructor(public readonly SyncError: SyncError) {
    super(SyncError.Message);
    this.name = "SyncMutationError";
  }
}

function defaultWebSocketFactory(url: string, protocols: readonly string[]): SyncWebSocket {
  if (globalThis.WebSocket === undefined) {
    throw new SyncConnectionError("This runtime does not provide native WebSocket.");
  }
  return new globalThis.WebSocket(url, [...protocols]) as SyncWebSocket;
}

function validateOptions(options: TerraScaleSyncSessionOptions): void {
  const url = new URL(options.Url);
  if (url.protocol !== "ws:" && url.protocol !== "wss:") {
    throw new TypeError("Sync Url must use ws or wss.");
  }
  validateUlid(options.DatabaseId, "DatabaseId");
  validateUlid(options.EnvironmentId, "EnvironmentId");
  validateText(options.Tenant, 1024, "Tenant");
  validateText(options.AuthorizationScope, 4 * 1024, "AuthorizationScope");
  if ((options.TicketProvider === undefined) === (options.BearerTokenProvider === undefined)) {
    throw new TypeError("Provide exactly one TicketProvider or BearerTokenProvider.");
  }
  const interval = options.HeartbeatIntervalMilliseconds ?? 15_000;
  const timeout = options.HeartbeatTimeoutMilliseconds ?? 45_000;
  if (
    !Number.isInteger(interval)
    || interval < 0
    || interval > 60_000
    || !Number.isInteger(timeout)
    || timeout < Math.max(interval, 1)
    || timeout > 300_000
  ) {
    throw new TypeError("Heartbeat intervals are outside their supported bounds.");
  }
}

function validateUlid(value: string, path: string): void {
  if (!/^[0-9A-HJKMNP-TV-Z]{26}$/.test(value)) {
    throw new TypeError(`${path} must be an uppercase ULID.`);
  }
}

function validateUInt64(value: string, path: string): void {
  try {
    parseSyncUInt64(value);
  } catch {
    throw new TypeError(`${path} must be a canonical UInt64 decimal string.`);
  }
}

function validateText(value: string, maximumBytes: number, path: string): void {
  if (typeof value !== "string" || value.length === 0 || textEncoder.encode(value).length > maximumBytes) {
    throw new TypeError(`${path} is required and exceeds its supported UTF-8 bound.`);
  }
}

function validateSerializedBytes(value: unknown, maximumBytes: number, path: string): void {
  if (textEncoder.encode(JSON.stringify(value)).length > maximumBytes) {
    throw new RangeError(`${path} exceeds ${maximumBytes} bytes.`);
  }
}

function ownJson(value: JsonValue, path: string): JsonValue {
  try {
    const serialized = JSON.stringify(value);
    if (serialized === undefined) {
      throw new TypeError();
    }
    return JSON.parse(serialized) as JsonValue;
  } catch {
    throw new TypeError(`${path} must contain a JSON value.`);
  }
}

function createUlid(): string {
  const bytes = new Uint8Array(16);
  globalThis.crypto.getRandomValues(bytes);
  let timestamp = BigInt(Date.now());
  for (let index = 5; index >= 0; index--) {
    bytes[index] = Number(timestamp & 255n);
    timestamp >>= 8n;
  }
  let value = 0n;
  for (const byte of bytes) {
    value = value << 8n | BigInt(byte);
  }
  const alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
  let result = "";
  for (let index = 0; index < 26; index++) {
    result = alphabet[Number(value & 31n)]! + result;
    value >>= 5n;
  }
  return result;
}

function uint64(value: bigint): SyncUInt64 {
  if (value < 0n || value > 18_446_744_073_709_551_615n) {
    throw new RangeError("The Sync sequence exceeds UInt64.");
  }
  return value.toString() as SyncUInt64;
}

function decodeBase64(value: string): Uint8Array {
  const decoded = globalThis.atob(value);
  const bytes = new Uint8Array(decoded.length);
  for (let index = 0; index < decoded.length; index++) {
    bytes[index] = decoded.charCodeAt(index);
  }
  return bytes;
}

function hex(bytes: Uint8Array): string {
  return [...bytes].map(byte => byte.toString(16).padStart(2, "0")).join("");
}

function sameSet(left: ReadonlySet<string>, right: ReadonlySet<string>): boolean {
  return left.size === right.size && [...left].every(value => right.has(value));
}

function sameDiagnostics(
  left: SyncConnectionSnapshot,
  right: SyncConnectionSnapshot,
): boolean {
  return left.Status === right.Status
    && left.ConnectionSequence === right.ConnectionSequence
    && left.ReconnectAttempt === right.ReconnectAttempt
    && left.LastAppliedVersion === right.LastAppliedVersion
    && left.LastAcknowledgedVersion === right.LastAcknowledgedVersion
    && left.MaximumObservedRevision === right.MaximumObservedRevision
    && left.IngressRegion === right.IngressRegion
    && left.ServingRegion === right.ServingRegion
    && left.DatabaseRegion === right.DatabaseRegion
    && left.RouteGeneration === right.RouteGeneration
    && left.LastError === right.LastError;
}

function cleanCloseReason(reason: string, code: number): string {
  return reason.length === 0 ? `WebSocket closed (${code}).` : reason.slice(0, 256);
}

function isRecoverable(error: unknown): boolean {
  return error instanceof SyncConnectionError
    || error instanceof Error && error.name === "SyncVersionMismatchError";
}
