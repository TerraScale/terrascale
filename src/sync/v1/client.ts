import { canonicalRegionCode, regionalApiBaseUrl } from "../../regions.js";
import {
  decodeTerrascaleSyncV1MsgpackEnvelope,
  encodeTerrascaleSyncV1MsgpackEnvelope,
  terrascaleSyncV1MsgpackManifestSha256,
  terrascaleSyncV1MsgpackMessageDirections,
  terrascaleSyncV1MsgpackProtocol,
  terrascaleSyncV1MsgpackSupportedFeatures,
  validateTerrascaleSyncV1MsgpackMessage,
  type TerrascaleSyncV1MsgpackEnvelope,
  type TerrascaleSyncV1MsgpackMessage,
  type TerrascaleSyncV1MsgpackProjectedField,
  type TerrascaleSyncV1MsgpackResultOperation,
  type TerrascaleSyncV1MsgpackSnapshot,
} from "../../msgpack/generated/terrascale-sync-v1-msgpack.js";
import {
  SyncV1ResultViewStore,
  SyncV1ViewCheckpoint,
  syncV1ResultDigest,
  type SyncV1ResultDelta,
  type SyncV1ResultItem,
  type SyncV1ResultOperation,
  type SyncV1ResultSnapshot,
  type SyncV1ResultResetReason,
} from "./result-view.js";

const MAX_U64 = 0xffff_ffff_ffff_ffffn;

export { SyncV1ViewCheckpoint };
export type { SyncV1ResultDelta, SyncV1ResultItem, SyncV1ResultOperation, SyncV1ResultSnapshot };

export const syncV1Protocol = terrascaleSyncV1MsgpackProtocol;
export const syncV1ManifestSha256 = terrascaleSyncV1MsgpackManifestSha256;

export type SyncV1Envelope = TerrascaleSyncV1MsgpackEnvelope;
export type SyncV1Message = TerrascaleSyncV1MsgpackMessage;

export type SyncV1OpenSubscriptionOptions = {
  readonly subscription: string;
  readonly query: string;
  readonly parameters: Uint8Array;
  readonly checkpoint?: SyncV1ViewCheckpoint | Uint8Array | undefined;
};

/** The reactive transport. HTTP is the cacheable, bounded default. */
export type SyncV1Transport = "http" | "websocket";

export type SyncV1AccessToken = string | (() => string | undefined | Promise<string | undefined>);

/** The small WebSocket surface used by the optional direct transport. */
export type SyncV1WebSocket = {
  binaryType: string;
  send: (data: Uint8Array | ArrayBuffer) => void;
  close: (code?: number, reason?: string) => void;
  addEventListener: (type: "open" | "message" | "error" | "close", listener: (event: unknown) => void) => void;
  removeEventListener: (type: "open" | "message" | "error" | "close", listener: (event: unknown) => void) => void;
};

/** The protocols carry the view capability in a browser-supported request header. */
export type SyncV1WebSocketFactory = (url: string, protocols?: readonly string[]) => SyncV1WebSocket;

export type SyncV1ClientOptions = {
  /** The member/API origin used for the authenticated view attach. */
  readonly apiBaseUrl?: string;
  /** The global CDN origin. Reactive delivery never uses a regional host. */
  readonly syncBaseUrl?: string;
  readonly fetch?: (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>;
  readonly accessToken?: SyncV1AccessToken;
  /** Authenticated attach scope; never sent to snapshot or log delivery. */
  readonly tenantId?: string;
  readonly projectId?: string;
  /** Optional direct WebSocket constructor. HTTP remains the default. */
  readonly webSocketFactory?: SyncV1WebSocketFactory;
  /** Bounded server long-poll window. The contract ceiling is 25 seconds. */
  readonly pollWindowMs?: number;
  /** Delay between reconnect attempts after a transport failure. */
  readonly reconnectDelayMs?: number;
};

export type SyncV1SubscribeOptions = SyncV1OpenSubscriptionOptions & {
  /** Database id required by the attach route. */
  readonly databaseId?: string;
  /** Alias accepted by the public attach contract. */
  readonly database?: string;
  readonly transport?: SyncV1Transport;
  readonly signal?: AbortSignal;
  /** Do not start the background HTTP poll loop until `poll` is called. */
  readonly autoStart?: boolean;
};

export type SyncV1Reset = {
  readonly reason: SyncV1ResultResetReason;
  readonly subscription?: string;
  readonly message?: string;
};

export type SyncV1Subscription = {
  readonly id: string;
  readonly view: SyncV1ResultViewStore;
};

export type SyncV1OpenSubscription = {
  readonly message: Extract<SyncV1Message, { readonly kind: "subscribe" }>;
  readonly subscription: SyncV1Subscription;
};

/** A live HTTP subscription returned by `subscribe`. */
export type SyncV1LiveSubscription = SyncV1Subscription & {
  readonly transport: SyncV1Transport;
  readonly generation: bigint;
  readonly capability: string;
  readonly logUrl: string;
  readonly snapshotUrls: readonly string[];
  readonly reset?: SyncV1Reset | undefined;
  readonly checkpoint: () => SyncV1ViewCheckpoint | undefined;
  readonly poll: (signal?: AbortSignal) => Promise<SyncV1Envelope | undefined>;
  readonly reconnect: () => Promise<void>;
  readonly close: () => void;
};

export type SyncV1ClientSnapshot = {
  readonly online: boolean;
  readonly paused: boolean;
  readonly region: string | null;
  readonly subscriptions: number;
};

export class TerraScaleSyncV1ClientError extends Error {
  public override readonly name = "TerraScaleSyncV1ClientError";

  public constructor(
    public readonly code:
      | "invalid_state"
      | "protocol_error"
      | "unknown_subscription"
      | "remote_error"
      | "reset_required"
      | "authentication_expired"
      | "permission_denied"
      | "slow_consumer"
      | "transport",
    reason: string,
    options?: ErrorOptions,
    public readonly reset?: SyncV1Reset,
  ) {
    super(`terrascale.sync.v1 client: ${code}: ${reason}`, options);
  }
}

/**
 * The single public reactive client state machine. Network transports feed and
 * drain generated MessagePack frames; this class owns no second wire schema.
 */
export class TerraScaleSyncV1Client {
  readonly #subscriptions = new Map<string, SyncV1Subscription>();
  readonly #live = new Map<string, LiveSubscriptionState>();
  readonly #fetch: ((input: RequestInfo | URL, init?: RequestInit) => Promise<Response>) | undefined;
  readonly #apiBaseUrl: string;
  readonly #syncBaseUrl: string;
  readonly #accessToken: SyncV1AccessToken | undefined;
  readonly #tenantId: string | undefined;
  readonly #projectId: string | undefined;
  readonly #webSocketFactory: SyncV1WebSocketFactory | undefined;
  readonly #pollWindowMs: number;
  readonly #reconnectDelayMs: number;
  #online = false;
  #paused = false;
  #region: string | null = null;
  #correlation = 1n;

  public constructor(options: SyncV1ClientOptions = {}) {
    this.#fetch = options.fetch ?? globalThis.fetch?.bind(globalThis);
    this.#apiBaseUrl = normalizeOrigin(options.apiBaseUrl ?? "https://api.terrascale.tech", options.fetch !== undefined);
    this.#syncBaseUrl = normalizeOrigin(options.syncBaseUrl ?? "https://sync.api.terrascale.tech", options.fetch !== undefined);
    this.#accessToken = options.accessToken;
    this.#tenantId = options.tenantId;
    this.#projectId = options.projectId;
    this.#webSocketFactory = options.webSocketFactory;
    this.#pollWindowMs = boundedDuration(options.pollWindowMs ?? 25_000, "pollWindowMs", 25_000);
    this.#reconnectDelayMs = boundedDuration(options.reconnectDelayMs ?? 250, "reconnectDelayMs", 30_000);
  }

  public getSnapshot(): SyncV1ClientSnapshot {
    return Object.freeze({
      online: this.#online,
      paused: this.#paused,
      region: this.#region,
      subscriptions: this.#subscriptions.size,
    });
  }

  public setOnline(online: boolean): void {
    if (typeof online !== "boolean") throw new TypeError("online must be a boolean");
    this.#online = online;
    for (const subscription of this.#subscriptions.values()) {
      subscription.view.setOnline(online);
    }
  }

  /** Opens one live SQL result view and returns the generated subscribe message to send. */
  public openSubscription(options: SyncV1OpenSubscriptionOptions): SyncV1OpenSubscription {
    if (typeof options.subscription !== "string" || options.subscription.trim().length === 0) {
      throw new TerraScaleSyncV1ClientError("invalid_state", "subscription must be a non-empty string");
    }
    if (this.#subscriptions.has(options.subscription)) {
      throw new TerraScaleSyncV1ClientError(
        "invalid_state",
        `subscription '${options.subscription}' is already open`,
      );
    }
    if (this.#subscriptions.size >= 128) {
      throw new TerraScaleSyncV1ClientError("invalid_state", "the 128-subscription bound is reached");
    }
    const message = Object.freeze({
      kind: "subscribe" as const,
      subscription: options.subscription,
      query: options.query,
      parameters: new Uint8Array(options.parameters),
      checkpoint: checkpointBytes(options.checkpoint),
    });
    validateTerrascaleSyncV1MsgpackMessage(message);

    const view = new SyncV1ResultViewStore();
    view.setOnline(this.#online);
    const subscription = Object.freeze({ id: options.subscription, view });
    this.#subscriptions.set(options.subscription, subscription);
    return Object.freeze({ message, subscription });
  }

  /** Closes one view and returns the generated unsubscribe message to send. */
  public closeSubscription(subscription: string): Extract<SyncV1Message, { readonly kind: "unsubscribe" }> {
    const current = this.#requireSubscription(subscription);
    const live = this.#live.get(subscription);
    if (live !== undefined) {
      live.closed = true;
      live.abortController.abort();
    }
    this.#live.delete(subscription);
    current.view.setOnline(false);
    this.#subscriptions.delete(subscription);
    if (this.#subscriptions.size === 0) this.#online = false;
    return Object.freeze({ kind: "unsubscribe", subscription: live?.viewId ?? subscription });
  }

  /**
   * Attaches to the canonical shared view and starts bounded HTTP long-polling.
   * WebSocket is an explicit override. HTTP remains the cacheable default.
   */
  public async subscribe(options: SyncV1SubscribeOptions): Promise<SyncV1LiveSubscription> {
    const transport = options.transport ?? "http";
    if (transport !== "http" && transport !== "websocket") {
      throw new TerraScaleSyncV1ClientError("invalid_state", `unsupported reactive transport '${String(transport)}'`);
    }
    if (this.#fetch === undefined) {
      throw new TerraScaleSyncV1ClientError("transport", "no fetch implementation is available");
    }
    const databaseId = options.databaseId ?? options.database;
    if (databaseId === undefined || databaseId.trim().length === 0) {
      throw new TerraScaleSyncV1ClientError("invalid_state", "databaseId is required for a reactive view attach");
    }
    const opened = this.openSubscription(options);
    const previousOnline = this.#online;
    let attached: AttachedView;
    let initialReset: SyncV1Reset | undefined;
    try {
      attached = await this.#attach(databaseId, opened.message, options.signal);
    } catch (error) {
      if (error instanceof TerraScaleSyncV1ClientError && error.code === "reset_required" && options.checkpoint !== undefined) {
        // The supplied checkpoint is stale. Retry attach exactly once without
        // it; the new authorized snapshot becomes the reset cut.
        initialReset = error.reset;
        try {
          attached = await this.#attach(databaseId, { ...opened.message, checkpoint: null }, options.signal);
        } catch (retryError) {
          this.#subscriptions.delete(options.subscription);
          throw retryError;
        }
      } else {
        this.#subscriptions.delete(options.subscription);
        throw error;
      }
    }
    const state: LiveSubscriptionState = {
      opened,
      databaseId,
      viewId: attached.viewId,
      generation: attached.generation,
      capability: attached.capability,
      logUrl: attached.logUrl,
      snapshotUrls: attached.snapshotUrls,
      closed: false,
      abortController: new AbortController(),
      freshRequired: false,
      transport,
      websocketUrl: attached.websocketUrl,
      socket: undefined,
      etag: undefined,
      etagOffset: undefined,
      loop: undefined,
      reset: initialReset,
    };
    this.#live.set(options.subscription, state);
    this.setOnline(true);

    try {
      if (attached.snapshot !== undefined) {
        installAttachedSnapshot(opened.subscription.view, attached.snapshot);
      } else {
        for (const snapshotUrl of attached.snapshotUrls) {
          const frame = await this.#getBytes(snapshotUrl, state, options.signal);
          if (frame === undefined) continue;
          this.#receive(frame, state);
        }
      }
    } catch (error) {
      state.closed = true;
      state.abortController.abort();
      this.#live.delete(options.subscription);
      this.#subscriptions.delete(options.subscription);
      opened.subscription.view.setOnline(false);
      // setOnline(true) above also touched any pre-existing subscriptions.
      // Restore the transport state that existed before this attach attempt.
      this.setOnline(previousOnline);
      throw error;
    }

    if (transport === "websocket") {
      try {
        await this.#openWebSocket(state, options.signal);
      } catch (error) {
        state.closed = true;
        state.abortController.abort();
        this.#live.delete(options.subscription);
        this.#subscriptions.delete(options.subscription);
        opened.subscription.view.setOnline(false);
        this.setOnline(previousOnline);
        throw error;
      }
    }

    const live = this.#publicLiveSubscription(state);
    if (options.autoStart !== false && transport === "http") state.loop = this.#pollLoop(state, options.signal);
    return live;
  }

  /** Returns the acknowledgement for the latest installed opaque checkpoint. */
  public checkpointAcknowledgement(
    subscription: string,
  ): Extract<SyncV1Message, { readonly kind: "checkpoint_ack" }> {
    const checkpoint = this.#requireSubscription(subscription).view.getCheckpoint();
    if (checkpoint === undefined) {
      throw new TerraScaleSyncV1ClientError(
        "invalid_state",
        `subscription '${subscription}' has no checkpoint to acknowledge`,
      );
    }
    return Object.freeze({
      kind: "checkpoint_ack",
      subscription: this.#live.get(subscription)?.viewId ?? subscription,
      checkpoint: checkpoint.toBytes(),
    });
  }

  /** Encodes one generated client message for a WebSocket or HTTP fallback transport. */
  public encode(message: SyncV1Message, correlation: bigint, causation = 0n): Uint8Array {
    return this.#encodeClientMessage(message, correlation, causation);
  }

  #encodeClientMessage(
    message: SyncV1Message,
    correlation: bigint,
    causation = 0n,
    allowOfflineSubscribe = false,
  ): Uint8Array {
    const direction = terrascaleSyncV1MsgpackMessageDirections[message.kind];
    if (direction === "server_to_client") {
      throw new TerraScaleSyncV1ClientError(
        "protocol_error",
        `message '${message.kind}' is server-to-client only`,
      );
    }
    if (!this.#online && (!allowOfflineSubscribe || message.kind !== "subscribe")) {
      throw new TerraScaleSyncV1ClientError("invalid_state", "the reactive transport is offline");
    }
    if (this.#paused && message.kind !== "heartbeat") {
      throw new TerraScaleSyncV1ClientError("invalid_state", "the server paused client traffic");
    }
    return encodeTerrascaleSyncV1MsgpackEnvelope({
      features: terrascaleSyncV1MsgpackSupportedFeatures,
      correlation,
      causation,
      message,
    });
  }

  /** Decodes and atomically applies one server frame. */
  public receive(frame: Uint8Array): SyncV1Envelope {
    return this.#receive(frame);
  }

  #receive(frame: Uint8Array, state?: LiveSubscriptionState): SyncV1Envelope {
    let envelope: SyncV1Envelope;
    try {
      envelope = decodeTerrascaleSyncV1MsgpackEnvelope(frame);
    } catch (error) {
      throw new TerraScaleSyncV1ClientError(
        "protocol_error",
        error instanceof Error ? error.message : String(error),
        { cause: error },
      );
    }
    if (terrascaleSyncV1MsgpackMessageDirections[envelope.message.kind] === "client_to_server") {
      throw new TerraScaleSyncV1ClientError(
        "protocol_error",
        `received client-to-server message '${envelope.message.kind}'`,
      );
    }

    const subscription = (wireId: string): SyncV1Subscription => {
      if (state === undefined) return this.#requireSubscription(wireId);
      if (wireId !== state.viewId) {
        throw new TerraScaleSyncV1ClientError("protocol_error", "frame belongs to another attached view");
      }
      return state.opened.subscription;
    };

    switch (envelope.message.kind) {
      case "snapshot":
        if (state === undefined) {
          subscription(envelope.message.subscription).view.bootstrap(resultSnapshot(envelope.message.snapshot));
        } else {
          installAttachedSnapshot(subscription(envelope.message.subscription).view, envelope.message.snapshot);
        }
        break;
      case "delta": {
        const view = subscription(envelope.message.subscription).view;
        const outcome = view.applyDelta(resultDelta(envelope.message));
        if (outcome !== "applied" && outcome !== "duplicate") {
          throw new TerraScaleSyncV1ClientError(
            "protocol_error",
            `subscription '${envelope.message.subscription}' requires reset after ${outcome}`,
          );
        }
        break;
      }
      case "reset":
        if (state !== undefined) {
          state.reset = { reason: normalizeResetReason(envelope.message.reason), subscription: state.opened.subscription.id };
        }
        subscription(envelope.message.subscription).view.reset(
          resultSnapshot(envelope.message.snapshot),
        );
        break;
      case "authorization_changed":
        subscription(envelope.message.subscription).view.transitionAuthorization(
          envelope.message.authorization_generation,
          envelope.message.removed,
          resultSnapshot(envelope.message.snapshot),
        );
        break;
      case "region_route_change":
        this.#region = envelope.message.region;
        this.setOnline(false);
        for (const state of this.#live.values()) state.regionMoved = true;
        break;
      case "backpressure":
        this.#paused = envelope.message.mode === "pause";
        break;
      case "error":
        this.setOnline(false);
        if (envelope.message.code === "slow_consumer") {
          throw new TerraScaleSyncV1ClientError(
            "slow_consumer",
            envelope.message.detail ?? "the server dropped this slow consumer",
            undefined,
            { reason: "backpressure", ...(envelope.message.detail === null ? {} : { message: envelope.message.detail }) },
          );
        }
        if (envelope.message.code === "authentication_expired") {
          throw new TerraScaleSyncV1ClientError(
            "authentication_expired",
            envelope.message.detail ?? "the reactive session expired",
          );
        }
        if (envelope.message.code === "permission_denied") {
          throw new TerraScaleSyncV1ClientError(
            "permission_denied",
            envelope.message.detail ?? "the reactive view is no longer authorized",
          );
        }
        throw new TerraScaleSyncV1ClientError(
          "remote_error",
          `${envelope.message.code}${envelope.message.detail === null ? "" : `: ${envelope.message.detail}`}`,
        );
      case "heartbeat":
        break;
      default:
        throw new TerraScaleSyncV1ClientError(
          "protocol_error",
          `message '${envelope.message.kind}' is not valid from the server`,
        );
    }
    return envelope;
  }

  /** Applies one complete WebSocket MessagePack message.
   *
   * The protocol defines one envelope per binary WebSocket message; the
   * plural name remains useful to transport adapters delivering messages in
   * a batch callback.
   */
  public receiveMany(frame: Uint8Array): readonly SyncV1Envelope[] {
    return [this.receive(frame)];
  }

  #requireSubscription(subscription: string): SyncV1Subscription {
    const current = this.#subscriptions.get(subscription);
    if (current === undefined) {
      throw new TerraScaleSyncV1ClientError(
        "unknown_subscription",
        `subscription '${subscription}' is not open`,
      );
    }
    return current;
  }

  async #attach(
    databaseId: string,
    message: Extract<SyncV1Message, { readonly kind: "subscribe" }>,
    signal?: AbortSignal,
  ): Promise<AttachedView> {
    const headers = await this.#headers("application/json", undefined);
    if (this.#tenantId !== undefined) headers.set("x-terrascale-tenant-id", this.#tenantId);
    if (this.#projectId !== undefined) headers.set("x-terrascale-project-id", this.#projectId);
    headers.set("Content-Type", "application/json");
    const response = await this.#request(
      `${this.#apiBaseUrl}/api/v1/reactive/views`,
      {
        method: "POST",
        headers,
        body: JSON.stringify({
          databaseId,
          subscription: message.subscription,
          statement: message.query,
          parameters: bytesToBase64(message.parameters),
          checkpoint: message.checkpoint === null ? null : bytesToBase64(message.checkpoint),
        }),
        ...(signal === undefined ? {} : { signal }),
      },
      "reactive view attach",
    );
    if (!response.ok) throw await this.#httpError(response, "reactive view attach");
    let value: unknown;
    try {
      value = await response.json();
    } catch (error) {
      throw new TerraScaleSyncV1ClientError("protocol_error", "reactive attach was not valid JSON", { cause: error });
    }
    const region = canonicalRegionCode(response.headers.get("x-terrascale-region") ?? undefined);
    const memberOrigin = region === undefined ? this.#apiBaseUrl : regionalApiBaseUrl(this.#apiBaseUrl, region);
    return decodeAttachedView(value, this.#syncBaseUrl, message.subscription, memberOrigin);
  }

  async #getBytes(url: string, state: LiveSubscriptionState, signal?: AbortSignal): Promise<Uint8Array | undefined> {
    const linked = linkAbortSignals(state.abortController.signal, signal);
    try {
      const response = await this.#request(
        url,
        {
          method: "GET",
          headers: await this.#headers("application/msgpack", state.capability),
          signal: linked.signal,
        },
        "reactive snapshot/log",
      );
      if (state.closed) return undefined;
      if (response.status === 204 || response.status === 304) return undefined;
      if (!response.ok) throw await this.#httpError(response, "reactive snapshot/log");
      const bytes = new Uint8Array(await response.arrayBuffer());
      return bytes.length === 0 ? undefined : bytes;
    } finally {
      linked.dispose();
    }
  }

  async #pollOnce(state: LiveSubscriptionState, signal?: AbortSignal): Promise<SyncV1Envelope | undefined> {
    while (this.#paused && !state.closed) await delay(this.#reconnectDelayMs, signal ?? state.abortController.signal);
    if (state.closed) return undefined;
    const revision = state.opened.subscription.view.getRevision();
    const url = new URL(state.logUrl);
    url.searchParams.set("offset", revision.toString(10));
    const timeout = new AbortController();
    if (signal?.aborted) throw signal.reason;
    const abort = () => timeout.abort(state.abortController.signal.reason ?? signal?.reason);
    state.abortController.signal.addEventListener("abort", abort, { once: true });
    if (signal !== undefined) {
      signal.addEventListener("abort", abort, { once: true });
    }
    const timer = globalThis.setTimeout(() => timeout.abort(), this.#pollWindowMs);
    try {
      let response: Response;
      try {
        response = await this.#request(
          url,
          {
            method: "GET",
            headers: await this.#headers("application/msgpack", state.capability),
            signal: timeout.signal,
          },
          "reactive log poll",
        );
      } catch (error) {
        // A server-side 25 second long-poll timeout is an empty response. It
        // must not trigger a new attach or consume the reconnect budget.
        if (error instanceof TerraScaleSyncV1ClientError &&
          error.code === "transport" && isAbortError(error.cause) &&
          !signal?.aborted) return undefined;
        throw error;
      }
      if (state.closed) return undefined;
      if (response.status === 204 || response.status === 304) return undefined;
      if (!response.ok) throw await this.#httpError(response, "reactive log poll");
      const bytes = new Uint8Array(await response.arrayBuffer());
      if (state.closed) return undefined;
      if (bytes.length === 0) return undefined;
      let envelope: SyncV1Envelope;
      try {
        envelope = this.#receive(bytes, state);
      } catch (error) {
        if (error instanceof TerraScaleSyncV1ClientError && error.reset !== undefined) state.reset = error.reset;
        throw error;
      }
      if (envelope.message.kind === "reset") {
        state.reset = { reason: normalizeResetReason(envelope.message.reason), subscription: state.opened.subscription.id };
      }
      return envelope;
    } catch (error) {
      if (error instanceof TerraScaleSyncV1ClientError && error.code === "reset_required") {
        if (state.freshRequired) throw error;
        state.reset = error.reset;
        state.freshRequired = true;
        // A refused/expired checkpoint is not a transport failure. Reattach
        // once without it so the server can issue a fresh authorized cut.
        // The resulting snapshot is installed before this method returns,
        // notifying view observers atomically.
        await this.#reconnect(state, signal);
        return undefined;
      }
      throw error;
    } finally {
      globalThis.clearTimeout(timer);
      signal?.removeEventListener("abort", abort);
      state.abortController.signal.removeEventListener("abort", abort);
    }
  }

  async #pollLoop(state: LiveSubscriptionState, signal?: AbortSignal): Promise<void> {
    while (!state.closed && !signal?.aborted) {
      try {
        const envelope = await this.#pollOnce(state, signal);
        if (envelope?.message.kind === "reset") {
          state.reset = { reason: normalizeResetReason(envelope.message.reason), subscription: state.opened.subscription.id };
        }
        if (envelope === undefined) await delay(100, signal);
        if (state.regionMoved) {
          state.regionMoved = false;
          await this.#reconnect(state, signal);
        }
      } catch (error) {
        if (state.closed || signal?.aborted) return;
        if (error instanceof TerraScaleSyncV1ClientError && error.reset !== undefined) {
          state.reset = error.reset;
        }
        if (error instanceof TerraScaleSyncV1ClientError &&
          (error.code === "authentication_expired" || error.code === "permission_denied" || error.code === "slow_consumer" || error.code === "remote_error")) {
          if (error.reset !== undefined) state.reset = error.reset;
          return;
        }
        if (error instanceof TerraScaleSyncV1ClientError && error.code === "reset_required" && state.freshRequired) return;
        this.setOnline(false);
        await delay(this.#reconnectDelayMs, signal);
        if (!state.closed && !signal?.aborted) {
          try {
            await this.#reconnect(state, signal);
          } catch {
            continue;
          }
        }
      }
    }
  }

  async #reconnect(state: LiveSubscriptionState, signal?: AbortSignal): Promise<void> {
    if (state.closed || this.#fetch === undefined) return;
    state.socket?.close(1000, "reconnect");
    state.socket = undefined;
    const current = state.freshRequired ? undefined : state.opened.subscription.view.getCheckpoint();
    const message = state.opened.message;
    const linked = linkAbortSignals(state.abortController.signal, signal);
    let attached: AttachedView;
    try {
      try {
        attached = await this.#attach(state.databaseId, {
          ...message,
          checkpoint: current?.toBytes() ?? null,
        }, linked.signal);
      } catch (error) {
        if (error instanceof TerraScaleSyncV1ClientError && error.code === "reset_required" && !state.freshRequired) {
          state.reset = error.reset;
          state.freshRequired = true;
          attached = await this.#attach(state.databaseId, { ...message, checkpoint: null }, linked.signal);
        } else {
          throw error;
        }
      }
    } finally {
      linked.dispose();
    }
    if (state.closed) return;
    state.viewId = attached.viewId;
    state.generation = attached.generation;
    state.capability = attached.capability;
    state.logUrl = attached.logUrl;
    state.snapshotUrls = attached.snapshotUrls;
    state.websocketUrl = attached.websocketUrl;
    if (attached.snapshot !== undefined) {
      installAttachedSnapshot(state.opened.subscription.view, attached.snapshot);
    } else {
      for (const snapshotUrl of attached.snapshotUrls) {
        const bytes = await this.#getBytes(snapshotUrl, state, signal);
        if (bytes !== undefined) this.#receive(bytes, state);
      }
    }
    if (state.transport === "websocket") await this.#openWebSocket(state, signal);
    state.freshRequired = false;
    state.regionMoved = false;
    this.setOnline(true);
  }

  async #openWebSocket(state: LiveSubscriptionState, signal?: AbortSignal): Promise<void> {
    const factory = this.#webSocketFactory ?? (typeof globalThis.WebSocket === "function"
      ? (url: string, protocols?: readonly string[]) => new globalThis.WebSocket(url, protocols === undefined ? undefined : [...protocols]) as unknown as SyncV1WebSocket
      : undefined);
    if (factory === undefined) {
      throw new TerraScaleSyncV1ClientError(
        "transport",
        "WebSocket transport was requested but no WebSocket implementation is available",
      );
    }
    const url = state.websocketUrl;
    if (url === undefined || new URL(url).hostname === "api.terrascale.tech") {
      throw new TerraScaleSyncV1ClientError("protocol_error", "WebSocket attach did not identify a regional member");
    }
    if (!/^[!#$%&'*+\-.^_`|~0-9A-Za-z]+$/u.test(state.capability)) {
      throw new TerraScaleSyncV1ClientError("protocol_error", "view capability is not a valid WebSocket protocol token");
    }
    const socket = factory(url, ["terrascale.sync.v1", `terrascale.cap.${state.capability}`]);
    if (socket === undefined || socket === null || typeof socket.send !== "function" || typeof socket.close !== "function") {
      throw new TerraScaleSyncV1ClientError("transport", "the WebSocket factory returned an invalid socket");
    }
    socket.binaryType = "arraybuffer";
    state.socket = socket;
    await new Promise<void>((resolve, reject) => {
      let opened = false;
      let settled = false;
      const cleanupOpen = () => socket.removeEventListener("open", onOpen);
      const abort = () => {
        socket.close(1000, "aborted");
        if (!settled) {
          settled = true;
          reject(signal?.reason ?? state.abortController.signal.reason);
        }
      };
      const onOpen = () => {
        opened = true;
        cleanupOpen();
        if (signal?.aborted || state.abortController.signal.aborted) {
          abort();
          return;
        }
        try {
          const subscribe = Object.freeze({
            ...state.opened.message,
            subscription: state.viewId,
            checkpoint: checkpointBytes(state.opened.subscription.view.getCheckpoint()),
          });
          socket.send(this.#encodeClientMessage(subscribe, this.#correlation++, 0n, true));
          if (!settled) {
            settled = true;
            resolve();
          }
        } catch (error) {
          if (!settled) {
            settled = true;
            reject(error instanceof TerraScaleSyncV1ClientError ? error : new TerraScaleSyncV1ClientError("transport", "WebSocket subscribe failed", { cause: error }));
          }
        }
      };
      const onMessage = (event: unknown) => {
        void (async () => {
          try {
            const frame = await socketFrame(event);
            if (frame === undefined) throw new TerraScaleSyncV1ClientError("protocol_error", "WebSocket data was not binary MessagePack");
            if (state.closed || state.socket !== socket) return;
            this.#receive(frame, state);
            if (state.regionMoved && !state.closed && state.socket === socket) {
              await delay(this.#reconnectDelayMs, state.abortController.signal);
              if (!state.closed && state.socket === socket) await this.#reconnect(state);
            }
          } catch (error) {
            this.setOnline(false);
            socket.close(1002, "invalid reactive frame");
            if (!opened && !settled) {
              settled = true;
              reject(error);
            }
          }
        })();
      };
      const onError = (event: unknown) => {
        if (state.socket !== socket) return;
        this.setOnline(false);
        if (!opened && !settled) {
          settled = true;
          reject(new TerraScaleSyncV1ClientError("transport", "WebSocket connection failed", { cause: event }));
        }
      };
      const onClose = () => {
        cleanupOpen();
        socket.removeEventListener("message", onMessage);
        socket.removeEventListener("error", onError);
        socket.removeEventListener("close", onClose);
        state.abortController.signal.removeEventListener("abort", abort);
        signal?.removeEventListener("abort", abort);
        if (state.socket !== socket) return;
        this.setOnline(false);
        if (!opened && !settled) {
          settled = true;
          reject(new TerraScaleSyncV1ClientError("transport", "WebSocket closed before it opened"));
        }
      };
      socket.addEventListener("open", onOpen);
      socket.addEventListener("message", onMessage);
      socket.addEventListener("error", onError);
      socket.addEventListener("close", onClose);
      state.abortController.signal.addEventListener("abort", abort, { once: true });
      signal?.addEventListener("abort", abort, { once: true });
    });
  }

  #publicLiveSubscription(state: LiveSubscriptionState): SyncV1LiveSubscription {
    return {
      id: state.opened.subscription.id,
      view: state.opened.subscription.view,
      get transport() { return state.transport; },
      get generation() { return state.generation; },
      get capability() { return state.capability; },
      get logUrl() { return state.logUrl; },
      get snapshotUrls() { return Object.freeze([...state.snapshotUrls]); },
      get reset() { return state.reset; },
      checkpoint: () => state.opened.subscription.view.getCheckpoint(),
      poll: signal => this.#pollOnce(state, signal),
      reconnect: () => this.#reconnect(state),
      close: () => {
        if (state.closed) return;
        state.closed = true;
        state.abortController.abort();
        state.socket?.close(1000, "closed");
        state.socket = undefined;
        this.#live.delete(state.opened.subscription.id);
        this.#subscriptions.delete(state.opened.subscription.id);
        state.opened.subscription.view.setOnline(false);
        if (this.#subscriptions.size === 0) this.#online = false;
      },
    };
  }

  async #headers(accept: string, capability: string | undefined): Promise<Headers> {
    const headers = new Headers({ accept });
    if (capability !== undefined) headers.set("x-terrascale-capability", capability);
    const token = await resolveToken(this.#accessToken);
    if (token !== undefined && token.length > 0) headers.set("Authorization", token.startsWith("Bearer ") ? token : `Bearer ${token}`);
    return headers;
  }

  async #request(input: RequestInfo | URL, init: RequestInit, operation: string): Promise<Response> {
    if (this.#fetch === undefined) throw new TerraScaleSyncV1ClientError("transport", `no fetch implementation is available for ${operation}`);
    try {
      const response = await this.#fetch(input, { ...init, redirect: "manual" });
      if (response.type === "opaqueredirect" || (response.status >= 300 && response.status < 400 && response.status !== 304)) {
        throw new TypeError("Reactive delivery returned an unsupported HTTP redirect.");
      }
      return response;
    } catch (error) {
      throw new TerraScaleSyncV1ClientError("transport", `${operation} failed`, { cause: error });
    }
  }

  async #httpError(response: Response, operation: string): Promise<TerraScaleSyncV1ClientError> {
    let detail = `${operation} returned HTTP ${response.status}`;
    let error: Record<string, unknown> | undefined;
    let reset: SyncV1Reset | undefined;
    try {
      const body: unknown = await response.clone().json();
      if (isRecord(body)) {
        error = isRecord(body.error) ? body.error : body;
        if (typeof error.message === "string") detail = error.message;
      }
    } catch {
      // Status and operation are sufficient when an error body is unavailable.
    }

    // Authentication and authorization are terminal for this session. Check
    // their typed contract codes before interpreting a status or reset reason,
    // so an expired session can never be retried or misclassified as a stale
    // checkpoint.
    if (response.status === 401 || error?.code === "authentication_expired") {
      return new TerraScaleSyncV1ClientError("authentication_expired", detail);
    }
    if (response.status === 403 || error?.code === "permission_denied") {
      return new TerraScaleSyncV1ClientError("permission_denied", detail);
    }
    if (error?.code === "checkpoint_refused") {
      reset = { reason: "checkpoint-expired", message: detail };
    } else if (typeof error?.reason === "string") {
      reset = { reason: normalizeResetReason(error.reason), message: detail };
    }
    if (reset === undefined && response.status === 410) {
      reset = { reason: "retention-advanced", message: detail };
    }
    if (reset !== undefined) return new TerraScaleSyncV1ClientError("reset_required", detail, undefined, reset);
    return new TerraScaleSyncV1ClientError("transport", detail);
  }
}

type AttachedView = {
  readonly viewId: string;
  readonly generation: bigint;
  readonly checkpoint: Uint8Array | undefined;
  readonly capability: string;
  readonly snapshotUrls: readonly string[];
  readonly logUrl: string;
  readonly snapshot: TerrascaleSyncV1MsgpackSnapshot | undefined;
  readonly websocketUrl: string | undefined;
};

type LiveSubscriptionState = {
  readonly opened: SyncV1OpenSubscription;
  readonly databaseId: string;
  viewId: string;
  generation: bigint;
  capability: string;
  snapshotUrls: readonly string[];
  logUrl: string;
  closed: boolean;
  readonly abortController: AbortController;
  freshRequired: boolean;
  transport: SyncV1Transport;
  websocketUrl: string | undefined;
  socket: SyncV1WebSocket | undefined;
  etag: string | undefined;
  etagOffset: bigint | undefined;
  regionMoved?: boolean;
  reset: SyncV1Reset | undefined;
  loop: Promise<void> | undefined;
};

function checkpointBytes(
  checkpoint: SyncV1ViewCheckpoint | Uint8Array | undefined,
): Uint8Array | null {
  if (checkpoint === undefined) return null;
  return checkpoint instanceof SyncV1ViewCheckpoint
    ? checkpoint.toBytes()
    : new Uint8Array(checkpoint);
}

function resultSnapshot(snapshot: TerrascaleSyncV1MsgpackSnapshot): SyncV1ResultSnapshot {
  return {
    view_generation: snapshot.view_generation,
    authorization_generation: snapshot.authorization_generation,
    revision: snapshot.revision,
    items: snapshot.items.map(resultItem),
    digest: snapshot.digest,
    checkpoint: new SyncV1ViewCheckpoint(snapshot.checkpoint),
  };
}

function installAttachedSnapshot(view: SyncV1ResultViewStore, snapshot: TerrascaleSyncV1MsgpackSnapshot): void {
  const result = resultSnapshot(snapshot);
  if (view.getCheckpoint() === undefined) {
    view.bootstrap(result);
    return;
  }
  if (result.view_generation > view.getViewGeneration() || result.revision > view.getRevision()) {
    view.reset(result);
  }
}

function resultDelta(
  message: Extract<SyncV1Message, { readonly kind: "delta" }>,
): SyncV1ResultDelta {
  return {
    view_generation: message.view_generation,
    from_revision: message.from_revision,
    to_revision: message.to_revision,
    operations: message.operations.map(resultOperation),
    digest: message.digest,
    checkpoint: new SyncV1ViewCheckpoint(message.checkpoint),
  };
}

function resultItem(item: {
  readonly identity: string;
  readonly fields: ReadonlyArray<TerrascaleSyncV1MsgpackProjectedField>;
}): SyncV1ResultItem {
  return {
    identity: item.identity,
    value: projectedValue(item.fields),
  };
}

function resultOperation(operation: TerrascaleSyncV1MsgpackResultOperation): SyncV1ResultOperation {
  switch (operation.kind) {
    case "insert":
      return {
        kind: "Insert",
        identity: operation.identity,
        position: operation.position,
        value: projectedValue(operation.fields),
      };
    case "update":
      return {
        kind: "Update",
        identity: operation.identity,
        value: projectedValue(operation.fields),
      };
    case "patch":
      return {
        kind: "Patch",
        identity: operation.identity,
        changes: operation.changes.map(change => ({
          path: change.path,
          value: change.value === null ? null : new Uint8Array(change.value),
        })),
      };
    case "remove":
      return { kind: "Remove", identity: operation.identity };
    case "move":
      return { kind: "Move", identity: operation.identity, position: operation.position };
  }
}

function projectedValue(
  fields: ReadonlyArray<TerrascaleSyncV1MsgpackProjectedField>,
): Readonly<Record<string, Uint8Array>> {
  const value: Record<string, Uint8Array> = Object.create(null) as Record<string, Uint8Array>;
  for (const field of fields) {
    if (Object.hasOwn(value, field.path)) {
      throw new TerraScaleSyncV1ClientError(
        "protocol_error",
        `projected field '${field.path}' appears more than once`,
      );
    }
    value[field.path] = new Uint8Array(field.value);
  }
  return value;
}

function normalizeOrigin(origin: string, explicitTestTransport = false): string {
  const value = origin.trim();
  if (value.length === 0) throw new TypeError("reactive origins cannot be empty");
  let url: URL;
  try {
    url = new URL(value);
  } catch {
    throw new TypeError("reactive origins must be absolute URLs");
  }
  if (url.protocol !== "http:" && url.protocol !== "https:") throw new TypeError("reactive origins must use HTTP or HTTPS");
  if (url.username !== "" || url.password !== "") throw new TypeError("reactive origins cannot contain credentials");
  for (const key of url.searchParams.keys()) {
    if (/(?:auth|cookie|credential|key|pass(?:word)?|secret|session|token)/iu.test(key)) {
      throw new TypeError("reactive origins cannot contain secret-bearing query parameters");
    }
  }
  const loopback = url.hostname === "localhost" || url.hostname === "127.0.0.1" || url.hostname === "[::1]";
  const testHost = url.hostname === "test" || url.hostname.endsWith(".test");
  if (url.protocol === "http:" && !loopback && !(explicitTestTransport && testHost)) {
    throw new TypeError("reactive origins must use HTTPS, except explicit loopback/test transports");
  }
  if (url.pathname !== "/" || url.search !== "" || url.hash !== "") throw new TypeError("reactive origins must not contain a path or query");
  return url.toString().replace(/\/$/u, "");
}

function boundedDuration(value: number, name: string, max: number): number {
  if (!Number.isInteger(value) || value < 0 || value > max) {
    throw new TypeError(`${name} must be an integer between 0 and ${max}`);
  }
  return value;
}

async function resolveToken(token: SyncV1AccessToken | undefined): Promise<string | undefined> {
  if (token === undefined) return undefined;
  return typeof token === "function" ? await token() : token;
}

async function socketFrame(event: unknown): Promise<Uint8Array | undefined> {
  if (!isRecord(event)) return undefined;
  const data = event.data;
  if (data instanceof Uint8Array) return new Uint8Array(data);
  if (data instanceof ArrayBuffer) return new Uint8Array(data.slice(0));
  if (typeof Blob !== "undefined" && data instanceof Blob) {
    return new Uint8Array(await data.arrayBuffer());
  }
  return undefined;
}

async function delay(milliseconds: number, signal?: AbortSignal): Promise<void> {
  if (milliseconds === 0) return;
  await new Promise<void>((resolve, reject) => {
    const timer = globalThis.setTimeout(resolve, milliseconds);
    if (signal === undefined) return;
    const abort = () => {
      globalThis.clearTimeout(timer);
      reject(signal.reason);
    };
    if (signal.aborted) abort();
    else {
      signal.addEventListener("abort", abort, { once: true });
      globalThis.setTimeout(() => signal.removeEventListener("abort", abort), milliseconds);
    }
  });
}

function bytesToBase64(bytes: Uint8Array): string {
  let binary = "";
  const chunk = 0x8000;
  for (let offset = 0; offset < bytes.length; offset += chunk) {
    binary += String.fromCharCode(...bytes.subarray(offset, offset + chunk));
  }
  return globalThis.btoa(binary);
}

function bytesFromUnknown(value: unknown, field: string): Uint8Array {
  if (value instanceof Uint8Array) return new Uint8Array(value);
  if (Array.isArray(value) && value.length <= 4096 && value.every(item => typeof item === "number" && Number.isInteger(item) && item >= 0 && item <= 255)) {
    return Uint8Array.from(value as number[]);
  }
  if (typeof value === "string" && value.length <= 8192) {
    // Attach responses use base64 for bytes. Plain strings remain accepted for
    // local fixtures and are encoded as UTF-8, without ever parsing JSON data.
    try {
      const binary = globalThis.atob(value);
      const bytes = Uint8Array.from(binary, character => character.charCodeAt(0));
      if (bytes.length > 0) return bytes;
    } catch {
      // Fall through to the opaque UTF-8 fixture form.
    }
    return new TextEncoder().encode(value);
  }
  throw new TerraScaleSyncV1ClientError("protocol_error", `attach response field '${field}' is not bytes`);
}

function bigintFromUnknown(value: unknown, field: string): bigint {
  try {
    const parsed = typeof value === "bigint"
      ? value
      : typeof value === "number" && Number.isSafeInteger(value) && value >= 0
        ? BigInt(value)
        : typeof value === "string" && /^\d+$/u.test(value)
          ? BigInt(value)
          : undefined;
    if (parsed !== undefined && parsed >= 0n && parsed <= MAX_U64) return parsed;
  } catch {
    // Fall through to the typed protocol error below.
  }
  throw new TerraScaleSyncV1ClientError("protocol_error", `attach response field '${field}' is not a u64`);
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function canonicalViewPath(view: string, generation: bigint, leaf: string): string {
  return `/api/v1/reactive/views/${encodeURIComponent(view)}/${generation.toString(10)}/${leaf}`;
}

function canonicalUrl(syncOrigin: string, path: string): string {
  return `${syncOrigin}${path}`;
}

function decodeAttachedView(value: unknown, syncOrigin: string, subscription: string, memberOrigin: string): AttachedView {
  if (!isRecord(value)) throw new TerraScaleSyncV1ClientError("protocol_error", "reactive attach response is not an object");
  const view = typeof value.viewId === "string"
    ? value.viewId
    : typeof value.view_id === "string"
      ? value.view_id
      : typeof value.view === "string"
        ? value.view
        : subscription;
  if (!/^[A-Za-z0-9_-]{1,128}$/u.test(view)) {
    throw new TerraScaleSyncV1ClientError("protocol_error", "reactive attach response has an invalid view id");
  }
  const generation = bigintFromUnknown(value.generation ?? value.view_generation ?? value.viewGeneration, "generation");
  const capabilityValue = value.capability ?? value.viewCapability ?? value.view_capability;
  if (typeof capabilityValue !== "string" || capabilityValue.trim().length === 0 || capabilityValue.length > 4096 || capabilityValue.includes(",")) {
    throw new TerraScaleSyncV1ClientError("protocol_error", "reactive attach response has no view capability");
  }
  const capabilityHeader = value.capability_header ?? value.capabilityHeader;
  if (capabilityHeader !== undefined && (typeof capabilityHeader !== "string" ||
    capabilityHeader.toLowerCase() !== "x-terrascale-capability")) {
    throw new TerraScaleSyncV1ClientError("protocol_error", "reactive attach response has an unsupported capability header");
  }
  const websocketValue = value.websocketUrl ?? value.websocket_url ?? value.wsUrl ?? value.ws_url;
  const attachedWebsocketUrl = websocketValue === undefined
    ? websocketUrl(memberOrigin, view, generation)
    : canonicalWebSocketUrl(websocketValue, memberOrigin, view, generation);
  const expectedLogPath = canonicalViewPath(view, generation, "log");
  const rawLogUrl = value.logUrl ?? value.log_url ?? value.log;
  const logUrl = rawLogUrl === undefined
    ? canonicalUrl(syncOrigin, expectedLogPath)
    : canonicalAttachedUrl(rawLogUrl, syncOrigin, expectedLogPath, "log");
  const rawSnapshotUrls = value.snapshotUrls ?? value.snapshot_urls ?? value.snapshotChunkUrls ?? value.snapshot_chunk_urls ?? value.snapshot_chunks;
  const snapshotUrls: string[] = [];
  if (Array.isArray(rawSnapshotUrls)) {
    if (rawSnapshotUrls.length > 1024) throw new TerraScaleSyncV1ClientError("protocol_error", "reactive attach response has too many snapshot URLs");
    for (const [index, raw] of rawSnapshotUrls.entries()) {
      if (typeof raw !== "string") throw new TerraScaleSyncV1ClientError("protocol_error", `snapshot URL ${index} is not a string`);
      const path = canonicalViewPath(view, generation, `snapshot/${index}`);
      snapshotUrls.push(canonicalAttachedUrl(raw, syncOrigin, path, "snapshot"));
    }
  } else if (rawSnapshotUrls !== undefined) {
    throw new TerraScaleSyncV1ClientError("protocol_error", "reactive attach response snapshot URLs are not an array");
  }
  const checkpointValue = value.checkpoint;
  const checkpoint = checkpointValue === undefined || checkpointValue === null ? undefined : bytesFromUnknown(checkpointValue, "checkpoint");
  const snapshot = decodeInlineSnapshot(value.snapshot, checkpoint);
  if (snapshot !== undefined && snapshot.view_generation !== generation) {
    throw new TerraScaleSyncV1ClientError("protocol_error", "inline snapshot generation does not match the attached view");
  }
  if (snapshot === undefined && snapshotUrls.length === 0 && checkpoint === undefined) {
    throw new TerraScaleSyncV1ClientError("protocol_error", "reactive attach response has no snapshot or snapshot URLs");
  }
  // A checkpoint-only attach is a valid resume bootstrap. The opaque token is
  // installed at revision zero; subsequent deltas advance from that cut.
  if (snapshot === undefined && snapshotUrls.length === 0 && checkpoint !== undefined) {
    return {
      viewId: view,
      generation,
      checkpoint,
      capability: capabilityValue,
      snapshotUrls,
      logUrl,
      snapshot: {
        view_generation: generation,
        authorization_generation: 0n,
        revision: 0n,
        items: [],
        digest: syncV1ResultDigest([]),
        checkpoint,
      },
      websocketUrl: attachedWebsocketUrl,
    };
  }
  return { viewId: view, generation, checkpoint, capability: capabilityValue, snapshotUrls, logUrl, snapshot, websocketUrl: attachedWebsocketUrl };
}

function canonicalWebSocketUrl(raw: unknown, apiOrigin: string, view: string, generation: bigint): string {
  if (typeof raw !== "string" || raw.length === 0 || raw.length > 4096) {
    throw new TerraScaleSyncV1ClientError("protocol_error", "WebSocket URL is outside its bound");
  }
  let parsed: URL;
  try {
    parsed = new URL(raw, apiOrigin);
  } catch (error) {
    throw new TerraScaleSyncV1ClientError("protocol_error", "WebSocket URL is invalid", { cause: error });
  }
  const expectedOrigin = new URL(apiOrigin).origin.replace(/^http/iu, "ws");
  const expectedPath = canonicalViewPath(view, generation, "websocket");
  if (parsed.protocol !== "ws:" && parsed.protocol !== "wss:") {
    throw new TerraScaleSyncV1ClientError("protocol_error", "WebSocket URL must use ws or wss");
  }
  if (parsed.origin !== expectedOrigin || parsed.username !== "" || parsed.password !== "" || parsed.hash !== "" || parsed.pathname !== expectedPath) {
    throw new TerraScaleSyncV1ClientError("protocol_error", "WebSocket URL is not the canonical API URL");
  }
  if ([...parsed.searchParams.keys()].length !== 0) {
    throw new TerraScaleSyncV1ClientError("protocol_error", "WebSocket URL must not carry query parameters");
  }
  return parsed.toString();
}

function websocketUrl(apiOrigin: string, view: string, generation: bigint): string {
  const parsed = new URL(apiOrigin);
  parsed.protocol = parsed.protocol === "https:" ? "wss:" : "ws:";
  parsed.pathname = canonicalViewPath(view, generation, "websocket");
  return parsed.toString();
}

function canonicalAttachedUrl(raw: unknown, syncOrigin: string, expectedPath: string, kind: "log" | "snapshot"): string {
  if (typeof raw !== "string" || raw.length === 0 || raw.length > 4096) {
    throw new TerraScaleSyncV1ClientError("protocol_error", `${kind} URL is outside its bound`);
  }
  let parsed: URL;
  try {
    parsed = new URL(raw, syncOrigin);
  } catch (error) {
    throw new TerraScaleSyncV1ClientError("protocol_error", `${kind} URL is invalid`, { cause: error });
  }
  const origin = new URL(syncOrigin).origin;
  if (parsed.origin !== origin || parsed.username !== "" || parsed.password !== "" || parsed.hash !== "" || parsed.pathname !== expectedPath) {
    throw new TerraScaleSyncV1ClientError("protocol_error", `${kind} URL is not the canonical sync URL`);
  }
  const allowed = kind === "snapshot" ? "cut" : "offset";
  const keys = [...new Set([...parsed.searchParams.keys()])];
  if (keys.some(key => key !== allowed) || parsed.searchParams.getAll(allowed).length > 1) {
    throw new TerraScaleSyncV1ClientError("protocol_error", `${kind} URL has unsupported query parameters`);
  }
  const value = parsed.searchParams.get(allowed);
  if (kind === "snapshot" && (value === null || value.length === 0)) {
    throw new TerraScaleSyncV1ClientError("protocol_error", "snapshot URL is missing its immutable cut");
  }
  if (kind === "snapshot" && value !== null) {
    try {
      if (!/^\d+$/u.test(value) || BigInt(value) > MAX_U64) throw new Error("invalid cut");
    } catch (error) {
      throw new TerraScaleSyncV1ClientError("protocol_error", "snapshot URL has an invalid immutable cut", { cause: error });
    }
  }
  if (kind === "log" && value !== null) {
    try {
      if (!/^\d+$/u.test(value) || BigInt(value) > MAX_U64) throw new Error("invalid offset");
    } catch (error) {
      throw new TerraScaleSyncV1ClientError("protocol_error", "log URL has an invalid offset", { cause: error });
    }
  }
  const canonical = new URL(expectedPath, syncOrigin);
  // The attach-time log offset is only a hint; polling always uses the
  // installed revision. Snapshot cuts, however, are immutable identity and
  // must remain part of the public URL.
  if (kind === "snapshot" && value !== null) canonical.searchParams.set(allowed, value);
  return canonical.toString();
}

function decodeInlineSnapshot(value: unknown, checkpoint: Uint8Array | undefined): TerrascaleSyncV1MsgpackSnapshot | undefined {
  if (!isRecord(value)) return undefined;
  const itemsValue = value.items;
  if (!Array.isArray(itemsValue)) throw new TerraScaleSyncV1ClientError("protocol_error", "inline snapshot items are not an array");
  if (itemsValue.length > 10_000) throw new TerraScaleSyncV1ClientError("protocol_error", "inline snapshot has too many items");
  const items = itemsValue.map((raw, index) => {
    if (!isRecord(raw) || typeof raw.identity !== "string" || !Array.isArray(raw.fields)) {
      throw new TerraScaleSyncV1ClientError("protocol_error", `inline snapshot item ${index} is malformed`);
    }
    if (raw.fields.length > 1_024) throw new TerraScaleSyncV1ClientError("protocol_error", `inline snapshot item ${index} has too many fields`);
    return {
      identity: raw.identity,
      fields: raw.fields.map((field, fieldIndex) => {
        if (!isRecord(field) || typeof field.path !== "string") throw new TerraScaleSyncV1ClientError("protocol_error", `inline snapshot field ${fieldIndex} is malformed`);
        return { path: field.path, value: bytesFromUnknown(field.value, `snapshot.items[${index}].fields[${fieldIndex}].value`) };
      }),
    };
  });
  if (typeof value.digest !== "string") throw new TerraScaleSyncV1ClientError("protocol_error", "inline snapshot digest is missing");
  const inlineCheckpoint = bytesFromUnknown(value.checkpoint, "snapshot.checkpoint");
  if (checkpoint !== undefined && !bytesEqual(checkpoint, inlineCheckpoint)) {
    throw new TerraScaleSyncV1ClientError("protocol_error", "attach checkpoint does not match inline snapshot checkpoint");
  }
  return {
    view_generation: bigintFromUnknown(value.view_generation ?? value.viewGeneration, "snapshot.view_generation"),
    authorization_generation: bigintFromUnknown(value.authorization_generation ?? value.authorizationGeneration ?? 0, "snapshot.authorization_generation"),
    revision: bigintFromUnknown(value.revision, "snapshot.revision"),
    items,
    digest: value.digest,
    checkpoint: checkpoint ?? inlineCheckpoint,
  };
}

function bytesEqual(left: Uint8Array, right: Uint8Array): boolean {
  return left.length === right.length && left.every((byte, index) => byte === right[index]);
}

function normalizeResetReason(value: string): SyncV1ResultResetReason {
  const normalized = value.replace(/_/gu, "-") as SyncV1ResultResetReason;
  const reasons: readonly SyncV1ResultResetReason[] = [
    "checkpoint-expired", "retention-advanced", "generation-changed", "checkpoint-not-current",
    "view-diverged", "oversized", "invalid-operations", "invalid-checkpoint", "backpressure",
  ];
  return reasons.includes(normalized) ? normalized : "checkpoint-expired";
}

function isAbortError(value: unknown): boolean {
  return isRecord(value) && value.name === "AbortError";
}

function linkAbortSignals(...signals: (AbortSignal | undefined)[]): {
  readonly signal: AbortSignal;
  readonly dispose: () => void;
} {
  const controller = new AbortController();
  const listeners: Array<() => void> = [];
  const abort = (signal: AbortSignal): void => {
    if (!controller.signal.aborted) controller.abort(signal.reason);
  };
  for (const signal of signals) {
    if (signal === undefined) continue;
    const listener = () => abort(signal);
    if (signal.aborted) abort(signal);
    else {
      signal.addEventListener("abort", listener, { once: true });
      listeners.push(() => signal.removeEventListener("abort", listener));
    }
  }
  return {
    signal: controller.signal,
    dispose: () => {
      for (const remove of listeners) remove();
    },
  };
}
